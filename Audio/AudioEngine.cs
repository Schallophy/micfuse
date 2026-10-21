using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

/// <summary>One source on one line: the buffer and resampler state are per line, not per source.</summary>
public sealed class RouteInfo
{
    public int LineId;
    public double BufferedMs;
    /// <summary>Adaptive-resampling ratio deviation for this line's clock; 0 means reading 1:1.</summary>
    public int RatioPpm;
    public long StarvedFrames;
    public long IdleFrames;
    public long DroppedFrames;
}

public sealed class SourceInfo
{
    public int Id;
    /// <summary>The live process, or 0 while the source is only remembered and waiting for its app.</summary>
    public int Pid;
    public string Name = "";
    public string Title = "";
    public string Path = "";
    public float Gain = 1f;
    public float Level;
    public float Hold;
    public bool Alive = true;
    /// <summary>Lines the user wants this source on, connected or not — what gets persisted.</summary>
    public int[] Lines = Array.Empty<int>();
    public RouteInfo[] Routes = Array.Empty<RouteInfo>();
}

/// <summary>Meter for one app while the add-source list is open; only alive for that moment.</summary>
public sealed class PreviewInfo
{
    public int Pid;
    public float Level;
    public float Hold;
    public string Error = "";
}

/// <summary>
/// What a source is, in a form that survives a reboot: the executable path when we can read it, the
/// process name when we cannot. A process id is deliberately not part of it.
/// </summary>
public sealed class RouteIdentity
{
    public string Name = "";
    public string Path = "";
    /// <summary>Path wins; the name only breaks ties for processes whose module we cannot open. Separators
    /// are normalised so a hand-typed or round-tripped path still matches.</summary>
    public string Key => string.IsNullOrEmpty(Path)
        ? "n:" + Name.Trim().ToLowerInvariant()
        : "p:" + Path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}

public sealed class LineInfo
{
    public int Id;
    public string DeviceId = "";
    public string Name = "";
    public string State = "stopped";
    public string Error = "";
    public float MixLevel;
    public float MixHold;
    public long ClippedSamples;
    /// <summary>Blocks replaced with silence because this line's mix threw; should stay 0.</summary>
    public long MixBackstops;
    /// <summary>Deadline misses on this line alone: silence this endpoint had to be given.</summary>
    public long StarvedFrames;
    /// <summary>Frames sitting in this render endpoint's buffer right now (GetCurrentPadding).</summary>
    public double QueueMs;
    /// <summary>Latency this endpoint actually granted at Init time.</summary>
    public double RenderLatencyMs;
    public bool LowLatencyActive;
    public string LowLatencyReason = "";
    /// <summary>Feed ids routed here; <see cref="AudioEngine.MicFeedId"/> means the microphone.</summary>
    public int[] SourceIds = Array.Empty<int>();
}

public sealed class EngineStatus
{
    public bool Running;
    public string State = "stopped";
    /// <summary>Only the aggregate: set when every line is down. Per-line faults stay on their line.</summary>
    public string Error = "";
    public string MicId = "";
    public string MicName = "";
    public float MicGain = 1f;
    public float MicLevel;
    public float MicHold;
    public RouteInfo[] MicRoutes = Array.Empty<RouteInfo>();
    /// <summary>Sum of deadline misses across every source on every line.</summary>
    public long StarvedFrames;
    public LineInfo[] Lines = Array.Empty<LineInfo>();
    public SourceInfo[] Sources = Array.Empty<SourceInfo>();
    /// <summary>Non-empty only while the add-source list is open: one capture per candidate app.</summary>
    public PreviewInfo[] Previews = Array.Empty<PreviewInfo>();
}

/// <summary>
/// OBS-like engine with one bus per output line. A capture (an app's process loopback, or the
/// microphone) produces one shared ring; every line that routes it reads through its own cursor and
/// its own resampler, because each endpoint runs on its own clock. Sources, lines and routes are all
/// edited live while audio keeps flowing on the lines that were not touched.
///
/// A source exists from the moment the user adds it, whether or not any line is carrying it; a process
/// id only lives inside <see cref="Feed"/>, so a source that is added but not running is still a row
/// with its identity, gain and remembered lines.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int RingCapacityFrames = SampleRate / 2;

    /// <summary>The microphone is a permanent feed with a fixed id; its routes are what the user edits.</summary>
    public const int MicFeedId = 0;

    /// <summary>The live half of a source: a process loopback writing into a shared ring.</summary>
    private sealed class Feed
    {
        public int Pid;
        public SourceRing Ring = null!;
        public WasapiRecorder? Recorder;
        public Process? Process;
        public bool Alive = true;
        public bool HandleCheck = true;
    }

    /// <summary>The user's source: identity, gain and lines outlive any one process.</summary>
    private sealed class SourceWant
    {
        public int Id;
        public RouteIdentity Identity = new();
        public string Title = "";
        public float Gain = 1f;
        public MeterState Meter = new();
        public Feed? Live;
        public readonly List<int> Lines = new();
    }

    /// <summary>A throwaway capture used only to paint meters while the add list is open.</summary>
    private sealed class Preview
    {
        public int Pid;
        public string Name = "";
        public string Title = "";
        public MeterState Meter = new();
        public WasapiRecorder? Recorder;
        public string Error = "";
    }

    private sealed class Line
    {
        public int Id;
        public string DeviceId = "";
        public string Name = "";
        public MMDevice? Device;
        public MMDeviceCollection? Devices;
        public Mixer? Bus;
        public MixerProvider? Provider;
        public WasapiPlayer? Player;
        public string State = "starting";
        public string Error = "";
        public volatile bool Stopping;
        public double RenderLatencyMs;
        public bool LowLatencyActive;
        public string LowLatencyReason = "";
    }

    private readonly SemaphoreSlim _ops = new(1, 1);
    private readonly object _gate = new();
    private readonly List<SourceWant> _wants = new();
    private readonly List<Line> _lines = new();
    private readonly Dictionary<int, Preview> _previews = new();
    private DateTime _previewLease;
    private readonly Feed _mic;
    private readonly MeterState _micMeter = new();
    private readonly List<int> _micLines = new();
    private int _nextSourceId = 1;
    private int _nextLineId = 1;

    private MMDevice? _micDevice;
    private MMDeviceCollection? _micDevices;
    private WasapiRecorder? _micRecorder;
    private string _micName = "";
    private string _micId = "";
    private float _micGain = 1f;
    private string _error = "";
    private CancellationTokenSource? _cts;
    private Task? _monitor;

    /// <summary>The session log file is the only sink now that the UI panel is gone.</summary>
    private void Log(string message)
    {
        global::MicFuse.Log.Line(message);
    }

    public AudioEngine()
    {
        _mic = new Feed
        {
            Pid = Environment.ProcessId,
            Ring = new SourceRing(RingCapacityFrames, Channels, SampleRate),
        };
    }

    public EngineStatus Snapshot()
    {
        var status = new EngineStatus
        {
            MicId = _micId,
            MicName = _micName,
            MicGain = _micGain,
            MicLevel = _micMeter.Level,
            MicHold = _micMeter.Hold,
        };

        Line[] lines;
        var wants = new List<(int Id, RouteIdentity Identity, string Title, float Gain, float Level, float Hold,
                              int Pid, bool Alive, int[] Lines)>();
        PreviewInfo[] previews;
        lock (_gate)
        {
            lines = _lines.ToArray();
            foreach (var w in _wants)
                wants.Add((w.Id, w.Identity, w.Title, w.Gain, w.Meter.Level, w.Meter.Hold,
                           w.Live?.Pid ?? 0, w.Live is { Alive: true }, w.Lines.ToArray()));
            previews = _previews.Values
                .Select(p => new PreviewInfo { Pid = p.Pid, Level = p.Meter.Level, Hold = p.Meter.Hold, Error = p.Error })
                .ToArray();
        }

        // One pass over the buses builds the per-source route table; the UI keys rows by source id and
        // pools by line, so both views come from the same snapshot.
        var routes = new Dictionary<int, List<RouteInfo>>();
        long starved = 0;
        int running = 0, faulted = 0;
        var lineInfos = new List<LineInfo>(lines.Length);
        foreach (var line in lines)
        {
            var bus = line.Bus;
            var info = new LineInfo
            {
                Id = line.Id,
                DeviceId = line.DeviceId,
                Name = line.Name,
                State = line.State,
                Error = line.Error,
                QueueMs = QueueDepthMs(line),
                RenderLatencyMs = line.RenderLatencyMs,
                LowLatencyActive = line.LowLatencyActive,
                LowLatencyReason = line.LowLatencyReason,
            };
            if (bus is not null)
            {
                info.MixLevel = bus.MixPeak;
                info.MixHold = bus.MixHold;
                info.ClippedSamples = bus.ClippedSamples;
                info.MixBackstops = bus.BackstopErrors;
                info.SourceIds = bus.RoutedIdsSnapshot();
                foreach (int feedId in info.SourceIds)
                {
                    var st = bus.Stats(feedId);
                    starved += st.StarvedFrames;
                    info.StarvedFrames += st.StarvedFrames;
                    if (!routes.TryGetValue(feedId, out var list))
                        routes[feedId] = list = new List<RouteInfo>();
                    list.Add(new RouteInfo
                    {
                        LineId = line.Id,
                        BufferedMs = st.BufferedMs,
                        RatioPpm = st.RatioPpm,
                        StarvedFrames = st.StarvedFrames,
                        IdleFrames = st.IdleFrames,
                        DroppedFrames = st.DroppedFrames,
                    });
                }
            }
            if (line.State == "running") running++;
            if (line.State == "faulted") faulted++;
            lineInfos.Add(info);
        }

        status.Lines = lineInfos.ToArray();
        status.StarvedFrames = starved;
        status.MicRoutes = routes.TryGetValue(MicFeedId, out var micList) ? micList.ToArray() : Array.Empty<RouteInfo>();

        status.Sources = wants.Select(w => new SourceInfo
        {
            Id = w.Id,
            Pid = w.Pid,
            Name = w.Identity.Name,
            Title = w.Title,
            Path = w.Identity.Path,
            Gain = w.Gain,
            Level = w.Level,
            Hold = w.Hold,
            Alive = w.Alive,
            Lines = w.Lines,
            Routes = routes.TryGetValue(w.Id, out var list) ? list.ToArray() : Array.Empty<RouteInfo>(),
        }).ToArray();
        status.Previews = previews;

        status.Running = running > 0;
        status.State = lines.Length == 0 ? "stopped"
                     : running > 0 ? "running"
                     : faulted > 0 ? "faulted" : "stopped";
        if (running == 0 && faulted > 0)
        {
            var first = lineInfos.FirstOrDefault(l => l.State == "faulted");
            _error = first?.Error ?? "";
        }
        else if (lines.Length == 0) _error = "";
        status.Error = _error;
        return status;
    }

    /// <summary>What the render endpoint still holds; read live because it is the part of the
    /// pipeline that actually varies while playing.</summary>
    private static double QueueDepthMs(Line line)
    {
        try { return line.Player?.CurrentLatency.TotalMilliseconds ?? 0; }
        catch (Exception) { return 0; }
    }

    private void CapturePlayerTiming(Line line)
    {
        var player = line.Player;
        if (player is null)
        {
            line.RenderLatencyMs = 0;
            line.LowLatencyActive = false;
            line.LowLatencyReason = "";
            return;
        }
        line.RenderLatencyMs = player.LatencyMilliseconds;
        line.LowLatencyActive = player.LowLatencyActive;
        line.LowLatencyReason = player.LowLatencyUnavailableReason ?? "";
        Log($"line {line.Id} '{line.Name}': granted={line.RenderLatencyMs:F1}ms lowLatency={line.LowLatencyActive}" +
            (string.IsNullOrEmpty(line.LowLatencyReason) ? "" : $" reason={line.LowLatencyReason}"));
    }

    /// <summary>
    /// Best effort: a declined low-latency request must fall back to standard shared mode, not
    /// abort the line; what was actually granted is read back via CapturePlayerTiming. The stop hook
    /// is attached before Init so a player that dies during startup is still seen.
    /// </summary>
    private WasapiPlayer BuildPlayer(Line line, MMDevice device)
    {
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency(false)
            .WithLatency(20)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        player.PlaybackStopped += (_, e) => OnPlaybackStopped(line, player, e.Exception);
        return player;
    }

    /// <summary>Detach before stopping so the stop we asked for cannot be reported as a fault.</summary>
    private void DetachPlayer(Line line)
    {
        var player = line.Player;
        line.Player = null;
        line.Stopping = true;
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
    }

    /// <summary>
    /// Puts a bus and a player on the line and starts it. Feeds are untouched: their rings keep being
    /// written by the capture callbacks, so re-attaching readers only re-primes this line's buffers.
    /// </summary>
    private void OpenLineBus(Line line, MMDevice device, MMDeviceCollection collection)
    {
        var bus = new Mixer(SampleRate, Channels, SampleRate * 10 / 1000, RingCapacityFrames, "L" + line.Id);
        lock (_gate)
        {
            foreach (var want in _wants)
                if (want.Lines.Contains(line.Id) && want.Live is { } live)
                    bus.AttachSource(live.Ring, want.Id, want.Gain);
            if (_micLines.Contains(line.Id)) bus.AttachSource(_mic.Ring, MicFeedId, _micGain);
        }
        var provider = new MixerProvider(bus, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels), null, SampleRate);
        var player = BuildPlayer(line, device);
        line.Bus = bus;
        line.Provider = provider;
        line.Player = player;
        player.Init(provider);
        line.Device = device;
        line.Devices = collection;
        line.DeviceId = device.ID;
        line.Name = device.FriendlyName;
        CapturePlayerTiming(line);
        player.Play();
        line.Stopping = false;
        line.State = "running";
        line.Error = "";
    }

    /// <summary>Replaces only this line's player: the bus, its readers and every other line keep going,
    /// so a failed switch can be undone by attaching a player to the previous endpoint.</summary>
    private void SwapPlayer(Line line, MMDevice device, MMDeviceCollection collection)
    {
        var previousDevice = line.Device;
        var previousCollection = line.Devices;
        line.Stopping = true;
        DetachPlayer(line);
        OpenLineBus(line, device, collection);
        try { previousDevice?.Dispose(); } catch { }
        try { previousCollection?.Dispose(); } catch { }
    }

    private void CloseLineBus(Line line)
    {
        DetachPlayer(line);
        line.Bus?.ClearSources();
        line.Bus = null;
        line.Provider = null;
        try { line.Device?.Dispose(); } catch { }
        try { line.Devices?.Dispose(); } catch { }
        line.Device = null;
        line.Devices = null;
    }

    /// <summary>Runs on NAudio's playback thread: record the fault on this line, never touch the pipeline.</summary>
    private void OnPlaybackStopped(Line line, WasapiPlayer player, Exception? ex)
    {
        // A player that was already stopped or replaced can raise this late; only the live one counts.
        if (line.Stopping || line.State != "running" || !ReferenceEquals(player, line.Player)) return;
        line.Error = ex is null ? "播放已停止（输出设备被移除？）" : Describe(ex);
        line.State = "faulted";
        Log($"line {line.Id} stopped: " + line.Error);
    }

    /// <summary>Adds an output line. Nothing is ever chosen for the user: the first call is the user's.</summary>
    public async Task<int> AddLine(string deviceId, int id = 0)
    {
        await _ops.WaitAsync();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var device = FindDevice(collection, deviceId)
                         ?? throw Fail(collection, "输出设备未找到: " + deviceId);
            var line = new Line { Id = id > 0 ? id : NextLineId() };
            lock (_gate) _nextLineId = Math.Max(_nextLineId, line.Id + 1);
            // The device is only ours to keep once the bus is up; OpenLineBus owns it from here.
            try
            {
                OpenLineBus(line, device, collection);
            }
            catch (Exception ex)
            {
                try { device.Dispose(); } catch { }
                try { collection.Dispose(); } catch { }
                throw new InvalidOperationException($"线路启动失败: {Describe(ex)}", ex);
            }
            lock (_gate) _lines.Add(line);
            EnsureMonitor();
            Log($"line {line.Id} added -> '{line.Name}'");
            return line.Id;
        }
        finally
        {
            _ops.Release();
        }
    }

    private static Exception Fail(MMDeviceCollection collection, string message)
    {
        try { collection.Dispose(); } catch { }
        return new InvalidOperationException(message);
    }

    public async Task RemoveLine(int lineId)
    {
        await _ops.WaitAsync();
        try
        {
            Line? line;
            lock (_gate) line = _lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return;
            lock (_gate) _lines.Remove(line);
            CloseLineBus(line);
            lock (_gate)
            {
                foreach (var want in _wants) want.Lines.Remove(lineId);
                _micLines.Remove(lineId);
            }
            Log($"line {lineId} removed ('{line.Name}')");
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Rebuilds one line: fresh readers, so this line's buffers re-prime. Every other line,
    /// every capture and every route definition survive untouched.</summary>
    public async Task RestartLine(int lineId)
    {
        await _ops.WaitAsync();
        try
        {
            Line? line;
            lock (_gate) line = _lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) throw new InvalidOperationException("线路不存在: " + lineId);
            string deviceId = line.DeviceId;
            string previousError = line.Error;
            CloseLineBus(line);
            line.State = "starting";
            line.Error = "";
            try
            {
                OpenLineWithDevice(line, deviceId);
                Log($"line {lineId} restarted -> '{line.Name}'");
            }
            catch (Exception ex)
            {
                line.State = "faulted";
                line.Error = Describe(ex);
                Log($"line {lineId} restart failed: {line.Error} (previous: {previousError})");
                throw new InvalidOperationException(line.Error, ex);
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    private void OpenLineWithDevice(Line line, string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var device = FindDevice(collection, deviceId)
                     ?? throw Fail(collection, "输出设备未找到: " + deviceId);
        try
        {
            OpenLineBus(line, device, collection);
        }
        catch
        {
            try { device.Dispose(); } catch { }
            try { collection.Dispose(); } catch { }
            throw;
        }
    }

    public async Task RestartAllLines()
    {
        foreach (int id in LineIds())
        {
            try { await RestartLine(id); }
            catch (Exception ex) { Log($"line {id} restart skipped: {ex.Message}"); }
        }
    }

    public async Task SetLineDevice(int lineId, string deviceId)
    {
        await _ops.WaitAsync();
        try
        {
            Line? line;
            lock (_gate) line = _lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) throw new InvalidOperationException("线路不存在: " + lineId);
            string previousId = line.DeviceId;
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var device = FindDevice(collection, deviceId)
                         ?? throw Fail(collection, "输出设备未找到: " + deviceId);
            try
            {
                SwapPlayer(line, device, collection);
                Log($"line {lineId} -> '{line.Name}'");
            }
            catch (Exception ex)
            {
                // The old player is gone but the bus is intact: put a player back on the previous
                // endpoint instead of tearing the line down and dropping every route on it.
                string reason = Describe(ex);
                Log($"line {lineId} device switch failed: {reason}");
                try { device.Dispose(); } catch { }
                try { collection.Dispose(); } catch { }
                DetachPlayer(line);
                try
                {
                    if (string.IsNullOrWhiteSpace(previousId))
                        throw new InvalidOperationException("没有可回滚的原输出");
                    OpenLineWithDevice(line, previousId);
                    Log($"line {lineId} rolled back -> '{line.Name}'");
                }
                catch (Exception rollbackEx)
                {
                    line.Error = "切换输出失败: " + reason + "; 回滚失败: " + Describe(rollbackEx);
                    line.State = "faulted";
                    Log(line.Error);
                }
                throw new InvalidOperationException(reason, ex);
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Adds an app as a source. It is captured from now on whether or not a line carries it,
    /// so its meter is live while the user decides where to route it.</summary>
    public async Task<int> AddAppSource(int pid, string title = "")
    {
        await _ops.WaitAsync();
        try
        {
            using var process = Process.GetProcessById(pid);
            var identity = new RouteIdentity { Name = process.ProcessName, Path = ModulePath(process) };
            SourceWant? want;
            lock (_gate)
            {
                // Reuse a remembered-but-not-running source with the same identity; two live processes
                // of one executable are two rows on purpose.
                want = _wants.FirstOrDefault(w => w.Identity.Key == identity.Key && w.Live is null)
                       ?? _wants.FirstOrDefault(w => w.Live?.Pid == pid);
                if (want is null)
                {
                    want = new SourceWant { Id = _nextSourceId++, Identity = identity };
                    _wants.Add(want);
                }
            }
            if (!string.IsNullOrWhiteSpace(title)) want.Title = title.Trim();
            else if (string.IsNullOrWhiteSpace(want.Title)) want.Title = identity.Name;
            await StopPreview(pid);
            if (want.Live is null) await AttachLiveLocked(want, pid);
            Log($"source added: id={want.Id} pid={pid} ({identity.Name})");
            EnsureMonitor();
            return want.Id;
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Removes the source from the list: it stops being captured and every line drops it.</summary>
    public async Task RemoveAppSource(int sourceId)
    {
        await _ops.WaitAsync();
        try
        {
            SourceWant? want;
            lock (_gate) want = _wants.FirstOrDefault(w => w.Id == sourceId);
            if (want is null) return;
            foreach (int lineId in want.Lines.ToArray())
                FindLine(lineId)?.Bus?.DetachSource(want.Id);
            lock (_gate) want.Lines.Clear();
            await DetachLiveLocked(want, log: false);
            lock (_gate) _wants.Remove(want);
            Log($"source removed: id={sourceId} ({want.Identity.Name})");
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Connects or disconnects a source from one line. The source itself is untouched, so
    /// turning off the last route no longer throws away the capture or the user's choice.</summary>
    public async Task SetAppRoute(int sourceId, int lineId, bool on)
    {
        await _ops.WaitAsync();
        try
        {
            var line = FindLine(lineId);
            SourceWant? want;
            lock (_gate) want = _wants.FirstOrDefault(w => w.Id == sourceId);
            if (want is null) throw new InvalidOperationException("音频源不存在: " + sourceId);
            if (on)
            {
                if (line is null) throw new InvalidOperationException("线路不存在: " + lineId);
                if (line.Bus is null) throw new InvalidOperationException("线路未就绪: " + line.Name);
                bool added;
                lock (_gate) { added = !want.Lines.Contains(lineId); if (added) want.Lines.Add(lineId); }
                if (!added) { line.Bus.SetGain(want.Id, want.Gain); return; }
                if (want.Live is { } live) line.Bus.AttachSource(live.Ring, want.Id, want.Gain);
                Log($"route on: {want.Identity.Name} -> line {lineId} '{line.Name}'");
                return;
            }
            bool removed;
            lock (_gate) removed = want.Lines.Remove(lineId);
            if (!removed) return;
            line?.Bus?.DetachSource(want.Id);
            Log($"route off: {want.Identity.Name} -> line {lineId}");
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Remembers a source that was in the list before this launch. The feed is attached only
    /// when its app is already running; otherwise the source stays visible and waiting.</summary>
    public async Task RestoreSource(RouteIdentity identity, string title, float gain, IEnumerable<int> lines)
    {
        if (string.IsNullOrWhiteSpace(identity?.Name)) return;
        await _ops.WaitAsync();
        try
        {
            var id = new RouteIdentity { Name = identity.Name.Trim(), Path = identity.Path?.Trim() ?? "" };
            SourceWant? want;
            lock (_gate)
            {
                want = _wants.FirstOrDefault(w => w.Identity.Key == id.Key && w.Live is null)
                       ?? _wants.FirstOrDefault(w => w.Identity.Key == id.Key);
                if (want is null)
                {
                    want = new SourceWant { Id = _nextSourceId++, Identity = id };
                    _wants.Add(want);
                }
                want.Title = string.IsNullOrWhiteSpace(title) ? want.Title : title.Trim();
                want.Gain = Math.Clamp(gain, 0f, 4f);
                foreach (int lineId in lines)
                    if (!want.Lines.Contains(lineId)) want.Lines.Add(lineId);
            }
            Log($"source restored: id={want.Id} '{id.Name}' lines=[{string.Join(',', want.Lines)}]");
            EnsureMonitor();
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>Tries to attach every remembered source that is not running yet; called at startup and
    /// then every couple of seconds by the monitor.</summary>
    public async Task ReconnectSources()
    {
        SourceWant[] pending;
        lock (_gate) pending = _wants.Where(w => w.Live is null).ToArray();
        foreach (var want in pending)
        {
            int pid = FindCandidatePid(want.Identity);
            if (pid <= 0) continue;
            string name = want.Identity.Name;
            await _ops.WaitAsync();
            try
            {
                if (want.Live is null)
                {
                    await AttachLiveLocked(want, pid);
                    Log($"source reconnected: '{name}' pid={pid}");
                }
            }
            catch (Exception ex)
            {
                Log($"reconnect '{name}' failed: {ex.Message}");
            }
            finally
            {
                _ops.Release();
            }
        }
    }

    private static int FindCandidatePid(RouteIdentity want)
    {
        if (string.IsNullOrWhiteSpace(want.Name)) return -1;
        var matches = new List<(int Pid, DateTime Start)>();
        foreach (var candidate in Process.GetProcessesByName(want.Name.Trim()))
        {
            using (candidate)
            {
                if (candidate.Id == Environment.ProcessId) continue;
                string path = ModulePath(candidate);
                // A stored path is a stronger claim than a name: only honour it when we can read both.
                if (!string.IsNullOrEmpty(want.Path) && !string.IsNullOrEmpty(path) &&
                    !string.Equals(path, want.Path, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime start = DateTime.MaxValue;
                try { start = candidate.StartTime; } catch (Exception) { }
                matches.Add((candidate.Id, start));
            }
        }
        if (matches.Count == 0) return -1;
        if (matches.Count == 1) return matches[0].Pid;
        // Several processes share one identity (browser children, launchers): the one holding an audio
        // session is the one actually making the sound the user routed.
        var signal = RenderSessionPids();
        var withSignal = matches.Where(m => signal.Contains(m.Pid)).ToList();
        return (withSignal.Count > 0 ? withSignal : matches).OrderBy(m => m.Start).First().Pid;
    }

    private static HashSet<int> RenderSessionPids()
    {
        var pids = new HashSet<int>();
        var held = new List<AudioSessionControl>();
        var devices = default(MMDeviceCollection);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < devices.Count; d++)
            {
                using var device = devices[d];
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int s = 0; s < sessions.Count; s++)
                    {
                        var session = sessions[s];
                        held.Add(session);
                        try { pids.Add(unchecked((int)session.GetProcessID)); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally
        {
            foreach (var session in held) { try { session.Dispose(); } catch { } }
            try { devices?.Dispose(); } catch { }
        }
        return pids;
    }

    public async Task SetMicRoute(int lineId, bool on)
    {
        await _ops.WaitAsync();
        try
        {
            var line = FindLine(lineId);
            if (on)
            {
                if (line is null) throw new InvalidOperationException("线路不存在: " + lineId);
                if (line.Bus is null) throw new InvalidOperationException("线路未就绪: " + line.Name);
                if (!_micLines.Contains(lineId))
                {
                    _micLines.Add(lineId);
                    line.Bus.AttachSource(_mic.Ring, MicFeedId, _micGain);
                    // A mic that was only selected, never opened, starts capturing as soon as it has
                    // somewhere to send.
                    if (_micDevice is not null) await OpenMicRecorderLocked(log: true);
                }
                else line.Bus.SetGain(MicFeedId, _micGain);
                Log($"route on: 麦克风 -> line {lineId} '{line.Name}'");
                return;
            }
            if (line is null) return;
            if (!_micLines.Remove(lineId)) return;
            line.Bus?.DetachSource(MicFeedId);
            Log($"route off: 麦克风 -> line {lineId}");
        }
        finally
        {
            _ops.Release();
        }
    }

    private Line? FindLine(int lineId)
    {
        lock (_gate) return _lines.FirstOrDefault(l => l.Id == lineId);
    }

    private int[] LineIds()
    {
        lock (_gate) return _lines.Select(l => l.Id).ToArray();
    }

    private SourceWant[] WantsSnapshot()
    {
        lock (_gate) return _wants.ToArray();
    }

    private int NextLineId()
    {
        lock (_gate) return _nextLineId++;
    }

    /// <summary>Reads the executable path when the OS lets us; a protected or different-bitness process
    /// leaves us the name, which is a weaker but still reboot-surviving identity.</summary>
    private static string ModulePath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; }
        catch (Exception) { return ""; }
    }

    private async Task AttachLiveLocked(SourceWant want, int pid)
    {
        var process = Process.GetProcessById(pid);
        var feed = new Feed
        {
            Pid = pid,
            Process = process,
            Ring = new SourceRing(RingCapacityFrames, Channels, SampleRate),
        };
        want.Live = feed;
        try
        {
            await AttachRecorder(want, feed);
        }
        catch
        {
            want.Live = null;
            try { process.Dispose(); } catch { }
            throw;
        }
        // Readers are attached after the recorder so the ring is already being filled; a reader created
        // now starts at the write head and primes itself as data arrives.
        foreach (int lineId in want.Lines.ToArray())
            FindLine(lineId)?.Bus?.AttachSource(feed.Ring, want.Id, want.Gain);
    }

    private async Task AttachRecorder(SourceWant want, Feed feed)
    {
        const ProcessLoopbackMode loopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        var ring = feed.Ring;
        var recorder = await Task.Run(() => new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)feed.Pid, loopbackMode)
            .WithBufferLength(100)
            .WithFormat(format)
            .WithMmcssThreadPriority("Pro Audio")
            .BuildAsync()).ConfigureAwait(false);
        try
        {
            recorder.DataAvailable += (buffer, _, _, _) =>
            {
                var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
                ring.Write(f);
                if (f.Length > 0)
                {
                    float peak = 0f;
                    foreach (float v in f) { float a = Math.Abs(v); if (a > peak) peak = a; }
                    want.Meter.Update(peak);
                }
            };
            recorder.RecordingStopped += (_, e) =>
            {
                feed.Alive = false;
                Log($"source stopped: pid={feed.Pid} {(e.Exception is null ? "clean" : e.Exception.Message)}");
            };
            recorder.StartRecording();
            feed.Recorder = recorder;
        }
        catch
        {
            try { recorder.Dispose(); } catch { }
            throw;
        }
    }

    private async Task DetachLiveLocked(SourceWant want, bool log = true)
    {
        var feed = want.Live;
        want.Live = null;
        if (feed is null) return;
        feed.Alive = false;
        try { feed.Recorder?.StopRecording(); } catch { }
        await DisposeRecorder(feed.Recorder);
        feed.Recorder = null;
        feed.Process?.Dispose();
        feed.Process = null;
        if (log) Log($"source detached: pid={feed.Pid} ({want.Identity.Name})");
    }

    public void SetSourceGain(int sourceId, float gain)
    {
        SourceWant? want;
        lock (_gate) want = _wants.FirstOrDefault(w => w.Id == sourceId);
        if (want is null) return;
        want.Gain = Math.Clamp(gain, 0f, 4f);
        foreach (int lineId in want.Lines.ToArray()) FindLine(lineId)?.Bus?.SetGain(want.Id, want.Gain);
    }

    // --- add-source previews ------------------------------------------------------------------
    // While the add list is open, every candidate app gets its own process loopback so the meters are
    // real captured audio. They are throwaway: no ring, no routes, disposed when the list closes.

    /// <summary>Starts/refreshes previews for the given windows. The lease expires unless renewed, so a
    /// tab that disappears cannot leave a dozen captures running.</summary>
    public async Task StartPreviews(IEnumerable<(int Pid, string Name, string Title)> candidates)
    {
        var wanted = new Dictionary<int, (string Name, string Title)>();
        foreach (var c in candidates)
            if (c.Pid > 0) wanted[c.Pid] = (c.Name, c.Title);

        HashSet<int> livePids;
        List<Preview> drop;
        var toStart = new List<Preview>();
        lock (_gate)
        {
            livePids = _wants.Where(w => w.Live is not null).Select(w => w.Live!.Pid).ToHashSet();
            _previewLease = DateTime.UtcNow.AddSeconds(15);
            drop = _previews.Values
                .Where(p => !wanted.ContainsKey(p.Pid) || livePids.Contains(p.Pid))
                .ToList();
            foreach (var p in drop) _previews.Remove(p.Pid);
            foreach (var (pid, info) in wanted)
            {
                // Already-added apps are captured by their real source; reuse that meter instead of
                // opening a second loopback on the same process.
                if (livePids.Contains(pid)) continue;
                if (_previews.TryGetValue(pid, out var existing))
                {
                    existing.Name = info.Name;
                    existing.Title = info.Title;
                    continue;
                }
                var preview = new Preview { Pid = pid, Name = info.Name, Title = info.Title };
                _previews[pid] = preview;
                toStart.Add(preview);
            }
        }
        foreach (var p in drop) await DisposeRecorder(p.Recorder);
        foreach (var p in toStart) await StartPreviewRecorder(p);
        EnsureMonitor();   // the preview lease is enforced by the monitor
    }

    private async Task StartPreviewRecorder(Preview preview)
    {
        try
        {
            const ProcessLoopbackMode loopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree;
            var recorder = await Task.Run(() => new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)preview.Pid, loopbackMode)
                .WithBufferLength(100)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
                .WithMmcssThreadPriority("Pro Audio")
                .BuildAsync());
            var meter = preview.Meter;
            recorder.DataAvailable += (buffer, _, _, _) =>
            {
                var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
                if (f.Length == 0) return;
                float peak = 0f;
                foreach (float v in f) { float a = Math.Abs(v); if (a > peak) peak = a; }
                meter.Update(peak);
            };
            recorder.StartRecording();
            preview.Recorder = recorder;
            preview.Error = "";
        }
        catch (Exception ex)
        {
            preview.Error = ex.Message;
            Log($"preview failed: pid={preview.Pid} ({preview.Name}): {ex.Message}");
        }
    }

    private async Task StopPreview(int pid)
    {
        Preview? preview;
        lock (_gate)
        {
            _previews.TryGetValue(pid, out preview);
            if (preview is not null) _previews.Remove(pid);
        }
        if (preview is null) return;
        try { preview.Recorder?.StopRecording(); } catch { }
        await DisposeRecorder(preview.Recorder);
    }

    public async Task StopPreviews()
    {
        List<Preview> all;
        lock (_gate)
        {
            all = _previews.Values.ToList();
            _previews.Clear();
        }
        if (all.Count == 0) return;
        foreach (var p in all) { try { p.Recorder?.StopRecording(); } catch { } }
        await Task.WhenAll(all.Select(p => DisposeRecorder(p.Recorder)));
        Log($"previews stopped ({all.Count})");
    }

    public async Task SetMic(string deviceId, float gain)
    {
        await _ops.WaitAsync();
        try { await SetMicLocked(deviceId, gain, log: true); }
        finally { _ops.Release(); }
    }

    /// <summary>Restoring a saved microphone must not grab the hardware: the selection comes back, but
    /// capture only opens once the mic has a line to send to.</summary>
    public async Task RestoreMic(string deviceId, float gain)
    {
        await _ops.WaitAsync();
        try { await SetMicLocked(deviceId, gain, log: true, onlyIfRouted: true); }
        finally { _ops.Release(); }
    }

    private async Task SetMicLocked(string deviceId, float gain, bool log, bool onlyIfRouted = false)
    {
        _micGain = Math.Clamp(gain, 0f, 4f);
        foreach (int lineId in _micLines) FindLine(lineId)?.Bus?.SetGain(MicFeedId, _micGain);

        // A gain edit on the device that is already chosen must not tear the capture down: the slider is
        // dragged continuously, and restarting the recorder would gap the very signal being adjusted.
        if ((_micRecorder is not null || _micId.Length > 0) && MicMatchesCurrent(deviceId))
        {
            if (log) Log($"mic gain -> {_micGain:F2}");
            return;
        }

        await CloseMicRecorderLocked();
        _micName = "";
        _micId = "";
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            if (log) Log("mic off");
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        _micDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        _micDevice = FindDevice(_micDevices, deviceId)
                     ?? throw new InvalidOperationException("麦克风未找到: " + deviceId);
        _micName = _micDevice.FriendlyName;
        _micId = _micDevice.ID;
        if (onlyIfRouted && _micLines.Count == 0)
        {
            if (log) Log($"mic selected: '{_micName}'，无线路，暂不采集");
            return;
        }
        await OpenMicRecorderLocked(log);
    }

    private async Task OpenMicRecorderLocked(bool log)
    {
        if (_micRecorder is not null || _micDevice is null) return;
        _micRecorder = new WasapiRecorderBuilder()
            .WithDevice(_micDevice)
            .WithBufferLength(100)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        var ring = _mic.Ring;
        var meter = _micMeter;
        _micRecorder.DataAvailable += (buffer, _, _, _) =>
        {
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            ring.Write(f);
            if (f.Length > 0)
            {
                float peak = 0f;
                foreach (float v in f) { float a = Math.Abs(v); if (a > peak) peak = a; }
                meter.Update(peak);
            }
        };
        _micRecorder.RecordingStopped += (_, e) =>
        {
            Log($"mic stopped: {(e.Exception is null ? "clean" : e.Exception.Message)}");
        };
        _micRecorder.StartRecording();
        if (log) Log($"mic capture on: '{_micName}' gain={_micGain:F2}");
    }

    private async Task CloseMicRecorderLocked()
    {
        if (_micRecorder is null) return;
        try { _micRecorder.StopRecording(); } catch { }
        await DisposeRecorder(_micRecorder);
        _micRecorder = null;
        _micDevice?.Dispose();
        _micDevice = null;
        _micDevices?.Dispose();
        _micDevices = null;
    }

    private bool MicMatchesCurrent(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(_micId)) return false;
        selector = selector.Trim('"', '\'');
        return string.Equals(selector, _micId, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(_micName) && string.Equals(selector, _micName, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureMonitor()
    {
        lock (_gate)
        {
            if (_monitor is not null || _cts is not null) return;
            _cts = new CancellationTokenSource();
            _monitor = MonitorAsync(_cts.Token);
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        int tick = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var want in WantsSnapshot())
                {
                    var feed = want.Live;
                    if (feed is null) continue;
                    // A capture can die before the process does (device removed, exclusive mode taken);
                    // either way the source goes back to waiting and the monitor keeps looking for it.
                    string how = "";
                    if (!feed.Alive) how = "capture stopped";
                    else if (feed.Process is not null)
                    {
                        bool handleCheck = feed.HandleCheck;
                        if (TargetGone(feed.Process, want.Identity.Name, ref handleCheck))
                        {
                            feed.HandleCheck = handleCheck;
                            how = "process exited";
                        }
                    }
                    if (how.Length == 0) continue;
                    int pid = feed.Pid;
                    await _ops.WaitAsync();
                    try
                    {
                        if (!ReferenceEquals(want.Live, feed)) continue;
                        await DetachLiveLocked(want, log: false);
                    }
                    finally { _ops.Release(); }
                    Log($"source gone: pid={pid} ({want.Identity.Name}) — {how}; waiting for it to come back");
                }
                // Two seconds is fast enough to feel instant when an app starts, and the scan is free
                // unless a source is actually waiting for its process.
                if (++tick % 4 == 0) await ReconnectSources();
                if (PreviewExpired()) await StopPreviews();
            }
            catch (Exception ex)
            {
                Log("monitor error: " + ex.Message);
            }
            try { await Task.Delay(500, token); } catch { return; }
        }
    }

    private bool PreviewExpired()
    {
        lock (_gate) return _previews.Count > 0 && DateTime.UtcNow > _previewLease;
    }

    public async Task Stop()
    {
        await _ops.WaitAsync();
        try
        {
            _cts?.Cancel();
            await StopPreviews();
            foreach (var want in WantsSnapshot()) await DetachLiveLocked(want, log: false);
            foreach (var line in LineIdsSnapshot())
            {
                Line? l;
                lock (_gate) l = _lines.FirstOrDefault(x => x.Id == line);
                if (l is null) continue;
                lock (_gate) _lines.Remove(l);
                CloseLineBus(l);
            }
            await SetMicLocked("", _micGain, log: false);
            _micLines.Clear();
            if (_monitor is not null)
            {
                try { await Task.WhenAny(_monitor, Task.Delay(1500)); } catch { }
                _monitor = null;
            }
            _cts?.Dispose();
            _cts = null;
            _error = "";
            Log("engine stopped");
        }
        finally
        {
            _ops.Release();
        }
    }

    private int[] LineIdsSnapshot()
    {
        lock (_gate) return _lines.Select(l => l.Id).ToArray();
    }

    private static MMDevice? FindDevice(MMDeviceCollection collection, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;
        selector = selector.Trim('"', '\'');
        for (int i = 0; i < collection.Count; i++)
        {
            var d = collection[i];
            if (string.Equals(d.ID, selector, StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
                return d;
            d.Dispose();
        }
        return null;
    }

    private static bool TargetGone(Process target, string name, ref bool handleCheckAvailable)
    {
        if (handleCheckAvailable)
        {
            try
            {
                target.Refresh();
                return target.HasExited;
            }
            catch (Exception)
            {
                handleCheckAvailable = false;
            }
        }
        try
        {
            using var again = Process.GetProcessById(target.Id);
            return !string.Equals(again.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return true; }
    }

    private static async Task DisposeRecorder(WasapiRecorder? recorder)
    {
        if (recorder is null) return;
        var pending = recorder.DisposeAsync().AsTask();
        if (await Task.WhenAny(pending, Task.Delay(3000)) != pending) return;
        try { await pending; } catch { }
    }

    private static string Describe(Exception ex)
    {
        string hr = ex.HResult != 0 ? $" (HRESULT 0x{ex.HResult:X8})" : "";
        return $"{ex.GetType().Name}: {ex.Message}{hr}";
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        _ops.Dispose();
    }
}
