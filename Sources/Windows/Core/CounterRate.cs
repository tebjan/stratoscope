namespace Stratoscope.Core;

/// <summary>
/// The rate of one cumulative counter, read every <see cref="SpeedHistory.SampleSeconds"/>. Some engines publish a counter in steps, not per
/// token: the custom 27B engine's <c>q27:generation_tokens_total</c> rises by about a second's worth of tokens once per second. The difference
/// of two neighbouring reads then alternates between 0 and twice the real speed (a ping-pong in the curve, whatever the smoothing is). So the
/// rate is taken between <b>change events</b> (the reads at which the counter was seen to rise), over a span of about two of the engine's own
/// publishing intervals: a steady counter gives the usual short window, a counter that rises once a second gives a two-second window, and the
/// result changes only when a new step arrives and is held until the next one is due.
/// </summary>
/// <remarks>
/// The typical interval is the median of the last intervals between change events. Span: two typical intervals, between 1 and 3 seconds
/// (<see cref="MinSpan"/>, <see cref="MaxSpan"/>). Hold: one and a half intervals, between 1 and 3 seconds; after it the rate is 0 (the counter has
/// stopped). A step that follows a long quiet time is spread over <see cref="BurstSeconds"/>, not over the whole quiet time. A counter that
/// falls (the engine restarted) returns NaN once and starts again from that value.
/// </remarks>
internal sealed class CounterRate
{
    public const double MinSpan = 1.0, MaxSpan = 3.0, MinHold = 1.0, MaxHold = 3.0, BurstSeconds = 1.5;
    private const int MaxEvents = 12, IntervalsForTypical = 5;

    private readonly List<(double T, double V)> events = new();     // the baseline, then every read at which the counter rose
    private double held;

    /// <summary>Forgets everything: the next <see cref="Update"/> is a new baseline.</summary>
    public void Reset() { events.Clear(); held = 0; }

    /// <summary>
    /// Feeds one read (<paramref name="t"/> seconds on any monotonic clock). Returns the rate in counts per second: 0 while nothing rises, NaN for the
    /// one read at which the counter fell. The first read after <see cref="Reset"/> is the baseline and returns 0.
    /// </summary>
    public double Update(double t, double v)
    {
        if (events.Count == 0) { events.Add((t, v)); return 0; }
        var last = events[^1];
        if (v < last.V) { events.Clear(); events.Add((t, v)); held = 0; return double.NaN; }
        if (v > last.V)
        {
            events.Add((t, v));
            if (events.Count > MaxEvents) events.RemoveAt(0);
            held = RateAtNewestEvent();
        }
        if (events.Count < 2) return 0;
        return t - events[^1].T <= Math.Clamp(1.5 * TypicalInterval(), MinHold, MaxHold) ? held : 0;
    }

    private double TypicalInterval()
    {
        var gaps = new List<double>(IntervalsForTypical);
        for (int i = events.Count - 1; i > 0 && gaps.Count < IntervalsForTypical; i--) gaps.Add(Math.Min(events[i].T - events[i - 1].T, MaxSpan));
        gaps.Sort();
        return gaps.Count == 0 ? MinSpan : gaps[gaps.Count / 2];
    }

    private double RateAtNewestEvent()
    {
        var e = events[^1];
        var span = Math.Clamp(2 * TypicalInterval(), MinSpan, MaxSpan);
        // The newest earlier event that is at least one span back; else the oldest one we have (a short history gives a short window).
        var b = 0;
        for (int i = events.Count - 2; i >= 0; i--)
            if (e.T - events[i].T >= span) { b = i; break; }
        var seconds = e.T - events[b].T;
        if (seconds <= 0) return 0;
        if (seconds > MaxSpan)
        {
            // The window reaches back over a long quiet time: the step is one burst, not a rate over the quiet time.
            var prev = events[^2];
            return (e.V - prev.V) / Math.Min(Math.Max(e.T - prev.T, 1e-3), BurstSeconds);
        }
        return (e.V - events[b].V) / seconds;
    }
}
