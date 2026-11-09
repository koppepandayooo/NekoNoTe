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

    /// <summary>この PC から届く範囲のブロードキャスト先</summary>
    private static List<IPAddress> Targets()
    {
        var list = new List<IPAddress> { IPAddress.Loopback, IPAddress.Broadcast };
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
                    list.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>配っている親を探して、いちばん優先される親を返す(自分は除く)。いなければ null</summary>
    public static async Task<LanHost?> DiscoverAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var request = Encoding.ASCII.GetBytes(DiscoverMessage);
        foreach (var target in Targets())
        {
            try { await udp.SendAsync(request, new IPEndPoint(target, UdpPort), ct); }
            catch (SocketException) { }
        }

        var hosts = new List<LanHost>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DiscoverTimeout);
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (!IsLocalNetwork(reply.RemoteEndPoint.Address)) continue;
                var parts = Encoding.ASCII.GetString(reply.Buffer).Split(' ');
                if (parts.Length == 5 && parts[0] == HostMessage && parts[4] != QuakeLanRelay.InstanceId
                    && int.TryParse(parts[1], out var port) && port is > 0 and < 65536
                    && int.TryParse(parts[2], out var priority) && long.TryParse(parts[3], out var started)
                    && hosts.All(h => h.Id != parts[4]))
                {
                    hosts.Add(new LanHost(new IPEndPoint(reply.RemoteEndPoint.Address, port), priority, started, parts[4]));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }

        LanHost? best = null;
        foreach (var h in hosts)
            if (best == null || h.IsBetterThan(best.Priority, best.StartedTicks, best.Id)) best = h;
        return best;
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
