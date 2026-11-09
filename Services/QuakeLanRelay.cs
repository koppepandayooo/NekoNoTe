using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NekoNoTe.Services;

/// <summary>
/// 受信した地震情報を、家の LAN の NekoNoTe に配る(P2P地震情報への接続は IP ごとに 2 つまでなので、外への接続を 1 つで済ませる)。
/// 探す時は UDP で問い合わせに答え、配信は TCP で JSON を 1 行ずつ流す。受け取るだけで、相手からの入力は読まない。
/// </summary>
public sealed class QuakeLanRelay
{
    public const int TcpPort = 47810;
    public const int UdpPort = 47811;
    public const string DiscoverMessage = "NEKO-QUAKE-DISCOVER-1";
    public const string HostMessage = "NEKO-QUAKE-HOST-1";
    private const int MaxClients = 16;
    /// <summary>親の候補が複数いる時の優先度(大きい方が親になる)</summary>
    public const int Priority = 1;
    /// <summary>自分の問い合わせに自分で答えないように使う</summary>
    public static readonly string InstanceId = Guid.NewGuid().ToString("N");
    /// <summary>同じ優先度なら、先に起動した方が親になる</summary>
    public static readonly long StartedTicks = DateTime.UtcNow.Ticks;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    public static QuakeLanRelay Instance { get; } = new();

    private sealed class Client
    {
        public required TcpClient Tcp { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private readonly List<Client> _clients = new();
    private CancellationTokenSource? _cts;
    private volatile bool _upstream;

    public bool Enabled => _cts != null;
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    /// <summary>P2P地震情報につながっているか(受け取る側にも知らせる)</summary>
    public bool Upstream
    {
        get => _upstream;
        set => _upstream = value;
    }

    /// <summary>オン・オフ。ポートを開けない時は false</summary>
    public bool SetEnabled(bool on)
    {
        if (!on)
        {
            _cts?.Cancel();
            _cts = null;
            DropAll();
            return true;
        }
        if (_cts != null) return true;

        TcpListener? tcp = null;
        UdpClient? udp = null;
        try
        {
            tcp = new TcpListener(IPAddress.Any, TcpPort);
            tcp.Start();
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
        }
        catch (SocketException)
        {
            tcp?.Stop();
            udp?.Dispose();
            return false;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = AcceptLoopAsync(tcp, ct);
        _ = AnswerLoopAsync(udp, ct);
        _ = HeartbeatLoopAsync(ct);
        return true;
    }

    /// <summary>家の中のアドレスかどうか(IPv4 のプライベートアドレスとこの PC 自身だけを相手にする)</summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
                lock (_clients)
                {
                    if (remote == null || !IsLocalNetwork(remote) || _clients.Count >= MaxClients)
                    {
                        tcp.Dispose();
                        continue;
                    }
                    tcp.NoDelay = true;
                    _clients.Add(new Client { Tcp = tcp });
                }
                await SendHeartbeatAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally { listener.Stop(); }
    }

    private static async Task AnswerLoopAsync(UdpClient udp, CancellationToken ct)
    {
        using (udp)
        {
            var reply = Encoding.ASCII.GetBytes($"{HostMessage} {TcpPort} {Priority} {StartedTicks} {InstanceId}");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var request = await udp.ReceiveAsync(ct);
                    if (IsLocalNetwork(request.RemoteEndPoint.Address) && Encoding.ASCII.GetString(request.Buffer) == DiscoverMessage)
                        await udp.SendAsync(reply, request.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { }   // 相手がいなくなった時など。待ち受けは続ける
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await SendHeartbeatAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task SendHeartbeatAsync() => SendLineAsync($"{{\"relay\":1,\"upstream\":{(_upstream ? "true" : "false")}}}");

    /// <summary>P2P地震情報から届いた JSON をそのまま配る</summary>
    public void Broadcast(string json)
    {
        if (Enabled) _ = SendLineAsync(json.Replace('\r', ' ').Replace('\n', ' '));
    }

    private async Task SendLineAsync(string line)
    {
        Client[] targets;
        lock (_clients) targets = _clients.ToArray();
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await Task.WhenAll(targets.Select(c => SendAsync(c, bytes)));
    }

    private async Task SendAsync(Client client, byte[] bytes)
    {
        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await client.WriteLock.WaitAsync(timeout.Token);
            try { await client.Tcp.GetStream().WriteAsync(bytes, timeout.Token); }
            finally { client.WriteLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            lock (_clients) _clients.Remove(client);
            client.Tcp.Dispose();
        }
    }

    private void DropAll()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Tcp.Dispose();
            _clients.Clear();
        }
    }
}
