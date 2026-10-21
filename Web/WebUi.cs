using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace MicFuse;

public sealed class WebOptions
{
    public int Port = 8765;
    public bool OpenBrowser = true;
}

/// <summary>
/// Tiny HTTP/1.1 server on a raw socket. HttpListener goes through http.sys and needs a URL ACL
/// on non-admin accounts; TcpListener on 127.0.0.1 does not.
/// </summary>
public sealed class WebUi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        IncludeFields = true,
    };

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    private readonly AudioEngine _engine;
    private readonly WebOptions _options;
    private readonly Settings _settings;
    private TcpListener? _listener;
    private volatile bool _sawUiClient;

    public WebUi(AudioEngine engine, WebOptions options, Settings settings)
    {
        _engine = engine;
        _options = options;
        _settings = settings;
    }

    public string? Url { get; private set; }

    public async Task RunAsync(CancellationToken token)
    {
        int port = _options.Port;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                break;
            }
            catch (SocketException)
            {
                _listener = null;
                port++;
            }
        }
        if (_listener is null) throw new InvalidOperationException("no free port for the web UI");
        Url = $"http://127.0.0.1:{port}/";
        Log.Line($"web ui: {Url}");
        if (_options.OpenBrowser) _ = OpenUnlessUiConnectedAsync(token);

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    /// <summary>
    /// A tab left open from the previous run keeps polling while the server is down, so it reaches
    /// the new instance within a second; give it a moment before opening a duplicate. Only visible
    /// tabs qualify: a hidden tab stops all of its traffic by design and cannot be detected.
    /// </summary>
    private async Task OpenUnlessUiConnectedAsync(CancellationToken token)
    {
        for (int i = 0; i < 20; i++)
        {
            if (_sawUiClient)
            {
                Log.Line("browser tab already open; not launching another");
                return;
            }
            try { await Task.Delay(100, token); }
            catch (OperationCanceledException) { return; }
        }
        if (_sawUiClient) return;
        OpenPage();
    }

    public void OpenPage()
    {
        if (Url is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            Log.Line("opened the web ui in the default browser");
        }
        catch (Exception ex)
        {
            Log.Line("open browser failed: " + ex.Message);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequest(stream);
                if (request is null) return;
                var (method, path, body) = request.Value;
                // Only the UI asks for these; /api/state is skipped so scripts and probes don't count.
                if (method == "GET" && (path == "/" || path == "/api/events" || path == "/api/ping"))
                    _sawUiClient = true;
                if (method == "GET" && path == "/api/events")
                {
                    await StreamEvents(stream);
                    return;
                }
                var (status, contentType, payload) = await Route(method, path, body);
                var head = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.Line("http error: " + ex.Message);
            }
        }
    }

    /// <summary>Server-sent events: pushes a state snapshot at ~33 Hz so the meters update continuously;
    /// the client only polls when an EventSource could not be created.</summary>
    private async Task StreamEvents(NetworkStream stream)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n" +
            "Connection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.FlushAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
        try
        {
            while (true)
            {
                string json = JsonSerializer.Serialize(_engine.Snapshot(), options);
                byte[] payload = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                await Task.Delay(30);
            }
        }
        catch (Exception)
        {
            // client closed the EventSource
        }
    }

    private static async Task<(string Method, string Path, string Body)?> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);
            var arr = buffer.GetBuffer();
            headerEnd = IndexOf(arr, (int)buffer.Length, "\r\n\r\n"u8);
            if (buffer.Length > 64 * 1024) return null;
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        int contentLength = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);
        }
        int bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        int already = (int)buffer.Length - bodyStart;
        if (already > 0) body.Write(buffer.GetBuffer(), bodyStart, already);
        while (body.Length < contentLength)
        {
            int read = await stream.ReadAsync(chunk);
            if (read <= 0) break;
            body.Write(chunk, 0, read);
        }
        return (parts[0], parts[1], Encoding.UTF8.GetString(body.ToArray(), 0, Math.Min((int)body.Length, contentLength)));
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private async Task<(string Status, string ContentType, byte[] Payload)> Route(string method, string path, string body)
    {
        try
        {
            if (method == "GET" && path == "/")
                return ("200 OK", "text/html; charset=utf-8", LoadUi());

            if (method == "GET" && path == "/api/state") return Json200(_engine.Snapshot());
            if (method == "GET" && path == "/api/ping") return Json200(new { ok = true });
            if (method == "GET" && path == "/api/processes") return Json200(ListAudioProcesses());
            if (method == "GET" && path == "/api/devices") return Json200(ListDevices());

            if (method == "GET" && path.StartsWith("/api/icon", StringComparison.Ordinal))
            {
                int q = path.IndexOf("pid=", StringComparison.Ordinal);
                int pid = 0;
                if (q > 0) int.TryParse(path[(q + 4)..].Split('&')[0], out pid);
                return ("200 OK", "image/png", IconCache.ForPid(pid));
            }

            if (method == "POST" && path == "/api/engine/start")
            {
                var req = JsonSerializer.Deserialize<OutputRequest>(body, ReadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.OutputDeviceId)) return JsonError(400, "outputDeviceId required");
                await _engine.Start(req.OutputDeviceId);
                return Json200(new { ok = true, error = _engine.Snapshot().Error });
            }

            if (method == "POST" && path == "/api/engine/restart")
            {
                var req = JsonSerializer.Deserialize<OutputRequest>(body, ReadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.OutputDeviceId)) return JsonError(400, "outputDeviceId required");
                try
                {
                    await _engine.Restart(req.OutputDeviceId);
                    return Json200(new { ok = true, error = _engine.Snapshot().Error });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/sources/add")
            {
                var req = JsonSerializer.Deserialize<SourceAddRequest>(body, ReadJson);
                if (req is null || req.Pid <= 0) return JsonError(400, "pid required");
                try
                {
                    int id = await _engine.AddSource(req.Pid);
                    return Json200(new { ok = true, id });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/sources/remove")
            {
                var req = JsonSerializer.Deserialize<IdRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "id required");
                await _engine.RemoveSource(req.Id);
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/sources/gain")
            {
                var req = JsonSerializer.Deserialize<GainRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "bad body");
                _engine.SetSourceGain(req.Id, req.Gain);
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/mic")
            {
                var req = JsonSerializer.Deserialize<MicRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "bad body");
                try
                {
                    await _engine.SetMic(req.DeviceId ?? "", req.Gain);
                    _settings.SetMic(req.DeviceId ?? "", req.Gain);
                    return Json200(new { ok = true });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/output")
            {
                var req = JsonSerializer.Deserialize<OutputChangeRequest>(body, ReadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.DeviceId)) return JsonError(400, "deviceId required");
                try
                {
                    await _engine.SetOutput(req.DeviceId);
                    return Json200(new { ok = true });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            return JsonError(404, "not found");
        }
        catch (Exception ex)
        {
            return JsonError(500, ex.Message);
        }
    }

    private static (string, string, byte[]) Json200(object value)
        => ("200 OK", "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static (string, string, byte[]) JsonError(int code, string message)
        => ($"{code} {(code == 404 ? "Not Found" : "Error")}", "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(new { error = message }, Json));

    private static byte[] LoadUi()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html");
        if (stream is null) return Encoding.UTF8.GetBytes("<h1>ui.html missing</h1>");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed class OutputRequest { public string OutputDeviceId { get; set; } = ""; }
    private sealed class SourceAddRequest { public int Pid { get; set; } }
    private sealed class IdRequest { public int Id { get; set; } }
    private sealed class GainRequest { public int Id { get; set; } public float Gain { get; set; } = 1f; }
    private sealed class MicRequest { public string? DeviceId { get; set; } = ""; public float Gain { get; set; } = 1f; }
    private sealed class OutputChangeRequest { public string? DeviceId { get; set; } = ""; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>Alt-tab style filter: an IME host or a cloaked UWP window is "visible" to IsWindowVisible
    /// but is not a window the user can pick.</summary>
    private static bool IsUserFacingWindow(IntPtr hWnd)
    {
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (!GetWindowRect(hWnd, out var rect) || rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return false;
        return true;
    }

    private sealed class ProcEntry
    {
        public int Pid;
        public string Name = "";
        public string Title = "";
        public float Peak;
        public bool HasSession;
        public bool HasWindow;
    }

    private static object ListAudioProcesses()
    {
        var map = new Dictionary<int, ProcEntry>();
        ProcEntry Get(int pid)
        {
            if (!map.TryGetValue(pid, out var e))
            {
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; } catch { name = "(unknown)"; }
                e = new ProcEntry { Pid = pid, Name = name };
                map[pid] = e;
            }
            return e;
        }

        // Sessions are kept alive across a few sampling rounds: a single instantaneous peak is noise
        // at a 2 s poll, but the maximum over ~75 ms reads as "this app is playing" without needing a
        // background sampler.
        var heldSessions = new List<AudioSessionControl>();
        var heldManagers = new List<AudioSessionManager>();
        var heldDevices = new List<MMDevice>();
        var heldCollection = default(MMDeviceCollection);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            heldCollection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < heldCollection.Count; d++)
            {
                var device = heldCollection[d];
                heldDevices.Add(device);
                try
                {
                    var manager = device.AudioSessionManager;
                    heldManagers.Add(manager);
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        heldSessions.Add(session);
                        try
                        {
                            int pid = unchecked((int)session.GetProcessID);
                            if (pid <= 0) continue;
                            Get(pid).HasSession = true;
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }

            for (int round = 0; round < 4; round++)
            {
                foreach (var session in heldSessions)
                {
                    try
                    {
                        int pid = unchecked((int)session.GetProcessID);
                        if (pid <= 0 || !map.TryGetValue(pid, out var e)) continue;
                        float peak = session.AudioMeterInformation?.MasterPeakValue ?? 0f;
                        if (peak > e.Peak) e.Peak = peak;
                    }
                    catch (Exception) { }
                }
                if (round < 3) Thread.Sleep(25);
            }
        }
        finally
        {
            foreach (var s in heldSessions) { try { s.Dispose(); } catch { } }
            foreach (var m in heldManagers) { try { m.Dispose(); } catch { } }
            foreach (var d in heldDevices) { try { d.Dispose(); } catch { } }
            try { heldCollection?.Dispose(); } catch { }
        }

        // Everything with a visible top-level window, so apps that are open but silent are selectable too.
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (!IsUserFacingWindow(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var e = Get((int)pid);
                if (sb.Length > e.Title.Length) e.Title = sb.ToString();
                e.HasWindow = true;
                IconCache.SetWindow((int)pid, hWnd);
            }
            catch (Exception) { }
            return true;
        }, IntPtr.Zero);

        int self = Environment.ProcessId;
        return map.Values
            .Where(e => e.Pid != self && e.Pid != 0 && e.Pid != 4)
            .Where(e => e.HasWindow || e.Peak > 0.0005f)
            .OrderByDescending(e => e.HasSession)
            .ThenByDescending(e => e.Peak)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new { pid = e.Pid, name = e.Name, title = e.Title, peak = e.Peak })
            .ToList();
    }

    private static object ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string defaultMic = enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia, out var dm) ? dm.FriendlyName : "";
        if (dm is not null) dm.Dispose();
        string defaultOut = enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var dout) ? dout.FriendlyName : "";
        if (dout is not null) dout.Dispose();

        var mics = new List<object>();
        var micDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        for (int i = 0; i < micDevices.Count; i++)
        {
            var d = micDevices[i];
            mics.Add(new { id = d.ID, name = d.FriendlyName, isDefault = d.FriendlyName == defaultMic });
            d.Dispose();
        }
        micDevices.Dispose();

        var outs = new List<object>();
        var outDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < outDevices.Count; i++)
        {
            var d = outDevices[i];
            outs.Add(new { id = d.ID, name = d.FriendlyName, isDefault = d.FriendlyName == defaultOut });
            d.Dispose();
        }
        outDevices.Dispose();

        return new { mics, outputs = outs };
    }
}
