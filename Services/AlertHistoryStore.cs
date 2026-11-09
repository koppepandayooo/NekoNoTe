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
