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
///  - <c>/ui/state</c> (JSON, about 150 KB, asked only while <c>/metrics</c> says requests run): <c>seqs</c> with each request's state, its own phase
///    (prefill or decode), prompt, fed, out, max_new, computed, prefill_s and decode_s. If it is missing, refused or not JSON, the phase is "running" only.
///
/// What the custom 27B server does not report is not drawn: finished requests, context size, RAM, video card. The metrics alone are totals across all
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

        // /ui/state is about 150 KB (logs and history): asked only while /metrics says requests run. Same router path, 429 handling and timeout as /metrics.
        CustomUiState? ui = null;
        if (metrics.Running > 0)
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
        EngineInfo? info = null;
        if (m.EngineSeqs.HasValue || m.KvFreePages.HasValue || m.PrefixCacheCheckpoints.HasValue || blocked != null)
            info = new EngineInfo
            {
                BatchSlots = m.EngineSeqs, KvFreePages = m.KvFreePages, KvCheckpointOnlyPages = m.KvCheckpointOnlyPages,
                PrefixCheckpoints = m.PrefixCacheCheckpoints, AdmissionBlocked = blocked,
            };

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = queued ?? (m.Waiting is double waiting ? (int)waiting : 0),
            Live = live, Recent = new List<RequestStats>(),
            Context = new ContextUsage { CompactAt = CompactAt(model) },
            Totals = totals, Info = info, Links = Links(),
        };
    }
}
