using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

internal static partial class Program
{
    private const int MixSampleRate = 48000;
    private const int MixChannels = 2;
    private const int MixBlockMs = 10;

    private static async Task<int> RunMix(Options o)
    {
        Log.Line($"mix: targetPid={o.Pid} mic={(o.NoMic ? "disabled" : string.IsNullOrWhiteSpace(o.DeviceSelector) ? "default" : o.DeviceSelector)} " +
                 $"gains app={o.AppGain:F2} mic={o.MicGain:F2} seconds={o.Seconds:F1} out={o.OutPath}");

        var target = TryGetProcess(o.Pid);
        if (target is null)
        {
            Log.Line($"RESULT=FAIL code={ExitBadPid} reason=target-pid-not-found pid={o.Pid}");
            return ExitBadPid;
        }
        Log.Line($"target: pid={target.Id} name={ProcessName(target)}");

        using var enumerator = new MMDeviceEnumerator();
        MMDeviceCollection? micDevices = null;
        MMDevice? micDevice = null;
        if (!o.NoMic)
        {
            if (!TryResolveCaptureDevice(enumerator, o.DeviceSelector, out micDevice, out micDevices, out string why))
            {
                Log.Line($"RESULT=FAIL code={ExitDeviceNotFound} stage=mic-device reason={why}");
                return ExitDeviceNotFound;
            }
            Log.Line($"mic: {micDevice!.FriendlyName} id={micDevice.ID}");
        }

        WasapiRecorder? appRecorder = null;
        WasapiRecorder? micRecorder = null;
        WaveFileWriter? writer = null;
        MMDevice? renderDevice = null;
        MMDeviceCollection? renderDevices = null;
        try
        {
            var appFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels);
            try
            {
                appRecorder = await Task.Run(() => new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)o.Pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithBufferLength(o.BufferMs)
                    .WithFormat(appFormat)
                    .BuildAsync()).ConfigureAwait(false);
                Log.Line($"app capture format: {appRecorder.WaveFormat}");
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitActivation} stage=app-activation error={Describe(ex)}");
                return ExitActivation;
            }

            if (micDevice is not null)
            {
                try
                {
                    micRecorder = new WasapiRecorderBuilder()
                        .WithDevice(micDevice)
                        .WithBufferLength(o.BufferMs)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels))
                        .Build();
                    Log.Line($"mic capture format: {micRecorder.WaveFormat}");
                }
                catch (Exception ex)
                {
                    Log.Line($"RESULT=FAIL code={ExitActivation} stage=mic-build error={Describe(ex)}");
                    return ExitActivation;
                }
            }

            int framesPerBlock = MixSampleRate * MixBlockMs / 1000;
            var mixer = new Mixer(MixSampleRate, MixChannels, framesPerBlock, MixSampleRate / 2)
            {
                AppGain = (float)o.AppGain,
                MicGain = (float)o.MicGain,
                TestToneHz = o.TestToneHz,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.OutPath))!);
            if (!o.NoWav)
                writer = new WaveFileWriter(o.OutPath, WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels));
            else
                Log.Line("audio output disabled (--no-wav): mixing and metering only");

            var appAlive = true;
            var micAlive = micRecorder is not null;
            var appStopped = new TaskCompletionSource<Exception?>();
            var micStopped = new TaskCompletionSource<Exception?>();

            appRecorder.DataAvailable += (buffer, _, _, _) =>
            {
                var floats = MemoryMarshal.Cast<byte, float>(buffer);
                mixer.WriteApp(floats);
            };
            appRecorder.RecordingStopped += (_, e) =>
            {
                appAlive = false;
                Log.Line($"app capture stopped: {(e.Exception is null ? "clean" : Describe(e.Exception))}");
                appStopped.TrySetResult(e.Exception);
            };

            if (micRecorder is not null)
            {
                micRecorder.DataAvailable += (buffer, _, _, _) =>
                {
                    var floats = MemoryMarshal.Cast<byte, float>(buffer);
                    mixer.WriteMic(floats);
                };
                micRecorder.RecordingStopped += (_, e) =>
                {
                    micAlive = false;
                    Log.Line($"mic capture stopped: {(e.Exception is null ? "clean" : Describe(e.Exception))}");
                    micStopped.TrySetResult(e.Exception);
                };
            }

            try
            {
                appRecorder.StartRecording();
                micRecorder?.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitStart} stage=start error={Describe(ex)}");
                return ExitStart;
            }
            WasapiPlayer? renderOut = null;
            if (!string.IsNullOrWhiteSpace(o.OutDeviceSelector))
            {
                if (!TryResolveRenderDevice(enumerator, o.OutDeviceSelector, out renderDevice, out renderDevices, out string why))
                {
                    Log.Line($"RESULT=FAIL code={ExitDeviceNotFound} stage=out-device reason={why}");
                    return ExitDeviceNotFound;
                }
                Log.Line($"out device: {renderDevice!.FriendlyName} id={renderDevice.ID}");
            }

            Log.Line(renderDevice is null
                ? "state=MIXING (timer clock; file only)"
                : $"state=RENDERING to '{renderDevice.FriendlyName}' (device clock)");

            var block = new float[framesPerBlock * MixChannels];
            var bytes = new byte[block.Length * sizeof(float)];
            var clock = Stopwatch.StartNew();
            long writtenFrames = 0;
            var deadline = DateTime.UtcNow.AddSeconds(o.Seconds);
            var statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
            using var sampler = new ProcessSampler();

            if (renderDevice is not null)
            {
                var provider = new MixerProvider(mixer, WaveFormat.CreateIeeeFloatWaveFormat(MixSampleRate, MixChannels), writer, MixSampleRate);
                renderOut = new WasapiPlayerBuilder()
                    .WithDevice(renderDevice)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(100)
                    .Build();
                renderOut.Init(provider);
                renderOut.PlaybackStopped += (_, e) =>
                {
                    Log.Line($"player stopped: {(e.Exception is null ? "clean" : Describe(e.Exception))}");
                };
                renderOut.Play();
            }

            while (DateTime.UtcNow < deadline && !_stop.IsCancellationRequested)
            {
                if (renderOut is null)
                {
                    long wanted = (long)(clock.Elapsed.TotalSeconds * MixSampleRate);
                    while (writtenFrames < wanted && DateTime.UtcNow < deadline)
                    {
                        mixer.Mix(block);
                        if (writer is not null)
                        {
                            Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
                            writer.Write(bytes, 0, bytes.Length);
                        }
                        writtenFrames += framesPerBlock;
                    }
                }

                if (DateTime.UtcNow >= statsDeadline)
                {
                    var (rss, cpu) = sampler.Sample();
                    Log.Line($"STATS {mixer.StatsLine(MixSampleRate, rss, cpu)} alive app={appAlive} mic={micAlive}");
                    statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
                }

                if (!appAlive && !micAlive) break;
                await Task.Delay(renderOut is null ? 2 : 10).ConfigureAwait(false);
            }

            renderOut?.Stop();
            renderOut?.Dispose();
            renderOut = null;

            appRecorder.StopRecording();
            micRecorder?.StopRecording();
            try { await Task.WhenAny(Task.WhenAll(appStopped.Task, micStopped.Task), Task.Delay(3000)).ConfigureAwait(false); }
            catch { }
            Log.Line("state=STOPPED");

            writer?.Dispose();
            writer = null;
            await DisposeRecorder(appRecorder).ConfigureAwait(false);
            appRecorder = null;
            if (micRecorder is not null)
            {
                await DisposeRecorder(micRecorder).ConfigureAwait(false);
                micRecorder = null;
            }

            var info = new FileInfo(o.OutPath);
            double mixedSeconds = (renderDevice is not null ? mixer.FramesConsumed : writtenFrames) / (double)MixSampleRate;
            var (fr, fc) = sampler.Sample();
            Log.Line($"summary: {mixer.StatsLine(MixSampleRate, fr, fc)}");
            Log.Line(o.NoWav
                ? "wav: not written (--no-wav)"
                : $"wav: {o.OutPath} ({(info.Exists ? info.Length + " bytes" : "MISSING")}) mixed={mixedSeconds:F2}s requested={o.Seconds:F1}s");

            if (mixedSeconds < o.Seconds * 0.9)
            {
                Log.Line($"RESULT=PARTIAL code={ExitCaptureError} reason=short-mix mixed={mixedSeconds:F2}s");
                return ExitCaptureError;
            }
            Log.Line($"RESULT=OK code={ExitOk} mixed={mixedSeconds:F2}s");
            return ExitOk;
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
            if (appRecorder is not null)
            {
                try { appRecorder.StopRecording(); } catch { }
                await DisposeRecorder(appRecorder).ConfigureAwait(false);
            }
            if (micRecorder is not null)
            {
                try { micRecorder.StopRecording(); } catch { }
                await DisposeRecorder(micRecorder).ConfigureAwait(false);
            }
            micDevice?.Dispose();
            micDevices?.Dispose();
            renderDevice?.Dispose();
            renderDevices?.Dispose();
            target.Dispose();
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static bool TryResolveRenderDevice(MMDeviceEnumerator enumerator, string selector,
        out MMDevice? device, out MMDeviceCollection? collection, out string reason)
    {
        device = null;
        collection = null;
        reason = "";
        selector = selector.Trim('"', '\'');
        collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var names = new List<string>();
        for (int i = 0; i < collection.Count; i++)
        {
            var candidate = collection[i];
            names.Add($"{candidate.FriendlyName} [{candidate.ID}]");
            bool match = string.Equals(candidate.ID, selector, StringComparison.OrdinalIgnoreCase)
                         || candidate.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase);
            if (match && device is null) device = candidate;
            else candidate.Dispose();
        }
        if (device is not null) return true;

        reason = $"selector-not-matched selector=\"{selector}\" available: {string.Join(" | ", names)}";
        collection.Dispose();
        collection = null;
        return false;
    }

    private static bool TryResolveCaptureDevice(MMDeviceEnumerator enumerator, string selector,
        out MMDevice? device, out MMDeviceCollection? collection, out string reason)
    {
        device = null;
        collection = null;
        reason = "";
        selector = selector.Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(selector))
        {
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia, out device)) return true;
            reason = "no-default-capture-endpoint";
            return false;
        }

        collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var names = new List<string>();
        for (int i = 0; i < collection.Count; i++)
        {
            var candidate = collection[i];
            names.Add($"{candidate.FriendlyName} [{candidate.ID}]");
            bool match = string.Equals(candidate.ID, selector, StringComparison.OrdinalIgnoreCase)
                         || candidate.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase);
            if (match && device is null) device = candidate;
            else candidate.Dispose();
        }
        if (device is not null) return true;

        reason = $"selector-not-matched selector=\"{selector}\" available: {string.Join(" | ", names)}";
        collection.Dispose();
        collection = null;
        return false;
    }
}
