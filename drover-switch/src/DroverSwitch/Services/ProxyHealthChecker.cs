using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DroverSwitch.Services;

/// <summary>
/// Tests whether Discord is actually reachable through a given proxy, by opening a real tunnel
/// to a Discord endpoint - an HTTP CONNECT for http proxies, a SOCKS5 handshake for socks5 ones,
/// or a plain TCP connect for "Direct". This is a connectivity probe only; no Discord traffic is sent.
/// </summary>
public static class ProxyHealthChecker
{
    private const string ProbeHost = "discord.com";
    private const int ProbePort = 443;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> IsReachableAsync(string proxyUrl, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            var proxy = ParsedProxy.Parse(proxyUrl);

            if (!proxy.IsSpecified)
                return await CheckDirectAsync(cts.Token);

            if (proxy.IsSocks5)
                return await CheckSocks5Async(proxy, cts.Token);

            // HTTP (and the unknown-protocol fallback, same as drover treats it) goes through CONNECT.
            return await CheckHttpConnectAsync(proxy, cts.Token);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckDirectAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ProbeHost, ProbePort, ct);
        return tcp.Connected;
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
        // "HTTP/1.1 200 Connection established" (or 200 OK) means the tunnel is open.
        var parts = statusLine.Split(' ', 3);
        return parts.Length >= 2 && parts[1].StartsWith('2');
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

        return true;
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
