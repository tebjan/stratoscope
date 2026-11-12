namespace Stratoscope.Core.Engines.LlamaCpp;

/// <summary>
/// Adapter for llama-server, the HTTP server of llama.cpp (read from tools/server of the llama.cpp source; a build that adds fields only adds
/// to what is shown, nothing here depends on a field a stock build lacks), behind a llama-swap router or directly.
///
/// Data sources, all GET:
///  - <c>/props</c>: the build, the slot count, the context size, whether the server sleeps and whether <c>--metrics</c> is on. The server
///    answers it while asleep; <c>/slots</c> would wake it, so a sleeping server is shown as asleep and <c>/slots</c> is not asked.
///  - <c>/slots</c>: per slot whether it runs a request, the tokens in its memory, how many prompt tokens were read and how many reused from the
///    cache, how many tokens were generated. There is no phase field: a running slot with 0 generated tokens is reading its prompt.
///  - <c>/metrics</c> (Prometheus text, only with <c>--metrics</c>): the prompt and generation speeds, the waiting requests and the totals. The
///    server resets the speed gauges on every scrape, so they are the average since the previous poll (and 0 when nothing ran: shown as unknown).
///
/// What llama-server does not say is not drawn: the size of the whole prompt (so no percentage while it is read), when a request ends and what
/// it came to (a slot's counters reset when its request ends, so there is no list of finished requests), RAM, video card and CPU.
/// </summary>
/// <remarks>
/// Speed rule: name the source of every value; never estimate.
/// When several requests run together, the headline decode speed (and the headline prefill speed while reading) is the engine's own total over
/// all requests. Each engine reports that total itself:
///  - Strata: live.tok_s, the total of the generating requests (serve/server.py _tok_s). Only when the engine has no total yet (the newest request
///    still reads its prompt) is the sum of the derived per-slot speeds used (StrataProvider.cs, the branch where rates.Sum() is taken). Prefill:
///    live.prefill_tok_s_mean, the engine's mean; a value derived from the progress between polls only when that is missing.
///  - Basalt: live.tok_s, which the engine sums over its slots (BasaltProvider.cs, the speed line).
///  - llama.cpp: /metrics llamacpp:predicted_tokens_seconds, the server-wide decode rate over all slots (prompt_tokens_seconds for prefill). It
///    needs the server's --metrics flag. No sum is used (LlamaCppProvider.cs, the DecodeTokensPerSecond line).
///  - custom: /ui/state tick.sample.decode_tps and tick.sample.prefill_tps, the engine's counter deltas over all requests between samples
///    (CustomProvider.cs). The per-request sum is used only when the sample has no rate.
/// A request's own speed is shown on its own row only. Never add up the per-request rows to make the headline unless the engine has no total.
/// This engine: /metrics llamacpp:predicted_tokens_seconds (prefill: prompt_tokens_seconds). Canonical text: RoutedProvider.cs.
/// </remarks>
public sealed class LlamaCppProvider : RoutedProvider
{
    public const string Id = "llamacpp";

    public LlamaCppProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
        : base(config, http, clients) { }

    public override string Brand => "llama.cpp";

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;

        // A failed /props only leaves the build, the slot count and the sleeping state unknown.
        var props = (await http.GetAsync(Endpoints.Join(engineBase, "props"), Json.LlamaPropsType, cancellation).ConfigureAwait(false)).Body;
        var model = routedId ?? props?.ModelAlias ?? Brand;
        if (props is { Sleeping: true })
            return new EngineRead(200, true, FetchFailure.None, null, Placeholder(model, EngineStatus.Loading, "Asleep"));

        var slots = await http.GetAsync(Endpoints.Join(engineBase, "slots"), Json.LlamaSlotsType, cancellation).ConfigureAwait(false);
        if (!slots.Ok) return new EngineRead(slots.Status, false, slots.Failure, slots.Detail, null);
        var rows = slots.Body!;
        if (routedId is null && !rows.Any(r => r != null && (r.ContextSize.HasValue || r.IsProcessing != null)))
            return new EngineRead(slots.Status, true, FetchFailure.None, null, null);

        LlamaCppMetrics? metrics = null;
        if (props is not { MetricsDisabled: true })
        {
            var text = await http.GetTextAsync(Endpoints.Join(engineBase, "metrics"), cancellation).ConfigureAwait(false);
            if (text.Ok) metrics = LlamaCppMetrics.Parse(text.Body);
        }
        return new EngineRead(slots.Status, true, FetchFailure.None, null, Translate(rows, props, metrics, model));
    }

    /// <summary>Translates the /slots rows (with /props and /metrics, when they were asked for) into a snapshot. Public so tests can feed recorded payloads.</summary>
    public EngineSnapshot Translate(List<LlamaSlotDto?> slotRows, LlamaProps? props, LlamaCppMetrics? metrics, string model)
    {
        var rows = slotRows.Where(s => s != null).Select(s => s!).ToList();
        var running = rows.Where(s => s.Processing).ToList();
        // A running slot that has generated nothing yet reads its prompt; one that does not say how much it generated counts as reading.
        var writers = running.Where(s => s.Generated > 0).ToList();
        var readers = running.Except(writers).ToList();
        var status = running.Count == 0 ? EngineStatus.Idle : writers.Count > 0 ? EngineStatus.Decode : EngineStatus.Prefill;

        // /slots resets the counters of a request when it ends, so a slot that has just started shows 0 read and 0 reused until its first batch:
        // that is "not known yet", and tokens are only taken from a slot whose request has begun to count.
        static bool Counting(LlamaSlotDto s) => s.Generated > 0 || s.Processed > 0 || s.Cached > 0;

        var together = running.Count > 1;
        var row = running.FirstOrDefault();
        LiveRequest? live = null;
        if (status != EngineStatus.Idle)
        {
            var reading = status == EngineStatus.Prefill;
            double? read = null, total = null, generated;
            if (reading)
            {
                // Read so far = tokens processed plus tokens reused from the cache (/slots n_prompt_tokens_processed and n_prompt_tokens_cache).
                read = readers.All(Counting) && readers.All(s => s.Processed.HasValue && s.Cached.HasValue) ? readers.Sum(s => s.Processed!.Value + s.Cached!.Value) : null;
                total = readers.All(s => s.Tokens.HasValue) ? readers.Sum(s => s.Tokens!.Value) : null;      // /slots n_prompt_tokens: the request's whole prompt
                generated = readers.All(s => s.Generated == 0) ? 0 : null;       // "0" is the engine's word; a slot that says nothing is unknown
            }
            else
                generated = writers.All(s => s.Generated.HasValue) ? writers.Sum(s => s.Generated!.Value) : null;
            double? reused = !together && row != null && Counting(row) ? row.Cached : null;
            live = new LiveRequest
            {
                PromptRead = read, PromptTotal = total, Generated = generated,
                MaxTokens = together ? null : row?.TokenLimit,
                PrefillTokensPerSecond = reading && metrics?.PromptTokensPerSecond > 0 ? metrics.PromptTokensPerSecond : null,
                DecodeTokensPerSecond = writers.Count > 0 && metrics?.PredictedTokensPerSecond > 0 ? metrics.PredictedTokensPerSecond : null,
                ReusedEstimate = reused,       // the engine's own count (the field also carries Strata's estimates)
                Running = together ? running.Count : null,
            };
        }

        // One entry per slot, for a server with more than one.
        List<BatchSlot>? batchSlots = null;
        if (rows.Count > 1)
            batchSlots = rows.Select((s, position) => new BatchSlot
            {
                Index = (int)(s.Id ?? position),
                State = !s.Processing ? "idle" : s.Generated > 0 ? "decoding" : "reading",
                HeldTokens = s.Tokens,        // the slot's memory: the conversation it holds, a running request's prompt and what it wrote
                PromptTokens = s.Processing && s.Generated is not > 0 ? s.Tokens : null,
                PromptRead = s.Processing && s.Generated is not > 0 && Counting(s) && s.Processed.HasValue && s.Cached.HasValue ? s.Processed + s.Cached : null,
                Generated = s.Processing ? s.Generated : null,
                MaxTokens = s.Processing ? s.TokenLimit : null,
            }).ToList();

        // The fullest running slot, or while idle the slot that ran last (the conversation the next request continues).
        double? used = running.Where(s => s.Generated > 0 || Counting(s)).Select(s => s.Tokens).Where(t => t.HasValue).DefaultIfEmpty(null).Max();
        if (running.Count == 0)
            used = rows.Where(s => s.TaskId.HasValue).OrderByDescending(s => s.TaskId).Select(s => s.Tokens).FirstOrDefault();
        var max = rows.Select(s => s.ContextSize).Where(c => c.HasValue).DefaultIfEmpty(null).Max() ?? props?.Generation?.ContextSize;

        // Counters since the server started. "Prompt tokens" counts what was read and what was reused; the engine has no request count here.
        Totals? totals = null;
        if (metrics != null && (metrics.PromptTokensTotal.HasValue || metrics.TokensPredictedTotal.HasValue))
            totals = new Totals
            {
                PromptTokens = metrics.PromptTokensTotal is double read && metrics.PromptTokensCachedTotal is double cached ? read + cached : null,
                ReusedTokens = metrics.PromptTokensCachedTotal, OutputTokens = metrics.TokensPredictedTotal,
                PromptMilliseconds = metrics.PromptSecondsTotal * 1000, DecodeMilliseconds = metrics.TokensPredictedSecondsTotal * 1000,
            };

        EngineInfo? info = null;
        if (props != null && (props.BuildInfo != null || props.TotalSlots.HasValue))
            info = new EngineInfo { Version = props.BuildInfo, BatchSlots = props.TotalSlots };

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = (int)(metrics?.RequestsDeferred ?? 0),
            Live = live, Recent = new List<RequestStats>(),
            Context = new ContextUsage { Used = used, Max = max, CompactAt = CompactAt(model) },
            Totals = totals, Info = info, Slots = batchSlots, Links = Links(),
        };
    }
}
