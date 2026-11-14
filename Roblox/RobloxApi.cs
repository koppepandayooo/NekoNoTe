using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace NekoNoTe.Roblox;

/// <summary>
/// Roblox のサーバーへの問い合わせと起動(元は Roblox Account Manager の Account.cs)。
/// Cookie はヘッダーにだけ付けて送り、記録には残さない。
/// </summary>
public static class RobloxApi
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static HttpRequestMessage Request(HttpMethod method, string url, string cookie)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cookie", ".ROBLOSECURITY=" + cookie);
        req.Headers.Referrer = new Uri("https://www.roblox.com/");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NekoNoTe");
        return req;
    }

    /// <summary>Cookie が有効か確かめて、その人の ID と名前を返す。無効なら null</summary>
    public static async Task<RobloxAccount?> ValidateAsync(string cookie)
    {
        cookie = cookie.Trim();
        // «_|WARNING:-DO-NOT-SHARE-THIS…|_» を含めた全体でも、その後ろだけでも受け付ける
        using var res = await Http.SendAsync(Request(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie));
        if (!res.IsSuccessStatusCode) return null;
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        var id = j?["id"]?.GetValue<long>() ?? 0;
        if (id == 0) return null;
        return new RobloxAccount
        {
            UserId = id,
            Name = j!["name"]?.GetValue<string>() ?? "",
            DisplayName = j["displayName"]?.GetValue<string>() ?? "",
            Cookie = cookie,
        };
    }

    /// <summary>アバターの顔の画像の URL(まとめて1回で聞く)</summary>
    public static async Task<Dictionary<long, string>> HeadshotsAsync(IEnumerable<long> ids)
    {
        var map = new Dictionary<long, string>();
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return map;
        try
        {
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={string.Join(",", list)}&size=150x150&format=Png&isCircular=false";
            var j = JsonNode.Parse(await Http.GetStringAsync(url));
            foreach (var d in j?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = d?["targetId"]?.GetValue<long>() ?? 0;
                var img = d?["imageUrl"]?.GetValue<string>();
                if (id != 0 && !string.IsNullOrEmpty(img)) map[id] = img;
            }
        }
        catch { }
        return map;
    }

    /// <summary>起動に使う一回きりの券(auth ticket)をもらう。CSRF トークンを先にもらってから頼む</summary>
    private static async Task<string> AuthTicketAsync(string cookie)
    {
        const string url = "https://auth.roblox.com/v1/authentication-ticket/";
        string csrf;
        using (var first = Request(HttpMethod.Post, url, cookie))
        {
            first.Headers.Referrer = new Uri("https://www.roblox.com/games/1818/");
            first.Content = new StringContent("");
            using var res = await Http.SendAsync(first);
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("ログインが切れています。アカウントを追加し直してください。");
            if (!res.Headers.TryGetValues("x-csrf-token", out var t)) throw new InvalidOperationException($"Roblox に断られました({(int)res.StatusCode})");
            csrf = t.First();
        }
        using var second = Request(HttpMethod.Post, url, cookie);
        second.Headers.Referrer = new Uri("https://www.roblox.com/games/1818/");
        second.Headers.Add("X-CSRF-TOKEN", csrf);
        second.Content = new StringContent("");
        using var res2 = await Http.SendAsync(second);
        if (res2.Headers.TryGetValues("rbx-authentication-ticket", out var ticket)) return ticket.First();
        throw new InvalidOperationException($"起動の準備ができませんでした({(int)res2.StatusCode})。ログインが切れているかもしれません。");
    }

    /// <summary>«https://www.roblox.com/games/123/…» や数字だけからプレイスの ID を取り出す</summary>
    public static long ParsePlaceId(string text)
    {
        var m = Regex.Match(text ?? "", @"games/(\d+)");
        if (m.Success) return long.Parse(m.Groups[1].Value);
        m = Regex.Match(text ?? "", @"^\s*(\d+)\s*$");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>そのアカウントで Roblox を起動する。placeId が 0 ならゲームを選ばずにアプリだけ開く</summary>
    public static async Task LaunchAsync(RobloxAccount account, long placeId, string jobId)
    {
        var ticket = await AuthTicketAsync(account.Cookie);
        if (string.IsNullOrEmpty(account.BrowserTrackerId)) account.BrowserTrackerId = RobloxAccounts.NewTrackerId();
        var time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var tracker = account.BrowserTrackerId;
        string uri;
        if (placeId > 0)
        {
            var job = string.IsNullOrWhiteSpace(jobId) ? "" : jobId.Trim();
            var launcher = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(job == "" ? "" : "Job")}&browserTrackerId={tracker}&placeId={placeId}{(job == "" ? "" : "&gameId=" + job)}&isPlayTogetherGame=false";
            uri = $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{time}+placelauncherurl:{HttpUtility.UrlEncode(launcher)}+browsertrackerid:{tracker}+robloxLocale:ja_jp+gameLocale:ja_jp+channel:+LaunchExp:InApp";
        }
        else
        {
            uri = $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{time}+browsertrackerid:{tracker}+robloxLocale:ja_jp+gameLocale:ja_jp+channel:+LaunchExp:InApp";
        }
        account.LastUsed = DateTime.Now;
        RobloxAccounts.Save();
        // ランチャーが終わるのを待たない(待つとこちらが固まることがある)
        await Task.Run(() => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));
    }
}

/// <summary>
/// Roblox の多重起動(元は Roblox Account Manager の «Multi Roblox»)。
/// Roblox は «ROBLOX_singletonMutex» という印で «もう起動している» かを見ているので、
/// NekoNoTe が先にその印を持っておくと、何個でも起動できるようになる。
/// </summary>
public static class RobloxMultiInstance
{
    private static Mutex? _mutex;

    public static bool Enabled => _mutex != null;
    /// <summary>オン・オフ・待っている、が変わった時</summary>
    public static event Action? Changed;

    /// <summary>オンにする。Roblox がもう起動している時は印を取れないので false</summary>
    public static bool Enable()
    {
        if (_mutex != null) return true;
        try
        {
            var m = new Mutex(true, "ROBLOX_singletonMutex", out var created);
            if (!created && !m.WaitOne(TimeSpan.Zero))
            {
                m.Dispose();
                return false;
            }
            _mutex = m;
            Changed?.Invoke();
            return true;
        }
        catch { return false; }
    }

    private static System.Windows.Threading.DispatcherTimer? _waitTimer;

    /// <summary>Roblox が閉じるのを待っていて、閉じたら自動でオンにするところか</summary>
    public static bool Pending => _waitTimer != null;

    /// <summary>
    /// Roblox が起動していてオンにできない時に、閉じたら自動でオンにする(2 秒ごとに試す)。
    /// 印(Mutex)は取ったスレッドのものになるので、必ず画面のスレッドから呼ぶ。
    /// </summary>
    public static void EnableWhenRobloxCloses()
    {
        if (_mutex != null || _waitTimer != null) return;
        _waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _waitTimer.Tick += (_, _) =>
        {
            if (!Enable()) return;
            StopWaiting();
        };
        _waitTimer.Start();
        Changed?.Invoke();
    }

    private static void StopWaiting()
    {
        if (_waitTimer == null) return;
        _waitTimer.Stop();
        _waitTimer = null;
        Changed?.Invoke();
    }

    public static void Disable()
    {
        StopWaiting();
        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _mutex = null;
        Changed?.Invoke();
    }

    public static bool RobloxRunning() => Process.GetProcessesByName("RobloxPlayerBeta").Length > 0;
}
