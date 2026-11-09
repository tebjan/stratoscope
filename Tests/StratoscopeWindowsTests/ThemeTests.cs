using System.IO;
using Stratoscope.Core;

namespace Stratoscope.Tests;

/// <summary>The panel's theme choice: system (default), light or dark; the readout stays with the taskbar.</summary>
public class ThemeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "stratoscope-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Write(string json)
    {
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static readonly IDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    [Theory]
    [InlineData(null, ThemeChoice.System)]
    [InlineData("system", ThemeChoice.System)]
    [InlineData("auto", ThemeChoice.System)]
    [InlineData("light", ThemeChoice.Light)]
    [InlineData("Dark", ThemeChoice.Dark)]
    public void TheSettingIsReadAndSystemIsTheDefault(string? value, ThemeChoice expected)
    {
        var config = Config.Load(NoEnvironment, Write(value == null ? "{}" : "{ \"theme\": \"" + value + "\" }"));
        Assert.Equal(expected, config.Theme);
        Assert.Empty(config.Problems);
    }

    [Fact]
    public void AnUnknownThemeIsNamedAsAProblemAndLeavesTheDefault()
    {
        var config = Config.Load(NoEnvironment, Write("{ \"theme\": \"blue\" }"));
        Assert.Equal(ThemeChoice.System, config.Theme);
        Assert.Contains(config.Problems, p => p.Contains("theme") && p.Contains("blue"));
    }

    [Fact]
    public void TheEnvironmentWinsOverTheFile()
    {
        var env = new Dictionary<string, string?> { ["STRATOSCOPE_THEME"] = "light" };
        Assert.Equal(ThemeChoice.Light, Config.Load(env, Write("{ \"theme\": \"dark\" }")).Theme);
    }

    [Fact]
    public void AFileWithOnlyAThemeStillLetsTheAppLookForTheServer()
    {
        Assert.False(Config.Load(NoEnvironment, Write("{ \"theme\": \"dark\" }")).Configured);
        Assert.True(Config.Load(NoEnvironment, Write("{ \"theme\": \"dark\", \"url\": \"http://127.0.0.1:9\" }")).Configured);
    }

    [Fact]
    public void SettingOneKeyKeepsTheRestAndCreatesAMissingFile()
    {
        var path = Write("{ // a comment\n \"url\": \"http://127.0.0.1:9\", \"compact_at\": 60000, }");
        Assert.True(ConfigFile.SetValue(path, "theme", "dark"));
        var config = Config.Load(NoEnvironment, path);
        Assert.Equal(ThemeChoice.Dark, config.Theme);
        Assert.Equal("http://127.0.0.1:9", config.Servers[0].Url);
        Assert.Equal(60000, config.CompactAt);

        var created = Path.Combine(dir, "new", "config.json");
        Assert.True(ConfigFile.SetValue(created, "theme", "light"));
        Assert.Equal(ThemeChoice.Light, Config.Load(NoEnvironment, created).Theme);
    }

    [Fact]
    public void AFileThatIsNotAnObjectIsLeftAlone()
    {
        var path = Write("[1, 2]");
        Assert.False(ConfigFile.SetValue(path, "theme", "dark"));
        Assert.Equal("[1, 2]", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(ThemeChoice.System, true, true, true, true)]
    [InlineData(ThemeChoice.System, false, false, false, false)]
    [InlineData(ThemeChoice.Light, true, true, false, true)]
    [InlineData(ThemeChoice.Dark, false, false, true, false)]
    public void OnlyThePanelFollowsTheChoiceTheReadoutKeepsTheTaskbar(ThemeChoice choice, bool windowsApps, bool windowsTaskbar, bool panelDark, bool readoutDark)
    {
        var ui = Ui.Controller(s: new FakeSettings { AppsDark = windowsApps, SystemDark = windowsTaskbar }, theme: choice);
        var metrics = ui.ForDpi(96);
        Assert.Equal(panelDark, metrics.AppsDark);
        Assert.Equal(readoutDark, metrics.SystemDark);
    }
}
