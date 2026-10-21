using System.Diagnostics;

namespace MicFuse;

/// <summary>
/// Holds deadline-miss events produced on the render thread until a low-priority sampler drains them.
/// The producer side must not allocate, lock, or touch the clock beyond one Stopwatch read, because
/// it runs inside the audio block loop where doing work is exactly what caused the miss.
/// </summary>
internal static class StarveWatch
{
    internal struct Event
    {
        public long Ticks;
        public long AgeTicks;
        /// <summary>Silence since the last packet arrived at the moment we ran dry — direct evidence of whether the source stalled.</summary>
        public long SincePacketTicks;
        public int PaddedFrames;
        public int BufferedBeforeFrames;
        public string Tag;
        /// <summary>Waveform jump across the inserted silence — what actually makes a click.</summary>
        public float Step;
        /// <summary>Loudest sample just before the gap, so a small step on a loud signal can be told from a big step on a quiet one.</summary>
        public float PeakBefore;
    }

    private const int Capacity = 512;
    private static readonly Event[] Ring = new Event[Capacity];
    private static int _write;      // single producer: every Read happens on the render thread
    private static int _read;
    private static long _lost;      // events overwritten before the sampler could drain them

    public static void Push(string tag, long ageTicks, long sincePacketTicks, int paddedFrames,
                            int bufferedBeforeFrames, float peakBefore, float step)
    {
        int w = _write;
        if (w - _read >= Capacity) { _read = w - Capacity + 1; _lost++; }
        Ring[w % Capacity] = new Event
        {
            Ticks = Stopwatch.GetTimestamp(),
            AgeTicks = ageTicks,
            SincePacketTicks = sincePacketTicks,
            PaddedFrames = paddedFrames,
            BufferedBeforeFrames = bufferedBeforeFrames,
            Tag = tag,
            PeakBefore = peakBefore,
            Step = step,
        };
        _write = w + 1;
    }

    /// <summary>Drains pending events. Called off the audio thread, so it may allocate.</summary>
    public static (List<Event> Events, long Lost) Drain()
    {
        var list = new List<Event>();
        while (_read < _write)
        {
            list.Add(Ring[_read % Capacity]);
            _read++;
        }
        long lost = _lost;
        _lost = 0;
        return (list, lost);
    }

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// One line per event. The context is whatever the caller can see without touching the audio
    /// thread — render queue depth, CPU, GC activity, thread and source counts.
    /// </summary>
    public static string Line(Event e, double rate, double uptimeSec, string tag, string context) =>
        $"{DateTime.Now:HH:mm:ss.fff} +{uptimeSec:F1}s {tag} " +
        $"gap={e.PaddedFrames * 1000.0 / rate:F1}ms({e.PaddedFrames}f) " +
        $"ring_before={e.BufferedBeforeFrames * 1000.0 / rate:F1}ms age={Ms(e.AgeTicks):F1}ms " +
        $"since_pkt={Ms(e.SincePacketTicks):F1}ms " +
        $"step={e.Step:F4} peak={e.PeakBefore:F3} {context}";
}
