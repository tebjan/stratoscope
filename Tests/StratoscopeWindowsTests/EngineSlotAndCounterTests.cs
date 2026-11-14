using Stratoscope.Core.Engines;
using Stratoscope.Core.Engines.Basalt;
using Stratoscope.Core.Engines.LlamaCpp;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Tests;

// Per adapter: the batch slots reach the per-request bars of the readout, and the history curves take their rates from the slots because no
// counter of these engines advances while a request runs (Totals.PrefillComputedTokens stays empty). The custom engine has its own tests.

public class EngineSlotAndCounterTests
{
    private static DateTime At(double seconds) => DateTime.UnixEpoch.AddSeconds(1_000_000 + seconds);

    private static string Slots(string fixture, params (string From, string To)[] edits)
    {
        var text = Fixtures.Text(fixture);
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, text);
            text = text.Replace(from, to);
        }
        return text;
    }

    private static List<LlamaSlotDto?> Rows(string json) => Json.Deserialize(json, Json.LlamaSlotsType)!;

    // MARK: llama.cpp

    private const string MixedFrom = "\"n_decoded\": 378", ReadFrom = "\"n_prompt_tokens_processed\": 5120";

    private static (EngineSnapshot First, EngineSnapshot Later) LlamaTwoPolls(LlamaCppMetrics? metrics)
    {
        var now = At(0);
        var provider = new LlamaCppProvider(new EngineConfig(), new FakeSource(), clock: () => now);
        var first = provider.Translate(Rows(Slots("llamacpp-parallel-mixed.slots.json")), null, metrics, "m");
        now = At(2);
        var later = provider.Translate(Rows(Slots("llamacpp-parallel-mixed.slots.json", (MixedFrom, "\"n_decoded\": 478"), (ReadFrom, "\"n_prompt_tokens_processed\": 6120"))), null, metrics, "m");
        return (first, later);
    }

    [Fact]
    public void LlamaCppGivesEveryDecodingSlotItsOwnSpeedFromTheGeneratedCountBetweenPolls()
    {
        var (first, later) = LlamaTwoPolls(null);
        Assert.All(first.Slots!, s => Assert.Null(s.TokensPerSecond));          // one poll: no speed yet
        Assert.Equal(50, later.Slots![0].TokensPerSecond);                      // (478 - 378) / 2 s
        Assert.Null(later.Slots[1].TokensPerSecond);                            // a reading slot has no decode speed
        Assert.Equal(50, later.Live!.DecodeTokensPerSecond);                    // no gauge: the sum of the slot speeds

        var bars = RequestBars.From(later);
        Assert.Equal(2, bars.Count);
        Assert.False(bars[0].Reading);
        Assert.Equal(50, bars[0].TokensPerSecond);
        Assert.True(bars[1].Reading);
    }

    [Fact]
    public void LlamaCppsOwnGaugeStaysTheHeadlineAndTheSlotsOnlyCarryTheirOwnRows()
    {
        var (_, later) = LlamaTwoPolls(LlamaCppMetrics.Parse("llamacpp:predicted_tokens_seconds 236.42\n"));
        Assert.Equal(236.42, later.Live!.DecodeTokensPerSecond);
        Assert.Equal(50, later.Slots![0].TokensPerSecond);
    }

    [Fact]
    public void LlamaCppHistoryCurvesTakeTheirRatesFromTheSlots()
    {
        var (first, later) = LlamaTwoPolls(null);
        var h = new SpeedHistory();
        h.Add(At(0), first);
        h.Add(At(2), later);
        Assert.Equal(50, h.Latest(HistoryCurve.Decode), 9);
        Assert.Equal(500, h.Latest(HistoryCurve.Prefill), 9);                   // the reading slot computed 1000 prompt tokens in 2 s
        Assert.Equal(550, h.Latest(HistoryCurve.Total), 9);
        Assert.Null(later.Totals);                                              // no counter of /metrics advances during a request: none is mapped
    }

    [Fact]
    public void ALlamaCppWithOneSlotTakesItsSpeedsFromTheSlotWhenTheGaugeSaysNothing()
    {
        var now = At(0);
        var provider = new LlamaCppProvider(new EngineConfig(), new FakeSource(), clock: () => now);
        var zero = LlamaCppMetrics.Parse("llamacpp:predicted_tokens_seconds 0\nllamacpp:prompt_tokens_seconds 0\n");
        provider.Translate(Rows(Fixtures.Text("llamacpp-prefill.slots.json")), null, zero, "m");
        now = At(2);
        var reading = provider.Translate(Rows(Slots("llamacpp-prefill.slots.json", ("\"n_prompt_tokens_processed\":26377", "\"n_prompt_tokens_processed\":28377"))), null, zero, "m");
        Assert.Equal(1000, reading.Live!.PrefillTokensPerSecond);              // 2000 tokens in 2 s
        Assert.Null(reading.Slots);                                            // one slot has nothing to tell apart

        var decode = new LlamaCppProvider(new EngineConfig(), new FakeSource(), clock: () => now);
        decode.Translate(Rows(Fixtures.Text("llamacpp-decode.slots.json")), null, zero, "m");
        now = At(4);
        var writing = decode.Translate(Rows(Slots("llamacpp-decode.slots.json", ("\"n_decoded\":316", "\"n_decoded\":416"))), null, zero, "m");
        Assert.Equal(50, writing.Live!.DecodeTokensPerSecond);
    }

    // MARK: Strata

    [Fact]
    public void StrataSlotsFeedTheBarsAndTheCurvesWhileItsTotalsDoNotAdvanceDuringARequest()
    {
        var now = At(0);
        var provider = new StrataProvider(new EngineConfig(), new FakeSource(), () => DateTime.UnixEpoch.AddSeconds(1_000_000) + (now - At(0)));
        var first = provider.Translate(Fixtures.Metrics("strata-parallel-mixed.json"), "m");
        now = At(2);
        var later = provider.Translate(Fixtures.Metrics("strata-parallel-mixed-later.json"), "m");
        var bars = RequestBars.From(later);
        Assert.Equal(3, bars.Count);                                           // one decoding, two reading
        Assert.Equal(1, bars.Count(b => !b.Reading));
        Assert.Equal((756.0 - 520) / 2, bars.Single(b => !b.Reading).TokensPerSecond!.Value, 6);
        Assert.Null(later.Totals?.PrefillComputedTokens);

        var h = new SpeedHistory();
        h.Add(At(0), first);
        h.Add(At(2), later);
        Assert.Equal((756.0 - 520) / 2, h.Latest(HistoryCurve.Decode), 6);
    }

    // MARK: Basalt

    [Fact]
    public void BasaltSlotsFeedTheBarsWithTheEnginesOwnSpeedPerSlot()
    {
        var m = Json.Deserialize(Fixtures.Text("basalt-0.3.2-parallel-mixed.json"), Json.BasaltMetricsType)!;
        var rows = Json.Deserialize(Fixtures.Text("basalt-0.3.2-parallel-mixed.slots.json"), Json.BasaltSlotsType)!;
        var s = new BasaltProvider(new EngineConfig(), new FakeSource()).Translate(m, rows, "m");
        var bars = RequestBars.From(s);
        Assert.Equal(3, bars.Count);                                           // two decoding, one reading; the idle slot is no bar
        Assert.Equal(new double?[] { 77.0, 21.6 }, bars.Where(b => !b.Reading).Select(b => b.TokensPerSecond).ToArray());
        Assert.Single(bars, b => b.Reading);
        Assert.Null(s.Totals);                                                 // Basalt has no counter: the curves use the slots, then the live speeds

        var h = new SpeedHistory();
        h.Add(At(0), s);
        Assert.Equal(77.0 + 21.6, h.Latest(HistoryCurve.Decode), 9);
    }
}
