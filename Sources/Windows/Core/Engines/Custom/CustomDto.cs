using System.Text.Json.Serialization;

namespace Stratoscope.Core.Engines.Custom;

/// <summary>
/// <c>GET /health</c> of the custom 27B server (frontend/server.py <c>do_GET</c>): <c>{"status":"ok"}</c> when the engine is ready, 200 with
/// <c>unloaded</c> or <c>loading</c> after an idle unload (the service is up; a request loads the engine), 503 while the engine is not ready.
/// </summary>
public sealed record CustomHealth
{
    [JsonPropertyName("status")] public string? Status { get; init; }
}

/// <summary>
/// The part of the custom 27B server's <c>GET /ui/state</c> that Stratoscope reads: the per-request list <c>seqs</c> (worker.py
/// <c>Scheduler.stats()</c>, stats.py <c>tick()</c>, server.py <c>GET /ui/state</c>). The answer also carries logs and history (about 150 KB), so it is
/// asked only while <c>/metrics</c> says requests run.
/// </summary>
public sealed record CustomUiState
{
    [JsonPropertyName("seqs")] public List<CustomSeq?>? Seqs { get; init; }
}

/// <summary>One request of <c>/ui/state</c> <c>seqs</c>. Null means the field was missing, null or of another type.</summary>
public sealed record CustomSeq
{
    [JsonPropertyName("rid")] public string? Rid { get; init; }
    /// <summary>waiting, running, parked or admitted.</summary>
    [JsonPropertyName("state")] public string? State { get; init; }
    /// <summary>The engine's own phase: prefill or decode.</summary>
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("prompt")] public double? Prompt { get; init; }
    /// <summary>Prompt tokens the engine has run (capped at the prompt).</summary>
    [JsonPropertyName("fed")] public double? Fed { get; init; }
    [JsonPropertyName("out")] public double? Out { get; init; }
    [JsonPropertyName("max_new")] public double? MaxNew { get; init; }
    /// <summary>Prompt tokens actually prefilled, not restored from the cache.</summary>
    [JsonPropertyName("computed")] public double? Computed { get; init; }
    [JsonPropertyName("prefill_s")] public double? PrefillSeconds { get; init; }
    [JsonPropertyName("decode_s")] public double? DecodeSeconds { get; init; }
}

/// <summary>
/// The numbers of the custom 27B server's <c>GET /metrics</c> (Prometheus text, series prefix <c>q27:</c>; worker.py <c>metrics()</c>). Null means the series
/// was not in the answer. The scheduler gauges are read at the time of the request; the totals count since the engine started.
/// </summary>
public sealed record CustomMetrics
{
    public const string Prefix = "q27:";

    public double? Running { get; init; }
    public double? Waiting { get; init; }
    /// <summary>The most sequences the engine was built to run together (not the configured target, <c>target_seqs</c>).</summary>
    public double? EngineSeqs { get; init; }
    public double? KvFreePages { get; init; }
    public double? KvCheckpointOnlyPages { get; init; }
    public double? PrefixCacheCheckpoints { get; init; }
    public double? PrefillTokensTotal { get; init; }
    public double? PromptTokensTotal { get; init; }
    public double? CachedTokensTotal { get; init; }
    public double? GenerationTokensTotal { get; init; }
    public double? RequestsTotal { get; init; }
    /// <summary>1 while the scheduler holds the first waiting request back for lack of KV pages (<c>admission_blocked{reason="kv"}</c>).</summary>
    public double? BlockedKv { get; init; }
    /// <summary>1 while it holds it back for the minimum per-sequence speed (<c>admission_blocked{reason="speed"}</c>).</summary>
    public double? BlockedSpeed { get; init; }

    /// <summary>Null when the text has no <c>q27:</c> series at all: that is no custom 27B server.</summary>
    public static CustomMetrics? Parse(string? text)
    {
        var values = PrometheusText.Parse(text, Prefix);
        if (values.Count == 0) return null;
        double? V(string name) => values.TryGetValue(name, out var v) ? v : null;
        return new CustomMetrics
        {
            Running = V("num_requests_running"), Waiting = V("num_requests_waiting"), EngineSeqs = V("engine_seqs"),
            KvFreePages = V("kv_free_pages"), KvCheckpointOnlyPages = V("kv_checkpoint_only_pages"), PrefixCacheCheckpoints = V("prefix_cache_checkpoints"),
            PrefillTokensTotal = V("prefill_tokens_total"), PromptTokensTotal = V("prompt_tokens_total"), CachedTokensTotal = V("cached_tokens_total"),
            GenerationTokensTotal = V("generation_tokens_total"), RequestsTotal = V("requests_total"),
            BlockedKv = V("admission_blocked{reason=\"kv\"}"), BlockedSpeed = V("admission_blocked{reason=\"speed\"}"),
        };
    }
}
