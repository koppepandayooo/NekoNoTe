using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekoNoTe.LanShare;

/// <summary>見つかった、共有をオンにしている PC</summary>
public sealed record SharePeer(string Id, string Name, IPEndPoint EndPoint);

/// <summary>共有フォルダの中のファイル</summary>
public sealed record ShareFile(string Name, long Size, DateTime Modified);

/// <summary>PIN が違う・PIN を間違えすぎて少し待つ必要がある時</summary>
public sealed class SharePinException(string message) : Exception(message);

/// <summary>LAN ファイル共有の設定。%APPDATA%\NekoNoTe\lanshare.json</summary>
public sealed class LanShareSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "lanshare.json");

    public bool Enabled { get; set; }
    /// <summary>この PC の PIN(ほかの PC から見る時に入れてもらう)</summary>
    public string Pin { get; set; } = "";
    /// <summary>共有するフォルダ(受け取ったファイルもここに入る)</summary>
    public string Folder { get; set; } = "";
    /// <summary>ほかの PC の PIN(一度入れたら覚えておく)。キーは相手の ID</summary>
    public Dictionary<string, string> PeerPins { get; set; } = new();

    public static LanShareSettings Load()
    {
        LanShareSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<LanShareSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        var changed = false;
        if (s.Pin.Length != 4 || !s.Pin.All(char.IsAsciiDigit)) { s.Pin = NewPin(); changed = true; }
        if (string.IsNullOrWhiteSpace(s.Folder))
        {
            s.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NekoNoTe 共有");
            changed = true;
        }
        if (string.IsNullOrEmpty(s.Id)) { s.Id = Guid.NewGuid().ToString("N"); changed = true; }
        if (changed) s.Save();   // PIN と ID は毎回変わらないように、最初に決めたものを残す
        return s;
    }

    /// <summary>この PC の ID(ほかの PC が PIN を覚えておくのに使う。インストールごとに 1 つ)</summary>
    public string Id { get; set; } = "";

    public static string NewPin() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000).ToString("0000");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 同じネットワークの PC どうしでファイルを送り合う(NekoNoTe のタブの中で使う。ブラウザは使わない。NekoApp とも送り合える)。
/// 共有をオンにすると、TCP で «一覧・受け取り・送り込み» を受け付け、UDP の問い合わせに答える。
/// 相手は家の中のアドレス(プライベート IP)だけ、毎回 4 桁の PIN を確かめ、間違えすぎたら少し締め出す。
/// 1 つの接続で 1 つの用事: 最初に JSON を 1 行送り、返事も JSON 1 行。ファイルの中身はその後に続ける。
/// </summary>
public sealed class LanShareService
{
    public const int TcpPort = 47820;
    public const int UdpPort = 47821;
    private const string DiscoverMessage = "NEKO-SHARE-DISCOVER-1";
    private const string HostMessage = "NEKO-SHARE-HOST-1";
    private const int MaxHeaderBytes = 8192;
    private const int MaxConnections = 8;
    private const int MaxPinFailures = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(1.2);
    private const int BufferSize = 1 << 20;

    public static LanShareService Instance { get; } = new();

    public LanShareSettings Settings { get; } = LanShareSettings.Load();

    private CancellationTokenSource? _cts;
    private int _connections;
    private readonly Dictionary<IPAddress, (int Failures, DateTime LockedUntil)> _pinFailures = new();

    public bool Enabled => _cts != null;
    /// <summary>オン・オフが変わった時</summary>
    public event Action? StateChanged;
    /// <summary>ほかの PC からファイルを受け取った時(ファイル名、相手のアドレス)</summary>
    public event Action<string, IPAddress>? FileReceived;

    private LanShareService() { }

    // ===================== この PC の共有(受け付ける側) =====================

    /// <summary>オン・オフ。ポートが使われていてオンにできない時は false</summary>
    public bool SetEnabled(bool on)
    {
        if (!on)
        {
            _cts?.Cancel();
            _cts = null;
            Settings.Enabled = false;
            Settings.Save();
            StateChanged?.Invoke();
            return true;
        }
        if (_cts != null) return true;

        TcpListener? tcp = null;
        UdpClient? udp = null;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            tcp = new TcpListener(IPAddress.Any, TcpPort);
            tcp.Start();
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            tcp?.Stop();
            udp?.Dispose();
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(tcp, _cts.Token);
        _ = AnswerLoopAsync(udp, _cts.Token);
        Settings.Enabled = true;
        Settings.Save();
        StateChanged?.Invoke();
        return true;
    }

    public void ChangePin()
    {
        Settings.Pin = LanShareSettings.NewPin();
        Settings.Save();
        StateChanged?.Invoke();
    }

    public IReadOnlyList<ShareFile> LocalFiles()
    {
        try
        {
            return new DirectoryInfo(Settings.Folder).EnumerateFiles()
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && (f.Attributes & FileAttributes.Hidden) == 0)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new ShareFile(f.Name, f.Length, f.LastWriteTime))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<ShareFile>(); }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote == null || !IsLocalNetwork(remote) || Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    if (remote != null && IsLocalNetwork(remote)) Interlocked.Decrement(ref _connections);
                    tcp.Dispose();
                    continue;
                }
                _ = HandleAsync(tcp, remote, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally { listener.Stop(); }
    }

    private async Task AnswerLoopAsync(UdpClient udp, CancellationToken ct)
    {
        using (udp)
        {
            var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.MachineName));
            var reply = Encoding.ASCII.GetBytes($"{HostMessage} {TcpPort} {Settings.Id} {name}");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var request = await udp.ReceiveAsync(ct);
                    if (IsLocalNetwork(request.RemoteEndPoint.Address) && Encoding.ASCII.GetString(request.Buffer) == DiscoverMessage)
                        await udp.SendAsync(reply, request.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { }
            }
        }
    }

    private async Task HandleAsync(TcpClient tcp, IPAddress remote, CancellationToken ct)
    {
        try
        {
            using (tcp)
            {
                var stream = tcp.GetStream();
                JsonObject request;
                using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    headerTimeout.CancelAfter(HeaderTimeout);
                    request = JsonNode.Parse(await ReadLineAsync(stream, headerTimeout.Token))!.AsObject();
                }

                if (!CheckPin(remote, (string?)request["pin"], out var pinError))
                {
                    await WriteLineAsync(stream, new JsonObject { ["ok"] = false, ["error"] = pinError }, ct);
                    return;
                }

                switch ((string?)request["op"])
                {
                    case "list":
                        var files = new JsonArray(LocalFiles().Select(f => (JsonNode)new JsonObject
                        {
                            ["n"] = f.Name, ["s"] = f.Size, ["m"] = f.Modified.ToString("O"),
                        }).ToArray());
                        await WriteLineAsync(stream, new JsonObject { ["ok"] = true, ["name"] = Environment.MachineName, ["files"] = files }, ct);
                        break;

                    case "get":
                        var path = Path.Combine(Settings.Folder, SafeName((string?)request["name"]));
                        if (!File.Exists(path))
                        {
                            await WriteLineAsync(stream, new JsonObject { ["ok"] = false, ["error"] = "ファイルが見つかりません" }, ct);
                            return;
                        }
                        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
                        {
                            await WriteLineAsync(stream, new JsonObject { ["ok"] = true, ["size"] = file.Length }, ct);
                            await file.CopyToAsync(stream, BufferSize, ct);
                        }
                        break;

                    case "put":
                        var size = (long?)request["size"] ?? -1;
                        if (size < 0)
                        {
                            await WriteLineAsync(stream, new JsonObject { ["ok"] = false, ["error"] = "大きさが分かりません" }, ct);
                            return;
                        }
                        Directory.CreateDirectory(Settings.Folder);
                        var dest = UniquePath(Path.Combine(Settings.Folder, SafeName((string?)request["name"])));
                        var part = dest + ".part";
                        await WriteLineAsync(stream, new JsonObject { ["ok"] = true }, ct);   // 送ってきてよい
                        try
                        {
                            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                                await CopyExactlyAsync(stream, file, size, null, ct);
                            File.Move(part, dest);
                        }
                        catch
                        {
                            try { File.Delete(part); } catch { }
                            throw;
                        }
                        await WriteLineAsync(stream, new JsonObject { ["ok"] = true, ["saved"] = Path.GetFileName(dest) }, ct);
                        FileReceived?.Invoke(Path.GetFileName(dest), remote);
                        break;

                    default:
                        await WriteLineAsync(stream, new JsonObject { ["ok"] = false, ["error"] = "分からない用事です" }, ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException
                                       or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException
                                       or NullReferenceException or FormatException)
        {
            // 相手が途中で切った・おかしな内容だった時は、その接続だけやめる
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    /// <summary>PIN を確かめる。続けて間違えたアドレスは少しの間締め出す</summary>
    private bool CheckPin(IPAddress remote, string? pin, out string error)
    {
        lock (_pinFailures)
        {
            var now = DateTime.UtcNow;
            _pinFailures.TryGetValue(remote, out var state);
            if (state.LockedUntil > now)
            {
                error = "locked";
                return false;
            }
            if (pin == Settings.Pin)
            {
                _pinFailures.Remove(remote);
                error = "";
                return true;
            }
            var failures = state.Failures + 1;
            _pinFailures[remote] = failures >= MaxPinFailures ? (0, now + LockoutTime) : (failures, DateTime.MinValue);
            error = failures >= MaxPinFailures ? "locked" : "pin";
            return false;
        }
    }

    // ===================== ほかの PC(使う側) =====================

    /// <summary>共有をオンにしている PC を探す(自分は除く)</summary>
    public async Task<IReadOnlyList<SharePeer>> DiscoverAsync(CancellationToken ct)
    {
        var sockets = new List<UdpClient>();
        var peers = new List<SharePeer>();
        var request = Encoding.ASCII.GetBytes(DiscoverMessage);
        try
        {
            // VPN などがあると、宛先だけのブロードキャストは別のネットワークに出てしまうので、ネットワークごとに送る
            foreach (var (local, broadcast) in Networks())
            {
                UdpClient udp;
                try
                {
                    udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                    udp.Client.Bind(new IPEndPoint(local, 0));
                    const int SioUdpConnReset = -1744830452;
                    udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
                }
                catch (SocketException) { continue; }
                sockets.Add(udp);
                foreach (var target in new[] { IPAddress.Broadcast, broadcast })
                {
                    try { await udp.SendAsync(request, new IPEndPoint(target, UdpPort), ct); }
                    catch (SocketException) { }
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DiscoverTimeout);
            await Task.WhenAll(sockets.Select(udp => ReceiveRepliesAsync(udp, peers, timeout.Token)));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (var udp in sockets) udp.Dispose();
        }
        return peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task ReceiveRepliesAsync(UdpClient udp, List<SharePeer> peers, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try { reply = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            if (!IsLocalNetwork(reply.RemoteEndPoint.Address)) continue;
            var parts = Encoding.ASCII.GetString(reply.Buffer).Split(' ');
            if (parts.Length != 4 || parts[0] != HostMessage || parts[2] == Settings.Id || !int.TryParse(parts[1], out var port) || port is <= 0 or >= 65536) continue;
            string name;
            try { name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])); }
            catch (FormatException) { continue; }
            lock (peers)
            {
                if (peers.All(p => p.Id != parts[2]))
                    peers.Add(new SharePeer(parts[2], name, new IPEndPoint(reply.RemoteEndPoint.Address, port)));
            }
        }
    }

    /// <summary>相手の共有フォルダの一覧</summary>
    public async Task<IReadOnlyList<ShareFile>> ListAsync(SharePeer peer, string pin, CancellationToken ct)
    {
        using var tcp = await ConnectAsync(peer, ct);
        var stream = tcp.GetStream();
        await WriteLineAsync(stream, new JsonObject { ["op"] = "list", ["pin"] = pin }, ct);
        var reply = await ReadReplyAsync(stream, ct);
        return reply["files"]!.AsArray().Select(f => new ShareFile(
            (string)f!["n"]!, (long)f["s"]!, DateTime.TryParse((string?)f["m"], out var m) ? m : DateTime.MinValue)).ToList();
    }

    /// <summary>相手のファイルを受け取って destFolder に保存する。保存した場所を返す</summary>
    public async Task<string> DownloadAsync(SharePeer peer, string pin, string name, string destFolder, IProgress<double>? progress, CancellationToken ct)
    {
        using var tcp = await ConnectAsync(peer, ct);
        var stream = tcp.GetStream();
        await WriteLineAsync(stream, new JsonObject { ["op"] = "get", ["pin"] = pin, ["name"] = name }, ct);
        var reply = await ReadReplyAsync(stream, ct);
        var size = (long)reply["size"]!;

        Directory.CreateDirectory(destFolder);
        var dest = UniquePath(Path.Combine(destFolder, SafeName(name)));
        var part = dest + ".part";
        try
        {
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                await CopyExactlyAsync(stream, file, size, progress, ct);
            File.Move(part, dest);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        return dest;
    }

    /// <summary>ファイルを相手の共有フォルダに送る。相手のところでの名前を返す(同じ名前があれば «(2)» などが付く)</summary>
    public async Task<string> UploadAsync(SharePeer peer, string pin, string path, IProgress<double>? progress, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
        using var tcp = await ConnectAsync(peer, ct);
        var stream = tcp.GetStream();
        await WriteLineAsync(stream, new JsonObject { ["op"] = "put", ["pin"] = pin, ["name"] = Path.GetFileName(path), ["size"] = file.Length }, ct);
        await ReadReplyAsync(stream, ct);   // 受け取る準備ができた
        await CopyExactlyAsync(file, stream, file.Length, progress, ct);
        var done = await ReadReplyAsync(stream, ct);
        return (string?)done["saved"] ?? Path.GetFileName(path);
    }

    private static async Task<TcpClient> ConnectAsync(SharePeer peer, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(peer.EndPoint, connect.Token);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<JsonObject> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var reply = JsonNode.Parse(await ReadLineAsync(stream, timeout.Token))!.AsObject();
        if ((bool?)reply["ok"] == true) return reply;
        throw (string?)reply["error"] switch
        {
            "pin" => new SharePinException("PIN が違います"),
            "locked" => new SharePinException("PIN を何度も間違えたので、1 分ほど待ってからやり直してください"),
            var e => new IOException(e ?? "相手の PC で失敗しました"),
        };
    }

    // ===================== 共通 =====================

    /// <summary>改行までを 1 バイトずつ読む(後ろに続くファイルの中身を読みすぎないように)</summary>
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) throw new IOException("切断されました");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
            if (bytes.Count > MaxHeaderBytes) throw new IOException("長すぎます");
        }
    }

    private static async Task WriteLineAsync(Stream stream, JsonObject json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>ちょうど size バイトを写す。足りないまま切れたら失敗にする</summary>
    private static async Task CopyExactlyAsync(Stream from, Stream to, long size, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long done = 0;
        var lastReport = DateTime.MinValue;
        while (done < size)
        {
            var n = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - done)), ct);
            if (n == 0) throw new IOException("途中で切れました");
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(size == 0 ? 1 : (double)done / size);
            }
        }
        await to.FlushAsync(ct);
        progress?.Report(1);
    }

    /// <summary>ファイル名だけを取り出して、Windows で使えない文字を消す(フォルダの外に出られないようにする)</summary>
    public static string SafeName(string? name)
    {
        var baseName = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        var cleaned = new string(baseName.Where(c => c >= 32 && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim(' ', '.');
        if (cleaned.Length == 0) cleaned = "file";
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>同じ名前があれば «名前 (2).拡張子» のようにずらす</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part")) return candidate;
        }
    }

    /// <summary>家の中のアドレスかどうか(IPv4 のプライベートアドレスとこの PC 自身だけ)</summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    private static List<(IPAddress Local, IPAddress Broadcast)> Networks()
    {
        var list = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || !IsLocalNetwork(ua.Address)) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add((ua.Address, new IPAddress(broadcast)));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }
}
