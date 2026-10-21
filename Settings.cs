using System.Text.Json;

namespace MicFuse;

public sealed class LineSetting
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
}

/// <summary>A remembered route from the old schema, kept only so an existing settings file migrates.</summary>
public sealed class RouteSetting
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class LineRoutesSetting
{
    public int LineId { get; set; }
    public List<RouteSetting> Routes { get; set; } = new();
}

/// <summary>A source the user added, with the lines it feeds. No process id: only what survives a reboot.</summary>
public sealed class SourceSetting
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public float Gain { get; set; } = 1f;
    public List<int> Lines { get; set; } = new();
}

/// <summary>
/// The state worth carrying across launches: which microphone, which output lines exist, which lines
/// the microphone feeds, and which sources the user added along with their lines and gains. Stored under
/// %APPDATA% because a non-admin account can write there. Nothing is ever picked for the user, and a
/// process id means nothing after a reboot, so a remembered source is an executable path (falling back
/// to the process name when the path is unreadable).
/// </summary>
public sealed class Settings
{
    public string MicId { get; set; } = "";
    public float MicGain { get; set; } = 1f;
    public List<LineSetting> Lines { get; set; } = new();
    public List<int> MicRoutes { get; set; } = new();
    public List<SourceSetting> Sources { get; set; } = new();
    /// <summary>Legacy: read once, folded into <see cref="Sources"/>, then dropped from the file.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<LineRoutesSetting>? LineRoutes { get; set; } = new();

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string StorePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicFuse", "settings.json");

    public static Settings Load()
    {
        Settings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(StorePath), FileJson) ?? new Settings();
        }
        catch (Exception)
        {
            return new Settings();   // first run, or an unreadable file: fall back to "no microphone, no lines"
        }
        loaded.MigrateLegacyRoutes();
        return loaded;
    }

    /// <summary>The previous file stored routes per line; fold that into the source list so an upgrade
    /// keeps what the user had.</summary>
    private void MigrateLegacyRoutes()
    {
        if (LineRoutes is null) return;
        foreach (var entry in LineRoutes)
        foreach (var route in entry.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Name)) continue;
            var source = Sources.FirstOrDefault(s =>
                string.Equals(s.Name, route.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Path, route.Path, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                source = new SourceSetting { Name = route.Name, Path = route.Path, Title = route.Name };
                Sources.Add(source);
            }
            if (!source.Lines.Contains(entry.LineId)) source.Lines.Add(entry.LineId);
        }
        LineRoutes = null;
    }

    public void SetMic(string deviceId, float gain)
    {
        if (MicId == deviceId && Math.Abs(MicGain - gain) < 0.0001f) return;
        MicId = deviceId;
        MicGain = gain;
        Save();
    }

    public void SetLines(IEnumerable<(int Id, string DeviceId)> lines)
    {
        var next = lines.Select(l => new LineSetting { Id = l.Id, DeviceId = l.DeviceId }).ToList();
        if (next.Count == Lines.Count &&
            next.Zip(Lines).All(pair => pair.First.Id == pair.Second.Id && pair.First.DeviceId == pair.Second.DeviceId))
            return;
        Lines = next;
        // A route to a line that no longer exists can never be re-established.
        MicRoutes = MicRoutes.Where(id => next.Any(l => l.Id == id)).ToList();
        foreach (var source in Sources)
            source.Lines = source.Lines.Where(id => next.Any(l => l.Id == id)).ToList();
        Save();
    }

    public void SetSources(IEnumerable<(string Name, string Path, string Title, float Gain, int[] Lines)> sources)
    {
        var next = sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new SourceSetting
            {
                Name = s.Name,
                Path = s.Path ?? "",
                Title = s.Title ?? "",
                Gain = s.Gain,
                Lines = (s.Lines ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToList(),
            })
            .ToList();
        bool same = next.Count == Sources.Count && next.Zip(Sources).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal) &&
            string.Equals(pair.First.Title, pair.Second.Title, StringComparison.Ordinal) &&
            Math.Abs(pair.First.Gain - pair.Second.Gain) < 0.0001f &&
            pair.First.Lines.SequenceEqual(pair.Second.Lines));
        if (same) return;
        Sources = next;
        Save();
    }

    public void SetMicRoutes(IEnumerable<int> lineIds)
    {
        var next = lineIds.Distinct().OrderBy(id => id).ToList();
        if (next.Count == MicRoutes.Count && next.Zip(MicRoutes).All(pair => pair.First == pair.Second)) return;
        MicRoutes = next;
        Save();
    }

    private void Save()
    {
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
