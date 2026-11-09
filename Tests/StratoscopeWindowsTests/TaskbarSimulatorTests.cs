using System.Drawing;
using Stratoscope.Core;
using Stratoscope.Windows;

namespace Stratoscope.Tests;

/// <summary>
/// A taskbar simulator: replays sequences of changes (notification icons, task buttons, clock width, alignment, DPI, auto-hide, content) and
/// checks what the readout would do. The placer sees the geometry every 500 ms, like the app; the clock is simulated.
/// </summary>
[Collection("ReadoutPainterState")]
public class TaskbarSimulatorTests
{
    private sealed class Sim
    {
        public const int Width = 2560, Height = 48, Step = 500;
        public DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        public readonly ReadoutPlacer Placer = new ReadoutPlacer();
        public int Buttons = 8, NotificationWidth = 300, Dpi = 96;
        public bool CentredButtons = true, Hidden;
        public Size Desired = new Size(330, 40);
        public readonly List<Rectangle> Applied = new List<Rectangle>();
        public int Violations;
        public int Steps;

        public TaskbarLayout.Geometry Geometry()
        {
            var g = new TaskbarLayout.Geometry { Bounds = new Rectangle(0, 0, Width, Height), Notification = new Rectangle(Width - NotificationWidth, 0, NotificationWidth, Height), Dpi = Dpi };
            var cluster = Buttons * 48;
            var x0 = CentredButtons ? (Width - cluster) / 2 : 12;
            for (int i = 0; i < Buttons; i++) g.Buttons.Add(new Rectangle(x0 + i * 48, 4, 44, 40));
            return g;
        }

        /// <summary>The free gap between the last button and the notification area, in pixels (with the 8 px margin the layout keeps).</summary>
        public (int Left, int Right) Gap()
        {
            var g = Geometry();
            return (g.Buttons.Max(b => b.Right) + 8, g.Notification.Left - 8);
        }

        public PlaceAction Tick(bool immediate = false)
        {
            Now = Now.AddMilliseconds(Step); Steps++;
            Rectangle? target = Hidden ? null : TaskbarLayout.Place(Geometry(), ReadoutPosition.Center, Desired);
            var action = Placer.Step(Now, target, immediate);
            if (action.Kind == PlaceKind.Show || action.Kind == PlaceKind.Move) { Applied.Add(action.Rect); CheckPlacement(action.Rect); }
            return action;
        }

        public void Run(double seconds) { for (var i = 0; i < seconds * 1000 / Step; i++) Tick(); }

        private void CheckPlacement(Rectangle r)
        {
            var g = Geometry();
            if (g.Buttons.Any(b => b.IntersectsWith(r)) || g.Notification.IntersectsWith(r)) Violations++;
        }

        public void AssertCentredInGap(int tolerance = 1)
        {
            var r = Placer.Current!.Value; var (left, right) = Gap();
            Assert.InRange(Math.Abs((r.Left + r.Right) / 2.0 - (left + right) / 2.0), 0, tolerance);
            Assert.InRange(Math.Abs((r.Top + r.Bottom) / 2.0 - Height / 2.0), 0, tolerance);
        }
    }

    [Fact]
    public void ContentChangesAloneNeverMoveOrResizeAnything()
    {
        var sim = new Sim();
        sim.Tick();
        // Numbers, states, theme colours: none of it enters the geometry, so the target never changes.
        sim.Run(600);
        Assert.Equal(0, sim.Placer.Moves);
        Assert.Single(sim.Applied);
    }

    /// <summary>
    /// Acceptance: in every state the item has the same width, its glyph slot ends at the same x (where the bar ends and the dot sits) and its
    /// value starts at the same x. Nothing moves when the state changes; only the glyph's shape and colour and the speed inside the slot. With
    /// one server and with several, at every scale.
    /// </summary>
    [Fact]
    public void ItemGeometryIsTheSameInEveryStateAndForEveryValueAtEveryScale()
    {
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            SameWidthAndPositionsInEveryState(dpi);
            AnchorGridNeverMovesWithStateOrValue(dpi);
        }
        TheNumberNeverHasMoreThanFourCharacters();
    }

    private static void SameWidthAndPositionsInEveryState(int dpi)
    {
        var m = UiMetrics.At(dpi);
        foreach (var servers in new[] { 1, 2 })
        {
            var geometries = new List<ItemGeometry>(); var widths = new List<int>(); var rendered = new List<int>();
            foreach (var (_, items) in SnapshotRenderer.StateRows())
            {
                var set = servers == 1 ? items : items.Select(i => new ReadoutItem(i.Name, i.State, i.Text, i.Fraction, i.Spoken, i.Tip, true)).ToList();
                geometries.Add(ReadoutPainter.GeometryOf(set[0], m));
                widths.Add(ReadoutPainter.Measure(new[] { set[0] }, m).Width);
                var bounds = new List<Rectangle>();
                using (ReadoutPainter.Render(new[] { set[0] }, m, ReadoutPainter.Measure(new[] { set[0] }, m), bounds)) rendered.Add(bounds[0].Width);
            }
            Assert.Single(geometries.Distinct());      // glyph slot, glyph right edge, value x and the width: the same in all states
            Assert.Single(widths.Distinct());
            Assert.Single(rendered.Distinct());
            Assert.Equal(m.Px(ReadoutPainter.AnchorGap), geometries[0].Anchor - geometries[0].GlyphRight);      // the graphics end one gap before the anchor
            Assert.Equal(m.Px(ReadoutPainter.AnchorGap), geometries[0].TextX - geometries[0].Anchor);          // the text starts one gap after it
        }
    }

    /// <summary>
    /// The hard anchor grid: the anchor, the end of the graphics, the start of the text and the unit sit at the same x in every state and for
    /// every speed of up to four digits; the item has the same width, and equal margins left and right. Geometry only.
    /// </summary>
    private static void AnchorGridNeverMovesWithStateOrValue(int dpi)
    {
        var m = UiMetrics.At(dpi);
        var geometries = new List<ItemGeometry>();
        foreach (var (_, items) in SnapshotRenderer.StateRows())
            geometries.Add(ReadoutPainter.GeometryOf(items[0], m));
        foreach (var value in new[] { "9", "99", "999", "3345", "9999", "10k" })
            foreach (var state in new[] { ServerState.Reading, ServerState.Writing, ServerState.Thinking })
                geometries.Add(ReadoutPainter.GeometryOf(new ReadoutItem("Workstation", state, new ReadoutText(value, "t/s"), 0.5, "", null, false), m));
        Assert.Single(geometries.Distinct());                                                      // one table: the content never moves anything
        var geometry = geometries[0];
        Assert.Equal(m.Px(ReadoutPainter.PadX), geometry.GlyphX);                                  // the left margin
        Assert.Equal(geometry.GlyphX, geometry.Width - geometry.TextRight);                         // the right margin is the same
        Assert.True(geometry.UnitX > geometry.TextX && geometry.UnitX < geometry.TextRight);
        // Four places hold the number and nothing extends outside the slot: the first pixel of a full number keeps at least the gap to the glyph.
        foreach (var value in new[] { "9", "99", "999", "3345", "9999", "10k" })
            Assert.True(Measurements.NumberClearance(dpi, value) >= m.Px(ReadoutPainter.AnchorGap), value + " at " + dpi + " DPI");
        // The dot, the rings and the mark are in the same place in every state with or without a number: one glyph centre.
        var centres = new[] { ServerState.Ready, ServerState.Writing, ServerState.Thinking, ServerState.Offline, ServerState.NoModel, ServerState.Loading, ServerState.Error, ServerState.Connecting }
            .Select(s => ReadoutPainter.GlyphPosition(new ReadoutItem("S", s, s == ServerState.Writing ? new ReadoutText("26", "t/s") : ReadoutText.None, null, "", null, false), m)).Distinct().ToList();
        Assert.Single(centres);
    }

    [Fact]
    public void ADotAndARingEndWhereTheBarEnds()
    {
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            var m = UiMetrics.At(dpi, 1.0, true, true);
            int RightEdge(ServerState state)
            {
                var item = new ReadoutItem("Workstation", state, ReadoutText.None, 1.0, "", null, false);
                var size = ReadoutPainter.Measure(new[] { item }, m);
                var geometry = ReadoutPainter.GeometryOf(item, m);
                using var bmp = ReadoutPainter.Render(new[] { item }, m, size);
                var right = -1;
                for (int x = 0; x < geometry.Anchor; x++)
                    for (int y = 0; y < bmp.Height; y++)
                        if (bmp.GetPixel(x, y).A >= 128) right = Math.Max(right, x);
                return right;
            }
            var geometry = ReadoutPainter.GeometryOf(new ReadoutItem("Workstation", ServerState.Ready, ReadoutText.None, 1.0, "", null, false), m);
            var bar = RightEdge(ServerState.Reading);
            Assert.InRange(bar, geometry.GlyphRight - 2, geometry.GlyphRight - 1);          // the end of the glyph slot, the anchor minus the gap
            Assert.Equal(bar, RightEdge(ServerState.Writing));
            Assert.Equal(bar, RightEdge(ServerState.Ready));
            Assert.Equal(bar, RightEdge(ServerState.Offline));
            Assert.Equal(bar, RightEdge(ServerState.Loading));
            Assert.Equal(bar, RightEdge(ServerState.Error));
        }
    }

    private static void TheNumberNeverHasMoreThanFourCharacters()
    {
        Assert.Equal("26", Display.ReadoutNumber(26.4));
        Assert.Equal("3345", Display.ReadoutNumber(3344.6));       // no thousands separator
        Assert.Equal("9999", Display.ReadoutNumber(9999.4));
        Assert.Equal("10k", Display.ReadoutNumber(9999.5));
        Assert.Equal("12k", Display.ReadoutNumber(12_345));
        Assert.Equal("99k", Display.ReadoutNumber(1_000_000));
        Assert.Equal("10k", Display.Readout(new EngineSnapshot("e", "m", EngineStatus.Decode) { Live = new LiveRequest { DecodeTokensPerSecond = 10_000.4 } }).Value);
    }


    [Fact]
    public void AnIconThatAppearsAndDisappearsTenTimesGivesOneMovePerSettledChange()
    {
        var sim = new Sim();
        sim.Tick();
        for (int i = 0; i < 10; i++)
        {
            sim.NotificationWidth += 36; sim.Run(25);       // an icon appears and stays
            sim.NotificationWidth -= 36; sim.Run(25);       // and goes again
        }
        Assert.True(sim.Placer.Moves <= 20, "moves " + sim.Placer.Moves);
        Assert.Equal(0, sim.Violations);
        sim.AssertCentredInGap();
    }

    /// <summary>
    /// The sequence logged on the real taskbar (readout-moves.log, 20:52 to 21:02): the Windows privacy indicator (a microphone or location
    /// in use) appears at the left of the notification area, 48 px wide, for 4 to 15 s, every one to five minutes. Zero moves.
    /// </summary>
    [Fact]
    public void ThePrivacyIndicatorThatFlapsTheNotificationAreaForFewSecondsNeverMovesTheReadout()
    {
        var sim = new Sim { NotificationWidth = 404 };
        sim.Tick();
        // (seconds with the normal width, seconds with the indicator): from the log.
        foreach (var (rest, flap) in new[] { (127.0, 5.0), (78.0, 4.0), (292.0, 9.0), (2.0, 15.0), (38.0, 5.0), (25.0, 5.0), (60.0, 15.0) })
        {
            sim.Run(rest);
            sim.NotificationWidth = 452; sim.Run(flap);
            sim.NotificationWidth = 404;
        }
        sim.Run(30);
        Assert.Equal(0, sim.Placer.Moves);
        Assert.Single(sim.Applied);
    }

    [Fact]
    public void ButtonsGrowingFromThreeToTwentyAndBackStayCentredAndNeverOverlap()
    {
        var sim = new Sim { Buttons = 3 };
        sim.Tick();
        int changes = 0;
        for (int n = 4; n <= 20; n++) { sim.Buttons = n; changes++; sim.Run(25); sim.AssertCentredInGap(); }
        for (int n = 19; n >= 3; n--) { sim.Buttons = n; changes++; sim.Run(25); sim.AssertCentredInGap(); }
        Assert.Equal(0, sim.Violations);
        Assert.True(sim.Placer.Moves <= changes, "moves " + sim.Placer.Moves + " for " + changes + " changes");
    }

    [Fact]
    public void AutoHideHidesAndShowsTheReadoutWithoutMovingIt()
    {
        var sim = new Sim();
        sim.Tick();
        for (int i = 0; i < 10; i++) { sim.Hidden = true; sim.Run(2); sim.Hidden = false; sim.Run(2); }
        Assert.Equal(0, sim.Placer.Moves);
        Assert.Equal(10, sim.Placer.Hides);
        Assert.Equal(11, sim.Placer.Shows);
        Assert.Equal(sim.Applied[0], sim.Applied[^1]);
    }

    [Fact]
    public void ADpiChangeIsAppliedAtOnceAndOnce()
    {
        var sim = new Sim();
        sim.Tick();
        sim.Dpi = 144; sim.Desired = new Size(495, 60);
        var action = sim.Tick(immediate: true);
        Assert.Equal(PlaceKind.Move, action.Kind);
        sim.Run(10);
        Assert.Equal(1, sim.Placer.Moves);
        sim.AssertCentredInGap();
    }

    /// <summary>The hover fill is a soft 10 to 12 % tint (pressed 16 to 18 %) in both themes, measured on the rendered pixels; the contrast curve of the text must not touch it.</summary>
    [Fact]
    public void TheHoverFillIsASoftTintInBothThemes()
    {
        foreach (var dark in new[] { true, false })
        {
            var m = UiMetrics.At(96, 1.0, dark, dark);
            var item = new ReadoutItem("S", ServerState.Ready, ReadoutText.None, null, "", null, false);
            var size = ReadoutPainter.Measure(new[] { item }, m);
            var x = ReadoutPainter.GeometryOf(item, m).GlyphRight - 12;      // inside the fill, beside the dot
            int Alpha(int hover, int pressed) { using var bmp = ReadoutPainter.Render(new[] { item }, m, size, null, false, hover, pressed); return bmp.GetPixel(x, size.Height / 2).A; }
            Assert.Equal(0, Alpha(-1, -1));
            Assert.InRange(Alpha(0, -1), 0.10 * 255 - 1, 0.12 * 255 + 1);
            Assert.InRange(Alpha(-1, 0), 0.16 * 255 - 1, 0.18 * 255 + 1);
        }
    }

    [Fact]
    public void ThePlacerBoundsItsOwnWorkOverALongMixedSequence()
    {
        var sim = new Sim();
        var random = new Random(7);
        sim.Tick();
        var changes = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (random.Next(10) == 0) { sim.Buttons = Math.Clamp(sim.Buttons + random.Next(-2, 3), 3, 20); changes++; }
            if (random.Next(15) == 0) { sim.NotificationWidth = 260 + random.Next(0, 3) * 36; changes++; }
            sim.Tick();
        }
        Assert.Equal(0, sim.Violations);
        Assert.True(sim.Placer.Moves <= changes, "moves " + sim.Placer.Moves + " for " + changes + " changes");
        Assert.True(sim.Placer.Moves + sim.Placer.Shows + sim.Placer.Hides < sim.Steps / 2);
    }
}
