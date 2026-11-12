using Stratoscope.Core.Engines;
using Stratoscope.Core.Engines.Custom;

namespace Stratoscope.Tests;

// The custom 27B server (its metrics use the q27: prefix) behind a llama-swap router. See Tests/Fixtures/custom/README.md for where each fixture comes from.

public class CustomParseTests
{
    private static CustomProvider Provider() => new(new EngineConfig(), new FakeSource());
    private static CustomMetrics Metrics(string fixture) => CustomMetrics.Parse(Fixtures.Text("custom/" + fixture))!;

    [Fact]
    public void PrometheusTextKeepsLabelsAndSkipsCommentsAndBadValues()
    {
        var values = PrometheusText.Parse("# HELP x\nq27:a 1\nq27:b{reason=\"kv\"} 2.5 123\nq27:c nan\nother:d 4\nq27:e\n", "q27:");
        Assert.Equal(2, values.Count);
        Assert.Equal(1, values["a"]);
        Assert.Equal(2.5, values["b{reason=\"kv\"}"]);
    }

    [Fact]
    public void TheRecordedIdleMetricsAreRead()
    {
        var m = Metrics("custom-idle.metrics.txt");
        Assert.Equal(0, m.Running);
        Assert.Equal(0, m.Waiting);
        Assert.Equal(4, m.EngineSeqs);
        Assert.Equal(3409, m.KvFreePages);
        Assert.Equal(1, m.KvCheckpointOnlyPages);
        Assert.Equal(1, m.PrefixCacheCheckpoints);
        Assert.Equal(34, m.PrefillTokensTotal);
        Assert.Equal(2, m.GenerationTokensTotal);
        Assert.Equal(0, m.BlockedKv);
        Assert.Equal(0, m.BlockedSpeed);
    }

    [Fact]
    public void TextWithoutQ27SeriesIsNoCustomDocument()
    {
        Assert.Null(CustomMetrics.Parse(null));
        Assert.Null(CustomMetrics.Parse("llamacpp:prompt_tokens_total 3\n"));
        Assert.Null(CustomMetrics.Parse("{\"status\":\"ok\"}"));
    }

    [Fact]
    public void AnIdleEngineIsReadyAndShowsWhatItReports()
    {
        var s = Provider().Translate(Metrics("custom-idle.metrics.txt"), "m");
        Assert.Equal("Custom", s.Engine);
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Equal(0, s.Queued);
        Assert.Null(s.Live);
        Assert.Equal(4, s.Info!.BatchSlots);
        Assert.Equal(3409, s.Info.KvFreePages);
        Assert.Equal(1, s.Info.PrefixCheckpoints);
        Assert.Equal("none", s.Info.AdmissionBlocked);
        Assert.Equal(34, s.Totals!.PromptTokens);
        Assert.Equal(2, s.Totals.OutputTokens);
        Assert.Null(s.Context.Max);                         // the server reports no context size
        Assert.Null(s.System);
        Assert.Empty(s.Recent);
    }

    [Fact]
    public void RunningRequestsAreRunningWithoutAPhase()
    {
        var running = Provider().Translate(Metrics("custom-busy.metrics.txt"), "m");
        Assert.Equal(EngineStatus.Running, running.Status);   // the metrics give no per-request phase: neither reading nor writing
        Assert.Equal(3, running.Live!.Running);
        Assert.Null(running.Live.PrefillTokensPerSecond);   // no speed is reported
        Assert.Null(running.Live.DecodeTokensPerSecond);
        Assert.Equal(2, running.Queued);
        Assert.Equal("kv", running.Info!.AdmissionBlocked);
        Assert.Equal(12, running.Info.KvCheckpointOnlyPages);
        Assert.Equal(5120, running.Totals!.OutputTokens);
    }

    [Fact]
    public void WaitingRequestsOfAnIdleSchedulerOnlyCountInTheQueue()
    {
        var s = Provider().Translate(CustomMetrics.Parse("q27:num_requests_running 0\nq27:num_requests_waiting 3\n")!, "m");
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Equal(3, s.Queued);
        Assert.Null(s.Info);                                // nothing else was reported: no section
        Assert.Null(s.Totals);
    }

    [Fact]
    public void AMissingBlockFlagIsUnknownNotNotBlocked()
    {
        var s = Provider().Translate(CustomMetrics.Parse("q27:num_requests_running 0\nq27:engine_seqs 4\nq27:admission_blocked{reason=\"kv\"} 0\n")!, "m");
        Assert.Null(s.Info!.AdmissionBlocked);
        Assert.Null(s.Info.KvFreePages);
    }
}

public class CustomDetectionTests
{
    private const string Router = "http://router:1";
    private const string Upstream = Router + "/upstream/demo-model";

    private static FakeSource Custom() => new FakeSource()
        .Route(Router + "/running", () => new FakeAnswer(200, Fixtures.Text("router-running-ready.json")))
        .Json(Upstream + "/health", "custom/custom-idle.health.json").Status(Upstream + "/props", 404)
        .Route(Upstream + "/metrics", () => new FakeAnswer(200, Fixtures.Text("custom/custom-idle.metrics.txt")));

    private static AutoEngineProvider Auto(FakeSource source) => new(new EngineConfig { Router = Router }, source);

    [Fact]
    public async Task AnUnnamedEngineWithoutPropsThatSendsQ27MetricsIsCustom()
    {
        var source = Custom();
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal("Custom", s!.Engine);
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Equal(3409, s.Info!.KvFreePages);
    }

    [Fact]
    public async Task ProbesAreOnlyGetsOfExistingEndpointsInOrderHealthPropsMetrics()
    {
        var source = Custom();
        var provider = Auto(source);
        await provider.PollAsync(CancellationToken.None);
        var engine = source.Requested.Where(r => r.StartsWith(Upstream)).Select(r => r.Substring(Upstream.Length)).ToList();
        Assert.Equal(new[] { "/health", "/props", "/metrics", "/health", "/metrics", "/ui/state" }, engine);       // the kind is asked once, then remembered
        source.Requested.Clear();
        await provider.PollAsync(CancellationToken.None);
        Assert.DoesNotContain(Upstream + "/props", source.Requested);
        Assert.DoesNotContain(Upstream + "/slots", source.Requested);
    }

    [Fact]
    public async Task ALlamaServerIsNotTakenForCustomAndStrataJsonStaysStrata()
    {
        var llama = new FakeSource().Route(Router + "/running", () => new FakeAnswer(200, Fixtures.Text("router-running-ready.json")))
            .Text(Upstream + "/health", @"{""status"":""ok""}").Json(Upstream + "/props", "llamacpp-props.json")
            .Json(Upstream + "/slots", "llamacpp-idle.slots.json").Route(Upstream + "/metrics", () => new FakeAnswer(200, Fixtures.Text("llamacpp-idle.metrics.txt")));
        Assert.Equal("llama.cpp", (await Auto(llama).PollAsync(CancellationToken.None))!.Engine);
        Assert.DoesNotContain(Upstream + "/metrics", llama.Requested.Take(2));

        var strata = new FakeSource().Route(Router + "/running", () => new FakeAnswer(200, Fixtures.Text("router-running-ready.json")))
            .Text(Upstream + "/health", @"{""status"":""ok""}").Status(Upstream + "/props", 404).Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        Assert.Equal("Strata", (await Auto(strata).PollAsync(CancellationToken.None))!.Engine);
    }

    [Fact]
    public async Task AnEngineThatIsNotReadyIsLoadingAndItsMetricsAreNotAsked()
    {
        var source = Custom().Text(Upstream + "/health", @"{""status"":""unloaded""}");
        var s = await new CustomProvider(new EngineConfig { Router = Router }, source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Loading, s!.Status);
        Assert.Equal("State: unloaded", s.LoadingReason);
        Assert.DoesNotContain(Upstream + "/metrics", source.Requested);
    }

    [Fact]
    public async Task ConfiguredCustomReadsItDirectlyAndRefusesADocumentWithoutQ27()
    {
        const string address = "http://127.0.0.1:8095";
        var ok = new FakeSource().Json(address + "/health", "custom/custom-idle.health.json")
            .Route(address + "/metrics", () => new FakeAnswer(200, Fixtures.Text("custom/custom-idle.metrics.txt")));
        Assert.Equal("Custom", (await new CustomProvider(new EngineConfig { Url = address }, ok).PollAsync(CancellationToken.None))!.Engine);

        var other = new FakeSource().Json(address + "/health", "custom/custom-idle.health.json").Text(address + "/metrics", "# nothing\n");
        var provider = new CustomProvider(new EngineConfig { Url = address }, other);
        Assert.Null(await provider.PollAsync(CancellationToken.None));
        Assert.Contains("not a Custom metrics document", provider.LastError);
    }

    [Fact]
    public void TheRegistryChoosesCustomFromTheEngineSetting()
    {
        Assert.IsType<CustomProvider>(EngineRegistry.Make(new EngineConfig { Engine = "custom" }, new FakeSource()));
        Assert.Contains("custom", EngineRegistry.Available);
    }
}

// The live capture custom-ui-state-idle.json (GET /ui/state, 2026-11-12, idle engine after two short requests, RTX 5090 Laptop GPU): expected values are
// read from that file. Metrics are the recorded idle ones (4 sequences, 3409 free KV pages, 1 checkpoint).
public class CustomIdleCaptureTests
{
    private static EngineSnapshot Idle()
    {
        var m = CustomMetrics.Parse(Fixtures.Text("custom/custom-idle.metrics.txt"))!;
        var ui = Json.Deserialize(Fixtures.Text("custom/custom-ui-state-idle.json"), Json.CustomUiStateType)!;
        return new CustomProvider(new EngineConfig(), new FakeSource()).Translate(m, "m", ui);
    }

    [Fact]
    public void TheEngineLineComesFromInfoAndSched()
    {
        var info = Idle().Info!;
        Assert.Equal("NVIDIA GeForce RTX 5090 Laptop GPU", info.GpuName);   // info.gpu
        Assert.Equal("DFlash2", info.DraftWindow);                          // info.spec true
        Assert.Equal("nvfp4", info.KvFormat);                               // info.kv_dtype
        Assert.Equal(4, info.BatchSlots);                                   // q27:engine_seqs
        Assert.Equal(4, info.TargetSeqs);                                   // tick.sched.target_seqs
        Assert.Equal(3409, info.KvFreePages);
    }

    [Fact]
    public void TheCardAndTheVideoMemoryComeFromTheSampleAndVram()
    {
        var s = Idle().System!;
        Assert.Equal(0, s.GpuPercent);                                      // sample.util
        Assert.Equal(0, s.GpuMemoryBusyPercent);                            // sample.mem_util
        Assert.Equal(34, s.GpuTemperatureCelsius);
        Assert.Equal(7.178, s.GpuPowerWatts);
        Assert.Equal(175, s.GpuPowerLimitWatts);
        Assert.Equal(25651314688, s.GpuMemoryTotalBytes);                   // vram.total
        Assert.Equal(25651314688 - 2007592960, s.GpuMemoryUsedBytes);       // minus the "free" part
        Assert.Null(s.RamUsedBytes);                                        // not in /ui/state
        Assert.Null(s.CpuPercent);
    }

    [Fact]
    public void TheKvCacheAndTheContextComeFromVramCacheAndInfo()
    {
        var s = Idle();
        Assert.Equal(4034396160, s.Cache!.BudgetBytes);                     // vram part "KV cache"
        Assert.Equal(4034396160 - 4022599680, s.Cache.UsedBytes);           // minus its "free" sub-part
        Assert.Equal(2, s.Cache.Requests);                                  // cache.lookups
        Assert.Equal(0, s.Cache.RequestsReused);                            // cache.hits
        Assert.Equal(0, s.Cache.Evictions);                                 // cache.evicted
        Assert.Equal(98304, s.Context.Max);                                 // info.max_tokens
        Assert.Equal(18, s.Context.Used);                                   // newest finished request: 17 prompt + 1 output
    }

    [Fact]
    public void FinishedRequestsAreTheDoneEventsOfTheLogNewestFirst()
    {
        var r = Idle().Recent;
        Assert.Equal(2, r.Count);
        Assert.Equal(0.26059770584106445, r[0].DurationSeconds);            // the later one
        Assert.Equal(0.15842962265014648, r[1].DurationSeconds);
        Assert.Equal(17, r[1].PromptTokens);
        Assert.Equal(0, r[1].ReusedTokens);                                 // cached_tokens
        Assert.Equal(17, r[1].PromptRead);                                  // computed_tokens
        Assert.Equal(116.1649227142334, r[1].PromptMilliseconds!.Value, 6); // prefill_s
        Assert.Equal(1, r[1].OutputTokens);
        Assert.Equal("stop", r[1].Finish);
        Assert.Null(r[1].DecodeTokensPerSecond);                            // decode_tps null
        Assert.Equal(new DateTime(2026, 11, 12, 2, 37, 51, DateTimeKind.Utc).AddMilliseconds(573.377), r[1].FinishedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void EmptyEngineSlotsAreIdleRowsUpToTheEngineCount()
    {
        var slots = Idle().Slots!;
        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal("idle", s.State));
        Assert.Equal(new[] { 0, 1, 2, 3 }, slots.Select(s => s.Index));
    }
}

// Per-request reading and writing from /ui/state. The fixture custom-ui-state-busy.json is hand-written after the engine's source (worker.py, stats.py),
// not captured from a live request. Its requests: r1 running prefill (prompt 6000, fed 2400, computed 2400, prefill_s 3.0), r2 running decode
// (out 300, max_new 2048, decode_s 10.0), r3 and r5 waiting, r4 running prefill with prefill_s 0.
public class CustomUiStateTests
{
    private const string Address = "http://127.0.0.1:8095";

    private static CustomProvider Provider() => new(new EngineConfig(), new FakeSource());
    private static CustomMetrics Busy() => CustomMetrics.Parse(Fixtures.Text("custom/custom-busy.metrics.txt"))!;
    private static CustomUiState Ui(params string[] rids)
    {
        var all = Json.Deserialize(Fixtures.Text("custom/custom-ui-state-busy.json"), Json.CustomUiStateType)!;
        return rids.Length == 0 ? all : new CustomUiState { Tick = new CustomTick { Seqs = all.Seqs!.Where(s => rids.Contains(s!.Rid)).ToList() } };
    }

    [Fact]
    public void APrefillSeqIsReadingWithFedAndPromptAndItsSpeed()
    {
        var s = Provider().Translate(Busy(), "m", Ui("r1"));
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(2400, s.Live!.PromptRead);                 // fed
        Assert.Equal(6000, s.Live.PromptTotal);                 // prompt
        Assert.Equal(40, s.Live.Percent);
        Assert.Equal(800, s.Live.PrefillTokensPerSecond);       // computed 2400 / prefill_s 3.0
        Assert.Null(s.Live.DecodeTokensPerSecond);
    }

    [Fact]
    public void ADecodeSeqIsWritingWithOutAndMaxNewAndItsSpeed()
    {
        var s = Provider().Translate(Busy(), "m", Ui("r2"));
        Assert.Equal(EngineStatus.Decode, s.Status);
        Assert.Equal(300, s.Live!.Generated);                   // out
        Assert.Equal(2048, s.Live.MaxTokens);                   // max_new
        Assert.Equal(30, s.Live.DecodeTokensPerSecond);         // out 300 / decode_s 10.0
        Assert.Null(s.Live.PrefillTokensPerSecond);
    }

    [Fact]
    public void WaitingSeqsAreQueuedAndNotBusy()
    {
        var s = Provider().Translate(Busy(), "m", Ui());
        Assert.Equal(2, s.Queued);                              // r3 and r5
        Assert.Equal(3, s.Live!.Running);                       // r1, r2, r4 run; the waiting ones do not
        Assert.Equal(EngineStatus.Decode, s.Status);            // r2 writes
        var onlyWaiting = Provider().Translate(Busy(), "m", Ui("r3"));
        Assert.Equal(EngineStatus.Running, onlyWaiting.Status); // nothing in the list runs: the aggregate state stays
    }

    [Fact]
    public void ParkedDecodingRequestsCountAsWritingAndAdmittedPrefillAsReading()
    {
        // The engine's own phase: a parked request that is decoding is still decoding. Without token counts there is no speed.
        var ui = new CustomUiState { Tick = new CustomTick { Seqs = [new CustomSeq { State = "parked", Phase = "decode" }, new CustomSeq { State = "admitted", Phase = "prefill" }] } };
        var s = Provider().Translate(Busy(), "m", ui);
        Assert.Equal(EngineStatus.Decode, s.Status);
        Assert.Equal(2, s.Live!.Running);
        Assert.Null(s.Live.Generated);
        Assert.Null(s.Live.DecodeTokensPerSecond);
    }

    [Fact]
    public void TheHeadlineSpeedIsTheEnginesOwnTotalOverAllRequests()
    {
        // Strata shows one total for the batch (live.tok_s). The custom engine publishes the same total in its live sample (tick.sample).
        var ui = new CustomUiState { Tick = new CustomTick { Seqs = [new CustomSeq { State = "running", Phase = "decode", Out = 150, DecodeSeconds = 1.5 }, new CustomSeq { State = "parked", Phase = "decode", Out = 90, DecodeSeconds = 2.0 }], Sample = new CustomSample { DecodeTps = 123.4 } } };
        var s = Provider().Translate(Busy(), "m", ui);
        Assert.Equal(123.4, s.Live!.DecodeTokensPerSecond);
        Assert.Equal(2, s.Live.Running);
    }

    [Fact]
    public void ASeqWithZeroTimeHasNoSpeed()
    {
        var s = Provider().Translate(Busy(), "m", Ui("r4"));
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(0, s.Live!.PromptRead);                    // the engine says fed 0
        Assert.Null(s.Live.PrefillTokensPerSecond);             // prefill_s 0: no speed
    }

    [Fact]
    public async Task AnEngineWithoutUiStateStaysRunning()
    {
        var source = new FakeSource().Json(Address + "/health", "custom/custom-idle.health.json").Json(Address + "/metrics", "custom/custom-busy.metrics.txt");
        var s = await new CustomProvider(new EngineConfig { Url = Address }, source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Running, s!.Status);
        Assert.Null(s.Live!.PromptRead);
        Assert.Null(s.Live.Generated);
        Assert.Contains(Address + "/ui/state", source.Requested);

        var notJson = new FakeSource().Json(Address + "/health", "custom/custom-idle.health.json").Json(Address + "/metrics", "custom/custom-busy.metrics.txt")
            .Text(Address + "/ui/state", "<html>");
        Assert.Equal(EngineStatus.Running, (await new CustomProvider(new EngineConfig { Url = Address }, notJson).PollAsync(CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task UiStateIsReadOnEveryPollAndMapsThroughTheProvider()
    {
        var idle = new FakeSource().Json(Address + "/health", "custom/custom-idle.health.json").Json(Address + "/metrics", "custom/custom-idle.metrics.txt")
            .Json(Address + "/ui/state", "custom/custom-ui-state-idle.json");
        var idleSnapshot = await new CustomProvider(new EngineConfig { Url = Address }, idle).PollAsync(CancellationToken.None);
        Assert.Contains(Address + "/ui/state", idle.Requested);
        Assert.Equal(98304, idleSnapshot!.Context.Max);

        var busy = new FakeSource().Json(Address + "/health", "custom/custom-idle.health.json").Json(Address + "/metrics", "custom/custom-busy.metrics.txt")
            .Json(Address + "/ui/state", "custom/custom-ui-state-busy.json");
        var s = await new CustomProvider(new EngineConfig { Url = Address }, busy).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Decode, s!.Status);
        Assert.Equal(3, s.Live!.Running);
        Assert.Equal(2, s.Queued);
    }
}
