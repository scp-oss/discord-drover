using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DroverSwitch.Services;

/// <summary>
/// Telegram WS proxy (Flowseal/tg-ws-proxy, MIT): a local MTProto proxy that carries Telegram's
/// traffic over WebSocket to Telegram's own web endpoints, so Telegram works where its plain
/// MTProto connections are blocked. The CI workflow builds the upstream CLI (pinned commit) into a
/// single tg-ws-proxy.exe and embeds it here; this class extracts it next to DroverSwitch.exe,
/// runs it hidden, restarts it if it dies, and stops it on exit.
///
/// It listens on all interfaces, so a phone on the same Wi-Fi can use this PC as its proxy
/// (QR with the LAN address); Telegram Desktop on this PC uses 127.0.0.1.
/// </summary>
public static class TgWsService
{
    private const string ResourceName = "DroverSwitch.Resources.tg-ws-proxy.exe";
    public const int DefaultPort = 1443;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DroverSwitch", "tg-ws-proxy");
    private static readonly string ExePath = Path.Combine(Dir, "tg-ws-proxy.exe");
    private static readonly string LogPath = Path.Combine(Dir, "tg-ws-proxy.log");

    private static Process? _proc;
    private static bool _wanted;
    private static int _port = DefaultPort;
    private static string _secret = "";

    public static event Action? StateChanged;

    /// <summary>Whether this build carries the proxy at all (local builds without the CI step don't).</summary>
    public static bool IsBundled => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    public static bool IsRunning => _proc is { HasExited: false };

    public static string? LastError { get; private set; }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>tg://proxy link; "dd" = random-padding obfuscation (what tg-ws-proxy itself prints).</summary>
    public static string Link(string host, int port, string secret) =>
        $"tg://proxy?server={host}&port={port}&secret=dd{secret}";

    public static string LocalLink => Link("127.0.0.1", _port, _secret);

    public static string? LanLink => LanAddress() is { } ip ? Link(ip, _port, _secret) : null;

    public static string LinkFor(string ip) => Link(ip, _port, _secret);

    /// <summary>The address the user picked in the QR dialog (saved in settings); used while that
    /// address still exists on this PC, otherwise the best candidate wins.</summary>
    public static string? PreferredLanIp { get; set; }

    /// <summary>A private IPv4 of this PC that a phone could reach, with a human label for its adapter.</summary>
    public sealed record LanCandidate(string Address, string Adapter, bool IsVirtual)
    {
        public string Display => $"{Address} — {Adapter}" + (IsVirtual ? " (виртуальный)" : "");
    }

    /// <summary>Best LAN address for the phone QR (see <see cref="LanCandidates"/>).</summary>
    public static string? LanAddress()
    {
        var all = LanCandidates();
        if (PreferredLanIp is { } p && all.Any(c => c.Address == p))
            return p;
        return all.FirstOrDefault()?.Address;
    }

    // adapters a phone on the Wi-Fi can't reach: hypervisor/WSL/Docker switches, VPN and overlay tunnels
    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "virtual", "tap-", "tap adapter", "wintun", "wireguard",
        "tailscale", "zerotier", "openvpn", "hamachi", "radmin", "npcap", "loopback", "bluetooth", "docker", "wsl",
        "vpn", "pangp", "fortinet", "anyconnect", "cisco", "outline", "amnezia", "sing-box", "clash", "v2ray",
    };

    /// <summary>All private IPv4 addresses of up adapters, best first: real (non-virtual) adapters, then
    /// ones with a default gateway, then the address Windows routes the internet through, then
    /// Wi-Fi/Ethernet over other types. The first one is what the phone on the same Wi-Fi most likely sees;
    /// the QR dialog lets the user pick another if the PC has several networks.</summary>
    public static List<LanCandidate> LanCandidates()
    {
        var scored = new List<(LanCandidate C, int Score)>();
        var routeIp = DefaultRouteSource();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var type = ni.NetworkInterfaceType;
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                    continue;
                var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var isVirtual = VirtualMarkers.Any(text.Contains);
                var isWifi = type == NetworkInterfaceType.Wireless80211;
                var isWired = type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var label = (isWifi ? "Wi-Fi" : isWired ? "кабель" : type.ToString()) + " · " + ni.Name;
                foreach (var u in props.UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a))
                        continue;
                    var score = (isVirtual ? 0 : 8) + (hasGateway ? 4 : 0) +
                                (a.ToString() == routeIp ? 2 : 0) + (isWifi || isWired ? 1 : 0);
                    scored.Add((new LanCandidate(a.ToString(), label, isVirtual), score));
                }
            }
        }
        catch { /* no network info - no LAN link */ }
        return scored.OrderByDescending(x => x.Score)
            .Select(x => x.C)
            .GroupBy(c => c.Address)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Source address Windows would use to reach the internet (UDP "connect" sends nothing).</summary>
    private static string? DefaultRouteSource()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static void Start(int port, string secret)
    {
        // (re)start: drop our own previous process first (port/secret may have changed)
        var old = _proc;
        _proc = null;
        try { if (old is { HasExited: false }) { old.Kill(entireProcessTree: true); old.WaitForExit(3000); } }
        catch { /* already gone */ }
        _wanted = true;
        _port = port;
        _secret = secret;
        LastError = null;
        KillStale();
        if (!IsBundled)
        {
            LastError = "в этой сборке нет tg-ws-proxy.exe";
            StateChanged?.Invoke();
            return;
        }
        try
        {
            ExtractIfChanged();
            Launch();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        StateChanged?.Invoke();
    }

    public static void Stop()
    {
        _wanted = false;
        var p = _proc;
        _proc = null;
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { /* already gone */ }
        StateChanged?.Invoke();
    }

    private static void Launch()
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            Arguments = $"--host 0.0.0.0 --port {_port} --secret {_secret} --log-file \"{LogPath}\" --log-max-mb 2",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Dir,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += async (_, _) =>
        {
            if (!_wanted || !ReferenceEquals(_proc, p))
                return;
            LastError = $"tg-ws-proxy завершился (код {SafeExitCode(p)}), перезапускаю…";
            StateChanged?.Invoke();
            await Task.Delay(3000);
            if (_wanted && ReferenceEquals(_proc, p))
            {
                try { Launch(); LastError = null; }
                catch (Exception ex) { LastError = ex.Message; }
                StateChanged?.Invoke();
            }
        };
        p.Start();
        _proc = p;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>Writes the embedded exe out only when it differs (new DroverSwitch version), so a
    /// normal start doesn't rewrite ~15 MB every time.</summary>
    private static void ExtractIfChanged()
    {
        Directory.CreateDirectory(Dir);
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("tg-ws-proxy.exe не найден в сборке");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        var bytes = ms.ToArray();
        if (File.Exists(ExePath))
        {
            var existing = File.ReadAllBytes(ExePath);
            if (existing.Length == bytes.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                return;
        }
        File.WriteAllBytes(ExePath, bytes);
    }

    /// <summary>A previous DroverSwitch that crashed or was killed by the updater can leave its
    /// tg-ws-proxy.exe running (holding the port and the file) - clean those up first.</summary>
    private static void KillStale()
    {
        foreach (var p in Process.GetProcessesByName("tg-ws-proxy"))
        {
            try
            {
                if (p.MainModule?.FileName?.Equals(ExePath, StringComparison.OrdinalIgnoreCase) == true &&
                    (_proc is null || p.Id != _proc.Id))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch { /* not ours or already gone */ }
        }
    }
}
