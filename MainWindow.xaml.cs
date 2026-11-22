using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MousePollingMonitor;

public partial class MainWindow : Window
{
    private readonly MouseEngine _engine = new();
    private EngineStats?         _lastStats;
    private const int            MaxLogEntries = 500;
    private readonly Stopwatch   _logThrottle  = Stopwatch.StartNew();

    // Pre-allocated spark bar rectangles — updated in-place each frame
    private readonly Rectangle[] _sparkRects = new Rectangle[MouseEngine.SparkLen];

    private static readonly SolidColorBrush BrushBurst = Frozen(Color.FromRgb(220, 50, 50));
    private static readonly SolidColorBrush BrushBar   = Frozen(Color.FromRgb(0, 200, 100));
    private static readonly SolidColorBrush BrushEmpty = Frozen(Color.FromRgb(25, 25, 45));

    private static readonly SolidColorBrush BrushScoreNatural    = Frozen(Color.FromRgb(0, 230, 120));
    private static readonly SolidColorBrush BrushScoreSuspicious = Frozen(Color.FromRgb(255, 215, 0));
    private static readonly SolidColorBrush BrushScoreInjection  = Frozen(Color.FromRgb(255, 70, 70));
    private static readonly SolidColorBrush BrushMuted           = Frozen(Color.FromRgb(51, 51, 85));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public MainWindow()
    {
        InitializeComponent();

        txtTimerInfo.Text = $"Timer: {Stopwatch.Frequency:N0} Hz  |  {(IntPtr.Size == 8 ? "x64" : "x86")}  |  Close window to exit";

        for (int i = 0; i < MouseEngine.SparkLen; i++)
        {
            var r = new Rectangle { Height = 2, Fill = BrushEmpty };
            sparkCanvas.Children.Add(r);
            _sparkRects[i] = r;
        }

        _engine.StatsUpdated += stats => Dispatcher.InvokeAsync(() => ApplyStats(stats));
        _engine.Start();

        Closing += (_, _) => _engine.Stop();
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = chkTopmost.IsChecked == true;
    }

    // ── Stats update (UI thread) ─────────────────────────────────────────────

    private void ApplyStats(EngineStats s)
    {
        _lastStats = s;

        txtInstantHz.Text  = s.InstantHz > 0 ? s.InstantHz.ToString() : "- - -";
        txtSamples.Text    = s.FilledSamples.ToString();

        if (s.FilledSamples >= 2)
        {
            txtAvgHz.Text     = s.AvgHz.ToString("F0", CultureInfo.InvariantCulture);
            txtStdDev.Text    = s.StdDev.ToString("F3", CultureInfo.InvariantCulture);
            txtBurstRate.Text = (s.BurstRate * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
            txtStdDev.Foreground    = StdDevBrush(s.StdDev);
            txtBurstRate.Foreground = BurstBrush(s.BurstRate);
        }
        else
        {
            txtAvgHz.Text     = "---";
            txtStdDev.Text    = "---";
            txtBurstRate.Text = "---";
        }

        ApplyScore(s);
        ApplyVerdict(s);
        DrawSparkline(s);

        if (_logThrottle.ElapsedMilliseconds >= 1000)
        {
            _logThrottle.Restart();
            AddLog(s);
        }
    }

    // ── Suspicion score ──────────────────────────────────────────────────────

    private void ApplyScore(EngineStats s)
    {
        var p = s.Prediction;
        var ic = CultureInfo.InvariantCulture;

        if (s.FilledSamples >= 2)
        {
            string pct = (p.Suspicion * 100).ToString("F0", ic);
            var brush  = ScoreBrush(p.Suspicion);

            txtSuspicion.Text        = pct;
            txtSuspicion.Foreground  = brush;
            scoreFill.Fill           = brush;
            txtCertainty.Text        = "certainty: " +
                                       (p.Certainty * 100).ToString("F0", ic) + "%";
            txtPrediction.Text       = s.Verdict switch
            {
                Verdict.Natural    => "PREDICTION: NATURAL",
                Verdict.Suspicious => "PREDICTION: SUSPICIOUS",
                Verdict.Injection  => "PREDICTION: INJECTION",
                _                  => "PREDICTION: COLLECTING"
            };
            txtPrediction.Foreground = brush;
            txtWhy.Text              = p.Reason;
            txtFeatures.Text         = FeatureLine(p.Features);
            UpdateScoreBar(p.Suspicion);

            // ── Sub-scores: Silent Aim ────────────────────────────────────────
            double silentRaw = Math.Max(p.Features.SilentAimSnapRate,
                                        p.Features.SilentAimReturnPrecision * p.Features.SilentAimSnapRate);
            double silentPct = silentRaw * 100;
            txtSilentAimScore.Text       = silentPct.ToString("F0", ic);
            txtSilentAimScore.Foreground = ScoreBrush(silentRaw);
            txtSilentAimDetail.Text      = string.Format(ic,
                "snap:{0:F2}  prec:{1:F2}",
                p.Features.SilentAimSnapRate, p.Features.SilentAimReturnPrecision);

            // ── Sub-scores: Triggerbot ────────────────────────────────────────
            double trigRaw = p.Features.TrigbotMicroPause * p.Features.TrigbotPauseRegularity;
            double trigPct = trigRaw * 100;
            txtTrigbotScore.Text       = trigPct.ToString("F0", ic);
            txtTrigbotScore.Foreground = ScoreBrush(trigRaw);
            txtTrigbotDetail.Text      = string.Format(ic,
                "pause:{0:F2}  reg:{1:F2}",
                p.Features.TrigbotMicroPause, p.Features.TrigbotPauseRegularity);

            // ── Sub-scores: Kinematics & Micro-aim ────────────────────────────
            double kinRaw = Math.Max(p.Features.KinematicJerk, p.Features.MicroAdjustmentDeficiency);
            double kinPct = kinRaw * 100;
            txtKinematicsScore.Text       = kinPct.ToString("F0", ic);
            txtKinematicsScore.Foreground = ScoreBrush(kinRaw);
            txtKinematicsDetail.Text      = string.Format(ic,
                "jerk:{0:F2}  lin:{1:F2}",
                p.Features.KinematicJerk, p.Features.MicroAdjustmentDeficiency);
        }
        else
        {
            txtSuspicion.Text        = "--";
            txtSuspicion.Foreground  = BrushMuted;
            txtCertainty.Text        = "certainty: --";
            txtPrediction.Text       = "PREDICTION: COLLECTING";
            txtPrediction.Foreground = BrushMuted;
            txtWhy.Text              = "Move the mouse to collect interval samples.";
            UpdateScoreBar(0);

            txtSilentAimScore.Text        = "--";
            txtSilentAimScore.Foreground  = BrushMuted;
            txtSilentAimDetail.Text       = "snap:--  prec:--";
            txtTrigbotScore.Text          = "--";
            txtTrigbotScore.Foreground    = BrushMuted;
            txtTrigbotDetail.Text         = "pause:--  reg:--";
            txtKinematicsScore.Text       = "--";
            txtKinematicsScore.Foreground = BrushMuted;
            txtKinematicsDetail.Text      = "jerk:--  trem:--";
        }

        txtModelInfo.Text = p.TrainingSamples == 0
            ? "model: heuristic prior · 0 labels"
            : $"model: online logistic regression · {p.TrainingSamples} labels";
    }

    private void ScoreTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastStats != null) UpdateScoreBar(_lastStats.Prediction.Suspicion);
    }

    private void UpdateScoreBar(double suspicion)
    {
        scoreFill.Width = Math.Clamp(suspicion, 0, 1) * scoreTrack.ActualWidth;
    }

    private static Brush ScoreBrush(double suspicion) =>
        suspicion >= 0.80 ? BrushScoreInjection :
        suspicion >= 0.45 ? BrushScoreSuspicious :
                            BrushScoreNatural;

    // ── Verdict ──────────────────────────────────────────────────────────────

    private void ApplyVerdict(EngineStats s)
    {
        string pct = (s.Prediction.Suspicion * 100).ToString("F1", CultureInfo.InvariantCulture);

        var (title, subtitle, fgColor, bgColor, borderColor) = s.Verdict switch
        {
            Verdict.Natural    => ("✓  LIKELY NATURAL",
                                   $"{pct}% suspicion — physical jitter and kinematics look normal",
                                   Color.FromRgb(0, 230, 120),
                                   Color.FromRgb(0, 20, 10),
                                   Color.FromRgb(0, 80, 40)),

            Verdict.Suspicious => ("?  SUSPICIOUS SIGNAL",
                                   $"{pct}% suspicion — {s.Prediction.Reason}",
                                   Color.FromRgb(255, 215, 0),
                                   Color.FromRgb(25, 18, 0),
                                   Color.FromRgb(100, 75, 0)),

            Verdict.Injection  => ("!  LIKELY INJECTION",
                                   $"{pct}% suspicion — pattern rarely produced by real USB hardware",
                                   Color.FromRgb(255, 70, 70),
                                   Color.FromRgb(28, 0, 5),
                                   Color.FromRgb(120, 20, 30)),

            _                  => ("…  COLLECTING DATA",
                                   $"Move the mouse — {s.FilledSamples}/30 samples collected",
                                   Color.FromRgb(85, 85, 119),
                                   Color.FromRgb(13, 13, 30),
                                   Color.FromRgb(51, 51, 85)),
        };

        txtVerdictTitle.Text       = title;
        txtVerdictTitle.Foreground = new SolidColorBrush(fgColor);
        txtVerdictSub.Text         = subtitle;
        verdictBorder.Background   = new SolidColorBrush(bgColor);
        verdictBorder.BorderBrush  = new SolidColorBrush(borderColor);
    }

    // ── Sparkline ────────────────────────────────────────────────────────────

    private void SparkCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastStats != null) DrawSparkline(_lastStats);
    }

    private void DrawSparkline(EngineStats s)
    {
        double w = sparkCanvas.ActualWidth;
        double h = sparkCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double barWidth = w / MouseEngine.SparkLen;
        double avgDt    = s.AvgHz > 0 ? 1000.0 / s.AvgHz : 8.0;
        double scaleMax = Math.Max(avgDt * 2.5, 1.0);

        txtSparkScale.Text = $"scale: 0 ms (burst) → {(avgDt * 2.5).ToString("F1", CultureInfo.InvariantCulture)} ms (tall)";

        for (int i = 0; i < MouseEngine.SparkLen; i++)
        {
            var r = _sparkRects[i];
            r.Width = Math.Max(barWidth - 1, 1);

            if (i < s.SparkCount)
            {
                if (s.SparkBurst[i])
                {
                    r.Height = 6;
                    r.Fill   = BrushBurst;
                }
                else
                {
                    r.Height = Math.Clamp(s.Spark[i] / scaleMax * h, 2, h);
                    r.Fill   = BrushBar;
                }
            }
            else
            {
                r.Height = 2;
                r.Fill   = BrushEmpty;
            }

            Canvas.SetLeft(r, i * barWidth);
            Canvas.SetTop(r, h - r.Height);
        }
    }

    // ── Log ──────────────────────────────────────────────────────────────────

    private void AddLog(EngineStats s)
    {
        var ic = CultureInfo.InvariantCulture;
        string entry = s.FilledSamples >= 2
            ? $"[{DateTime.Now:HH:mm:ss}] {s.InstantHz,4}Hz avg:{s.AvgHz.ToString("F0", ic),6} σ:{s.StdDev.ToString("F3", ic)} burst:{(s.BurstRate * 100).ToString("F1", ic)}% score:{(s.Prediction.Suspicion * 100).ToString("F0", ic)}% {VerdictTag(s.Verdict)}"
            : $"[{DateTime.Now:HH:mm:ss}]  collecting... ({s.FilledSamples}/30 samples)";

        logList.Items.Add(entry);
        if (logList.Items.Count > MaxLogEntries)
            logList.Items.RemoveAt(0);
        if (logList.Items.Count > 0)
            logList.ScrollIntoView(logList.Items[logList.Items.Count - 1]);
    }

    private static string VerdictTag(Verdict v) => v switch
    {
        Verdict.Natural    => "[NATURAL]",
        Verdict.Suspicious => "[SUSPICIOUS]",
        Verdict.Injection  => "[INJECTION]",
        _                  => "[waiting]"
    };

    // ── Feedback ─────────────────────────────────────────────────────────────

    private void LabelNatural_Click(object sender, RoutedEventArgs e) => LabelCurrent(false);
    private void LabelInjected_Click(object sender, RoutedEventArgs e) => LabelCurrent(true);

    private void LabelCurrent(bool injected)
    {
        if (!_engine.LabelCurrent(injected))
        {
            txtFeedback.Text = "Need at least 30 movement samples before labeling.";
            txtFeedback.Foreground = BrushScoreSuspicious;
            return;
        }

        txtFeedback.Text = injected
            ? "Labeled as injected — model weights updated locally."
            : "Labeled as natural — model weights updated locally.";
        txtFeedback.Foreground = injected ? BrushScoreInjection : BrushScoreNatural;
    }

    private void ResetModel_Click(object sender, RoutedEventArgs e)
    {
        _engine.ResetModel();
        txtFeedback.Text = "Model reset to the heuristic prior.";
        txtFeedback.Foreground = BrushMuted;
    }

    // ── Feature display ─────────────────────────────────────────────────────

    private static string FeatureLine(DetectionFeatures f)
    {
        var ic = CultureInfo.InvariantCulture;
        return string.Format(ic,
            "inj:[bst:{0:F2} jit:{1:F2} cnc:{2:F2} rep:{3:F2} unf:{4:F2} cls:{5:F2}]  " +
            "s-aim:[snp:{6:F2} prc:{7:F2}]  t-bot:[pse:{8:F2} reg:{9:F2}]  " +
            "kin:[jrk:{10:F2} lin:{11:F2}]",
            f.BurstRate, f.LowJitter, f.IntervalConcentration,
            f.RepeatedIntervals, f.MovementUniformity, f.BurstClustering,
            f.SilentAimSnapRate, f.SilentAimReturnPrecision,
            f.TrigbotMicroPause, f.TrigbotPauseRegularity,
            f.KinematicJerk, f.MicroAdjustmentDeficiency);
    }

    // ── Color helpers ───────────────────────────────────────────────────────

    private static Brush StdDevBrush(double sd) =>
        sd <= 0.015 ? new SolidColorBrush(Color.FromRgb(255, 70, 70)) :
        sd <= 0.060 ? new SolidColorBrush(Color.FromRgb(255, 215, 0)) :
                      new SolidColorBrush(Color.FromRgb(0, 230, 120));

    private static Brush BurstBrush(double rate) =>
        rate >= 0.20 ? new SolidColorBrush(Color.FromRgb(255, 70, 70)) :
        rate >= 0.10 ? new SolidColorBrush(Color.FromRgb(255, 215, 0)) :
                       new SolidColorBrush(Color.FromRgb(0, 230, 120));
}
