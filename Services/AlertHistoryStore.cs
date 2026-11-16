using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using NekoNoTe.Models;

namespace NekoNoTe.Services;

/// <summary>
/// 通知履歴(直近N件)を保持し、メインウィンドウの一覧表示用に永続化する。
/// </summary>
public sealed class AlertHistoryStore
{
    private const int MaxEntries = 100;

    private readonly string _path;

    public ObservableCollection<AlertInfo> Items { get; } = new();

    public AlertHistoryStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "history.json");

        foreach (var item in LoadFromDisk())
        {
            Items.Add(item);
        }

        // 前に入った «同じ地震の予報と地震情報» の重なりも片付ける
        foreach (var quake in Items.Where(a => a.Kind == AlertKind.Earthquake).ToList())
        {
            RemoveForecastsFor(quake);
        }
    }

    public void Add(AlertInfo alert)
    {
        // 気象庁は同じ地震について震度確定前後で複数回レポートを更新することがある
        // (例: 最大震度不明 → 最大震度3)。発生時刻が同じ地震情報は別件として積み増さず、
        // 最新の内容に置き換える。
        if (alert.Kind == AlertKind.Earthquake)
        {
            var existingIndex = Items.ToList().FindIndex(a => a.Kind == AlertKind.Earthquake && a.ReportedAt == alert.ReportedAt);
            if (existingIndex >= 0)
            {
                Items.RemoveAt(existingIndex);
            }
        }

        Items.Insert(0, alert);
        while (Items.Count > MaxEntries)
        {
            Items.RemoveAt(Items.Count - 1);
        }
        SaveToDisk();
    }

    /// <summary>履歴にある指定Idの警報を取消済みに置き換える。見つからない・既に取消済みなら null。</summary>
    public AlertInfo? MarkCancelled(string alertId)
    {
        var index = Items.ToList().FindIndex(a => a.Id == alertId);
        if (index < 0 || Items[index].IsCancelled)
        {
            return null;
        }

        var cancelled = Items[index].AsCancelled();
        Items[index] = cancelled;
        SaveToDisk();
        return cancelled;
    }

    /// <summary>指定Idの項目を置き換える。update が null を返したら何もしない。置き換えた時は (前, 後) を返す</summary>
    public (AlertInfo Old, AlertInfo New)? Update(string alertId, Func<AlertInfo, AlertInfo?> update)
    {
        var index = Items.ToList().FindIndex(a => a.Id == alertId);
        if (index < 0) return null;
        var old = Items[index];
        if (update(old) is not { } updated) return null;
        Items[index] = updated;
        SaveToDisk();
        return (old, updated);
    }

    /// <summary>
    /// 地震情報が届いた時に、同じ地震の緊急地震速報(予報)を消す(同じ地震が 2 つ並ばないように)。
    /// 地震情報の時刻は分までしかないので、予報の発生時刻が «その分の 30 秒前〜1 分半後» で、
    /// 震源の名前が同じか、震央が 1 度以内なら同じ地震とみなす。警報は大事な記録なので残す。
    /// </summary>
    public void RemoveForecastsFor(AlertInfo quake)
    {
        if (quake.Kind != AlertKind.Earthquake) return;
        var removed = false;
        foreach (var f in Items.Where(a => a.Id.StartsWith("eew-forecast:", StringComparison.Ordinal)).ToList())
        {
            if (f.Detail?.OriginTime is not { } origin) continue;
            var diff = (origin - quake.ReportedAt).TotalSeconds;
            if (diff is < -30 or > 90) continue;
            var sameName = !string.IsNullOrEmpty(f.Detail.HypocenterName) && f.Detail.HypocenterName == quake.Detail?.HypocenterName;
            var near = f.Detail.Latitude is { } la1 && f.Detail.Longitude is { } lo1 && quake.Detail?.Latitude is { } la2 && quake.Detail?.Longitude is { } lo2
                       && Math.Abs(la1 - la2) <= 1 && Math.Abs(lo1 - lo2) <= 1;
            if (!sameName && !near) continue;
            Items.Remove(f);
            removed = true;
        }
        if (removed) SaveToDisk();
    }

    /// <summary>指定Idの項目を消す</summary>
    public void Remove(string alertId)
    {
        var index = Items.ToList().FindIndex(a => a.Id == alertId);
        if (index < 0) return;
        Items.RemoveAt(index);
        SaveToDisk();
    }

    private List<AlertInfo> LoadFromDisk()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var items = JsonSerializer.Deserialize<List<AlertInfo>>(json);
                if (items is not null)
                {
                    return items;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 破損時は空の履歴から再開する
        }

        return new List<AlertInfo>();
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(Items.ToList());
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失敗は致命的ではないため無視する
        }
    }
}
