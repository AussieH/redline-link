// The localhost WebSocket (and a plain GET /state.json) for an iCUE widget: iCUE widgets can fetch and open WebSockets
// to localhost with a url permission (tested on the XENEON EDGE), but cannot receive UDP. Both 127.0.0.1 and ::1, since
// iCUE's fetch tries ::1 first. Only local pages: the Host must be this PC, and the Origin absent, "null" or file://
// (what iCUE sends), so a web page in a browser cannot read the stream. Text frames of state.json, as written.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RedlineLink;

public static class WebSockets
{
    static readonly List<NetworkStream> Clients = new();
    public static int Count { get { lock (Clients) return Clients.Count; } }

    public static void Start(int port)
    {
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var l = new TcpListener(ip, port);
                l.Start();
                new Thread(() => Accept(l)) { IsBackground = true, Name = "ws" }.Start();
            }
            catch (SocketException) { /* the port is taken, or no IPv6: the other one may still work */ }
        }
    }

    static void Accept(TcpListener l)
    {
        while (!Program.Quit)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); } catch (SocketException) { continue; }
            new Thread(() => Serve(c)) { IsBackground = true }.Start();
        }
    }

    static void Serve(TcpClient c)
    {
        var s = c.GetStream();
        try
        {
            c.ReceiveTimeout = 5000;
            var head = ReadHead(s);
            if (head == null) { c.Close(); return; }
            var lines = head.Split("\r\n");
            var req = lines[0].Split(' ');
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1)) { int i = line.IndexOf(':'); if (i > 0) h[line[..i].Trim()] = line[(i + 1)..].Trim(); }
            var host = h.GetValueOrDefault("Host", "");
            var hostName = host.StartsWith("[") ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
            var origin = h.GetValueOrDefault("Origin", "");
            bool local = hostName is "localhost" or "127.0.0.1" or "[::1]";
            bool okOrigin = origin == "" || origin == "null" || origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
            if (req.Length < 2 || req[0] != "GET" || !local || !okOrigin) { Reply(s, "403 Forbidden", "text/plain", "forbidden"u8.ToArray()); c.Close(); return; }
            if (!h.TryGetValue("Sec-WebSocket-Key", out var key))
            {
                if (req[1] is "/state.json" or "/state" or "/") Reply(s, "200 OK", "application/json", Program.Dash.StateJson(Program.Now(), Program.Port, Program.Pps, Program.LinkState));
                else Reply(s, "404 Not Found", "text/plain", "not found"u8.ToArray());
                c.Close(); return;
            }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var hello = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
            s.Write(hello);
            c.ReceiveTimeout = 0;
            lock (Clients) Clients.Add(s);
            Send(s, Program.Dash.StateJson(Program.Now(), Program.Port, Program.Pps, Program.LinkState));
            // Read and drop what the widget sends; a close frame or the end of the stream ends it.
            var buf = new byte[512];
            while (!Program.Quit)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0 || (buf[0] & 0x0F) == 0x8) break;
            }
        }
        catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        lock (Clients) Clients.Remove(s);
        try { c.Close(); } catch (SocketException) { }
    }

    static string? ReadHead(NetworkStream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (sb.Length < 8192)
        {
            if (s.Read(one, 0, 1) <= 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') return sb.ToString();
        }
        return null;
    }

    static void Reply(NetworkStream s, string status, string type, byte[] body)
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        s.Write(Encoding.ASCII.GetBytes(head)); s.Write(body);
    }

    static void Send(NetworkStream s, byte[] payload)
    {
        var len = payload.Length;
        byte[] head = len < 126 ? new byte[] { 0x81, (byte)len } : len < 65536 ? new byte[] { 0x81, 126, (byte)(len >> 8), (byte)len } : throw new InvalidOperationException("too big");
        lock (s) { s.Write(head); s.Write(payload); }
    }

    public static void Broadcast(byte[] json)
    {
        NetworkStream[] all;
        lock (Clients) all = Clients.ToArray();
        foreach (var s in all) { try { Send(s, json); } catch (Exception) { lock (Clients) Clients.Remove(s); } }
    }
}
