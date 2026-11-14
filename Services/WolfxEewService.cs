using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NekoNoTe.Models;
using NekoNoTe.Settings;

namespace NekoNoTe.Services;

/// <summary>
/// Wolfx Project (https://wolfx.jp/) の緊急地震速報 WebSocket に接続する。
/// P2P地震情報(556)は警報だけだが、こちらは予報も届く。主要動の到達時刻は入っていないので、
/// 警報の到達時刻は P2P地震情報から補う(App 側で同じ地震をまとめる)。
/// 規約: 1 台につき接続は 1 本、データ提供元として「Wolfx Project」を明記する。
/// </summary>
public sealed class WolfxEewService
{
    private const string WebSocketUrl = "wss://ws-api.wolfx.jp/jma_eew";
    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(2);
    /// <summary>サーバーから 1 分ごとにハートビートが来るので、これだけ何も来なければ切れたとみなす</summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMinutes(3);
    /// <summary>発表からこれ以上たった報は通知しない(接続直後に古い報が届いた時など)</summary>
    private static readonly TimeSpan StaleLimit = TimeSpan.FromMinutes(3);

    private static readonly IReadOnlyDictionary<string, int> IntensityScale = new Dictionary<string, int>
    {
        ["1"] = 10, ["2"] = 20, ["3"] = 30, ["4"] = 40,
        ["5弱"] = 45, ["5-"] = 45, ["5強"] = 50, ["5+"] = 50,
        ["6弱"] = 55, ["6-"] = 55, ["6強"] = 60, ["6+"] = 60, ["7"] = 70,
    };

    private static readonly IReadOnlyDictionary<string, int> MinScaleSetting = new Dictionary<string, int>
    {
        ["1"] = 10, ["2"] = 20, ["3"] = 30, ["4"] = 40,
        ["5-"] = 45, ["5+"] = 50, ["6-"] = 55, ["6+"] = 60, ["7"] = 70,
    };

    private readonly AppSettings _config;
    private readonly HashSet<string> _notifiedForecast = new();
    private readonly HashSet<string> _notifiedWarning = new();
    private readonly HashSet<string> _notifiedCancel = new();
    private CancellationTokenSource? _cts;

    public event Action<AlertInfo>? AlertReceived;
    public event Action<bool>? ConnectionStateChanged;

    public WolfxEewService(AppSettings config)
    {
        _config = config;
    }

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "debug.log");

    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            File.AppendAllText(DebugLogPath, $"{DateTimeOffset.Now:HH:mm:ss.fff} [wolfx] {message}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = MinReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            var receivedAny = false;
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(WebSocketUrl), ct);
                ConnectionStateChanged?.Invoke(true);
                receivedAny = await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DebugLog($"connection loop exception: {ex.GetType().FullName}: {ex.Message}");
            }
            finally
            {
                ConnectionStateChanged?.Invoke(false);
            }

            // 接続してすぐ切れるのを繰り返さないように、何も受け取れなかった時は間隔を延ばす
            delay = receivedAny ? MinReconnectDelay : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>受信を続ける。1 件でも受け取れたら true</summary>
    private async Task<bool> ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var receivedAny = false;
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReceiveTimeout);
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await ws.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return receivedAny;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                DebugLog("no message for a while, reconnecting");
                return receivedAny;
            }

            receivedAny = true;
            var text = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
            try
            {
                if (ProcessMessage(text, out var alert) is "heartbeat")
                {
                    // ハートビートには ping で応える(ドキュメントの推奨)
                    await ws.SendAsync(Encoding.ASCII.GetBytes("ping"), WebSocketMessageType.Text, true, ct);
                }
                if (alert is not null)
                {
                    DebugLog($"received -> {alert.Title}");
                    AlertReceived?.Invoke(alert);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WebSocketException)
            {
                DebugLog($"ProcessMessage exception: {ex.GetType().FullName}: {ex.Message}");
            }
        }
        return receivedAny;
    }

    /// <summary>メッセージを読む。戻り値は type(heartbeat / jma_eew / pong など)</summary>
    private string? ProcessMessage(string json, out AlertInfo? alert)
    {
        alert = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        if (type == "jma_eew")
        {
            alert = ParseEew(root, DateTimeOffset.Now);
        }
        return type;
    }

    /// <summary>緊急地震速報 1 報を通知用に変換する。通知しない報は null</summary>
    internal AlertInfo? ParseEew(JsonElement root, DateTimeOffset now)
    {
        if (GetBool(root, "isTraining")) return null;

        var eventId = root.TryGetProperty("EventID", out var idEl) ? idEl.ToString() : null;
        if (string.IsNullOrEmpty(eventId)) return null;

        var announced = TryParseJstTime(GetString(root, "AnnouncedTime")) ?? now;
        if (now - announced > StaleLimit) return null;

        if (GetBool(root, "isCancel"))
        {
            if (!_notifiedCancel.Add(eventId)) return null;
            // 予報だけ出ていた場合も、App 側で予報の方を取り消す
            return new AlertInfo
            {
                Id = $"eew-cancel:{eventId}",
                Kind = AlertKind.EewWarning,
                Title = "緊急地震速報は取り消されました",
                Body = "先ほどの緊急地震速報は取り消されました。落ち着いて、最新の情報を確認してください。",
                ReportedAt = announced,
                CancelsAlertId = $"eew:{eventId}",
            };
        }

        var isWarn = GetBool(root, "isWarn");
        var maxIntensityText = GetString(root, "MaxIntensity") ?? "";
        var maxScale = IntensityScale.GetValueOrDefault(maxIntensityText, -1);

        if (isWarn)
        {
            if (!_notifiedWarning.Add(eventId)) return null;
        }
        else
        {
            // 予報は設定した最小震度に届いた時だけ、1 つの地震につき 1 回
            var minScale = _config.MinEarthquakeIntensity == "none" ? int.MaxValue : MinScaleSetting.GetValueOrDefault(_config.MinEarthquakeIntensity, 30);
            if (_notifiedWarning.Contains(eventId) || maxScale < minScale || !_notifiedForecast.Add(eventId)) return null;
        }

        var hypocenter = GetString(root, "Hypocenter") is { Length: > 0 } h ? h : "不明";
        var magnitude = GetPositiveDouble(root, "Magnitude") ?? GetPositiveDouble(root, "Magunitude");
        var depth = GetPositiveDouble(root, "Depth");
        var latitude = GetCoordinate(root, "Latitude");
        var longitude = GetCoordinate(root, "Longitude");
        var originTime = TryParseJstTime(GetString(root, "OriginTime"));

        var points = new List<EarthquakeIntensityPoint>();
        var areaNames = new List<string>();
        if (root.TryGetProperty("WarnArea", out var areasEl) && areasEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var area in areasEl.EnumerateArray())
            {
                var name = GetString(area, "Chiiki") ?? "";
                var shindo = GetString(area, "Shindo1") ?? "";
                var scale = IntensityScale.GetValueOrDefault(shindo, -1);
                if (name.Length > 0)
                {
                    areaNames.Add(name);
                    if (scale >= 0) points.Add(new EarthquakeIntensityPoint("", name, scale, FormatScale(scale)));
                }
            }
        }

        var kindText = isWarn ? "警報" : "予報";
        var scaleText = maxScale >= 0 ? $"最大震度{FormatScale(maxScale)}" : "震度推定中";
        var magText = magnitude is { } m ? m.ToString("0.0") : "-";
        var body = $"震源地: {hypocenter} / M{magText}";
        if (areaNames.Count > 0) body += $" / 対象: {string.Join("、", areaNames.Take(5))}";
        if (GetBool(root, "isAssumption")) body += " (仮定震源)";

        return new AlertInfo
        {
            Id = isWarn ? $"eew:{eventId}" : $"eew-forecast:{eventId}",
            Kind = AlertKind.EewWarning,
            Title = $"緊急地震速報({kindText}) {scaleText}",
            Body = body,
            ReportedAt = announced,
            IsCritical = maxScale >= 45,
            Detail = new EarthquakeDetail
            {
                HypocenterName = hypocenter,
                Depth = depth,
                Magnitude = magnitude,
                Latitude = latitude,
                Longitude = longitude,
                OriginTime = originTime,
                Points = points.OrderByDescending(p => p.Scale).ToList(),
            },
        };
    }

    private static string FormatScale(int scale) => scale switch
    {
        10 => "1", 20 => "2", 30 => "3", 40 => "4",
        45 => "5弱", 50 => "5強", 55 => "6弱", 60 => "6強", 70 => "7",
        _ => "不明",
    };

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static double? GetPositiveDouble(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            var v = el.GetDouble();
            if (v >= 0) return v;
        }
        return null;
    }

    private static double? GetCoordinate(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            var v = el.GetDouble();
            if (v is >= -180 and <= 180) return v;
        }
        return null;
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
