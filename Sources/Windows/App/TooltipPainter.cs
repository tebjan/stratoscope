using System.Drawing;

namespace Stratoscope.Windows;

/// <summary>
/// The text of a tooltip: its size and its lines in the panel's palette and type (the body size of the ramp). The window around it, with
/// its rounded corners and shadow, is <see cref="TipWindow"/>; the panel and the readout both use it, so they look the same.
/// </summary>
internal static class TooltipPainter
{
    private const float PadX = 8f, PadY = 6f, LineGap = 2f, FontEpx = Typography.BodyEpx;

    /// <summary>The size of the tooltip body for these lines at this scale, in device pixels.</summary>
    public static Size Measure(string text, float scale)
    {
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
        using (var font = Typography.Regular(FontEpx * scale))
        {
            var lines = text.Split('\n');
            float width = 0;
            foreach (var line in lines) width = Math.Max(width, Typography.TextWidth(g, line, font));
            return new Size((int)Math.Ceiling(width + 2 * PadX * scale), (int)Math.Ceiling(lines.Length * font.Height + (lines.Length - 1) * LineGap * scale + 2 * PadY * scale));
        }
    }

    /// <summary>Draws the lines into the body rectangle: the first line in the primary colour (semibold when there are more), the rest secondary.</summary>
    public static void DrawText(Graphics g, Rectangle body, string text, Palette pal, float scale)
    {
        using (var regular = Typography.Regular(FontEpx * scale))
        using (var strong = Typography.Strong(FontEpx * scale))
        using (var brush = new SolidBrush(pal.Label))
        using (var dim = new SolidBrush(pal.Dim))
        {
            var lines = text.Split('\n');
            var y = body.Y + PadY * scale;
            for (int i = 0; i < lines.Length; i++)
            {
                g.DrawString(lines[i], i == 0 && lines.Length > 1 ? strong : regular, i == 0 ? brush : dim, body.X + PadX * scale, y, StringFormat.GenericTypographic);
                y += regular.Height + LineGap * scale;
            }
        }
    }
}
