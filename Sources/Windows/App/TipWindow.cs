using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Stratoscope.Windows;

/// <summary>
/// The tooltip of the panel and of the readout: one themed window with rounded corners and a soft shadow, drawn in the panel's palette.
/// A layered popup that never takes focus and lets the pointer through, so it can never cover the element and make it lose the hover.
/// There is no native tooltip control anywhere in the app. Create it and use it on one thread (the one that owns the pointer events).
/// </summary>
internal sealed class TipWindow : IDisposable
{
    private const string ClassName = "Stratoscope.Tip";
    private const int WS_EX_TOPMOST = 0x8;
    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new IntPtr(-1);
    private static readonly Native.WndProc Proc = (h, msg, w, l) => msg == WM_NCHITTEST ? HTTRANSPARENT : Native.DefWindowProc(h, msg, w, l);
    private static bool registered;

    private const float RadiusEpx = 6f, ShadowEpx = 8f;

    private IntPtr window;

    public bool IsShown { get; private set; }

    private static void Register()
    {
        if (registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WNDCLASSEX)), lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null), hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW), lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        registered = true;
    }

    /// <summary>
    /// Shows the tooltip next to <paramref name="anchor"/> (a screen rectangle): above it, or below when there is no room above (or when
    /// <paramref name="preferAbove"/> is false), centred on it and kept inside <paramref name="workArea"/>. Never over the anchor.
    /// </summary>
    public void Show(string text, Rectangle anchor, Rectangle workArea, bool preferAbove, Palette palette, float scale)
    {
        using (var bmp = Render(text, palette, scale))
        {
            var shadow = (int)Math.Round(ShadowEpx * scale);
            var body = new Size(bmp.Width - 2 * shadow, bmp.Height - 2 * shadow);
            var gap = (int)Math.Round(6 * scale);
            var x = anchor.Left + anchor.Width / 2 - body.Width / 2;
            x = Math.Max(workArea.Left + 4, Math.Min(x, workArea.Right - body.Width - 4));
            var above = anchor.Top - gap - body.Height;
            var below = anchor.Bottom + gap;
            var y = preferAbove ? (above >= workArea.Top ? above : below) : (below + body.Height <= workArea.Bottom ? below : above);
            Place(bmp, new Point(x - shadow, y - shadow));
        }
    }

    /// <summary>Shows the tooltip at an exact screen position of its body (used by the screenshot tool).</summary>
    public void ShowAt(string text, Point bodyTopLeft, Palette palette, float scale)
    {
        using (var bmp = Render(text, palette, scale))
        {
            var shadow = (int)Math.Round(ShadowEpx * scale);
            Place(bmp, new Point(bodyTopLeft.X - shadow, bodyTopLeft.Y - shadow));
        }
    }

    private void Place(Bitmap bmp, Point topLeft)
    {
        if (window == IntPtr.Zero || !Native.IsWindow(window))
        {
            Register();
            window = Native.CreateWindowEx(WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, ClassName, "Stratoscope tooltip",
                Native.WS_POPUP, 0, 0, bmp.Width, bmp.Height, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (window == IntPtr.Zero) return;
        }
        ReadoutHost.Present(window, bmp, topLeft);
        Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
        IsShown = true;
    }

    public void Hide()
    {
        if (window != IntPtr.Zero && IsShown) Native.ShowWindow(window, Native.SW_HIDE);
        IsShown = false;
    }

    public void Dispose()
    {
        if (window != IntPtr.Zero && Native.IsWindow(window)) Native.DestroyWindow(window);
        window = IntPtr.Zero; IsShown = false;
    }

    /// <summary>The tooltip as a premultiplied bitmap: the body in the palette, 6 epx corners, a soft shadow around it.</summary>
    internal static Bitmap Render(string text, Palette pal, float scale)
    {
        var body = TooltipPainter.Measure(text, scale);
        var shadow = (int)Math.Round(ShadowEpx * scale);
        var bmp = new Bitmap(body.Width + 2 * shadow, body.Height + 2 * shadow, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var radius = (int)Math.Round(RadiusEpx * scale);
            var rect = new Rectangle(shadow, shadow, body.Width, body.Height);
            // The shadow: a few rounded rectangles that grow and fade, a little lower than the body. No blur filter.
            for (int i = 4; i >= 1; i--)
            {
                var grow = (int)Math.Round(i * scale * 1.2);
                using (var path = Round(new Rectangle(rect.X - grow, rect.Y - grow + (int)Math.Round(2 * scale), rect.Width + 2 * grow, rect.Height + 2 * grow), radius + grow))
                using (var brush = new SolidBrush(Color.FromArgb(pal.Dark ? 20 : 11, 0, 0, 0)))
                    g.FillPath(brush, path);
            }
            using (var path = Round(rect, radius))
            {
                using (var back = new SolidBrush(pal.Background)) g.FillPath(back, path);
                if (!pal.HighContrast) using (var wash = new SolidBrush(pal.Faint(0.06))) g.FillPath(wash, path);
                using (var pen = new Pen(pal.HighContrast ? pal.Label : pal.Faint(0.18), 1f)) g.DrawPath(pen, path);
            }
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            TooltipPainter.DrawText(g, rect, text, pal, scale);
        }
        return bmp;
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
