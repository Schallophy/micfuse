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
            if (method == "GET" && path == "/api/devices") return Json200(ListDevices());

            if (method == "GET" && path.StartsWith("/api/icon", StringComparison.Ordinal))
            {
                int q = path.IndexOf("pid=", StringComparison.Ordinal);
                int pid = 0;
                if (q > 0) int.TryParse(path[(q + 4)..].Split('&')[0], out pid);
                return ("200 OK", "image/png", IconCache.ForPid(pid));
            }

            if (method == "POST" && path == "/api/lines/add")
            {
                var req = JsonSerializer.Deserialize<DeviceRequest>(body, ReadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.DeviceId)) return JsonError(400, "deviceId required");
                try
                {
                    int lineId = await _engine.AddLine(req.DeviceId);
                    PersistTopology();
                    return Json200(new { ok = true, lineId });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/lines/remove")
            {
                var req = JsonSerializer.Deserialize<LineRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "lineId required");
                await _engine.RemoveLine(req.LineId);
                PersistTopology();
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/lines/device")
            {
                var req = JsonSerializer.Deserialize<LineDeviceRequest>(body, ReadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.DeviceId)) return JsonError(400, "deviceId required");
                try
                {
                    await _engine.SetLineDevice(req.LineId, req.DeviceId);
                    PersistTopology();
                    return Json200(new { ok = true });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/lines/restart")
            {
                var req = JsonSerializer.Deserialize<LineRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "lineId required");
                try { await _engine.RestartLine(req.LineId); }
                catch (Exception ex) { return JsonError(400, ex.Message); }
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/engine/restart")
            {
                await _engine.RestartAllLines();
                return Json200(new { ok = true, error = _engine.Snapshot().Error });
            }

            if (method == "POST" && path == "/api/sources/add")
            {
                var req = JsonSerializer.Deserialize<SourceAddRequest>(body, ReadJson);
                if (req is null || req.Pid <= 0) return JsonError(400, "pid required");
                try
                {
                    int id = await _engine.AddAppSource(req.Pid, req.Title ?? "");
                    PersistTopology();
                    return Json200(new { ok = true, id });
                }
                catch (Exception ex) { return JsonError(400, ex.Message); }
            }

            if (method == "POST" && path == "/api/sources/remove")
            {
                var req = JsonSerializer.Deserialize<SourceIdRequest>(body, ReadJson);
                if (req is null || req.Id <= 0) return JsonError(400, "id required");
                await _engine.RemoveAppSource(req.Id);
                PersistTopology();
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/previews/start")
            {
                var rows = ListWindowCandidates();
                await _engine.StartPreviews(rows.Select(r => (r.Pid, r.Name, r.Title)));
                return Json200(new { rows });
            }

            if (method == "POST" && path == "/api/previews/stop")
            {
                await _engine.StopPreviews();
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/routes/app")
            {
                var req = JsonSerializer.Deserialize<AppRouteRequest>(body, ReadJson);
                if (req is null || req.Id <= 0) return JsonError(400, "id required");
                try { await _engine.SetAppRoute(req.Id, req.LineId, req.On); }
                catch (Exception ex) { return JsonError(400, ex.Message); }
                PersistTopology();
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/routes/mic")
            {
                var req = JsonSerializer.Deserialize<MicRouteRequest>(body, ReadJson);
                if (req is null) return JsonError(400, "lineId required");
                try { await _engine.SetMicRoute(req.LineId, req.On); }
                catch (Exception ex) { return JsonError(400, ex.Message); }
                PersistTopology();
                return Json200(new { ok = true });
            }

            if (method == "POST" && path == "/api/sources/gain")
            {
                var req = JsonSerializer.Deserialize<GainRequest>(body, ReadJson);
                if (req is null || req.Id <= 0) return JsonError(400, "bad body");
                _engine.SetSourceGain(req.Id, req.Gain);
                PersistTopology();
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

            return JsonError(404, "not found");
        }
        catch (Exception ex)
        {
            return JsonError(500, ex.Message);
        }
    }

    /// <summary>Lines, the microphone's routes and the added sources are the topology worth keeping;
    /// nothing here stores a process id, because it means nothing after a reboot.</summary>
    private void PersistTopology()
    {
        var status = _engine.Snapshot();
        _settings.SetLines(status.Lines.Select(l => (l.Id, l.DeviceId)));
        _settings.SetMicRoutes(status.MicRoutes.Select(r => r.LineId));
        _settings.SetSources(status.Sources.Select(s =>
            (s.Name, s.Path, s.Title, s.Gain, s.Lines)));
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

    private sealed class DeviceRequest { public string? DeviceId { get; set; } = ""; }
    private sealed class LineRequest { public int LineId { get; set; } }
    private sealed class LineDeviceRequest { public int LineId { get; set; } public string? DeviceId { get; set; } = ""; }
    private sealed class AppRouteRequest { public int Id { get; set; } public int LineId { get; set; } public bool On { get; set; } }
    private sealed class MicRouteRequest { public int LineId { get; set; } public bool On { get; set; } }
    private sealed class SourceAddRequest { public int Pid { get; set; } public string? Title { get; set; } = ""; }
    private sealed class SourceIdRequest { public int Id { get; set; } }
    private sealed class GainRequest { public int Id { get; set; } public float Gain { get; set; } = 1f; }
    private sealed class MicRequest { public string? DeviceId { get; set; } = ""; public float Gain { get; set; } = 1f; }

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

    private sealed class WindowRow
    {
        public int Pid;
        public string Name = "";
        public string Title = "";
    }

    /// <summary>
    /// Every window the user could pick, alt-tab filtered. This runs only when the add list is opened;
    /// no audio metric is read here at all — the meters come from the engine's preview captures, which
    /// are real process loopbacks.
    /// </summary>
    private static List<WindowRow> ListWindowCandidates()
    {
        var map = new Dictionary<int, WindowRow>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (!IsUserFacingWindow(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;
                int id = (int)pid;
                if (id == Environment.ProcessId || id == 4) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.Length == 0) return true;
                if (!map.TryGetValue(id, out var row))
                {
                    string name;
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        name = process.ProcessName;
                    }
                    catch { name = "(unknown)"; }
                    row = new WindowRow { Pid = id, Name = name };
                    map[id] = row;
                }
                if (sb.Length > row.Title.Length) row.Title = sb.ToString();
                IconCache.SetWindow(id, hWnd);
            }
            catch (Exception) { }
            return true;
        }, IntPtr.Zero);

        return map.Values
            .OrderBy(r => string.IsNullOrWhiteSpace(r.Title) ? r.Name : r.Title, StringComparer.CurrentCultureIgnoreCase)
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
