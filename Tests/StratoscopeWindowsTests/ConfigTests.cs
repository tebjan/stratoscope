using System.IO;

namespace Stratoscope.Tests;

public class ConfigTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "stratoscope-test-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string File_(string? json)
    {
        var path = Path.Combine(dir, "config.json");
        if (json != null) File.WriteAllText(path, json);
        return path;
    }

    private static IDictionary<string, string?> Env(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void TheDefaultsTheFileTheEnvironmentAndABrokenFile()
    {
        MissingFileGivesTheDefaultsWithoutAProblem();
        File.Delete(Path.Combine(dir, "config.json"));
        EnvironmentBeatsFile();
        ServersListWatchesSeveralEngines();
        BrokenFileDoesNotCrashAndIsReported();
    }

    private void MissingFileGivesTheDefaultsWithoutAProblem()
    {
        var c = Config.Load(Env(), File_(null));
        Assert.Empty(c.Problems);
        var s = Assert.Single(c.Servers);
        Assert.Equal("strata", s.Engine);
        Assert.Equal(Config.DefaultUrl, s.Url);
        Assert.Null(s.Router);
        Assert.Null(c.CompactAt);
        Assert.Equal(ReadoutPosition.Center, c.ReadoutPosition);
        Assert.Null(c.UiScalePercent);
    }

    private void BrokenFileDoesNotCrashAndIsReported()
    {
        var c = Config.Load(Env(), File_("{ this is not json"));
        Assert.Single(c.Servers);
        Assert.Contains(c.Problems, p => p.Contains("not a JSON object"));
    }

    private void EnvironmentBeatsFile()
    {
        var path = File_(@"{""url"":""http://file:1"",""webui"":""http://ui:2"",""compact_at"":1000}");
        var c = Config.Load(Env(("STRATOSCOPE_URL", "http://env:3")), path);
        var s = Assert.Single(c.Servers);
        Assert.Equal("http://env:3", s.Url);
        Assert.Equal("http://ui:2", s.Webui);
        Assert.Equal(1000, c.CompactAt);
    }

    private void ServersListWatchesSeveralEngines()
    {
        var path = File_(@"{""log"":""C:\\logs\\strata.log"",""servers"":[
            {""name"":""Workstation"",""router"":""http://127.0.0.1:9000/""},
            {""name"":""Mac"",""url"":""http://127.0.0.1:9001"",""log"":""other.log""},
            {""router"":""http://10.0.0.5:9000""},
            {""engine"":""demo""}]}");
        var c = Config.Load(Env(), path);
        Assert.Equal(new[] { "Workstation", "Mac", "10.0.0.5:9000", "Demo" }, c.Servers.Select(s => s.Name).ToArray());
        Assert.Equal("http://127.0.0.1:9000", c.Servers[0].Router);   // trailing slash removed
        Assert.Equal("C:\\logs\\strata.log", c.Servers[0].Log);        // top-level default
        Assert.Equal("other.log", c.Servers[1].Log);
        Assert.Equal("demo", c.Servers[3].Engine);
    }

    }
