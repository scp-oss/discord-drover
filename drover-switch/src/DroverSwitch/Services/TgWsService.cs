using System;
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

    /// <summary>IPv4 of the interface that has a default gateway (the one the phone's Wi-Fi shares).</summary>
    public static string? LanAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                     !g.Address.Equals(IPAddress.Any)))
                    continue;
                var addr = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));
                if (addr is not null)
                    return addr.ToString();
            }
        }
        catch { /* no network info - no LAN link */ }
        return null;
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
