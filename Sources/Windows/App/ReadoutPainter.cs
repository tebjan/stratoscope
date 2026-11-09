using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>One server in the taskbar readout.</summary>
internal sealed class ReadoutItem
{
    public ReadoutItem(string name, ServerState state, ReadoutText text, double? fraction, string spoken, string? tip = null, bool nameInCaption = true, CacheHit? hit = null)
    {
        Name = name; State = state; Text = text; Fraction = fraction; Spoken = spoken; Tip = tip; NameInCaption = nameInCaption; Hit = hit;
    }
    /// <summary>How much of the prompt came from the cache while it is read: the colour of the bar, as on the Mac (green, yellow, orange, gray). Null when unknown.</summary>
    public CacheHit? Hit { get; }
    public string Name { get; }
    public ServerState State { get; }
    public ReadoutText Text { get; }
    /// <summary>The prompt progress while reading, 0..1: the length of the bar.</summary>
    public double? Fraction { get; }
    /// <summary>What a screen reader says: "Workstation, writing 26 tokens per second".</summary>
    public string Spoken { get; }
    /// <summary>The tooltip: only what the item does not show. Null when there is nothing to add.</summary>
    public string? Tip { get; }
    /// <summary>True when several servers are shown: the item then carries its name as a small constant label. With one server there is no name.</summary>
    public bool NameInCaption { get; }

    public static ReadoutItem From(ServerMonitor monitor, bool nameInCaption = true)
    {
        var s = monitor.Snapshot;
        var progress = s != null ? Display.Progress(s) : null;
        var text = Display.Readout(s);
        var visible = new List<string> { text.Value, text.Unit };
        if (nameInCaption) visible.Add(monitor.Name);
        return new ReadoutItem(monitor.Name, monitor.State, text,
            progress?.Fraction, Display.Spoken(monitor.Name, s, monitor.Polled, monitor.Faulted),
            Tooltips.NotShown(Tooltips.ReadoutExtra(monitor.State, s, nameInCaption ? null : monitor.Name), visible), nameInCaption, progress?.Hit);
    }
}

/// <summary>
/// The geometry of one item, in device pixels relative to the item's left edge. The same in every state and for every content: it comes
/// from the table of constants (<see cref="ReadoutTable"/>), never from the text.
/// </summary>
internal readonly record struct ItemGeometry(int Width, int GlyphX, int GlyphRight, int Anchor, int TextX, int UnitX, int TextRight);

/// <summary>
/// The fixed layout of one readout item, one table of constants in logical px (epx, 4 epx grid) scaled to the display by one function
/// (<see cref="ReadoutPainter.TableFor"/>). Nothing in the readout is positioned or sized from the current text, the current state, whether
/// a number is shown or how long a server's name is. Text is drawn into its slot with a fixed alignment; the only measurement is the width
/// of a figure and of "t/s" in the font, taken once per display scale.
/// <code>
///  | 8 margin | [name 80, only with several servers] 8 | glyph slot 40 | 6 | anchor | 6 | number slot 4 figures | 2 | "t/s" | 8 margin |
/// </code>
/// The glyph (the bar, the dot, the rings) ends at the anchor minus 6; the text slot starts at the anchor plus 6; the digits are right-aligned
/// in the number slot, so a short number leaves the space on its left.
/// </summary>
internal readonly record struct ReadoutTable(int Margin, int NameX, int NameWidth, int GlyphX, int GlyphRight, int Anchor, int TextX, int NumberRight, int UnitX, int TextRight, int Width, float Cell);

/// <summary>
/// Draws the readout that sits in the taskbar: one item per server, the same table for every item and every state. The glyph slot is as
/// wide as the progress bar (40 epx). While a prompt is read the bar fills it; every other state is a small shape of 6 epx right-aligned in
/// it, so that it ends where the bar ends: a filled dot in the state colour (ready, writing, thinking), a hollow grey ring (offline, no model,
/// connecting), a dotted ring (loading) or a red dot with a white mark (error). The shapes differ, not only the colour. The speed (plain
/// digits, right-aligned in a slot of four places, then the muted unit) is shown while something runs and nothing otherwise: there is no
/// state word, the state is the glyph; the full state in words is in the tooltip and the screen reader name. The visible hover fill is a
/// fixed rectangle per state class (bar with a number, dot with a number, glyph only), 8 epx of padding left and right, 4 above and
/// below, at least 24 epx wide.
/// </summary>
internal static class ReadoutPainter
{
    // The table, in epx.
    internal const float PadX = 8f;
    /// <summary>The width of the name slot (only with several servers); a longer name is cut with an ellipsis.</summary>
    internal const float NameSlotWidth = 80f;
    /// <summary>Between the name slot and the glyph slot.</summary>
    internal const float SlotGap = 8f;
    /// <summary>The gap g either side of the anchor: the graphics end g before it, the text starts g after it.</summary>
    internal const float AnchorGap = 6f;
    /// <summary>Between the number slot and the unit.</summary>
    internal const float UnitGap = 2f;
    /// <summary>The width of the progress bar, which is the width of the glyph slot.</summary>
    internal const float BarWidth = 40f;
    /// <summary>The number of figure places of the number slot; a speed has at most this many characters (see <see cref="Display.ReadoutNumber"/>).</summary>
    internal const int NumberPlaces = 4;
    /// <summary>The height of the bar and the diameter of the dot and the rings: one pill of one height, the dot is the bar at its shortest.</summary>
    internal const float GlyphHeight = 6f;
    /// <summary>The height and the dot diameter in epx for the comparison sheet only (null: <see cref="GlyphHeight"/> for both).</summary>
    internal static (float Bar, float Dot)? GlyphHeightOverride;
    /// <summary>The smallest width of the visible hover fill: a lone dot with its padding is the size of a click target.</summary>
    internal const float MinChipWidth = 24f;
    /// <summary>Extra space on each side of a separator, so that content to separator is 12 epx (8 of padding plus 4).</summary>
    internal const float SeparatorMargin = 4f;
    internal const float SeparatorHeight = 24f;
    internal const float TaskbarHeight = 48f;

    /// <summary>Draws thin guide lines at the slot boundaries and the centre line. For the screenshot sheets only.</summary>
    internal static bool DebugSlots;

    /// <summary>How the text is rendered into the layered bitmap. Grid-fitted grey-scale: hinted stems land on whole pixels (measured with --measure).</summary>
    internal static TextRenderingHint TextHint = TextRenderingHint.AntiAliasGridFit;
    /// <summary>
    /// GDI+ grey-scale text has no contrast enhancement, so thin stems stay half transparent and look soft next to the taskbar's own text
    /// (DirectWrite applies gamma and enhanced contrast). The alpha of the rendered bitmap is raised along a curve: alpha to the power of this
    /// value (below 1 strengthens the partly covered edge pixels). 0 switches it off.
    /// </summary>
    internal static double TextContrast = 0.70;

    private static void Strengthen(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (int i = 0; i < bytes.Length; i += 4)
            {
                var a = bytes[i + 3];
                if (a <= 8 || a == 255) continue;       // the click-catching background (alpha 3) stays as it is
                var stronger = (int)Math.Round(255 * Math.Pow(a / 255.0, TextContrast));
                var scale = stronger / (double)a;                         // premultiplied colour keeps its hue: scale it with the alpha
                bytes[i] = (byte)Math.Min(stronger, Math.Round(bytes[i] * scale));
                bytes[i + 1] = (byte)Math.Min(stronger, Math.Round(bytes[i + 1] * scale));
                bytes[i + 2] = (byte)Math.Min(stronger, Math.Round(bytes[i + 2] * scale));
                bytes[i + 3] = (byte)stronger;
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>The width reserved for the glyph in device pixels: the bar. The other shapes are right-aligned in the same slot.</summary>
    internal static int GlyphPx(UiMetrics m) => m.Px(BarWidth);

    /// <summary>The width of the glyph this state draws, in device pixels.</summary>
    internal static int GlyphWidth(ServerState state, UiMetrics m) => state == ServerState.Reading ? m.Px(BarWidth) : GlyphHeightPx(m, false);

    /// <summary>The height of the bar, or the diameter of the dot, in device pixels.</summary>
    internal static int GlyphHeightPx(UiMetrics m, bool bar) => m.Px(GlyphHeightOverride is { } o ? (bar ? o.Bar : o.Dot) : GlyphHeight);

    /// <summary>Space taken by a separator: the line and its margins.</summary>
    internal static int SeparatorWidth(UiMetrics m) => 1 + 2 * m.Px(SeparatorMargin);

    public static Color Ink(Palette p) => p.HighContrast ? p.Label : p.Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);

    private sealed class Fonts : IDisposable
    {
        public readonly Font Value, Unit, Name;
        public Fonts(UiMetrics m)
        {
            Value = Typography.Strong(Typography.DisplayEpx * m.TextFactor);      // the number: semibold
            Unit = Typography.Regular(Typography.CaptionEpx * m.TextFactor);      // t/s: small, muted
            Name = Typography.Regular(Typography.CaptionEpx * m.TextFactor);      // the constant name label
        }
        public void Dispose() { Value.Dispose(); Unit.Dispose(); Name.Dispose(); }
    }

    // The two measurements the table needs, taken once per display scale and text size: the cell of a figure and the width of "t/s".
    private static readonly Dictionary<string, (float Cell, int Unit)> Measured = new Dictionary<string, (float, int)>();

    private static (float Cell, int Unit) FontMeasures(UiMetrics m)
    {
        var key = m.Dpi + "|" + m.TextFactor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        lock (Measured)
        {
            if (Measured.TryGetValue(key, out var known)) return known;
            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            using (var f = new Fonts(m))
                return Measured[key] = (Typography.FigureWidth(g, f.Value), (int)Math.Ceiling(Typography.TextWidth(g, "t/s", f.Unit)) + 1);
        }
    }

    /// <summary>The table of one item in device pixels. <paramref name="named"/> is a property of the setup (several servers), not of the content.</summary>
    internal static ReadoutTable TableFor(UiMetrics m, bool named)
    {
        var (cell, unit) = FontMeasures(m);
        var margin = m.Px(PadX);
        var nameWidth = named ? m.Px(NameSlotWidth) : 0;
        var glyphX = margin + (named ? nameWidth + m.Px(SlotGap) : 0);
        var glyphRight = glyphX + m.Px(BarWidth);
        var anchor = glyphRight + m.Px(AnchorGap);
        var textX = anchor + m.Px(AnchorGap);
        var numberRight = textX + (int)Math.Ceiling(cell * NumberPlaces);
        var unitX = numberRight + m.Px(UnitGap);
        var textRight = unitX + unit;
        return new ReadoutTable(margin, margin, nameWidth, glyphX, glyphRight, anchor, textX, numberRight, unitX, textRight, textRight + margin, cell);
    }

    /// <summary>The geometry of an item in device pixels from its left edge.</summary>
    internal static ItemGeometry GeometryOf(ReadoutItem item, UiMetrics m)
    {
        var t = TableFor(m, item.NameInCaption && item.Name.Length > 0);
        return new ItemGeometry(t.Width, t.GlyphX, t.GlyphRight, t.Anchor, t.TextX, t.UnitX, t.TextRight);
    }

    /// <summary>The size the readout wants: the width of its items and the height of the taskbar.</summary>
    public static Size Measure(IList<ReadoutItem> items, UiMetrics m, int? height = null)
    {
        float w = 0;
        for (int i = 0; i < items.Count; i++) w += TableFor(m, items[i].NameInCaption && items[i].Name.Length > 0).Width + (i > 0 ? SeparatorWidth(m) : 0);
        return new Size((int)Math.Ceiling(w), height ?? m.Px(TaskbarHeight));
    }

    /// <summary>
    /// Renders the readout into a premultiplied ARGB bitmap of <paramref name="size"/>. Where the items do not fit, their names are cut with
    /// an ellipsis. <paramref name="bounds"/> receives the pixel rectangle of every item (the whole height, for hit-testing).
    /// </summary>
    public static Bitmap Render(IList<ReadoutItem> items, UiMetrics m, Size size, List<Rectangle>? bounds = null, bool hitTestable = false,
        int hover = -1, int pressed = -1)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        // The hover and pressed fill is its own layer below the content: the contrast curve below is for text edges and must not strengthen it.
        var chipLayer = hover >= 0 || pressed >= 0 ? new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb) : null;
        using (var chipGraphics = chipLayer != null ? Graphics.FromImage(chipLayer) : null)
        using (var g = Graphics.FromImage(bmp))
        using (var f = new Fonts(m))
        {
            if (chipGraphics != null) { chipGraphics.SmoothingMode = SmoothingMode.AntiAlias; chipGraphics.PixelOffsetMode = PixelOffsetMode.Half; }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextHint;   // grey-scale: ClearType needs an opaque background, the readout has per-pixel alpha
            g.PixelOffsetMode = PixelOffsetMode.Half;   // shapes and rules land exactly on device pixels; the text below switches to Default (see DrawItem)
            g.Clear(hitTestable ? Color.FromArgb(3, 0, 0, 0) : Color.Transparent);
            var palette = m.Taskbar;
            var ink = Ink(palette);

            var natural = items.Select(i => (float)TableFor(m, i.NameInCaption && i.Name.Length > 0).Width).ToList();
            var separators = Math.Max(0, items.Count - 1) * SeparatorWidth(m);
            var widths = Distribute(natural, size.Width - separators);

            float x = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    var sepTop = (size.Height - m.Px(SeparatorHeight)) / 2;
                    using (var brush = new SolidBrush(Palette.Alpha(ink, 0.22)))
                        g.FillRectangle(brush, (float)Math.Round(x) + m.Px(SeparatorMargin), sepTop, 1, m.Px(SeparatorHeight));   // exactly one device pixel
                    x += SeparatorWidth(m);
                }
                var rect = new Rectangle((int)Math.Round(x), 0, (int)Math.Round(widths[i]), size.Height);
                DrawItem(g, chipGraphics, items[i], TableFor(m, items[i].NameInCaption && items[i].Name.Length > 0), rect, m, palette, ink, f, i == pressed ? 2 : i == hover ? 1 : 0);
                bounds?.Add(rect);
                x += widths[i];
            }
            if (TextContrast > 0) Strengthen(bmp);
            if (chipLayer == null) return bmp;
            chipGraphics!.PixelOffsetMode = PixelOffsetMode.Default;
            chipGraphics.DrawImageUnscaled(bmp, 0, 0);       // the content over the fill
        }
        bmp.Dispose();
        return chipLayer;
    }

    /// <summary>Gives every item its natural width if the total fits, otherwise shrinks the widest ones first (their names are cut).</summary>
    internal static List<float> Distribute(List<float> natural, float budget)
    {
        var result = new List<float>(natural);
        if (natural.Count == 0 || natural.Sum() <= budget) return result;
        var order = Enumerable.Range(0, natural.Count).OrderBy(i => natural[i]).ToList();
        var remaining = budget;
        for (int k = 0; k < order.Count; k++)
        {
            var share = remaining / (order.Count - k);
            var i = order[k];
            result[i] = Math.Min(natural[i], share);
            remaining -= result[i];
        }
        return result;
    }

    /// <summary>
    /// The soft fill of a hovered or pressed item (the rectangle is fixed per state class, see <see cref="DrawItem"/>): corners 6 epx, no
    /// outline. The foreground colour at 11 % (17 % pressed): a grey tint on light, a lighter surface on dark.
    /// </summary>
    private static void DrawState(Graphics g, Rectangle bounds, UiMetrics m, Palette palette, bool pressed)
    {
        var alpha = pressed ? Palette.PressedAlpha : Palette.HoverAlpha;
        var fill = palette.HighContrast ? SystemColors.Highlight : (palette.Dark ? Color.White : Color.Black);
        using (var path = RoundRect(bounds, m.Px(6)))
        using (var brush = new SolidBrush(palette.HighContrast ? fill : Palette.Alpha(fill, alpha)))
            g.FillPath(brush, path);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The vertical positions of an item: the baselines, the centre line, and the extent of the text, in device pixels.</summary>
    internal sealed class Rhythm
    {
        public float BigBaseline, CaptionBaseline, OpticalCentre, BlockTop, BlockBottom;
    }

    internal static Rhythm RhythmFor(UiMetrics m, int height, bool numberLine = true)
    {
        using (var big = Typography.Strong(Typography.DisplayEpx * m.TextFactor))
        {
            // Whole device pixels throughout: the type is rendered hinted at these sizes, so a fractional cap height only blurs the baseline.
            var cap1 = (int)Math.Round(Typography.Measure(big, numberLine ? "7" : "H").CapHeight);   // the figures are a little taller than the capitals
            var descent = m.Px(3);
            // One line on the centre line of the taskbar: the figures are centred on it, the glyph too.
            var centre = height / 2f;
            var baseline = (float)Math.Round(centre + cap1 / 2f);
            return new Rhythm { BigBaseline = baseline, CaptionBaseline = baseline, BlockTop = baseline - cap1, BlockBottom = baseline + descent, OpticalCentre = centre };
        }
    }

    /// <summary>The screen-independent facts a trace line needs: where the glyph ends and where its centre is, in device pixels from the item's left edge.</summary>
    internal static (int GlyphRight, double GlyphCentre) GlyphPosition(ReadoutItem item, UiMetrics m)
    {
        var t = TableFor(m, item.NameInCaption && item.Name.Length > 0);
        var w = GlyphWidth(item.State, m);
        return (t.GlyphRight, t.GlyphRight - w / 2.0);
    }

    /// <param name="state">0 plain, 1 hovered, 2 pressed.</param>
    private static void DrawItem(Graphics g, Graphics? chipLayer, ReadoutItem item, ReadoutTable t, Rectangle rect, UiMetrics m, Palette palette, Color ink, Fonts f, int state = 0)
    {
        var rhythm = RhythmFor(m, rect.Height, true);
        var pad = t.Margin;
        var text = item.Text;

        // Every x comes from the table, from the item's left edge. A narrower item (many servers) gives its name slot back first.
        var shortBy = Math.Max(0, t.Width - rect.Width);
        var nameWidth = Math.Max(0, t.NameWidth - shortBy);
        var move = (t.NameWidth - nameWidth) + (t.NameWidth > 0 && nameWidth == 0 ? m.Px(SlotGap) : 0);     // what the glyph and the text move left by
        var glyphRight = rect.X + t.GlyphRight - move; var anchorX = rect.X + t.Anchor - move; var textX = rect.X + t.TextX - move;
        var unitX = rect.X + t.UnitX - move; var numberRight = rect.X + t.NumberRight - move; var unitRight = rect.X + t.TextRight - move;
        var nameX = rect.X + t.NameX;

        // The glyph: the bar fills its slot; every other shape is right-aligned in it, so it ends where the bar ends: one gap g before the anchor.
        var reading = item.State == ServerState.Reading;
        var glyphWidth = GlyphWidth(item.State, m);
        var glyphHeight = GlyphHeightPx(m, reading);
        var glyphBox = new RectangleF(glyphRight - glyphWidth, (float)Math.Round(rhythm.OpticalCentre - glyphHeight / 2f), glyphWidth, glyphHeight);

        var offsetBefore = g.PixelOffsetMode;
        g.PixelOffsetMode = PixelOffsetMode.Default;       // text lands on its baseline with Default; shapes need Half
        var dim = palette.Dim;
        var hasNumber = text.Unit.Length > 0;

        if (state != 0 && chipLayer != null)
        {
            // The hover fill: a fixed rectangle per state class (the bar with a number, a dot with a number, the glyph alone), 8 epx of padding
            // left and right, 4 above and below; never narrower than a click target.
            var padY = m.Px(4);
            var top = (int)Math.Floor(Math.Min(glyphBox.Top, rhythm.BlockTop)) - padY;
            var bottom = (int)Math.Ceiling(Math.Max(glyphBox.Bottom, rhythm.BlockBottom)) + padY;
            var left = (nameWidth > 0 ? nameX : (int)glyphBox.X) - pad;
            var right = (hasNumber ? unitRight : (int)glyphBox.Right) + pad;
            var missing = m.Px(MinChipWidth) - (right - left);
            if (missing > 0) { left -= missing / 2; right += missing - missing / 2; }
            var chip = Rectangle.FromLTRB(Math.Max(rect.X, left), Math.Max(0, top), Math.Min(rect.Right, right), Math.Min(rect.Height, bottom));
            DrawState(chipLayer, chip, m, palette, state == 2);
        }
        DrawGlyph(g, item, glyphBox, m, palette);
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (hasNumber)
        {
            // Digits right-aligned in the number slot (four places): the unit stays at its x, a short number leaves its space on the left.
            var width = Typography.TabularWidth(g, text.Value, f.Value, 0);
            using (var brush = new SolidBrush(ink))
                Typography.DrawTabular(g, text.Value, f.Value, brush, (float)Math.Round(numberRight - 1 - width), rhythm.BigBaseline);
            using (var brush = new SolidBrush(dim))
                Typography.DrawAtBaseline(g, text.Unit, f.Unit, brush, unitX, rhythm.BigBaseline);
        }
        if (nameWidth > 0)
            using (var brush = new SolidBrush(dim))
                Typography.DrawAtBaseline(g, Typography.Ellipsize(g, item.Name, f.Name, nameWidth), f.Name, brush, nameX, rhythm.BigBaseline);
        if (DebugSlots)
        {
            // Thin guides, for the screenshot sheet only: the layout box (outline, its edges are the margins), the glyph slot, the anchor (green),
            // the text start, the number slot's right edge, the unit, the end of the text slot, and the centre line.
            using (var anchorPen = new Pen(Color.FromArgb(220, 0, 200, 0), 1f))
                g.DrawLine(anchorPen, anchorX, 0, anchorX, rect.Height);
            using (var pen = new Pen(Color.FromArgb(150, 255, 0, 255), 1f))
            {
                foreach (var gx in new[] { glyphRight - m.Px(BarWidth), glyphRight, textX, numberRight, unitX, unitRight })
                    g.DrawLine(pen, gx, 0, gx, rect.Height);
                g.DrawLine(pen, rect.X, rhythm.OpticalCentre, rect.Right, rhythm.OpticalCentre);
                g.DrawRectangle(pen, rect.X, 0, rect.Width - 1, rect.Height - 1);
            }
        }
        g.PixelOffsetMode = offsetBefore;
    }

    /// <summary>A pill: a rectangle whose corners have the radius height / 2. A pill as wide as it is high is a circle: the dot is the bar at its shortest.</summary>
    private static GraphicsPath Pill(RectangleF r)
    {
        var p = new GraphicsPath();
        var d = Math.Min(r.Height, r.Width);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// The mark of a server, all from one rounded pill of one height. A reading server is the bar that fills with the progress of the prompt,
    /// coloured by how much of the prompt came from the cache, on a faint track (like the Mac menu bar item). A filled dot, the pill at its shortest, in the
    /// state colour is ready, writing or thinking. The states that are not busy have another shape as well as another colour: a hollow grey
    /// ring is offline, connecting or no model, a dotted ring is loading, a red dot with a white mark is an error.
    /// </summary>
    internal static void DrawGlyph(Graphics g, ReadoutItem item, RectangleF box, UiMetrics m, Palette palette)
    {
        // Shapes are placed at exact coordinates with PixelOffsetMode.Half; text, measured, lands exactly with Default.
        var savedOffset = g.PixelOffsetMode;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var state = palette.StateColor(item.State);
        switch (item.State)
        {
            case ServerState.Reading:
            {
                var track = Palette.Alpha(palette.HighContrast ? palette.Gray : Ink(palette), 0.25);
                using (var path = Pill(box))
                using (var brush = new SolidBrush(track))
                    g.FillPath(brush, path);
                var width = Math.Max(box.Height, (float)Math.Round(box.Width * Math.Max(0, Math.Min(1, item.Fraction ?? 0))));
                using (var path = Pill(new RectangleF(box.X, box.Y, width, box.Height)))
                using (var brush = new SolidBrush(item.Hit is CacheHit hit ? palette.HitColor(hit) : state))
                    g.FillPath(brush, path);
                break;
            }
            case ServerState.Offline:
            case ServerState.NoModel:
            case ServerState.Connecting:
            case ServerState.Loading:
            {
                // A ring of the same diameter: closed for "nothing there", four arcs for "on its way".
                var stroke = Math.Max(1f, m.Px(1) * 1.5f);
                var ring = RectangleF.Inflate(box, -stroke / 2, -stroke / 2);
                using (var pen = new Pen(state, stroke))
                {
                    if (item.State == ServerState.Loading)
                        for (int i = 0; i < 4; i++) g.DrawArc(pen, ring, i * 90 + 20, 50);
                    else g.DrawEllipse(pen, ring);
                }
                break;
            }
            case ServerState.Error:
            {
                using (var path = Pill(box)) using (var brush = new SolidBrush(state)) g.FillPath(brush, path);
                // A white exclamation mark: a bar and a point, a sixth of the glyph wide (at least a device pixel).
                var w = Math.Max(1f, (float)Math.Round(box.Height / 6));
                var x = box.X + (box.Width - w) / 2;
                using (var brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, x, box.Y + box.Height * 0.20f, w, box.Height * 0.36f);
                    g.FillRectangle(brush, x, box.Y + box.Height * 0.66f, w, Math.Max(1f, w));
                }
                break;
            }
            default:
                using (var path = Pill(box)) using (var brush = new SolidBrush(state)) g.FillPath(brush, path);
                break;
        }
        g.PixelOffsetMode = savedOffset;
    }
}
