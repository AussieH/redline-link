// Redline Link: receives Forza Horizon 6's Data Out packets, writes %APPDATA%\RedlineDeck\state.json for an iCUE widget
// (the same file and schema the Redline Deck plugin writes), serves the same state on a localhost WebSocket, and passes
// every packet on to up to four other ports. A tray icon; no window, no admin, no installer. README.md has the details.
//
//   RedlineLink.exe [--no-tray] [--port N] [--ws-port N]
// Settings: %APPDATA%\RedlineDeck\companion-settings.json { "port": 5605, "network": false, "forward": [], "wsPort": 5605 }
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RedlineLink;

public static class Program
{
    public static string Dir = "";
    public static int Port = 5605, WsPort = 5605, Pps;
    public static bool Network;
    public static readonly List<IPEndPoint> Targets = new();
    public static string LinkState = "starting";
    public static readonly Dash Dash = new();
    static readonly object Gate = new();
    static readonly Queue<long> Window = new();
    public static long Forwarded;
    public static volatile bool Quit;

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [STAThread]
    public static int Main(string[] args)
    {
        bool tray = !args.Contains("--no-tray");
        Dir = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedlineDeck");
        Directory.CreateDirectory(Dir);
        Mutex? single = null;
        if (tray) { single = new Mutex(true, @"Local\RedlineLink", out bool first); if (!first) return 1; }   // one copy at a time
        LoadSettings(args);
        Files.CleanLeftovers();
        // Bind first, then say "waiting": a packet sent the moment the file appears must find the socket there.
        UdpClient? udp = null;
        try { udp = new UdpClient(new IPEndPoint(Network ? IPAddress.Any : IPAddress.Loopback, Port)); LinkState = "listening"; }
        catch (SocketException) { LinkState = "busy"; }
        Files.WriteState(Dash.StateJson(Now(), Port, 0, LinkState));
        if (udp != null) new Thread(() => Receive(udp)) { IsBackground = true, Name = "udp" }.Start();
        new Thread(Tick) { IsBackground = true, Name = "tick" }.Start();
        WebSockets.Start(WsPort);
        if (tray) Tray.Run(); else while (!Quit) Thread.Sleep(200);
        Quit = true;
        Files.RemoveHeartbeat();
        return 0;
    }

    static void LoadSettings(string[] args)
    {
        var path = Path.Combine(Dir, "companion-settings.json");
        if (!File.Exists(path)) File.WriteAllText(path, "{\n  \"port\": 5605,\n  \"network\": false,\n  \"forward\": [],\n  \"wsPort\": 5605\n}\n");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv)) Port = Clean(pv);
            if (r.TryGetProperty("wsPort", out var w) && w.TryGetInt32(out var wv) && wv > 1023 && wv < 65536) WsPort = wv;
            if (r.TryGetProperty("network", out var n)) Network = n.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("forward", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var t in f.EnumerateArray()) if (Targets.Count < 4 && t.ValueKind == JsonValueKind.String && Target(t.GetString()!) is { } ep) Targets.Add(ep);
        }
        catch (Exception) { /* a broken file: the defaults */ }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var a)) Port = Clean(a);
            if (args[i] == "--ws-port" && int.TryParse(args[i + 1], out var b)) WsPort = b;
        }
    }
    static int Clean(int p) => p < 1024 || p > 65535 || (p >= 5200 && p <= 5300) ? 5605 : p;
    /// <summary>"5606" or "host:port"; never this program's own port on this PC (a loop).</summary>
    static IPEndPoint? Target(string text)
    {
        var t = text.Trim(); var host = "127.0.0.1"; var ps = t;
        int c = t.LastIndexOf(':');
        if (c > 0) { host = t[..c].Trim('[', ']'); ps = t[(c + 1)..]; }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        if (!int.TryParse(ps, out var port) || port < 1 || port > 65535 || !IPAddress.TryParse(host, out var ip)) return null;
        if (IPAddress.IsLoopback(ip) && port == Port) return null;
        return new IPEndPoint(ip, port);
    }

    static void Receive(UdpClient udp)
    {
        using var output = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!Quit)
        {
            byte[] buf;
            try { buf = udp.Receive(ref from); }
            catch (SocketException) { continue; }   // a reset from somewhere: keep listening
            foreach (var t in Targets) { try { output.Send(buf, buf.Length, t); Interlocked.Increment(ref Forwarded); } catch (SocketException) { } }
            var p = Packet.Parse(buf);
            if (p == null) continue;
            var now = Now();
            lock (Gate)
            {
                Dash.Push(p, now);
                Window.Enqueue(now);
            }
        }
    }

    static void Tick()
    {
        long last = 0, beat = 0; string lastState = "";
        while (!Quit)
        {
            Thread.Sleep(100);
            var now = Now();
            byte[] json; string st;
            lock (Gate)
            {
                while (Window.Count > 0 && now - Window.Peek() > 1000) Window.Dequeue();
                Pps = Window.Count;
                st = LinkState is "busy" ? "busy" : Dash.State(now);
                json = Dash.StateJson(now, Port, Pps, LinkState);
            }
            if (now - beat >= 2000) { beat = now; Files.Heartbeat(now); }
            bool due = st == "driving" ? now - last >= 100 : now - last >= 5000;
            if (due || st != lastState) { Files.WriteState(json); last = now; lastState = st; WebSockets.Broadcast(json); }
        }
    }

    public static string Status()
    {
        if (LinkState == "busy") return $"port {Port} is in use";
        var st = Dash.State(Now());
        return st == "driving" ? $"receiving {Pps}/s" : st == "paused" ? "paused or in menus" : $"waiting on port {Port}";
    }
}

/// <summary>state.json written whole and safely (a temp file renamed over it; if a reader such as iCUE holds the file, one
/// retry, then a rewrite in place), and the heartbeat companion.json that tells the Redline Deck plugin to stand back.</summary>
public static class Files
{
    static string StatePath => Path.Combine(Program.Dir, "state.json");
    public static string WriteState(byte[] json)
    {
        var tmp = StatePath + ".tmp";
        try { File.WriteAllBytes(tmp, json); } catch (IOException) { return "failed"; } catch (UnauthorizedAccessException) { return "failed"; }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // A plain rename over the old file (MoveFileEx with replace), not File.Replace: ReplaceFile leaves
            // state.json~RF*.TMP files behind when a reader such as iCUE holds the file at the wrong moment.
            try { File.Move(tmp, StatePath, overwrite: true); return "renamed"; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (attempt == 0) Thread.Sleep(5);
        }
        try
        {
            using var fs = new FileStream(StatePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(json, 0, json.Length);
            return "in place";
        }
        catch (IOException) { return "held"; } catch (UnauthorizedAccessException) { return "held"; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public static void Heartbeat(long now)
    {
        try { File.WriteAllText(Path.Combine(Program.Dir, "companion.json"), $"{{\"app\":\"Redline Link\",\"pid\":{Environment.ProcessId},\"at\":{now},\"port\":{Program.Port}}}"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    /// <summary>Removes the state.json~RF*.TMP files Redline Link 0.1.0 could leave behind (its File.Replace), and only those.</summary>
    public static void CleanLeftovers()
    {
        try { foreach (var f in Directory.GetFiles(Program.Dir, "state.json~RF*.TMP")) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static void RemoveHeartbeat() { try { File.Delete(Path.Combine(Program.Dir, "companion.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
