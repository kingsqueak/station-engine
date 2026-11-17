// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR — letting a radio go when another SimpleHPSDR takes it over.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SimpleHPSDR.Server;

/// <summary>What <see cref="RadioYieldService"/> last gave up, for the app to show.</summary>
public sealed record RadioYieldInfo(long Count, string? Radio, string? TakenBy, DateTimeOffset? At);

/// <summary>
/// A take-over's stop command only reaches the radio. The program that had the
/// radio is never told, keeps sending its own settings, and the radio then has
/// two controllers: it switches its band filter and antenna relays every time
/// one of them speaks (seen 2026-11-17, Mac on 40 m and a Pi on 20 m).
///
/// <para>So the engine that takes over first says so on the local network
/// (<see cref="AnnounceAsync"/>: a UDP broadcast naming the radio), and every
/// engine listens (<see cref="ExecuteAsync"/>). One that is connected to that
/// radio disconnects at once and remembers who took it (<see cref="Last"/>);
/// its app shows that. A controller that isn't SimpleHPSDR doesn't listen;
/// the take-over refuses to share the radio with it (RadioConnectionEndpoints).</para>
///
/// <para>The datagram is no more trusted than the radio protocol itself, where
/// any host on the network can stop the radio: it can only make this engine
/// let go of the radio it names.</para>
/// </summary>
public sealed class RadioYieldService : BackgroundService
{
    public const int Port = 47810;
    private const string Magic = "SIMPLEHPSDR-TAKEOVER 1";
    private const int SendCount = 3;
    private const string Yielding = "SIMPLEHPSDR-YIELDING 1";
    private const string Yielded = "SIMPLEHPSDR-YIELDED 1";
    /// <summary>How long to wait for an engine to say it heard the announcement.</summary>
    private static readonly TimeSpan HearTime = TimeSpan.FromMilliseconds(400);
    /// <summary>How long an engine that heard it gets to finish disconnecting.</summary>
    private static readonly TimeSpan YieldTime = TimeSpan.FromSeconds(4);

    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly IServiceProvider _services;
    private readonly ILogger<RadioYieldService> _log;
    private readonly object _sync = new();
    private RadioYieldInfo _last = new(0, null, null, null);
    private (IPAddress From, IPAddress Radio)? _lastHeard;

    public RadioYieldService(IServiceProvider services, ILogger<RadioYieldService> log)
    {
        _services = services;
        _log = log;
    }

    public RadioYieldInfo Last { get { lock (_sync) return _last; } }

    internal static byte[] BuildAnnouncement(IPAddress radio, string instance) =>
        Encoding.ASCII.GetBytes($"{Magic} {radio} {instance}");

    /// <summary>The radio and sender named by an announcement, or false.</summary>
    internal static bool TryParseAnnouncement(ReadOnlySpan<byte> datagram, out IPAddress radio, out string instance)
    {
        radio = IPAddress.None;
        instance = "";
        if (datagram.Length is 0 or > 200) return false;
        var text = Encoding.ASCII.GetString(datagram);
        if (!text.StartsWith(Magic + " ", StringComparison.Ordinal)) return false;
        var parts = text[(Magic.Length + 1)..].Split(' ');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || parts[1].Length == 0) return false;
        radio = ip;
        instance = parts[1];
        return true;
    }

    /// <summary>
    /// Tell the other SimpleHPSDR engines on the local network that this one
    /// is taking <paramref name="radio"/>, and wait for any that had it to let
    /// go: they answer at once that they heard, and again when they have
    /// disconnected. Best effort: a network that drops broadcasts just means
    /// nobody hears it. Returns how many engines let the radio go.
    /// </summary>
    public async Task<int> AnnounceAsync(IPAddress radio, CancellationToken ct)
    {
        var payload = BuildAnnouncement(radio, _instance);
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.EnableBroadcast = true;
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));
        int networks = 0;
        foreach (var broadcast in BroadcastAddresses())
        {
            try
            {
                for (int i = 0; i < SendCount; i++) sock.SendTo(payload, new IPEndPoint(broadcast, Port));
                networks++;
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "radio.yield announce to {Broadcast} failed", broadcast);
            }
        }

        // Nobody answering within HearTime means no SimpleHPSDR had it. One
        // that answers gets YieldTime to finish disconnecting, so its own
        // last stop command can't land on our new connection.
        var hearing = new HashSet<string>();
        int done = 0;
        var buf = new byte[256];
        var deadline = DateTime.UtcNow + HearTime;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(left);
            try
            {
                var r = await sock.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token)
                    .ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(buf, 0, r.ReceivedBytes);
                var from = ((IPEndPoint)r.RemoteEndPoint).Address.ToString();
                if (text == Yielding && hearing.Add(from)) deadline = DateTime.UtcNow + YieldTime;
                else if (text == Yielded && hearing.Remove(from))
                {
                    done++;
                    if (hearing.Count == 0) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
        }
        _log.LogInformation(
            "radio.yield announced take-over of {Radio} on {N} network(s): {Done} engine(s) let go, {Waiting} still busy",
            radio, networks, done, hearing.Count);
        return done;
    }

    /// <summary>The broadcast address of each IPv4 network this computer is on.</summary>
    private static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = a.Address.GetAddressBytes();
                    var mask = a.IPv4Mask?.GetAddressBytes();
                    if (mask is not { Length: 4 }) continue;
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var b = new IPAddress(bc);
                    if (!list.Contains(b)) list.Add(b);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Socket sock;
        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // More than one engine on a computer may listen.
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (SocketException ex)
        {
            _log.LogWarning(ex, "radio.yield cannot listen on UDP {Port}; a take-over from another computer will not be noticed here", Port);
            return;
        }

        using (sock)
        {
            var buf = new byte[256];
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var r = await sock.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stoppingToken)
                        .ConfigureAwait(false);
                    if (!TryParseAnnouncement(buf.AsSpan(0, r.ReceivedBytes), out var radio, out var instance)
                        || instance == _instance) continue;
                    await YieldAsync(sock, radio, (IPEndPoint)r.RemoteEndPoint, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "radio.yield listener error");
                    try { await Task.Delay(1000, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    private async Task YieldAsync(Socket sock, IPAddress radioIp, IPEndPoint taker, CancellationToken ct)
    {
        var radio = _services.GetRequiredService<RadioService>();
        var from = taker.Address;
        var endpoint = radio.IsConnected ? radio.Snapshot().Endpoint : null;
        if (!RadioConnectionEndpoints.TryParseIpEndpoint(endpoint ?? "", out var connected)
            || !connected.Address.Equals(radioIp))
        {
            // Once per announcement would be three lines; one is enough.
            if (_lastHeard is not { } heard || !heard.From.Equals(from) || !heard.Radio.Equals(radioIp))
                _log.LogInformation("radio.yield {From} is taking over {Radio}; not ours, nothing to do", from, radioIp);
            _lastHeard = (from, radioIp);
            return;
        }
        _lastHeard = null;

        _log.LogWarning("radio.yield {From} is taking over {Radio}; disconnecting", from, radioIp);
        Reply(sock, taker, Yielding);
        lock (_sync) _last = new RadioYieldInfo(_last.Count + 1, radioIp.ToString(), from.ToString(), DateTimeOffset.UtcNow);
        try
        {
            // As the operator's own disconnect: nothing reconnects by itself afterwards.
            _services.GetRequiredService<IP2AutoConnectControl>().DisableForManualDisconnect();
            if (radio.IsProtocol2Active)
                await _services.GetRequiredService<DspPipelineService>().DisconnectP2Async(ct).ConfigureAwait(false);
            else
                await radio.DisconnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Reply(sock, taker, Yielded);
        }
        _log.LogInformation("radio.yield disconnected from {Radio}", radioIp);
    }

    /// <summary>The announcement is sent several times; one answer per copy is harmless.</summary>
    private void Reply(Socket sock, IPEndPoint taker, string text)
    {
        try { sock.SendTo(Encoding.ASCII.GetBytes(text), taker); }
        catch (SocketException ex) { _log.LogDebug(ex, "radio.yield reply to {Taker} failed", taker); }
    }
}
