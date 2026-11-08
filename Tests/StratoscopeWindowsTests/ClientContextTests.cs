using System.IO;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Tests;

/// <summary>The summarise marker from the config of a client that talks to the engine, read from fake files in a temporary folder.</summary>
public class ClientConfigTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "stratoscope-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(home, true); } catch (IOException) { } }

    // Comments and a trailing comma, as people write these files; the key is a fake value that must never be read.
    private const string Kilo = @"{
      // local model through the router
      ""provider"": { ""openai-compatible"": {
        ""options"": { ""apiKey"": ""FAKE-KEY"", ""baseURL"": ""http://localhost:8080/v1"", },
        ""models"": { ""m1"": { ""limit"": { ""context"": 90112, ""output"": 24576 } },   /* main */
                    ""m2"": { ""limit"": { ""context"": 32768, ""output"": 8192 } } } } } }";
    private const string OpenCode = @"{ ""providers"": { ""swap"": { ""settings"": { ""baseURL"": ""http://127.0.0.1:8080/v1"" },
      ""models"": { ""x"": { ""modelID"": ""m1"", ""limit"": { ""context"": 90112, ""output"": 24576 } } } } } }";

    private void Write(string root, string client, string file, string text)
    {
        Directory.CreateDirectory(Path.Combine(root, client));
        File.WriteAllText(Path.Combine(root, client, file), text);
    }

    private static EngineSnapshot Snapshot(EngineConfig config, ClientConfigReader? reader, string model)
    {
        var metrics = Json.Deserialize(@"{""engine"": {""max_context"": 98304}, ""live"": {""state"": ""idle""}}", Json.MetricsType)!;
        return new StrataProvider(config, new FakeSource(), clients: reader).Translate(metrics, model);
    }

    [Fact]
    public void TheMarkerComesFromTheClientConfigThatPointsAtTheSameServer()
    {
        Write(Path.Combine(home, ".config"), "kilo", "kilo.jsonc", Kilo);
        var reader = new ClientConfigReader(home);
        var config = new EngineConfig { Router = "http://127.0.0.1:8080" };

        // Both layouts, comments and trailing commas; the same address in another spelling matches; context minus output.
        Assert.Equal(2, reader.Limits().Count);
        Assert.Equal(65_536, Snapshot(config, reader, "m1").Context.CompactAt);
        Assert.Null(Snapshot(config, reader, "other").Context.CompactAt);                       // another model
        Assert.Null(Snapshot(new EngineConfig { Router = "http://127.0.0.1:9999", Url = "http://127.0.0.1:9998" }, reader, "m1").Context.CompactAt);   // another server

        // compact_at is the fallback; what a client says wins; the reading can be switched off; client_model names the id the client uses.
        config.CompactAt = 50_000;
        Assert.Equal(65_536, Snapshot(config, reader, "m1").Context.CompactAt);
        Assert.Equal(50_000, Snapshot(config, reader, "other").Context.CompactAt);
        Assert.Equal(50_000, Snapshot(config, null, "m1").Context.CompactAt);
        config.CompactAt = null;
        Assert.Null(Snapshot(config, null, "m1").Context.CompactAt);                            // nobody knows: no marker
        config.ClientModel = "m1";
        Assert.Equal(65_536, Snapshot(config, reader, "engine-own-name").Context.CompactAt);

        // Two clients that agree count once, two that disagree give nothing; broken files and an output larger than the context give nothing.
        Write(Path.Combine(home, ".config"), "opencode", "opencode.json", OpenCode);
        Assert.Equal(65_536, new ClientConfigReader(home).CompactAt(new[] { "http://127.0.0.1:8080" }, "m1"));
        var a = new ClientModelLimit("A", "http://127.0.0.1:8080/v1", "m", 100_000, 20_000);
        var b = a with { Client = "B", Output = 30_000 };
        Assert.Null(ClientLimits.CompactAt(new[] { a, b }, new[] { "http://127.0.0.1:8080" }, "m"));
        Assert.Null(ClientLimits.CompactAt(new[] { a with { Output = 120_000 } }, new[] { "http://127.0.0.1:8080" }, "m"));
        Assert.Empty(ClientLimits.Parse("{ not json", "Kilo"));

        // XDG_CONFIG_HOME replaces the .config folder; client_configs: none and client_model are read from the environment and the file.
        var elsewhere = Path.Combine(home, "elsewhere");
        Write(elsewhere, "kilo", "kilo.json", Kilo);
        Assert.Single(new ClientConfigReader(Path.Combine(home, "nowhere"), elsewhere).Limits(), l => l.Model == "m1");
        Assert.Empty(new ClientConfigReader(Path.Combine(home, "nowhere")).Limits());
        var settings = Path.Combine(home, "config.json");
        File.WriteAllText(settings, @"{ ""client_configs"": ""none"", ""servers"": [ { ""url"": ""http://127.0.0.1:8095"", ""client_model"": ""m1"" } ] }");
        var loaded = Config.Load(new Dictionary<string, string?>(), settings);
        Assert.False(loaded.Servers[0].ReadClientConfigs);
        Assert.Equal("m1", loaded.Servers[0].ClientModel);
        Assert.True(Config.Load(new Dictionary<string, string?>(), Path.Combine(home, "missing.json")).Servers[0].ReadClientConfigs);
        Assert.Equal("x", Config.Load(new Dictionary<string, string?> { ["STRATOSCOPE_CLIENT_MODEL"] = "x" }, Path.Combine(home, "missing.json")).Servers[0].ClientModel);
    }
}

public class ContextNumbersTests
{
    [Fact]
    public void FreeAndTheContextAfterARequestAreOnlyKnownFromKnownNumbers()
    {
        Assert.Equal(40_464, new ContextUsage { Used = 57_840, Max = 98_304 }.Free);
        Assert.Null(new ContextUsage { Used = null, Max = 98_304 }.Free);
        Assert.Null(new ContextUsage { Used = 1_000, Max = null }.Free);
        Assert.Null(new ContextUsage { Used = 1_000, Max = 0 }.Free);                      // 0 = no limit reported
        Assert.Equal(0, new ContextUsage { Used = 100, Max = 50 }.Free);                    // never negative

        Assert.Equal(42_394, new RequestStats { PromptTokens = 42_385, OutputTokens = 9 }.ContextTokens);
        Assert.Null(new RequestStats { PromptTokens = 42_385 }.ContextTokens);              // an unknown part is not 0
        Assert.Null(new RequestStats { OutputTokens = 9 }.ContextTokens);
    }
}

public class ParkedConversationTests
{
    [Fact]
    public void AParkedConversationWithoutDetailsIsNotAFreeSlot()
    {
        var cache = new CacheStats { Slots = 4, Parked = 3, Entries = new List<CacheEntry> { new CacheEntry(7, 100, 1) } };
        Assert.False(cache.IsParkedWithoutDetails(0));      // known
        Assert.True(cache.IsParkedWithoutDetails(1));       // parked, no details
        Assert.True(cache.IsParkedWithoutDetails(2));
        Assert.False(cache.IsParkedWithoutDetails(3));      // really free
        Assert.False(new CacheStats { Slots = 4 }.IsParkedWithoutDetails(0));               // the engine did not say how many are parked
    }
}
