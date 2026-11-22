// DroverSwitch Telegram relay (Cloudflare Worker).
// Protocol = Flowseal/tg-ws-proxy "--cfproxy-worker-domain": WebSocket at /apiws?dst=<DC IP>&dc=<n>,
// the worker opens a raw TCP connection to dst:443 and pipes bytes both ways.
// Not an open relay: dst must be an IPv4 address inside Telegram's official ranges
// (https://core.telegram.org/resources/cidr.txt), port is always 443.
import { connect } from "cloudflare:sockets";

const TELEGRAM_CIDRS = [
  "91.105.192.0/23",
  "91.108.4.0/22",
  "91.108.8.0/22",
  "91.108.12.0/22",
  "91.108.16.0/22",
  "91.108.20.0/22",
  "91.108.56.0/22",
  "149.154.160.0/20",
  "185.76.151.0/24",
];

function ipToInt(ip) {
  const parts = ip.split(".");
  if (parts.length !== 4) return null;
  let n = 0;
  for (const p of parts) {
    if (!/^\d{1,3}$/.test(p) || +p > 255) return null;
    n = n * 256 + +p;
  }
  return n >>> 0;
}

function isTelegramIp(ip) {
  const v = ipToInt(ip);
  if (v === null) return false;
  return TELEGRAM_CIDRS.some((c) => {
    const [base, bits] = c.split("/");
    const mask = (0xffffffff << (32 - +bits)) >>> 0;
    return ((v & mask) >>> 0) === ((ipToInt(base) & mask) >>> 0);
  });
}

async function toBytes(data) {
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (typeof data === "string") return new TextEncoder().encode(data);
  if (data && typeof data.arrayBuffer === "function") return new Uint8Array(await data.arrayBuffer());
  return new Uint8Array();
}

export default {
  async fetch(request) {
    if ((request.headers.get("Upgrade") || "").toLowerCase() !== "websocket") {
      return new Response("Expected websocket", { status: 426 });
    }
    const url = new URL(request.url);
    if (url.pathname !== "/apiws") return new Response("Not found", { status: 404 });
    const dst = url.searchParams.get("dst") || "";
    if (!isTelegramIp(dst)) return new Response("dst not allowed", { status: 403 });

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    server.accept();

    const socket = connect({ hostname: dst, port: 443 });
    const tcpReader = socket.readable.getReader();
    const tcpWriter = socket.writable.getWriter();

    server.addEventListener("message", async (event) => {
      try {
        await tcpWriter.write(await toBytes(event.data));
      } catch {
        try { server.close(1011, "tcp write failed"); } catch {}
      }
    });
    server.addEventListener("close", async () => {
      try { await tcpWriter.close(); } catch {}
      try { socket.close(); } catch {}
    });

    (async () => {
      try {
        while (true) {
          const { value, done } = await tcpReader.read();
          if (done) break;
          if (value) server.send(value);
        }
      } catch {
      } finally {
        try { server.close(); } catch {}
        try { tcpReader.releaseLock(); } catch {}
        try { socket.close(); } catch {}
      }
    })();

    return new Response(null, { status: 101, webSocket: client });
  },
};
