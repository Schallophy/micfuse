using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

public sealed class SourceInfo
{
    public int Id;
    public int Pid;
    public string Name = "";
    public float Gain = 1f;
    public float Level;
    public float Hold;
    public bool Alive = true;
    public double BufferedMs;
    public long StarvedFrames;
    public long IdleFrames;
    public long DroppedFrames;
    /// <summary>Current adaptive-resampling ratio deviation in ppm; 0 means reading 1:1.</summary>
    public int RatioPpm;
}

public sealed class EngineStatus
{
    public bool Running;
    public string State = "stopped";
    public string Error = "";
    public string OutputId = "";
    public string OutputName = "";
    public string MicId = "";
    public float MicGain = 1f;
    public float MicLevel;
    public float MixLevel;
    public float MicHold;
    public float MixHold;
    public long ClippedSamples;
    /// <summary>Blocks replaced with silence because the mix threw; should stay 0.</summary>
    public long MixBackstops;
    /// <summary>Sum of deadline misses across live sources and the mic: silence we had to insert.</summary>
    public long StarvedFrames;
    /// <summary>Frames sitting in the render endpoint buffer right now (GetCurrentPadding).</summary>
    public double QueueMs;
    /// <summary>Latency the audio engine actually granted this output at Init time.</summary>
    public double RenderLatencyMs;
    public bool LowLatencyActive;
    public string LowLatencyReason = "";
    public double MicBufferedMs;
    /// <summary>Adaptive-resampling ratio of the mic path, in ppm; 0 means reading 1:1.</summary>
    public int MicRatioPpm;
    public SourceInfo[] Sources = Array.Empty<SourceInfo>();
}

/// <summary>
/// OBS-like engine: the output device and mixer stay up; every captured app is an independent
/// source that can be added, removed or re-gained while audio keeps flowing.
/// Windows binds one PID per process-loopback client, so one source == one recorder.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private sealed class AppSource
    {
        public int MixerId;
        public int Pid;
        public string Name = "";
        public float Gain = 1f;
        public MeterState Meter = new();
        public bool Alive = true;
        public WasapiRecorder? Recorder;
        public Process? Process;
        public bool HandleCheck = true;
    }

    private readonly SemaphoreSlim _ops = new(1, 1);
    private readonly object _gate = new();
    private readonly List<AppSource> _sources = new();

    private Mixer? _mixer;
    private MixerProvider? _provider;
    private WasapiPlayer? _player;
    private MMDevice? _outDevice;
    private MMDeviceCollection? _outDevices;
    private MMDeviceCollection? _micDevices;
    private MMDevice? _micDevice;
    private WasapiRecorder? _micRecorder;
    private string _micName = "";
    private string _micId = "";
    private float _micGain = 1f;
    private bool _running;
    private volatile bool _stopping;
    private string _state = "stopped";
    private string _error = "";
    private string _outName = "";
    private string _outId = "";
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private double _renderLatencyMs;
    private bool _lowLatencyActive;
    private string _lowLatencyReason = "";

    /// <summary>The session log file is the only sink now that the UI panel is gone.</summary>
    private void Log(string message)
    {
        global::MicFuse.Log.Line(message);
    }

    public EngineStatus Snapshot()
    {
        var status = new EngineStatus
        {
            Running = _running,
            State = _state,
            Error = _error,
            OutputId = _outId,
            OutputName = _outName,
            MicId = _micId,
            MicGain = _micGain,
            MicLevel = _mixer?.MicPeak ?? 0,
            MixLevel = _mixer?.MixPeak ?? 0,
            MicHold = _mixer?.MicHold ?? 0,
            MixHold = _mixer?.MixHold ?? 0,
            ClippedSamples = _mixer?.ClippedSamples ?? 0,
            MixBackstops = _mixer?.BackstopErrors ?? 0,
            QueueMs = QueueDepthMs(),
            RenderLatencyMs = _renderLatencyMs,
            LowLatencyActive = _lowLatencyActive,
            LowLatencyReason = _lowLatencyReason,
        };
        var micStats = _mixer?.MicStats();
        status.MicBufferedMs = micStats?.BufferedMs ?? 0;
        status.MicRatioPpm = micStats?.RatioPpm ?? 0;
        status.StarvedFrames = micStats?.StarvedFrames ?? 0;
        lock (_gate)
        {
            var mixer = _mixer;
            var list = new List<SourceInfo>(_sources.Count);
            foreach (var s in _sources)
            {
                var st = mixer?.SourceStats(s.MixerId) ?? default;
                status.StarvedFrames += st.StarvedFrames;
                list.Add(new SourceInfo
                {
                    Id = s.MixerId,
                    Pid = s.Pid,
                    Name = s.Name,
                    Gain = s.Gain,
                    Level = s.Meter.Level,
                    Hold = s.Meter.Hold,
                    Alive = s.Alive,
                    BufferedMs = st.BufferedMs,
                    StarvedFrames = st.StarvedFrames,
                    IdleFrames = st.IdleFrames,
                    DroppedFrames = st.DroppedFrames,
                    RatioPpm = st.RatioPpm,
                });
            }
            status.Sources = list.ToArray();
        }
        return status;
    }

    /// <summary>What the render endpoint still holds; read live because it is the part of the
    /// pipeline that actually varies while playing.</summary>
    private double QueueDepthMs()
    {
        try { return _player?.CurrentLatency.TotalMilliseconds ?? 0; }
        catch (Exception) { return 0; }
    }

    private void CapturePlayerTiming()
    {
        var player = _player;
        if (player is null)
        {
            _renderLatencyMs = 0;
            _lowLatencyActive = false;
            _lowLatencyReason = "";
            return;
        }
        _renderLatencyMs = player.LatencyMilliseconds;
        _lowLatencyActive = player.LowLatencyActive;
        _lowLatencyReason = player.LowLatencyUnavailableReason ?? "";
        Log($"output timing: granted={_renderLatencyMs:F1}ms lowLatency={_lowLatencyActive}" +
            (string.IsNullOrEmpty(_lowLatencyReason) ? "" : $" reason={_lowLatencyReason}"));
    }

    public async Task Start(string outputDeviceId)
    {
        await _ops.WaitAsync();
        try { await StartLocked(outputDeviceId); }
        finally { _ops.Release(); }
    }

    private async Task StartLocked(string outputDeviceId)
    {
        try
        {
            if (_running) return;
            _error = "";
            _state = "starting";
            using var enumerator = new MMDeviceEnumerator();
            _outDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            _outDevice = FindDevice(_outDevices, outputDeviceId)
                         ?? throw new InvalidOperationException("output device not found: " + outputDeviceId);
            _outName = _outDevice.FriendlyName;
            _outId = _outDevice.ID;

            _mixer = new Mixer(SampleRate, Channels, SampleRate * 10 / 1000, SampleRate / 2, addInitialSource: false);
            _provider = new MixerProvider(_mixer, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels), null, SampleRate);
            _player = BuildPlayer(_outDevice);
            _player.Init(_provider);
            CapturePlayerTiming();
            _player.Play();
            _cts = new CancellationTokenSource();
            _monitor = MonitorAsync(_cts.Token);
            _running = true;
            _state = "running";
            Log($"engine started -> '{_outName}'");
        }
        catch (Exception ex)
        {
            _error = Describe(ex);
            _state = "faulted";
            Log("start failed: " + _error);
            await StopLocked(keepError: true);
        }
    }

    /// <summary>
    /// Best effort: a declined low-latency request must fall back to standard shared mode, not
    /// abort the engine; what was actually granted is read back via CapturePlayerTiming. The stop
    /// hook is attached before Init so a player that dies during startup is still seen.
    /// </summary>
    private WasapiPlayer BuildPlayer(MMDevice device)
    {
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency(false)
            .WithLatency(20)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        player.PlaybackStopped += (_, e) => OnPlaybackStopped(player, e.Exception);
        return player;
    }

    /// <summary>Detach before stopping so the stop we asked for cannot be reported as a fault.</summary>
    private void DetachPlayer()
    {
        var player = _player;
        _player = null;
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
    }

    /// <summary>
    /// Replaces the render player in place: the mixer, every source and the mic keep running on the
    /// same provider, so a failed switch can be undone by attaching a player to the previous endpoint.
    /// On failure no player is left attached.
    /// </summary>
    private void SwapPlayer(MMDevice device, MMDeviceCollection collection)
    {
        _stopping = true;
        DetachPlayer();
        _stopping = false;
        var player = BuildPlayer(device);
        _player = player;
        player.Init(_provider!);
        player.Play();
        _outName = device.FriendlyName;
        _outId = device.ID;
        CapturePlayerTiming();
        try { _outDevice?.Dispose(); } catch { }
        try { _outDevices?.Dispose(); } catch { }
        _outDevice = device;
        _outDevices = collection;
        _state = "running";
        _error = "";
    }

    /// <summary>Runs on NAudio's playback thread: record the fault, never touch the pipeline here.</summary>
    private void OnPlaybackStopped(WasapiPlayer player, Exception? ex)
    {
        // A player that was already stopped or replaced can raise this late; only the live one counts.
        if (_stopping || !_running || !ReferenceEquals(player, _player)) return;
        _error = ex is null ? "playback stopped (output device removed?)" : Describe(ex);
        _state = "faulted";
        Log("playback stopped: " + _error);
    }

    public async Task Stop()
    {
        await _ops.WaitAsync();
        try { await StopLocked(keepError: false); }
        finally { _ops.Release(); }
    }

    public async Task<bool> StartPreferredAsync()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? deviceId = null;
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    deviceId = devices[i].ID;
                    break;
                }
            }
            if (deviceId is null && enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var fallback))
            {
                deviceId = fallback.ID;
                fallback.Dispose();
            }
            if (deviceId is null && devices.Count > 0) deviceId = devices[0].ID;
        }
        finally
        {
            devices.Dispose();
        }

        if (deviceId is null) return false;
        await Start(deviceId);
        return true;
    }

    public async Task RestartCurrentAsync()
    {
        string outputDeviceId = _outId;
        if (string.IsNullOrWhiteSpace(outputDeviceId))
        {
            await StartPreferredAsync();
            return;
        }
        await Restart(outputDeviceId);
    }

    public async Task Restart(string outputDeviceId)
    {
        List<(int Pid, float Gain)> previousSources;
        lock (_gate) previousSources = _sources.Select(s => (s.Pid, s.Gain)).ToList();
        string previousMicId = _micId;
        float previousMicGain = _micGain;

        await Stop();
        await Start(outputDeviceId);

        foreach (var (pid, gain) in previousSources)
        {
            try
            {
                int id = await AddSource(pid);
                SetSourceGain(id, gain);
            }
            catch (Exception ex)
            {
                Log($"source restore failed pid={pid}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(previousMicId))
        {
            try { await SetMic(previousMicId, previousMicGain); }
            catch (Exception ex) { Log($"mic restore failed: {ex.Message}"); }
        }

        Log($"engine restarted: sources={previousSources.Count} mic={(!string.IsNullOrWhiteSpace(previousMicId) ? "yes" : "no")}");
    }

    private async Task StopLocked(bool keepError)
    {
        _stopping = true;
        _cts?.Cancel();
        foreach (var source in _sources.ToArray()) await RemoveSourceLocked(source, log: false);
        _sources.Clear();
        await SetMicLocked("", _micGain, log: false);
        DetachPlayer();
        _mixer = null;
        _provider = null;
        _outDevice?.Dispose();
        _outDevices?.Dispose();
        _outDevice = null;
        _outDevices = null;
        if (_monitor is not null)
        {
            try { await Task.WhenAny(_monitor, Task.Delay(1500)); } catch { }
            _monitor = null;
        }
        _cts?.Dispose();
        _cts = null;
        _running = false;
        _state = "stopped";
        _stopping = false;
        if (!keepError) _error = "";
        if (!keepError) Log("engine stopped");
    }

    public async Task<int> AddSource(int pid)
    {
        await _ops.WaitAsync();
        try
        {
            if (!_running || _mixer is null) throw new InvalidOperationException("engine is not running");

            var process = Process.GetProcessById(pid);
            var source = new AppSource
            {
                MixerId = _mixer.AddAppSource(),
                Pid = pid,
                Name = process.ProcessName,
                Process = process,
            };
            await AttachRecorder(source);
            lock (_gate) _sources.Add(source);
            Log($"source added: id={source.MixerId} pid={pid} ({source.Name})");
            return source.MixerId;
        }
        finally
        {
            _ops.Release();
        }
    }

    private async Task AttachRecorder(AppSource source)
    {
        var mixer = _mixer ?? throw new InvalidOperationException("mixer not ready");
        const ProcessLoopbackMode loopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        int mixerId = source.MixerId;
        var recorder = await Task.Run(() => new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)source.Pid, loopbackMode)
            .WithBufferLength(100)
            .WithFormat(format)
            .WithMmcssThreadPriority("Pro Audio")
            .BuildAsync()).ConfigureAwait(false);
        mixer.SetAppGain(mixerId, source.Gain);
        recorder.DataAvailable += (buffer, _, _, _) =>
        {
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            mixer.WriteApp(mixerId, f);
            if (f.Length > 0)
            {
                float peak = 0f;
                foreach (float v in f) { float a = Math.Abs(v); if (a > peak) peak = a; }
                source.Meter.Update(peak);
            }
        };
        recorder.RecordingStopped += (_, e) =>
        {
            source.Alive = false;
            mixer.RemoveAppSource(mixerId);
            Log($"source stopped: pid={source.Pid} {(e.Exception is null ? "clean" : e.Exception.Message)}");
        };
        recorder.StartRecording();
        source.Recorder = recorder;
    }

    public async Task RemoveSource(int mixerId)
    {
        await _ops.WaitAsync();
        try
        {
            AppSource? source;
            lock (_gate) source = _sources.FirstOrDefault(s => s.MixerId == mixerId);
            if (source is null) return;
            await RemoveSourceLocked(source, log: true);
            lock (_gate) _sources.Remove(source);
        }
        finally
        {
            _ops.Release();
        }
    }

    private async Task RemoveSourceLocked(AppSource source, bool log)
    {
        source.Alive = false;
        try { source.Recorder?.StopRecording(); } catch { }
        await DisposeRecorder(source.Recorder);
        source.Recorder = null;
        _mixer?.RemoveAppSource(source.MixerId);
        source.Process?.Dispose();
        source.Process = null;
        if (log) Log($"source removed: id={source.MixerId} pid={source.Pid}");
    }

    public void SetSourceGain(int mixerId, float gain)
    {
        AppSource? source;
        lock (_gate) source = _sources.FirstOrDefault(s => s.MixerId == mixerId);
        if (source is null) return;
        source.Gain = Math.Clamp(gain, 0f, 4f);
        _mixer?.SetAppGain(mixerId, source.Gain);
    }

    public async Task SetMic(string deviceId, float gain)
    {
        await _ops.WaitAsync();
        try { await SetMicLocked(deviceId, gain, log: true); }
        finally { _ops.Release(); }
    }

    private async Task SetMicLocked(string deviceId, float gain, bool log)
    {
        _micGain = Math.Clamp(gain, 0f, 4f);
        if (_mixer is not null) _mixer.MicGain = _micGain;

        if (_micRecorder is not null)
        {
            try { _micRecorder.StopRecording(); } catch { }
            await DisposeRecorder(_micRecorder);
            _micRecorder = null;
            _micDevice?.Dispose();
            _micDevice = null;
            _micDevices?.Dispose();
            _micDevices = null;
            _micName = "";
            _micId = "";
        }
        if (string.IsNullOrWhiteSpace(deviceId) || _mixer is null)
        {
            if (log) Log("mic off");
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        _micDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        _micDevice = FindDevice(_micDevices, deviceId)
                     ?? throw new InvalidOperationException("microphone not found: " + deviceId);
        _micName = _micDevice.FriendlyName;
        _micId = _micDevice.ID;
        _micRecorder = new WasapiRecorderBuilder()
            .WithDevice(_micDevice)
            .WithBufferLength(100)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        var mixer = _mixer;
        _micRecorder.DataAvailable += (buffer, _, _, _) =>
        {
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            mixer.WriteMic(f);
        };
        _micRecorder.RecordingStopped += (_, e) =>
        {
            Log($"mic stopped: {(e.Exception is null ? "clean" : e.Exception.Message)}");
        };
        _micRecorder.StartRecording();
        if (log) Log($"mic set: '{_micName}' gain={_micGain:F2}");
    }

    public async Task SetOutput(string deviceId)
    {
        await _ops.WaitAsync();
        try
        {
            if (!_running || _mixer is null || _provider is null)
                throw new InvalidOperationException("engine is not running");
            string previousId = _outId;
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var device = FindDevice(collection, deviceId)
                         ?? throw new InvalidOperationException("output device not found: " + deviceId);
            try
            {
                SwapPlayer(device, collection);
                Log($"output -> '{_outName}'");
            }
            catch (Exception ex)
            {
                // The old player is gone but the mix is intact: put a player back on the previous
                // endpoint instead of tearing the engine down and losing every source and the mic.
                string reason = Describe(ex);
                Log("output switch failed: " + reason);
                _stopping = true;
                DetachPlayer();
                _stopping = false;
                // SwapPlayer failed before adopting these, so they are still ours to release.
                try { device.Dispose(); } catch { }
                try { collection.Dispose(); } catch { }
                try
                {
                    if (string.IsNullOrWhiteSpace(previousId))
                        throw new InvalidOperationException("no previous output to roll back to");
                    using var rollbackEnumerator = new MMDeviceEnumerator();
                    var rollbackCollection = rollbackEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    var rollbackDevice = FindDevice(rollbackCollection, previousId)
                                         ?? throw new InvalidOperationException("previous output not found: " + previousId);
                    SwapPlayer(rollbackDevice, rollbackCollection);
                    Log($"output rolled back -> '{_outName}'");
                }
                catch (Exception rollbackEx)
                {
                    _error = "output switch failed: " + reason + "; rollback failed: " + Describe(rollbackEx);
                    _state = "faulted";
                    Log(_error);
                }
                throw;
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                List<AppSource> snapshot;
                lock (_gate) snapshot = _sources.ToList();
                foreach (var source in snapshot)
                {
                    if (!source.Alive || source.Process is null) continue;
                    if (TargetGone(source.Process, source.Name, ref source.HandleCheck))
                    {
                        source.Alive = false;
                        await RemoveSourceLocked(source, log: false);
                        Log($"source gone: pid={source.Pid} ({source.Name}); engine keeps running");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("monitor error: " + ex.Message);
            }
            try { await Task.Delay(500, token); } catch { return; }
        }
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
