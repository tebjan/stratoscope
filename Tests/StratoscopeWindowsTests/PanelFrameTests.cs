using System.Drawing;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

/// <summary>
/// The panel never changes its structure with what the engine does at the moment, and a change is one composed frame: one size change at
/// most, one paint, no background erase, nothing at all for the same data. The offscreen panel runs the same steps as the window.
/// </summary>
public class PanelFrameTests
{
    private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static PanelModel Model(Stratoscope.Core.EngineSnapshot snapshot)
    {
        var model = Painters.Model(snapshot);
        model.Now = Now;
        return model;
    }

    [Fact]
    public void RequestsComingAndGoingChangeOnlyTheLiveBlockAndEveryChangeIsOneResizeAndOneFrame()
    {
        using var panel = new OffscreenPanel();
        string? below = null;
        var updates = new List<PanelUpdate>();
        var sequence = ImageSet.Sequence();      // idle, 1 running, 3 running, 2 running, idle
        foreach (var (label, snapshot) in sequence)
        {
            updates.Add(panel.Show(label, Model(snapshot)));
            below ??= ImageSet.BelowLive(panel);
            Assert.Equal(below, ImageSet.BelowLive(panel));      // everything below the live block keeps its distance from the anchored bottom edge
        }
        Assert.All(updates, u => { Assert.True(u.PositionChanges <= 1); Assert.Equal(1, u.Paints); Assert.Equal(0, u.Erases); });      // size and position in one call, one frame, no erase
        Assert.Equal(new[] { true, false, true, true, true }, updates.Select(u => u.SizeChanged).ToArray());      // one request is the single bar: no change of height
        Assert.All(updates.Where(u => u.SizeChanged), u => Assert.Equal(1, u.PositionChanges));

        var last = sequence[^1].Snapshot;
        var again = panel.Show("same data", Model(last));     // a poll with the same numbers
        Assert.Equal(0, again.Paints); Assert.Equal(0, again.PositionChanges); Assert.Equal(0, again.Erases);
    }

    [Fact]
    public void TheSteadyStateOfThePresenterAllocatesNothing()
    {
        var surface = new RecordingPanelSurface();
        using var presenter = new PanelPresenter(surface);
        var bounds = new Rectangle(10, 10, 352, 600);
        void Compose() { using var g = Graphics.FromImage(presenter.Target(bounds.Size)); g.Clear(Color.White); }
        Compose(); presenter.Update("first", bounds);          // paints
        Compose(); presenter.Update("same", bounds);           // the same pixels: nothing happens
        for (int i = 0; i < 50; i++) presenter.Update("warm up", bounds);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) presenter.Update("tick", bounds);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1, surface.Counters.Paints);
        Assert.Equal(1, surface.Counters.PositionChanges);
    }
}
