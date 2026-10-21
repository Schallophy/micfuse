using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

internal static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitBadPid = 2;
    private const int ExitActivation = 3;
    private const int ExitStart = 4;
    private const int ExitCaptureError = 5;
    private const int ExitTargetGone = 6;
    private const int ExitDeviceNotFound = 7;
    private const int ExitDeviceGone = 8;

    [System.STAThread]
    private static async Task<int> Main(string[] args)
    {
        EnsureConsole(args);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var options = Options.Parse(args, out int parseExit);
        if (options is null) return parseExit;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Line("SIGINT received, stopping");
            _stop.Cancel();
        };

        try
        {
            Log.Init(options.LogPath);
            if (options.ListDevices) return Devices.ListDevices();
            if (options.ListSessions) return await Devices.ListAudioSessions(options.Seconds);
            if (options.Probe) return await Probe.Run(options);
            if (options.Web) return await RunWeb(options);
            if (options.SetVolumeValue >= 0) return SetEndpointVolume(options);
            if (options.Loopback) return await RunLoopback(options);
            if (options.Mix) return await RunMix(options);
            return options.Mic ? await RunMic(options) : await RunCapture(options);
        }
        finally
        {
            Log.Close();
        }
    }

    private static readonly CancellationTokenSource _stop = new();

    private static void EnsureConsole(string[] args)
    {
        // The product is a GUI/tray application. A console is only created for explicit CLI
        // diagnostics launched without an inherited console; redirected output must stay redirected.
        if (GetConsoleWindow() != IntPtr.Zero) return;
        if (args.Length == 0) return;
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return;
        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) return;
        AllocConsole();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static int SetEndpointVolume(Options o)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try
        {
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                bool match = string.Equals(device.ID, o.SetVolumeSelector, StringComparison.OrdinalIgnoreCase)
                             || device.FriendlyName.Contains(o.SetVolumeSelector, StringComparison.OrdinalIgnoreCase);
                if (!match) { device.Dispose(); continue; }
                float before = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                float beforeDb = device.AudioEndpointVolume.MasterVolumeLevel;
                device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(o.SetVolumeValue, 0f, 1f);
                Log.Line($"endpoint '{device.FriendlyName}': volume {before:F2} ({beforeDb:F2} dB) -> " +
                         $"{device.AudioEndpointVolume.MasterVolumeLevelScalar:F2} ({device.AudioEndpointVolume.MasterVolumeLevel:F2} dB)");
                device.Dispose();
                return ExitOk;
            }
            Log.Line($"RESULT=FAIL code={ExitDeviceNotFound} reason=render-device-not-found selector=\"{o.SetVolumeSelector}\"");
            return ExitDeviceNotFound;
        }
        finally
        {
            devices.Dispose();
        }
    }

    private static async Task<int> RunWeb(Options o)
    {
        await using var engine = new AudioEngine();
        var settings = Settings.Load();

        // Restored from choices the user made, never invented: a first run adds no line and starts nothing.
        foreach (var line in settings.Lines)
        {
            try
            {
                await engine.AddLine(line.DeviceId, line.Id);
                Log.Line($"line {line.Id} restored from settings");
            }
            catch (Exception ex)
            {
                Log.Line($"line {line.Id} restore skipped: " + Describe(ex));
            }
        }

        // Sources the user added come back by identity, not by pid: whatever is already running is
        // attached now, whatever is not stays in the list waiting until it appears.
        foreach (var source in settings.Sources)
        {
            try
            {
                await engine.RestoreSource(
                    new RouteIdentity { Name = source.Name, Path = source.Path },
                    source.Title, source.Gain, source.Lines);
                Log.Line($"source '{source.Name}' restored from settings, lines=[{string.Join(',', source.Lines)}]");
            }
            catch (Exception ex)
            {
                Log.Line($"source '{source.Name}' restore skipped: " + Describe(ex));
            }
        }

        try { await engine.ReconnectSources(); }
        catch (Exception ex) { Log.Line("source reconnect failed: " + Describe(ex)); }

        foreach (int lineId in settings.MicRoutes)
        {
            try
            {
                await engine.SetMicRoute(lineId, true);
                Log.Line($"mic route restored -> line {lineId}");
            }
            catch (Exception ex)
            {
                Log.Line($"mic route {lineId} restore skipped: " + Describe(ex));
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.MicId))
        {
            try
            {
                await engine.RestoreMic(settings.MicId, settings.MicGain);
                Log.Line($"mic restored from settings, gain={settings.MicGain:F2}");
            }
            catch (Exception ex)
            {
                Log.Line("mic restore skipped: " + Describe(ex));
            }
        }

        var web = new WebUi(engine, new WebOptions { Port = o.Port, OpenBrowser = !o.NoBrowser }, settings);
        if (!o.Tray)
        {
            try
            {
                await web.RunAsync(_stop.Token);
                return ExitOk;
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=web error={Describe(ex)}");
                return ExitCaptureError;
            }
            finally
            {
                await engine.Stop();
            }
        }

        return await RunWebWithTray(engine, web);
    }

    private static async Task<int> RunWebWithTray(AudioEngine engine, WebUi web)
    {
        var server = web.RunAsync(_stop.Token);
        System.Threading.SpinWait.SpinUntil(() => web.Url is not null || server.IsCompleted, TimeSpan.FromSeconds(5));

        if (web.Url is null)
        {
            string reason = server.IsFaulted ? Describe(server.Exception!.GetBaseException()) : "web server did not start";
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=tray-server error={reason}");
            _stop.Cancel();
            return ExitCaptureError;
        }

        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var tray = new SystemTray(engine, web, server, _stop.Token);
            System.Windows.Forms.Application.Run(tray.Context);
        }
        catch (Exception ex)
        {
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=tray error={Describe(ex)}");
        }
        finally
        {
            _stop.Cancel();
            var finished = await Task.WhenAny(server, Task.Delay(3000));
            if (finished != server) Log.Line("WARN web server did not stop within 3s");
            await engine.Stop();
        }

        if (server.IsFaulted)
        {
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=web error={Describe(server.Exception!.GetBaseException())}");
            return ExitCaptureError;
        }
        Log.Line("RESULT=OK code=0 reason=tray-exited");
        return ExitOk;
    }

    private static async Task<int> RunCapture(Options o)
    {
        if (o.Cycles <= 1) return await RunOnce(o, o.OutPath);

        Log.Line($"cycle stress: {o.Cycles} cycles of {o.Seconds}s against pid={o.Pid}");
        int last = 0;
        for (int cycle = 1; cycle <= o.Cycles; cycle++)
        {
            string outPath = $"{o.OutPath}.cycle{cycle:D3}.wav";
            Log.Line($"CYCLE {cycle}/{o.Cycles} begin {Resources()}");
            last = await RunOnce(o, outPath);
            var beforeGc = Resources();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Log.Line($"CYCLE {cycle}/{o.Cycles} end exit={last} {beforeGc} | afterGC {Resources()}");
            if (last is not (0 or ExitTargetGone))
            {
                Log.Line($"CYCLE {cycle} FAILED, stopping stress run; WAV kept at {outPath}");
                return last;
            }
            try { File.Delete(outPath); } catch { }
        }
        Log.Line($"CYCLE STRESS RESULT=OK cycles={o.Cycles} all completed, wavs deleted {Resources()}");
        return 0;
    }

    private static string Resources()
    {
        using var self = Process.GetCurrentProcess();
        return $"rss={self.WorkingSet64 / 1048576.0:F1}MB handles={self.HandleCount} threads={self.Threads.Count}";
    }

    private static async Task<int> RunOnce(Options o, string outPath)
    {
        Log.Line($"MicFuse 0.1.0 | .NET {Environment.Version} | OS {Environment.OSVersion.Version} | {RuntimeInformation.OSDescription}");
        Log.Line($"request: pid={o.Pid} mode={o.Mode} seconds={o.Seconds:F1} out={o.OutPath} " +
                 $"formatRequested={(o.NoFormatOverride ? "default(native)" : $"{o.SampleRate}Hz/{o.Channels}ch/{(o.Pcm16 ? "pcm16" : "float32")}")} bufferMs={o.BufferMs}");

        Process target;
        try
        {
            target = Process.GetProcessById(o.Pid);
            target.Refresh();
        }
        catch (ArgumentException)
        {
            Log.Line($"RESULT=FAIL code={ExitBadPid} reason=target-pid-not-found pid={o.Pid}");
            return ExitBadPid;
        }
        Log.Line($"target: pid={target.Id} name={ProcessName(target)} started={SafeStart(target)}");

        WasapiRecorder? recorder = null;
        WaveFileWriter? writer = null;
        CaptureMetrics? metrics = null;
        var stoppedByRecorder = new TaskCompletionSource<Exception?>();
        Exception? captureThreadError = null;

        try
        {
            recorder = await Task.Run(() =>
            {
                var builder = new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)o.Pid, o.Mode)
                    .WithBufferLength(o.BufferMs)
                    .WithMmcssThreadPriority("Pro Audio")
                    .WithLowLatency(o.LowLatency);
                if (!o.NoFormatOverride)
                {
                    var fmt = o.Pcm16
                        ? new WaveFormat(o.SampleRate, 16, o.Channels)
                        : WaveFormat.CreateIeeeFloatWaveFormat(o.SampleRate, o.Channels);
                    builder = builder.WithFormat(fmt);
                }
                return builder.BuildAsync();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Line($"RESULT=FAIL code={ExitActivation} stage=activation error={Describe(ex)}");
            return ExitActivation;
        }

        var wf = recorder!.WaveFormat;
        Log.Line($"activated: deviceId={recorder.DeviceId}");
        LogLowLatency("capture", recorder, o.LowLatency);
        Log.Line($"capture format: {wf} | encoding={wf.Encoding} rate={wf.SampleRate} channels={wf.Channels} " +
                 $"bits={wf.BitsPerSample} blockAlign={wf.BlockAlign} avgBytesPerSec={wf.AverageBytesPerSecond}");

        metrics = new CaptureMetrics(wf.BlockAlign, wf.SampleRate);
        int bytesPerFrame = wf.BlockAlign;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        var recorderUsed = recorder;
        try
        {
            writer = new WaveFileWriter(outPath, wf);
            var w = writer;
            var m = metrics;
            Exception? writerError = null;

            recorderUsed.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
            {
                if (writerError is not null) return;
                bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                m.OnData(buffer, devicePosition, qpcPosition, silent);
                try
                {
                    if (!buffer.IsEmpty) w.Write(buffer);
                }
                catch (Exception ex)
                {
                    writerError = ex;
                    Log.Line($"ERROR writing WAV: {Describe(ex)}");
                    _stop.Cancel();
                }
            };
            recorderUsed.RecordingStopped += (_, e) =>
            {
                captureThreadError = e.Exception;
                stoppedByRecorder.TrySetResult(e.Exception);
            };

            try
            {
                recorderUsed.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitStart} stage=start error={Describe(ex)}");
                return ExitStart;
            }
            Log.Line("state=CAPTURING");

            var deadline = DateTime.UtcNow.AddSeconds(o.Seconds);
            var statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
            using var sampler = new ProcessSampler();
            int targetGoneExit = -1;
            string targetName = ProcessName(target);
            bool handleCheckAvailable = true;

            while (true)
            {
                bool timedOut = DateTime.UtcNow >= deadline;
                if (timedOut || _stop.IsCancellationRequested || stoppedByRecorder.Task.IsCompleted) break;

                if (TargetGone(target, targetName, ref handleCheckAvailable))
                {
                    Log.Line($"target process gone at t={metrics.ElapsedSeconds:F1}s -> no more audio from it");
                    targetGoneExit = ExitTargetGone;
                    break;
                }

                if (DateTime.UtcNow >= statsDeadline)
                {
                    var (rss, cpu) = sampler.Sample();
                    Log.Line($"STATS {m.Stats(rss, cpu)}");
                    statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
                }

                await Task.Delay(200).ConfigureAwait(false);
            }

            recorderUsed.StopRecording();
            try
            {
                await stoppedByRecorder.Task.ConfigureAwait(false);
            }
            catch (Exception) { }
            Log.Line("state=STOPPED capture thread joined");

            Log.Line("teardown: writer disposing");
            writer.Dispose();
            writer = null;
            Log.Line("teardown: writer disposed, recorder disposing");
            await DisposeRecorder(recorder).ConfigureAwait(false);
            recorder = null;

            int exit = Report(o, outPath, metrics, targetGoneExit, captureThreadError, writerError);
            return exit;
        }
        catch (Exception ex)
        {
            var frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=capture error={Describe(ex)} at {frame}");
            return ExitCaptureError;
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
            if (recorder is not null)
            {
                try { recorder.StopRecording(); } catch { }
                await DisposeRecorder(recorder).ConfigureAwait(false);
            }
            target.Dispose();
        }
    }

    /// <summary>
    /// Disposes the recorder with a bound. On this machine DisposeAsync was observed to
    /// never complete for a target that rendered only silence, which would hang the process.
    /// </summary>
    private static async Task DisposeRecorder(WasapiRecorder recorder)
    {
        var pending = recorder.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (finished != pending)
        {
            Log.Line("WARN recorder.DisposeAsync did not complete within 5s (leaked COM client to allow process exit)");
            return;
        }
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) { Log.Line("recorder dispose threw: " + Describe(ex)); }
        Log.Line("teardown: recorder disposed");
    }

    /// <summary>
    /// True when the target is no longer rendering anything because it is gone. Protected
    /// processes deny the synchronize handle HasExited needs, so that path degrades to a
    /// pid-plus-name lookup instead of aborting the capture.
    /// </summary>
    private static bool TargetGone(Process target, string targetName, ref bool handleCheckAvailable)
    {
        if (handleCheckAvailable)
        {
            try
            {
                target.Refresh();
                return target.HasExited;
            }
            catch (Exception ex)
            {
                handleCheckAvailable = false;
                Log.Line($"WARN HasExited unavailable ({ex.GetType().Name}: {ex.Message}); " +
                         "falling back to pid lookup (a recycled pid may be mistaken for the target)");
            }
        }

        try
        {
            using var again = Process.GetProcessById(target.Id);
            return !string.Equals(again.ProcessName, targetName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static int Report(Options o, string outPath, CaptureMetrics m, int targetGoneExit, Exception? captureThreadError, Exception? writerError)
    {
        var info = new FileInfo(outPath);
        double audioSeconds = m.AudioSeconds;
        double expectedSeconds = o.Seconds;
        string wav = info.Exists
            ? $"{info.Length} bytes, {audioSeconds:F2}s of PCM"
            : "MISSING";

        Log.Line($"summary: packets={m.Packets} silentPackets={m.SilentPackets} bytes={m.Bytes} " +
                 $"audio={audioSeconds:F2}s elapsed={m.ElapsedSeconds:F2}s coverage={(audioSeconds / Math.Max(0.001, m.ElapsedSeconds) * 100):F1}% " +
                 $"maxDrift={m.MaxDriftMs:+0.0;-0.0;0.0}ms maxCallbackGap={m.MaxCallbackIntervalMs:F1}ms gapEvents={m.GapEvents}");
        Log.Line("callbacks: " + m.IntervalSummary());
        Log.Line($"wav: {outPath} ({wav})");

        if (captureThreadError is not null)
        {
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=capture-thread error={Describe(captureThreadError)}");
            return ExitCaptureError;
        }
        if (writerError is not null)
        {
            Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=wav-writer error={Describe(writerError)}");
            return ExitCaptureError;
        }
        if (targetGoneExit >= 0)
        {
            Log.Line($"RESULT=STOPPED code={targetGoneExit} reason=target-process-exited expectedSeconds={expectedSeconds:F1}");
            return targetGoneExit;
        }
        if (m.Packets == 0)
        {
            Log.Line($"RESULT=NO_DATA code={ExitOk} reason=no-buffers-received (target rendered no audio, or OS/NAudio limitation)");
            return ExitOk;
        }
        Log.Line($"RESULT=OK code={ExitOk} captured={audioSeconds:F2}s of {expectedSeconds:F1}s requested");
        return ExitOk;
    }

    private static void LogLowLatency(string path, WasapiRecorder recorder, bool requested)
    {
        if (!requested) return;
        Log.Line($"{path} low latency: active={recorder.LowLatencyActive} granted={recorder.LatencyMilliseconds}ms " +
                 $"reason={recorder.LowLatencyUnavailableReason ?? "-"}");
    }

    private static string ProcessName(Process p)
    {
        try { return p.ProcessName; } catch { return "(unknown)"; }
    }

    private static string SafeStart(Process p)
    {
        try { return p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch (Exception ex) { return "(unavailable: " + ex.GetType().Name + ")"; }
    }

    private static string Describe(Exception ex)
    {
        string hr = ex.HResult != 0 ? $" (HRESULT 0x{ex.HResult:X8})" : "";
        string inner = ex.InnerException is null ? "" : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        return $"{ex.GetType().Name}: {ex.Message}{hr}{inner}";
    }
}
