namespace NekoNoTe.Models;

using System.Text.Json.Serialization;

public enum AlertKind
{
    WarningAdvisory,
    Earthquake,
    EewWarning,
    Tsunami,
    /// <summary>「予定」タブで設定した時刻の通知。履歴には残さない。</summary>
    Schedule,
}

/// <summary>地域ごとの震度観測点(地震情報・緊急地震速報の詳細タブ用)。</summary>
public sealed record EarthquakeIntensityPoint(string Prefecture, string Address, int Scale, string ScaleLabel);

/// <summary>地震情報・緊急地震速報の追加詳細。地震以外の警報種別ではnull。</summary>
public sealed class EarthquakeDetail
{
    public string? HypocenterName { get; init; }
    public double? Depth { get; init; }
    public double? Magnitude { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? DomesticTsunami { get; init; }
    public IReadOnlyList<EarthquakeIntensityPoint> Points { get; init; } = Array.Empty<EarthquakeIntensityPoint>();

    /// <summary>震源で地震が発生した時刻。地点別の主要動到達時刻の概算に使う。</summary>
    public DateTimeOffset? OriginTime { get; init; }

    /// <summary>緊急地震速報で推定される、監視対象地域への主要動到達予測時刻。取得できない場合はnull。</summary>
    public DateTimeOffset? ArrivalTime { get; init; }
}

public sealed class AlertInfo
{
    public required string Id { get; init; }
    public required AlertKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset ReportedAt { get; init; }
    [JsonIgnore]
    public DateTime LocalReportDate => ReportedAt.LocalDateTime.Date;
    [JsonIgnore]
    public string EarthquakeLocationLabel => $"震源地: {Detail?.HypocenterName ?? "不明"}";
    [JsonIgnore]
    public string EarthquakeSummaryLabel =>
        Kind == AlertKind.Earthquake && Title.StartsWith("地震情報:", StringComparison.Ordinal)
            ? Title["地震情報:".Length..].Trim()
            : Title;
    /// <summary>
    /// 一覧のカード用の見出し。上にラベル(«緊急地震速報» など)を出すので、同じ言葉を頭から外す。
    /// 例: «緊急地震速報(警報) 最大震度5弱» → «警報 · 最大震度5弱»
    /// </summary>
    [JsonIgnore]
    public string ListSummaryLabel
    {
        get
        {
            var t = EarthquakeSummaryLabel;
            if (Kind == AlertKind.EewWarning && t.StartsWith("緊急地震速報", StringComparison.Ordinal))
            {
                t = t["緊急地震速報".Length..].Trim();
                foreach (var (from, to) in new[] { ("(警報)", "警報 · "), ("（警報）", "警報 · "), ("(予報)", "予報 · "), ("（予報）", "予報 · ") })
                    if (t.StartsWith(from, StringComparison.Ordinal)) { t = to + t[from.Length..].Trim(); break; }
            }
            return t.Length > 0 ? t : EarthquakeSummaryLabel;
        }
    }
    [JsonIgnore]
    public string AlertKindLabel => Kind switch
    {
        AlertKind.EewWarning => "緊急地震速報",
        AlertKind.Tsunami => "津波",
        AlertKind.Schedule => "予定",
        _ => "地震",
    };
    public EarthquakeDetail? Detail { get; init; }

    /// <summary>震度5弱以上・津波警報級など、重要度に応じた通知(音の連続再生など)の対象かどうか。</summary>
    public bool IsCritical { get; init; }

    /// <summary>設定した最小震度未満などの理由で、通知(トースト・音・リアルタイムタブへの自動切替)を出さないかどうか。
    /// falseでも履歴・地震タブへの記録は行う。</summary>
    public bool ShouldNotify { get; init; } = true;

    /// <summary>気象警報・注意報の取得対象地域コード(設定の対象地域と一致するかの判定に使う)。地震・津波ではnull。</summary>
    public string? AreaCode { get; init; }

    /// <summary>取消報を受け取って取り消された緊急地震速報かどうか。履歴に残すが、直近の地震などの集計からは外す。</summary>
    public bool IsCancelled { get; init; }

    /// <summary>この通知が取り消す対象のAlertInfo.Id。取消報(履歴には追加せず、対象の更新と通知だけに使う)でのみ設定される。</summary>
    [JsonIgnore]
    public string? CancelsAlertId { get; init; }

    /// <summary>主要動の到達予測時刻だけを差し替えた複製を返す(Wolfx の報に P2P地震情報の到達時刻を足す時に使う)。</summary>
    public AlertInfo WithArrivalTime(DateTimeOffset arrivalTime) => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Body = Body,
        ReportedAt = ReportedAt,
        Detail = new EarthquakeDetail
        {
            HypocenterName = Detail?.HypocenterName,
            Depth = Detail?.Depth,
            Magnitude = Detail?.Magnitude,
            Latitude = Detail?.Latitude,
            Longitude = Detail?.Longitude,
            DomesticTsunami = Detail?.DomesticTsunami,
            Points = Detail?.Points ?? Array.Empty<EarthquakeIntensityPoint>(),
            OriginTime = Detail?.OriginTime,
            ArrivalTime = arrivalTime,
        },
        IsCritical = IsCritical,
        ShouldNotify = ShouldNotify,
        AreaCode = AreaCode,
        IsCancelled = IsCancelled,
    };

    /// <summary>取消済みとして、タイトルに「【取消】」を付けた複製を返す。</summary>
    public AlertInfo AsCancelled() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = $"【取消】{Title}",
        Body = Body,
        ReportedAt = ReportedAt,
        Detail = Detail,
        IsCritical = false,
        ShouldNotify = ShouldNotify,
        AreaCode = AreaCode,
        IsCancelled = true,
    };
}
