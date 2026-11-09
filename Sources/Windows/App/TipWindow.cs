using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace Stratoscope.Windows;

/// <summary>
/// The tooltip of the panel and of the readout: one themed window with rounded corners and a soft shadow, drawn in the panel's palette.
/// A layered popup that never takes focus and lets the pointer through, so it can never cover the element and make it lose the hover.
/// There is no native tooltip control anywhere in the app. Create it and use it on one thread (the one that owns the pointer events).
/// </summary>
internal sealed class TipWindow : IDisposable
{
    private const string ClassName = "Stratoscope.Tip";
    private static readonly WNDPROC Proc = (h, msg, w, l) => msg == WM_NCHITTEST ? new LRESULT(HTTRANSPARENT) : DefWindowProc(h, msg, w, l);
    private static bool registered;

    private const float RadiusEpx = 6f, ShadowEpx = 8f;

    private HWND window;

    public bool IsShown { get; private set; }

    private static void Register()
    {
        if (registered) return;
        LayeredWindows.RegisterClass(ClassName, Proc);
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
        if (window.IsNull || !IsWindow(window))
        {
            Register();
            window = LayeredWindows.Create(WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                ClassName, "Stratoscope tooltip", WINDOW_STYLE.WS_POPUP, bmp.Width, bmp.Height, HWND.Null);
            if (window.IsNull) return;
        }
        LayeredWindows.Present(window, bmp, topLeft);
        ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        IsShown = true;
    }

    public void Hide()
    {
        if (!window.IsNull && IsShown) ShowWindow(window, SHOW_WINDOW_CMD.SW_HIDE);
        IsShown = false;
    }

    public void Dispose()
    {
        if (!window.IsNull && IsWindow(window)) DestroyWindow(window);
        window = HWND.Null; IsShown = false;
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

    /// <summary>The outline path of a rectangle in whole pixels: the last pixel row and column are the right and bottom edge.</summary>
    private static GraphicsPath Round(Rectangle r, int radius) => Shapes.RoundedRect(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
}
