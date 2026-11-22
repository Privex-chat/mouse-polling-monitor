using System.IO;
using System.Text.Json;

namespace MousePollingMonitor;

/// <summary>
/// Twelve normalized (0..1) injection indicators extracted from the rolling window.
/// Each value is already oriented so that 1.0 = strongly cheat-like.
///
/// Indices 0–5  : generic injection / rate-spoofing features
/// Indices 6–7  : silent-aim (snap-and-return) features
/// Indices 8–9  : triggerbot (micro-pause) features
/// Indices 10–11: kinematics (impossible jerk) & micro-adjustments (lack of tremor)
/// </summary>
public readonly record struct DetectionFeatures(
    // ── Generic injection ────────────────────────────────────────────────────
    double BurstRate,
    double LowJitter,
    double IntervalConcentration,
    double RepeatedIntervals,
    double MovementUniformity,
    double BurstClustering,
    // ── Silent aim ───────────────────────────────────────────────────────────
    double SilentAimSnapRate,          // fraction of packets that are snap events
    double SilentAimReturnPrecision,   // how exactly the return vector cancels the snap
    // ── Triggerbot ───────────────────────────────────────────────────────────
    double TrigbotMicroPause,          // fraction of micro-pause (near-zero magnitude) interruptions
    double TrigbotPauseRegularity,     // regularity / periodicity of those pauses
    // ── Kinematics & micro-adjustments ───────────────────────────────────────
    double KinematicJerk,              // unnatural acceleration/jerk spikes violating biomechanics
    double MicroAdjustmentDeficiency   // trajectory linearity / absence of physiological micro-tremor
)
{
    public const int Count = 12;

    public double this[int i] => i switch
    {
        0  => BurstRate,
        1  => LowJitter,
        2  => IntervalConcentration,
        3  => RepeatedIntervals,
        4  => MovementUniformity,
        5  => BurstClustering,
        6  => SilentAimSnapRate,
        7  => SilentAimReturnPrecision,
        8  => TrigbotMicroPause,
        9  => TrigbotPauseRegularity,
        10 => KinematicJerk,
        11 => MicroAdjustmentDeficiency,
        _  => 0
    };
}

public sealed record DetectionPrediction(
    double Suspicion,        // 0..1  P(injection)
    double Certainty,        // 0..1  confidence, scaled by sample count
    string Reason,           // human-readable top factors
    DetectionFeatures Features,
    int TrainingSamples
);

/// <summary>
/// Tiny online logistic-regression classifier.
///
/// Prior weights encode domain heuristics from anti-cheat research (VACNet,
/// BeCAPTCHA-Mouse, Fitts' law, KMBox reverse engineering). The two feedback
/// buttons call Learn(), which performs one SGD step and persists the weights to
/// %LOCALAPPDATA%\MousePollingMonitor\model.json, so the model adapts to the
/// user's own mouse over time.
/// </summary>
internal sealed class DetectionModel
{
    private const int    FeatureCount = DetectionFeatures.Count;  // 12
    private const double LearningRate = 0.08;
    private const double L2           = 0.002;

    // Indices 0-5  : generic injection priors
    // Indices 6-7  : silent-aim priors
    // Indices 8-9  : triggerbot priors
    // Indices 10-11: kinematic jerk & micro-adjustment priors
    private static readonly double[] PriorWeights =
    {
        2.0,   // 0:  burst rate
        1.6,   // 1:  low jitter
        0.9,   // 2:  interval concentration
        0.5,   // 3:  repeated intervals
        0.35,  // 4:  movement uniformity
        0.7,   // 5:  burst clustering
        2.2,   // 6:  silent aim snap rate     — strong signal when present
        1.8,   // 7:  silent aim return prec.  — near-perfect cancellation is unnatural
        1.4,   // 8:  trigbot micro pause      — rhythmic freezes are suspicious
        1.5,   // 9:  trigbot pause regularity — periodic pauses unlikely from humans
        1.8,   // 10: kinematic jerk           — impossible acceleration violating inertia
        1.5,   // 11: micro-adjust deficiency  — unnatural linearity / zero tremor
    };
    private const double PriorBias = -2.8;

    private static readonly string[] FeatureNames =
    {
        "burst rate",
        "low jitter",
        "interval concentration",
        "repeated intervals",
        "movement uniformity",
        "burst clustering",
        "silent-aim snap rate",
        "silent-aim return precision",
        "triggerbot micro-pause",
        "triggerbot pause regularity",
        "kinematic jerk",
        "micro-adjustment deficiency",
    };

    private readonly double[] _weights = (double[])PriorWeights.Clone();
    private double _bias = PriorBias;
    private int    _trained;

    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MousePollingMonitor", "model.json");

    public DetectionModel() => Load();

    public int TrainedSamples => _trained;

    public DetectionPrediction Predict(DetectionFeatures f, int sampleCount)
    {
        double p = Sigmoid(Score(f));

        // Confidence combines distance from the decision boundary with how much
        // data the rolling window currently holds.
        double sufficiency = 0.5 + 0.5 * Math.Min(1.0, sampleCount / 100.0);
        double certainty   = 2.0 * Math.Abs(p - 0.5) * sufficiency;

        return new DetectionPrediction(p, certainty, BuildReason(f, p), f, _trained);
    }

    public void Learn(DetectionFeatures f, bool injected)
    {
        double error = (injected ? 1.0 : 0.0) - Sigmoid(Score(f));

        for (int i = 0; i < FeatureCount; i++)
        {
            _weights[i] += LearningRate * (error * f[i] - L2 * _weights[i]);
            _weights[i] = Math.Clamp(_weights[i], 0.0, 6.0);
        }

        _bias += LearningRate * 0.5 * error;
        _bias = Math.Clamp(_bias, -8.0, 2.0);
        _trained++;
    }

    public void Reset()
    {
        Array.Copy(PriorWeights, _weights, FeatureCount);
        _bias = PriorBias;
        _trained = 0;
    }

    private double Score(DetectionFeatures f)
    {
        double z = _bias;
        for (int i = 0; i < FeatureCount; i++)
            z += _weights[i] * f[i];
        return z;
    }

    private static double Sigmoid(double z) =>
        Math.Clamp(1.0 / (1.0 + Math.Exp(-z)), 0.001, 0.999);

    private string BuildReason(DetectionFeatures f, double p)
    {
        if (p < 0.45)
            return "Jitter, interval spread, and kinematics match physical human hardware.";

        if (p <= 0.55)
            return "Features sit near the decision boundary — more movement needed.";

        Span<int> order = stackalloc int[FeatureCount];
        for (int i = 0; i < FeatureCount; i++) order[i] = i;

        for (int i = 0; i < FeatureCount - 1; i++)
            for (int j = i + 1; j < FeatureCount; j++)
                if (_weights[order[j]] * f[order[j]] > _weights[order[i]] * f[order[i]])
                    (order[i], order[j]) = (order[j], order[i]);

        var parts = new List<string>(2);
        for (int k = 0; k < FeatureCount && parts.Count < 2; k++)
        {
            int idx = order[k];
            if (_weights[idx] * f[idx] < 0.08) break;
            parts.Add(FeatureNames[idx]);
        }

        return parts.Count == 0
            ? "Weak injection indicators — score is only slightly elevated."
            : "Main factors: " + string.Join(" + ", parts) + ".";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ModelPath)) return;

            var model = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(ModelPath));
            if (model?.Weights is not { } w) return;

            // Accept saved models with fewer features (e.g. 6 or 10-feature save files).
            // Extra features default to the heuristic prior.
            int loadCount = Math.Min(w.Length, FeatureCount);
            for (int i = 0; i < loadCount; i++)
            {
                if (!double.IsFinite(w[i]) || w[i] < 0 || w[i] > 6) return;
                _weights[i] = w[i];
            }
            if (!double.IsFinite(model.Bias)) return;
            _bias    = Math.Clamp(model.Bias, -8.0, 2.0);
            _trained = Math.Max(0, model.Trained);
        }
        catch
        {
            // Corrupt or unreadable model file — keep the heuristic prior.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            File.WriteAllText(ModelPath, JsonSerializer.Serialize(new ModelFile
            {
                Version = 1,
                Weights = (double[])_weights.Clone(),
                Bias    = _bias,
                Trained = _trained
            }));
        }
        catch
        {
            // Persistence is best-effort; the in-memory model still works.
        }
    }

    private sealed class ModelFile
    {
        public ModelFile() { }

        public int      Version { get; set; }
        public double[] Weights { get; set; } = Array.Empty<double>();
        public double   Bias    { get; set; }
        public int      Trained { get; set; }
    }
}
