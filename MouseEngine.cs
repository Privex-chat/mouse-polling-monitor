using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MousePollingMonitor;

internal readonly record struct MousePacket(long Ticks, int Dx, int Dy, bool HasMovement);

public enum Verdict { Waiting, Natural, Suspicious, Injection }

public sealed record EngineStats(
    int                 InstantHz,
    double              AvgHz,
    double              StdDev,
    double              BurstRate,
    long                TotalPackets,
    long                TotalBursts,
    int                 FilledSamples,
    Verdict             Verdict,
    DetectionPrediction Prediction,
    double[]            Spark,
    bool[]              SparkBurst,
    int                 SparkCount
);

internal sealed class MouseEngine
{
    private const double BurstThreshMs   = 0.5;
    private const double CvNatural       = 0.10;   // σ / mean at the "natural" boundary
    private const double CvInjection     = 0.01;   // σ / mean at the "injection" boundary
    private const double BurstSaturation = 0.25;   // burst rate that maps to feature 1.0
    private const int    MinSamples      = 30;
    private const int    WindowSize      = 100;
    public  const int    SparkLen        = 60;

    private readonly double[] _intervals   = new double[WindowSize];
    private readonly bool[]   _bursts      = new bool[WindowSize];
    private readonly int[]    _mags        = new int[WindowSize];
    private readonly int[]    _dxs         = new int[WindowSize];   // raw dx for snap & kinematic detection
    private readonly int[]    _dys         = new int[WindowSize];   // raw dy for snap & kinematic detection
    private int    _head         = 0;
    private int    _filled       = 0;
    private int    _windowBursts = 0;
    private double _windowSum    = 0;

    // ── Silent-aim state ────────────────────────────────────────────────────
    // A "snap event" = a packet with magnitude ≥ SnapMinMag that is immediately
    // followed within SnapWindowMs by a packet whose vector nearly cancels it.
    private const int    SnapMinMag         = 20;   // px: minimum snap size to track
    private const double SnapWindowMs       = 2.5;  // ms: max time between snap and return
    private const double SnapCancelThresh   = 0.70; // dot-product fraction for "cancel"
    private int    _snapCandidateDx;
    private int    _snapCandidateDy;
    private double _snapCandidateTicks;
    private bool   _awaitingReturn;
    private int    _snapCount;
    private int    _preciseReturnCount;    // returns that cancel ≥ 85% of snap vector

    // ── Triggerbot state ────────────────────────────────────────────────────
    // A "micro-pause" = a movement packet with magnitude < PauseMagThresh following
    // a packet with magnitude ≥ PauseLeadMag. We track when they occur (in ticks)
    // so we can measure inter-pause interval regularity (low CV → suspicious).
    private const int    PauseMagThresh = 3;    // px: near-zero magnitude
    private const int    PauseLeadMag   = 8;    // px: "was moving" threshold
    private const int    PauseHistory   = 20;   // how many pause timestamps to keep
    private bool   _prevWasMoving;
    private int    _pauseCount;
    private readonly long[] _pauseTicks = new long[PauseHistory];

    private readonly DetectionModel _model = new();
    private readonly object         _modelLock = new();
    private DetectionFeatures       _lastFeatures;
    private bool                    _hasLabelableSample;

    private readonly double[] _spark      = new double[SparkLen];
    private readonly bool[]   _sparkBurst = new bool[SparkLen];
    private int _sparkCount = 0;

    // Instantaneous Hz: count events that arrived in the last 1 second
    private readonly Queue<long> _instantTicks = new();

    private readonly ConcurrentQueue<MousePacket> _queue = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private volatile bool _alive = true;
    private Win32.WndProcDelegate? _wndProcDelegate;

    private long _totalMovPkts = 0;
    private long _totalBursts  = 0;
    private long _prevTicks    = 0;

    private readonly Stopwatch _renderThrottle = Stopwatch.StartNew();
    private const long RenderIntervalMs = 50;

    public event Action<EngineStats>? StatsUpdated;

    public void Start()
    {
        new Thread(ProcessorLoop)
        {
            IsBackground = true,
            Name         = "PacketProcessor",
            Priority     = ThreadPriority.BelowNormal
        }.Start();

        new Thread(MessageLoopThread)
        {
            IsBackground = true,
            Name         = "Win32MsgLoop"
        }.Start();
    }

    public void Stop() => _alive = false;

    private void MessageLoopThread()
    {
        _wndProcDelegate = WndProc;
        IntPtr hInstance = Win32.GetModuleHandleW(null);
        string cls = "MPM_" + Environment.ProcessId;

        var wc = new Win32.WNDCLASSW
        {
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance     = hInstance,
            lpszClassName = cls
        };
        Win32.RegisterClassW(ref wc);

        IntPtr hwnd = Win32.CreateWindowExW(0, cls, "MPM", 0, 0, 0, 0, 0,
            Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;

        Win32.RegisterRawInputDevices(new[] { new Win32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage     = 0x02,
            dwFlags     = Win32.RIDEV_INPUTSINK,
            hwndTarget  = hwnd
        }}, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());

        Win32.MSG msg;
        while (_alive && Win32.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_INPUT)   { CapturePacket(lParam); return IntPtr.Zero; }
        if (msg == Win32.WM_DESTROY) { Win32.PostQuitMessage(0); return IntPtr.Zero; }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private unsafe void CapturePacket(IntPtr hRaw)
    {
        uint sz = 0, hdrSz = Win32.RawInputHeaderSize;
        Win32.GetRawInputData(hRaw, Win32.RID_INPUT, IntPtr.Zero, ref sz, hdrSz);
        if (sz == 0 || sz > 256) return;

        byte* buf = stackalloc byte[(int)sz];
        Win32.GetRawInputData(hRaw, Win32.RID_INPUT, (IntPtr)buf, ref sz, hdrSz);
        if (*(uint*)buf != Win32.RIM_TYPEMOUSE) return;

        byte* m = buf + hdrSz;
        int dx = *(int*)(m + 12);
        int dy = *(int*)(m + 16);
        _queue.Enqueue(new MousePacket(_sw.ElapsedTicks, dx, dy, dx != 0 || dy != 0));
    }

    private void ProcessorLoop()
    {
        int idle = 0;
        while (_alive || !_queue.IsEmpty)
        {
            if (_queue.TryDequeue(out var pkt)) { idle = 0; Analyze(pkt); }
            else if (++idle > 600) { Thread.Sleep(1); idle = 0; }
        }
    }

    private void Analyze(MousePacket p)
    {
        // Track all packets for Hz measurement (hardware polls even without movement)
        long nowTicks = p.Ticks;
        long cutoff   = nowTicks - Stopwatch.Frequency; // 1 second ago
        _instantTicks.Enqueue(nowTicks);
        while (_instantTicks.Count > 0 && _instantTicks.Peek() < cutoff)
            _instantTicks.Dequeue();

        if (_prevTicks == 0) { _prevTicks = p.Ticks; return; }

        double dt = (p.Ticks - _prevTicks) * 1000.0 / Stopwatch.Frequency;
        _prevTicks = p.Ticks;
        if (dt <= 0) return;

        // Burst/injection analysis only applies to movement packets
        if (!p.HasMovement)
        {
            if (_renderThrottle.ElapsedMilliseconds >= RenderIntervalMs)
            {
                _renderThrottle.Restart();
                FireStats();
            }
            return;
        }
        _totalMovPkts++;

        bool burst = dt < BurstThreshMs;
        if (burst) _totalBursts++;

        int slot = _head;
        if (_filled == WindowSize) { _windowSum -= _intervals[slot]; if (_bursts[slot]) _windowBursts--; }
        else _filled++;
        _intervals[slot] = dt;
        _bursts[slot]    = burst;
        int mag          = Math.Abs(p.Dx) + Math.Abs(p.Dy);
        _mags[slot]      = mag;
        _dxs[slot]       = p.Dx;
        _dys[slot]       = p.Dy;
        _windowSum      += dt;
        if (burst) _windowBursts++;
        _head = (slot + 1) % WindowSize;

        // ── Silent-aim online tracker ──────────────────────────────────────────
        // Research basis: community reverse-engineering of silent-aim mechanics
        // (snap to hitbox → fire → return). The return vector dot-products nearly
        // perfectly with -snap because the cheat computes -dx,-dy or stores the
        // pre-snap angle and restores it. Human overshoot+correction is noisy and
        // delayed (100-250 ms feedback loop, Fitts' law submovement research).
        if (_awaitingReturn)
        {
            double elapsedMs = (p.Ticks - (long)_snapCandidateTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs <= SnapWindowMs && mag >= 4)
            {
                // Dot product of (snapDx,snapDy) · (dx,dy) normalised.
                // Perfect reversal → cos θ = -1 → product negative.
                double dot  = _snapCandidateDx * p.Dx + _snapCandidateDy * p.Dy;
                double magA = Math.Sqrt(_snapCandidateDx * _snapCandidateDx + _snapCandidateDy * _snapCandidateDy);
                double magB = Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy);
                double cosT = (magA > 0 && magB > 0) ? dot / (magA * magB) : 0;

                // cosT < -SnapCancelThresh means the return vector is > 135° opposed.
                // Also require the return magnitude is ≥ 60% of the snap magnitude —
                // a human correction is usually much smaller and slower.
                bool canceled    = cosT < -SnapCancelThresh;
                bool sizeSimilar = magB >= magA * 0.60;
                if (canceled && sizeSimilar)
                {
                    _snapCount++;
                    // "Precise" return: cancels ≥ 85% in size AND within 1 ms.
                    if (magB >= magA * 0.85 && elapsedMs <= 1.0)
                        _preciseReturnCount++;
                }
            }
            _awaitingReturn = false;
        }

        if (mag >= SnapMinMag)
        {
            _snapCandidateDx    = p.Dx;
            _snapCandidateDy    = p.Dy;
            _snapCandidateTicks = p.Ticks;
            _awaitingReturn     = true;
        }

        // ── Triggerbot online tracker ──────────────────────────────────────────
        // Research basis: triggerbots halt movement for 1-10 ms around fire events
        // (the bot pauses its own movement injection to avoid interfering with
        // recoil / shot registration). Human shooting-while-moving never produces
        // sub-2 px stillness this frequently or at such regular intervals.
        // VACNet / BeCAPTCHA-Mouse: reaction-time consistency is the primary signal.
        bool wasMoving = _prevWasMoving;
        bool nowMoving = mag >= PauseLeadMag;
        _prevWasMoving = nowMoving;

        if (wasMoving && mag < PauseMagThresh)
        {
            // Ring-buffer of pause timestamps.
            _pauseTicks[_pauseCount % PauseHistory] = p.Ticks;
            _pauseCount++;
        }

        if (_sparkCount < SparkLen)
        {
            _spark[_sparkCount] = dt;
            _sparkBurst[_sparkCount] = burst;
            _sparkCount++;
        }
        else
        {
            Array.Copy(_spark,      1, _spark,      0, SparkLen - 1);
            Array.Copy(_sparkBurst, 1, _sparkBurst, 0, SparkLen - 1);
            _spark[SparkLen - 1] = dt;
            _sparkBurst[SparkLen - 1] = burst;
        }

        if (_renderThrottle.ElapsedMilliseconds >= RenderIntervalMs)
        {
            _renderThrottle.Restart();
            FireStats();
        }
    }

    private void FireStats()
    {
        var (avgDt, stdDev, burstRate, avgHz) = ComputeStats();
        var features = ComputeFeatures(avgDt, stdDev, burstRate);

        DetectionPrediction prediction;
        lock (_modelLock)
        {
            _lastFeatures       = features;
            _hasLabelableSample = _filled >= MinSamples;
            prediction          = _model.Predict(features, _filled);
        }

        var verdict = EvaluateVerdict(prediction.Suspicion);

        StatsUpdated?.Invoke(new EngineStats(
            _instantTicks.Count, avgHz, stdDev, burstRate,
            _totalMovPkts, _totalBursts, _filled, verdict, prediction,
            (double[])_spark.Clone(), (bool[])_sparkBurst.Clone(), _sparkCount
        ));
    }

    private (double avgDt, double stdDev, double burstRate, double avgHz) ComputeStats()
    {
        if (_filled < 2) return (0, 0, 0, 0);
        double avg = _windowSum / _filled;
        double variance = 0;
        for (int i = 0; i < _filled; i++)
            variance += (_intervals[i] - avg) * (_intervals[i] - avg);
        return (avg, Math.Sqrt(variance / _filled), (double)_windowBursts / _filled, 1000.0 / avg);
    }

    private Verdict EvaluateVerdict(double suspicion)
    {
        if (_filled < MinSamples) return Verdict.Waiting;
        if (suspicion >= 0.80)    return Verdict.Injection;
        if (suspicion >= 0.45)    return Verdict.Suspicious;
        return Verdict.Natural;
    }

    // ── Feature extraction ───────────────────────────────────────────────────
    //
    // Every feature is normalized to [0,1] where 1.0 = "strongly cheat-like".
    //
    // Features 0-5  : generic injection / rate-spoofing
    // Features 6-7  : silent-aim (snap-and-return vector cancellation)
    // Features 8-9  : triggerbot (micro-pause periodicity)
    // Features 10-11: kinematics (impossible jerk) & micro-adjustments (lack of tremor)
    //
    // Research basis:
    // - VACNet (GDC 2018): view-angle delta sequences, reaction-time consistency
    // - BeCAPTCHA-Mouse (arXiv:2005.00890): kinematics, velocity, acceleration, jerk
    // - Fitts's law & submovement decomposition: human corrective tremor vs linear aim
    // - Community reverse-engineering of KMBox, pSilent, and hardware injectors

    private DetectionFeatures ComputeFeatures(double avgDt, double stdDev, double burstRate)
    {
        double cv = avgDt > 0 ? stdDev / avgDt : 0;
        double lowJitter = Clamp01((CvNatural - cv) / (CvNatural - CvInjection));
        double burst     = Clamp01(burstRate / BurstSaturation);

        if (_filled < 2)
            return new DetectionFeatures(burst, lowJitter, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        int n = _filled;

        // ── Feature 2: Interval concentration ───────────────────────────────
        var buckets = new Dictionary<long, int>(n);
        int modeCount = 0;
        for (int i = 0; i < n; i++)
        {
            long key = (long)Math.Round(_intervals[i] * 100.0);
            buckets.TryGetValue(key, out int count);
            count++;
            buckets[key] = count;
            if (count > modeCount) modeCount = count;
        }
        double modeShare     = (double)modeCount / n;
        double concentration = Clamp01((modeShare - 0.15) / 0.55);

        // ── Feature 3: Repeated intervals (2 µs resolution) ─────────────────
        int repeats = 0;
        for (int i = 1; i < n; i++)
            if (Math.Abs(_intervals[i] - _intervals[i - 1]) <= 0.002)
                repeats++;
        double repeated = Clamp01(repeats / (double)(n - 1) / 0.80);

        // ── Feature 4: Movement uniformity ──────────────────────────────────
        double magSum = 0;
        for (int i = 0; i < n; i++) magSum += _mags[i];
        double magAvg = magSum / n;
        double magVar = 0;
        for (int i = 0; i < n; i++)
        {
            double d = _mags[i] - magAvg;
            magVar += d * d;
        }
        double magCv      = magAvg > 0 ? Math.Sqrt(magVar / n) / magAvg : 0;
        double uniformity = Clamp01((0.80 - magCv) / 0.70);

        // ── Feature 5: Burst clustering ──────────────────────────────────────
        int maxRun = 0, run = 0;
        int start = n == WindowSize ? _head : 0;
        for (int k = 0; k < n; k++)
        {
            int idx = (start + k) % WindowSize;
            if (_bursts[idx]) { run++; if (run > maxRun) maxRun = run; }
            else run = 0;
        }
        double clustering = Clamp01(maxRun / 8.0);

        // ── Features 6-7: Silent-aim (snap + return) ────────────────────────
        double snapDenom       = Math.Max(1, _totalMovPkts);
        double snapRate        = Clamp01(_snapCount / (snapDenom * 0.05));
        double returnPrecision = _snapCount > 0
            ? Clamp01(_preciseReturnCount / (double)_snapCount / 0.5)
            : 0;

        // ── Features 8-9: Triggerbot (micro-pause periodicity) ───────────────
        int validPauses = Math.Min(_pauseCount, PauseHistory);
        double microPauseRate = Clamp01(_pauseCount / (snapDenom * 0.10));

        double pauseRegularity = 0;
        if (validPauses >= 4)
        {
            long[] sorted = new long[validPauses];
            for (int i = 0; i < validPauses; i++) sorted[i] = _pauseTicks[i];
            Array.Sort(sorted);
            double gapSum = 0;
            double[] gaps = new double[validPauses - 1];
            for (int i = 0; i < validPauses - 1; i++)
            {
                gaps[i] = (sorted[i + 1] - sorted[i]) * 1000.0 / Stopwatch.Frequency;
                gapSum += gaps[i];
            }
            double gapAvg = gapSum / gaps.Length;
            double gapVar = 0;
            foreach (double g in gaps) gapVar += (g - gapAvg) * (g - gapAvg);
            double gapCv = gapAvg > 0 ? Math.Sqrt(gapVar / gaps.Length) / gapAvg : 1.0;
            pauseRegularity = Clamp01((0.30 - gapCv) / 0.25);
        }

        // ── Features 10-11: Kinematics & Micro-adjustments ──────────────────
        // Research basis:
        // 1. BeCAPTCHA-Mouse (arXiv:2005.00890) & biomechanical inertia:
        //    Human arms cannot produce instantaneous velocity steps or infinite jerk.
        //    Acceleration |a| > 35 px/ms² with significant speed (> 8 px/ms)
        //    violates muscle-mass physical limits.
        // 2. Fitts's Law submovement & physiological tremor (8-12 Hz):
        //    Human movements continuously exhibit angular micro-corrections (Δθ).
        //    Aimbot linear interpolation / smoothing locks onto straight vectors
        //    with near-zero angular variance over multiple consecutive packets.
        int jerkSpikes = 0;
        int straightRun = 0;
        int maxStraightRun = 0;
        double prevHeading = double.NaN;
        double prevVel = 0;

        int windowStart = n == WindowSize ? _head : 0;
        for (int k = 0; k < n; k++)
        {
            int idx = (windowStart + k) % WindowSize;
            double dtStep = Math.Max(0.1, _intervals[idx]);
            int dx = _dxs[idx];
            int dy = _dys[idx];
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double vel = dist / dtStep; // px / ms

            if (k > 0)
            {
                double accel = Math.Abs(vel - prevVel) / dtStep; // px / ms²
                if (accel > 35.0 && (vel > 8.0 || prevVel > 8.0))
                    jerkSpikes++;
            }
            prevVel = vel;

            // Micro-adjustments / heading tremor:
            if (dist >= 2.0)
            {
                double heading = Math.Atan2(dy, dx);
                if (!double.IsNaN(prevHeading))
                {
                    double diff = Math.Abs(heading - prevHeading);
                    if (diff > Math.PI) diff = 2 * Math.PI - diff; // shortest arc

                    // Consecutive packets with virtually zero angular deviation (< 1.1°)
                    if (diff < 0.02)
                    {
                        straightRun++;
                        if (straightRun > maxStraightRun) maxStraightRun = straightRun;
                    }
                    else
                    {
                        straightRun = 0;
                    }
                }
                prevHeading = heading;
            }
            else
            {
                straightRun = 0;
                prevHeading = double.NaN;
            }
        }

        // Normalize features to [0, 1]:
        // ≥ 3 jerk spikes in a 100-packet window is heavily synthetic
        double kinematicJerk = Clamp01(jerkSpikes / 3.0);

        // Natural human flicks rarely sustain > 3 consecutive packets with < 1.1° heading diff.
        // 8+ consecutive straight packets under speed strongly signals linear interpolation.
        double microTremorAbsence = Clamp01((maxStraightRun - 3) / 6.0);

        return new DetectionFeatures(
            burst, lowJitter, concentration, repeated, uniformity, clustering,
            snapRate, returnPrecision,
            microPauseRate, pauseRegularity,
            kinematicJerk, microTremorAbsence
        );
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // ── Feedback / online learning ───────────────────────────────────────────

    public bool LabelCurrent(bool injected)
    {
        lock (_modelLock)
        {
            if (!_hasLabelableSample) return false;
            _model.Learn(_lastFeatures, injected);
            _model.Save();
            return true;
        }
    }

    public void ResetModel()
    {
        lock (_modelLock)
        {
            _model.Reset();
            _model.Save();
        }
    }
}
