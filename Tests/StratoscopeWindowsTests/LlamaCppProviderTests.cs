using System.IO;
using Stratoscope.Core.Engines;
using Stratoscope.Core.Engines.LlamaCpp;

namespace Stratoscope.Tests;

// llama-server (llama.cpp tools/server) behind a llama-swap router. See Tests/Fixtures/README.md for where each fixture comes from.

public class LlamaCppSnapshotTests
{
    private static LlamaCppProvider Provider() => new(new EngineConfig(), new FakeSource());

    private static List<LlamaSlotDto?> Slots(string fixture) =>
        Json.Deserialize(Fixtures.Text(fixture), Json.LlamaSlotsType) ?? throw new InvalidDataException(fixture);

    private static LlamaProps Props() => Json.Deserialize(Fixtures.Text("llamacpp-props.json"), Json.LlamaPropsType)!;

    private static LlamaCppMetrics Metrics(string fixture) => LlamaCppMetrics.Parse(Fixtures.Text(fixture))!;

    [Fact]
    public void ARunningSlotWithNothingGeneratedIsReadingAndShowsWhatTheEngineSays()
    {
        var s = Provider().Translate(Slots("llamacpp-prefill.slots.json"), Props(), Metrics("llamacpp-prefill.metrics.txt"), "m");
        Assert.Equal("llama.cpp", s.Engine);
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal(0, s.Queued);
        Assert.Equal(26377, s.Live!.PromptRead);            // /slots n_prompt_tokens_processed 26377 + n_prompt_tokens_cache 0
        Assert.Equal(26889, s.Live.PromptTotal);            // /slots n_prompt_tokens, the request's whole prompt
        Assert.NotNull(s.Live.Percent);
        Assert.Null(s.Live.PrefillTokensPerSecond);         // the engine counts a prompt into its speed gauge only when it is read: 0 during a long read, unknown here
        Assert.Null(s.Live.DecodeTokensPerSecond);          // the gauge is 0 when nothing was generated: unknown, not 0
        Assert.Equal(0, s.Live.Generated);
        Assert.Equal(0, s.Live.ReusedEstimate);             // the engine counted tokens and reused none: a real 0
        Assert.Equal(400, s.Live.MaxTokens);
        Assert.Null(s.Live.Running);
        Assert.Equal(26889, s.Context.Used);
        Assert.Equal(98304, s.Context.Max);
    }

    [Fact]
    public void ThePromptSpeedGaugeIsShownWhileReadingAndOnlyWhenItIsAboveZero()
    {
        var rows = Slots("llamacpp-prefill.slots.json");
        var read = Provider().Translate(rows, Props(), LlamaCppMetrics.Parse("llamacpp:prompt_tokens_seconds 1423.5\nllamacpp:predicted_tokens_seconds 90\n"), "m");
        Assert.Equal(1423.5, read.Live!.PrefillTokensPerSecond);
        Assert.Null(read.Live.DecodeTokensPerSecond);       // nothing is generated yet: the generation gauge is not this request's
        var idle = Provider().Translate(Slots("llamacpp-idle.slots.json"), Props(), Metrics("llamacpp-idle.metrics.txt"), "m");
        Assert.Null(idle.Live);                             // the gauges still hold the last window while idle: not shown as a speed
    }

    [Fact]
    public void ASlotThatHasJustStartedHasNoCountsYetAndAnUnlimitedRequestHasNoLimit()
    {
        var s = Provider().Translate(Slots("llamacpp-prefill-start.slots.json"), Props(), null, "m");
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Null(s.Live!.PromptRead);                    // the slot still holds the last conversation until its first batch
        Assert.Null(s.Live.ReusedEstimate);                 // 0 read and 0 reused at the start is not a cache miss
        Assert.Null(s.Live.MaxTokens);                      // -1 means no limit
        Assert.Null(s.Context.Used);
    }

    [Fact]
    public void GeneratedTokensMakeItDecodeWithTheEnginesOwnSpeed()
    {
        var s = Provider().Translate(Slots("llamacpp-decode.slots.json"), Props(), Metrics("llamacpp-decode.metrics.txt"), "m");
        Assert.Equal(EngineStatus.Decode, s.Status);
        Assert.Equal(316, s.Live!.Generated);
        Assert.Equal(236.42, s.Live.DecodeTokensPerSecond);
        Assert.Null(s.Live.PrefillTokensPerSecond);
        Assert.Null(s.Live.PromptRead);
        Assert.Equal(447, s.Live.ReusedEstimate);
        Assert.Equal(782, s.Context.Used);                // the slot's memory: prompt plus what it has written
        Assert.Equal(0, s.Queued);
    }

    [Fact]
    public void IdleHasNoRunningRequestAndTheContextOfTheConversationTheSlotHolds()
    {
        var s = Provider().Translate(Slots("llamacpp-idle.slots.json"), Props(), Metrics("llamacpp-idle.metrics.txt"), "m");
        Assert.Equal(EngineStatus.Idle, s.Status);
        Assert.Null(s.Live);
        Assert.Null(s.Slots);                               // one slot has nothing to tell apart
        Assert.Equal(1010, s.Context.Used);
        Assert.Equal(98304, s.Context.Max);
        Assert.Empty(s.Recent);                             // a slot resets its counters when a request ends: nothing to list
    }

    [Fact]
    public void SeveralSlotsAreListedAndTheHeadlineIsTheirTotal()
    {
        var rows = Slots("llamacpp-parallel-mixed.slots.json");
        var s = Provider().Translate(rows, Props(), Metrics("llamacpp-decode.metrics.txt"), "m");
        Assert.Equal(EngineStatus.Decode, s.Status);        // generating while any slot decodes
        Assert.Equal(new[] { "decoding", "reading", "idle", "idle" }, s.Slots!.Select(x => x.State));
        Assert.Equal(new[] { 0, 1, 2, 3 }, s.Slots!.Select(x => x.Index));
        Assert.Equal(2, s.Live!.Running);
        Assert.True(s.Live.IsParallel);
        Assert.Equal(378, s.Live.Generated);
        Assert.Null(s.Live.MaxTokens);                      // one limit per request, not for the total
        Assert.Null(s.Live.ReusedEstimate);
        Assert.Equal(4096, s.Slots![0].MaxTokens);
        Assert.Null(s.Slots[1].MaxTokens);                  // no limit
        Assert.Equal(5120, s.Slots[1].PromptRead);
        Assert.Equal(5120, s.Slots[1].PromptTokens);        // /slots n_prompt_tokens of the reading slot
        Assert.Equal(18000, s.Slots[2].HeldTokens);         // an idle slot says what it holds
        Assert.False(s.Slots[2].IsActive);
        Assert.Null(s.Slots[3].HeldTokens);                 // a slot that never ran has no task and says nothing
        Assert.Equal(9321, s.Context.Used);                 // the fullest running slot
        Assert.Equal(32768, s.Context.Max);
    }

    [Fact]
    public void TotalsAreTheCountersSinceTheServerStartedAndTheCacheShareComesFromThem()
    {
        var t = Provider().Translate(Slots("llamacpp-idle.slots.json"), Props(), Metrics("llamacpp-decode.metrics.txt"), "m").Totals!;
        Assert.Equal(29304 + 53713, t.PromptTokens);        // what was read plus what was reused
        Assert.Equal(53713, t.ReusedTokens);
        Assert.Equal(2343, t.OutputTokens);
        Assert.Equal(18536.5, t.PromptMilliseconds);
        Assert.Equal(9880.73, t.DecodeMilliseconds);
        Assert.Null(t.Requests);                            // no request count in the metrics
        Assert.Null(t.Since);
    }

    [Fact]
    public void WithoutMetricsAndPropsOnlyWhatSlotsSayIsShown()
    {
        var s = Provider().Translate(Slots("llamacpp-decode.slots.json"), null, null, "m");
        Assert.Equal(EngineStatus.Decode, s.Status);
        Assert.Null(s.Live!.DecodeTokensPerSecond);
        Assert.Null(s.Totals);
        Assert.Null(s.Info);
        Assert.Equal(0, s.Queued);
        Assert.Equal(98304, s.Context.Max);                 // every slot says its context size
    }

    [Fact]
    public void TheBuildAndTheSlotCountComeFromProps()
    {
        var info = Provider().Translate(Slots("llamacpp-idle.slots.json"), Props(), null, "m").Info!;
        Assert.Equal("b1-d493f84", info.Version);
        Assert.Equal(1, info.BatchSlots);
        Assert.Null(info.KvFormat);
    }

    [Fact]
    public void MissingFieldsAreUnknownNotZero()
    {
        var rows = Json.Deserialize(@"[{""is_processing"": true, ""n_prompt_tokens"": ""many"", ""next_token"": ""x""}, null]", Json.LlamaSlotsType)!;
        var s = Provider().Translate(rows, null, null, "m");
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Null(s.Live!.PromptRead);
        Assert.Null(s.Live.Generated);                      // the slot did not say: not 0
        Assert.Null(s.Live.ReusedEstimate);
        Assert.Null(s.Context.Used);
        Assert.Null(s.Context.Max);
    }

    [Fact]
    public void ThePrometheusTextIsReadByNameAndOnlyFiniteNumbersCount()
    {
        var m = LlamaCppMetrics.Parse("# HELP llamacpp:x y\nllamacpp:prompt_tokens_seconds 12.5\r\nllamacpp:predicted_tokens_seconds nan\nllamacpp:requests_deferred{slot=\"1\"} 4\nllamacpp:requests_processing 3 1700000000\nother:prompt_tokens_total 9\n")!;
        Assert.Equal(12.5, m.PromptTokensPerSecond);
        Assert.Null(m.PredictedTokensPerSecond);
        Assert.Null(m.RequestsDeferred);                    // labelled series are not read
        Assert.Equal(3, m.RequestsProcessing);              // a timestamp after the value is ignored
        Assert.Null(m.PromptTokensTotal);                   // another prefix is another program's metric
        Assert.Null(LlamaCppMetrics.Parse("# nothing\nprocess_cpu_seconds_total 1\n"));
        Assert.Null(LlamaCppMetrics.Parse(""));
        Assert.Null(LlamaCppMetrics.Parse(null));
    }

    [Fact]
    public void OnlyALlamaServersPropsAreRecognised()
    {
        Assert.True(Props().IsLlamaServer);
        Assert.False(Json.Deserialize(@"{""status"":""ok""}", Json.LlamaPropsType)!.IsLlamaServer);
        Assert.False(Json.Deserialize(@"{""total_slots"":""4""}", Json.LlamaPropsType)!.IsLlamaServer);
        Assert.False(Json.Deserialize(@"{""default_generation_settings"":{""n_ctx"":8192}}", Json.LlamaPropsType)!.IsLlamaServer);
    }
}

public class LlamaCppFlowTests
{
    private const string Router = "http://router:1";
    private const string Upstream = Router + "/upstream/demo-model";

    private static FakeSource LlamaServer(string state = "prefill") =>
        new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Text(Upstream + "/health", @"{""status"":""ok""}")
            .Json(Upstream + "/props", "llamacpp-props.json")
            .Json(Upstream + "/slots", "llamacpp-" + state + ".slots.json")
            .Route(Upstream + "/metrics", () => new FakeAnswer(200, Fixtures.Text("llamacpp-" + state + ".metrics.txt")));

    private static AutoEngineProvider Auto(FakeSource source, string? url = null) => new(new EngineConfig { Router = Router, Url = url }, source);

    [Fact]
    public async Task ALlamaServerBehindTheRouterIsRecognisedByItsPropsAndReadWithGetRequestsOnly()
    {
        var source = LlamaServer();
        var provider = Auto(source);
        var s = await provider.PollAsync(CancellationToken.None);
        Assert.Equal("llama.cpp", s!.Engine);
        Assert.Equal("llama.cpp", provider.Brand);
        Assert.Equal(EngineStatus.Prefill, s.Status);
        Assert.Equal("Demo Model (4 parallel)", s.Model);   // the router's friendly name
        Assert.Equal(26377, s.Live!.PromptRead);
        Assert.Equal(new[] { Router + "/running", Upstream + "/health", Upstream + "/props", Upstream + "/props", Upstream + "/slots", Upstream + "/metrics" }, source.Requested);
        Assert.Contains(s.Links, l => l.Title == "Open llama.cpp UI" && l.Target == Upstream + "/");

        source.Requested.Clear();
        await provider.PollAsync(CancellationToken.None);
        Assert.Equal(new[] { Router + "/running", Upstream + "/props", Upstream + "/slots", Upstream + "/metrics" }, source.Requested);   // the kind is asked once
    }

    [Fact]
    public async Task ASleepingServerIsShownAsAsleepAndItsSlotsAreNotAskedBecauseThatWouldWakeIt()
    {
        var source = LlamaServer().Text(Upstream + "/props", @"{""total_slots"":1,""build_info"":""b1"",""is_sleeping"":true}");
        var provider = Auto(source);
        await provider.PollAsync(CancellationToken.None);      // the kind is found by the probe
        source.Requested.Clear();
        var s = await provider.PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Loading, s!.Status);
        Assert.Equal("Asleep", s.LoadingReason);
        Assert.Equal(new[] { Router + "/running", Upstream + "/props" }, source.Requested);
    }

    [Fact]
    public async Task ServerWithoutMetricsFlagIsReadWithoutAskingForMetrics()
    {
        var source = LlamaServer().Text(Upstream + "/props", @"{""total_slots"":1,""build_info"":""b1"",""endpoint_metrics"":false}");
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal("llama.cpp", s!.Engine);
        Assert.DoesNotContain(Upstream + "/metrics", source.Requested);
        Assert.Null(s.Totals);
    }

    [Fact]
    public async Task MetricsThatAreRefusedLeaveTheSpeedsUnknownAndTheRestShown()
    {
        var source = LlamaServer().Status(Upstream + "/metrics", 501);
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Prefill, s!.Status);
        Assert.Null(s.Live!.PrefillTokensPerSecond);
        Assert.Equal(26377, s.Live.PromptRead);
        Assert.Null(s.Totals);
    }

    [Fact]
    public async Task ServerWithoutSlotsEndpointIsAnErrorState()
    {
        var source = LlamaServer().Status(Upstream + "/slots", 501);
        var s = await Auto(source).PollAsync(CancellationToken.None);
        Assert.Equal(EngineStatus.Error, s!.Status);
    }

    [Fact]
    public async Task AnEngineWhoseHealthNamesNothingAndWhoseBodyIsNoLlamaServerIsReadAsStrataAndAskedOnce()
    {
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Text(Upstream + "/health", @"{""status"":""ok""}").Status(Upstream + "/props", 404)
            .Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        var provider = Auto(source);
        Assert.Equal("Strata", (await provider.PollAsync(CancellationToken.None))!.Engine);
        // The kind is asked once: health, props, and a look at /metrics for the custom 27B server's q27: series (Strata's JSON has none), then the Strata read.
        Assert.Equal(new[] { Router + "/running", Upstream + "/health", Upstream + "/props", Upstream + "/metrics", Upstream + "/metrics" }, source.Requested);
        source.Requested.Clear();
        await provider.PollAsync(CancellationToken.None);
        Assert.Equal(new[] { Router + "/running", Upstream + "/metrics" }, source.Requested);
    }

    [Fact]
    public async Task AProbeThatCannotBeAnsweredIsAskedAgain()
    {
        var up = false;
        var source = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Text(Upstream + "/health", @"{""status"":""ok""}")
            .Route(Upstream + "/props", () => up ? new FakeAnswer(200, Fixtures.Text("llamacpp-props.json")) : new FakeAnswer(0, null, FetchFailure.Timeout))
            .Json(Upstream + "/slots", "llamacpp-idle.slots.json").Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        var provider = Auto(source);
        await provider.PollAsync(CancellationToken.None);       // the probe timed out: Strata for now, not remembered
        up = true;
        source.Requested.Clear();
        var s = await provider.PollAsync(CancellationToken.None);
        Assert.Equal("llama.cpp", s!.Engine);
        Assert.Contains(Upstream + "/health", source.Requested);
    }

    [Fact]
    public async Task StrataAndBasaltAreStillRecognisedByTheirHealthWithoutAskingForProps()
    {
        var strata = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Text(Upstream + "/health", @"{""status"":""ok"",""service"":""strata""}").Json(Upstream + "/metrics", "strata-0.1.56-idle.json");
        Assert.Equal("Strata", (await Auto(strata).PollAsync(CancellationToken.None))!.Engine);
        Assert.DoesNotContain(Upstream + "/props", strata.Requested);

        var basalt = new FakeSource().Json(Router + "/running", "router-running-ready.json")
            .Json(Upstream + "/health", "basalt-0.3.2-idle.health.json").Json(Upstream + "/metrics", "basalt-0.3.2-idle.json");
        Assert.Equal("Basalt", (await Auto(basalt).PollAsync(CancellationToken.None))!.Engine);
        Assert.DoesNotContain(Upstream + "/props", basalt.Requested);
    }

    [Fact]
    public async Task RouterLimitKeepsTheLastLlamaCppData()
    {
        var limited = false;
        var source = LlamaServer().Route(Upstream + "/slots", () => limited ? new FakeAnswer(429, null, FetchFailure.Status) : new FakeAnswer(200, Fixtures.Text("llamacpp-prefill.slots.json")));
        var provider = Auto(source);
        Assert.False((await provider.PollAsync(CancellationToken.None))!.RouterLimited);
        limited = true;
        var second = await provider.PollAsync(CancellationToken.None);
        Assert.True(second!.RouterLimited);
        Assert.Equal("llama.cpp", second.Engine);
    }

    [Fact]
    public async Task WithoutARouterLlamaServerIsReadDirectlyAndNamedByItsAlias()
    {
        const string address = "http://127.0.0.1:8095";
        var source = new FakeSource().Text(address + "/health", @"{""status"":""ok""}").Json(address + "/props", "llamacpp-props.json")
            .Json(address + "/slots", "llamacpp-decode.slots.json").Route(address + "/metrics", () => new FakeAnswer(200, Fixtures.Text("llamacpp-decode.metrics.txt")));
        var s = await new AutoEngineProvider(new EngineConfig { Url = address }, source).PollAsync(CancellationToken.None);
        Assert.Equal("llama.cpp", s!.Engine);
        Assert.Equal("qwen3.8-27b-ud-q4_k_m", s.Model);
        Assert.Equal(EngineStatus.Decode, s.Status);
    }

    [Fact]
    public async Task ADocumentThatIsNoSlotListIsNotARunningLlamaServer()
    {
        const string address = "http://127.0.0.1:8095";
        var source = new FakeSource().Text(address + "/slots", @"[{""x"":1}]");
        var provider = new LlamaCppProvider(new EngineConfig { Url = address }, source);
        Assert.Null(await provider.PollAsync(CancellationToken.None));
        Assert.Contains("not a llama.cpp metrics document", provider.LastError);
    }

    [Fact]
    public void TheRegistryChoosesTheProviderFromTheEngineSetting()
    {
        var source = new FakeSource();
        Assert.IsType<LlamaCppProvider>(EngineRegistry.Make(new EngineConfig { Engine = "llamacpp" }, source));
        Assert.IsType<AutoEngineProvider>(EngineRegistry.Make(new EngineConfig { Engine = "strata" }, source));
        Assert.Contains("llamacpp", EngineRegistry.Available);
    }
}
