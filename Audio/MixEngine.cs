namespace MicFuse;

/// <summary>Everything that happened to one source's ring: what is buffered and where the audio went.</summary>
internal readonly record struct BufferStats
{
    public double BufferedMs { get; init; }
    public long StarvedFrames { get; init; }
    public long IdleFrames { get; init; }
    public long DroppedFrames { get; init; }
    /// <summary>Current read-ratio deviation of the adaptive resampler, in parts per million.</summary>
    public int RatioPpm { get; init; }
}

/// <summary>
/// Pulls the mix from the audio device's clock (WasapiPlayer) and optionally mirrors it to WAV.
/// Using the device as the clock removes the timer-vs-device drift of the file-only path.
/// </summary>
internal sealed class MixerProvider : NAudio.Wave.IWaveProvider
{
    private readonly Mixer _mixer;
    private readonly NAudio.Wave.WaveFileWriter? _writer;
    private readonly float[] _scratch;

    public NAudio.Wave.WaveFormat WaveFormat { get; }

    public MixerProvider(Mixer mixer, NAudio.Wave.WaveFormat format, NAudio.Wave.WaveFileWriter? writer, int maxBlockFrames)
    {
        _mixer = mixer;
        _writer = writer;
        WaveFormat = format;
        _scratch = new float[maxBlockFrames * format.Channels];
    }

    public int Read(Span<byte> buffer)
    {
        int blockAlign = WaveFormat.BlockAlign;
        int frames = buffer.Length / blockAlign;
        int used = frames * blockAlign;
        int maxFrames = _scratch.Length / WaveFormat.Channels;
        if (frames > maxFrames)
        {
            frames = maxFrames;
            used = frames * blockAlign;
        }
        var dest = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..used]);
        try
        {
            _mixer.Mix(dest);
            _writer?.Write(buffer[..used]);
        }
        catch (Exception ex)
        {
            // Never hand a half-written block to the endpoint: one block of silence beats garbage, and
            // the try costs nothing on the normal path.
            dest.Clear();
            if (_mixer.BackstopErrors++ == 0)
                Log.Line("WARN mix backstop (silence for one block): " + ex.GetType().Name + ": " + ex.Message);
        }
        if (used < buffer.Length) buffer[used..].Clear();
        return buffer.Length;
    }
}

/// <summary>
/// Fixed-capacity float ring buffer for one capture source. Overflow drops the oldest
/// frames and underrun returns silence, so the mixer can never build an unbounded
/// queue or stall the capture thread.
/// </summary>
internal sealed class SourceBuffer
{
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly object _gate = new();
    private int _read;
    private int _countFrames;
    private readonly int _capacityFrames;

    /// <summary>Frames discarded because the ring was full — the source clock outruns the render clock.</summary>
    public long DroppedFrames;
    /// <summary>
    /// Frames we had to pad although a packet arrived moments ago: our side missed the deadline.
    /// This is the only one of the two padding counters that means "glitch".
    /// </summary>
    public long StarvedFrames;
    /// <summary>Frames padded because nothing had arrived — the source is silent, paused, or not started.</summary>
    public long IdleFrames;
    private long _lastWriteTicks;
    private float _lastWritePeak;
    private long _lastReadTicks;
    private long _arrivalAgeTicks;
    private long _firstWriteTicks;
    private readonly string _tag;
    private readonly int _sampleRate;
    // Target level for the lossless guarantee. Measured on this machine: arrivals come in 10 ms
    // bursts (mic 2x240 frames back to back), so the level sawtooths by one burst around the target.
    // Worst-phase margin against a scheduling stall = target - 5 ms (half sawtooth) - 1 block
    // (2.67 ms): 16 ms buys ~8.3 ms of stall tolerance, 12 ms only ~4.3 ms. Measured idle tail is
    // ~1 ms, so 16 ms is insurance for load, at a cost of 4 ms of latency.
    private readonly int _targetFrames;
    private readonly bool _levelControl;
    private bool _primedReady;
    private double _levelAvg;
    private double _frac;          // fractional read position inside the current frame
    private double _ratio = 1.0;   // current resampling ratio, 1.0 = read as fast as data arrives
    private double _ratioBias;     // integral term, accumulates the ppm needed to cancel clock drift
    // ~130 ms time constant at 375 blocks/s: kills the 100 Hz arrival sawtooth, tracks real drift.
    private const double LevelSmoothing = 0.02;
    private const double RatioKp = 1e-3;
    private const double RatioKi = 1e-6;
    private const double MaxRatioDev = 2e-3;   // ±2000 ppm, 100x more than the drift it must cancel
    // A gap is recorded as one event, and only once it closes: the sample that follows the silence is
    // what fixes the size of the discontinuity, and that is what makes it audible.
    private readonly float[] _gapTail;
    private bool _gapOpen;
    private int _gapPaddedFrames, _gapBufferedBefore;
    private long _gapAgeTicks, _gapSincePacketTicks;
    private float _gapPeak;
    private static readonly long StarveWindowTicks = 12L * System.Diagnostics.Stopwatch.Frequency / 1000; // 1.2 packets
    // A paused app stops sending packets entirely. On resume the ring is empty, so treat it as a fresh
    // start and prebuffer again instead of counting the rebuild as deadline misses.
    private static readonly long ReArmTicks = 50L * System.Diagnostics.Stopwatch.Frequency / 1000;
    // A splice whose jump stays below this cannot be heard (the same threshold the probe uses for gap
    // severity), so such gaps are accounted as idle rather than as deadline misses.
    private const float StepThreshold = 0.02f;
    // 5 ms ramps at both ends of a gap: ~44 dB down at 10 kHz, so the splice cannot be heard, while
    // staying far short of the ~20 ms where a fade starts to be perceived as a dip.
    private const int FadeFrames = 240;
    // A source that has just joined always runs dry for its first packet or two; that ramp is not a
    // miss. It is bounded by time, not by ring level, so a chronically under-buffered pipeline —
    // which never fills at all — still reports its misses.
    private static readonly long RampTicks = 50L * System.Diagnostics.Stopwatch.Frequency / 1000;

    public SourceBuffer(int capacityFrames, int channels, int sampleRate, string tag = "", bool levelControl = true)
    {
        _capacityFrames = capacityFrames;
        _channels = channels;
        _sampleRate = sampleRate;
        _targetFrames = sampleRate * 16 / 1000;
        _levelControl = levelControl;
        _tag = tag;
        _gapTail = new float[channels];
        _samples = new float[capacityFrames * channels];
    }

    public int BufferedFrames
    {
        get { lock (_gate) return _countFrames; }
    }

    /// <summary>How far the read ratio currently deviates from 1:1, in parts per million.</summary>
    public int RatioPpm => (int)Math.Round((_ratio - 1.0) * 1e6);

    /// <summary>Catmull-Rom: exact at integer positions, smooth in between, four taps.</summary>
    private static float Cubic(float x0, float x1, float x2, float x3, double d)
    {
        double a = -0.5 * x0 + 1.5 * x1 - 1.5 * x2 + 0.5 * x3;
        double b = x0 - 2.5 * x1 + 2.0 * x2 - 0.5 * x3;
        double c = -0.5 * x0 + 0.5 * x2;
        return (float)(((a * d + b) * d + c) * d + x1);
    }

    public double BufferedMs(int sampleRate) => BufferedFrames * 1000.0 / sampleRate;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return;
        lock (_gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            // How far behind the read clock this packet landed: a healthy source arrives just after a
            // read consumed the previous one, a late one arrives long after.
            if (_lastReadTicks != 0) _arrivalAgeTicks = now - _lastReadTicks;
            if (_firstWriteTicks == 0) _firstWriteTicks = now;
            _lastWriteTicks = now;
            int writable = Math.Min(frames, _capacityFrames);
            int drop = frames - writable;
            int skip = drop * _channels;
            float writePeak = 0f;
            int count = writable * _channels;
            for (int i = 0; i < count; i++)
            {
                float a = interleaved[i] < 0f ? -interleaved[i] : interleaved[i];
                if (a > writePeak) writePeak = a;
            }
            _lastWritePeak = writePeak;
            for (int i = 0; i < writable; i++)
            {
                int dstFrame = (_read + _countFrames + i) % _capacityFrames;
                int src = skip + i * _channels;
                for (int c = 0; c < _channels; c++) _samples[dstFrame * _channels + c] = interleaved[src + c];
            }
            int after = _countFrames + writable;
            int over = Math.Max(0, after - _capacityFrames);
            if (over > 0)
            {
                _read = (_read + over) % _capacityFrames;
                after = _capacityFrames;
            }
            _countFrames = after;
            DroppedFrames += drop + over;
        }
    }

    public void Read(Span<float> destination, int frames)
    {
        int need = frames * _channels;
        lock (_gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int bufferedBefore = _countFrames;

            if (_primedReady && _lastWriteTicks != 0 && now - _lastWriteTicks > ReArmTicks)
            {
                if (_gapOpen) { IdleFrames += _gapPaddedFrames; _gapOpen = false; }
                _primedReady = false;
                _levelAvg = 0;
            }

            // Prebuffer before consuming. A new source starts with an empty ring, and synthesising a
            // 16 ms level at the ±2000 ppm clamp would take seconds and starve the whole way. Output
            // silence until the first packets have filled the ring, then start clean.
            if (!_primedReady && bufferedBefore < _targetFrames)
            {
                destination[..need].Clear();
                if (need < destination.Length) destination[need..].Clear();
                _levelAvg += (bufferedBefore - _levelAvg) * LevelSmoothing;
                _lastReadTicks = now;
                IdleFrames += frames;
                return;
            }
            _primedReady = true;

            // Adaptive resampling, the way PulseAudio's loopback does it: a slow controller on the
            // smoothed fill level drives a continuously varying read ratio. Nothing is dropped or
            // duplicated — the ratio error is spread over time and stays far below audibility.
            _levelAvg += (bufferedBefore - _levelAvg) * LevelSmoothing;
            if (_levelControl)
            {
                double err = (_levelAvg - _targetFrames) / _targetFrames;
                _ratioBias = Math.Clamp(_ratioBias + RatioKi * err, -MaxRatioDev, MaxRatioDev);
                _ratio = Math.Clamp(1.0 + RatioKp * err + _ratioBias, 1.0 - MaxRatioDev, 1.0 + MaxRatioDev);
            }
            else _ratio = 1.0;   // open loop: used by the probe's drift calibration

            // Cubic interpolation reads one frame back and two ahead, so a sample can only be produced
            // while that whole window is inside the ring.
            int filled = 0;
            if (_countFrames >= 4)
            {
                filled = (int)Math.Floor(((_countFrames - 3) - _frac) / _ratio) + 1;
                if (filled > frames) filled = frames;
                if (filled < 0) filled = 0;
            }
            float peak = 0f;
            for (int i = 0; i < filled; i++)
            {
                double p = _frac + i * _ratio;
                int adv = (int)p;
                double d = p - adv;
                int n1 = _read + adv;
                if (n1 >= _capacityFrames) n1 -= _capacityFrames;
                int n0 = n1 == 0 ? _capacityFrames - 1 : n1 - 1;
                int n2 = n1 + 1;
                if (n2 >= _capacityFrames) n2 -= _capacityFrames;
                int n3 = n2 + 1;
                if (n3 >= _capacityFrames) n3 -= _capacityFrames;
                if (adv == 0) n0 = n1;   // the frame before the head has already been consumed
                int dst = i * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    float v = Cubic(_samples[n0 * _channels + c], _samples[n1 * _channels + c],
                                    _samples[n2 * _channels + c], _samples[n3 * _channels + c], d);
                    destination[dst + c] = v;
                    float a = v < 0f ? -v : v;
                    if (a > peak) peak = a;
                }
            }
            if (filled > 0)
            {
                double total = _frac + filled * _ratio;
                int consumed = (int)total;
                _frac = total - consumed;
                _read = (_read + consumed) % _capacityFrames;
                _countFrames -= consumed;
            }
            if (_gapOpen && filled > 0)
            {
                // Ramp back up out of the silence first: the return splice must not step either.
                int n = Math.Min(FadeFrames, filled);
                for (int k = 0; k < n; k++)
                {
                    int idx = k * _channels;
                    float w = (k + 1) / (float)n;
                    for (int c = 0; c < _channels; c++) destination[idx + c] *= w;
                }
                // The click is the jump from the last sample before the silence to the first one after
                // it; the silent stretch in between only says how long we had to bridge. The measured
                // splice decides the accounting: only a jump that could be heard counts as a miss.
                float step = 0f;
                for (int c = 0; c < _channels; c++)
                {
                    float d = destination[c] - _gapTail[c];
                    if (d < 0f) d = -d;
                    if (d > step) step = d;
                }
                if (step >= StepThreshold) StarvedFrames += _gapPaddedFrames;
                else IdleFrames += _gapPaddedFrames;
                // Logged either way: the raw step is what makes the verdict auditable.
                StarveWatch.Push(_tag, _gapAgeTicks, _gapSincePacketTicks, _gapPaddedFrames, _gapBufferedBefore, _gapPeak, step);
                _gapOpen = false;
            }            for (int i = filled; i < frames; i++)
            {
                int dst = i * _channels;
                for (int c = 0; c < _channels; c++) destination[dst + c] = 0f;
            }
            _lastReadTicks = now;
            int padded = frames - filled;
            if (padded > 0)
            {
                // Padding is expected while the ring is still ramping up and whenever the source has
                // gone quiet. Only a dry read right after a packet landed means we missed a deadline.
                bool pastRamp = _firstWriteTicks != 0 && now - _firstWriteTicks > RampTicks;
                bool fresh = _lastWriteTicks != 0 && now - _lastWriteTicks < StarveWindowTicks;
                if (pastRamp && fresh)
                {
                    if (!_gapOpen)
                    {
                        _gapOpen = true;
                        _gapPaddedFrames = 0;
                        _gapAgeTicks = _arrivalAgeTicks;
                        _gapSincePacketTicks = now - _lastWriteTicks;
                        _gapBufferedBefore = bufferedBefore;
                        _gapPeak = peak;
                    }
                    _gapPaddedFrames += padded;
                    if (filled > 0)
                    {
                        // Ramp the last real samples into the silence so the splice has no step.
                        int n = Math.Min(FadeFrames, filled);
                        for (int k = 0; k < n; k++)
                        {
                            int idx = (filled - n + k) * _channels;
                            float w = (n - 1 - k) / (float)n;
                            for (int c = 0; c < _channels; c++) destination[idx + c] *= w;
                        }
                        int last = (filled - 1) * _channels;
                        for (int c = 0; c < _channels; c++) _gapTail[c] = destination[last + c];
                    }
                }
                else IdleFrames += padded;
            }
        }
        if (need < destination.Length) destination[need..].Clear();
    }
}

internal sealed class MeterState
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastTicks;
    private long _holdUntilMs;

    public float Level;
    public float Hold;

    /// <summary>Instant attack, -60 dB/s fall. Peak hold stays 1.2 s, then falls at -20 dB/s.</summary>
    public void Update(float sample)
    {
        long now = Clock.ElapsedTicks;
        double dt = _lastTicks == 0 ? 0 : (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTicks = now;
        Level = (float)Math.Max(sample, Level * Math.Pow(10.0, -3.0 * dt));
        long nowMs = now * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (sample >= Hold)
        {
            Hold = sample;
            _holdUntilMs = nowMs + 1200;
        }
        else if (nowMs > _holdUntilMs)
        {
            Hold = (float)(Hold * Math.Pow(10.0, -dt));
        }
        if (Hold < Level) Hold = Level;
    }
}

internal sealed class Mixer
{
    private sealed class Slot
    {
        public int Id;
        public SourceBuffer Buffer = null!;
        public float[] Block = Array.Empty<float>();
        public bool Removed;
        public float Gain = 1f;
        /// <summary>Ramped towards Gain so gain edits and fades never step the waveform.</summary>
        public float CurrentGain;
    }

    private readonly object _gate = new();
    private readonly List<Slot> _apps = new();
    private readonly SourceBuffer _mic;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly int _capacityFrames;
    private readonly bool _levelControl;
    private float[] _micBlock;
    private int _nextId = 1;
    public float AppGain = 1f;
    public float MicGain = 1f;
    private float _micGainCurrent;
    // 0 -> 1 in ~14 ms: fast enough for a slider edit, slow enough that no edge becomes a step.
    private const float GainSlewPerFrame = 0.0015f;
    public double TestToneHz;
    private double _tonePhase;
    public long FramesConsumed;
    public long ClippedSamples;
    /// <summary>Blocks the provider had to replace with silence because mixing threw.</summary>
    public long BackstopErrors;
    private readonly MeterState _micMeter = new();
    private readonly MeterState _mixMeter = new();

    public float MicPeak => _micMeter.Level;
    public float MixPeak => _mixMeter.Level;
    public float MicHold => _micMeter.Hold;
    public float MixHold => _mixMeter.Hold;

    public Mixer(int sampleRate, int channels, int blockFrames, int capacityFrames,
                 bool addInitialSource = true, bool levelControl = true)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _blockFrames = blockFrames;
        _capacityFrames = capacityFrames;
        _levelControl = levelControl;
        _mic = new SourceBuffer(capacityFrames, channels, sampleRate, "mic", levelControl);
        _micBlock = new float[blockFrames * channels];
        if (addInitialSource) AddAppSource();
    }

    public int AppSourceCount { get { lock (_gate) return _apps.Count; } }

    public int AddAppSource()
    {
        lock (_gate)
        {
            int id = _nextId++;
            var slot = new Slot
            {
                Id = id,
                Buffer = new SourceBuffer(_capacityFrames, _channels, _sampleRate, "app#" + id, _levelControl),
                Block = new float[_blockFrames * _channels],
                CurrentGain = 0f,
            };
            _apps.Add(slot);
            return id;
        }
    }

    public void SetAppGain(int id, float gain)
    {
        lock (_gate)
        {
            var slot = _apps.FirstOrDefault(s => s.Id == id);
            if (slot is not null) slot.Gain = Math.Clamp(gain, 0f, 4f);
        }
    }

    public void RemoveAppSource(int id)
    {
        lock (_gate)
        {
            var slot = _apps.FirstOrDefault(s => s.Id == id);
            // Keep the slot until Mix has faded it to zero; dropping it here would step the output.
            if (slot is not null) slot.Removed = true;
        }
    }

    public void WriteApp(int id, ReadOnlySpan<float> interleaved)
    {
        Slot? slot;
        lock (_gate) slot = _apps.FirstOrDefault(s => s.Id == id);
        slot?.Buffer.Write(interleaved);
    }

    public void WriteApp(ReadOnlySpan<float> interleaved)
    {
        int id = 0;
        lock (_gate) { if (_apps.Count > 0) id = _apps[0].Id; }
        if (id > 0) WriteApp(id, interleaved);
    }

    public void WriteMic(ReadOnlySpan<float> interleaved)
    {
        _mic.Write(interleaved);
        _micMeter.Update(Peak(interleaved));
    }

    public BufferStats SourceStats(int id)
    {
        Slot? slot;
        lock (_gate) slot = _apps.FirstOrDefault(s => s.Id == id);
        if (slot is null) return default;
        return new BufferStats
        {
            BufferedMs = slot.Buffer.BufferedMs(_sampleRate),
            StarvedFrames = slot.Buffer.StarvedFrames,
            IdleFrames = slot.Buffer.IdleFrames,
            DroppedFrames = slot.Buffer.DroppedFrames,
            RatioPpm = slot.Buffer.RatioPpm,
        };
    }

    public BufferStats MicStats() => new()
    {
        BufferedMs = _mic.BufferedMs(_sampleRate),
        StarvedFrames = _mic.StarvedFrames,
        IdleFrames = _mic.IdleFrames,
        DroppedFrames = _mic.DroppedFrames,
        RatioPpm = _mic.RatioPpm,
    };

    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>Mixes one block and writes interleaved float samples into <paramref name="destination"/>.</summary>
    public void Mix(Span<float> destination)
    {
        int frames = destination.Length / _channels;
        Slot[] slots;
        lock (_gate) slots = _apps.ToArray();

        if (_micBlock.Length < destination.Length) _micBlock = new float[destination.Length];
        foreach (var slot in slots)
            if (slot.Block.Length < destination.Length) slot.Block = new float[destination.Length];

        destination.Clear();
        bool anyRemoved = false;
        foreach (var slot in slots)
        {
            var block = slot.Block.AsSpan(0, destination.Length);
            slot.Buffer.Read(block, frames);
            // Every gain change is a ramp: a 0.5 dB edit, a fade-in on add, a fade-out on remove.
            float target = slot.Removed ? 0f : slot.Gain;
            float cur = slot.CurrentGain;
            if (Math.Abs(target - cur) <= GainSlewPerFrame)
            {
                cur = target;
                for (int i = 0; i < destination.Length; i++) destination[i] += block[i] * cur;
            }
            else
            {
                int dir = target > cur ? 1 : -1;
                for (int f = 0; f < frames; f++)
                {
                    cur += dir * GainSlewPerFrame;
                    if (dir > 0 ? cur > target : cur < target) cur = target;
                    int i = f * _channels;
                    for (int c = 0; c < _channels; c++) destination[i + c] += block[i + c] * cur;
                }
            }
            slot.CurrentGain = cur;
            if (slot.Removed && cur <= 0f) anyRemoved = true;
        }
        if (anyRemoved) lock (_gate) _apps.RemoveAll(s => s.Removed && s.CurrentGain <= 0f);

        var mic = _micBlock.AsSpan(0, destination.Length);
        _mic.Read(mic, frames);
        FramesConsumed += frames;

        double phase = _tonePhase;
        double phaseStep = TestToneHz > 0 ? 2 * Math.PI * TestToneHz / _sampleRate : 0;
        float micTarget = MicGain;
        float micCur = _micGainCurrent;
        for (int f = 0; f < frames; f++)
        {
            float tone = 0f;
            if (phaseStep > 0)
            {
                tone = (float)(0.05 * Math.Sin(phase));
                phase += phaseStep;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            }
            if (micCur != micTarget)
            {
                micCur += Math.Clamp(micTarget - micCur, -GainSlewPerFrame, GainSlewPerFrame);
            }
            int i = f * _channels;
            for (int c = 0; c < _channels; c++)
            {
                float v = destination[i + c] * AppGain + mic[i + c] * micCur + tone;
                if (v > 1f) { v = 1f; ClippedSamples++; }
                else if (v < -1f) { v = -1f; ClippedSamples++; }
                destination[i + c] = v;
            }
        }
        _micGainCurrent = micCur;
        _tonePhase = phase;
        float mixPeak = 0f;
        for (int i = 0; i < destination.Length; i++)
        {
            float a = Math.Abs(destination[i]);
            if (a > mixPeak) mixPeak = a;
        }
        _mixMeter.Update(mixPeak);
    }

    public string StatsLine(int sampleRate, double rssMb, double cpuPercent)
    {
        double outSec = FramesConsumed / (double)sampleRate;
        return $"out={outSec:F1}s sources={AppSourceCount} micBuffered={_mic.BufferedMs(sampleRate):F1}ms " +
               $"clipSamples={ClippedSamples} rss={rssMb:F1}MB cpu={cpuPercent:F1}%";
    }
}
