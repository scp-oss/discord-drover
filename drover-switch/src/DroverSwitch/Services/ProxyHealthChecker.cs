using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DroverSwitch.Services;

/// <summary>
/// Tests whether Discord is actually reachable through a given proxy. Opens a real tunnel to
/// discord.com (an HTTP CONNECT for http proxies, a SOCKS5 handshake for socks5 ones, or a plain
/// TCP connect for "Direct"), then does a genuine TLS handshake and HTTP request over it - some
/// proxies answer "200 Connection established" optimistically without the upstream connection
/// actually working, and DPI-based blocking often lets a bare TCP/CONNECT through but resets the
/// connection once it sees the TLS ClientHello's SNI. A successful handshake + HTTP response is
/// the only way to tell those apart from a proxy that genuinely reaches Discord.
/// </summary>
public static class ProxyHealthChecker
{
    private const string ProbeHost = "discord.com";
    private const int ProbePort = 443;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Reachable + how long the whole probe (tunnel + TLS + HTTP round trip) took, in ms.</summary>
    public static async Task<(bool Reachable, long? LatencyMs)> CheckAsync(string proxyUrl, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var proxy = ParsedProxy.Parse(proxyUrl);

            var ok = !proxy.IsSpecified
                ? await CheckDirectAsync(cts.Token)
                : proxy.IsSocks5
                    ? await CheckSocks5Async(proxy, cts.Token)
                    // HTTP (and the unknown-protocol fallback, same as drover treats it) goes through CONNECT.
                    : await CheckHttpConnectAsync(proxy, cts.Token);

            return ok ? (true, stopwatch.ElapsedMilliseconds) : (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<bool> CheckDirectAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ProbeHost, ProbePort, ct);
        return await VerifyTunnelAsync(tcp.GetStream(), ct);
    }

    private static async Task<bool> CheckHttpConnectAsync(ParsedProxy proxy, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);

        var sb = new StringBuilder();
        sb.Append($"CONNECT {ProbeHost}:{ProbePort} HTTP/1.1\r\n");
        sb.Append($"Host: {ProbeHost}:{ProbePort}\r\n");
        if (proxy.IsAuth)
        {
            var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{proxy.Login}:{proxy.Password}"));
            sb.Append($"Proxy-Authorization: Basic {creds}\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");

        var requestBytes = Encoding.ASCII.GetBytes(sb.ToString());

        var stream = tcp.GetStream();
        await stream.WriteAsync(requestBytes, ct);

        var statusLine = await ReadLineAsync(stream, ct);
        // "HTTP/1.1 200 Connection established" (or 200 OK) means the proxy is willing to tunnel -
        // not proof that the tunnel actually reaches Discord; some proxies answer this immediately
        // and optimistically before (or without) actually connecting upstream.
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[1].StartsWith('2'))
            return false;

        // Drain the rest of the proxy's response headers up to the blank line, so none of them
        // end up mixed into the TLS handshake that follows.
        string line;
        do
        {
            line = await ReadLineAsync(stream, ct);
        } while (line.Length > 0);

        return await VerifyTunnelAsync(stream, ct);
    }

    private static async Task<bool> CheckSocks5Async(ParsedProxy proxy, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);
        var stream = tcp.GetStream();

        // Greeting: SOCKS5, 1 method offered, "no authentication".
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var chosen = await ReadExactAsync(stream, 2, ct);
        if (chosen[0] != 0x05 || chosen[1] != 0x00)
            return false; // server wants auth we don't support, or rejected us outright.

        var domainBytes = Encoding.ASCII.GetBytes(ProbeHost);
        var request = new byte[7 + domainBytes.Length];
        request[0] = 0x05; // version
        request[1] = 0x01; // CONNECT
        request[2] = 0x00; // reserved
        request[3] = 0x03; // ATYP = domain name
        request[4] = (byte)domainBytes.Length;
        Array.Copy(domainBytes, 0, request, 5, domainBytes.Length);
        request[5 + domainBytes.Length] = (byte)(ProbePort >> 8);
        request[6 + domainBytes.Length] = (byte)(ProbePort & 0xFF);

        await stream.WriteAsync(request, ct);

        var reply = await ReadExactAsync(stream, 4, ct);
        if (reply[0] != 0x05 || reply[1] != 0x00)
            return false;

        // Drain the rest of the reply (bound address/port) so we don't leave the socket mid-frame.
        var remaining = reply[3] switch
        {
            0x01 => 4 + 2,
            0x03 => (await ReadExactAsync(stream, 1, ct))[0] + 2,
            0x04 => 16 + 2,
            _ => 0,
        };
        if (remaining > 0)
            await ReadExactAsync(stream, remaining, ct);

        return await VerifyTunnelAsync(stream, ct);
    }

    /// <summary>
    /// Proves the tunnel actually reaches Discord: a real TLS handshake (with normal certificate
    /// validation - a blocking appliance terminating TLS itself would fail this) followed by a
    /// minimal HTTP request/response round trip.
    /// </summary>
    private static async Task<bool> VerifyTunnelAsync(Stream rawStream, CancellationToken ct)
    {
        using var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = ProbeHost }, ct);

        var request = Encoding.ASCII.GetBytes(
            $"HEAD / HTTP/1.1\r\nHost: {ProbeHost}\r\nConnection: close\r\n\r\n");
        await ssl.WriteAsync(request, ct);

        var buffer = new byte[16];
        var read = await ssl.ReadAsync(buffer, ct);
        return read > 0;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                throw new IOException("Соединение закрыто прокси раньше ответа.");
            offset += read;
        }
        return buffer;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0)
                break;
            if (buffer[0] == (byte)'\n')
                break;
            if (buffer[0] != (byte)'\r')
                bytes.Add(buffer[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
