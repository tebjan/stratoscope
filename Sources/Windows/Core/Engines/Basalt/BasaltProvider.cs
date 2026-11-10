using System.Globalization;

namespace Stratoscope.Core.Engines.Basalt;

/// <summary>
/// Adapter for the Basalt local inference engine (verified against Basalt 0.3.2, behind a llama-swap router).
///
/// Data sources:
///  - the router's /running (optional), and Basalt's /metrics, reached through the router at /upstream/&lt;model&gt;/metrics
///  - Basalt's /slots (the llama.cpp shape), asked only while a request runs: /metrics gives the prompt's size, only /slots says how much of
///    it is read, and it also carries the request's token limit and its cache reuse.
/// Basalt has no conversation cache list, no totals and no batch slots in these documents, so those parts of the panel are not drawn.
/// </summary>
public sealed class BasaltProvider : RoutedProvider
{
    public const string Id = "basalt";

    public BasaltProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
        : base(config, http, clients) { }

    public override string Brand => "Basalt";

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        BaseForLinks = engineBase;
        var metrics = await http.GetAsync(Endpoints.Join(engineBase, "metrics"), Json.BasaltMetricsType, cancellation).ConfigureAwait(false);
        if (!metrics.Ok) return new EngineRead(metrics.Status, false, metrics.Failure, metrics.Detail, null);
        var body = metrics.Body!;
        if (routedId is null && IsEmpty(body.Engine)) return new EngineRead(metrics.Status, true, FetchFailure.None, null, null);

        // Only a running request has a row worth asking for. A refused or failed /slots just leaves the progress unknown.
        List<BasaltSlotDto?>? slots = null;
        if (body.Live?.State is "reading" or "generating")
            slots = (await http.GetAsync(Endpoints.Join(engineBase, "slots"), Json.BasaltSlotsType, cancellation).ConfigureAwait(false)).Body;

        var model = routedId ?? body.Engine?.Model ?? "Basalt";
        return new EngineRead(metrics.Status, true, FetchFailure.None, null, Translate(body, slots, model));
    }

    /// <summary>Translates one /metrics document (and the /slots rows, when asked for) into a snapshot. Public so tests can feed recorded payloads.</summary>
    public EngineSnapshot Translate(BasaltMetrics m, List<BasaltSlotDto?>? slotRows, string model)
    {
        var eng = m.Engine ?? new BasaltEngineDto();
        var live = m.Live ?? new BasaltLiveDto();
        var reqs = (m.Requests ?? new List<BasaltRequestDto?>()).Where(r => r != null).Select(r => r!).ToList();

        // Status: Basalt says "reading" and "generating" and does not tell reasoning from the answer, so there is no Thinking here.
        // Anything else but "idle" means the weights are not ready for a request, and the engine's own word is shown as the reason.
        var status = live.State switch
        {
            "idle" => EngineStatus.Idle,
            "reading" => EngineStatus.Prefill,
            "generating" => EngineStatus.Decode,
            _ => EngineStatus.Loading,
        };
        if (status == EngineStatus.Loading) return Placeholder(model, status, NotReady(live));

        // The running request: the slot row when /slots was asked (it is the request that holds the engine; /metrics describes the newest
        // one, which may still wait in the queue), otherwise /metrics.
        var row = slotRows?.FirstOrDefault(s => s is { State: "prefill" or "decoding" });
        var reading = status == EngineStatus.Prefill;
        LiveRequest? liveRequest = null;
        if (status != EngineStatus.Idle)
        {
            var speed = live.TokensPerSecond;
            liveRequest = new LiveRequest
            {
                PromptTotal = row?.PromptTokens ?? live.PromptTokens,
                PromptRead = row?.PrefillDone,
                Generated = row?.Generated ?? live.Generated,
                MaxTokens = row?.MaxTokens,
                ElapsedSeconds = live.ElapsedSeconds,
                PrefillTokensPerSecond = reading && (live.PrefillTokensPerSecondMean ?? 0) > 0 ? live.PrefillTokensPerSecondMean : null,
                DecodeTokensPerSecond = (speed ?? 0) > 0 ? speed : null,
                ReusedEstimate = row?.Reused,      // the engine's own number once it knows it; the field also carries Strata's estimates
                // Several requests at once only when Basalt has more than one slot; the per-slot rows are not shown.
                Running = live.Slots is { Total: > 1, Active: > 1 } s ? (int)s.Active!.Value : null,
            };
        }

        var recent = reqs.Select(q => new RequestStats
        {
            PromptTokens = q.PromptTokens, ReusedTokens = q.Reused,
            // Basalt's own definition of the tokens a request read (its "best prefill" is this over prompt_ms).
            PromptRead = q.PromptTokens is double prompt && q.Reused is double reused ? Math.Max(0, prompt - reused) : null,
            PromptMilliseconds = q.PromptMilliseconds, OutputTokens = q.OutputTokens,
            DecodeTokensPerSecond = q.DecodeTokensPerSecond, DurationSeconds = q.DurationSeconds,
            FinishedAt = FinishedAt(q.Time), Finish = q.Finish, ExpertHitRate = q.HitRate,
        }).ToList();

        double? ctxUsed = row?.CtxUsed;
        if (ctxUsed is null && live.PromptTokens is double pt && status != EngineStatus.Idle) ctxUsed = pt + (live.Generated ?? 0);
        if (ctxUsed is null && recent.Count > 0 && recent[0].PromptTokens is double p0 && recent[0].OutputTokens is double o0) ctxUsed = p0 + o0;

        // One video card is shown (Basalt reports a list; this one is the first). A machine with several would need a model of its own.
        SystemStats? system = null;
        if (!IsEmpty(m.Hardware))
        {
            var hw = m.Hardware!;
            var gpu = hw.Gpus?.FirstOrDefault(g => g != null);
            system = new SystemStats
            {
                RamUsedBytes = hw.RamUsed, RamTotalBytes = hw.RamTotal, CpuPercent = hw.Cpu,
                GpuPercent = gpu?.Util, GpuTemperatureCelsius = gpu?.Temp, GpuPowerWatts = gpu?.Power, GpuPowerLimitWatts = gpu?.PowerLimit,
                GpuMemoryUsedBytes = gpu?.MemUsed, GpuMemoryTotalBytes = gpu?.MemTotal,
            };
        }

        EngineInfo? info = null;
        if (!IsEmpty(m.Engine))
            info = new EngineInfo
            {
                Version = eng.Version, KvFormat = eng.Kv, DraftWindow = eng.Spec, ExpertCacheMib = eng.ExpertCacheMib,
                BatchSlots = live.Slots?.Total, ExpertHitRate = reqs.Count > 0 ? reqs[0].HitRate : null,
            };

        // The conversation cache: Basalt reports its budget and slot count, not what is parked, so only those two are drawn. A budget of 0 is
        // the cache switched off: there is nothing to show.
        CacheStats? cache = null;
        if (eng.ConversationCacheMib is double budget && budget > 0)
            cache = new CacheStats { BudgetBytes = budget * 1_048_576, Slots = eng.ConversationCacheSlots is double sl ? (int)sl : null };

        return new EngineSnapshot(Brand, model, status)
        {
            Queued = (int)(live.Queued ?? live.Slots?.QueueDepth ?? 0),
            Live = liveRequest, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = eng.MaxContext, CompactAt = CompactAt(model) },
            System = system, Cache = cache, Info = info, Links = Links(),
        };
    }

    /// <summary>Why a request cannot start yet, from the engine's state and, while it loads, its progress.</summary>
    private static string NotReady(BasaltLiveDto live)
    {
        switch (live.State)
        {
            case "loading":
                var percent = live.Load?.Percent is double p ? " " + Math.Round(p).ToString(CultureInfo.InvariantCulture) + " %" : "";
                return live.Load?.Kind == "wake" ? "Waking up" : "Loading weights" + percent;
            case "unloaded": return "Weights not loaded";
            case "asleep": return "Asleep";
            default: return live.State is null ? "State not reported" : "State: " + live.State;
        }
    }
}
