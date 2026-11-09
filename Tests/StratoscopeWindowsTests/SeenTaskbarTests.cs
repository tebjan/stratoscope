using System.Drawing;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

/// <summary>The readout's colours are checked against the taskbar as it really looks, not against the plain colour of its theme.</summary>
public class SeenTaskbarTests
{
    [Fact]
    public void ATintedLightTaskbarStillGivesTheGreyRingTextContrast()
    {
        var seen = Color.FromArgb(200, 200, 204);          // a light Windows 11 taskbar over a wallpaper: darker than #F3F3F3
        var plain = Palette.Taskbar(false);
        var real = Palette.Taskbar(false, false, seen);
        Assert.Equal(seen.ToArgb(), real.Background.ToArgb());
        Assert.True(Contrast.Ratio(real.Gray, seen) >= Contrast.LargeTextAndGraphics);
        Assert.True(Contrast.Ensure(real.Gray, real.Background, Contrast.SmallText) != real.Gray, "the ring is drawn darker than the plain grey");
        Assert.True(Contrast.Ratio(plain.Gray, seen) < Contrast.Ratio(real.Gray, seen) + 0.001);
    }

    [Fact]
    public void TheSeenColourIsPartOfWhatMakesTheLookChange()
    {
        var ui = Ui.Controller();
        var before = ui.ForDpi(96).Signature;
        ui.TaskbarSeen = Color.FromArgb(200, 200, 204);
        var after = ui.ForDpi(96);
        Assert.NotEqual(before, after.Signature);
        Assert.Equal(ui.TaskbarSeen!.Value.ToArgb(), after.Taskbar.Background.ToArgb());
    }

    [Fact]
    public void HighContrastIgnoresTheSeenColour()
    {
        var palette = Palette.Taskbar(false, true, Color.FromArgb(200, 200, 204));
        Assert.True(palette.HighContrast);
    }
}
