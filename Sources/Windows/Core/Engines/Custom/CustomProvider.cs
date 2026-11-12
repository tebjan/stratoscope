namespace Stratoscope.Core.Engines.Custom;

/// <summary>
/// Adapter for the custom 27B server (its metrics use the q27: prefix; frontend/server.py), behind a llama-swap router or directly.
///
/// Data sources, all GET on endpoints the server answers without waking it:
///  - <c>/health</c>: <c>{"status":"ok"}</c> when the engine is ready; another status (<c>unloaded</c>, <c>loading</c>) means no request can run
///    yet and <c>/metrics</c> is not asked. It names no engine, which is why <see cref="AutoEngineProvider"/> recognises the custom 27B server by its metrics.
///  - <c>/metrics</c> (Prometheus text, prefix <c>q27:</c>): running and waiting requests, the engine's sequence capacity, free KV pages,
///    prefix cache checkpoints, which admission block holds a request back, and the token totals.
///
///  - <c>/ui/state</c> (JSON, about 150 KB, asked on every poll): <c>tick.seqs</c> with each request's state, its own phase (prefill or decode), prompt,
///    fed, out, max_new, computed, prefill_s and decode_s; <c>tick.sample</c> and <c>tick.vram</c> (video card, memory parts, KV cache); <c>tick.cache</c>
///    and <c>tick.sched</c>; <c>info</c> (context size, slots, DFlash2, KV format, card name); <c>log</c> (the <c>done</c> events are the finished requests).
///    If it is missing, refused or not JSON, the phase is "running" only and those parts are not drawn.
///
/// What the custom 27B server does not report is not drawn: RAM and CPU. The metrics alone are totals across all
/// requests: without /ui/state neither reading nor writing is shown, and no speed.
/// </summary>
public sealed class CustomProvider : RoutedProvider
{
    public const string Id = "custom";

    public CustomProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
        : base(config, http, clients) { }

    public override string Brand => "Custom";

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;
        var model = routedId ?? Brand;

        var health = await http.GetAsync(Endpoints.Join(engineBase, "health"), Json.CustomHealthType, cancellation).ConfigureAwait(false);
        if (health.Ok && NotReady(health.Body!.Status) is string reason)
            return new EngineRead(200, true, FetchFailure.None, null, Placeholder(model, EngineStatus.Loading, reason));
        // The server answers 503 while its engine is not ready (a start, or after a crash).
        if (health.Failure == FetchFailure.Status && health.Status == 503)
            return new EngineRead(200, true, FetchFailure.None, null, Placeholder(model, EngineStatus.Loading, "Engine not ready (HTTP 503)"));

        var text = await http.GetTextAsync(Endpoints.Join(engineBase, "metrics"), cancellation).ConfigureAwait(false);
        if (!text.Ok) return new EngineRead(text.Status, false, text.Failure, text.Detail, null);
        var metrics = CustomMetrics.Parse(text.Body);
        if (metrics is null)
        {
            // Read directly, a document without q27: series is no custom 27B server. Routed, the router named an engine that sends no q27: metrics: an error.
            return routedId is null
                ? new EngineRead(text.Status, true, FetchFailure.None, null, null)
                : new EngineRead(text.Status, false, FetchFailure.Other, "no q27: metrics", null);
        }

        // /ui/state is about 150 KB (logs and history). It is the only source of the video card, the memory parts, the cache counters, the engine
        // settings and the finished requests, so it is asked on every poll, idle too. Same router path, 429 handling and timeout as /metrics.
        CustomUiState? ui = null;
        {
            var state = await http.GetAsync(Endpoints.Join(engineBase, "ui", "state"), Json.CustomUiStateType, cancellation).ConfigureAwait(false);
            if (state.Status == 429) return new EngineRead(429, false, FetchFailure.Status, state.Detail, null);
            // Missing, refused or not JSON: no per-request data, the aggregate state "running" stays.
            if (state.Ok) ui = state.Body;
        }

        return new EngineRead(text.Status, true, FetchFailure.None, null, Translate(metrics, model, ui));
    }

    private static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tokens divided by the seconds spent, only when both are exposed and the time is above zero.</summary>
    private static double? Speed(double? tokens, double? seconds) => tokens.HasValue && seconds > 0 ? tokens / seconds : null;

    /// <summary>The state text of a <c>/health</c> status other than ok, or null when the engine is ready.</summary>
    internal static string? NotReady(string? status) =>
        string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ? null : status is null ? "State not reported" : "State: " + status;

    /// <summary>
    /// Translates the metrics into a snapshot: running requests give Running, none give Idle. With <paramref name="ui"/> (<c>/ui/state</c>, read only while
    /// requests run) the phase of each running request is the engine's own: phase "prefill" is reading, "decode" is writing. Public so tests can feed payloads.
    /// </summary>
    public EngineSnapshot Translate(CustomMetrics m, string model, CustomUiState? ui = null)
    {
        var running = m.Running ?? 0;
        var busy = running > 0;
        var status = busy ? EngineStatus.Running : EngineStatus.Idle;

        LiveRequest? live = busy ? new LiveRequest { Running = running > 1 ? (int)running : null } : null;
        int? queued = null;

        if (busy && ui?.Seqs is { } seqs)
        {
            var rows = seqs.Where(s => s != null).Select(s => s!).ToList();
            // waiting: queued, not drawn as busy. running, parked and admitted: counted as running. parked and admitted carry no phase here
            // (the phase is taken only from a "running" seq), so they add to the count without reading or writing.
            var active = rows.Where(s => Is(s.State, "running") || Is(s.State, "parked") || Is(s.State, "admitted")).ToList();
            var readers = active.Where(s => Is(s.State, "running") && Is(s.Phase, "prefill")).ToList();
            var writers = active.Where(s => Is(s.State, "running") && Is(s.Phase, "decode")).ToList();
            if (active.Count > 0)
            {
                queued = rows.Count(s => Is(s.State, "waiting"));
                status = writers.Count > 0 ? EngineStatus.Decode : readers.Count > 0 ? EngineStatus.Prefill : EngineStatus.Running;
                var together = active.Count > 1;
                var one = together ? null : active[0];
                // Speeds only from exposed values and only for a time above zero. Several requests together: the sum over those in the phase.
                double? Sum(IEnumerable<double?> values) { var list = values.ToList(); return list.Count > 0 && list.All(v => v.HasValue) ? list.Sum(v => v!.Value) : null; }
                live = new LiveRequest
                {
                    PromptRead = status == EngineStatus.Prefill ? Sum(readers.Select(s => s.Fed)) : null,
                    PromptTotal = status == EngineStatus.Prefill && !together ? one!.Prompt : null,
                    Generated = status == EngineStatus.Decode ? Sum(writers.Select(s => s.Out)) : null,
                    MaxTokens = status == EngineStatus.Decode && !together ? one!.MaxNew : null,
                    PrefillTokensPerSecond = status == EngineStatus.Prefill ? Sum(readers.Select(s => Speed(s.Computed, s.PrefillSeconds))) : null,
                    DecodeTokensPerSecond = Sum(writers.Select(s => Speed(s.Out, s.DecodeSeconds))),
                    Running = together ? active.Count : null,
                };
            }
        }

        // Counters since the engine started. Prompt tokens are all prompts admitted, reused ones included.
        Totals? totals = null;
        if (m.PromptTokensTotal.HasValue || m.GenerationTokensTotal.HasValue || m.RequestsTotal.HasValue)
            totals = new Totals
            {
                Requests = m.RequestsTotal, PromptTokens = m.PromptTokensTotal, ReusedTokens = m.CachedTokensTotal, OutputTokens = m.GenerationTokensTotal,
            };

        string? blocked = m.BlockedKv == 1 ? "kv" : m.BlockedSpeed == 1 ? "speed" : m.BlockedKv == 0 && m.BlockedSpeed == 0 ? "none" : null;
        var tick = ui?.Tick;
        var uiInfo = ui?.Info;
        var batchSlotCount = m.EngineSeqs ?? uiInfo?.MaxSeqs;
        // Metrics first (they are asked on every poll); /ui/state fills what they lack.
        var kvFree = m.KvFreePages ?? tick?.Cache?.FreePages;
        var kvCheckpointOnly = m.KvCheckpointOnlyPages ?? tick?.Cache?.CheckpointOnlyPages;
        var checkpoints = m.PrefixCacheCheckpoints ?? tick?.Cache?.Checkpoints;
        string? draft = uiInfo?.Spec is bool spec ? (spec ? "DFlash2" : "none") : null;
        EngineInfo? info = null;
        if (batchSlotCount.HasValue || kvFree.HasValue || checkpoints.HasValue || blocked != null || uiInfo != null || tick?.Sched != null)
            info = new EngineInfo
            {
                BatchSlots = batchSlotCount, KvFreePages = kvFree, KvCheckpointOnlyPages = kvCheckpointOnly,
                PrefixCheckpoints = checkpoints, AdmissionBlocked = blocked,
                KvFormat = uiInfo?.KvDtype, DraftWindow = draft, GpuName = uiInfo?.Gpu, TargetSeqs = tick?.Sched?.TargetSeqs,
            };

        // Finished requests: the "done" events of the engine's log (the last 500 events), newest first. ttft and the like are not drawn.
        var recent = (ui?.Log ?? new List<CustomLogEntry?>())
            .Where(e => e is { Kind: "done", Rec: not null }).Select(e => e!)
            .Reverse().Take(20)
            .Select(e => new RequestStats
            {
                PromptTokens = e.Rec!.PromptTokens, ReusedTokens = e.Rec.CachedTokens, PromptRead = e.Rec.ComputedTokens,
                PromptMilliseconds = e.Rec.PrefillSeconds * 1000, OutputTokens = e.Rec.CompletionTokens,
                DecodeTokensPerSecond = e.Rec.DecodeTps, DurationSeconds = e.Rec.Total,
                FinishedAt = e.T is double t ? DateTime.UnixEpoch.AddSeconds(t) : null, Finish = e.Rec.Reason,
            }).ToList();

        // The context of the fullest running request: the prompt fed so far plus what it wrote (the prompt when fed is missing); otherwise the newest finished one.
        double? ctxUsed = null;
        var runningSeqs = (ui?.Seqs ?? new List<CustomSeq?>()).Where(s => s != null && Is(s.State, "running")).Select(s => s!).ToList();
        foreach (var s in runningSeqs)
            if ((s.Fed ?? s.Prompt) is double basis) ctxUsed = Math.Max(ctxUsed ?? 0, basis + (s.Out ?? 0));
        if (ctxUsed is null && recent.Count > 0) ctxUsed = recent[0].ContextTokens;

        // The video card: the NVML sample. Video memory used is the total minus the "free" part of the engine's own accounting (vram.parts).
        SystemStats? system = null;
        if (tick?.Sample != null || tick?.Vram != null)
        {
            var vram = tick?.Vram;
            double? free = vram?.Parts?.FirstOrDefault(p => p?.Name == "free")?.Bytes;
            system = new SystemStats
            {
                GpuPercent = tick?.Sample?.Util, GpuMemoryBusyPercent = tick?.Sample?.MemUtil, GpuTemperatureCelsius = tick?.Sample?.Temp,
                GpuPowerWatts = tick?.Sample?.Power, GpuPowerLimitWatts = tick?.Sample?.PowerLimit,
                GpuMemoryTotalBytes = vram?.Total, GpuMemoryUsedBytes = vram?.Total is double total && free.HasValue ? total - free : null,
                // RAM and CPU are not in /ui/state.
            };
        }

        // The KV cache: its budget is the "KV cache" part of the video memory, in use is that minus its own "free" part; the counters are the engine's.
        CacheStats? cache = null;
        var kv = tick?.Vram?.Parts?.FirstOrDefault(p => p?.Name == "KV cache");
        if (kv != null || tick?.Cache != null)
        {
            var kvFreeBytes = kv?.Parts?.FirstOrDefault(p => p?.Name == "free")?.Bytes;
            cache = new CacheStats
            {
                BudgetBytes = kv?.Bytes, UsedBytes = kv?.Bytes is double kvBytes && kvFreeBytes.HasValue ? kvBytes - kvFreeBytes : null,
                Requests = tick?.Cache?.Lookups, RequestsReused = tick?.Cache?.Hits, Evictions = tick?.Cache?.Evicted,
            };
        }

        // One row per engine slot: a running request fills one, the rest are idle. Only when the slot count is known.
        List<BatchSlot>? slots = null;
        if (batchSlotCount is double count && count > 0 && ui != null)
        {
            var held = (ui.Seqs ?? new List<CustomSeq?>())
                .Where(s => s != null && (Is(s.State, "running") || Is(s.State, "parked") || Is(s.State, "admitted"))).Select(s => s!).ToList();
            slots = new List<BatchSlot>();
            for (var i = 0; i < Math.Max(count, held.Count); i++)
            {
                if (i >= held.Count) { slots.Add(new BatchSlot { Index = i, State = "idle" }); continue; }
                var s = held[i];
                var isRunning = Is(s.State, "running");
                slots.Add(new BatchSlot
                {
                    Index = i,
                    State = isRunning ? (Is(s.Phase, "decode") ? "decoding" : Is(s.Phase, "prefill") ? "reading" : "running") : s.State!.ToLowerInvariant(),
                    PromptTokens = s.Prompt, PromptRead = s.Fed, Generated = s.Out, MaxTokens = s.MaxNew,
                    TokensPerSecond = isRunning && Is(s.Phase, "decode") ? Speed(s.Out, s.DecodeSeconds) : null,
                });
            }
        }

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = queued ?? (m.Waiting is double waiting ? (int)waiting : 0),
            Live = live, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = uiInfo?.MaxTokens, CompactAt = CompactAt(model) },
            System = system, Cache = cache, Totals = totals, Info = info, Slots = slots, Links = Links(),
        };
    }
}
