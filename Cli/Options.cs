using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;

namespace MicFuse;

internal sealed class Options
{
    public int Pid;
    public bool Mic;
    public bool Mix;
    public bool NoMic;
    public bool NoWav;
    public bool Loopback;
    public double AppGain = 1.0;
    public double MicGain = 1.0;
    public string OutDeviceSelector = "";
    public double TestToneHz;
    public string DeviceSelector = "";
    public ProcessLoopbackMode Mode = ProcessLoopbackMode.IncludeTargetProcessTree;
    public double Seconds = 10;
    public string OutPath = "";
    public string LogPath = "";
    public int SampleRate = 48000;
    public int Channels = 2;
    public bool Pcm16 = true;
    public int BufferMs = 100;
    public double StatsEverySeconds = 5;
    public int Cycles = 1;
    public bool ListDevices;
    public bool ListSessions;
    public bool Web;
    public bool Tray;
    public bool Probe;
    public double ProbeDriftPpm;
    public bool ProbeOpenLoop;
    public bool LowLatency;
    public int Port = 8765;
    public bool NoBrowser;
    public string SetVolumeSelector = "";
    public float SetVolumeValue = -1f;
    public bool NoFormatOverride;

    public static Options? Parse(string[] args, out int exitCode)
    {
        exitCode = 0;
        var o = new Options();
        if (args.Length == 0) o.Tray = true;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next(string name) => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgsException($"missing value for {name}");
                switch (a)
                {
                    case "--pid": o.Pid = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--mic": o.Mic = true; break;
                    case "--mix": o.Mix = true; break;
                    case "--no-mic": o.NoMic = true; break;
                    case "--no-wav": o.NoWav = true; break;
                    case "--loopback": o.Loopback = true; break;
                    case "--app-gain": o.AppGain = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--mic-gain": o.MicGain = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--out-device": o.OutDeviceSelector = Next(a); break;
                    case "--test-tone": o.TestToneHz = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--device": o.DeviceSelector = Next(a); break;
                    case "--seconds": o.Seconds = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--out": o.OutPath = Next(a); break;
                    case "--log": o.LogPath = Next(a); break;
                    case "--rate": o.SampleRate = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--channels": o.Channels = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--float32": o.Pcm16 = false; break;
                    case "--native-format": o.NoFormatOverride = true; break;
                    case "--buffer-ms": o.BufferMs = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--stats-every": o.StatsEverySeconds = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--cycles": o.Cycles = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--exclude": o.Mode = ProcessLoopbackMode.ExcludeTargetProcessTree; break;
                    case "--list-devices": o.ListDevices = true; break;
                    case "--list-audio-sessions": o.ListSessions = true; break;
                    case "--web": o.Web = true; break;
                    case "--probe": o.Probe = true; break;
                    case "--probe-drift": o.ProbeDriftPpm = double.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--probe-open-loop": o.ProbeOpenLoop = true; break;
                    case "--low-latency": o.LowLatency = true; break;
                    case "--tray": o.Tray = true; break;
                    case "--port": o.Port = int.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--no-browser": o.NoBrowser = true; break;
                    case "--set-volume": o.SetVolumeSelector = Next(a); o.SetVolumeValue = float.Parse(Next(a), CultureInfo.InvariantCulture); break;
                    case "--help": throw new ArgsException(null);
                    default: throw new ArgsException($"unknown argument: {a}");
                }
            }
            if (o.Tray) o.Web = true;
            if (o.SetVolumeValue >= 0)
            {
                if (string.IsNullOrWhiteSpace(o.LogPath)) o.LogPath = Path.Combine(Path.GetTempPath(), "MicFuse-set-volume.log");
                return o;
            }
            if (o.ListDevices || o.ListSessions || o.Web || o.Probe)
            {
                if (string.IsNullOrWhiteSpace(o.LogPath))
                {
                    string mode = o.ListDevices ? "devices" : o.ListSessions ? "sessions" : o.Probe ? "probe" : "web";
                    o.LogPath = Path.Combine(Path.GetTempPath(), $"MicFuse-{mode}.log");
                }
                return o;
            }
            if (o.Loopback)
            {
                if (o.Pid > 0 || o.Mic || o.Mix) throw new ArgsException("--loopback cannot be combined with --pid/--mic/--mix");
                if (string.IsNullOrWhiteSpace(o.OutPath)) throw new ArgsException("--out is required");
                if (string.IsNullOrWhiteSpace(o.LogPath)) o.LogPath = Path.ChangeExtension(o.OutPath, ".log");
                return o;
            }
            if (o.Mic && o.Mix) throw new ArgsException("--mic and --mix are mutually exclusive");
            if (o.Mix)
            {
                if (o.Pid <= 0) throw new ArgsException("--mix requires --pid");
            }
            else if (o.Mic)
            {
                if (o.Pid > 0) throw new ArgsException("--mic and --pid are mutually exclusive");
            }
            else if (o.Pid <= 0) throw new ArgsException("either --pid, --mic or --mix is required");
            if (o.AppGain < 0 || o.MicGain < 0) throw new ArgsException("gains must be >= 0");
            if (o.Seconds <= 0) throw new ArgsException("--seconds must be > 0");
            if (o.Cycles < 1) throw new ArgsException("--cycles must be >= 1");
            if (string.IsNullOrWhiteSpace(o.OutPath))
                throw new ArgsException("--out is required");
            if (string.IsNullOrWhiteSpace(o.LogPath))
                o.LogPath = Path.ChangeExtension(o.OutPath, ".log");
        }
        catch (ArgsException ex)
        {
            if (ex.Message is not null) Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(Usage);
            exitCode = ex.Message is null ? 0 : 1;
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: bad argument value: " + ex.Message);
            Console.Error.WriteLine(Usage);
            exitCode = 1;
            return null;
        }
        return o;
    }

    public static string Usage => string.Join('\n',
        "MicFuse - mix selected application audio and the microphone into a virtual microphone",
        "",
        "  --pid <n>             target process id (required unless --mic)",
        "  --mic                 capture a microphone (capture endpoint) instead of a process",
        "  --mix                 mix the target process with a microphone into one WAV (M3)",
        "  --loopback            capture the whole default render endpoint (control path for diagnostics)",
        "  --no-mic              with --mix, capture the process only",
        "  --no-wav              with --mix, do not write audio (long endurance runs)",
        "  --app-gain <g>        linear gain for the process stream (default 1.0)",
        "  --mic-gain <g>        linear gain for the microphone stream (default 1.0)",
        "  --out-device <id|name> with --mix, render the mix to this device in real time (e.g. \"CABLE Input\")",
        "                        instead of the timer-driven file writer",
        "  --test-tone <hz>      TEST ONLY: add a 0.05-amplitude sine at the mixer output (no speakers needed)",
        "  --device <id|name>    capture device: id, friendly-name substring, or empty for default",
        "  --out <file.wav>      WAV output path (required)",
        "  --log <file.log>      log path (default: output path with .log)",
        "  --seconds <n>         capture duration (default 10)",
        "  --exclude             capture everything EXCEPT the target process tree",
        "  --rate <hz>           requested sample rate (default 48000)",
        "  --channels <n>        requested channel count (default 2)",
        "  --float32             request IEEE float 32-bit instead of PCM 16-bit",
        "  --native-format       do not request a format (NAudio process-loopback default is 44.1k stereo float)",
        "  --buffer-ms <n>       capture buffer length (default 100)",
        "  --stats-every <sec>   stats interval (default 5)",
        "  --cycles <n>          repeat the whole activate/capture/stop/dispose sequence n times in one",
        "                        process, logging rss/handles/threads each cycle (default 1)",
        "  --list-devices        enumerate render/capture endpoints and exit",
        "  --web                 serve the local web UI (default http://127.0.0.1:8765/)",
        "  --probe               DIAGNOSTIC: inject tone bursts into the app and mic rings, loopback-capture",
        "                        --out-device and report per-burst latency, jitter and cadence (see --seconds)",
        "  --probe-drift <ppm>   with --probe, make the injection clock deliberately fast by ppm (calibration:",
        "                        the measured slope should equal ppm/1000 ms per second when control is off)",
        "  --probe-open-loop     with --probe, disable the adaptive resampler (calibration baseline)",
        "  --low-latency         DIAGNOSTIC: ask the capture endpoint for IAudioClient3 low latency and log",
        "                        what it actually grants (packet length and gap stats then show the change)",
        "  --tray                add a system-tray entry; implies --web (default with no args)",
        "  --port <n>            web UI port (default 8765; increments if busy)",
        "  --no-browser          do not open the browser automatically",
        "  --set-volume <dev> <0..1>  diagnostic: set a render endpoint volume (no admin needed)",
        "  --list-audio-sessions list render sessions on the default output device with peak levels, and exit",
        "                        (use --seconds to change the sampling window, default 10s)");

    private sealed class ArgsException : Exception
    {
        public ArgsException(string? message) : base(message ?? "") { }
    }
}

internal static class Log
{
    private static StreamWriter? _file;
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Init(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public static void Line(string message)
    {
        lock (Gate)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [+{Clock.Elapsed.TotalSeconds:F1}s] {message}";
            Console.Out.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
        }
    }
}
