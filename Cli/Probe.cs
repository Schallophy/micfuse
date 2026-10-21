using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicFuse;

/// <summary>
/// Latency and stutter diagnostic that needs no audio source and puts nothing audible on speakers.
/// 2 ms tone bursts are injected into the app ring and the mic ring at known moments, the same
/// render endpoint is loopback-captured in this process, and each detected burst is paired with the
/// most recent injection that could have produced it. The captured frame count is the time axis, so
/// latency growth over a long run is measurable to the sample; the absolute value carries about one
/// capture packet of uncertainty.
/// </summary>
internal static class Probe
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int PacketFrames = 480;          // 10 ms, the period a real capture delivers at
    private const int ClickEveryPackets = 10;      // one burst per 100 ms per path
    private const int MicOffsetPackets = 5;        // the mic train sits 50 ms off the app train
    private const int BurstFrames = 96;            // 1 kHz, two whole cycles
    private const float AppAmp = 0.6f;
    private const float MicAmp = 0.3f;
    private const float AppFloor = 0.45f;
    private const float MicFloor = 0.18f;
    private const int RefractoryFrames = 960;      // 20 ms, so one burst yields one hit
    private const double MaxLatencyMs = 250.0;     // widest injection→render delay a pairing may claim
    // Acceptance line for gap severity, so no ear has to be in the loop: a waveform jump of 0.02
    // full scale (~-34 dBFS) is the level below which a discontinuity stops surviving into the far
    // end's codec, and fewer than one per 5 seconds of those is what a healthy pipeline looks like.
    private const float StepThreshold = 0.02f;
    private const double LoudPerSecond = 0.2;
    // A continuous tone under the clicks: gaps can only be judged against real signal, and with a
    // known level the step threshold itself becomes calibratable instead of guessed.
    private const float ToneAmp = 0.05f;

    private static readonly float[] Burst = BuildBurst();

    private static float[] BuildBurst()
    {
        var b = new float[BurstFrames];
        for (int i = 0; i < b.Length; i++) b[i] = (float)Math.Sin(2 * Math.PI * 1000.0 * i / Rate);
        return b;
    }

    private static long TicksOfFrames(long frames, int rate) => frames * Stopwatch.Frequency / rate;
    private static long TicksOfMs(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);
    private static double MsOfTicks(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private readonly record struct Hit(long Frame, long RenderTicks, float Peak);

    public static async Task<int> Run(Options o)
    {
        string selector = string.IsNullOrWhiteSpace(o.OutDeviceSelector) ? "CABLE Input" : o.OutDeviceSelector;
        MMDevice? device = null;
        using (var enumerator = new MMDeviceEnumerator())
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            bool owned = false;
            for (int i = 0; i < devices.Count && device is null; i++)
            {
                var d = devices[i];
                if (d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.ID, selector, StringComparison.OrdinalIgnoreCase)) { device = d; owned = true; }
                else d.Dispose();
            }
            if (owned) devices.Dispose();
        }
        if (device is null)
        {
            Log.Line($"RESULT=FAIL reason=render-device-not-found selector=\"{selector}\"");
            return 7;
        }

        Log.Line($"probe target: {device.FriendlyName} id={device.ID}");

        var mixer = new Mixer(Rate, Channels, PacketFrames, Rate / 2, "probe",
                              levelControl: !o.ProbeOpenLoop);
        var appRing = new SourceRing(Rate / 2, Channels, Rate);
        var micRing = new SourceRing(Rate / 2, Channels, Rate);
        int appId = mixer.AttachSource(appRing);
        int micId = mixer.AttachSource(micRing);
        var provider = new MixerProvider(mixer, WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels), null, Rate);
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency(false)
            .WithLatency(20)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        player.Init(provider);
        Log.Line($"player: granted={player.LatencyMilliseconds}ms lowLatency={player.LowLatencyActive} " +
                 $"reason={player.LowLatencyUnavailableReason ?? "-"}");
        Log.Line($"probe mode: injectDrift={o.ProbeDriftPpm:F0}ppm openLoop={o.ProbeOpenLoop} " +
                 $"expectedSlope={(o.ProbeOpenLoop ? o.ProbeDriftPpm / 1000.0 * 60.0 : 0):F1}ms/min");
        player.Play();

        var recorder = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .WithDevice(device)
            .WithBufferLength(100)
            .Build();
        var wf = recorder.WaveFormat;
        int capRate = wf.SampleRate, capCh = wf.Channels, blockAlign = wf.BlockAlign;
        Log.Line($"capture format: {wf}");

        var hits = new List<Hit>();
        var injects = new List<(long Ticks, float Amp)>();
        var gate = new object();
        long captured = 0, axisOrigin = 0, lastHit = -RefractoryFrames;
        int packets = 0, packetsWithQpc = 0, minPacketFrames = int.MaxValue, maxPacketFrames = 0;

        recorder.DataAvailable += (buffer, _, _, qpcPosition) =>
        {
            var s = MemoryMarshal.Cast<byte, float>(buffer);
            int frames = buffer.Length / blockAlign;
            long arrival = Stopwatch.GetTimestamp();
            lock (gate)
            {
                packets++;
                if (qpcPosition != 0) packetsWithQpc++;
                minPacketFrames = Math.Min(minPacketFrames, frames);
                maxPacketFrames = Math.Max(maxPacketFrames, frames);
                if (axisOrigin == 0) axisOrigin = arrival - TicksOfFrames(frames, capRate);
                bool hasQpc = qpcPosition != 0;
                for (int i = 0; i < frames; i++)
                {
                    float a = Math.Abs(s[i * capCh]);
                    if (a < MicFloor) continue;
                    long abs = captured + i;
                    if (abs - lastHit < RefractoryFrames) continue;
                    int ahead = Math.Min(frames, i + BurstFrames);
                    float peak = a;
                    for (int j = i + 1; j < ahead; j++) peak = Math.Max(peak, Math.Abs(s[j * capCh]));
                    // The packet's QPC marks its end, so every frame inside it is placed exactly;
                    // arrival time is only good enough to fall back on when the driver withholds it.
                    long renderTicks = hasQpc
                        ? qpcPosition - TicksOfFrames(frames - 1 - i, capRate)
                        : axisOrigin + TicksOfFrames(abs, capRate);
                    hits.Add(new Hit(abs, renderTicks, peak));
                    lastHit = abs;
                }
                captured += frames;
            }
        };
        recorder.StartRecording();

        var producer = Task.Run(() =>
        {
            var appBlock = new float[PacketFrames * Channels];
            var micBlock = new float[PacketFrames * Channels];
            long start = Stopwatch.GetTimestamp();
            long end = start + TicksOfMs(o.Seconds * 1000.0);
            long next = start;
            int p = 0;
            long toneFrame = 0;
            long packetTicks = TicksOfMs(PacketFrames * 1000.0 / (Rate * (1.0 + o.ProbeDriftPpm * 1e-6)));
            // Fixed rate, like a real capture endpoint: packets arrive on a deadline rather than on
            // demand, so the level controller under test is the only thing reacting to the fill level.
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= end) break;
                if (now < next)
                {
                    long wait = next - now;
                    if (wait > TicksOfMs(2)) Thread.Sleep(TimeSpan.FromTicks(wait - TicksOfMs(1)));
                    else Thread.SpinWait(200);
                    continue;
                }
                Array.Clear(appBlock);
                Array.Clear(micBlock);
                for (int i = 0; i < PacketFrames; i++)
                {
                    float t = ToneAmp * (float)Math.Sin(2 * Math.PI * 1000.0 * (toneFrame + i) / Rate);
                    appBlock[i * Channels] = t;
                    appBlock[i * Channels + 1] = t;
                    micBlock[i * Channels] = t;
                    micBlock[i * Channels + 1] = t;
                }
                bool app = p % ClickEveryPackets == 0;
                bool mic = p % ClickEveryPackets == MicOffsetPackets;
                if (app) Fill(appBlock, AppAmp);
                if (mic) Fill(micBlock, MicAmp);
                if (app || mic)
                {
                    lock (gate)
                    {
                        long t = Stopwatch.GetTimestamp();
                        if (app) injects.Add((t, AppAmp));
                        if (mic) injects.Add((t, MicAmp));
                    }
                }
                appRing.Write(appBlock);
                micRing.Write(micBlock);
                toneFrame += PacketFrames;
                p++;
                next += packetTicks;
                if (now - next > TicksOfMs(50)) next = now;   // fell far behind: resync, never burst
            }
        });

        await producer;
        recorder.StopRecording();
        recorder.Dispose();
        await player.DisposeAsync();
        device.Dispose();

        var (starves, starveLost) = StarveWatch.Drain();
        int loud = starves.Count(e => e.Step > StepThreshold);
        double loudLimit = o.Seconds * LoudPerSecond;
        double maxStep = starves.Count == 0 ? 0 : starves.Max(e => e.Step);
        double medStep = starves.Count == 0 ? 0 : starves.Select(e => e.Step).OrderBy(v => v).ElementAt(starves.Count / 2);
        bool gapsBad = loud > loudLimit;
        Log.Line($"gaps: events={starves.Count} lost={starveLost} step max={maxStep:F4} median={medStep:F4} " +
                 $"over_{StepThreshold:F3}={loud} limit={loudLimit:F1} rate={starves.Count / o.Seconds:F2}/s");
        // The controller's own rate estimate is the ground truth for drift calibration: unlike the
        // latency slope it does not pass through the arrival-based capture axis, which carries its own
        // ppm-level bias when the driver withholds QPC.
        var appStats = mixer.Stats(appId);
        var micStats = mixer.Stats(micId);
        Log.Line($"ratio: app={appStats.RatioPpm:+0;-0;0}ppm mic={micStats.RatioPpm:+0;-0;0}ppm " +
                 $"buffered app={appStats.BufferedMs:F1}ms mic={micStats.BufferedMs:F1}ms " +
                 $"starved app={appStats.StarvedFrames} mic={micStats.StarvedFrames} " +
                 $"dropped app={appStats.DroppedFrames} mic={micStats.DroppedFrames} " +
                 $"(injected {o.ProbeDriftPpm:+0;-0;0}ppm, expect ratio to match it)");

        List<Hit> hitList; List<(long Ticks, float Amp)> injectList;
        int pkt, pktQpc, minF, maxF; long totalFrames;
        lock (gate)
        {
            hitList = hits.ToList();
            injectList = injects.ToList();
            pkt = packets; pktQpc = packetsWithQpc; minF = minPacketFrames; maxF = maxPacketFrames;
            totalFrames = captured;
        }
        Log.Line($"capture: packets={pkt} withQpc={pktQpc} sizeMs min={minF * 1000.0 / capRate:F2} " +
                 $"avg={(pkt == 0 ? 0 : totalFrames * 1000.0 / capRate / pkt):F2} max={maxF * 1000.0 / capRate:F2}");
        int rc = Report(o, capRate, pktQpc == pkt && pkt > 0, hitList, injectList);
        if (gapsBad) rc += 2;
        Log.Line($"RESULT={(rc == 0 ? "OK" : "FAIL")} code={rc} probe seconds={o.Seconds:F0}");
        return rc;
    }

    private static void Fill(float[] block, float amp)
    {
        for (int i = 0; i < BurstFrames; i++)
        {
            block[i * Channels] = Burst[i] * amp;
            block[i * Channels + 1] = Burst[i] * amp;
        }
    }

    private static int Report(Options o, int capRate, bool timed, List<Hit> hits, List<(long Ticks, float Amp)> injects)
    {
        if (!timed) Log.Line("WARN: the capture driver withheld packet QPC; latency is measured against " +
                             "arrival time and carries one capture packet of error");
        var head = new StringBuilder("head hits: ");
        for (int i = 0; i < Math.Min(6, hits.Count); i++)
        {
            head.Append($"{hits[i].Frame}|{(hits[i].Peak >= AppFloor ? 'A' : 'M')}|" +
                        $"{MsOfTicks(hits[i].RenderTicks - hits[0].RenderTicks):F1}  ");
        }
        Log.Line(head.ToString());
        var headInj = new StringBuilder("head inj: ");
        for (int i = 0; i < Math.Min(6, injects.Count); i++)
        {
            headInj.Append($"{(injects[i].Amp >= AppFloor ? 'A' : 'M')}|" +
                           $"{MsOfTicks(injects[i].Ticks - injects[0].Ticks):F1}  ");
        }
        Log.Line(headInj.ToString());
        var appHits = hits.Where(h => h.Peak >= AppFloor).ToList();
        var micHits = hits.Where(h => h.Peak < AppFloor && h.Peak >= MicFloor).ToList();
        var appInj = injects.Where(i => i.Amp >= AppFloor).ToList();
        var micInj = injects.Where(i => i.Amp < AppFloor).ToList();

        int bad = 0;
        bad += Sum("app", o, capRate, appHits, appInj);
        bad += Sum("mic", o, capRate, micHits, micInj);
        return bad == 0 ? 0 : 9;
    }

    private static int Sum(string path, Options o, int capRate, List<Hit> hitList, List<(long Ticks, float Amp)> inj)
    {
        var samples = new List<(double T, double Lat)>();
        var used = new bool[inj.Count];
        int unmatched = 0, preRoll = 0;
        foreach (var hit in hitList)
        {
            int best = -1;
            for (int k = 0; k < inj.Count; k++)
            {
                if (used[k]) continue;
                double d = MsOfTicks(hit.RenderTicks - inj[k].Ticks);
                if (d < 0 || d > MaxLatencyMs) continue;
                if (best < 0 || inj[k].Ticks > inj[best].Ticks) best = k;
            }
            if (best < 0)
            {
                // Anything detected before the first injection is startup debris, not a lost burst.
                if (inj.Count > 0 && hit.RenderTicks < inj[0].Ticks) preRoll++;
                else unmatched++;
                continue;
            }
            used[best] = true;
            samples.Add((MsOfTicks(inj[best].Ticks), MsOfTicks(hit.RenderTicks - inj[best].Ticks)));
        }
        int n = samples.Count;
        Log.Line($"--- {path}: injected={inj.Count} detected={hitList.Count} paired={n} ---");
        if (n == 0) return 1;
        var lat = samples.Select(s => s.Lat).ToList();

        var sorted = lat.OrderBy(v => v).ToList();
        double P(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * (sorted.Count - 1)))];
        int quarter = Math.Max(1, n / 4);
        double firstQ = lat.Take(quarter).OrderBy(v => v).ElementAt(quarter / 2);
        double lastQ = lat.Skip(n - quarter).OrderBy(v => v).ElementAt(quarter / 2);

        // Trend against the injection clock (not the click index, so a missed click cannot stretch
        // the time axis), fitted on 10 s medians: medians remove the occasional hiccup and the
        // autocorrelated jitter that would otherwise dominate a plain least-squares fit. The 95%
        // interval is what decides whether a slope is resolvable at all.
        double slopePerMin = 0, slopeCi = 0;
        var buckets = new SortedDictionary<long, List<double>>();
        double t0 = samples.Min(s => s.T);
        foreach (var s in samples)
        {
            long b = (long)((s.T - t0) / 10000.0);
            if (!buckets.TryGetValue(b, out var list)) buckets[b] = list = new List<double>();
            list.Add(s.Lat);
        }
        if (buckets.Count >= 4)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var kv in buckets)
            {
                xs.Add((kv.Key + 0.5) * 10.0);                      // bucket centre, seconds
                var vals = kv.Value.OrderBy(v => v).ToList();
                ys.Add(vals[vals.Count / 2]);
            }
            double mx = xs.Average(), my = ys.Average(), sxx = 0, sxy = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                double dx = xs[i] - mx;
                sxx += dx * dx;
                sxy += dx * (ys[i] - my);
            }
            double b = sxx > 0 ? sxy / sxx : 0;                     // ms per second
            double ssr = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                double e = ys[i] - (my + b * (xs[i] - mx));
                ssr += e * e;
            }
            double se = xs.Count > 2 && sxx > 0 ? Math.Sqrt(ssr / (xs.Count - 2) / sxx) : 0;
            slopePerMin = b * 60.0;
            slopeCi = 2.0 * se * 60.0;
        }
        bool trendKnown = buckets.Count >= 4;
        bool drifting = trendKnown && Math.Abs(slopePerMin) + slopeCi > 0.2;

        var ibi = new List<double>();
        for (int i = 1; i < hitList.Count; i++)
            ibi.Add((hitList[i].Frame - hitList[i - 1].Frame) * 1000.0 / capRate);
        // The first couple of intervals straddle the ramp, so they say nothing about the chain.
        var steady = ibi.Skip(2).ToList();
        int offCadence = steady.Count(v => Math.Abs(v - 100.0) > 20.0);

        Log.Line($"{path} latency ms: min={sorted[0]:F2} p5={P(0.05):F2} median={P(0.5):F2} " +
                 $"p95={P(0.95):F2} max={sorted[^1]:F2} spread={sorted[^1] - sorted[0]:F2}");
        Log.Line($"{path} trend ms: first_quarter_median={firstQ:F2} last_quarter_median={lastQ:F2} " +
                 $"growth={lastQ - firstQ:+0.00;-0.00;0.00} slope={slopePerMin:+0.00;-0.00;0.00}" +
                 $"(±{slopeCi:F2}) ms/min over {o.Seconds:F0}s buckets={buckets.Count} " +
                 $"parallel={(trendKnown ? (drifting ? "NO" : "YES") : "INSUFFICIENT")}");
        Log.Line($"{path} cadence: intervals={ibi.Count} steady={steady.Count} off_by_>20ms={offCadence} " +
                 $"min={(steady.Count > 0 ? steady.Min() : 0):F1} max={(steady.Count > 0 ? steady.Max() : 0):F1} (expected 100.0)");
        int unused = used.Count(u => !u);
        Log.Line($"{path} unmatched_detections={unmatched} pre_roll={preRoll} unproduced_injections={unused} " +
                 $"(a couple pending at the tail is normal) " +
                 $"series(1/s): {string.Join(" ", lat.Where((_, i) => i % 10 == 0).Select(v => v.ToString("F1")))}");
        return unmatched + offCadence + (drifting ? 1 : 0);
    }
}
