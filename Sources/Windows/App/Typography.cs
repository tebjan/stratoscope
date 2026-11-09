using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace Stratoscope.Windows;

/// <summary>
/// Type for the taskbar readout. Segoe UI Variable (Windows 11) with Segoe UI as the fallback, sizes from the Windows type ramp
/// (Caption 12 epx, Body 14 epx), the number in semibold and everything else regular. Metrics come from the font outlines, so
/// the text can be placed by its cap height and baseline instead of by the font box.
/// </summary>
internal static class Typography
{
    /// <summary>The type ramp, in epx (device-independent px at 100 %): Body for every label and value, Caption for small secondary text, Model for the model name in the panel header, Display for the number and the name in the readout.</summary>
    public const float CaptionEpx = 11f;
    public const float BodyEpx = 12f;
    public const float ModelEpx = 13f;
    public const float DisplayEpx = 12f;

    private static string First(params string[] families)
    {
        foreach (var f in families) if (FontFamily.Families.Any(x => x.Name == f)) return f;
        return "Segoe UI";
    }

    public static readonly string TextFamily = First("Segoe UI Variable Text", "Segoe UI");
    public static readonly string StrongFamily = First("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");

    /// <summary>Device pixels are whole: a font of 14.4 px is drawn at 14, so the stems land on the pixel grid.</summary>
    public static float Whole(float px) => Math.Max(1f, (float)Math.Round(px));
    public static Font Regular(float px) => new Font(TextFamily, Whole(px), FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Strong(float px) => StrongFamily == "Segoe UI" ? new Font(StrongFamily, Whole(px), FontStyle.Bold, GraphicsUnit.Pixel) : new Font(StrongFamily, Whole(px), FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Where the capital letters of a font sit in its line box.</summary>
    public struct LineMetrics
    {
        /// <summary>Distance from the top of the line to the baseline, in pixels.</summary>
        public float Baseline;
        /// <summary>Height of a capital letter (and of the figures), in pixels.</summary>
        public float CapHeight;
    }

    private static readonly Dictionary<string, LineMetrics> Cache = new Dictionary<string, LineMetrics>();

    /// <summary>The baseline and cap height of the font, measured on the outline of a flat-topped, flat-bottomed probe letter ("H" for capitals, "7" for figures) drawn the way the text is drawn.</summary>
    public static LineMetrics Measure(Font font, string probe = "H")
    {
        var key = probe + "|" + font.Name + "|" + font.Style + "|" + font.Size.ToString("0.###", CultureInfo.InvariantCulture);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var m)) return m;
            // Measured on the pixels the renderer really produces (a flat-topped, flat-bottomed probe drawn the same way as the text),
            // so the layout is right whatever hinting or rounding the font gets at this size. Sub-pixel: the coverage of the edge rows counts.
            var side = (int)Math.Ceiling(font.Size * 2) + 8;
            var originY = font.Size * 0.5f;
            using (var bmp = new Bitmap(side + (int)font.Size, side, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                g.DrawString(probe, font, Brushes.White, 2f, originY, StringFormat.GenericTypographic);
                var coverage = new double[bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        coverage[y] = Math.Max(coverage[y], bmp.GetPixel(x, y).A / 255.0);
                int first = Array.FindIndex(coverage, c => c >= 0.12), last = Array.FindLastIndex(coverage, c => c >= 0.12);
                var top = first + (1 - coverage[first]);
                var bottom = last + coverage[last];
                m = new LineMetrics { Baseline = (float)(bottom - originY), CapHeight = (float)(bottom - top) };
            }
            return Cache[key] = m;
        }
    }

    /// <summary>The width of the widest figure: the cell width for numbers, so they do not change width when digits change.</summary>
    public static float FigureWidth(Graphics g, Font font)
    {
        float widest = 0;
        for (char c = '0'; c <= '9'; c++)
            widest = Math.Max(widest, g.MeasureString(c.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width);
        return widest;
    }

    public static float TextWidth(Graphics g, string s, Font font) =>
        s.Length == 0 ? 0 : g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    /// <summary>The width of a number set with tabular figures: every digit takes a cell, at least <paramref name="minDigits"/> of them.</summary>
    public static float TabularWidth(Graphics g, string s, Font font, int minDigits)
    {
        var cell = FigureWidth(g, font);
        float w = 0; int digits = 0;
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9') { w += cell; digits++; }
            else w += TextWidth(g, c.ToString(), font);
        }
        return w + Math.Max(0, minDigits - digits) * cell;
    }

    /// <summary>Draws a number with tabular figures, left-aligned, with its baseline at <paramref name="baseline"/>.</summary>
    public static void DrawTabular(Graphics g, string s, Font font, Brush brush, float x, float baseline)
    {
        var cell = FigureWidth(g, font);
        var top = baseline - Measure(font).Baseline;
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9')
            {
                var w = TextWidth(g, c.ToString(), font);
                g.DrawString(c.ToString(), font, brush, x + (cell - w) / 2, top, StringFormat.GenericTypographic);
                x += cell;
            }
            else
            {
                g.DrawString(c.ToString(), font, brush, x, top, StringFormat.GenericTypographic);
                x += TextWidth(g, c.ToString(), font);
            }
        }
    }

    public static void DrawAtBaseline(Graphics g, string s, Font font, Brush brush, float x, float baseline) =>
        g.DrawString(s, font, brush, x, baseline - Measure(font).Baseline, StringFormat.GenericTypographic);

    public static string Ellipsize(Graphics g, string s, Font font, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (TextWidth(g, s, font) <= maxWidth) return s;
        while (s.Length > 1 && TextWidth(g, s + "…", font) > maxWidth) s = s.Substring(0, s.Length - 1);
        return s + "…";
    }
}
