using System.IO;

namespace Stratoscope.Tests;

public class ServerDetectionTests
{
    private const string Router = "http://127.0.0.1:18080";
    private const string Engine = "http://127.0.0.1:8095";

    private static FakeSource Source() => new FakeSource()
        .Text(Router + "/running", @"{""running"": []}")
        .Json(Engine + "/metrics", "strata-0.1.56-idle.json");

    [Fact]
    public async Task ARouterIsTheServerToWatchAndTheEngineBehindItIsLeftOut()
    {
        var found = await ServerDetection.FindAsync(Source(), null, CancellationToken.None);
        var server = Assert.Single(found);
        Assert.Equal(Router, server.Router);
        Assert.Null(server.Url);
    }

    [Fact]
    public async Task AnEngineWithoutARouterIsFoundByItsMetrics()
    {
        var source = new FakeSource().Json(Engine + "/metrics", "strata-0.1.56-idle.json");
        var found = await ServerDetection.FindAsync(source, null, CancellationToken.None);
        var server = Assert.Single(found);
        Assert.Equal(Engine, server.Url);
        Assert.Null(server.Router);
    }

    [Fact]
    public async Task NothingIsFoundWhenNothingAnswersAndNothingFarAwayIsAsked()
    {
        var source = new FakeSource();
        Assert.Empty(await ServerDetection.FindAsync(source, null, CancellationToken.None));
        Assert.All(source.Requested, url => Assert.StartsWith("http://127.0.0.1:", url));
    }

    [Theory]
    [InlineData("http://localhost:18080/v1", true)]
    [InlineData("http://127.0.0.1:8095", true)]
    [InlineData("http://192.168.1.20:9000", true)]
    [InlineData("http://10.0.0.5:9000", true)]
    [InlineData("http://172.20.0.2:80", true)]
    [InlineData("http://100.101.102.103:8080", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://172.32.0.1:80", false)]
    [InlineData("http://8.8.8.8", false)]
    public void OnlyThisComputerAndThePrivateNetworkAreAsked(string address, bool local) =>
        Assert.Equal(local, ServerDetection.IsLocalNetwork(new Uri(address)));

    [Fact]
    public void ClientAddressesComeFirstWithoutTheirPathAndOnlyOnce()
    {
        var home = Path.Combine(Path.GetTempPath(), "stratoscope-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".config", "kilo"));
        try
        {
            File.WriteAllText(Path.Combine(home, ".config", "kilo", "kilo.jsonc"),
                @"{""provider"": {""local"": {""options"": {""baseURL"": ""http://127.0.0.1:18080/v1""}, ""models"": {""qwen"": {""limit"": {""context"": 131072, ""output"": 8192}}}},
                                 ""cloud"": {""options"": {""baseURL"": ""https://api.openai.com/v1""}, ""models"": {""gpt"": {""limit"": {""context"": 1, ""output"": 1}}}}}}");
            var candidates = ServerDetection.Candidates(new ClientConfigReader(home));
            Assert.Equal(Router, candidates[0]);
            Assert.DoesNotContain(candidates, c => c.Contains("openai"));
            Assert.Equal(candidates.Count, candidates.Distinct().Count());
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void FoundServersReplaceTheDefaultOneWithTheNormalNames()
    {
        var config = Config.Load(new Dictionary<string, string?>(), Path.Combine(Path.GetTempPath(), "no-such-settings-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.False(config.Configured);
        config.UseDetected(new[] { new EngineConfig { Router = Router } });
        var server = Assert.Single(config.Servers);
        Assert.Equal(Router, server.Router);
        Assert.Equal("127.0.0.1:18080", server.Name);
    }
}
