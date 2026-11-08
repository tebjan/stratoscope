using System.Globalization;

namespace Stratoscope.Core;

/// <summary>
/// The text of the tooltips of the panel and the readout. A tooltip adds data that is not on the element: never its name, its state word
/// or a number it already shows. Each function returns lines of such data (a part the engine did not report is left out, never invented)
/// or null when there is nothing to add; <see cref="NotShown"/> then drops every line the element's visible text already contains.
/// Where each part comes from is listed in docs/WINDOWS.md. The functions are pure: the UI calls them on hover and keeps the result until
/// the data changes.
/// </summary>
public static class Tooltips
{
    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo("en-US");

    private static bool Has(double? v) => v is double d && !double.IsNaN(d) && !double.IsInfinity(d);

    private static string? Ago(DateTime? at, DateTime now) =>
        at.HasValue ? Fmt.Duration(Math.Max(0, (now - at.Value).TotalSeconds)) + " ago" : null;

    /// <summary>Share of <paramref name="part"/> in <paramref name="whole"/> as "59 %", or null.</summary>
    private static string? Percent(double? part, double? whole) =>
        Has(part) && Has(whole) && whole > 0 ? Fmt.Number(part!.Value / whole!.Value * 100) + " %" : null;

    /// <summary>Removes from <paramref name="tip"/> every line that is equal to, or contained in, a text the element already shows.</summary>
    public static string? NotShown(string? tip, IEnumerable<string> visible)
    {
        if (string.IsNullOrEmpty(tip)) return null;
        var shown = visible.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var lines = tip.Split('\n').Where(l => l.Length > 0 && !shown.Any(v => v.Contains(l, StringComparison.OrdinalIgnoreCase))).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // MARK: Requests (the bars of the speed charts and the dots)

    /// <summary>One finished request: when, its cache verdict, prompt, speeds, tokens, how it ended. <paramref name="index"/> 0 is the newest.</summary>
    public static string? Request(IReadOnlyList<RequestStats> recent, int index, DateTime now)
    {
        if (index < 0 || index >= recent.Count) return null;
        var r = recent[index];
        var prompt = Casing.Join(
            Has(r.PromptTokens) ? Fmt.Number(r.PromptTokens) + " prompt tokens" : null,
            Has(r.PromptRead) ? Fmt.Number(r.PromptRead) + " read" : null,
            Has(r.ReusedTokens) ? Fmt.Number(r.ReusedTokens) + " reused" : null);
        var pre = r.PrefillTokensPerSecond(1500, 2000);   // the same rule as the chart
        var speeds = Casing.Join(
            pre.HasValue ? "Prefill " + Fmt.Number(pre) + " t/s" : null,
            Has(r.DecodeTokensPerSecond) ? "Decode " + Fmt.Number(r.DecodeTokensPerSecond, 1) + " t/s" : null);
        var written = Casing.Join(
            Has(r.OutputTokens) ? Fmt.Number(r.OutputTokens) + " tokens written" : null,
            Has(r.DurationSeconds) ? Fmt.Number(r.DurationSeconds, 1) + " s in total" : null);
        var end = Casing.Join(
            string.IsNullOrWhiteSpace(r.Finish) ? null : "Finish " + r.Finish,
            Has(r.ExpertHitRate) ? "Expert hit " + Fmt.Number(r.ExpertHitRate!.Value * 100) + " %" : null);
        return Casing.Lines(Casing.Join(Ago(r.FinishedAt, now), r.Hit?.Label()), prompt, speeds, written, end);
    }

    /// <summary>The cache result of one finished request (a dot, which shows a colour only): reused tokens of the prompt, the verdict, when.</summary>
    public static string? CacheResult(IReadOnlyList<RequestStats> recent, int index, DateTime now)
    {
        if (index < 0 || index >= recent.Count) return null;
        var r = recent[index];
        if (r.Hit is not CacheHit hit) return null;
        return Casing.Lines(Casing.Join(
            Fmt.Number(r.ReusedTokens) + " of " + Fmt.Number(r.PromptTokens) + " tokens reused", hit.Label(), Ago(r.FinishedAt, now)));
    }

    // MARK: Context

    /// <summary>The context bar shows used and maximum, and the free room under it; the tooltip adds the share and the distance to the summarise position.</summary>
    public static string? Context(ContextUsage context)
    {
        var used = context.Used; var max = context.Max; var compactAt = context.CompactAt;
        string? compact = null;
        if (Has(compactAt) && Has(used))
        {
            var away = compactAt!.Value - used!.Value;
            compact = away >= 0
                ? "Summarises at " + Fmt.Number(compactAt) + ": " + Fmt.Number(away) + " tokens away"
                : "Summarises at " + Fmt.Number(compactAt) + ": passed by " + Fmt.Number(-away) + " tokens";
        }
        return Casing.Lines(Percent(used, max) is string p ? p + " used" : null, compact);
    }

    /// <summary>
    /// One bar of the context history (the size of the conversation after a finished request): how long ago, the context size, the prompt
    /// and the answer it is made of, and the change against the request before it. <paramref name="index"/> 0 is the newest.
    /// </summary>
    public static string? ContextAfter(IReadOnlyList<RequestStats> recent, int index, DateTime now)
    {
        if (index < 0 || index >= recent.Count) return null;
        var r = recent[index];
        if (r.ContextTokens is not double size) return null;
        string? change = null;
        if (index + 1 < recent.Count && recent[index + 1].ContextTokens is double before && size != before)
            change = (size > before ? "Up " : "Down ") + Fmt.Number(Math.Abs(size - before)) + " from the request before";
        return Casing.Lines(
            Casing.Join(Ago(r.FinishedAt, now), Fmt.Number(size) + " tokens in context"),
            Casing.Join(Has(r.PromptTokens) ? Fmt.Number(r.PromptTokens) + " prompt" : null, Has(r.OutputTokens) ? Fmt.Number(r.OutputTokens) + " written" : null),
            change);
    }

    // MARK: System rows (each shows its used and total, or its load; the tooltip adds free space, shares and what the row leaves out)

    private static string? Mib(double? bytes) => Has(bytes) ? Fmt.Number(bytes!.Value / 1_048_576) + " MiB" : null;

    public static string? Ram(SystemStats s) =>
        Has(s.RamUsedBytes) && Has(s.RamTotalBytes) ? Casing.Lines(Casing.Join(Fmt.Gib(s.RamTotalBytes - s.RamUsedBytes) + " GB free", Percent(s.RamUsedBytes, s.RamTotalBytes) + " used")) : null;

    public static string? Gpu(SystemStats s) => Has(s.GpuPowerLimitWatts) ? Casing.Lines("Power limit " + Fmt.Number(s.GpuPowerLimitWatts) + " W") : null;

    public static string? Vram(SystemStats s) =>
        Has(s.GpuMemoryUsedBytes) && Has(s.GpuMemoryTotalBytes)
            ? Casing.Lines(Casing.Join(Mib(s.GpuMemoryTotalBytes - s.GpuMemoryUsedBytes) + " free", Percent(s.GpuMemoryUsedBytes, s.GpuMemoryTotalBytes) + " used")) : null;

    /// <summary>The CPU row shows its load and nothing else is known.</summary>
    public static string? Cpu(SystemStats s) => null;

    // MARK: Prompt cache

    /// <summary>The prompt cache bar: the share of the budget, the tokens held and how many requests reused the cache.</summary>
    public static string? PromptCache(CacheStats c)
    {
        var tokens = c.EntriesKnown && c.Entries.Count > 0 ? c.Entries.Sum(e => e.Tokens) : (long?)null;
        return Casing.Lines(
            Percent(c.UsedBytes, c.BudgetBytes) is string p ? p + " of the budget" : null,
            tokens.HasValue ? Fmt.Number(tokens.Value) + " tokens held" : null,
            Has(c.Requests) && Has(c.RequestsReused) ? Fmt.Number(c.RequestsReused) + " of " + Fmt.Number(c.Requests) + " requests reused it" : null);
    }

    /// <summary>A parked conversation of the prompt cache (index into the entries, newest first): its share of the budget. A free slot has nothing to add.</summary>
    public static string? CacheSlot(CacheStats c, int index, DateTime now)
    {
        var list = Enumerable.Reverse(c.Entries).ToList();
        if (index < 0 || index >= list.Count) return null;
        return Casing.Lines(Percent(list[index].Bytes, c.BudgetBytes) is string p ? p + " of the budget" : null);
    }

    /// <summary>A batch slot: the share of the context its tokens take.</summary>
    public static string? BatchSlot(BatchSlot slot, ContextUsage context) =>
        Has(slot.Tokens) && Percent(slot.Tokens, context.Max) is string p ? Casing.Lines(p + " of the context") : null;

    // MARK: Totals

    /// <summary>The totals: since when (local time by default), the exact token counts and the averages the totals allow.</summary>
    public static string? Totals(Totals t, DateTime now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        string? since = null;
        if (t.Since is DateTime at)
            since = Casing.Join("Since " + TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone).ToString("d MMM yyyy HH:mm", Locale));
        double? read = Has(t.PromptTokens) && Has(t.ReusedTokens) ? t.PromptTokens - t.ReusedTokens : null;
        double? prefill = Has(read) && Has(t.PromptMilliseconds) && t.PromptMilliseconds > 0 ? read / (t.PromptMilliseconds / 1000) : null;
        double? decode = Has(t.OutputTokens) && Has(t.DecodeMilliseconds) && t.DecodeMilliseconds > 0 ? t.OutputTokens / (t.DecodeMilliseconds / 1000) : null;
        return Casing.Lines(
            since,
            Casing.Join(Has(t.PromptTokens) ? Fmt.Number(t.PromptTokens) + " prompt tokens" : null,
                Has(read) ? Fmt.Number(read) + " read" : null,
                Has(t.ReusedTokens) ? Fmt.Number(t.ReusedTokens) + " reused" : null),
            prefill.HasValue || decode.HasValue
                ? "Average " + string.Join(" · ", new[] { prefill.HasValue ? "prefill " + Fmt.Number(prefill) + " t/s" : null, decode.HasValue ? "decode " + Fmt.Number(decode, 1) + " t/s" : null }.Where(p => p != null))
                : null);
    }

    // MARK: The readout item

    /// <summary>
    /// While several requests run together the item shows their total: the tooltip adds how many run, each one's own speed and how many
    /// wait. Reading slots show their share of the prompt. Null for a single request.
    /// </summary>
    public static string? Parallel(EngineSnapshot s)
    {
        if (s.Live is not { IsParallel: true } live || s.Slots is null) return null;
        var rows = s.Slots.Where(x => x.IsActive).Select(x =>
            (x.Index + 1) + " " + (x.IsDecoding && Has(x.TokensPerSecond) && x.TokensPerSecond > 0 ? Fmt.Number(x.TokensPerSecond) + " t/s"
                : x.IsReading && Percent(x.PromptRead, x.PromptTokens) is string p ? "reading " + p : "starting")).ToList();
        return Casing.Lines(
            live.Running + " requests running" + (live.Waiting > 0 ? " · " + live.Waiting + " waiting" : ""),
            rows.Count > 0 ? string.Join(" · ", rows) : null);
    }

    /// <summary>
    /// What the readout item does not say: the state in words (the item shows a glyph and a speed only), the share of the prompt read, the
    /// server's name (with one server, it is not on the item), the model, the context, the speeds of the newest finished request and the
    /// queue; the reason while loading or not answering.
    /// </summary>
    public static string? ReadoutExtra(ServerState state, EngineSnapshot? s, string? serverName = null)
    {
        var word = Display.Word(state);
        if (s is null || !s.HasModel) return Casing.Lines(Casing.Join(word, serverName));
        if (s.Status == EngineStatus.Loading || s.Status == EngineStatus.Error) return Casing.Lines(Casing.Join(word, serverName), s.LoadingReason);
        var newest = s.Recent.Count > 0 ? s.Recent[0] : null;
        var pre = newest?.PrefillTokensPerSecond(1500, 2000);
        return Casing.Lines(
            Casing.Join(word, state == ServerState.Reading && Has(s.Live?.Percent) ? Fmt.Number(s.Live!.Percent) + " % of the prompt" : null, serverName),
            Parallel(s),
            string.IsNullOrEmpty(s.Model) ? null : s.Model,
            Has(s.Context.Used) ? "Context " + Fmt.Number(s.Context.Used) + (Has(s.Context.Max) ? " / " + Fmt.Number(s.Context.Max) : "") : null,
            newest != null && (pre.HasValue || Has(newest.DecodeTokensPerSecond))
                ? Casing.Join("Last request", pre.HasValue ? "prefill " + Fmt.Number(pre) + " t/s" : null, Has(newest.DecodeTokensPerSecond) ? "decode " + Fmt.Number(newest.DecodeTokensPerSecond, 1) + " t/s" : null) : null,
            s.Queued > 0 ? "Queue " + s.Queued : null);
    }
}
