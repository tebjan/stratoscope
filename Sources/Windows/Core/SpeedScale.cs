namespace Stratoscope.Core;

/// <summary>
/// An adaptive speed scale for bars and graphs: 0 to <see cref="Max"/> tokens per second. The maximum is slow on purpose.
/// Rise: a value above the maximum sets the maximum to that value plus <see cref="RiseMargin"/>, rounded up to a nice number (1, 2, 2.5 or 5 times a power of ten)
/// (126 becomes 200), so a speed that creeps upwards does not move the scale with every sample.
/// Fall: when every value stays below <see cref="FallBelow"/> of the maximum for <see cref="FallHoldSeconds"/>, the maximum steps back to the
/// next nice number above the peak of that period (never below <see cref="Floor"/>). The new maximum is above the peak by the margin, so the
/// values that caused the step are above half of it: the scale does not oscillate, and a second step needs another full period.
/// </summary>
public sealed class SpeedScale
{
    /// <summary>The share of the scale below which values count as low.</summary>
    public const double FallBelow = 0.5;
    /// <summary>How long every value must stay low before the maximum steps back, in seconds.</summary>
    public const double FallHoldSeconds = 25;
    /// <summary>The room above a value when the maximum rises or falls to it.</summary>
    public const double RiseMargin = 0.05;
    /// <summary>The maximum a scale starts at and never falls below, tokens per second: a request speed that fits a single local engine.</summary>
    public const double DefaultMax = 100;

    private double lowSince = double.NaN;
    private double lowPeak;

    /// <param name="initialMax">The first maximum; also the floor. Use what the engine reports when it does, else <see cref="DefaultMax"/>.</param>
    public SpeedScale(double initialMax = DefaultMax)
    {
        Floor = initialMax > 0 ? RoundUp(initialMax) : DefaultMax;
        Max = Floor;
    }

    public double Floor { get; }
    /// <summary>The lower mark of the scale. A speed is never negative.</summary>
    public double Min => 0;
    public double Max { get; private set; }

    /// <summary>The fraction of the scale a value fills, 0..1.</summary>
    public double Fraction(double value) => double.IsFinite(value) && value > 0 ? Math.Min(1, value / Max) : 0;

    /// <summary>Feeds one observation: the largest speed shown now. <paramref name="nowSeconds"/> is any monotonic clock. Unknown values are not fed.</summary>
    public double Observe(double value, double nowSeconds)
    {
        if (!double.IsFinite(value) || value < 0) return Max;
        if (value > Max)
        {
            Max = RoundUp(value * (1 + RiseMargin));
            lowSince = double.NaN;
            return Max;
        }
        if (value >= Max * FallBelow)
        {
            lowSince = double.NaN;
            return Max;
        }
        if (double.IsNaN(lowSince)) { lowSince = nowSeconds; lowPeak = value; }
        else lowPeak = Math.Max(lowPeak, value);
        if (nowSeconds - lowSince >= FallHoldSeconds)
        {
            var target = Math.Max(Floor, RoundUp(lowPeak * (1 + RiseMargin)));
            if (target < Max) Max = target;
            lowSince = double.NaN;
        }
        return Max;
    }

    private static readonly double[] NiceMantissas = { 1, 2, 2.5, 5, 10 };

    /// <summary>
    /// The smallest "nice number" at or above <paramref name="value"/>: 1, 2, 2.5, 5 times a power of ten (100, 200, 250, 500, 1000, ...).
    /// Followed: the "nice numbers" of Heckbert's graph-axis algorithm (Graphics Gems, 1990), which axis code in monitoring graphs such as uPlot and
    /// Grafana uses in the same way (increments of 1, 2, 2.5, 5), and the usual auto-range that rises at once and shrinks only after a hold; so the scale takes a few distinct values
    /// and the labels stay readable.
    /// </summary>
    public static double RoundUp(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in NiceMantissas)
            if (m * magnitude >= value * (1 - 1e-9)) return m * magnitude;
        return 10 * magnitude;
    }
}

/// <summary>
/// A one-pole IIR low-pass, <c>y = d * y + (1 - d) * x</c>, with the damping <c>d</c> of the chosen <see cref="HistorySmoothing"/>.
/// The first value, and the first one after an unknown value, passes unchanged.
/// </summary>
public struct OnePoleFilter
{
    private double y;
    private bool primed;

    public OnePoleFilter(double damping) { Damping = Math.Clamp(damping, 0, 0.999); y = 0; primed = false; }

    public double Damping { get; }

    /// <summary>Feeds one sample and returns the filtered one. NaN (unknown) resets the filter and returns NaN.</summary>
    public double Step(double x)
    {
        if (!double.IsFinite(x)) { primed = false; return double.NaN; }
        y = primed ? Damping * y + (1 - Damping) * x : x;
        primed = true;
        return y;
    }
}

/// <summary>How strongly the history graph is smoothed (setting <c>history_smoothing</c>).</summary>
public enum HistorySmoothing
{
    Off,
    /// <summary>Default.</summary>
    Normal,
    High,
    Highest,
}

public static class HistorySmoothings
{
    /// <summary>The damping d of the filter: 0 (off), 0.5, 0.9, 0.95.</summary>
    public static double Damping(this HistorySmoothing s) => s switch
    {
        HistorySmoothing.Off => 0, HistorySmoothing.High => 0.9, HistorySmoothing.Highest => 0.95, _ => 0.5,
    };
}
