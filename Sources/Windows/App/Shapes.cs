using System.Drawing;
using System.Drawing.Drawing2D;

namespace Stratoscope.Windows;

/// <summary>The closed paths GDI+ has no call for: a rectangle with round corners, and the pill (a rounded rectangle whose ends are half circles).</summary>
internal static class Shapes
{
    /// <summary>A rectangle with corners of <paramref name="radius"/>, never larger than half of the shorter side. Dispose the path.</summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A pill: the corner radius is half of the shorter side, so a pill as wide as it is high is a circle. Dispose the path.</summary>
    public static GraphicsPath Pill(RectangleF r) => RoundedRect(r, Math.Min(r.Width, r.Height) / 2f);
}
