using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

internal static partial class Program
{
    /// <summary>
    /// System loopback: captures the mix of the whole render endpoint. Used as the control
    /// path when comparing against per-process loopback.
    /// </summary>
    private static async Task<int> RunLoopback(Options o)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        MMDeviceCollection? devices = null;
        try
        {
            if (string.IsNullOrWhiteSpace(o.DeviceSelector))
            {
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device))
                {
                    Log.Line("RESULT=FAIL code=7 reason=no-default-render-endpoint");
                    return ExitDeviceNotFound;
                }
            }
            else
            {
                devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < devices.Count && device is null; i++)
                {
                    var d = devices[i];
                    if (d.FriendlyName.Contains(o.DeviceSelector, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.ID, o.DeviceSelector, StringComparison.OrdinalIgnoreCase))
                        device = d;
                    else d.Dispose();
                }
                if (device is null)
                {
                    Log.Line($"RESULT=FAIL code=7 reason=render-device-not-found selector=\"{o.DeviceSelector}\"");
                    return ExitDeviceNotFound;
                }
            }

            Log.Line($"system loopback device: {device.FriendlyName} id={device.ID} " +
                     $"volume={device.AudioEndpointVolume.MasterVolumeLevelScalar:F3} " +
                     $"volumeDb={device.AudioEndpointVolume.MasterVolumeLevel:F2} mute={device.AudioEndpointVolume.Mute}");

            WasapiRecorder recorder;
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithLoopbackCapture()
                    .WithBufferLength(o.BufferMs);
                if (!string.IsNullOrWhiteSpace(o.DeviceSelector)) builder = builder.WithDevice(device);
                recorder = builder.Build();
            }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitActivation} stage=build error={Describe(ex)}");
                return ExitActivation;
            }

            var wf = recorder.WaveFormat;
            Log.Line($"capture format: {wf}");
            var metrics = new CaptureMetrics(wf.BlockAlign, wf.SampleRate);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.OutPath))!);
            using var writer = new WaveFileWriter(o.OutPath, wf);
            var stopped = new TaskCompletionSource<Exception?>();
            Exception? threadError = null;

            recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
            {
                metrics.OnData(buffer, devicePosition, qpcPosition, (flags & AudioClientBufferFlags.Silent) != 0);
                if (!buffer.IsEmpty) writer.Write(buffer);
            };
            recorder.RecordingStopped += (_, e) =>
            {
                threadError = e.Exception;
                stopped.TrySetResult(e.Exception);
            };

            try { recorder.StartRecording(); }
            catch (Exception ex)
            {
                Log.Line($"RESULT=FAIL code={ExitStart} stage=start error={Describe(ex)}");
                return ExitStart;
            }

            var deadline = DateTime.UtcNow.AddSeconds(o.Seconds);
            var statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
            using var sampler = new ProcessSampler();
            while (DateTime.UtcNow < deadline && !_stop.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= statsDeadline)
                {
                    var (rss, cpu) = sampler.Sample();
                    Log.Line($"STATS {metrics.Stats(rss, cpu)}");
                    statsDeadline = DateTime.UtcNow.AddSeconds(o.StatsEverySeconds);
                }
                await Task.Delay(200).ConfigureAwait(false);
            }

            recorder.StopRecording();
            try { await stopped.Task.ConfigureAwait(false); } catch { }
            writer.Dispose();
            await DisposeRecorder(recorder).ConfigureAwait(false);

            Log.Line($"summary: packets={metrics.Packets} bytes={metrics.Bytes} audio={metrics.AudioSeconds:F2}s");
            if (threadError is not null)
            {
                Log.Line($"RESULT=FAIL code={ExitCaptureError} stage=capture-thread error={Describe(threadError)}");
                return ExitCaptureError;
            }
            Log.Line($"RESULT=OK code=0 captured={metrics.AudioSeconds:F2}s");
            return ExitOk;
        }
        finally
        {
            device?.Dispose();
            devices?.Dispose();
        }
    }
}
