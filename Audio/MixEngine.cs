namespace MicFuse;

/// <summary>What happened to one source as seen by one reader: what is buffered and where the audio went.</summary>
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
/// Fixed-capacity float ring: one capture callback writes, every output line that routes this source
/// reads through its own cursor. Producers never wait on readers; the ring recycles over whatever a
/// slow line has not consumed yet and that line accounts the skip as its own drop, so one stalled
/// endpoint can neither stall the capture thread nor starve another line.
/// </summary>
internal sealed class SourceRing
{
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _capacityFrames;

    /// <summary>Shared by every reader of this ring; writers take it for the copy only.</summary>
    internal readonly object Gate = new();

    private long _writePos;
    private long _lastWriteTicks;
    private int _readers;

    /// <summary>Frames discarded because a single packet was larger than the whole ring.</summary>
    public long DroppedFrames;

    public SourceRing(int capacityFrames, int channels, int sampleRate)
    {
        _capacityFrames = capacityFrames;
        _channels = channels;
        SampleRate = sampleRate;
        _samples = new float[capacityFrames * channels];
    }

    public int Channels => _channels;
    public int SampleRate { get; }
    public int CapacityFrames => _capacityFrames;
    public int ReaderCount { get { lock (Gate) return _readers; } }

    /// <summary>A reader starts at the write head: frames older than this are already recyclable.</summary>
    public SourceReader CreateReader(string tag, bool levelControl = true)
    {
        lock (Gate)
        {
            _readers++;
            return new SourceReader(this, tag, levelControl, _writePos);
        }
    }

    internal void DetachReader()
    {
        lock (Gate) _readers--;
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return;
        lock (Gate)
        {
            _lastWriteTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // With nobody reading, the copy would only be recycled over: record the arrival, skip the work.
            if (_readers == 0) return;
            int writable = Math.Min(frames, _capacityFrames);
            int skip = (frames - writable) * _channels;
            for (int i = 0; i < writable; i++)
            {
                int dst = (int)((_writePos + i) % _capacityFrames) * _channels;
                int src = skip + i * _channels;
                for (int c = 0; c < _channels; c++) _samples[dst + c] = interleaved[src + c];
            }
            _writePos += writable;
            DroppedFrames += frames - writable;
        }
    }

    internal float SampleAt(long absoluteFrame, int channel) =>
        _samples[(int)(absoluteFrame % _capacityFrames) * _channels + channel];

    internal int Capacity => _capacityFrames;

    /// <summary>Only valid while <see cref="Gate"/> is held.</summary>
    internal long WritePosLocked => _writePos;

    /// <summary>Only valid while <see cref="Gate"/> is held.</summary>
    internal long LastWriteTicksLocked => _lastWriteTicks;
}

/// <summary>
/// One output line's view of a <see cref="SourceRing"/>: its own cursor, its own level controller, its
/// own glitch accounting. Two endpoints on the same source are two readers, each tracking a different
/// hardware clock, which is why a single mix cannot be computed once and copied to every line.
/// </summary>
internal sealed class SourceReader
{
    private readonly SourceRing _ring;
    private readonly int _channels;
    private readonly int _capacityFrames;
    private readonly int _sampleRate;
    private readonly string _tag;
    private long _pos;

    private double _frac;          // fractional read position inside the current frame
    private double _ratio = 1.0;   // current resampling ratio, 1.0 = read as fast as data arrives
    private double _ratioBias;     // integral term, accumulates the ppm needed to cancel clock drift
    private double _levelAvg;
    private bool _primedReady;
    private long _startedTicks;
    private long _lastReadTicks;

    // Target level for the lossless guarantee. Measured on this machine: arrivals come in 10 ms
    // bursts (mic 2x240 frames back to back), so the level sawtooths by one burst around the target.
    // Worst-phase margin against a scheduling stall = target - 5 ms (half sawtooth) - 1 block
    // (2.67 ms): 16 ms buys ~8.3 ms of stall tolerance, 12 ms only ~4.3 ms. Measured idle tail is
    // ~1 ms, so 16 ms is insurance for load, at a cost of 4 ms of latency.
    private readonly int _targetFrames;
    private readonly bool _levelControl;
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
    // A reader that has just joined always runs dry for its first packet or two; that ramp is not a
    // miss. It is bounded by time, not by ring level, so a chronically under-buffered pipeline —
    // which never fills at all — still reports its misses.
    private static readonly long RampTicks = 50L * System.Diagnostics.Stopwatch.Frequency / 1000;

    /// <summary>Frames this reader skipped forward because the ring recycled over it.</summary>
    public long DroppedFrames;
    /// <summary>
    /// Frames this line had to pad although a packet arrived moments ago: that endpoint missed its
    /// deadline. This is the only one of the two padding counters that means "glitch".
    /// </summary>
    public long StarvedFrames;
    /// <summary>Frames padded because nothing had arrived — the source is silent, paused, or not started.</summary>
    public long IdleFrames;

    internal SourceReader(SourceRing ring, string tag, bool levelControl, long startFrame)
    {
        _ring = ring;
        _channels = ring.Channels;
        _capacityFrames = ring.Capacity;
        _sampleRate = ring.SampleRate;
        _targetFrames = _sampleRate * 16 / 1000;
        _levelControl = levelControl;
        _tag = tag;
        _pos = startFrame;
        _gapTail = new float[_channels];
    }

    public void Dispose()
    {
        _ring.DetachReader();
    }

    /// <summary>How far the read ratio currently deviates from 1:1, in parts per million.</summary>
    public int RatioPpm => (int)Math.Round((_ratio - 1.0) * 1e6);

    public int BufferedFrames
    {
        get { lock (_ring.Gate) return (int)Math.Clamp(_ring.WritePosLocked - _pos, 0, _capacityFrames); }
    }

    public double BufferedMs() => BufferedFrames * 1000.0 / _sampleRate;

    public BufferStats Stats() => new()
    {
        BufferedMs = BufferedMs(),
        StarvedFrames = StarvedFrames,
        IdleFrames = IdleFrames,
        DroppedFrames = DroppedFrames + _ring.DroppedFrames,
        RatioPpm = RatioPpm,
    };

    /// <summary>Catmull-Rom: exact at integer positions, smooth in between, four taps.</summary>
    private static float Cubic(float x0, float x1, float x2, float x3, double d)
    {
        double a = -0.5 * x0 + 1.5 * x1 - 1.5 * x2 + 0.5 * x3;
        double b = x0 - 2.5 * x1 + 2.0 * x2 - 0.5 * x3;
        double c = -0.5 * x0 + 0.5 * x2;
        return (float)(((a * d + b) * d + c) * d + x1);
    }

    public void Read(Span<float> destination, int frames)
    {
        int need = frames * _channels;
        lock (_ring.Gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_startedTicks == 0) _startedTicks = now;

            // The ring recycles over a reader that falls behind: skip forward to the oldest frame that
            // still exists and own the loss, instead of letting one line drag the others.
            long writePos = _ring.WritePosLocked;
            int bufferedBefore = (int)Math.Clamp(writePos - _pos, 0, _capacityFrames);
            long behind = writePos - _pos - _capacityFrames;
            if (behind > 0)
            {
                _pos += behind;
                DroppedFrames += behind;
                bufferedBefore = _capacityFrames;
            }

            if (_primedReady && _ring.LastWriteTicksLocked != 0 && now - _ring.LastWriteTicksLocked > ReArmTicks)
            {
                if (_gapOpen) { IdleFrames += _gapPaddedFrames; _gapOpen = false; }
                _primedReady = false;
                _levelAvg = 0;
            }

            // Prebuffer before consuming. A new reader starts at the write head with an empty window,
            // and synthesising a 16 ms level at the ±2000 ppm clamp would take seconds and starve the
            // whole way. Output silence until the first packets have filled the ring, then start clean.
            if (!_primedReady && bufferedBefore < _targetFrames)
            {
                destination[..need].Clear();
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
            if (bufferedBefore >= 4)
            {
                filled = (int)Math.Floor(((bufferedBefore - 3) - _frac) / _ratio) + 1;
                if (filled > frames) filled = frames;
                if (filled < 0) filled = 0;
            }
            float peak = 0f;
            for (int i = 0; i < filled; i++)
            {
                double p = _frac + i * _ratio;
                int adv = (int)p;
                double d = p - adv;
                long n1 = _pos + adv;
                int n0 = adv == 0 ? 0 : -1;   // the frame before the head has already been consumed
                int dst = i * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    float v = Cubic(_ring.SampleAt(n1 + n0, c), _ring.SampleAt(n1, c),
                                    _ring.SampleAt(n1 + 1, c), _ring.SampleAt(n1 + 2, c), d);
                    destination[dst + c] = v;
                    float a = v < 0f ? -v : v;
                    if (a > peak) peak = a;
                }
            }
            if (filled > 0)
            {
                double total = _frac + filled * _ratio;
                _pos += (int)total;
                _frac = total - (int)total;
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
                    float diff = destination[c] - _gapTail[c];
                    if (diff < 0f) diff = -diff;
                    if (diff > step) step = diff;
                }
                if (step >= StepThreshold) StarvedFrames += _gapPaddedFrames;
                else IdleFrames += _gapPaddedFrames;
                // Logged either way: the raw step is what makes the verdict auditable.
                StarveWatch.Push(_tag, now - _lastReadTicks, now - _ring.LastWriteTicksLocked,
                                 _gapPaddedFrames, _gapBufferedBefore, _gapPeak, step);
                _gapOpen = false;
            }
            for (int i = filled; i < frames; i++)
            {
                int dst = i * _channels;
                for (int c = 0; c < _channels; c++) destination[dst + c] = 0f;
            }
            _lastReadTicks = now;
            int padded = frames - filled;
            if (padded > 0)
            {
                // Padding is expected while the ring is still ramping up and whenever the source has
                // gone quiet. Only a dry read right after a packet landed means this line missed a
                // deadline.
                bool pastRamp = now - _startedTicks > RampTicks;
                bool fresh = _ring.LastWriteTicksLocked != 0 &&
                             now - _ring.LastWriteTicksLocked < StarveWindowTicks;
                if (pastRamp && fresh)
                {
                    if (!_gapOpen)
                    {
                        _gapOpen = true;
                        _gapPaddedFrames = 0;
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

/// <summary>
/// Pulls one line's mix from that endpoint's clock (WasapiPlayer) and optionally mirrors it to WAV.
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
                Log.Line($"WARN mix backstop on '{_mixer.Name}' (silence for one block): " + ex.GetType().Name + ": " + ex.Message);
        }
        if (used < buffer.Length) buffer[used..].Clear();
        return buffer.Length;
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

/// <summary>
/// One output line's bus: the set of sources routed to it, each read through its own cursor so every
/// line resamples against its own endpoint clock. Slots come and go while audio keeps flowing, which
/// is what makes routing a live edit rather than a restart.
/// </summary>
internal sealed class Mixer
{
    internal sealed class Slot
    {
        public int Id;
        public SourceRing Ring = null!;
        public SourceReader Reader = null!;
        public float[] Block = Array.Empty<float>();
        public bool Removed;
        public float Gain = 1f;
        /// <summary>Ramped towards Gain so route edits and gain edits never step the waveform.</summary>
        public float CurrentGain;
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly Dictionary<int, Slot> _byId = new();
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly int _capacityFrames;
    private readonly bool _levelControl;
    private int _nextId = 1;
    public readonly string Name;
    /// <summary>0 -> 1 in ~14 ms: fast enough for a slider edit, slow enough that no edge becomes a step.</summary>
    private const float GainSlewPerFrame = 0.0015f;
    public double TestToneHz;
    private double _tonePhase;
    public long FramesConsumed;
    public long ClippedSamples;
    /// <summary>Blocks the provider had to replace with silence because mixing threw.</summary>
    public long BackstopErrors;
    private readonly MeterState _mixMeter = new();

    public float MixPeak => _mixMeter.Level;
    public float MixHold => _mixMeter.Hold;

    public Mixer(int sampleRate, int channels, int blockFrames, int capacityFrames, string name = "mix",
                 bool levelControl = true)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _blockFrames = blockFrames;
        _capacityFrames = capacityFrames;
        _levelControl = levelControl;
        Name = name;
    }

    public int AppSourceCount { get { lock (_gate) return _slots.Count; } }

    /// <summary>Routes a shared source onto this line. Pass a negative <paramref name="id"/> to have one
    /// assigned; 0 is the microphone's real id, not a sentinel.</summary>
    public int AttachSource(SourceRing ring, int id = -1, float gain = 1f)
    {
        lock (_gate)
        {
            if (id < 0) id = _nextId;
            if (_byId.TryGetValue(id, out var existing))
            {
                // Re-attaching a route that is still fading out: swap in the new reader, drop the old one.
                if (!ReferenceEquals(existing.Ring, ring))
                {
                    existing.Reader.Dispose();
                    existing.Ring = ring;
                    existing.Reader = ring.CreateReader($"{Name}#{id}", _levelControl);
                }
                existing.Gain = Math.Clamp(gain, 0f, 4f);
                existing.Removed = false;
                return id;
            }
            if (id >= _nextId) _nextId = id + 1;
            var slot = new Slot
            {
                Id = id,
                Ring = ring,
                Reader = ring.CreateReader($"{Name}#{id}", _levelControl),
                Block = new float[_blockFrames * _channels],
                Gain = Math.Clamp(gain, 0f, 4f),
                CurrentGain = 0f,
            };
            _slots.Add(slot);
            _byId[id] = slot;
            return id;
        }
    }

    public bool HasSource(int id) { lock (_gate) return _byId.ContainsKey(id); }

    public void SetGain(int id, float gain)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var slot)) slot.Gain = Math.Clamp(gain, 0f, 4f);
        }
    }

    public void DetachSource(int id)
    {
        lock (_gate)
        {
            // Keep the slot until Mix has faded it to zero; dropping it here would step the output.
            if (_byId.TryGetValue(id, out var slot)) slot.Removed = true;
        }
    }

    /// <summary>Drops every slot at once. Used when the line itself is going away, so nothing left on it
    /// can be heard and there is no waveform to protect.</summary>
    public void ClearSources()
    {
        lock (_gate)
        {
            foreach (var slot in _slots) slot.Reader.Dispose();
            _slots.Clear();
            _byId.Clear();
        }
    }

    public int[] RoutedIdsSnapshot()
    {
        lock (_gate)
        {
            var live = new List<int>(_slots.Count);
            foreach (var slot in _slots) if (!slot.Removed) live.Add(slot.Id);
            return live.ToArray();
        }
    }

    public BufferStats Stats(int id)
    {
        Slot? slot;
        lock (_gate) slot = _byId.TryGetValue(id, out var found) ? found : null;
        if (slot is null) return default;
        return slot.Reader.Stats();
    }

    /// <summary>Mixes one block and writes interleaved float samples into <paramref name="destination"/>.</summary>
    public void Mix(Span<float> destination)
    {
        int frames = destination.Length / _channels;
        Slot[] slots;
        lock (_gate) slots = _slots.ToArray();

        foreach (var slot in slots)
            if (slot.Block.Length < destination.Length) slot.Block = new float[destination.Length];

        destination.Clear();
        bool anyRemoved = false;
        double phase = _tonePhase;
        double phaseStep = TestToneHz > 0 ? 2 * Math.PI * TestToneHz / _sampleRate : 0;
        foreach (var slot in slots)
        {
            var block = slot.Block.AsSpan(0, destination.Length);
            slot.Reader.Read(block, frames);
            // Every gain change is a ramp: a 0.5 dB edit, a fade-in on route, a fade-out on un-route.
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
        if (anyRemoved)
        {
            Slot[] gone;
            lock (_gate)
            {
                gone = _slots.Where(s => s.Removed && s.CurrentGain <= 0f).ToArray();
                foreach (var slot in gone)
                {
                    _slots.Remove(slot);
                    _byId.Remove(slot.Id);
                }
            }
            foreach (var slot in gone) slot.Reader.Dispose();
        }

        FramesConsumed += frames;

        if (phaseStep > 0)
        {
            for (int f = 0; f < frames; f++)
            {
                float tone = (float)(0.05 * Math.Sin(phase));
                phase += phaseStep;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                int i = f * _channels;
                for (int c = 0; c < _channels; c++) destination[i + c] += tone;
            }
        }
        _tonePhase = phase;

        float mixPeak = 0f;
        long clipped = 0;
        for (int i = 0; i < destination.Length; i++)
        {
            float v = destination[i];
            if (v > 1f) { v = 1f; clipped++; }
            else if (v < -1f) { v = -1f; clipped++; }
            destination[i] = v;
            float a = v < 0f ? -v : v;
            if (a > mixPeak) mixPeak = a;
        }
        ClippedSamples += clipped;
        _mixMeter.Update(mixPeak);
    }

    public string StatsLine(int sampleRate, double rssMb, double cpuPercent)
    {
        Slot[] slots;
        lock (_gate) slots = _slots.ToArray();
        double worstMs = 0;
        long starved = 0, dropped = 0;
        foreach (var slot in slots)
        {
            var st = slot.Reader.Stats();
            if (st.BufferedMs > worstMs) worstMs = st.BufferedMs;
            starved += st.StarvedFrames;
            dropped += st.DroppedFrames;
        }
        double outSec = FramesConsumed / (double)sampleRate;
        return $"out={outSec:F1}s sources={slots.Length} worstBuffered={worstMs:F1}ms " +
               $"starved={starved} dropped={dropped} clipSamples={ClippedSamples} " +
               $"rss={rssMb:F1}MB cpu={cpuPercent:F1}%";
    }
}
