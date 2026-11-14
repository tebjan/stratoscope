namespace Stratoscope.Core;

/// <summary>The curves of the history graph.</summary>
public enum HistoryCurve { Prefill, Decode, Total }

/// <summary>One running request as the readout bar shows it.</summary>
/// <param name="Reading">True while the request reads its prompt (the bar is its progress), false while it generates (the bar is its speed).</param>
/// <param name="Fraction">Prefill progress 0..1 while reading; unused while generating.</param>
/// <param name="TokensPerSecond">The request's own decode speed, or null when the engine does not report it.</param>
public readonly record struct RequestBar(bool Reading, double Fraction, double? TokensPerSecond);

/// <summary>
/// The per-request bars of the taskbar readout. One bar per running request, at most <see cref="MaxBars"/>. Source: the batch slots the
/// engine reports (state, prompt read and prompt tokens, the slot's own generated-token speed). A single request, or an engine without
/// slots, is one bar from the live request, so one request looks as it always did.
/// </summary>
public static class RequestBars
{
    public const int MaxBars = 8;

    public static IReadOnlyList<RequestBar> From(EngineSnapshot? s)
    {
        if (s is null || !s.IsBusy) return Array.Empty<RequestBar>();
        var active = s.Slots?.Where(sl => sl.IsReading || sl.IsDecoding).ToList();
        if (active != null && active.Count > 1)
        {
            var list = new List<RequestBar>(Math.Min(MaxBars, active.Count));
            foreach (var slot in active.Take(MaxBars))
                list.Add(slot.IsReading
                    ? new RequestBar(true, Display.SlotFraction(slot, 0), null)
                    : new RequestBar(false, 0, slot.TokensPerSecond));
            return list;
        }
        switch (s.Status)
        {
            case EngineStatus.Prefill: return new[] { new RequestBar(true, (s.Live?.Percent ?? 0) / 100.0, null) };
            case EngineStatus.Thinking:
            case EngineStatus.Decode: return new[] { new RequestBar(false, 0, s.Live?.DecodeTokensPerSecond) };
            default: return Array.Empty<RequestBar>();     // running without a phase: no bar, the state dot says it
        }
    }

    /// <summary>The largest decode speed among the bars, or null when none is known. What the speed scale is fed.</summary>
    public static double? PeakSpeed(IReadOnlyList<RequestBar> bars)
    {
        double? peak = null;
        foreach (var b in bars) if (!b.Reading && b.TokensPerSecond is double v && double.IsFinite(v)) peak = Math.Max(peak ?? 0, v);
        return peak;
    }
}

/// <summary>
/// The last 60 seconds of speed of one server, for the history graph and the adaptive scales. One sample per poll.
/// Curves (tokens per second, all from the engine's own numbers; an unknown value is NaN and breaks the line):
///  - Prefill: <c>Live.PrefillTokensPerSecond</c>, prompt tokens per second.
///  - Decode: <c>Live.DecodeTokensPerSecond</c>, generated tokens per second.
///  - Total: tokens processed per second through the system, prompt plus generated, the throughput meaning of vLLM
///    (<c>vllm:prompt_tokens_total</c> + <c>vllm:generation_tokens_total</c> as a rate) and of the OpenTelemetry GenAI token usage
///    (<c>gen_ai.client.token.usage</c>, input and output tokens). It is the sum of the two curves above and is unknown when either is unknown.
/// While the engine reports it is idle, all three are 0 (nothing runs). While one request reads, it does not write, so an unreported
/// decode speed is 0 then, and the other way round; with parallel requests an unreported speed stays unknown.
/// The samples are raw. The smoothing (<see cref="OnePoleFilter"/>) is applied when the graph reads them, so changing the setting changes
/// the whole window at once.
/// </summary>
public sealed class SpeedHistory
{
    public const double WindowSeconds = 60;
    public const int Capacity = 1024;     // a poll every 2 s fills 30 of them; the ring survives much faster polling

    private readonly double[] time = new double[Capacity];
    private readonly double[][] values = { new double[Capacity], new double[Capacity], new double[Capacity] };
    private int start, count;

    /// <summary>The scale of the per-request bars in the taskbar readout (one request's decode speed).</summary>
    public SpeedScale RequestScale { get; } = new SpeedScale();
    /// <summary>The scale of the history graph (the largest of the three curves).</summary>
    public SpeedScale GraphScale { get; } = new SpeedScale();
    /// <summary>The scales of the bar charts of the Speed section (the speed of the last finished requests): prefill from 100 t/s, decode from 10 t/s.</summary>
    public SpeedScale PrefillChartScale { get; } = new SpeedScale(100);
    public SpeedScale DecodeChartScale { get; } = new SpeedScale(10);

    public int Count => count;

    public static double Seconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Records the state of one poll. A null snapshot (nothing answers) records unknown values.</summary>
    public void Add(DateTime utc, EngineSnapshot? s)
    {
        var (prefill, decode) = Rates(s);
        AddSample(Seconds(utc), prefill, decode);
        if (RequestBars.PeakSpeed(RequestBars.From(s)) is double peak) RequestScale.Observe(peak, Seconds(utc));
        if (s != null && s.Recent.Count > 0)
        {
            // The charts of the finished requests share the rule of the other scales: they do not change with every poll.
            var recent = s.Recent.Take(12).ToList();
            var decodeMax = recent.Max(r => r.DecodeTokensPerSecond ?? 0);
            var prefillMax = recent.Max(r => r.PrefillTokensPerSecond(1500, 2000) ?? 0);
            DecodeChartScale.Observe(decodeMax, Seconds(utc));
            PrefillChartScale.Observe(prefillMax, Seconds(utc));
        }
    }

    /// <summary>The prefill and decode speed of a snapshot as the curves take them (see the class comment).</summary>
    public static (double Prefill, double Decode) Rates(EngineSnapshot? s)
    {
        if (s is null || s.IsError || !s.HasModel || s.IsLoading) return (double.NaN, double.NaN);
        if (s.Status == EngineStatus.Idle) return (0, 0);
        double P(double? v) => v is double d && double.IsFinite(d) ? d : double.NaN;
        var prefill = P(s.Live?.PrefillTokensPerSecond);
        var decode = P(s.Live?.DecodeTokensPerSecond);
        if (s.Live?.IsParallel != true)
        {
            if (double.IsNaN(decode) && s.Status == EngineStatus.Prefill) decode = 0;
            if (double.IsNaN(prefill) && (s.Status == EngineStatus.Decode || s.Status == EngineStatus.Thinking)) prefill = 0;
        }
        return (prefill, decode);
    }

    /// <summary>Adds one sample; the total is prefill plus decode. Samples older than <see cref="WindowSeconds"/> are dropped.</summary>
    public void AddSample(double nowSeconds, double prefill, double decode)
    {
        while (count > 0 && nowSeconds - time[start] > WindowSeconds) { start = (start + 1) % Capacity; count--; }
        if (count == Capacity) { start = (start + 1) % Capacity; count--; }
        var i = (start + count) % Capacity;
        var total = double.IsNaN(prefill) || double.IsNaN(decode) ? double.NaN : prefill + decode;
        time[i] = nowSeconds;
        values[0][i] = prefill; values[1][i] = decode; values[2][i] = total;
        count++;
        double Known(double v) => double.IsNaN(v) ? 0 : v;
        GraphScale.Observe(Math.Max(Known(total), Math.Max(Known(prefill), Known(decode))), nowSeconds);
    }

    /// <summary>
    /// Copies one curve, smoothed with damping <paramref name="damping"/>, into the caller's arrays: the age of each sample in seconds
    /// (0 = now, up to <see cref="WindowSeconds"/>) and its value (NaN where unknown). Returns the number of samples. The arrays must hold
    /// <see cref="Count"/> items.
    /// </summary>
    public int ReadSmoothed(HistoryCurve curve, double damping, double nowSeconds, double[] ages, double[] output)
    {
        var filter = new OnePoleFilter(damping);
        var source = values[(int)curve];
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            var i = (start + k) % Capacity;
            var age = nowSeconds - time[i];
            if (age > WindowSeconds) continue;
            ages[n] = Math.Max(0, age); output[n] = filter.Step(source[i]); n++;
        }
        return n;
    }

    /// <summary>
    /// The average of the raw samples of a curve over the last <see cref="WindowSeconds"/> (the smoothing is not applied), counting the samples
    /// with a known value; NaN when there is none. The legend of the history graph shows it.
    /// </summary>
    public double Average(HistoryCurve curve, double nowSeconds)
    {
        var source = values[(int)curve];
        double sum = 0; int n = 0;
        for (int k = 0; k < count; k++)
        {
            var i = (start + k) % Capacity;
            if (nowSeconds - time[i] > WindowSeconds || double.IsNaN(source[i])) continue;
            sum += source[i]; n++;
        }
        return n == 0 ? double.NaN : sum / n;
    }

    /// <summary>The newest raw value of a curve, or NaN when there is none.</summary>
    public double Latest(HistoryCurve curve) => count == 0 ? double.NaN : values[(int)curve][(start + count - 1) % Capacity];
}
