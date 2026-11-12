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
        Assert.Equal(new[] { "/health", "/props", "/metrics", "/health", "/metrics" }, engine);       // the kind is asked once, then remembered
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
