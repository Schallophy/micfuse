using System.Diagnostics;

namespace MicFuse;

/// <summary>
/// Counters for the capture thread. Tracks gaps between packets using the WASAPI
/// device position and QPC stamps rather than wall-clock arrival, so a stalled
/// capture thread is distinguishable from a target that rendered nothing.
/// </summary>
internal sealed class CaptureMetrics
{
    private readonly int _bytesPerFrame;
    private readonly double _framesPerMs;

    public long Packets;
    public long SilentPackets;
    public long Bytes;
    public long Frames;

    private long _prevDevicePosition;
    private long _prevQpc;
    private bool _hasPrev;

    public double MaxDriftMs;
    public int GapEvents;
    public long SuspiciousTimestamps;
    private int _loggedSuspicious;
    public double MaxCallbackIntervalMs;
    private long _lastCallbackTicks = -1;
    private long _lastSilenceReportedMs;
    // Inter-arrival histogram: sizing a lossless buffer needs the distribution, not the maximum.
    private readonly int[] _intervalBuckets = new int[41];   // index i = (i-1, i] ms, 40 = "> 40 ms"
    private long _intervalCount;

    private readonly int _sampleRate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public CaptureMetrics(int bytesPerFrame, int sampleRate)
    {
        _bytesPerFrame = Math.Max(1, bytesPerFrame);
        _sampleRate = sampleRate;
        _framesPerMs = sampleRate / 1000.0;
    }

    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    public void OnData(ReadOnlySpan<byte> buffer, long devicePosition, long qpcPosition, bool silent)
    {
        Packets++;
        if (silent) SilentPackets++;
        Bytes += buffer.Length;
        Frames += buffer.Length / _bytesPerFrame;

        var nowTicks = _clock.ElapsedTicks;
        if (_lastCallbackTicks >= 0)
        {
            double intervalMs = (nowTicks - _lastCallbackTicks) * 1000.0 / Stopwatch.Frequency;
            if (intervalMs > MaxCallbackIntervalMs) MaxCallbackIntervalMs = intervalMs;
            int bucket = (int)Math.Ceiling(intervalMs);
            if (bucket < 0) bucket = 0;
            if (bucket > 40) bucket = 40;
            _intervalBuckets[bucket]++;
            _intervalCount++;
            if (intervalMs > 250 && (long)_clock.Elapsed.TotalMilliseconds - _lastSilenceReportedMs > 2000)
            {
                _lastSilenceReportedMs = (long)_clock.Elapsed.TotalMilliseconds;
                Log.Line($"WARN gap-in-callbacks interval={intervalMs:F1}ms (target likely rendered no audio)");
            }
        }
        _lastCallbackTicks = nowTicks;

        if (_hasPrev)
        {
            long framesAdvanced = devicePosition - _prevDevicePosition;
            double expectedMs = framesAdvanced / _framesPerMs;
            double actualMs = (qpcPosition - _prevQpc) / 10000.0;
            bool plausible = framesAdvanced >= 0 && actualMs > 0 && actualMs <= expectedMs * 4 + 100;
            if (plausible)
            {
                double drift = actualMs - expectedMs;
                if (Math.Abs(drift) > Math.Abs(MaxDriftMs)) MaxDriftMs = drift;
                if (actualMs - expectedMs > 100.0) GapEvents++;
            }
            else
            {
                SuspiciousTimestamps++;
                if (_loggedSuspicious++ < 5)
                {
                    Log.Line($"WARN implausible timestamps: devicePos {_prevDevicePosition}->{devicePosition} " +
                             $"qpc {_prevQpc}->{qpcPosition} expected={expectedMs:F3}ms actual={actualMs:F3}ms");
                }
            }
        }
        _prevDevicePosition = devicePosition;
        _prevQpc = qpcPosition;
        _hasPrev = true;
    }

    public double AudioSeconds => (double)Frames / _sampleRate;

    /// <summary>Callback interval distribution, in whole milliseconds. This is what sizes a lossless buffer.</summary>
    public string IntervalSummary()
    {
        if (_intervalCount == 0) return "intervals=0";
        int P(double q)
        {
            long target = (long)Math.Ceiling(q * _intervalCount);
            long acc = 0;
            for (int i = 0; i < _intervalBuckets.Length; i++)
            {
                acc += _intervalBuckets[i];
                if (acc >= target) return i;
            }
            return 40;
        }
        string tail = string.Join(" ", Enumerable.Range(0, _intervalBuckets.Length)
            .Where(i => _intervalBuckets[i] > 0 && (i >= 8 || _intervalBuckets[i] * 100 > _intervalCount))
            .Select(i => $"{(i == 40 ? "40+" : i.ToString())}ms:{_intervalBuckets[i]}"));
        return $"intervals={_intervalCount} p50<={P(0.50)}ms p99<={P(0.99)}ms p999<={P(0.999)}ms " +
               $"max={MaxCallbackIntervalMs:F1}ms buckets[{tail}]";
    }

    public string Stats(double rssMb, double cpuPercent)
    {
        double elapsed = ElapsedSeconds;
        double coverage = elapsed > 0 ? AudioSeconds / elapsed * 100.0 : 0;
        return $"packets={Packets} silent={SilentPackets} bytes={Bytes} audio={AudioSeconds:F2}s " +
               $"elapsed={elapsed:F1}s coverage={coverage:F1}% maxDrift={MaxDriftMs:+0.0;-0.0;0.0}ms " +
               $"maxGapBetweenCallbacks={MaxCallbackIntervalMs:F1}ms gapEvents={GapEvents} badTimestamps={SuspiciousTimestamps} " +
               $"rss={rssMb:F1}MB cpu={cpuPercent:F1}%";
    }
}

internal sealed class ProcessSampler : IDisposable
{
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastWall;

    public ProcessSampler()
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastWall = DateTime.UtcNow;
    }

    public (double RssMb, double CpuPercent) Sample()
    {
        _self.Refresh();
        var nowWall = DateTime.UtcNow;
        var nowCpu = _self.TotalProcessorTime;
        double wallMs = (nowWall - _lastWall).TotalMilliseconds;
        double cpuMs = (nowCpu - _lastCpu).TotalMilliseconds;
        _lastWall = nowWall;
        _lastCpu = nowCpu;
        double cores = Environment.ProcessorCount;
        double cpuPercent = wallMs > 0 ? cpuMs / (wallMs * cores) * 100.0 : 0;
        return (_self.WorkingSet64 / 1048576.0, cpuPercent);
    }

    public void Dispose() => _self.Dispose();
}
