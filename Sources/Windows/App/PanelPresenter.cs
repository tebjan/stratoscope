using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Stratoscope.Windows;

/// <summary>What the panel is drawn on: the window on screen, or a recorder when it is rendered offscreen.</summary>
internal interface IPanelSurface
{
    /// <summary>Applies the final rectangle (position and size) in one call.</summary>
    void SetBounds(Rectangle bounds);
    /// <summary>Shows the finished frame, in this call. Only <paramref name="dirty"/> differs from what was shown before.</summary>
    void Present(Bitmap frame, Rectangle dirty);
    /// <summary>What the surface has seen so far; the presenter reports the difference per update.</summary>
    PanelSurfaceCounters Counters { get; }
}

/// <summary>Window events the surface has handled: position or size changes (SetWindowPos), paints (WM_PAINT) and background erases (WM_ERASEBKGND).</summary>
internal readonly record struct PanelSurfaceCounters(int PositionChanges, int Paints, int Erases);

/// <summary>What one update of the panel did. <see cref="ToString"/> is the line of the trace.</summary>
internal readonly record struct PanelUpdate(string Reason, bool SizeChanged, int PositionChanges, int Paints, int Erases, Rectangle Dirty)
{
    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "panel update: reason={0} size-changed={1} set-window-pos={2} paints={3} erases={4} dirty={5}",
        Reason, SizeChanged ? "yes" : "no", PositionChanges, Paints, Erases, Dirty.IsEmpty ? "none" : Dirty.X + "," + Dirty.Y + "," + Dirty.Width + "," + Dirty.Height);
}

/// <summary>
/// Takes finished frames, never draws content. A change is one update: the new frame is complete before anything on screen changes, then
/// the window gets its final rectangle (position and size) in one call and the frame is shown in one operation, only the rows that differ;
/// the same pixels do nothing at all. The two bitmaps (the one shown and the one being composed) and their byte copies are reused, so a
/// steady state allocates nothing here.
/// </summary>
internal sealed class PanelPresenter : IDisposable
{
    private readonly IPanelSurface surface;
    private Bitmap? front, back;
    private byte[] frontBytes = Array.Empty<byte>(), backBytes = Array.Empty<byte>();
    private Rectangle bounds = Rectangle.Empty;

    public PanelPresenter(IPanelSurface panelSurface) { surface = panelSurface; }

    /// <summary>The frame on screen (null before the first update).</summary>
    public Bitmap? Shown => front;

    /// <summary>The pixels of <see cref="Shown"/>, 32 bits per pixel, top row first: what a window copies to the screen with one GDI call.</summary>
    public byte[] ShownBytes => frontBytes;

    /// <summary>An opaque bitmap of this size to compose the next frame into. Reused while the size stays the same.</summary>
    public Bitmap Target(Size size)
    {
        if (back == null || back.Size != size)
        {
            back?.Dispose();
            back = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppRgb);
        }
        return back;
    }

    /// <summary>Makes the frame composed into <see cref="Target"/> and the rectangle the window has to have the state on screen.</summary>
    public PanelUpdate Update(string reason, Rectangle wanted)
    {
        if (back == null) throw new InvalidOperationException("Compose into Target() first.");
        var before = surface.Counters;
        var sizeChanged = wanted.Size != bounds.Size;
        var boundsChanged = wanted != bounds;
        Read(back, ref backBytes);
        var dirty = front == null || front.Size != back.Size ? new Rectangle(Point.Empty, back.Size) : Difference(frontBytes, backBytes, back.Width, back.Height);
        if (boundsChanged) { surface.SetBounds(wanted); bounds = wanted; }
        if (!dirty.IsEmpty)
        {
            (front, back) = (back, front);
            (frontBytes, backBytes) = (backBytes, frontBytes);
            surface.Present(front!, dirty);
        }
        var after = surface.Counters;
        var update = new PanelUpdate(reason, sizeChanged, after.PositionChanges - before.PositionChanges, after.Paints - before.Paints, after.Erases - before.Erases, dirty);
        if (ReadoutHost.Tracing) ReadoutHost.Trace(update.ToString());
        return update;
    }

    private readonly BitmapData lockData = new BitmapData();      // reused: LockBits would allocate a new one per call

    /// <summary>The 32-bit pixels of a bitmap in a reused array.</summary>
    private void Read(Bitmap bitmap, ref byte[] bytes)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb, lockData);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            if (bytes.Length != length) bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>The rows, over the full width, from the first to the last that differ; empty when the two are the same.</summary>
    private static Rectangle Difference(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length != b.Length) return new Rectangle(0, 0, width, height);
        var stride = a.Length / Math.Max(1, height);
        int first = -1, last = -1;
        for (int y = 0; y < height; y++)
        {
            if (a.AsSpan(y * stride, stride).SequenceEqual(b.AsSpan(y * stride, stride))) continue;
            if (first < 0) first = y;
            last = y;
        }
        return first < 0 ? Rectangle.Empty : new Rectangle(0, first, width, last - first + 1);
    }

    public void Dispose() { front?.Dispose(); back?.Dispose(); front = back = null; }
}

/// <summary>Draws a complete frame of the panel into a bitmap: the background, the content at the panel's scale and scroll position, the scroll bar.</summary>
internal static class PanelFrame
{
    public static PanelPainter Compose(Bitmap target, PanelModel model, Palette palette, PanelLayout layout, float scroll)
    {
        using (var g = Graphics.FromImage(target))
        {
            g.Clear(palette.Background);
            g.ScaleTransform(layout.Scale, layout.Scale);
            g.TranslateTransform(0, -scroll);
            var painter = new PanelPainter(g, palette, model);
            painter.Paint();
            if (layout.Scrolls) DrawScrollBar(g, palette, layout, scroll);
            return painter;
        }
    }

    private static void DrawScrollBar(Graphics g, Palette palette, PanelLayout layout, float scroll)
    {
        var viewport = layout.Size.Height / layout.Scale;
        var maxScroll = Math.Max(1f, layout.ContentHeight - viewport);
        var thumb = Math.Max(24f, viewport * viewport / layout.ContentHeight);
        var top = scroll / maxScroll * (viewport - thumb) + scroll;
        using (var path = new GraphicsPath())
        using (var brush = new SolidBrush(palette.Faint(0.35)))
        {
            path.AddRectangle(new RectangleF(PanelPainter.Width - 5, top + 2, 3, thumb - 4));
            g.FillPath(brush, path);
        }
    }
}

/// <summary>
/// The panel without a window: the same steps as <see cref="StatusPanel"/> (height, final rectangle, one composed frame, one update) on a
/// recording surface, at the bottom of a 2560 x 1440 screen next to a 48 px taskbar. For <c>--trace-panel</c> and the tests.
/// </summary>
internal sealed class OffscreenPanel : IDisposable
{
    private readonly RecordingPanelSurface surface = new RecordingPanelSurface();
    private readonly PanelPresenter presenter;
    private readonly Rectangle anchor = new Rectangle(1100, 1392, 120, 48), work = new Rectangle(0, 0, 2560, 1392);
    private readonly int dpi;
    private readonly bool dark;

    public OffscreenPanel(int dpi = 96, bool dark = false) { this.dpi = dpi; this.dark = dark; presenter = new PanelPresenter(surface); }

    public RecordingPanelSurface Surface => surface;
    public PanelLayout? Layout { get; private set; }
    /// <summary>The sections of the last frame with their top and bottom in logical pixels.</summary>
    public List<(string Name, float Top, float Bottom)> Sections { get; private set; } = new List<(string, float, float)>();

    /// <summary>Shows the model as the next state of the panel.</summary>
    public PanelUpdate Show(string reason, PanelModel model)
    {
        var palette = Palette.For(dark);
        float height;
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
            height = new PanelPainter(g, palette, model).Paint();
        var layout = Layout = PanelLayout.Compute(anchor, height, TaskbarEdge.Bottom, dpi, PanelSizing.Scale(dpi), work);
        var painter = PanelFrame.Compose(presenter.Target(layout.Size), model, palette, layout, 0);
        Sections = new List<(string, float, float)>(painter.Sections);
        return presenter.Update(reason, layout.Bounds);
    }

    public void Dispose() => presenter.Dispose();
}

/// <summary>A surface without a window: it counts what the presenter asks of it.</summary>
internal sealed class RecordingPanelSurface : IPanelSurface
{
    private int positionChanges, paints;
    public List<Rectangle> Bounds { get; } = new List<Rectangle>();

    public void SetBounds(Rectangle bounds) { positionChanges++; Bounds.Add(bounds); }
    public void Present(Bitmap frame, Rectangle dirty) { paints++; }

    /// <summary>There is no window, so there is never a background to erase.</summary>
    public PanelSurfaceCounters Counters => new PanelSurfaceCounters(positionChanges, paints, 0);
}
