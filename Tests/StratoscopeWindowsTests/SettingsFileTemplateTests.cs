using System.IO;
using Stratoscope.Core;

namespace Stratoscope.Tests;

/// <summary>The file "Open settings file" creates, and what the app says about a setting it does not know.</summary>
public class SettingsFileTemplateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "stratoscope-template-" + Guid.NewGuid().ToString("N"));

    public SettingsFileTemplateTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Write(string json)
    {
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static readonly IDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    [Fact]
    public void TheTemplateChangesNothingAndKeepsTheServerSearchOn()
    {
        var config = Config.Load(NoEnvironment, Write(Config.Template));
        Assert.Empty(config.Problems);
        Assert.False(config.Configured);
        Assert.Equal(ThemeChoice.System, config.Theme);
    }

    [Theory]
    [InlineData("url", "\"url\": \"http://127.0.0.1:9\"")]
    [InlineData("router", "\"router\": \"http://127.0.0.1:18080\"")]
    [InlineData("theme", "\"theme\": \"dark\"")]
    [InlineData("ui_scale", "\"ui_scale\": 125")]
    [InlineData("readout_position", "\"readout_position\": \"left\"")]
    [InlineData("compact_at", "\"compact_at\": 60000")]
    public void EveryExampleInTheTemplateIsASettingTheAppKnows(string key, string line)
    {
        Assert.Contains("// " + line.Split(':')[0] + ":", Config.Template);      // it is in the template, commented
        var config = Config.Load(NoEnvironment, Write("{ " + line + " }"));
        Assert.Empty(config.Problems);
    }

    [Fact]
    public void ASettingThatDoesNotExistIsNamed()
    {
        var config = Config.Load(NoEnvironment, Write("{ \"display\": \"both\", \"theme\": \"dark\" }"));
        Assert.Contains(config.Problems, p => p.Contains("display"));
        Assert.DoesNotContain(config.Problems, p => p.Contains("theme"));
        Assert.Equal(ThemeChoice.Dark, config.Theme);
    }

    [Fact]
    public void TheStarterIsWrittenOnceAndAnExistingFileIsNeverTouched()
    {
        var path = Path.Combine(dir, "sub", "config.json");
        Config.WriteStarterIfMissing(path);
        Assert.Equal(Config.Template, File.ReadAllText(path));

        File.WriteAllText(path, "{ \"url\": \"http://127.0.0.1:9\" }");
        Config.WriteStarterIfMissing(path);
        Assert.Equal("{ \"url\": \"http://127.0.0.1:9\" }", File.ReadAllText(path));
    }

    [Fact]
    public void ChoosingTheThemeSwitchesOnItsExampleAndKeepsEveryComment()
    {
        var path = Path.Combine(dir, "new.json");
        Assert.True(ConfigFile.SetValue(path, "theme", "dark"));
        var text = File.ReadAllText(path);
        Assert.Contains("// Stratoscope settings", text);
        Assert.Contains("  \"theme\": \"dark\",", text);
        Assert.DoesNotContain("// \"theme\"", text);
        Assert.Contains("// \"url\"", text);
        var config = Config.Load(NoEnvironment, path);
        Assert.Equal(ThemeChoice.Dark, config.Theme);
        Assert.Empty(config.Problems);
        Assert.False(config.Configured);

        Assert.True(ConfigFile.SetValue(path, "theme", "light"));      // the active line gets the new value
        Assert.Contains("  \"theme\": \"light\",", File.ReadAllText(path));
        Assert.Equal(ThemeChoice.Light, Config.Load(NoEnvironment, path).Theme);
    }

    [Fact]
    public void WithoutAnExampleALineIsAddedAtTheTopAndTheCommentsStay()
    {
        var path = Write("{ // my own note\n  \"url\": \"http://127.0.0.1:9\" // and another\n}");
        Assert.True(ConfigFile.SetValue(path, "theme", "dark"));
        var text = File.ReadAllText(path);
        Assert.Contains("// my own note", text);
        Assert.Contains("// and another", text);
        var config = Config.Load(NoEnvironment, path);
        Assert.Equal(ThemeChoice.Dark, config.Theme);
        Assert.Equal("http://127.0.0.1:9", config.Servers[0].Url);
    }

    [Fact]
    public void EnvironmentVariablesAreNotReportedAsUnknownSettings()
    {
        var env = new Dictionary<string, string?> { ["STRATOSCOPE_TRACE_MOVES"] = "1", ["STRATOSCOPE_CONFIG"] = "x" };
        Assert.Empty(Config.Load(env, Write("{}")).Problems);
    }
}
