using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NekoNoTe.Roblox;

/// <summary>Roblox のアカウント1つ(ログイン中の Cookie を持つ)</summary>
public sealed class RobloxAccount
{
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>自分用のメモ(サブ垢・メイン など)</summary>
    public string Alias { get; set; } = "";
    /// <summary>.ROBLOSECURITY の値。暗号化したファイルにだけ保存し、画面には出さない</summary>
    public string Cookie { get; set; } = "";
    /// <summary>Roblox がブラウザを見分ける番号。アカウントごとに固定にしておく</summary>
    public string BrowserTrackerId { get; set; } = "";
    public DateTime LastUsed { get; set; }

    [JsonIgnore] public string? HeadshotUrl { get; set; }
}

/// <summary>
/// アカウントの保存(元は Roblox Account Manager)。
/// %APPDATA%\NekoNoTe\roblox-accounts.dat に、Windows のユーザーごとの暗号(DPAPI)で保存する。
/// 別のユーザー・別の PC では読めない。
/// </summary>
public static class RobloxAccounts
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "roblox-accounts.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NekoNoTe.Roblox.Accounts");

    private static List<RobloxAccount>? _items;
    public static event Action? Changed;

    public static IReadOnlyList<RobloxAccount> Items => _items ??= Load();

    private static List<RobloxAccount> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<RobloxAccount>>(plain) ?? new();
        }
        catch { return new(); }
    }

    public static void Save()
    {
        _items ??= Load();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_items), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, data);
        }
        catch { }
        Changed?.Invoke();
    }

    /// <summary>足す(同じ人がいれば Cookie を新しくする)</summary>
    public static void AddOrUpdate(RobloxAccount account)
    {
        _items ??= Load();
        var existing = _items.FirstOrDefault(a => a.UserId == account.UserId);
        if (existing != null)
        {
            existing.Cookie = account.Cookie;
            existing.Name = account.Name;
            existing.DisplayName = account.DisplayName;
            if (string.IsNullOrEmpty(existing.Alias)) existing.Alias = account.Alias;
        }
        else
        {
            if (string.IsNullOrEmpty(account.BrowserTrackerId)) account.BrowserTrackerId = NewTrackerId();
            _items.Add(account);
        }
        Save();
    }

    public static void Remove(RobloxAccount account)
    {
        _items ??= Load();
        if (_items.Remove(account)) Save();
    }

    public static string NewTrackerId() => $"{Random.Shared.Next(100000, 175000)}{Random.Shared.Next(100000, 900000)}";

    // Roblox Account Manager が保存に使っている固定の値(パスワードを設定していない時)
    private static readonly byte[] RamEntropy =
    {
        0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c,
        0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59,
        0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66,
    };

    /// <summary>
    /// Roblox Account Manager のデータから、名前・メモ・Cookie を読む。
    /// パスワードで暗号化している場合は読めない(その時は Account Manager でパスワードを外してから)。
    /// </summary>
    public static List<RobloxAccount> ReadAccountManager(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string json;
        try
        {
            json = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, RamEntropy, DataProtectionScope.LocalMachine));
        }
        catch
        {
            // 暗号化していない古い形式
            json = Encoding.UTF8.GetString(bytes);
            if (!json.TrimStart().StartsWith("[")) throw new InvalidOperationException("読めませんでした。Roblox Account Manager でパスワードを設定している場合は、外してからもう一度試してください。");
        }
        var list = new List<RobloxAccount>();
        foreach (var n in JsonNode.Parse(json)?.AsArray() ?? new JsonArray())
        {
            if (n == null) continue;
            string S(string k) => n[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            long id = 0;
            try { id = n["UserID"]?.GetValue<long>() ?? 0; } catch { }
            var cookie = S("SecurityToken");
            if (cookie == "" || id == 0) continue;
            list.Add(new RobloxAccount
            {
                UserId = id,
                Name = S("Username"),
                DisplayName = S("Username"),
                Alias = S("Alias"),
                Cookie = cookie,
                BrowserTrackerId = S("BrowserTrackerID"),
            });
        }
        return list;
    }
}
