namespace Stratoscope.Core.Engines.Strata;

/// <summary>
/// Adapter for the Strata local inference engine. Port of StrataProvider.swift.
///
/// Data sources:
///  - the router's /running (optional, when a llama-swap style router sits in front of Strata)
///  - the Strata server's /metrics, reached through the router at /upstream/&lt;model&gt;/metrics
///  - the Strata log, for the parked-conversation list that /metrics does not name
/// </summary>
public sealed class StrataProvider : IEngineProvider
{
    public const string Id = "strata";

    private readonly EngineConfig config;
    private readonly IJsonSource http;
    private readonly StrataCacheLog cacheLog;
    private readonly Func<DateTime> clock;
    private (double Read, DateTime At)? lastRead;
    private string? lastModelId;
    private EngineSnapshot? lastSnapshot;
    private string baseForLinks;
    private readonly ClientConfigReader? clients;

    /// <param name="clients">Where to look for the summarise threshold of the AI clients that use this engine. Null (the default) reads no client config.</param>
    public StrataProvider(EngineConfig config, IJsonSource http, Func<DateTime>? clock = null, ClientConfigReader? clients = null)
    {
        this.config = config;
        this.http = http;
        this.clients = clients;
        this.clock = clock ?? (() => DateTime.UtcNow);
        cacheLog = new StrataCacheLog(this.clock);
        baseForLinks = config.Url ?? config.Router ?? Config.DefaultUrl;
    }

    public string? LastError { get; private set; }

    // MARK: Polling

    public async Task<EngineSnapshot?> PollAsync(CancellationToken cancellation)
    {
        LastError = null;
        if (config.Router is null) return await PollDirect(cancellation).ConfigureAwait(false);

        var router = config.Router;
        var running = await http.GetAsync(Endpoints.Join(router, "running"), Json.RunningType, cancellation).ConfigureAwait(false);
        if (!running.Ok)
        {
            // Without an explicitly configured server address there is nothing else to ask: no guessing at localhost.
            if (config.Url != null) return await PollDirect(cancellation).ConfigureAwait(false);
            LastError = Describe(running.Failure, running.Detail, router);
            return null;
        }
        var first = running.Body!.Running?.FirstOrDefault(r => r != null);
        if (first is null)
        {
            if (config.Url != null)
            {
                var direct = await PollDirect(cancellation).ConfigureAwait(false);
                if (direct != null) return direct;
                LastError = null;
            }
            lastSnapshot = null; lastModelId = null;
            return Placeholder("-", EngineStatus.NoModel, null);
        }
        var id = first.Model ?? "Model";
        var state = first.State ?? "?";
        if (state != "ready") return Placeholder(id, EngineStatus.Loading, "State: " + state);

        var metricsUrl = Endpoints.Join(router, "upstream", id, "metrics");
        baseForLinks = Endpoints.Join(router, "upstream", id).AbsoluteUri.TrimEnd('/');
        var metrics = await http.GetAsync(metricsUrl, Json.MetricsType, cancellation).ConfigureAwait(false);
        if (metrics.Status == 429)
        {
            // The router's per-model request limit is reached. Keep the last data and say so.
            if (lastSnapshot != null && lastModelId == id) return lastSnapshot.WithRouterLimited();
            return Placeholder(id, EngineStatus.Loading, "Router request limit reached");
        }
        if (!metrics.Ok) return Placeholder(id, EngineStatus.Error, "Server not responding");
        var snapshot = Translate(metrics.Body!, id);
        lastSnapshot = snapshot; lastModelId = id;
        return snapshot;
    }

    /// <summary>Without a router: ask the Strata server directly. If nothing answers, the engine counts as not running.</summary>
    private async Task<EngineSnapshot?> PollDirect(CancellationToken cancellation)
    {
        var address = config.Url ?? Config.DefaultUrl;
        var metrics = await http.GetAsync(Endpoints.Join(address, "metrics"), Json.MetricsType, cancellation).ConfigureAwait(false);
        if (!metrics.Ok || IsEmpty(metrics.Body!.Engine))
        {
            LastError = metrics.Ok ? "The answer from " + address + "/metrics is not a Strata metrics document." : Describe(metrics.Failure, metrics.Detail, address);
            return null;
        }
        baseForLinks = address;
        var snapshot = Translate(metrics.Body!, metrics.Body.Engine!.Model ?? "Strata");
        lastSnapshot = snapshot; lastModelId = null;
        return snapshot;
    }

    private EngineSnapshot Placeholder(string model, EngineStatus status, string? reason) =>
        new EngineSnapshot("Strata", model, status) { LoadingReason = reason, Links = Links(engineUi: false) };

    /// <summary>Short English reason, independent of the Windows display language.</summary>
    public static string Describe(FetchFailure failure, string? detail, string target) =>
        failure switch
        {
            FetchFailure.Refused => "Connection refused at " + target + ". Is the engine, or the SSH forward to it, running?",
            FetchFailure.Timeout => "No answer from " + target + " in time.",
            FetchFailure.NameNotResolved => "Host name not found: " + target,
            FetchFailure.Status => target + " answered with " + detail + ".",
            FetchFailure.NotJson => "The answer from " + target + " is not JSON.",
            _ => "Cannot read " + target + (detail != null ? ": " + detail : "."),
        };

    // MARK: Translation

    private List<EngineLink> Links(bool engineUi = true)
    {
        var links = new List<EngineLink>();
        if (engineUi) links.Add(new EngineLink("Open Strata UI", baseForLinks + "/"));
        if (config.Router != null) links.Add(new EngineLink("Open router UI", config.Router + "/ui/"));
        if (!string.IsNullOrEmpty(config.Webui)) links.Add(new EngineLink("Open chat UI", config.Webui!));
        var log = CurrentLogPath();
        if (log != null) links.Add(new EngineLink("Open log", log));
        return links;
    }

    /// <summary>The engine's log: the configured path, otherwise none (there is no standard place on Windows).</summary>
    internal string? CurrentLogPath() => string.IsNullOrEmpty(config.Log) ? null : config.Log;

    /// <summary>A section the engine sent as {} (or not at all) is no section: records compare by value, so it equals a fresh one.</summary>
    private static bool IsEmpty<T>(T? section) where T : class, new() => section is null || section.Equals(new T());

    private readonly Dictionary<int, List<(DateTime At, double Generated)>> slotHistory = new();
    private static readonly TimeSpan SlotWindow = TimeSpan.FromSeconds(4), SlotMinSpan = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The speed of each generating slot from its generated count between polls: the oldest sample of the last few seconds against the
    /// newest, needing at least a second between them. A slot with fewer samples, or one that started a new request, has no speed yet.
    /// </summary>
    private Dictionary<int, double> SlotRates(List<SlotDto> decoding, DateTime now)
    {
        var rates = new Dictionary<int, double>();
        var live = decoding.Select(s => (int)(s.Slot ?? -1)).ToHashSet();
        foreach (var gone in slotHistory.Keys.Where(k => !live.Contains(k)).ToList()) slotHistory.Remove(gone);
        foreach (var s in decoding)
        {
            if (s.Slot is not double slot || s.Generated is not double generated) continue;
            var id = (int)slot;
            if (!slotHistory.TryGetValue(id, out var history)) slotHistory[id] = history = new List<(DateTime, double)>();
            if (history.Count > 0 && (generated < history[^1].Generated || now <= history[^1].At)) history.Clear();      // a new request in this slot
            history.Add((now, generated));
            history.RemoveAll(h => now - h.At > SlotWindow);
            var oldest = history[0];
            if (now - oldest.At >= SlotMinSpan && generated >= oldest.Generated) rates[id] = (generated - oldest.Generated) / (now - oldest.At).TotalSeconds;
        }
        return rates;
    }

    /// <summary>
    /// Strata's /metrics has no such value. It comes from the configs of the clients that talk to this server and use this model; where those
    /// say nothing (or disagree), the user's own <c>compact_at</c> is the fallback.
    /// </summary>
    private double? CompactAt(string model)
    {
        var endpoints = new[] { config.Router, config.Url ?? Config.DefaultUrl, baseForLinks }.Where(e => e != null).Select(e => e!);
        return clients?.CompactAt(endpoints, config.ClientModel ?? model) ?? config.CompactAt;
    }

    /// <summary>Translates one /metrics document into a snapshot. Public so tests can feed recorded payloads.</summary>
    public EngineSnapshot Translate(StrataMetrics m, string model)
    {
        var eng = m.Engine ?? new EngineDto();
        var live = m.Live ?? new LiveDto();
        var reqs = (m.Requests ?? new List<RequestDto?>()).Where(r => r != null).Select(r => r!).ToList();

        // Status
        var state = live.State ?? "idle";
        var phase = live.Phase ?? "";
        var reading = state != "idle" && (phase.Contains("prompt") || (live.Generated ?? 0) == 0);
        var status = state == "idle" ? EngineStatus.Idle : reading ? EngineStatus.Prefill : phase == "thinking" ? EngineStatus.Thinking : EngineStatus.Decode;

        // Requests that run together in batch slots. live.tok_s is already the total of all generating requests (serve/server.py _tok_s), but
        // it is null while the newest request is still reading its prompt, and live.prompt_read describes that one request only; the slots
        // carry the rest. A slot's own tok_s is a mean over a time base that includes tokens generated before it, so it is not used: each
        // slot's speed is derived from its generated count between polls. While any slot generates, that is the state (the headline); the
        // reading slots then do not show. One running request is the single-request case and is left as it is.
        var rawSlots = (live.Slots ?? new List<SlotDto?>()).Where(s => s != null).Select(s => s!).ToList();
        var running = rawSlots.Where(s => s.State == "reading" || s.State == "decoding").ToList();
        var together = running.Count > 1;
        var decoding = running.Where(s => s.State == "decoding").ToList();
        var readingSlots = running.Where(s => s.State == "reading").ToList();
        if (together && state != "idle")
        {
            reading = decoding.Count == 0;
            status = reading ? EngineStatus.Prefill : EngineStatus.Decode;
        }

        // Prefill speed: Strata's mean, otherwise derived from the progress between two polls.
        var prefill = live.PrefillTokensPerSecondMean;
        var read = live.PromptRead;
        if (together && reading) { read = readingSlots.Sum(s => s.PromptRead ?? 0); prefill = null; }
        var now = clock();
        if ((prefill ?? 0) <= 0 && reading && read is double r1 && lastRead is (double r0, DateTime t0) && r1 > r0 && now > t0)
            prefill = (r1 - r0) / (now - t0).TotalSeconds;
        if (reading && read is double r2)
        {
            if (lastRead is null || r2 != lastRead.Value.Read) lastRead = (r2, now);
        }
        else lastRead = null;

        // Cache reuse of the running request is not reported directly: when it starts, prompt_read already sits at
        // the reused part; later, read = mean prefill speed x elapsed time. Approximate, and shown with an "about" sign.
        double? reused = null;
        if (reading && read is double r3)
        {
            var mean = live.PrefillTokensPerSecondMean ?? 0;
            var elapsed = live.ElapsedSeconds ?? 0;
            reused = mean > 0 && elapsed > 1 ? Math.Max(0, r3 - mean * elapsed) : r3;
            if (together) reused = null;      // the cache verdict is not known per slot
        }

        var slotRates = SlotRates(decoding, now);
        LiveRequest? liveRequest = null;
        if (state != "idle")
        {
            var t = live.TokensPerSecond;
            var decodeSpeed = (t ?? 0) > 0 ? t : live.TokensPerSecondMean;
            double? total = live.PromptTotal, generated = live.Generated;
            if (together)
            {
                if (reading) total = readingSlots.Sum(s => s.PromptTokens ?? 0);
                generated = decoding.Sum(s => s.Generated ?? 0);
                if ((t ?? 0) <= 0)        // the engine has no total while the newest request reads: the sum of the derived slot speeds
                {
                    var rates = decoding.Select(s => slotRates.GetValueOrDefault((int)(s.Slot ?? -1))).Where(r => r > 0).ToList();
                    decodeSpeed = rates.Count > 0 ? rates.Sum() : null;
                }
                else decodeSpeed = t;
            }
            liveRequest = new LiveRequest
            {
                PromptRead = read, PromptTotal = total, Generated = generated,
                MaxTokens = live.MaxTokens, ElapsedSeconds = live.ElapsedSeconds,
                PrefillTokensPerSecond = prefill,
                DecodeTokensPerSecond = decodeSpeed,
                ReusedEstimate = reused,
                Running = together ? (int)Math.Max(running.Count, live.Running ?? 0) : null,
                Waiting = live.Waiting is double w && w > 0 ? (int)w : null,
            };
        }

        var recent = reqs.Select(q => new RequestStats
        {
            PromptTokens = q.PromptTokens, ReusedTokens = q.Reused, PromptRead = q.PromptRead,
            PromptMilliseconds = q.PromptMilliseconds, OutputTokens = q.OutputTokens,
            DecodeTokensPerSecond = q.DecodeTokensPerSecond, DurationSeconds = q.DurationSeconds,
            FinishedAt = q.Time is double time && double.IsFinite(time) && time > 0 && time < 253_402_300_000 ? DateTime.UnixEpoch.AddSeconds(time) : null,
            Finish = q.Finish, ExpertHitRate = q.HitRate,
        }).ToList();

        double? ctxUsed = live.PromptTokens is double pt ? pt + (live.Generated ?? 0) : null;
        if (ctxUsed is null && recent.Count > 0 && recent[0].PromptTokens is double p0 && recent[0].OutputTokens is double o0) ctxUsed = p0 + o0;

        SystemStats? system = null;
        if (!IsEmpty(m.Hardware))
        {
            var hw = m.Hardware!;
            system = new SystemStats
            {
                RamUsedBytes = hw.RamUsed, RamTotalBytes = hw.RamTotal, GpuPercent = hw.GpuUtil, CpuPercent = hw.Cpu,
                GpuTemperatureCelsius = hw.GpuTemp, GpuPowerWatts = hw.GpuPower, GpuPowerLimitWatts = hw.GpuPowerLimit, GpuMemoryUsedBytes = hw.GpuMemUsed, GpuMemoryTotalBytes = hw.GpuMemTotal,
            };
        }

        CacheStats? cache = null;
        if (!IsEmpty(m.ConversationCache))
        {
            var cc = m.ConversationCache!;
            cacheLog.Update(CurrentLogPath());
            var entries = new List<CacheEntry>(cacheLog.Entries);
            int? parked = cc.Parked is double pk ? (int)pk : cacheLog.Readable ? entries.Count : null;
            while (parked is int pn && entries.Count > pn && entries.Count > 0) entries.RemoveAt(0);
            cache = new CacheStats
            {
                BudgetBytes = cc.BudgetMib * 1_048_576, UsedBytes = cc.Bytes,
                Slots = cc.Slots is double sl ? (int)sl : null, Parked = parked,
                Requests = cc.Requests, RequestsReused = cc.RequestsReused, Evictions = cc.Evictions,
                Entries = cacheLog.Readable ? entries : new List<CacheEntry>(), EntriesKnown = cacheLog.Readable,
            };
        }

        Totals? totals = null;
        if (!IsEmpty(m.Totals))
        {
            var tot = m.Totals!;
            totals = new Totals
            {
                Requests = tot.Requests, OutputTokens = tot.OutputTokens, PromptTokens = tot.PromptTokens, ReusedTokens = tot.Reused,
                PromptMilliseconds = tot.PromptMilliseconds, DecodeMilliseconds = tot.DecodeMilliseconds,
                Since = tot.Since is double since ? DateTime.UnixEpoch.AddSeconds(since) : null,
            };
        }

        EngineInfo? info = null;
        if (!IsEmpty(m.Engine))
            info = new EngineInfo
            {
                Version = eng.Version, KvFormat = eng.Kv, KvResidentTokens = eng.KvResident,
                DraftWindow = eng.Spec, ExpertCacheMib = eng.ExpertCacheMib, BatchSlots = eng.BatchSlots,
                ExpertHitRate = reqs.Count > 0 ? reqs[0].HitRate : null,
            };

        List<BatchSlot>? slots = null;
        if (rawSlots.Count > 0)
            slots = rawSlots.Select(s => new BatchSlot
            {
                Index = (int)(s.Slot ?? 0), State = s.State, HeldTokens = s.HeldTokens,
                PromptTokens = s.PromptTokens, PromptRead = s.PromptRead, Generated = s.Generated, TokensPerSecond = slotRates.TryGetValue((int)(s.Slot ?? -1), out var rate) ? rate : null, ElapsedSeconds = s.ElapsedSeconds, MaxTokens = s.MaxTokens,
            }).ToList();

        return new EngineSnapshot("Strata", model, status)
        {
            Queued = (int)(live.Queued ?? 0),
            Live = liveRequest, Recent = recent,
            Context = new ContextUsage { Used = ctxUsed, Max = eng.MaxContext, CompactAt = CompactAt(model) },   // null = the engine did not say
            System = system, Cache = cache, Totals = totals, Info = info, Slots = slots, Links = Links(),
        };
    }
}
