using Stratoscope.Core;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

public class SpeedScaleTests
{
    [Theory]
    [InlineData(0.8, 1)] [InlineData(1, 1)] [InlineData(1.1, 2)] [InlineData(2.2, 2.5)] [InlineData(2.6, 5)] [InlineData(5.1, 10)]
    [InlineData(99, 100)] [InlineData(126, 200)] [InlineData(201, 250)] [InlineData(251, 500)] [InlineData(501, 1000)] [InlineData(1260, 2000)]
    public void TheMaximumIsANiceNumber(double value, double expected) => Assert.Equal(expected, SpeedScale.RoundUp(value), 9);

    [Fact]
    public void ARiseAddsAMarginAndRoundsUpToANiceNumberAtOnce()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(200, scale.Observe(120, 0));       // 120 + 5 % = 126, up to the next of 1, 2, 2.5, 5
        Assert.Equal(250, scale.Observe(205, 1));       // 215.25
        Assert.Equal(250, scale.Max);
    }

    [Fact]
    public void TheMaximumDoesNotMoveWhileValuesStayBelowIt()
    {
        var scale = new SpeedScale(100);
        for (int i = 0; i < 100; i++) Assert.Equal(100, scale.Observe(60 + (i % 7) * 5, i));
        scale.Observe(150, 100);                        // up to 200
        foreach (var v in new[] { 151.0, 170, 199, 160, 190 }) Assert.Equal(200, scale.Observe(v, 101));      // a creeping speed does not move it
    }

    [Fact]
    public void TheMaximumHoldsThenFallsToTheNextNiceNumberAboveThePeak()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);
        Assert.Equal(500, scale.Max);                    // 420 up to 500
        // Everything below half of 500 (250) for less than the hold time: nothing changes.
        for (double t = 1; t < SpeedScale.FallHoldSeconds; t += 1) Assert.Equal(500, scale.Observe(100, t));
        // At the hold time: the next nice number above the peak of the period (100 + 5 % = 105).
        Assert.Equal(200, scale.Observe(100, SpeedScale.FallHoldSeconds + 1));
        // Idle for another period: down to the floor, not below.
        double now = SpeedScale.FallHoldSeconds + 1;
        for (int i = 0; i < 100; i++) scale.Observe(0, now += 1);
        Assert.Equal(20, scale.Max);
    }

    [Fact]
    public void AValueAtHalfOfTheMaximumRestartsTheHoldTime()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);                                   // 500
        for (double t = 1; t < 20; t++) scale.Observe(100, t);
        scale.Observe(260, 20);                                  // at least half: the period starts over
        for (double t = 21; t < 20 + SpeedScale.FallHoldSeconds; t++) Assert.Equal(500, scale.Observe(100, t));
        Assert.True(scale.Observe(100, 20 + SpeedScale.FallHoldSeconds + 1) < 500);
    }

    [Fact]
    public void ASteppedDownScaleDoesNotOscillate()
    {
        var scale = new SpeedScale(20);
        scale.Observe(400, 0);
        double now = 0;
        // A steady 100 t/s for two minutes: one step down, then it stays (100 is at half of the new maximum, and below it).
        var seen = new HashSet<double>();
        for (int i = 0; i < 120; i++) seen.Add(scale.Observe(100, now += 1));
        Assert.Equal(new HashSet<double> { 500, 200 }, seen);
    }

    [Fact]
    public void ASpeedThatSwingsAroundTheMaximumNeverMakesTheScaleChangePerSample()
    {
        var scale = new SpeedScale(100);
        int changes = 0; double last = scale.Max;
        for (int i = 0; i < 600; i++)
        {
            var v = 90 + 30 * Math.Sin(i * 0.9);        // 60 to 120 around the first maximum
            var max = scale.Observe(v, i);
            if (max != last) { changes++; last = max; }
        }
        Assert.Equal(1, changes);                        // one rise to 200, never back: the peak stays above half of it
    }

    [Fact]
    public void UnknownValuesAreIgnored()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(100, scale.Observe(double.NaN, 0));
        Assert.Equal(100, scale.Observe(double.PositiveInfinity, 1));
        Assert.Equal(100, scale.Observe(-5, 2));
        Assert.Equal(0, scale.Fraction(double.NaN));
    }

    [Fact]
    public void TheFractionIsTheShareOfTheScale()
    {
        var scale = new SpeedScale(100);
        Assert.Equal(0.25, scale.Fraction(25));
        Assert.Equal(1, scale.Fraction(500));
        Assert.Equal(0, scale.Min);
    }

    [Fact]
    public void TheChartScalesOfTheSpeedSectionFollowTheSameRule()
    {
        var h = new SpeedHistory();
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Idle);
        s.Recent.Add(new RequestStats { DecodeTokensPerSecond = 30, PromptRead = 20_000, PromptMilliseconds = 40_000 });     // prefill 500 t/s
        h.Add(DateTime.UnixEpoch.AddSeconds(1000), s);
        Assert.Equal(50, h.DecodeChartScale.Max);        // 30 + 5 % up from 10
        Assert.Equal(1000, h.PrefillChartScale.Max);     // 500 + 5 % up from 100
        s.Recent[0].DecodeTokensPerSecond = 28;
        h.Add(DateTime.UnixEpoch.AddSeconds(1002), s);
        Assert.Equal(50, h.DecodeChartScale.Max);        // not changed by the next poll
    }
}

public class OnePoleFilterTests
{
    [Theory]
    [InlineData(HistorySmoothing.Off, 0.0)]
    [InlineData(HistorySmoothing.Normal, 0.5)]
    [InlineData(HistorySmoothing.High, 0.9)]
    [InlineData(HistorySmoothing.Highest, 0.95)]
    public void TheSettingsHaveTheirDamping(HistorySmoothing smoothing, double damping) => Assert.Equal(damping, smoothing.Damping());

    [Fact]
    public void ItFollowsYEqualsDYPlusOneMinusDX()
    {
        var f = new OnePoleFilter(0.5);
        Assert.Equal(10, f.Step(10));                 // the first value passes
        Assert.Equal(15, f.Step(20), 9);              // 0.5 * 10 + 0.5 * 20
        Assert.Equal(7.5, f.Step(0), 9);
    }

    [Fact]
    public void OffPassesEveryValue()
    {
        var f = new OnePoleFilter(0);
        foreach (var v in new[] { 3.0, 99, 0, 12 }) Assert.Equal(v, f.Step(v));
    }

    [Fact]
    public void ASlowerFilterReactsLessToAStep()
    {
        double After(double d) { var f = new OnePoleFilter(d); f.Step(0); return f.Step(100); }
        Assert.True(After(0.5) > After(0.9) && After(0.9) > After(0.95));
        Assert.Equal(5, After(0.95), 9);
    }

    [Fact]
    public void AnUnknownValueBreaksTheLineAndRestartsTheFilter()
    {
        var f = new OnePoleFilter(0.9);
        f.Step(100);
        Assert.True(double.IsNaN(f.Step(double.NaN)));
        Assert.Equal(40, f.Step(40));
    }
}

public class SpeedHistoryTests
{
    private static readonly double[] Ages = new double[SpeedHistory.Capacity];
    private static readonly double[] Values = new double[SpeedHistory.Capacity];

    [Fact]
    public void TheBufferKeepsTheLastSixtySeconds()
    {
        var h = new SpeedHistory();
        for (int t = 0; t <= 200; t += 2) h.AddSample(t, 10, 20);
        Assert.Equal(31, h.Count);                        // 140 to 200 s, both ends included
        var n = h.ReadSmoothed(HistoryCurve.Decode, 0, 200, Ages, Values);
        Assert.Equal(31, n);
        Assert.Equal(60, Ages[0]);
        Assert.Equal(0, Ages[n - 1]);
        Assert.All(Values.Take(n), v => Assert.Equal(20, v));
    }

    [Fact]
    public void TheTotalIsPromptPlusGeneratedAndUnknownWhenEitherIs()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 100, 30);
        Assert.Equal(130, h.Latest(HistoryCurve.Total));
        h.AddSample(2, double.NaN, 30);
        Assert.True(double.IsNaN(h.Latest(HistoryCurve.Total)));
        Assert.Equal(30, h.Latest(HistoryCurve.Decode));
    }

    [Fact]
    public void TheCurvesAreSmoothedWhenReadAndRawInside()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);
        h.AddSample(2, 0, 100);
        Assert.Equal(100, h.Latest(HistoryCurve.Decode));
        var n = h.ReadSmoothed(HistoryCurve.Decode, HistorySmoothing.Normal.Damping(), 2, Ages, Values);
        Assert.Equal(2, n);
        Assert.Equal(50, Values[1], 9);
        h.ReadSmoothed(HistoryCurve.Decode, HistorySmoothing.Off.Damping(), 2, Ages, Values);
        Assert.Equal(100, Values[1]);
    }

    [Fact]
    public void TheGraphScaleFollowsTheLargestCurve()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 400, 50);                           // total 450
        Assert.True(h.GraphScale.Max >= 450);
    }

    [Fact]
    public void AnIdleEngineIsZeroAndAnOfflineOneIsUnknown()
    {
        Assert.Equal((0.0, 0.0), SpeedHistory.Rates(new EngineSnapshot("Strata", "m", EngineStatus.Idle)));
        var (p, d) = SpeedHistory.Rates(null);
        Assert.True(double.IsNaN(p) && double.IsNaN(d));
    }

    [Fact]
    public void OneReadingRequestDoesNotWriteButParallelOnesStayUnknown()
    {
        var one = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PrefillTokensPerSecond = 900 } };
        Assert.Equal((900.0, 0.0), SpeedHistory.Rates(one));
        var parallel = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PrefillTokensPerSecond = 900, Running = 3 } };
        var (_, decode) = SpeedHistory.Rates(parallel);
        Assert.True(double.IsNaN(decode));
    }
}

public class RequestBarTests
{
    private static BatchSlot Slot(int i, string state, double? read = null, double? total = null, double? tps = null) =>
        new BatchSlot { Index = i, State = state, PromptRead = read, PromptTokens = total, TokensPerSecond = tps };

    [Fact]
    public void OneRequestIsOneBarWithItsPrefillProgress()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Prefill) { Live = new LiveRequest { PromptRead = 500, PromptTotal = 1000 } };
        var bars = RequestBars.From(s);
        Assert.Single(bars);
        Assert.True(bars[0].Reading);
        Assert.Equal(0.5, bars[0].Fraction);
    }

    [Fact]
    public void OneWritingRequestIsOneBarWithItsSpeed()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode) { Live = new LiveRequest { DecodeTokensPerSecond = 26 } };
        var bars = RequestBars.From(s);
        Assert.Single(bars);
        Assert.False(bars[0].Reading);
        Assert.Equal(26, bars[0].TokensPerSecond);
    }

    [Fact]
    public void ParallelRequestsAreOneBarEachWithTheirOwnPhase()
    {
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode)
        {
            Live = new LiveRequest { Running = 3, DecodeTokensPerSecond = 60 },
            Slots = new List<BatchSlot> { Slot(0, "decoding", tps: 30), Slot(1, "reading", 100, 400), Slot(2, "idle"), Slot(3, "decoding", tps: 25) },
        };
        var bars = RequestBars.From(s);
        Assert.Equal(3, bars.Count);
        Assert.False(bars[0].Reading); Assert.Equal(30, bars[0].TokensPerSecond);
        Assert.True(bars[1].Reading); Assert.Equal(0.25, bars[1].Fraction);
        Assert.Equal(30, RequestBars.PeakSpeed(bars));
    }

    [Fact]
    public void AtMostEightBars()
    {
        var slots = Enumerable.Range(0, 12).Select(i => Slot(i, "decoding", tps: 10)).ToList();
        var s = new EngineSnapshot("Strata", "m", EngineStatus.Decode) { Live = new LiveRequest { Running = 12 }, Slots = slots };
        Assert.Equal(8, RequestBars.From(s).Count);
    }

    [Fact]
    public void NoPhaseAndNoBusyMeanNoBars()
    {
        Assert.Empty(RequestBars.From(new EngineSnapshot("Strata", "m", EngineStatus.Running)));
        Assert.Empty(RequestBars.From(new EngineSnapshot("Strata", "m", EngineStatus.Idle)));
        Assert.Empty(RequestBars.From(null));
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void TheStackFitsTheSpaceAndOneBarKeepsItsHeight(int dpi)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        Assert.Equal(ReadoutPainter.GlyphHeightPx(m, true), ReadoutPainter.BarRows(1, m).Total);
        for (int n = 2; n <= 8; n++)
        {
            var stack = ReadoutPainter.BarRows(n, m); var (bar, gap, total) = (stack.Bar, stack.Gap, stack.Total);
            Assert.True(bar >= 2);
            Assert.Equal(n * bar + stack.Extra + (n - 1) * gap, total);
            Assert.True(total <= m.Px(ReadoutPainter.MaxStackHeight), n + " bars at " + dpi + " DPI");
            Assert.True(bar >= 2 && gap >= 1, n + " bars at " + dpi + " DPI");
        }
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void TheStackGrowsWithTheCountButBarsGetThinner(int dpi)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        var single = ReadoutPainter.BarRows(1, m);
        var previous = single;
        for (int n = 2; n <= 8; n++)
        {
            var rows = ReadoutPainter.BarRows(n, m);
            Assert.True(rows.Total >= previous.Total, "taller or equal at " + n);
            Assert.True(rows.HeightOf(0) <= previous.HeightOf(0), "thinner or equal at " + n);
            Assert.True(rows.HeightOf(0) < single.Bar);
            Assert.True(rows.Total < single.Total * n, "grows less than the count");
            previous = rows;
        }
    }
}

public class WindowAverageTests
{
    [Fact]
    public void TheAverageIsOfTheRawSamplesOfTheWindowAndSkipsUnknownOnes()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);                      // older than the window at the end: dropped
        h.AddSample(100, 10, 100);
        h.AddSample(110, 20, 200);
        h.AddSample(120, double.NaN, 300);
        Assert.Equal(15, h.Average(HistoryCurve.Prefill, 120));        // the two known samples
        Assert.Equal(200, h.Average(HistoryCurve.Decode, 120));
        Assert.Equal(165, h.Average(HistoryCurve.Total, 120));         // (110 + 220) / 2; the unknown total is skipped
        Assert.True(double.IsNaN(new SpeedHistory().Average(HistoryCurve.Total, 0)));
    }

    [Fact]
    public void TheAverageIgnoresTheSmoothing()
    {
        var h = new SpeedHistory();
        h.AddSample(0, 0, 0);
        h.AddSample(2, 0, 100);
        Assert.Equal(50, h.Average(HistoryCurve.Decode, 2));
    }
}

public class HistorySmoothingConfigTests
{
    [Theory]
    [InlineData("off", HistorySmoothing.Off)]
    [InlineData("normal", HistorySmoothing.Normal)]
    [InlineData("High", HistorySmoothing.High)]
    [InlineData("highest", HistorySmoothing.Highest)]
    public void TheSettingIsReadFromTheEnvironment(string value, HistorySmoothing expected)
    {
        var config = Config.Load(new Dictionary<string, string?> { ["STRATOSCOPE_HISTORY_SMOOTHING"] = value }, "does-not-exist.json");
        Assert.Equal(expected, config.HistorySmoothing);
        Assert.Empty(config.Problems);
    }

    [Fact]
    public void NormalIsTheDefaultAndAWrongValueIsNamed()
    {
        Assert.Equal(HistorySmoothing.Normal, Config.Load(new Dictionary<string, string?>(), "does-not-exist.json").HistorySmoothing);
        var wrong = Config.Load(new Dictionary<string, string?> { ["STRATOSCOPE_HISTORY_SMOOTHING"] = "heavy" }, "does-not-exist.json");
        Assert.Contains(wrong.Problems, p => p.Contains("history_smoothing"));
    }
}
