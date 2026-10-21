using System.Text.Json;

namespace MicFuse;

/// <summary>
/// The one piece of state worth carrying across launches: which microphone was chosen. Stored under
/// %APPDATA% because a non-admin account can write there. The output device is deliberately not
/// stored — it has a documented auto rule (prefer CABLE Input) — and enabled sources are not either,
/// because a process id means nothing after a reboot.
/// </summary>
public sealed class Settings
{
    public string MicId { get; set; } = "";
    public float MicGain { get; set; } = 1f;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string StorePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicFuse", "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(StorePath), FileJson) ?? new Settings();
        }
        catch (Exception)
        {
            return new Settings();   // first run, or an unreadable file: fall back to "no microphone"
        }
    }

    public void SetMic(string deviceId, float gain)
    {
        if (MicId == deviceId && Math.Abs(MicGain - gain) < 0.0001f) return;
        MicId = deviceId;
        MicGain = gain;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, FileJson));
        }
        catch (Exception ex)
        {
            Log.Line("settings save failed: " + ex.Message);
        }
    }
}
