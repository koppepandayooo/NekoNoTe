using System.IO;
using System.Text.Json;

namespace NekoNoTe.Services;

/// <summary>
/// 通知済みアラートIDを永続化し、再起動をまたいだ重複通知を防ぐ。
/// 直近N件のみ保持する(無限増殖を避けるため)。
/// </summary>
public sealed class AlertStateStore
{
    private const int MaxTrackedIds = 500;

    private readonly string _path;
    private readonly HashSet<string> _seenIds;

    public AlertStateStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "seen-alerts.json");
        _seenIds = LoadFromDisk();
    }

    public bool IsNew(string alertId) => !_seenIds.Contains(alertId);

    public void MarkSeen(string alertId)
    {
        if (_seenIds.Add(alertId) && _seenIds.Count > MaxTrackedIds)
        {
            // 単純化のため、上限を超えたら古い記録をまとめて捨てる
            _seenIds.Clear();
            _seenIds.Add(alertId);
        }
        SaveToDisk();
    }

    private HashSet<string> LoadFromDisk()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var ids = JsonSerializer.Deserialize<string[]>(json);
                if (ids is not null)
                {
                    return new HashSet<string>(ids);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 破損時は空の状態から再開する
        }

        return new HashSet<string>();
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_seenIds);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失敗は致命的ではないため無視する(次回起動時に再通知される程度)
        }
    }
}
