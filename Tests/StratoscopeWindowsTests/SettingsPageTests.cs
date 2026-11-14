using System.Drawing;
using Stratoscope.Core;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

/// <summary>The settings page of the panel: the same rows and colours as the rest of the panel, not a system menu.</summary>
public class SettingsPageTests
{
    private static PanelPainter Paint(PanelSettings settings, out float height)
    {
        var model = new PanelModel { Settings = settings };
        using var scratch = new Bitmap(1, 1);
        using var g = Graphics.FromImage(scratch);
        var painter = new PanelPainter(g, Palette.For(true), model);
        height = painter.Paint();
        return painter;
    }

    [Fact]
    public void TheRowsAreBackStartupThreeThemesFourSmoothingsTheFileAndReload()
    {
        var page = Paint(new PanelSettings { StartWithWindows = true, Theme = ThemeChoice.Dark }, out var height);
        Assert.Equal(
            new[] { PanelAction.SettingsBack, PanelAction.ToggleStartup, PanelAction.SetTheme, PanelAction.SetTheme, PanelAction.SetTheme, PanelAction.SetSmoothing, PanelAction.SetSmoothing, PanelAction.SetSmoothing, PanelAction.SetSmoothing, PanelAction.OpenSettingsFile, PanelAction.ReloadSettings },
            page.Hits.Select(h => h.Action).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, page.Hits.Where(h => h.Action == PanelAction.SetTheme).Select(h => h.Index).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, page.Hits.Where(h => h.Action == PanelAction.SetSmoothing).Select(h => h.Index).ToArray());
        Assert.True(height > 100);
    }

    [Fact]
    public void ProblemsInTheSettingsFileAreShownOnThePage()
    {
        var settings = new PanelSettings();
        settings.Problems.Add("Ignored theme \"blue\": use system, light or dark.");
        var withProblem = Paint(settings, out var tall);
        Paint(new PanelSettings(), out var short_);
        Assert.True(tall > short_);
        Assert.Contains(withProblem.Texts, t => t.Contains("blue"));
    }

    [Fact]
    public void TheStatusPageHasTheSettingsAndUpdateRows()
    {
        using var scratch = new Bitmap(1, 1);
        using var g = Graphics.FromImage(scratch);
        var painter = new PanelPainter(g, Palette.For(false), new PanelModel());
        painter.Paint();
        var actions = painter.Hits.Select(h => h.Action).ToList();
        Assert.Contains(PanelAction.Settings, actions);
        Assert.Contains(PanelAction.CheckForUpdates, actions);
    }
}
