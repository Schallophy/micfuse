using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

internal static partial class Program
{
    private static async Task<int> RunMic(Options o)
    {
        if (o.Cycles <= 1) return await RunMicOnce(o, o.OutPath);

        Log.Line($"mic cycle test: {o.Cycles} cycles of {o.Seconds}s device=\"{o.DeviceSelector}\"");
        int last = 0;
        for (int cycle = 1; cycle <= o.Cycles; cycle++)
        {
            string outPath = $"{o.OutPath}.cycle{cycle:D3}.wav";
            Log.Line($"CYCLE {cycle}/{o.Cycles} begin {Resources()}");
            last = await RunMicOnce(o, outPath);
            var beforeGc = Resources();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Log.Line($"CYCLE {cycle}/{o.Cycles} end exit={last} {beforeGc} | afterGC {Resources()}");
            if (last is not (0 or ExitDeviceGone))
            {
                Log.Line($"CYCLE {cycle} FAILED, stopping mic cycle test; WAV kept at {outPath}");
                return last;
            }
            try { File.Delete(outPath); } catch { }
        }
        Log.Line($"MIC CYCLE RESULT=OK cycles={o.Cycles} all completed {Resources()}");
        return 0;
    }

    private static async Task<int> RunMicOnce(Options o, string outPath)
    {
        Log.Line($"request: source=mic device=\"{o.DeviceSelector}\" seconds={o.Seconds:F1} out={outPath} " +
                 $"formatRequested={(o.NoFormatOverride ? "native" : $"{o.SampleRate}Hz/{o.Channels}ch/{(o.Pcm16 ? "pcm16" : "float32")}")} bufferMs={o.BufferMs}");

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        MMDeviceCollection? collection = null;
        string resolved;
        try
        {
            if (string.IsNullOrWhiteSpace(o.DeviceSelector))
            {
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia, out device))
                {
                    Log.Line($"RESULT=FAIL code={ExitDeviceNotFound} stage=device reason=no-default-capture-endpoint");
                    return ExitDeviceNotFound;
                }
                resolved = "default";
            }
            else
            {
                collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var names = new List<string>();
                for (int i = 0; i < collection.Count && device is null; i++)
                {
                    var candidate = collection[i];
                    names.Add($"{candidate.FriendlyName} [{candidate.ID}]");
                    bool match = string.Equals(candidate.ID, o.DeviceSelector, StringComparison.OrdinalIgnoreCase)
                                 || candidate.FriendlyName.Contains(o.DeviceSelector, StringComparison.OrdinalIgnoreCase);
                    if (match) device = candidate;
                    else candidate.Dispose();
                }
                if (device is null)
                {
                    Log.Line($"RESULT=FAIL code={ExitDeviceNotFound} stage=device reason=selector-not-matched selector=\"{o.DeviceSelector}\"");
                    foreach (var n in names) Log.Line($"  available capture device: {n}");
                    return ExitDeviceNotFound;
                }
                resolved = "selector";
            }

            Log.Line($"device: {device.FriendlyName} id={device.ID} state={device.State} resolvedBy={resolved}");

            var builder = new WasapiRecorderBuilder().WithDevice(device).WithBufferLength(o.BufferMs)
                .WithMmcssThreadPriority("Pro Audio")
                .WithLowLatency(o.LowLatency);
            if (!o.NoFormatOverride)
            {
                var fmt = o.Pcm16
                    ? new WaveFormat(o.SampleRate, 16, o.Channels)
                    : WaveFormat.CreateIeeeFloatWaveFormat(o.SampleRate, o.Channels);
                builder = builder.WithFormat(fmt);
            }

            WasapiRecorder recorder;
            try
            {
                recorder = builder.Build();
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitActivation} stage=build error={Describe(ex)}");
                return ExitActivation;
            }

            var wf = recorder.WaveFormat;
            Log.Line($"capture format: {wf} | encoding={wf.Encoding} rate={wf.SampleRate} channels={wf.Channels} bits={wf.BitsPerSample} blockAlign={wf.BlockAlign}");
            LogLowLatency("mic", recorder, o.LowLatency);

            var metrics = new CaptureMetrics(wf.BlockAlign, wf.SampleRate);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

            WaveFileWriter? writer = null;
            var stopped = new TaskCompletionSource<Exception?>();
            Exception? captureThreadError = null;
            Exception? writerError = null;
            int deviceGoneExit = -1;

            try
            {
                writer = new WaveFileWriter(outPath, wf);
                var w = writer;
                Exception? we = null;

                recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
                {
                    if (we is not null) return;
                    metrics.OnData(buffer, devicePosition, qpcPosition, (flags & AudioClientBufferFlags.Silent) != 0);
                    try { if (!buffer.IsEmpty) w.Write(buffer); }
                    catch (Exception ex)
                    {
                        we = ex;
                        writerError = ex;
                        Log.Line($"ERROR writing WAV: {Describe(ex)}");
                        _stop.Cancel();
                    }
                };
                recorder.RecordingStopped += (_, e) =>
                {
                    captureThreadError = e.Exception;
                    stopped.TrySetResult(e.Exception);
                };

                try
                {
                    recorder.StartRecording();
                }
                catch (Exception ex)
                {
                    Log.Line($"RESULT=FAIL code={ExitStart} stage=start error={Describe(ex)}");
                    return ExitStart;
                }
                Log.Line("state=CAPTURING mic");

                var deadline = DateTime.UtcNow.AddSeconds(o.Seconds);
                var statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
                using var sampler = new ProcessSampler();

                while (true)
                {
                    if (DateTime.UtcNow >= deadline || _stop.IsCancellationRequested || stopped.Task.IsCompleted) break;

                    if (device.State != DeviceState.Active)
                    {
                        Log.Line($"device state changed to {device.State} at t={metrics.ElapsedSeconds:F1}s");
                        deviceGoneExit = ExitDeviceGone;
                        break;
                    }

                    if (DateTime.UtcNow >= statsDeadline)
                    {
                        var (rss, cpu) = sampler.Sample();
                        Log.Line($"STATS {metrics.Stats(rss, cpu)}");
                        statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                }

                recorder.StopRecording();
                try { await stopped.Task.ConfigureAwait(false); } catch (Exception) { }
                Log.Line("state=STOPPED");

                Log.Line("teardown: writer disposing");
                writer.Dispose();
                writer = null;
                await DisposeRecorder(recorder).ConfigureAwait(false);

                return ReportMic(o, outPath, device, metrics, deviceGoneExit, captureThreadError, writerError);
            }
            finally
            {
                try { writer?.Dispose(); } catch { }
            }
        }
        finally
        {
            device?.Dispose();
            collection?.Dispose();
        }
    }

    private static int ReportMic(Options o, string outPath, MMDevice device, CaptureMetrics m,
        int deviceGoneExit, Exception? captureThreadError, Exception? writerError)
    {
        var info = new FileInfo(outPath);
        double audioSeconds = m.AudioSeconds;
        Log.Line($"device: {device.FriendlyName}");
        Log.Line($"summary: packets={m.Packets} silentPackets={m.SilentPackets} bytes={m.Bytes} " +
                 $"audio={audioSeconds:F2}s elapsed={m.ElapsedSeconds:F2}s coverage={(audioSeconds / Math.Max(0.001, m.ElapsedSeconds) * 100):F1}% " +
                 $"maxDrift={m.MaxDriftMs:+0.0;-0.0;0.0}ms maxCallbackGap={m.MaxCallbackIntervalMs:F1}ms gapEvents={m.GapEvents}");
        Log.Line("callbacks: " + m.IntervalSummary());
        Log.Line($"wav: {outPath} ({(info.Exists ? info.Length + " bytes" : "MISSING")})");

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
        if (deviceGoneExit >= 0)
        {
            Log.Line($"RESULT=STOPPED code={deviceGoneExit} reason=capture-device-unavailable");
            return deviceGoneExit;
        }
        if (m.Packets == 0)
        {
            Log.Line($"RESULT=NO_DATA code={ExitOk} reason=no-buffers-received (muted device or OS limitation)");
            return ExitOk;
        }
        Log.Line($"RESULT=OK code={ExitOk} captured={audioSeconds:F2}s of {o.Seconds:F1}s requested");
        return ExitOk;
    }
}
