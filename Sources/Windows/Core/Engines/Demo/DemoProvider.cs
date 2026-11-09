namespace Stratoscope.Core.Engines.Demo;

/// <summary>
/// Fixed fake data. Used for the README screenshots (<c>STRATOSCOPE_ENGINE=demo</c>), for trying out the UI
/// without an engine, and as the smallest possible example of an <see cref="IEngineProvider"/>. Same numbers as DemoProvider.swift.
/// </summary>
public sealed class DemoProvider : IEngineProvider
{
    public const string Id = "demo";

    public enum Scenario { Prefill, Decode, Idle, Parallel, ParallelThree }

    private readonly Scenario scenario;
    private readonly Func<DateTime> clock;

    public DemoProvider(string? scenario = null, Func<DateTime>? clock = null)
    {
        switch (scenario)
        {
            case "decode": this.scenario = Scenario.Decode; break;
            case "idle": this.scenario = Scenario.Idle; break;
            case "parallel": this.scenario = Scenario.Parallel; break;
            case "parallel3": this.scenario = Scenario.ParallelThree; break;
            default: this.scenario = Scenario.Prefill; break;
        }
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    public string? LastError => null;

    public Task<EngineSnapshot?> PollAsync(CancellationToken cancellation) => Task.FromResult<EngineSnapshot?>(Snapshot());

    public EngineSnapshot Snapshot()
    {
        var now = clock();
        // (prompt tokens, tokens actually read, tokens generated, decode t/s, prefill t/s)
        RequestStats R(double prompt, double read, double output, double tps, double pre)
        {
            var ms = read / pre * 1000;
            return new RequestStats
            {
                PromptTokens = prompt, ReusedTokens = prompt - read, PromptRead = read, PromptMilliseconds = ms,
                OutputTokens = output, DecodeTokensPerSecond = tps, DurationSeconds = ms / 1000 + output / tps,
            };
        }
        // Newest first. The four oldest requests climb to the 110k marker; then the client starts over with a shorter conversation (the
        // drop), which the cache can mostly not reuse (orange).
        var recent = new List<RequestStats>
        {
            R(42385, 88, 9, 30.7, 190),     R(41120, 2100, 212, 27.9, 238),  R(40210, 23826, 640, 24.1, 231),
            R(39800, 3100, 118, 28.6, 245), R(38950, 2600, 305, 26.4, 252),  R(38020, 2600, 77, 29.3, 240),
            R(36400, 27400, 410, 25.2, 226), R(35900, 27900, 150, 27.0, 249), R(109200, 3500, 96, 28.8, 244),
            R(106900, 2900, 540, 25.9, 235), R(104300, 2300, 61, 29.9, 255),  R(101800, 3400, 233, 27.7, 241),
        };
        for (int i = 0; i < recent.Count; i++)
        {
            recent[i].FinishedAt = now.AddSeconds(-(90 + i * 195));
            recent[i].Finish = "stop";
        }
        const double gib = 1_073_741_824.0;
        LiveRequest? live = null;
        List<BatchSlot>? slots = null;
        var status = EngineStatus.Idle;
        switch (scenario)
        {
            case Scenario.Prefill:
                status = EngineStatus.Prefill;
                live = new LiveRequest
                {
                    PromptRead = 57_840, PromptTotal = 101_000, Generated = 0, MaxTokens = 24_576, ElapsedSeconds = 232,
                    PrefillTokensPerSecond = 247, ReusedEstimate = 41_000,
                };
                break;
            case Scenario.Decode:
                status = EngineStatus.Decode;
                live = new LiveRequest
                {
                    PromptRead = 61_000, PromptTotal = 61_000, Generated = 1_840, MaxTokens = 24_576, ElapsedSeconds = 94,
                    PrefillTokensPerSecond = 251, DecodeTokensPerSecond = 26.4, ReusedEstimate = 60_900,
                };
                break;
            case Scenario.Parallel:
            case Scenario.ParallelThree:
                // Three or four requests decode together; the headline is the engine total, each slot has its own derived speed.
                status = EngineStatus.Decode;
                var speeds = scenario == Scenario.ParallelThree ? new[] { 128.4, 104.2, 96.7 } : new[] { 128.4, 104.2, 96.7, 90.9 };
                slots = new List<BatchSlot>();
                for (int i = 0; i < speeds.Length; i++)
                    slots.Add(new BatchSlot
                    {
                        Index = i, State = "decoding", PromptTokens = 38_000 + i * 1_900, Generated = 1_840 - i * 310, TokensPerSecond = speeds[i], ElapsedSeconds = 94 - i * 17,
                    });
                if (scenario == Scenario.ParallelThree) slots.Add(new BatchSlot { Index = 3, State = "idle", HeldTokens = 41_430 });
                live = new LiveRequest
                {
                    PromptRead = 61_000, PromptTotal = 61_000, Generated = slots.Sum(s => s.Generated), MaxTokens = 24_576, ElapsedSeconds = 94,
                    DecodeTokensPerSecond = speeds.Sum(), Running = speeds.Length, ReusedEstimate = 60_900,
                };
                break;
        }
        // The context is the whole prompt plus what was generated so far (not only the part read so far).
        var contextUsed = scenario switch { Scenario.Prefill => 101_000, Scenario.Idle => 42_394, _ => 62_840 };
        return new EngineSnapshot("Demo", "demo-model", status)
        {
            Slots = slots,
            Live = live,
            Recent = recent,
            Context = new ContextUsage { Used = contextUsed, Max = 131_072, CompactAt = 110_000 },
            System = new SystemStats { RamUsedBytes = 48.3 * gib, RamTotalBytes = 64 * gib, GpuPercent = 94, CpuPercent = 18 },
            Cache = new CacheStats
            {
                BudgetBytes = 7 * gib, UsedBytes = 4.9 * gib, Slots = 4, Parked = 3, Requests = 214, RequestsReused = 186, Evictions = 12, EntriesKnown = true,
                Entries = new List<CacheEntry>
                {
                    new CacheEntry(5, 18_910, 0.8 * gib, now.AddSeconds(-3900)),
                    new CacheEntry(6, 42_385, 1.7 * gib, now.AddSeconds(-1260)),
                    new CacheEntry(7, 61_204, 2.4 * gib, now.AddSeconds(-195)),
                },
            },
            Totals = new Totals
            {
                Requests = 214, OutputTokens = 61_480, PromptTokens = 7_940_000, ReusedTokens = 6_660_000,
                Since = now.AddSeconds(-(3 * 3600 + 12 * 60)),
            },
            Links = new List<EngineLink> { new EngineLink("Open engine UI", "http://127.0.0.1:8095/") },
        };
    }
}
