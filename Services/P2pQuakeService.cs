using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using NekoNoTe.Models;
using NekoNoTe.Settings;

namespace NekoNoTe.Services;

/// <summary>
/// P2P地震情報 (https://www.p2pquake.net/) のWebSocket APIに常時接続し、
/// 地震情報(code 551)と緊急地震速報・警報(code 556)を速報として受信する。
/// 気象庁のポーリングAPIと違い常時接続のプッシュ配信のため、体感速度が大きく異なる。
/// </summary>
public sealed class P2pQuakeService
{
    private const string WebSocketUrl = "wss://api.p2pquake.net/v2/ws";
    private const string HistoryUrlTemplate = "https://api.p2pquake.net/v2/history?codes={0}&limit=20";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HostCheckInterval = TimeSpan.FromSeconds(15);
    private volatile bool _yielded;
    private volatile bool _hosting;
    /// <summary>今、親として配っているか</summary>
    public bool IsHosting => _hosting;
    private static readonly HttpClient HistoryHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    // P2P地震情報の震度スケール(JMA震度 x10、5弱/強・6弱/強は45/50/55/60)。
    private static readonly IReadOnlyDictionary<string, int> ScaleThreshold = new Dictionary<string, int>
    {
        ["1"] = 10, ["2"] = 20, ["3"] = 30, ["4"] = 40,
        ["5-"] = 45, ["5+"] = 50, ["6-"] = 55, ["6+"] = 60, ["7"] = 70,
    };

    private static readonly IReadOnlyDictionary<int, string> ScaleName = new Dictionary<int, string>
    {
        [-1] = "不明", [10] = "1", [20] = "2", [30] = "3", [40] = "4",
        [45] = "5弱", [50] = "5強", [55] = "6弱", [60] = "6強", [70] = "7",
    };

    private readonly AppSettings _config;
    private readonly HashSet<string> _notifiedEewEventIds = new();
    private CancellationTokenSource? _cts;
    private bool _hasConnectedBefore;
    private DateTimeOffset? _lastDisconnectedAt;

    public event Action<AlertInfo>? AlertReceived;
    /// <summary>受信できているか、サイドバーに出す文字</summary>
    public event Action<bool, string>? StatusChanged;

    public P2pQuakeService(AppSettings config)
    {
        _config = config;
    }

    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "debug.log");

    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            File.AppendAllText(DebugLogPath, $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        QuakeLanRelay.Instance.SetEnabled(false);
    }

    /// <summary>受信方法の設定に従って、つなぎ続ける</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = ReconnectDelay;
            try
            {
                switch (_config.QuakeSource)
                {
                    case "Off":
                        Report(false, "オフ");
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "Direct":
                        await RunDirectOnceAsync(ct, host: false);
                        break;
                    default:
                        var parent = await LanQuakeClient.DiscoverAsync(ct);
                        if (parent != null)
                        {
                            await RunLanOnceAsync(parent.EndPoint, ct);
                            // 親がいなくなった時は全員が同時に親にならないよう、少しばらしてすぐ探し直す
                            delay = TimeSpan.FromMilliseconds(Random.Shared.Next(100, 2000));
                        }
                        else
                        {
                            // 親がいないので自分が親になる(ポートが使えない時は、自分の分だけ受信する)
                            await RunDirectOnceAsync(ct, host: QuakeLanRelay.Instance.SetEnabled(true));
                            if (_yielded)
                            {
                                _yielded = false;
                                QuakeLanRelay.Instance.SetEnabled(false);
                                delay = TimeSpan.Zero;
                            }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 理由を問わず必ず再接続を試みる(想定外の例外でループが静かに死なないように)
                DebugLog($"connection loop exception: {ex.GetType().FullName}: {ex.Message}");
            }

            Report(false, "再接続");
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Report(bool connected, string text) => StatusChanged?.Invoke(connected, text);

    private async Task CatchUpIfReconnectedAsync(CancellationToken ct)
    {
        // 切断していた間に発生した地震を、REST API で取りこぼしなく拾う(初回接続時は除く)
        if (_hasConnectedBefore && _lastDisconnectedAt is { } disconnectedAt)
            await CatchUpMissedAlertsAsync(disconnectedAt, ct);
        _hasConnectedBefore = true;
    }

    private async Task RunLanOnceAsync(System.Net.IPEndPoint host, CancellationToken ct)
    {
        try
        {
            await LanQuakeClient.ReceiveAsync(host, line =>
            {
                try { ProcessMessage(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line))); }
                catch (Exception ex) { DebugLog($"ProcessMessage exception: {ex.GetType().FullName}: {ex.Message}"); }
            }, upstream => Report(upstream, upstream ? "共有で受信中" : "再接続"), ct);
        }
        finally
        {
            _lastDisconnectedAt = DateTimeOffset.Now;
        }
    }

    private async Task RunDirectOnceAsync(CancellationToken ct, bool host)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _hosting = host;
        if (host) _ = WatchForBetterHostAsync(linked);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(WebSocketUrl), linked.Token);
            if (host) QuakeLanRelay.Instance.Upstream = true;
            Report(true, "受信中");
            await CatchUpIfReconnectedAsync(linked.Token);
            await ReceiveLoopAsync(ws, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // もっと優先される親が見つかったので、親をやめる
        }
        finally
        {
            QuakeLanRelay.Instance.Upstream = false;
            _lastDisconnectedAt = DateTimeOffset.Now;
            _hosting = false;
            linked.Cancel();
        }
    }

    /// <summary>親の間、もっと優先される親が現れたら譲る</summary>
    private async Task WatchForBetterHostAsync(CancellationTokenSource direct)
    {
        try
        {
            while (!direct.IsCancellationRequested)
            {
                await Task.Delay(HostCheckInterval, direct.Token);
                var other = await LanQuakeClient.DiscoverAsync(direct.Token);
                if (other != null && other.IsBetterThan(QuakeLanRelay.Priority, QuakeLanRelay.StartedTicks, QuakeLanRelay.InstanceId))
                {
                    _yielded = true;
                    direct.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>切断していた間の地震情報・緊急地震速報をREST APIで取得し、見逃しがあれば通知する。</summary>
    private async Task CatchUpMissedAlertsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var recovered = await FetchAlertsSinceAsync(since, ct);
        foreach (var alert in recovered)
        {
            DebugLog($"catch-up recovered: {alert.Title}");
            AlertReceived?.Invoke(alert);
        }
    }

    /// <summary>アプリが起動していなかった間の地震情報・緊急地震速報をREST APIで取得する(通知は呼び出し側の判断に委ねる)。</summary>
    public Task<IReadOnlyList<AlertInfo>> FetchMissedSinceStartupAsync(DateTimeOffset since, CancellationToken ct) =>
        FetchAlertsSinceAsync(since, ct).ContinueWith(
            t => (IReadOnlyList<AlertInfo>)t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private async Task<List<AlertInfo>> FetchAlertsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        // (alert, 気象庁の発表時刻) のペアで保持する。地震情報(551)は同じ地震について
        // 震度速報→震源に関する情報→震源・震度に関する情報 と発表時刻の異なる複数レポートが
        // 同じ発生時刻(ReportedAt)で届くため、ReportedAtでソートすると発表順が失われ、
        // 最後に適用したレポートが古い(震源情報のない)ものになってしまうことがある。
        // 必ず発表時刻順に適用することで、最新かつ最も詳細なレポートが最後に残るようにする。
        var recovered = new List<(AlertInfo Alert, DateTimeOffset IssuedAt)>();
        foreach (var code in new[] { 551, 552, 556 })
        {
            try
            {
                var url = string.Format(HistoryUrlTemplate, code);
                using var stream = await HistoryHttp.GetStreamAsync(url, ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    AlertInfo? alert = code switch
                    {
                        551 => ParseEarthquakeInfo(item),
                        552 => ParseTsunamiForecast(item),
                        _ => ParseEewWarning(item),
                    };
                    if (alert is not null && alert.ReportedAt >= since)
                    {
                        var issuedAt = item.TryGetProperty("issue", out var issueEl) && issueEl.TryGetProperty("time", out var issueTimeEl)
                            ? ParseJstTime(issueTimeEl.GetString())
                            : alert.ReportedAt;
                        recovered.Add((alert, issuedAt));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                DebugLog($"fetch-since failed for code={code}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return recovered.OrderBy(r => r.IssuedAt).Select(r => r.Alert).ToList();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            QuakeLanRelay.Instance.Broadcast(System.Text.Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length));
            messageStream.Position = 0;
            try
            {
                ProcessMessage(messageStream);
            }
            catch (Exception ex)
            {
                // 想定外のメッセージ形式でも受信ループ自体は継続する(1件のパース失敗で
                // 接続全体を巻き込んで再接続してしまわないようにする)。
                DebugLog($"ProcessMessage exception: {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    private void ProcessMessage(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var codeEl))
        {
            return;
        }

        var code = codeEl.GetInt32();
        var alert = code switch
        {
            551 => ParseEarthquakeInfo(root),
            552 => ParseTsunamiForecast(root),
            556 => ParseEewWarning(root),
            _ => null,
        };

        if (alert is not null)
        {
            DebugLog($"received code={code} -> {alert.Title}");
            AlertReceived?.Invoke(alert);
        }
    }

    private AlertInfo? ParseEarthquakeInfo(JsonElement root)
    {
        // WebSocketのリアルタイム配信では "_id"、REST の /v2/history では "id" というフィールド名で
        // 同じ値が返ってくる(配信経路でフィールド名が異なる)。両対応にする。
        var id = GetIdField(root);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (!root.TryGetProperty("earthquake", out var eq))
        {
            return null;
        }

        var maxScale = eq.TryGetProperty("maxScale", out var msEl) ? msEl.GetInt32() : -1;
        var minScale = ScaleThreshold.GetValueOrDefault(_config.MinEarthquakeIntensity, 30);
        // 設定した最小震度未満でも、履歴・地震タブへの記録は行い、通知(音・トースト・自動切替)のみ抑制する。
        var shouldNotify = maxScale >= minScale;

        var hypocenterName = "不明";
        var magnitude = "-";
        double? depth = null, latitude = null, longitude = null, magnitudeValue = null;
        if (eq.TryGetProperty("hypocenter", out var hyp))
        {
            hypocenterName = hyp.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "不明" : "不明";
            magnitude = hyp.TryGetProperty("magnitude", out var magEl) ? FormatMagnitude(magEl) : "-";
            magnitudeValue = TryGetPositiveDouble(hyp, "magnitude");
            depth = TryGetPositiveDouble(hyp, "depth");
            latitude = TryGetCoordinate(hyp, "latitude");
            longitude = TryGetCoordinate(hyp, "longitude");
        }

        var domesticTsunami = eq.TryGetProperty("domesticTsunami", out var tsunamiEl) ? tsunamiEl.GetString() : null;

        var points = new List<EarthquakeIntensityPoint>();
        if (root.TryGetProperty("points", out var pointsEl))
        {
            foreach (var p in pointsEl.EnumerateArray())
            {
                var scale = p.TryGetProperty("scale", out var scaleEl) ? scaleEl.GetInt32() : -1;
                if (scale < 0)
                {
                    continue;
                }
                var pref = p.TryGetProperty("pref", out var prefEl) ? prefEl.GetString() ?? "" : "";
                var addr = p.TryGetProperty("addr", out var addrEl) ? addrEl.GetString() ?? "" : "";
                points.Add(new EarthquakeIntensityPoint(pref, addr, scale, ScaleName.GetValueOrDefault(scale, "不明")));
            }
        }

        var reportedAt = eq.TryGetProperty("time", out var timeEl)
            ? ParseJstTime(timeEl.GetString())
            : DateTimeOffset.Now;

        return new AlertInfo
        {
            Id = $"quake:{id}",
            Kind = AlertKind.Earthquake,
            Title = $"地震情報: 最大震度{ScaleName.GetValueOrDefault(maxScale, "不明")}",
            Body = $"震源地: {hypocenterName} / M{magnitude}",
            ReportedAt = reportedAt,
            IsCritical = maxScale >= CriticalScaleThreshold,
            ShouldNotify = shouldNotify,
            Detail = new EarthquakeDetail
            {
                HypocenterName = hypocenterName,
                Depth = depth,
                Magnitude = magnitudeValue,
                Latitude = latitude,
                Longitude = longitude,
                DomesticTsunami = domesticTsunami,
                Points = points.OrderByDescending(p => p.Scale).ToList(),
            },
        };
    }

    // 震度5弱以上・津波警報級を「重要」として、通知音の連続再生などで強調する。
    private const int CriticalScaleThreshold = 45;

    private static double? TryGetPositiveDouble(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            var v = el.GetDouble();
            if (v >= 0)
            {
                return v;
            }
        }
        return null;
    }

    private static double? TryGetCoordinate(JsonElement obj, string propertyName)
    {
        // 緯度・経度は不明な場合に -200 のような明らかな範囲外の値が入る慣例がある。
        if (obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            var v = el.GetDouble();
            if (v is >= -180 and <= 180)
            {
                return v;
            }
        }
        return null;
    }

    private static readonly IReadOnlyDictionary<string, int> TsunamiGradeRank = new Dictionary<string, int>
    {
        ["Unknown"] = 0, ["Watch"] = 1, ["Warning"] = 2, ["MajorWarning"] = 3,
    };

    private static readonly IReadOnlyDictionary<string, string> TsunamiGradeName = new Dictionary<string, string>
    {
        ["MajorWarning"] = "大津波警報", ["Warning"] = "津波警報", ["Watch"] = "津波注意報",
    };

    private AlertInfo? ParseTsunamiForecast(JsonElement root)
    {
        var cancelled = root.TryGetProperty("cancelled", out var cancelledEl) && cancelledEl.GetBoolean();
        if (cancelled)
        {
            return null;
        }

        if (!root.TryGetProperty("areas", out var areasEl) || areasEl.GetArrayLength() == 0)
        {
            return null;
        }

        var bestGrade = "Unknown";
        var bestRank = -1;
        var areaNames = new List<string>();
        foreach (var area in areasEl.EnumerateArray())
        {
            var grade = area.TryGetProperty("grade", out var gradeEl) ? gradeEl.GetString() ?? "Unknown" : "Unknown";
            var rank = TsunamiGradeRank.GetValueOrDefault(grade, 0);
            if (rank > bestRank)
            {
                bestRank = rank;
                bestGrade = grade;
            }
            if (area.TryGetProperty("name", out var nameEl))
            {
                areaNames.Add(nameEl.GetString() ?? "");
            }
        }

        var issueTime = root.TryGetProperty("issue", out var issue) && issue.TryGetProperty("time", out var timeEl)
            ? ParseJstTime(timeEl.GetString())
            : DateTimeOffset.Now;

        var id = GetIdField(root) ?? $"{issueTime:O}";
        var gradeName = TsunamiGradeName.GetValueOrDefault(bestGrade, "津波予報");
        var areaText = areaNames.Count > 0 ? string.Join("、", areaNames.Where(n => n.Length > 0).Take(5)) : "対象地域を確認中";

        return new AlertInfo
        {
            Id = $"tsunami:{id}",
            Kind = AlertKind.Tsunami,
            Title = gradeName,
            Body = $"対象: {areaText}",
            ReportedAt = issueTime,
            IsCritical = bestRank >= 2, // Warning(津波警報)・MajorWarning(大津波警報)
        };
    }

    /// <summary>緊急地震速報の取消報。元の警報(同じeventId)を取り消すための通知として返す。</summary>
    private static AlertInfo? ParseEewCancellation(JsonElement root)
    {
        if (!root.TryGetProperty("issue", out var issue) ||
            !issue.TryGetProperty("eventId", out var eventIdEl) ||
            string.IsNullOrEmpty(eventIdEl.GetString()))
        {
            return null;
        }

        var eventId = eventIdEl.GetString()!;
        var issuedAt = issue.TryGetProperty("time", out var timeEl)
            ? ParseJstTime(timeEl.GetString())
            : DateTimeOffset.Now;

        return new AlertInfo
        {
            Id = $"eew-cancel:{eventId}",
            Kind = AlertKind.EewWarning,
            Title = "緊急地震速報(警報)は取り消されました",
            Body = "先ほどの緊急地震速報は取り消されました。落ち着いて、最新の情報を確認してください。",
            ReportedAt = issuedAt,
            CancelsAlertId = $"eew:{eventId}",
        };
    }

    private AlertInfo? ParseEewWarning(JsonElement root)
    {
        var cancelled = root.TryGetProperty("cancelled", out var cancelledEl) && cancelledEl.GetBoolean();
        if (cancelled)
        {
            return ParseEewCancellation(root);
        }

        var isTest = false;
        if (root.TryGetProperty("test", out var testEl))
        {
            isTest = testEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => testEl.GetString() != "本番",
                _ => false,
            };
        }

        if (!root.TryGetProperty("issue", out var issue) || !issue.TryGetProperty("eventId", out var eventIdEl))
        {
            return null;
        }

        var eventId = eventIdEl.GetString();
        if (string.IsNullOrEmpty(eventId))
        {
            return null;
        }

        // 同じ地震(eventId)について、震度の続報が来るたびに通知すると洪水になるため
        // 最初の一報のみ通知する。テスト配信は通知しない。
        if (isTest || !_notifiedEewEventIds.Add(eventId))
        {
            return null;
        }

        var hypocenterName = "不明";
        var magnitude = "-";
        double? depth = null, latitude = null, longitude = null, magnitudeValue = null;
        if (root.TryGetProperty("earthquake", out var eq) && eq.TryGetProperty("hypocenter", out var hyp))
        {
            hypocenterName = hyp.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "不明" : "不明";
            magnitude = hyp.TryGetProperty("magnitude", out var magEl) ? FormatMagnitude(magEl) : "-";
            magnitudeValue = TryGetPositiveDouble(hyp, "magnitude");
            depth = TryGetPositiveDouble(hyp, "depth");
            latitude = TryGetCoordinate(hyp, "latitude");
            longitude = TryGetCoordinate(hyp, "longitude");
        }

        var maxScale = -1;
        var areaNames = new List<string>();
        var points = new List<EarthquakeIntensityPoint>();
        DateTimeOffset? earliestArrival = null;
        DateTimeOffset? matchedAreaArrival = null;
        if (root.TryGetProperty("areas", out var areasEl))
        {
            foreach (var area in areasEl.EnumerateArray())
            {
                var scaleTo = area.TryGetProperty("scaleTo", out var scaleToEl) ? scaleToEl.GetInt32() : -1;
                if (scaleTo > maxScale)
                {
                    maxScale = scaleTo;
                }
                var areaName = area.TryGetProperty("name", out var areaNameEl) ? areaNameEl.GetString() ?? "" : "";
                if (areaName.Length > 0)
                {
                    areaNames.Add(areaName);
                }
                var pref = area.TryGetProperty("pref", out var prefEl) ? prefEl.GetString() ?? "" : "";
                if (scaleTo >= 0)
                {
                    points.Add(new EarthquakeIntensityPoint(pref, areaName, scaleTo, ScaleName.GetValueOrDefault(scaleTo, "不明")));
                }

                if (area.TryGetProperty("arrivalTime", out var arrivalEl))
                {
                    var arrival = ParseJstTime(arrivalEl.GetString());
                    if (earliestArrival is null || arrival < earliestArrival)
                    {
                        earliestArrival = arrival;
                    }
                    // 設定中の監視地域(例:「東京都」)に対応するエリア(例:pref「東京」)の到達時刻を優先的に使う。
                    if (matchedAreaArrival is null && pref.Length > 0 && !string.IsNullOrEmpty(_config.AreaName) &&
                        _config.AreaName.StartsWith(pref, StringComparison.Ordinal))
                    {
                        matchedAreaArrival = arrival;
                    }
                }
            }
        }

        var arrivalTime = matchedAreaArrival ?? earliestArrival;
        if (arrivalTime is null && eq.TryGetProperty("arrivalTime", out var eqArrivalEl))
        {
            arrivalTime = ParseJstTime(eqArrivalEl.GetString());
        }

        var reportedAt = issue.TryGetProperty("time", out var timeEl)
            ? ParseJstTime(timeEl.GetString())
            : DateTimeOffset.Now;
        var originTime = eq.TryGetProperty("originTime", out var originTimeEl)
            ? TryParseJstTime(originTimeEl.GetString())
            : null;

        var scaleText = maxScale >= 0 ? $"最大震度{ScaleName.GetValueOrDefault(maxScale, "不明")}" : "震度推定中";
        var areaText = areaNames.Count > 0 ? string.Join("、", areaNames.Where(n => n.Length > 0).Take(5)) : "対象地域を確認中";

        return new AlertInfo
        {
            Id = $"eew:{eventId}",
            Kind = AlertKind.EewWarning,
            Title = $"緊急地震速報(警報) {scaleText}",
            Body = $"震源地: {hypocenterName} / M{magnitude} / 対象: {areaText}",
            ReportedAt = reportedAt,
            IsCritical = maxScale >= CriticalScaleThreshold,
            Detail = new EarthquakeDetail
            {
                HypocenterName = hypocenterName,
                Depth = depth,
                Magnitude = magnitudeValue,
                Latitude = latitude,
                Longitude = longitude,
                OriginTime = originTime,
                Points = points.OrderByDescending(p => p.Scale).ToList(),
                ArrivalTime = arrivalTime,
            },
        };
    }

    private static string? GetIdField(JsonElement root)
    {
        if (root.TryGetProperty("_id", out var underscoreId))
        {
            return underscoreId.GetString();
        }
        if (root.TryGetProperty("id", out var plainId))
        {
            return plainId.GetString();
        }
        return null;
    }

    private static string FormatMagnitude(JsonElement magEl)
    {
        var value = magEl.ValueKind == JsonValueKind.Number ? magEl.GetDouble() : -1;
        return value > 0 ? value.ToString("0.0") : "-";
    }

    private static DateTimeOffset ParseJstTime(string? text)
    {
        // P2P地震情報の時刻表記は "yyyy/MM/dd HH:mm:ss" (JST、タイムゾーン表記なし)
        if (text is not null &&
            DateTime.TryParseExact(text, "yyyy/MM/dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return new DateTimeOffset(dt, TimeSpan.FromHours(9));
        }

        return DateTimeOffset.Now;
    }

    private static DateTimeOffset? TryParseJstTime(string? text)
    {
        if (text is not null &&
            DateTime.TryParseExact(text, "yyyy/MM/dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return new DateTimeOffset(dt, TimeSpan.FromHours(9));
        }

        return null;
    }
}
