using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicFuse;

internal static class Devices
{
    public static int ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
            Log.Line($"--- {flow} endpoints ({devices.Count} including disabled/not-present) ---");
            foreach (var d in devices)
            {
                string mix;
                try
                {
                    using var client = d.CreateAudioClient();
                    mix = client.MixFormat.ToString();
                }
                catch (Exception ex) { mix = "mix format unavailable: " + ex.GetType().Name; }
                string vol;
                try
                {
                    vol = $"{d.AudioEndpointVolume.MasterVolumeLevelScalar:F2} mute={d.AudioEndpointVolume.Mute}";
                }
                catch (Exception ex) { vol = "unavailable: " + ex.GetType().Name; }
                Log.Line($"  name={d.FriendlyName}");
                Log.Line($"      id={d.ID}");
                Log.Line($"      state={d.State} instanceId={d.InstanceId}");
                Log.Line($"      mixFormat={mix}");
                Log.Line($"      volume={vol}");
                d.Dispose();
            }
            devices.Dispose();
        }

        Log.Line("--- default endpoints ---");
        foreach (var (flow, role) in new (DataFlow, Role)[]
        {
            (DataFlow.Render, Role.Console),
            (DataFlow.Render, Role.Multimedia),
            (DataFlow.Render, Role.Communications),
            (DataFlow.Capture, Role.Console),
            (DataFlow.Capture, Role.Multimedia),
            (DataFlow.Capture, Role.Communications),
        })
        {
            if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out var dev))
            {
                Log.Line($"  {flow}/{role}: {dev.FriendlyName}");
                dev.Dispose();
            }
            else
            {
                Log.Line($"  {flow}/{role}: none");
            }
        }
        return 0;
    }

    /// <summary>
    /// Lists the render sessions on the default output device with the peak level observed
    /// over a short window, so a target PID can be chosen from what is actually making sound.
    /// </summary>
    public static async Task<int> ListAudioSessions(double sampleSeconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
        {
            Log.Line("no default render endpoint found");
            return 1;
        }
        Log.Line($"render device: {device.FriendlyName}");
        Log.Line($"device peak: {device.AudioMeterInformation.MasterPeakValue:F4}");
        Log.Line($"endpoint volume: {device.AudioEndpointVolume.MasterVolumeLevelScalar:F3} muted={device.AudioEndpointVolume.Mute}");

        using var manager = device.AudioSessionManager;
        var peaks = new Dictionary<string, double>();
        var meta = new Dictionary<string, (int Pid, string Name, string Display, bool System, AudioSessionState State, float Volume, bool Muted)>();
        var idPresence = new Dictionary<string, int>();

        int samples = Math.Max(1, (int)(sampleSeconds / 0.1));
        for (int i = 0; i < samples; i++)
        {
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int s = 0; s < sessions.Count; s++)
            {
                var session = sessions[s];
                try
                {
                    int pid = 0;
                    try
                    {
                        pid = unchecked((int)session.GetProcessID);
                    }
                    catch (Exception) { }
                    // Key by pid: session identifiers are not guaranteed unique, and merging
                    // on them attributes one process's energy to whichever pid was seen first.
                    string key = pid.ToString();
                    string? identifier = session.GetSessionIdentifier;
                    idPresence.TryAdd(key, 0);
                    if (identifier is not null) idPresence[key]++;

                    double peak = session.AudioMeterInformation?.MasterPeakValue ?? 0.0;
                    peaks.TryGetValue(key, out double best);
                    peaks[key] = Math.Max(best, peak);
                    if (!meta.ContainsKey(key))
                    {
                        string name = "(unknown)";
                        try { if (pid > 0) name = Process.GetProcessById(pid).ProcessName; }
                        catch (Exception) { }
                        float volume = -1;
                        bool muted = false;
                        try
                        {
                            volume = session.SimpleAudioVolume?.Volume ?? -1;
                            muted = session.SimpleAudioVolume?.Mute ?? false;
                        }
                        catch (Exception) { }
                        meta[key] = (pid, name, session.DisplayName ?? "", session.IsSystemSoundsSession, session.State, volume, muted);
                    }
                }
                catch (Exception ex)
                {
                    Log.Line($"  session access failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    session.Dispose();
                }
            }
            await Task.Delay(100);
        }

        Log.Line($"--- render sessions (peak over {sampleSeconds:F1}s) ---");
        var rows = meta.OrderBy(m => m.Value.Pid).ToList();
        foreach (var (key, value) in rows)
        {
            double peak = peaks.TryGetValue(key, out var p) ? p : 0.0;
            int withId = idPresence.TryGetValue(key, out var c) ? c : 0;
            Log.Line($"  pid={value.Pid,-7} name={value.Name,-22} state={value.State,-8} peak={peak:F4} " +
                     $"sessionsWithId={withId} vol={value.Volume:F2} mute={value.Muted} systemSounds={value.System} display={value.Display}");
        }
        Log.Line($"total sessions={rows.Count}");
        device.Dispose();
        return 0;
    }
}
