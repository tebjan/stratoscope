using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>
/// Renders the panel, the taskbar readout and the icons to PNG files without opening a window
/// (the Windows counterpart of Screenshot.swift). Used for the README images and for checking the layout.
/// </summary>
internal static class SnapshotRenderer
{
    private const float Margin = 40f;
    private const float TaskbarHeight = 48f;

    private static float PanelHeight(PanelModel model, bool dark)
    {
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
            return new PanelPainter(g, Palette.For(dark), model).Paint();
    }

    /// <summary>The panel above a taskbar strip with the readout, on a soft backdrop. Opaque PNG.</summary>
    public static void Panel(string path, PanelModel model, IList<ReadoutItem> items, bool dark, float scale = 2f, int readoutHover = -1)
    {
        var panelH = PanelHeight(model, dark);
        var metrics = UiMetrics.At(96, 1.0, dark, dark);
        var readoutSize = ReadoutPainter.Measure(items, metrics);
        var width = Math.Max(PanelPainter.Width + 2 * Margin, readoutSize.Width + 2 * 24f + 40f);
        var height = Margin + panelH + 14f + TaskbarHeight;
        using (var bmp = new Bitmap((int)(width * scale), (int)(height * scale), PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        {
            bmp.SetResolution(96f * scale, 96f * scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.ScaleTransform(scale, scale);

            var top = dark ? Color.FromArgb(26, 31, 56) : Color.FromArgb(158, 179, 242);
            var bottom = dark ? Color.FromArgb(51, 26, 71) : Color.FromArgb(217, 199, 242);
            using (var brush = new LinearGradientBrush(new RectangleF(0, 0, width, height), top, bottom, 60f))
                g.FillRectangle(brush, 0, 0, width, height);

            // Taskbar strip with the readout centred in it, as it sits in the free gap of a real taskbar; the panel opens centred above it.
            var strip = new RectangleF(0, height - TaskbarHeight, width, TaskbarHeight);
            using (var brush = new SolidBrush(dark ? Color.FromArgb(235, 28, 28, 28) : Color.FromArgb(235, 243, 243, 243)))
                g.FillRectangle(brush, strip);
            using (var pen = new Pen(dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0), 1f))
                g.DrawLine(pen, 0, strip.Top, width, strip.Top);
            var readoutX = (width - readoutSize.Width) / 2f;
            var readoutBounds = new List<Rectangle>();
            using (var readout = ReadoutPainter.Render(items, metrics, readoutSize, readoutBounds, hover: readoutHover))
                g.DrawImage(readout, readoutX, strip.Top + (TaskbarHeight - readoutSize.Height) / 2f, readoutSize.Width, readoutSize.Height);

            // The panel, centred over the selected item, with a shadow and rounded corners.
            var sel = Math.Max(0, Math.Min(model.Selected, Math.Max(0, readoutBounds.Count - 1)));
            var centre = readoutBounds.Count > 0 ? readoutX + readoutBounds[sel].X + readoutBounds[sel].Width / 2f : width / 2f;
            var px = Math.Max(Margin / 2, Math.Min(centre - PanelPainter.Width / 2, width - PanelPainter.Width - Margin / 2));
            var panel = new RectangleF(px, Margin * 0.5f, PanelPainter.Width, panelH);
            var palette = Palette.For(dark);
            for (int i = 6; i >= 1; i--)   // a soft shadow without a blur filter
                using (var shadow = Shapes.RoundedRect(new RectangleF(panel.X - i, panel.Y - i + 6, panel.Width + 2 * i, panel.Height + 2 * i), 8 + i))
                using (var brush = new SolidBrush(Color.FromArgb(dark ? 22 : 12, 0, 0, 0)))
                    g.FillPath(brush, shadow);
            using (var shape = Shapes.RoundedRect(panel, 8))
            {
                using (var brush = new SolidBrush(palette.Background)) g.FillPath(brush, shape);
                var state = g.Save();
                g.SetClip(shape);
                g.TranslateTransform(panel.X, panel.Y);
                new PanelPainter(g, palette, model).Paint();
                g.Restore(state);
                using (var pen = new Pen(palette.Faint(0.18), 1f)) g.DrawPath(pen, shape);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>
    /// Tooltips of the panel as the user sees them: the panel with the pointer on a bar (the same request is outlined in both charts and in
    /// the dot row), on a dot, on the context bar and on a system row, each with its tooltip. Four crops stacked. Opaque PNG.
    /// </summary>
    public static void TooltipsSheet(string path, PanelModel model, bool dark)
    {
        const float scale = 2f;
        var palette = Palette.For(dark);
        float height;
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
            height = new PanelPainter(g, palette, model).Paint();
        var targets = new[] { (TipKind.Request, 3), (TipKind.CacheResult, 3), (TipKind.ContextRequest, 3), (TipKind.Context, 0), (TipKind.Ram, 0) };
        var crops = new List<Bitmap>();
        try
        {
            foreach (var (kind, index) in targets)
            {
                model.HoverRequest = PanelPainter.IsRequestTip(kind) ? index : -1;
                using (var full = new Bitmap((int)(PanelPainter.Width * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format24bppRgb))
                using (var g = Graphics.FromImage(full))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(palette.Background);
                    g.ScaleTransform(scale, scale);
                    var painter = new PanelPainter(g, palette, model);
                    painter.Paint();
                    var area = painter.Tips.First(t => t.Kind == kind && t.Index == index && (kind != TipKind.CacheResult || t.Rect.Width < 20));
                    var text = painter.TipText(area) ?? "";
                    var tip = TooltipPainter.Measure(text, scale);
                    var top = (int)Math.Max(0, (area.Rect.Top - 24) * scale);
                    var bottom = (int)(area.Rect.Bottom * scale + 12 * scale + tip.Height + 20 * scale);
                    var crop = new Bitmap(full.Width, bottom - top, PixelFormat.Format24bppRgb);
                    using (var cg = Graphics.FromImage(crop))
                    {
                        cg.Clear(palette.Background);
                        cg.DrawImage(full, new Rectangle(0, 0, full.Width, Math.Min(full.Height - top, crop.Height)), new Rectangle(0, top, full.Width, Math.Min(full.Height - top, crop.Height)), GraphicsUnit.Pixel);
                        var x = (int)Math.Min(area.Rect.Left * scale + 24 * scale, full.Width - tip.Width - 8 * scale);
                        var y = (int)(area.Rect.Bottom * scale + 12 * scale) - top;
                        using (var tipBmp = TipWindow.Render(text, palette, scale)) { var shadowPx = (int)Math.Round(8 * scale); cg.DrawImage(tipBmp, Math.Max(8, x) - shadowPx, y - shadowPx); }
                    }
                    crops.Add(crop);
                }
            }
            Stack(path, crops, 8);
        }
        finally { foreach (var c in crops) c.Dispose(); }
    }

    /// <summary>
    /// Rest, hover and pressed side by side at 100 % for both themes: a panel row, a bar of the speed chart and a readout item. For judging
    /// the step between the states. Opaque PNG.
    /// </summary>
    public static void HoverSheet(string path, PanelModel template)
    {
        var rows = new List<Bitmap>();
        try
        {
            foreach (var dark in new[] { true, false })
            {
                var palette = Palette.For(dark);
                var metrics = UiMetrics.At(96, 1.0, dark, dark);
                var cells = new List<Bitmap>();
                for (int state = 0; state < 3; state++)
                {
                    // A panel row and a speed chart bar, cut out of the real panel.
                    template.Hover = state == 1 ? 0 : -1; template.Pressed = state == 2 ? 0 : -1; template.HoverRequest = state == 0 ? -1 : 3;
                    float height;
                    PanelPainter painter;
                    using (var scratch = new Bitmap(1, 1))
                    using (var g = Graphics.FromImage(scratch)) { painter = new PanelPainter(g, palette, template); height = painter.Paint(); }
                    using (var full = new Bitmap((int)PanelPainter.Width, (int)Math.Ceiling(height), PixelFormat.Format24bppRgb))
                    using (var g = Graphics.FromImage(full))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                        g.Clear(palette.Background);
                        var p = new PanelPainter(g, palette, template); p.Paint();
                        var row = p.Hits[0].Area;
                        var bar = p.Tips.First(t => t.Kind == TipKind.Request && t.Index == 3).Rect;
                        var cell = new Bitmap((int)PanelPainter.Width, 44 + 66, PixelFormat.Format24bppRgb);
                        using (var cg = Graphics.FromImage(cell))
                        {
                            cg.Clear(palette.Background);
                            cg.DrawImage(full, new Rectangle(0, 0, cell.Width, 62), new Rectangle(0, (int)bar.Top - 14, cell.Width, 62), GraphicsUnit.Pixel);
                            cg.DrawImage(full, new Rectangle(0, 66, cell.Width, 44), new Rectangle(0, (int)row.Top - 6, cell.Width, 44), GraphicsUnit.Pixel);
                        }
                        cells.Add(cell);
                    }
                    var items = Items(("Workstation", ServerState.Writing, StateSnap(EngineStatus.Decode)));
                    var size = ReadoutPainter.Measure(items, metrics);
                    var strip = new Bitmap((int)PanelPainter.Width, size.Height, PixelFormat.Format24bppRgb);
                    using (var sg = Graphics.FromImage(strip))
                    using (var one = ReadoutPainter.Render(items, metrics, size, null, hover: state == 1 ? 0 : -1, pressed: state == 2 ? 0 : -1))
                    {
                        sg.Clear(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));
                        sg.DrawImage(one, 8, 0);
                    }
                    cells.Add(strip);
                }
                // cells: panel cut-out and readout strip for each of the three states, laid out as three columns.
                var columnWidth = (int)PanelPainter.Width;
                var rowHeight = cells[0].Height + cells[1].Height + 8;
                var sheetRow = new Bitmap(3 * columnWidth + 16, rowHeight, PixelFormat.Format24bppRgb);
                using (var rg = Graphics.FromImage(sheetRow))
                {
                    rg.Clear(Color.FromArgb(128, 128, 128));
                    for (int state = 0; state < 3; state++)
                    {
                        rg.DrawImage(cells[2 * state], state * (columnWidth + 8), 0);
                        rg.DrawImage(cells[2 * state + 1], state * (columnWidth + 8), cells[0].Height + 8);
                    }
                }
                foreach (var c in cells) c.Dispose();
                rows.Add(sheetRow);
            }
            Stack(path, rows, 8);
        }
        finally { foreach (var r in rows) r.Dispose(); }
    }

    /// <summary>
    /// The readout at 100, 125, 150 and 200 % cut to its first 150 px and enlarged 400 % without smoothing, to judge the sharpness of the text:
    /// above the old rendering (grey-scale outlines, no contrast curve), below the current one (grid-fitted, with the alpha curve). Dark taskbar.
    /// </summary>
    public static void ZoomSheet(string path)
    {
        var rows = new List<Bitmap>();
        var keptHint = ReadoutPainter.TextHint; var keptContrast = ReadoutPainter.TextContrast;
        try
        {
            foreach (var dpi in new[] { 96, 120, 144, 192 })
                foreach (var after in new[] { false, true })
                {
                    ReadoutPainter.TextHint = after ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;
                    ReadoutPainter.TextContrast = after ? keptContrast : 0;
                    var m = UiMetrics.At(dpi, 1.0, true, true);
                    var items = StateRows()[1].Items;       // writing: a number, a dot, two lines
                    var size = ReadoutPainter.Measure(items, m);
                    using (var one = ReadoutPainter.Render(items, m, size))
                    {
                        var w = Math.Min(size.Width, (int)(150 * dpi / 96.0));
                        var row = new Bitmap(w * 4, size.Height * 4, PixelFormat.Format24bppRgb);
                        using (var g = Graphics.FromImage(row))
                        {
                            g.Clear(Color.FromArgb(32, 32, 32));
                            g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(one, new Rectangle(0, 0, row.Width, row.Height), new Rectangle(0, 0, w, size.Height), GraphicsUnit.Pixel);
                        }
                        rows.Add(row);
                    }
                }
            Stack(path, rows, 6);
        }
        finally { ReadoutPainter.TextHint = keptHint; ReadoutPainter.TextContrast = keptContrast; foreach (var r in rows) r.Dispose(); }
    }

    /// <summary>
    /// The readout in every state at 100, 125, 150 and 200 % (dark, light, dark, light), side by side, with thin guide lines at the layout box
    /// (outline), the glyph slot, the value and the centre line: nothing but the glyph and the speed inside the slots changes from row to row.
    /// Then speeds of one to four digits, and two servers, each with its name before the glyph. Opaque PNG.
    /// </summary>
    public static void SlotsSheet(string path, bool numbers = false)
    {
        var keptGuides = ReadoutPainter.DebugSlots;
        var columns = new List<Bitmap>();
        try
        {
            ReadoutPainter.DebugSlots = true;
            foreach (var (dpi, dark) in numbers ? new[] { (96, true), (96, false), (144, true), (144, false) } : new[] { (96, true), (120, false), (144, true), (192, false) })
            {
                var all = StateRows();
                if (numbers)
                {
                    // The number slot has four places: one to four digits, right-aligned, while a prompt is read (the bar) and while tokens are written (the dot).
                    var valueRows = new List<IList<ReadoutItem>>();
                    foreach (var state in new[] { ServerState.Reading, ServerState.Writing })
                        foreach (var value in new[] { "9", "99", "999", "3345", "9999" })
                            valueRows.Add(new[] { new ReadoutItem("Workstation", state, new ReadoutText(value, "t/s"), 0.57, "", null, false) });
                    columns.Add(ReadoutColumn(valueRows, UiMetrics.At(dpi, 1.0, dark, dark), dark));
                    continue;
                }
                var rows = new[] { "ready", "writing", "reading", "thinking", "loading", "no model", "offline", "error" }.Select(l => (IList<ReadoutItem>)all.First(r => r.Label == l).Items).ToList();
                rows.Add(Items(("Workstation", ServerState.Reading, StateSnap(EngineStatus.Prefill)), ("Mac", ServerState.Ready, StateSnap(EngineStatus.Idle))));
                rows.Add(Items(("Workstation", ServerState.Writing, StateSnap(EngineStatus.Decode)), ("Mac", ServerState.Offline, null)));
                rows.Add(Items(("Workstation", ServerState.Loading, StateSnap(EngineStatus.Loading)), ("Mac", ServerState.Error, StateSnap(EngineStatus.Error))));
                columns.Add(ReadoutColumn(rows, UiMetrics.At(dpi, 1.0, dark, dark), dark));
            }
            var sheet = new Bitmap(columns.Sum(c => c.Width) + 4 * (columns.Count + 1), columns.Max(c => c.Height) + 8, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(128, 128, 128));
                var x = 4;
                foreach (var c in columns) { g.DrawImage(c, x, 4); x += c.Width + 4; }
            }
            sheet.Save(path, ImageFormat.Png); sheet.Dispose();
        }
        finally { ReadoutPainter.DebugSlots = keptGuides; foreach (var c in columns) c.Dispose(); }
    }

    /// <summary>One readout per row on a taskbar background, every row at the same size (the widest), for the sheets.</summary>
    private static Bitmap ReadoutColumn(IList<IList<ReadoutItem>> rows, UiMetrics m, bool dark)
    {
        var sizes = rows.Select(r => ReadoutPainter.Measure(r, m)).ToList();
        var width = sizes.Max(s => s.Width); var height = sizes.Max(s => s.Height);
        var gap = 4;
        var column = new Bitmap(width + 2 * m.Px(8), rows.Count * (height + gap) + gap, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(column))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            for (int r = 0; r < rows.Count; r++)
            {
                var y = gap + r * (height + gap);
                using (var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243))) g.FillRectangle(back, 0, y, column.Width, height);
                using (var one = ReadoutPainter.Render(rows[r], m, sizes[r])) g.DrawImage(one, m.Px(8), y);
            }
        }
        return column;
    }

    /// <summary>
    /// The glyph heights compared: (a) bar and dot both 6, (b) both 5, (c) the bar 4 and the dot 8 (the reference of the earlier design), in the
    /// states reading, writing, ready, no model, loading and error, at 100 and 150 %, dark and light. Opaque PNG.
    /// </summary>
    public static void GlyphSizesSheet(string path)
    {
        var strips = new List<Bitmap>();
        var kept = ReadoutPainter.GlyphHeightOverride;
        try
        {
            var all = StateRows();
            var items = new[] { "reading", "writing", "ready", "no model", "loading", "error" }
                .Select(l => { var i = all.First(r => r.Label == l).Items[0]; return new ReadoutItem(i.Name, i.State, i.Text, i.Fraction, i.Spoken, null, false); }).ToList();
            using (var label = new Font("Segoe UI", 11, GraphicsUnit.Pixel))
                foreach (var (name, bar, dot) in new[] { ("a: bar and dot 6", 6f, 6f), ("b: bar and dot 5", 5f, 5f), ("c: bar 4, dot 8 (reference)", 4f, 8f) })
                {
                    ReadoutPainter.GlyphHeightOverride = (bar, dot);
                    foreach (var (dpi, dark) in new[] { (96, true), (96, false), (144, true), (144, false) })
                    {
                        var m = UiMetrics.At(dpi, 1.0, dark, dark);
                        var size = ReadoutPainter.Measure(items, m);
                        var strip = new Bitmap(size.Width + 220, size.Height, PixelFormat.Format24bppRgb);
                        using (var g = Graphics.FromImage(strip))
                        {
                            g.Clear(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));
                            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                            g.DrawString(name + ", " + dpi * 100 / 96 + " %", label, new SolidBrush(dark ? Color.White : Color.Black), 8, size.Height / 2 - 7);
                            using (var one = ReadoutPainter.Render(items, m, size)) g.DrawImage(one, 220, 0);
                        }
                        strips.Add(strip);
                    }
                }
            Stack(path, strips, 4);
        }
        finally { ReadoutPainter.GlyphHeightOverride = kept; foreach (var s in strips) s.Dispose(); }
    }

    /// <summary>The speed charts with 1, 3, 6, 8 and 12 finished requests, one above the other. Opaque PNG.</summary>
    public static void ChartsSheet(string path, PanelModel template, bool dark)
    {
        const float scale = 2f;
        var palette = Palette.For(dark);
        var crops = new List<Bitmap>();
        try
        {
            foreach (var requests in new[] { 1, 3, 6, 8, 12 })
            {
                var snapshot = new Stratoscope.Core.Engines.Demo.DemoProvider("idle").Snapshot();
                snapshot.Recent = snapshot.Recent.Take(requests).ToList();
                var model = new PanelModel { Snapshot = snapshot, State = ServerState.Ready, ServerName = template.ServerName, Now = template.Now };
                float height;
                using (var scratch = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(scratch)) height = new PanelPainter(g, palette, model).Paint();
                using (var full = new Bitmap((int)(PanelPainter.Width * scale), (int)Math.Ceiling(height * scale), PixelFormat.Format24bppRgb))
                using (var g = Graphics.FromImage(full))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(palette.Background); g.ScaleTransform(scale, scale);
                    var painter = new PanelPainter(g, palette, model); painter.Paint();
                    var bar = painter.Tips.First(t => t.Kind == TipKind.Request && t.Rect.Width < 40).Rect;
                    var crop = new Bitmap(full.Width, (int)(76 * scale), PixelFormat.Format24bppRgb);
                    using (var cg = Graphics.FromImage(crop))
                        cg.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), new Rectangle(0, (int)((bar.Top - 26) * scale), crop.Width, crop.Height), GraphicsUnit.Pixel);
                    crops.Add(crop);
                }
            }
            Stack(path, crops, 6);
        }
        finally { foreach (var c in crops) c.Dispose(); }
    }

    // MARK: The taskbar mock

    /// <summary>How the mock taskbar is set up.</summary>
    internal sealed class StripOptions
    {
        public bool Centred { get; set; } = true;
        public bool Widgets { get; set; }
        public int Buttons { get; set; } = 7;
        public ReadoutPosition Position { get; set; } = ReadoutPosition.Center;
        public int? WidthLimit { get; set; }
        public int HoverIndex { get; set; } = -1;
        public int PressedIndex { get; set; } = -1;
    }

    /// <summary>
    /// A mock taskbar at a display scale with the readout placed by the same rules as the real one (TaskbarLayout.Place): start, search
    /// and task buttons (centred or left-aligned), an optional widgets button, the notification area, and the readout. Opaque.
    /// </summary>
    internal static Bitmap Strip(IList<ReadoutItem> items, UiMetrics m, StripOptions o, int width)
    {
        var height = m.Px(TaskbarHeight);
        var dark = m.SystemDark;
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));
            var glyph = m.Px(24); var step = m.Px(40);
            var buttons = new List<Rectangle>();
            var cluster = o.Buttons * step;
            var x0 = o.Centred ? (width - cluster) / 2 : m.Px(12);
            for (int i = 0; i < o.Buttons; i++) buttons.Add(new Rectangle(x0 + i * step, (height - m.Px(40)) / 2, step - m.Px(4), m.Px(40)));
            if (o.Widgets && o.Centred) buttons.Add(new Rectangle(m.Px(12), (height - m.Px(40)) / 2, m.Px(40), m.Px(40)));
            var notification = new Rectangle(width - m.Px(150), 0, m.Px(150), height);

            using (var brush = new SolidBrush(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0)))
            {
                foreach (var b in buttons) g.FillRoundedRectangle(brush, new Rectangle(b.X + (b.Width - glyph) / 2, b.Y + (b.Height - glyph) / 2, glyph, glyph), m.Px(5));
                for (int i = 0; i < 3; i++) g.FillRoundedRectangle(brush, new Rectangle(notification.Left + m.Px(14) + i * m.Px(30), (height - m.Px(16)) / 2, m.Px(16), m.Px(16)), m.Px(4));
            }

            var size = ReadoutPainter.Measure(items, m);
            if (o.WidthLimit.HasValue) size.Width = Math.Min(size.Width, o.WidthLimit.Value);
            var geometry = new TaskbarLayout.Geometry { Bounds = new Rectangle(0, 0, width, height), Notification = notification, Buttons = buttons, Dpi = m.Dpi };
            var place = TaskbarLayout.Place(geometry, o.Position, size);
            if (place != null)
            {
                var list = new List<Rectangle>();
                using (var readout = ReadoutPainter.Render(items, m, place.Value.Size, list, hover: o.HoverIndex, pressed: o.PressedIndex))
                    g.DrawImage(readout, place.Value.Location);
            }
        }
        return bmp;
    }

    private static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle r, int radius)
    {
        using (var p = Shapes.RoundedRect(r, radius)) g.FillPath(brush, p);
    }

    /// <summary>The readout over a mock taskbar at one display scale. Opaque PNG at that scale.</summary>
    public static void Readout(string path, IList<ReadoutItem> items, float scale, bool dark, int? widthLimit = null, StripOptions? options = null)
    {
        var m = UiMetrics.At((int)Math.Round(96 * scale), 1.0, dark, dark);
        var o = options ?? new StripOptions();
        if (widthLimit.HasValue) o.WidthLimit = widthLimit;
        var width = Math.Max(ReadoutPainter.Measure(items, m).Width + m.Px(150) + m.Px(500), m.Px(900));
        using (var strip = Strip(items, m, o, width))
            strip.Save(path, ImageFormat.Png);
    }

    // MARK: Sheets

    internal static List<ReadoutItem> Items(params (string Name, ServerState State, EngineSnapshot? Snapshot)[] servers) => Items(SpeedScale.DefaultMax, servers);

    internal static List<ReadoutItem> Items(double scaleMax, params (string Name, ServerState State, EngineSnapshot? Snapshot)[] servers) =>
        servers.Select(s =>
        {
            var polled = s.State != ServerState.Connecting;
            var nameInCaption = servers.Length > 1;      // one server: the glyph and the speed alone; several: the name stands before the glyph
            var text = Display.Readout(s.Snapshot);
            var progress = s.Snapshot != null ? Display.Progress(s.Snapshot) : null;
            return new ReadoutItem(s.Name, s.State, text, progress?.Fraction, Display.Spoken(s.Name, s.Snapshot, polled, s.State == ServerState.Error && s.Snapshot == null), null, nameInCaption, progress?.Hit,
                RequestBars.From(s.Snapshot), scaleMax);
        }).ToList();

    /// <summary>One server in each state, as the readout shows it.</summary>
    internal static List<(string Label, List<ReadoutItem> Items)> StateRows(string name = "Workstation")
    {
        EngineSnapshot Snap(EngineStatus status, Action<EngineSnapshot>? tweak = null)
        {
            var s = new EngineSnapshot("Strata", "m", status)
            {
                Live = status == EngineStatus.Idle || status == EngineStatus.NoModel || status == EngineStatus.Loading ? null : new LiveRequest
                {
                    PromptRead = 57_840, PromptTotal = 101_000, Generated = 1_840, MaxTokens = 24_576, ElapsedSeconds = 94,
                    PrefillTokensPerSecond = 247, DecodeTokensPerSecond = 26.4, ReusedEstimate = 41_000,
                },
                Info = new EngineInfo { BatchSlots = 4 },
            };
            tweak?.Invoke(s);
            return s;
        }
        var rows = new List<(string, List<ReadoutItem>)>
        {
            ("reading", Items((name, ServerState.Reading, Snap(EngineStatus.Prefill)))),
            ("writing", Items((name, ServerState.Writing, Snap(EngineStatus.Decode)))),
            ("thinking", Items((name, ServerState.Thinking, Snap(EngineStatus.Thinking)))),
            ("ready", Items((name, ServerState.Ready, Snap(EngineStatus.Idle)))),
            ("no model", Items((name, ServerState.NoModel, Snap(EngineStatus.NoModel)))),
            ("loading", Items((name, ServerState.Loading, Snap(EngineStatus.Loading)))),
            ("offline", Items((name, ServerState.Offline, null))),
            ("error", Items((name, ServerState.Error, Snap(EngineStatus.Error)))),
        };
        return rows;
    }

    /// <summary>Every state, one item per row, on dark and light taskbars at 150 %, with the hover and pressed look. Opaque PNG.</summary>
    public static void StatesSheet(string path)
    {
        var rows = StateRows();
        // Hover and pressed get their own rows, so the state rows show the plain look.
        var hoverRow = rows.Count; rows.Add(("hover", rows[1].Items));
        var pressedRow = rows.Count; rows.Add(("pressed", rows[1].Items));
        var hoverIdleRow = rows.Count; rows.Add(("hover, glyph only", rows[3].Items));
        var m = UiMetrics.At(144, 1.0, true, true);
        var w = m.Px(330); var h = m.Px(TaskbarHeight);
        var labelW = m.Px(150);
        using (var bmp = new Bitmap(2 * (labelW + w) + m.Px(24), rows.Count * (h + m.Px(6)) + m.Px(8), PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var font = Typography.Regular(m.Px(12)))
                for (int c = 0; c < 2; c++)
                {
                    var dark = c == 0;
                    var mm = UiMetrics.At(144, 1.0, dark, dark);
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var x = c * (labelW + w + m.Px(8)); var y = m.Px(4) + r * (h + m.Px(6));
                        using (var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243))) g.FillRectangle(back, x, y, labelW + w, h);
                        g.DrawString(rows[r].Label, font, new SolidBrush(dark ? Color.White : Color.Black), x + m.Px(8), y + h / 2 - m.Px(8));
                        var size = new Size(Math.Min(w, ReadoutPainter.Measure(rows[r].Items, mm).Width), h);
                        using (var item = ReadoutPainter.Render(rows[r].Items, mm, size, null, hover: r == hoverRow || r == hoverIdleRow ? 0 : -1, pressed: r == pressedRow ? 0 : -1))
                            g.DrawImage(item, x + labelW, y);
                    }
                }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>
    /// Parallel requests in the readout: one running request that reads and one that writes, then 2, 4 and 8 requests (the first of them
    /// reading, the others writing at speeds below the scale of 0 to 125 t/s), on dark and light taskbars at 150 %. Opaque PNG.
    /// </summary>
    public static void ParallelSheet(string path, int dpi = 144)
    {
        const double scaleMax = 125;
        List<ReadoutItem> Row(int count, int readers)
        {
            var speeds = new[] { 118.0, 96, 82, 74, 61, 47, 33, 21 };
            var slots = Enumerable.Range(0, count).Select(i => i < readers
                ? new BatchSlot { Index = i, State = "reading", PromptTokens = 60_000, PromptRead = 38_000 }
                : new BatchSlot { Index = i, State = "decoding", Generated = 900, TokensPerSecond = speeds[i] }).ToList();
            var reading = count == 1 && readers == 1;
            var total = slots.Where(s => s.IsDecoding).Sum(s => s.TokensPerSecond ?? 0);
            var snapshot = new EngineSnapshot("Strata", "m", reading ? EngineStatus.Prefill : EngineStatus.Decode)
            {
                Slots = slots,
                Live = reading
                    ? new LiveRequest { PromptRead = 38_000, PromptTotal = 60_000, PrefillTokensPerSecond = 247, ReusedEstimate = 41_000 }
                    : new LiveRequest { PrefillTokensPerSecond = null, DecodeTokensPerSecond = count == 1 ? speeds[0] : total, Running = count > 1 ? count : null },
            };
            return Items(scaleMax, ("Workstation", reading ? ServerState.Reading : ServerState.Writing, snapshot));
        }
        var rows = new List<(string Label, List<ReadoutItem> Items)>
        {
            ("1 reading", Row(1, 1)), ("1 writing", Row(1, 0)), ("2: 1 reading", Row(2, 1)), ("3: 1 reading", Row(3, 1)), ("4: 1 reading", Row(4, 1)), ("5: 1 reading", Row(5, 1)), ("6: 2 reading", Row(6, 2)), ("7: 1 reading", Row(7, 1)), ("8: 2 reading", Row(8, 2)), ("8 writing", Row(8, 0)),
        };
        var m = UiMetrics.At(dpi, 1.0, true, true);
        var w = m.Px(330); var h = m.Px(TaskbarHeight);
        var labelW = m.Px(150);
        using (var bmp = new Bitmap(2 * (labelW + w) + m.Px(24), rows.Count * (h + m.Px(6)) + m.Px(8), PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var font = Typography.Regular(m.Px(12)))
                for (int c = 0; c < 2; c++)
                {
                    var dark = c == 0;
                    var mm = UiMetrics.At(dpi, 1.0, dark, dark);
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var x = c * (labelW + w + m.Px(8)); var y = m.Px(4) + r * (h + m.Px(6));
                        using (var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243))) g.FillRectangle(back, x, y, labelW + w, h);
                        using (var ink = new SolidBrush(dark ? Color.White : Color.Black)) g.DrawString(rows[r].Label, font, ink, x + m.Px(8), y + h / 2 - m.Px(8));
                        var size = new Size(Math.Min(w, ReadoutPainter.Measure(rows[r].Items, mm).Width), h);
                        using (var item = ReadoutPainter.Render(rows[r].Items, mm, size))
                            g.DrawImage(item, x + labelW, y);
                    }
                }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>The reading state at 100, 125, 150, 175, 200 and 250 % on dark and light taskbars. Opaque PNG at 100 %, scaled items drawn at their own scale.</summary>
    public static void ScalesSheet(string path, IList<int>? dpis = null)
    {
        var scales = (dpis ?? new[] { 96, 120, 144, 168, 192, 240 }).ToArray();
        var items = StateRows()[0].Items;
        var strips = new List<Bitmap>();
        try
        {
            foreach (var dpi in scales)
                foreach (var dark in new[] { true, false })
                {
                    var m = UiMetrics.At(dpi, 1.0, dark, dark);
                    var two = items.Concat(StateRows()[3].Items).ToList();
                    var size = ReadoutPainter.Measure(two, m);
                    using (var one = ReadoutPainter.Render(two, m, size))
                    {
                        var bmp = new Bitmap(size.Width + m.Px(40), size.Height, PixelFormat.Format24bppRgb);
                        using (var g = Graphics.FromImage(bmp)) { g.Clear(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243)); g.DrawImage(one, m.Px(20), 0); }
                        strips.Add(bmp);
                    }
                }
            Stack(path, strips, 4);
        }
        finally { foreach (var s in strips) s.Dispose(); }
    }

    /// <summary>1, 2 and 3 servers, long names, centred and left-aligned taskbars, the three readout positions, 100 % and 150 %. Opaque PNG.</summary>
    public static void LayoutsSheet(string path)
    {
        var strips = new List<Bitmap>();
        try
        {
            var one = StateRows()[0].Items;
            var two = Items(("Workstation", ServerState.Reading, StateSnap(EngineStatus.Prefill)), ("Mac", ServerState.Writing, StateSnap(EngineStatus.Decode)));
            var three = Items(("Workstation", ServerState.Reading, StateSnap(EngineStatus.Prefill)), ("Mac", ServerState.Ready, StateSnap(EngineStatus.Idle)), ("Server", ServerState.Offline, null));
            var longNames = Items(("Workstation in the basement lab (Strata)", ServerState.Reading, StateSnap(EngineStatus.Prefill)), ("Laptop on the second floor", ServerState.Writing, StateSnap(EngineStatus.Decode)));
            var cases = new List<(IList<ReadoutItem> Items, StripOptions Options, int Dpi, bool Dark)>
            {
                (one, new StripOptions(), 96, true), (two, new StripOptions(), 96, true), (three, new StripOptions(), 96, false), (longNames, new StripOptions { WidthLimit = 520 }, 96, true),
                (two, new StripOptions { Centred = false }, 96, true), (two, new StripOptions { Position = ReadoutPosition.AfterButtons }, 96, false),
                (two, new StripOptions { Position = ReadoutPosition.Left, Widgets = true }, 96, true), (two, new StripOptions { Centred = false, Position = ReadoutPosition.AfterButtons, Buttons = 12 }, 96, false),
                (three, new StripOptions { Widgets = true }, 144, true), (longNames, new StripOptions { WidthLimit = 640, Position = ReadoutPosition.AfterButtons }, 144, false),
            };
            foreach (var c in cases)
            {
                var m = UiMetrics.At(c.Dpi, 1.0, c.Dark, c.Dark);
                strips.Add(Strip(c.Items, m, c.Options, m.Px(1440)));
            }
            Stack(path, strips, 4);
        }
        finally { foreach (var s in strips) s.Dispose(); }
    }

    internal static EngineSnapshot StateSnap(EngineStatus status)
    {
        var s = new EngineSnapshot("Strata", "m", status)
        {
            Live = status == EngineStatus.Idle ? null : new LiveRequest
            {
                PromptRead = 57_840, PromptTotal = 101_000, Generated = 1_840, MaxTokens = 24_576, ElapsedSeconds = 94,
                PrefillTokensPerSecond = 247, DecodeTokensPerSecond = 26.4, ReusedEstimate = 41_000,
            },
            Info = new EngineInfo { BatchSlots = 4 },
        };
        return s;
    }

    internal static void Stack(string path, List<Bitmap> strips, int gap)
    {
        var w = strips.Max(s => s.Width);
        var h = strips.Sum(s => s.Height + gap);
        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            var y = 0;
            foreach (var s in strips) { g.DrawImage(s, 0, y); y += s.Height + gap; }
            bmp.Save(path, ImageFormat.Png);
        }
    }

}
