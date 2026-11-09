using System.Drawing;
using System.Drawing.Imaging;
using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>
/// Measurements of the rendered readout and of the colours, used by the tests and by <c>Stratoscope.exe --measure</c> to fill
/// the "Design rules" table in docs/WINDOWS.md. Everything here measures what is drawn (pixels), not what the layout code intended.
/// </summary>
internal static class Measurements
{
    public sealed class Centring
    {
        public int Dpi;
        public bool Dark;
        public string Case = "";
        /// <summary>Height of the item in device pixels.</summary>
        public int Height;
        /// <summary>Top and bottom (baseline) of the digits of the value and of the glyph, in device pixels.</summary>
        public double ValueTop, ValueBottom, ValueCentre;
        public double GlyphTop, GlyphBottom, GlyphCentre;
        /// <summary>Each centre minus half the item height: 0 when it sits on the centre line of the taskbar.</summary>
        public double ValueOffset => ValueCentre - Height / 2.0;
        public double GlyphOffset => GlyphCentre - Height / 2.0;
    }

    /// <summary>Pixels of a rendered bitmap as unpremultiplied alpha and colour.</summary>
    private static (byte A, Color C)[,] Pixels(Bitmap bmp)
    {
        var result = new (byte, Color)[bmp.Width, bmp.Height];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var i = y * data.Stride + x * 4;
                    int a = bytes[i + 3];
                    result[x, y] = a == 0 ? ((byte)0, Color.Transparent)
                        : ((byte)a, Color.FromArgb(255, Math.Min(255, bytes[i + 2] * 255 / a), Math.Min(255, bytes[i + 1] * 255 / a), Math.Min(255, bytes[i] * 255 / a)));
                }
        }
        finally { bmp.UnlockBits(data); }
        return result;
    }

    private static bool Near(Color a, Color b, int tolerance) => Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    /// <summary>
    /// The sub-pixel extent of a set of pixels: the first and last row with coverage, refined by how much of the edge row is covered.
    /// Coverage is alpha relative to the largest alpha in the set.
    /// </summary>
    private static (double Top, double Bottom) Extent(IEnumerable<(int Row, byte A)> pixels)
    {
        var list = pixels.ToList();
        if (list.Count == 0) return (double.NaN, double.NaN);
        var max = list.Max(p => p.A);
        var rows = list.GroupBy(p => p.Row).ToDictionary(g => g.Key, g => g.Max(p => p.A) / (double)max);
        var first = rows.Keys.Where(r => rows[r] >= 0.15).Min();
        var last = rows.Keys.Where(r => rows[r] >= 0.15).Max();
        return (first + (1 - Math.Min(1, rows[first])), last + Math.Min(1, rows[last]));
    }

    /// <summary>
    /// Renders one readout item and measures where its glyph and its text block are. The texts have no descenders, so the bottom of the
    /// caption is its baseline.
    /// </summary>
    /// <summary>The share of the text's pixels that are (almost) fully opaque: crisp stems have it high, soft smeared ones low. Alpha of the readout bitmap.</summary>
    public static double Sharpness(Bitmap bmp)
    {
        var px = Pixels(bmp);
        int visible = 0, solid = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var (a, _) = px[x, y];
                if (a >= 25) visible++;
                if (a >= 230) solid++;
            }
        return visible == 0 ? 0 : solid / (double)visible;
    }

    /// <summary>The last pixel column (alpha of 50 % or more) of the glyph of a state, from the item's left edge, and the end of its slot (the anchor minus the gap).</summary>
    public static (int LastColumn, int SlotEnd) GlyphRightEdge(int dpi, ServerState state)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        var item = new ReadoutItem("Workstation", state, ReadoutText.None, 1.0, "", null, false);
        var geometry = ReadoutPainter.GeometryOf(item, m);
        using (var bmp = ReadoutPainter.Render(new[] { item }, m, ReadoutPainter.Measure(new[] { item }, m)))
        {
            var px = Pixels(bmp);
            for (int x = geometry.Anchor - 1; x >= 0; x--)
                for (int y = 0; y < bmp.Height; y++)
                    if (px[x, y].A >= 128) return (x, geometry.GlyphRight);
        }
        return (-1, geometry.GlyphRight);
    }

    /// <summary>
    /// Renders a writing item with this speed and returns the empty columns, in device pixels, between the right edge of the dot and the first
    /// pixel of the number (alpha of 25 % or more). Measured on the rendered pixels.
    /// </summary>
    public static int NumberClearance(int dpi, string value)
    {
        var m = UiMetrics.At(dpi, 1.0, true, true);
        var item = new ReadoutItem("Workstation", ServerState.Writing, new ReadoutText(value, "t/s"), null, "", null, false);
        var geometry = ReadoutPainter.GeometryOf(item, m);
        using (var bmp = ReadoutPainter.Render(new[] { item }, m, ReadoutPainter.Measure(new[] { item }, m)))
        {
            var px = Pixels(bmp);
            for (int x = geometry.GlyphRight; x < geometry.UnitX; x++)
                for (int y = 0; y < bmp.Height; y++)
                    if (px[x, y].A >= 64) return x - geometry.GlyphRight;
        }
        return -1;
    }

    /// <summary>
    /// Renders one item (a dot with "26 t/s", or the bar with "247 t/s") and measures where the glyph and the first digit of the value sit
    /// vertically. Digits have no descenders, so their bottom is the baseline.
    /// </summary>
    public static Centring MeasureCentring(int dpi, bool dark, bool dot)
    {
        var m = UiMetrics.At(dpi, 1.0, dark, dark);
        var text = dot ? new ReadoutText("26", "t/s") : new ReadoutText("247", "t/s");
        var item = new ReadoutItem("Workstation", dot ? ServerState.Writing : ServerState.Reading, text, 0.57, "", null, false);
        var size = ReadoutPainter.Measure(new[] { item }, m);
        var geometry = ReadoutPainter.GeometryOf(item, m);
        using (var bmp = ReadoutPainter.Render(new[] { item }, m, size))
        {
            var px = Pixels(bmp);
            var glyphRows = new List<(int, byte)>(); var valueRows = new List<(int, byte)>();
            var digit = m.Px(7);       // the width of the first digit: its columns have its top and its baseline
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var (a, _) = px[x, y];
                    if (a <= 8) continue;
                    if (x < geometry.GlyphRight) glyphRows.Add((y, a));
                    else if (x >= geometry.TextX && x < geometry.UnitX) valueRows.Add((y, a));
                }
            var glyph = Extent(glyphRows); var value = Extent(valueRows);
            return new Centring
            {
                Dpi = dpi, Dark = dark, Case = dot ? "dot, value" : "bar, value", Height = bmp.Height,
                ValueTop = value.Top, ValueBottom = value.Bottom, ValueCentre = (value.Top + value.Bottom) / 2,
                GlyphTop = glyph.Top, GlyphBottom = glyph.Bottom, GlyphCentre = (glyph.Top + glyph.Bottom) / 2,
            };
        }
    }

    public sealed class ContrastRow
    {
        public string Surface = "", Name = "";
        public Color Foreground, Background;
        public double Ratio, Required;
        public bool Passes => Ratio + 1e-9 >= Required;
    }

    /// <summary>Every text and glyph colour the app draws against the surface it is drawn on, with the WCAG AA ratio it must reach.</summary>
    public static List<ContrastRow> ContrastTable()
    {
        var rows = new List<ContrastRow>();
        foreach (var taskbar in new[] { true, false })
            foreach (var dark in new[] { true, false })
            {
                var pal = taskbar ? Palette.Taskbar(dark) : Palette.For(dark);
                var surface = (taskbar ? "taskbar " : "panel ") + (dark ? "dark" : "light");
                void Add(string name, Color fg, double required) =>
                    rows.Add(new ContrastRow { Surface = surface, Name = name, Foreground = fg, Background = pal.Background, Ratio = Contrast.Ratio(fg, pal.Background), Required = required });
                Add("text", taskbar ? ReadoutPainter.Ink(pal) : pal.Label, Contrast.SmallText);
                Add("secondary text (captions, units)", pal.Dim, Contrast.SmallText);
                Add("disabled text (version)", pal.TertiaryLabel, Contrast.SmallText);
                foreach (ServerState s in Enum.GetValues(typeof(ServerState))) Add("glyph " + Display.Word(s), pal.StateColor(s), Contrast.LargeTextAndGraphics);
                foreach (CacheHit h in Enum.GetValues(typeof(CacheHit))) Add("cache " + h.Label().Split(' ')[0], pal.HitColor(h), Contrast.LargeTextAndGraphics);
                if (!taskbar)
                {
                    Add("bar teal", pal.Teal, Contrast.LargeTextAndGraphics); Add("bar purple", pal.Purple, Contrast.LargeTextAndGraphics);
                    Add("bar indigo", pal.Indigo, Contrast.LargeTextAndGraphics); Add("bar mint", pal.Mint, Contrast.LargeTextAndGraphics);
                    Add("bar red", pal.Red, Contrast.LargeTextAndGraphics);
                    foreach (var (name, c) in new[] { ("status word orange", pal.Orange), ("status word blue", pal.Blue), ("status word green", pal.Green), ("status word yellow", pal.Yellow), ("status word red", pal.Red), ("status word grey", pal.Gray) })
                        Add(name, pal.TextOf(c), Contrast.SmallText);
                }
            }
        return rows;
    }
}
