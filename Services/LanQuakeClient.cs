using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NekoNoTe.Services;

/// <summary>配っている親の情報</summary>
public sealed record LanHost(IPEndPoint EndPoint, int Priority, long StartedTicks, string Id)
{
    /// <summary>優先度が高い方、同じなら先に起動した方が親になる(同時なら ID で決める)</summary>
    public bool IsBetterThan(int priority, long startedTicks, string id) =>
        Priority != priority ? Priority > priority
        : StartedTicks != startedTicks ? StartedTicks < startedTicks
        : string.CompareOrdinal(Id, id) < 0;
}

/// <summary>同じネットワークで地震情報を配っている親を探して、受け取る</summary>
public static class LanQuakeClient
{
    private const int UdpPort = 47811;
    private const string DiscoverMessage = "NEKO-QUAKE-DISCOVER-1";
    private const string HostMessage = "NEKO-QUAKE-HOST-1";
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(30);

    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    /// <summary>この PC のネットワークごとのアドレスと、そのブロードキャスト先</summary>
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
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add((ua.Address, new IPAddress(broadcast)));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>UDP のソケットを 1 つ作る(誰もいない宛先に送った時の «接続リセット» で受信が失敗しないようにする)</summary>
    private static UdpClient CreateSocket(IPAddress local)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(local, 0));
        const int SioUdpConnReset = -1744830452;
        udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        return udp;
    }

    /// <summary>配っている親を探して、いちばん優先される親を返す(自分は除く)。いなければ null</summary>
    public static async Task<LanHost?> DiscoverAsync(CancellationToken ct)
    {
        // VPN などがあると、宛先だけ指定したブロードキャストは別のネットワークに出てしまう。
        // ネットワークごとにソケットを作って、それぞれのネットワークに送る
        var sockets = new List<UdpClient>();
        var hosts = new List<LanHost>();
        var request = Encoding.ASCII.GetBytes(DiscoverMessage);
        try
        {
            async Task SendAsync(IPAddress local, params IPAddress[] targets)
            {
                UdpClient udp;
                try { udp = CreateSocket(local); }
                catch (SocketException) { return; }
                sockets.Add(udp);
                foreach (var target in targets)
                {
                    try { await udp.SendAsync(request, new IPEndPoint(target, UdpPort), ct); }
                    catch (SocketException) { }
                }
            }

            await SendAsync(IPAddress.Loopback, IPAddress.Loopback);
            foreach (var (local, broadcast) in Networks())
                await SendAsync(local, IPAddress.Broadcast, broadcast);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DiscoverTimeout);
            await Task.WhenAll(sockets.Select(udp => ReceiveRepliesAsync(udp, hosts, timeout.Token)));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (var udp in sockets) udp.Dispose();
        }

        LanHost? best = null;
        foreach (var h in hosts)
            if (best == null || h.IsBetterThan(best.Priority, best.StartedTicks, best.Id)) best = h;
        return best;
    }

    private static async Task ReceiveRepliesAsync(UdpClient udp, List<LanHost> hosts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try { reply = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }   // 1 つの宛先の失敗で、他の親の返事を捨てない

            if (!IsLocalNetwork(reply.RemoteEndPoint.Address)) continue;
            var parts = Encoding.ASCII.GetString(reply.Buffer).Split(' ');
            if (parts.Length == 5 && parts[0] == HostMessage && parts[4] != QuakeLanRelay.InstanceId
                && int.TryParse(parts[1], out var port) && port is > 0 and < 65536
                && int.TryParse(parts[2], out var priority) && long.TryParse(parts[3], out var started))
            {
                lock (hosts)
                {
                    if (hosts.All(h => h.Id != parts[4]))
                        hosts.Add(new LanHost(new IPEndPoint(reply.RemoteEndPoint.Address, port), priority, started, parts[4]));
                }
            }
        }
    }

    /// <summary>つながっている間ずっと受け取る。切れたら戻る</summary>
    public static async Task ReceiveAsync(IPEndPoint host, Action<string> onMessage, Action<bool> onUpstream, CancellationToken ct)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, connect.Token);
        }
        using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
            silence.CancelAfter(SilenceTimeout);   // 10 秒ごとの生存確認が届かなければ、切れたとみなす
            var line = await reader.ReadLineAsync(silence.Token);
            if (line == null) return;
            if (IsHeartbeat(line, out var upstream)) onUpstream(upstream);
            else onMessage(line);
        }
    }

    private static bool IsHeartbeat(string line, out bool upstream)
    {
        upstream = false;
        if (!line.StartsWith("{\"relay\":", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            upstream = doc.RootElement.TryGetProperty("upstream", out var u) && u.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { }
        return true;
    }
}
