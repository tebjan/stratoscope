using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using Stratoscope.Core;

namespace Stratoscope.Windows;

internal sealed class TabInfo
{
    public TabInfo(string name, ServerState state, string brief) { Name = name; State = state; Brief = brief; }
    public string Name { get; }
    public ServerState State { get; }
    public string Brief { get; }
}

/// <summary>What the settings page of the panel shows.</summary>
internal sealed class PanelSettings
{
    public bool StartWithWindows { get; set; }
    public ThemeChoice Theme { get; set; }
    /// <summary>Things in the settings file that could not be used, in English.</summary>
    public List<string> Problems { get; } = new List<string>();
}

/// <summary>Everything the panel shows, as plain data, so it can be drawn on screen or offscreen.</summary>
internal sealed class PanelModel
{
    /// <summary>Not null: the panel shows the settings page instead of the status.</summary>
    public PanelSettings? Settings { get; set; }
    public List<TabInfo> Tabs { get; } = new List<TabInfo>();
    public int Selected { get; set; }
    public string ServerName { get; set; } = "";
    public ServerState State { get; set; } = ServerState.Connecting;
    public EngineSnapshot? Snapshot { get; set; }
    /// <summary>Why the selected server does not answer.</summary>
    public string? Error { get; set; }
    public string VersionLabel { get; set; } = "Stratoscope";
    public DateTime Now { get; set; } = DateTime.UtcNow;
    /// <summary>The row the mouse is over (index into the hit list), or -1.</summary>
    public int Hover { get; set; } = -1;
    /// <summary>The row being pressed, or -1.</summary>
    public int Pressed { get; set; } = -1;
    /// <summary>The row that has the keyboard focus, or -1.</summary>
    public int Focus { get; set; } = -1;
    /// <summary>The system accent colour, for the focus ring.</summary>
    public Color? Accent { get; set; }
    /// <summary>The finished request under the mouse (index into the newest-first list), or -1. The same request is marked in both charts and in the dot row.</summary>
    public int HoverRequest { get; set; } = -1;
}

/// <summary>What a tooltip of the panel is about.</summary>
internal enum TipKind { Server, Request, CacheResult, Context, ContextRequest, Ram, Gpu, Vram, Cpu, ConversationCache, CacheSlot, BatchSlot, Engine, Totals }

/// <summary>An area of the panel with a data tooltip. The text is built on hover (<see cref="PanelPainter.TipText"/>), never while painting.</summary>
internal readonly record struct TipArea(RectangleF Rect, TipKind Kind, int Index);

internal enum PanelAction { SelectTab, OpenLink, Settings, SettingsBack, ToggleStartup, SetTheme, OpenSettingsFile, ReloadSettings, CheckForUpdates, Quit }

internal sealed class PanelHit
{
    public PanelHit(RectangleF area, PanelAction action, int index, bool highlight) { Area = area; Action = action; Index = index; Highlight = highlight; }
    public RectangleF Area { get; }
    public PanelAction Action { get; }
    public int Index { get; }
    /// <summary>Rows (not tabs) get the hover highlight.</summary>
    public bool Highlight { get; }
}

/// <summary>
/// Draws the panel. A port of Panels.swift: same width, sections, order, wording, colours and coordinates.
/// All coordinates are logical pixels; the caller sets the Graphics scale for the monitor.
/// </summary>
internal sealed class PanelPainter
{
    public const float Width = 352f;
    // Heights of the blocks, in epx. All multiples of the 4 epx grid; the Mac menu has 158, 104, 38 and 50 here.
    /// <summary>The fewest slots a speed chart has. With fewer finished requests the older slots are empty track on the left.</summary>
    internal const int MinChartSlots = 6;
    // The type ramp (Typography): Body for every label and value, Caption for the small secondary text, Model for the model name.
    private const float Body = Typography.BodyEpx, Caption = Typography.CaptionEpx, Model = Typography.ModelEpx;
    internal const float SpeedHeight = 96f, CacheHeadHeight = 84f, CacheRowHeight = 36f, TotalsHeight = 52f, SystemRowPitch = 28f, TabHeight = 28f, EdgePadding = 8f;
    private const float Pad = 16f;
    private const float SepHeight = 8f;
    private const float RowHeight = 24f;

    private readonly Graphics g;
    private readonly Palette pal;
    private readonly PanelModel model;
    private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    private static readonly string BoldFamily = FontFamily.Families.Any(f => f.Name == "Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";

    public List<PanelHit> Hits { get; } = new List<PanelHit>();
    /// <summary>The sections of the last <see cref="Paint"/> with their top and bottom, in logical pixels. The offscreen trace and the tests read them.</summary>
    public List<(string Name, float Top, float Bottom)> Sections { get; } = new List<(string, float, float)>();

    public PanelPainter(Graphics graphics, Palette palette, PanelModel panel)
    {
        g = graphics; pal = palette; model = panel;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    // MARK: Drawing helpers (Drawing.swift)

    private Font F(float size, bool bold)
    {
        var key = size.ToString(CultureInfo.InvariantCulture) + (bold ? "b" : "r");
        if (!fonts.TryGetValue(key, out var font))
        {
            font = bold && BoldFamily == "Segoe UI Semibold"
                ? new Font(BoldFamily, size, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            fonts[key] = font;
        }
        return font;
    }

    private float TextWidth(string s, Font f) => g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;

    private void T(string s, float x, float y, float size = Body, bool bold = false, Color? color = null, float? right = null)
    {
        var f = F(size, bold);
        var px = right.HasValue ? right.Value - TextWidth(s, f) : x;
        Texts.Add(s);
        // Coloured text is held to 4.5:1 on the background; the state colours of dots and bars only need 3:1.
        using (var brush = new SolidBrush(color.HasValue ? pal.TextOf(color.Value) : pal.Label))
            g.DrawString(s, f, brush, px, y - 1.5f, StringFormat.GenericTypographic);
    }

    /// <summary>Every text the painter drew, in order: what the user can read. Used by the tests that check the wording.</summary>
    public List<string> Texts { get; } = new List<string>();
    /// <summary>Areas with a tooltip (and an accessible description), in the panel's logical coordinates.</summary>
    public List<TipArea> Tips { get; } = new List<TipArea>();

    /// <summary>The tooltip text of an area: lines of data about exactly that element, or null when there is nothing to say.</summary>
    public string? TipText(TipArea area) => TipText(model, area, Texts);

    internal static string? TipText(PanelModel model, TipArea area, IEnumerable<string>? visible = null)
    {
        var tip = BuildTip(model, area);
        return visible == null ? tip : Tooltips.NotShown(tip, visible);
    }

    private static string? BuildTip(PanelModel model, TipArea area)
    {
        var s = model.Snapshot;
        if (s is null) return null;
        switch (area.Kind)
        {
            case TipKind.Request: return Tooltips.Request(s.Recent, area.Index, model.Now);
            case TipKind.CacheResult: return Tooltips.CacheResult(s.Recent, area.Index, model.Now);
            case TipKind.Context: return Tooltips.Context(s.Context);
            case TipKind.ContextRequest: return Tooltips.ContextAfter(s.Recent, area.Index, model.Now);
            case TipKind.Ram: return s.System is null ? null : Tooltips.Ram(s.System);
            case TipKind.Gpu: return s.System is null ? null : Tooltips.Gpu(s.System);
            case TipKind.Vram: return s.System is null ? null : Tooltips.Vram(s.System);
            case TipKind.Cpu: return s.System is null ? null : Tooltips.Cpu(s.System);
            case TipKind.ConversationCache: return s.Cache is null ? null : Tooltips.ConversationCache(s.Cache);
            case TipKind.CacheSlot: return s.Cache is null ? null : Tooltips.CacheSlot(s.Cache, area.Index, model.Now);
            case TipKind.BatchSlot: return s.Slots != null && area.Index < s.Slots.Count ? Tooltips.BatchSlot(s.Slots[area.Index], s.Context) : null;
            case TipKind.Totals: return s.Totals is null ? null : Tooltips.Totals(s.Totals, model.Now);
            default: return null;
        }
    }

    private void AddTip(float x, float y, float w, float h, TipKind kind, int index = 0) => Tips.Add(new TipArea(new RectangleF(x, y, w, h), kind, index));

    /// <summary>True for the areas that stand for one finished request: hovering one marks that request in every chart and in the dot row.</summary>
    internal static bool IsRequestTip(TipKind kind) => kind == TipKind.Request || kind == TipKind.CacheResult || kind == TipKind.ContextRequest;

    private void Bar(float x, float y, float w, float h, double frac, Color color, double? tick = null)
    {
        using (var track = Shapes.RoundedRect(new RectangleF(x, y, w, h), h / 2))
        using (var brush = new SolidBrush(pal.Faint(0.16)))
            g.FillPath(brush, track);
        var f = Math.Max(0, Math.Min(1, double.IsNaN(frac) || double.IsInfinity(frac) ? 0 : frac));
        if (f > 0)
        {
            using (var fill = Shapes.RoundedRect(new RectangleF(x, y, Math.Max(h, w * (float)f), h), h / 2))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, fill);
        }
        if (tick.HasValue)
            using (var brush = new SolidBrush(pal.Label))
                g.FillRectangle(brush, (float)(x + w * tick.Value - 0.75), y - 2, 1.5f, h + 4);
    }

    /// <summary>
    /// The bars of a speed chart, oldest on the left. Every bar is a tooltip area for its request (index into the newest-first list);
    /// the bar of the request under the mouse gets an outline, the same request as in the other chart and in the dot row.
    /// </summary>
    private void SparkBars(float x, float y, float w, float h, IList<double?> values, double maxV, Color color, TipKind tip = TipKind.Request)
    {
        // The chart has as many slots as there are requests, but at least MinChartSlots (so a few bars are never huge blocks) and at most 12.
        // Older slots without a request are the empty track on the left, newest on the right; they have no tooltip.
        int n = Math.Clamp(values.Count, MinChartSlots, 12);
        int empty = n - Math.Min(values.Count, 12);
        const float gap = 2f;
        var bw = (w - gap * (n - 1)) / n;
        for (int slot = 0; slot < empty; slot++)
            using (var track = new SolidBrush(pal.Faint(0.10)))
                g.FillRectangle(track, x + slot * (bw + gap), y, bw, h);
        for (int i = 0; i < values.Count; i++)
        {
            var bx = x + (empty + i) * (bw + gap);
            var request = values.Count - 1 - i;
            using (var track = new SolidBrush(pal.Faint(0.10)))
                g.FillRectangle(track, bx, y, bw, h);
            if (request == model.HoverRequest)          // the request under the pointer: a soft tint behind its own bar, 1 px around it
                using (var path = Shapes.RoundedRect(new RectangleF(bx - 1, y - 1, bw + 2, h + 2), 2))
                using (var wash = new SolidBrush(pal.Faint(Palette.HoverAlpha)))
                    g.FillPath(wash, path);
            if (values[i] is double v && v > 0)
            {
                var bh = Math.Max(2f, h * (float)Math.Min(1, v / maxV));
                using (var brush = new SolidBrush(i == values.Count - 1 ? color : Palette.Alpha(color, 0.65)))
                    g.FillRectangle(brush, bx, y + h - bh, bw, bh);
            }
            else
                // A request without a value here (a prompt served from the cache has no prefill speed): a thin muted stub, so the gap does
                // not look like a missing bar. The speed stays unknown; the tooltip has the tokens that came from the cache.
                using (var stub = new SolidBrush(pal.Faint(0.30)))
                    g.FillRectangle(stub, bx, y + h - 2f, bw, 2f);
            AddTip(bx - gap / 2, y, bw + gap, h, tip, request);
        }
    }

    private void Dot(float x, float y, Color color)
    {
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, x, y, 10, 10);
    }

    private float Separator(float y)
    {
        using (var brush = new SolidBrush(pal.Faint(0.14)))
            g.FillRectangle(brush, 10, y + SepHeight / 2, Width - 20, 1);
        return y + SepHeight;
    }

    // MARK: Layout (Panels.sections)

    /// <summary>Paints the whole panel and returns its height. Fills <see cref="Hits"/>.</summary>
    public float Paint()
    {
        Hits.Clear(); Sections.Clear();
        var s = model.Snapshot;
        float y = EdgePadding;
        if (model.Settings != null) return SettingsPage(y);
        if (model.Tabs.Count > 1) y = Tabs(y);

        if (s is null) y += Offline(y);
        else if (!s.HasModel || s.IsLoading || s.IsError) y += Header(s, y, 72);
        else
        {
            if (s.RouterLimited) y += LimitNote(y);
            var liveTop = y;
            y += Live(s, y);
            Sections.Add(("Live", liveTop, y));
            var groups = new List<(string, Func<float, float>)>();
            if (s.Recent.Count > 0) groups.Add(("Speed", yy => Speed(s, yy)));
            if (s.System != null && HasSystemRows(s.System)) groups.Add(("System", yy => SystemBlock(s.System, yy)));
            if (s.Cache != null) groups.Add(("Conversation cache", yy => CacheBlock(s, yy)));
            if (s.Info != null && HasEngineLines(s.Info, s)) groups.Add(("Engine", yy => EngineBlock(s, yy)));
            if (s.Slots != null && s.Slots.Count > 0) groups.Add(("Batch slots", yy => SlotBlock(s, yy)));
            if (s.Totals != null) groups.Add(("Totals", yy => TotalsBlock(s.Totals, yy)));
            foreach (var (name, group) in groups)
            {
                y = Separator(y);
                var top = y;
                y += group(y);
                Sections.Add((name, top, y));
            }
        }

        // The menu rows below the sections, as in the Mac menu: links, settings, updates, version, quit.
        var links = s?.Links ?? new List<EngineLink>();
        if (links.Count > 0)
        {
            y = Separator(y);
            for (int i = 0; i < links.Count; i++) y += MenuRow(y, links[i].Title, null, false, PanelAction.OpenLink, i);
        }
        y = Separator(y);
        y += MenuRow(y, MenuText.Settings, "›", false, PanelAction.Settings, 0);
        y += MenuRow(y, MenuText.CheckForUpdates, null, false, PanelAction.CheckForUpdates, 0);
        y = Separator(y);
        y += MenuRow(y, model.VersionLabel, null, true, null, 0);
        y += MenuRow(y, MenuText.Quit, "Ctrl+Q", false, PanelAction.Quit, 0);
        return y + EdgePadding;
    }

    /// <summary>The settings, drawn with the same rows as the rest of the panel: a tick on the right marks what is on.</summary>
    private float SettingsPage(float y)
    {
        var settings = model.Settings!;
        y += MenuRow(y, "‹  " + MenuText.Settings, null, false, PanelAction.SettingsBack, 0);
        foreach (var problem in settings.Problems) y += ProblemNote(y, problem);
        y = Separator(y);
        y += MenuRow(y, MenuText.StartWithWindows, settings.StartWithWindows ? "✓" : null, false, PanelAction.ToggleStartup, 0);
        y = Separator(y);
        T(MenuText.Theme, Pad, y + 4, Body, false, pal.Dim);
        y += RowHeight;
        foreach (var (title, choice) in new[] { (MenuText.ThemeSystem, ThemeChoice.System), (MenuText.ThemeLight, ThemeChoice.Light), (MenuText.ThemeDark, ThemeChoice.Dark) })
            y += MenuRow(y, title, settings.Theme == choice ? "✓" : null, false, PanelAction.SetTheme, (int)choice);
        y = Separator(y);
        y += MenuRow(y, MenuText.OpenSettingsFile, null, false, PanelAction.OpenSettingsFile, 0);
        y += MenuRow(y, MenuText.ReloadSettings, null, false, PanelAction.ReloadSettings, 0);
        return y + EdgePadding;
    }

    private float ProblemNote(float y, string text)
    {
        var f = F(Body, false);
        var rect = new RectangleF(Pad, y + 2, Width - 2 * Pad, 200);
        var measured = g.MeasureString(text, f, (int)rect.Width);
        Texts.Add(text);
        using (var brush = new SolidBrush(pal.Orange))
            g.DrawString(text, f, brush, rect);
        return (float)Math.Ceiling((measured.Height + 8) / 4) * 4;
    }

    private float MenuRow(float y, string title, string? shortcut, bool dimmed, PanelAction? action, int index)
    {
        var area = new RectangleF(6, y, Width - 12, RowHeight);      // the whole row takes the click ...
        if (action.HasValue)
        {
            Hits.Add(new PanelHit(area, action.Value, index, true));
            var hit = Hits.Count - 1;
            // ... but only the text is marked: its own bounds plus 2 px, a soft fill, no outline.
            var tight = new RectangleF(Pad - 2, y + 3, TextWidth(title, F(Body, false)) + 4, 18);
            if (hit == model.Pressed || hit == model.Hover) SoftFill(tight, hit == model.Pressed);
            if (hit == model.Focus) FocusRing(tight);
        }
        T(title, Pad, y + 4, Body, false, dimmed ? pal.TertiaryLabel : pal.Label);
        if (shortcut != null) T(shortcut, 0, y + 4, Body, false, pal.Dim, Width - Pad);
        return RowHeight;
    }

    /// <summary>Hover and pressed: the foreground colour at 7 % (12 % pressed), corners 4 epx. Black on light, white on dark, so a grey tint, never a white box.</summary>
    private void SoftFill(RectangleF bounds, bool pressed)
    {
        using (var path = Shapes.RoundedRect(bounds, 4))
        using (var brush = new SolidBrush(pal.HighContrast ? SystemColors.Highlight : pal.Faint(pressed ? Palette.PressedAlpha : Palette.HoverAlpha)))
            g.FillPath(brush, path);
    }

    /// <summary>The keyboard focus: a thin ring in the accent colour (a little muted), 4 epx corners, just outside the element.</summary>
    private void FocusRing(RectangleF area)
    {
        using (var path = Shapes.RoundedRect(RectangleF.Inflate(area, 1, 1), 5))
        using (var pen = new Pen(pal.HighContrast ? SystemColors.Highlight : Palette.Alpha(model.Accent ?? pal.Accent, 0.85), 1f))
            g.DrawPath(pen, path);
        using (var path = Shapes.RoundedRect(area, 4))
        using (var brush = new SolidBrush(pal.HighContrast ? Color.Transparent : pal.Faint(Palette.HoverAlpha)))
            g.FillPath(brush, path);       // focus is a little stronger than hover
    }

    // MARK: Server tabs (several servers)

    private float Tabs(float y)
    {
        var count = model.Tabs.Count;
        var w = (Width - 2 * 8f) / count;
        for (int i = 0; i < count; i++)
        {
            var tab = model.Tabs[i];
            var r = new RectangleF(8f + i * w, y, w - 4f, TabHeight);
            var hit = Hits.Count;
            if (i == model.Selected || hit == model.Hover || hit == model.Pressed)
                using (var p = Shapes.RoundedRect(RectangleF.Inflate(r, 0, -2), 4f))
                using (var brush = new SolidBrush(pal.Faint(hit == model.Pressed ? Palette.PressedAlpha : i == model.Selected ? 0.09 : Palette.HoverAlpha)))
                    g.FillPath(brush, p);
            Dot(r.X + 8, r.Y + 11, pal.StateColor(tab.State));
            var brief = Ellipsize(tab.Brief, F(Caption, false), r.Width * 0.45f);
            var nameWidth = r.Width - 24 - 8 - TextWidth(brief, F(Caption, false));
            T(Ellipsize(tab.Name, F(Body, i == model.Selected), nameWidth), r.X + 24, r.Y + 8, Body, i == model.Selected);
            T(brief, 0, r.Y + 11, Caption, false, pal.Dim, r.Right - 7);
            Hits.Add(new PanelHit(r, PanelAction.SelectTab, i, false));
            if (hit == model.Focus) FocusRing(r);
        }
        return y + TabHeight + 4f;
    }

    private string Ellipsize(string s, Font f, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (TextWidth(s, f) <= maxWidth) return s;
        while (s.Length > 1 && TextWidth(s + "…", f) > maxWidth) s = s.Substring(0, s.Length - 1);
        return s + "…";
    }

    // MARK: Header and the states without numbers

    private float Header(EngineSnapshot s, float y, float height)
    {
        var state = model.State;
        var col = pal.StateColor(state);
        Dot(Pad, y + 14, col);
        var word = Display.Word(state) + (s.Queued > 0 ? " · Queue " + s.Queued : "");
        T(!s.HasModel ? model.ServerName : s.Model, Pad + 18, y + 11, Model, true);
        T(word, 0, y + 11, Body, true, col, Width - Pad);
        if (s.IsLoading || s.IsError) T(s.LoadingReason ?? "", Pad, y + 44, Body);
        return height;
    }

    private float Offline(float y)
    {
        var col = pal.StateColor(model.State);
        Dot(Pad, y + 14, col);
        T(model.ServerName, Pad + 18, y + 11, Model, true);
        T(Display.Word(model.State), 0, y + 11, Body, true, col, Width - Pad);
        if (model.State == ServerState.Connecting) return 44;
        // The reason, as plain text under the state word. No heading: the state word already says it.
        var f = F(Body, false);
        var text = model.Error ?? "";
        var rect = new RectangleF(Pad, y + 40, Width - 2 * Pad, 200);
        var measured = g.MeasureString(text, f, (int)rect.Width);
        Texts.Add(text);
        using (var brush = new SolidBrush(pal.Dim))
            g.DrawString(text, f, brush, rect);
        return (float)Math.Ceiling((40 + measured.Height + 8) / 4) * 4;
    }

    private float LimitNote(float y)
    {
        T("Router limit · last data", Pad, y + 4, Body, false, pal.Orange);
        return 22;
    }

    // MARK: Live (paintLive)

    private float Live(EngineSnapshot s, float y)
    {
        var inner = Width - 2 * Pad;
        var col = pal.StateColor(model.State);
        Dot(Pad, y + 14, col);
        T(s.Model, Pad + 18, y + 11, Model, true);
        T(Display.Word(model.State) + (s.Queued > 0 ? " · Queue " + s.Queued : ""), 0, y + 11, Body, true, col, Width - Pad);

        var l = s.Live;
        var contextY = 108f;
        var running = l != null && l.IsParallel && s.Slots != null ? s.Slots.Where(sl => sl.IsActive).Take(MaxRunningRows).ToList() : new List<BatchSlot>();
        if (running.Count > 1)
        {
            // Several requests run together: the main bar stands for all of them (the headline is their total speed, the bar their combined
            // progress), and directly under it one smaller bar per request, each with its own numbers: no caption under the main bar.
            var reading = s.IsReading;
            var combined = Display.Combined(running);
            var main = reading ? pal.Orange : pal.Blue;
            T(reading ? "Prefill" + (l!.Percent.HasValue ? "  " + Fmt.Number(combined.Fraction * 100) + " %" : "") : "Decode", Pad, y + 40, Body, true);
            var total = reading ? l!.PrefillTokensPerSecond : l!.DecodeTokensPerSecond;
            T(total.HasValue ? Fmt.Number(total) + " t/s" : Fmt.Missing, 0, y + 40, Body, true, null, Width - Pad);
            Bar(Pad, y + 60, inner, 9, combined.Fraction, main);
            var longest = running.Where(sl => sl.IsDecoding).Select(sl => sl.Generated ?? 0).DefaultIfEmpty(0).Max();
            var ry = y + 78;
            foreach (var slot in running) { RunningRow(slot, Display.SlotFraction(slot, longest), ry); ry += RunningRowHeight; }
            contextY = ry - y + 10;
        }
        else if (l != null && s.IsReading)
        {
            var pct = l.Percent;
            T("Prefill" + (pct.HasValue ? "  " + pct + " %" : ""), Pad, y + 40, Body, true);
            T(l.PrefillTokensPerSecond.HasValue ? Fmt.Number(l.PrefillTokensPerSecond) + " t/s" : Fmt.Missing, 0, y + 40, Body, true, null, Width - Pad);
            Bar(Pad, y + 60, inner, 9, l.PromptTotal > 0 && l.PromptRead.HasValue ? l.PromptRead.Value / l.PromptTotal.Value : 0, pal.Orange);
            var line = Fmt.Number(l.PromptRead) + " / " + Fmt.Number(l.PromptTotal);
            if (l.PrefillTokensPerSecond is double sp && sp > 0 && l.PromptTotal is double total && l.PromptRead is double read && total > read)
                line += " · " + Fmt.Duration((total - read) / sp) + " left";
            T(line, Pad, y + 73, Body);
            T(Fmt.Number(l.ElapsedSeconds) + " s", 0, y + 73, Body, false, null, Width - Pad);
            if (l.PromptTotal is double pt && l.ReusedEstimate is double ru)
            {
                var hit = CacheHits.Classify(pt, ru);
                Dot(Pad, y + 88, pal.HitColor(hit));
                T(hit.Label() + "  ≈ " + Fmt.Number(Math.Round(ru / 100) * 100) + " / " + Fmt.Number(pt), Pad + 16, y + 86, Body, true);
            }
        }
        else if (l != null && s.Status == EngineStatus.Running)
        {
            // Requests run; the engine does not say whether it reads or writes, so no phase, speed or bar is drawn.
            T("Running", Pad, y + 40, Body, true);
            T(Fmt.Missing, 0, y + 40, Body, true, null, Width - Pad);
        }
        else if (l != null)
        {
            T("Decode", Pad, y + 40, Body, true);
            T(l.DecodeTokensPerSecond.HasValue ? Fmt.Number(l.DecodeTokensPerSecond, 1) + " t/s" : Fmt.Missing, 0, y + 40, Body, true, null, Width - Pad);
            Bar(Pad, y + 60, inner, 9, l.Generated.HasValue && l.MaxTokens > 0 ? l.Generated.Value / l.MaxTokens.Value : 0, pal.Blue);
            T(Fmt.Number(l.Generated) + " / " + Fmt.Number(l.MaxTokens), Pad, y + 73, Body);
            T(Fmt.Number(l.ElapsedSeconds) + " s", 0, y + 73, Body, false, null, Width - Pad);
        }
        else if (s.Recent.Count > 0)
        {
            var r = s.Recent[0];
            // The newest finished request: its speeds, the cache verdict and its numbers. No heading; the values name themselves.
            if (r.PrefillTokensPerSecond() is double ps) T("Prefill  " + Fmt.Number(ps) + " t/s", Pad, y + 40, Body, true, pal.Orange);
            T("Decode  " + Fmt.Number(r.DecodeTokensPerSecond, 1) + " t/s", 0, y + 40, Body, true, pal.Blue, Width - Pad);
            if (r.Hit is CacheHit hit)
            {
                Dot(Pad, y + 61, pal.HitColor(hit));
                T(hit.Label() + "  " + Fmt.Number(r.ReusedTokens) + " / " + Fmt.Number(r.PromptTokens), Pad + 16, y + 59, Body, true);
                AddTip(Pad, y + 55, inner, 20, TipKind.CacheResult, 0);
            }
            AddTip(Pad, y + 36, inner, 20, TipKind.Request, 0);
            T(Fmt.Number(r.PromptRead) + " read · " + Fmt.Number(r.OutputTokens) + " written · " + Fmt.Number(r.DurationSeconds) + " s", Pad, y + 75, Body);
        }

        T("Context", Pad, y + contextY, Body, true);
        var max = s.Context.Max;
        var known = max.HasValue && max.Value > 0;
        T(known ? Fmt.Number(s.Context.Used) + " / " + Fmt.Number(max) : Fmt.Number(s.Context.Used), 0, y + contextY, Body, false, null, Width - Pad);
        Bar(Pad, y + contextY + 20, inner, 9, known ? (s.Context.Used ?? 0) / max!.Value : 0, pal.Teal,
            known && s.Context.CompactAt.HasValue ? s.Context.CompactAt.Value / max!.Value : (double?)null);
        // The thin tick on the bar is the whole marker; the context tooltip says how far away it is.
        AddTip(Pad, y + contextY - 6, inner, 44, TipKind.Context);

        // The room left below the limit, and one bar per finished request showing the size of the conversation after it (oldest left): a
        // sudden drop is where a client started over. Both are left out when the numbers are not known.
        var bottom = contextY + 40;
        var chartTop = contextY + 36;
        if (s.Context.Free is double free)
        {
            T(Fmt.Number(free) + " free", Pad, y + contextY + 33, Caption);
            chartTop = contextY + 52;
            bottom = contextY + 56;
        }
        var sizes = s.Recent.Take(12).Reverse().Select(r => r.ContextTokens).ToList();
        if (sizes.Any(v => v > 0))
        {
            SparkBars(Pad, y + chartTop, inner, ContextChartHeight, sizes, sizes.Max(v => v ?? 0) * 1.15, pal.Teal, TipKind.ContextRequest);
            bottom = chartTop + ContextChartHeight + 12;
        }
        return bottom;
    }

    /// <summary>The height of the context history bars (a multiple of 4).</summary>
    internal const float ContextChartHeight = 24f;

    /// <summary>The most requests shown as rows under the headline (the engine runs at most this many together).</summary>
    internal const int MaxRunningRows = 8;
    /// <summary>The height of the row of one running request including the gap below it (a multiple of 4).</summary>
    internal const float RunningRowHeight = 40f;
    /// <summary>The bar of a running request: the main live bar (9 high) scaled down, with the same rounded ends.</summary>
    private const float RunningBarHeight = 6f;

    /// <summary>
    /// One running request: the state word in its colour with the slot number ("1  Decoding", "2  Reading 57 %") and its own speed at the
    /// right, the bar, and under it what it has done so far in secondary colour. The engine reports no token limit for a slot, so the bar of
    /// a decoding request is its length against the longest running one (see Display.SlotFraction).
    /// </summary>
    private void RunningRow(BatchSlot slot, double frac, float y)
    {
        var number = (slot.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Color color; string word, detail; string? speed = null;
        if (slot.IsReading)
        {
            color = pal.Orange;
            word = "Reading" + (slot.PromptTokens > 0 && slot.PromptRead.HasValue ? "  " + Fmt.Number(frac * 100) + " %" : "");
            detail = Fmt.Number(slot.PromptRead) + " / " + Fmt.Number(slot.PromptTokens);
        }
        else
        {
            color = pal.Blue;
            word = "Decoding";
            detail = slot.MaxTokens > 0 ? Fmt.Number(slot.Generated) + " / " + Fmt.Number(slot.MaxTokens) : Fmt.Number(slot.Generated) + " tokens";
            if (slot.TokensPerSecond is double ts && ts > 0) speed = Fmt.Number(ts) + " t/s";
        }
        if (slot.ElapsedSeconds is double e) detail += " · " + Fmt.Duration(e);
        T(number + "  " + word, Pad, y, Body, false, color);
        if (speed != null) T(speed, 0, y, Body, false, null, Width - Pad);
        Bar(Pad, y + 16, Width - 2 * Pad, RunningBarHeight, frac, color);
        T(detail, Pad, y + 23, Caption, false, pal.Dim);
    }

    // MARK: Speed

    private float Speed(EngineSnapshot s, float y)
    {
        var reqs = s.Recent.Take(12).Reverse().ToList();
        var dec = reqs.Select(r => r.DecodeTokensPerSecond).ToList();
        // Prefill speed only for prompts that were actually read (not served from cache).
        var pre = reqs.Select(r => r.PrefillTokensPerSecond(1500, 2000)).ToList();
        var half = (Width - 2 * Pad - 16) / 2;
        var xr = Pad + half + 16;
        T("Speed", Pad, y + 8, Body, true);
        var dv = dec.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var pv = pre.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        T("Prefill", Pad, y + 30, Body, true, pal.Orange);
        T((pv.Count > 0 ? Fmt.Number(pv.Last()) : Fmt.Missing) + " t/s", 0, y + 30, Body, false, null, Pad + half);
        SparkBars(Pad, y + 48, half, 30, pre, Math.Max((pv.Count > 0 ? pv.Max() : 0) * 1.15, 100), pal.Orange);
        T("avg " + (pv.Count == 0 ? Fmt.Missing : Fmt.Number(pv.Average())), Pad, y + 80, Caption);
        T("Decode", xr, y + 30, Body, true, pal.Blue);
        T((dv.Count > 0 ? Fmt.Number(dv.Last(), 1) : Fmt.Missing) + " t/s", 0, y + 30, Body, false, null, Width - Pad);
        SparkBars(xr, y + 48, half, 30, dec, Math.Max((dv.Count > 0 ? dv.Max() : 0) * 1.15, 10), pal.Blue);
        T("avg " + (dv.Count == 0 ? Fmt.Missing : Fmt.Number(dv.Average(), 1)), xr, y + 80, Caption);
        return SpeedHeight;
    }

    // MARK: System

    private static bool HasSystemRows(SystemStats s) =>
        (s.RamUsedBytes.HasValue && s.RamTotalBytes > 0) || s.GpuPercent.HasValue || (s.GpuMemoryUsedBytes.HasValue && s.GpuMemoryTotalBytes > 0) || s.CpuPercent.HasValue;

    private float SystemBlock(SystemStats sys, float y)
    {
        var inner = Width - 2 * Pad;
        T("System", Pad, y + 8, Body, true);
        float row = y + 28;
        void Row(string label, string value, double frac, Color c)
        {
            AddTip(Pad, row - 3, inner, SystemRowPitch - 2, label == "RAM" ? TipKind.Ram : label == "GPU" ? TipKind.Gpu : label == "VRAM" ? TipKind.Vram : TipKind.Cpu);
            T(label, Pad, row, Body);
            T(value, 0, row, Body, false, null, Width - Pad);
            Bar(Pad, row + 15, inner, 6, frac, c);
            row += SystemRowPitch;
        }
        // A value the engine did not report leaves its row out; it is never drawn as 0.
        if (sys.RamUsedBytes is double used && sys.RamTotalBytes is double total && total > 0)
            Row("RAM", Fmt.Gib(used) + " / " + Fmt.Gib(total) + " GB", used / total, used / total > 0.9 ? pal.Red : pal.Purple);
        if (sys.GpuPercent is double gpu)
        {
            var extra = (sys.GpuTemperatureCelsius is double t ? " · " + Fmt.Number(t) + " °C" : "")
                + (sys.GpuPowerWatts is double w ? " · " + Fmt.Number(w) + " W" : "");
            Row("GPU", Fmt.Number(gpu) + " %" + extra, gpu / 100, pal.Green);
        }
        if (sys.GpuMemoryUsedBytes is double vu && sys.GpuMemoryTotalBytes is double vt && vt > 0)
            Row("VRAM", Fmt.Gib(vu) + " / " + Fmt.Gib(vt) + " GB", vu / vt, vu / vt > 0.97 ? pal.Red : pal.Mint);
        if (sys.CpuPercent is double cpu) Row("CPU", Fmt.Number(cpu) + " %", cpu / 100, pal.Indigo);
        return row - y - 4;
    }

    // MARK: Conversation cache

    private float CacheBlock(EngineSnapshot s, float y)
    {
        var c = s.Cache!;
        var inner = Width - 2 * Pad;
        T("Conversation cache", Pad, y + 8, Body, true);
        T(Fmt.Gib(c.UsedBytes) + " / " + Fmt.Gib(c.BudgetBytes) + " GiB", 0, y + 8, Body, false, null, Width - Pad);
        var frac = c.BudgetBytes > 0 && c.UsedBytes.HasValue ? c.UsedBytes.Value / c.BudgetBytes.Value : 0;
        Bar(Pad, y + 28, inner, 9, frac, frac > 0.9 ? pal.Red : pal.Mint);
        T((c.Parked.HasValue ? c.Parked.ToString() : Fmt.Missing) + " / " + (c.Slots.HasValue ? c.Slots.ToString() : Fmt.Missing), Pad, y + 42, Body);
        AddTip(Pad, y + 4, inner, 54, TipKind.ConversationCache);
        var rate = c.Requests > 0 && c.RequestsReused.HasValue ? Fmt.Number(c.RequestsReused.Value / c.Requests.Value * 100) : Fmt.Missing;
        T("Hit rate " + rate + " % · evicted " + Fmt.Number(c.Evictions), 0, y + 42, Body, false, null, Width - Pad);
        var recent = s.Recent.Take(12).Reverse().ToList();
        var x = Width - Pad - recent.Count * 14 + 4;
        for (int i = 0; i < recent.Count; i++)
        {
            var request = recent.Count - 1 - i;
            if (recent[i].Hit is CacheHit hit)
            {
                if (request == model.HoverRequest)       // a soft disc behind the dot, 2 px around it
                    using (var wash = new SolidBrush(pal.Faint(Palette.HoverAlpha)))
                        g.FillEllipse(wash, x - 2f, y + 61f, 14f, 14f);
                Dot(x, y + 63, pal.HitColor(hit));
                AddTip(x - 2, y + 59, 14, 18, TipKind.CacheResult, request);
            }
            x += 14;
        }
        float h = 84;
        // The parked conversations come from the engine's log; without a readable log there are no rows (never "free" ones).
        var list = Enumerable.Reverse(c.Entries).ToList();   // newest first
        var rows = c.EntriesKnown ? Math.Min(c.Slots ?? 0, 8) : 0;
        for (int i = 0; i < rows; i++)
        {
            var ry = y + h;
            if (i >= list.Count)
            {
                T("Slot " + (i + 1), Pad, ry + 6, Body);
                T(c.IsParkedWithoutDetails(i) ? "In use" : "Free", 0, ry + 6, Body, false, null, Width - Pad);
                AddTip(Pad, ry + 2, inner, CacheRowHeight - 4, TipKind.CacheSlot, i);
                Bar(Pad, ry + 24, inner, 6, 0, pal.Mint);
            }
            else
            {
                var e = list[i];
                T("K" + e.Id, Pad, ry + 6, Body, true);
                T(Fmt.Number(e.Tokens) + " tokens", Pad + 46, ry + 6, Body);
                var right = Fmt.Gib(e.Bytes) + " GiB" + (e.ParkedAt.HasValue ? " · " + Fmt.Duration((model.Now - e.ParkedAt.Value).TotalSeconds) + " ago" : "");
                T(right, 0, ry + 6, Body, false, null, Width - Pad);
                Bar(Pad, ry + 24, inner, 6, c.BudgetBytes > 0 ? e.Bytes / c.BudgetBytes.Value : 0, pal.Mint);
                AddTip(Pad, ry + 2, inner, CacheRowHeight - 4, TipKind.CacheSlot, i);
            }
            h += CacheRowHeight;
        }
        return h;
    }

    // MARK: Engine and batch slots (optional sections, drawn only when the engine reports them)

    private static List<string> EngineLine1(EngineInfo e)
    {
        var parts = new List<string>();
        if (e.KvFormat != null)
            parts.Add("KV " + e.KvFormat + (e.KvResidentTokens > 0 ? " (" + Fmt.Number(e.KvResidentTokens) + " in VRAM)" : ""));
        if (e.DraftWindow != null) parts.Add("draft " + e.DraftWindow);
        return parts;
    }

    private static List<string> EngineLine2(EngineInfo e)
    {
        var parts = new List<string>();
        if (e.ExpertCacheMib.HasValue) parts.Add("expert cache " + Fmt.Number(e.ExpertCacheMib.Value / 1024, 1) + " GiB");
        if (e.BatchSlots.HasValue && e.BatchSlots.Value > 1) parts.Add(Fmt.Number(e.BatchSlots) + " parallel");      // batch_slots: requests that run together (not the prompt cache slots)
        if (e.ExpertHitRate.HasValue) parts.Add("expert hit " + Fmt.Number(e.ExpertHitRate.Value * 100) + " %");
        if (e.KvFreePages.HasValue) parts.Add(Fmt.Number(e.KvFreePages) + " KV pages free");
        if (e.PrefixCheckpoints.HasValue) parts.Add(Fmt.Number(e.PrefixCheckpoints) + " prefix " + (e.PrefixCheckpoints == 1 ? "checkpoint" : "checkpoints"));
        if (e.AdmissionBlocked is "kv" or "speed") parts.Add("admission held by " + e.AdmissionBlocked);
        return parts;
    }

    private static bool HasEngineLines(EngineInfo e, EngineSnapshot s) =>
        e.Version != null || s.Context.Max.HasValue || EngineLine1(e).Count > 0 || EngineLine2(e).Count > 0;

    private float EngineBlock(EngineSnapshot s, float y)
    {
        var e = s.Info!;
        T("Engine", Pad, y + 8, Body, true);
        if (e.Version != null) T(s.Engine + " " + e.Version, 0, y + 9, Body, false, pal.Dim, Width - Pad);
        var l1 = EngineLine1(e);
        if (s.Context.Max.HasValue) l1.Insert(0, "context " + Fmt.Number(s.Context.Max));
        float h = 28;
        if (l1.Count > 0) { T(Casing.First(string.Join(" · ", l1)), Pad, y + h, Body); h += 16; }
        var l2 = EngineLine2(e);
        if (l2.Count > 0) { T(Casing.First(string.Join(" · ", l2)), Pad, y + h, Body); h += 16; }
        return h + 6;
    }

    private float SlotBlock(EngineSnapshot s, float y)
    {
        T("Batch slots", Pad, y + 8, Body, true);
        float row = y + 28;
        var slotNumber = 0;
        foreach (var slot in s.Slots!)
        {
            AddTip(Pad, row - 3, Width - 2 * Pad, 22, TipKind.BatchSlot, slotNumber++);
            // The memory of each slot: the tokens it holds (an idle slot keeps its last conversation; a running one holds its prompt and what it
            // has written). The speeds of the running requests are in the live block. A part the engine did not report is not drawn.
            T("Slot " + (slot.Index + 1), Pad, row, Body);
            if (slot.Tokens.HasValue) T(Fmt.Number(slot.Tokens) + " tokens", 0, row, Body, false, null, Width - Pad);
            var frac = s.Context.Max > 0 && slot.Tokens.HasValue ? slot.Tokens.Value / s.Context.Max.Value : 0;
            Bar(Pad, row + 15, Width - 2 * Pad, 4, frac, slot.IsActive ? pal.Blue : pal.Mint);
            row += 24;
        }
        return row - y;
    }

    // MARK: Totals

    private float TotalsBlock(Totals t, float y)
    {
        var since = t.Since.HasValue ? " since " + Fmt.Duration((model.Now - t.Since.Value).TotalSeconds) : "";
        AddTip(Pad, y + 2, Width - 2 * Pad, TotalsHeight - 4, TipKind.Totals);
        T("Total" + since, Pad, y + 7, Body, true);
        T(Fmt.Number(t.Requests) + " requests · " + Fmt.Number(t.OutputTokens) + " tokens", Pad, y + 22, Body);
        var share = t.PromptTokens > 0 && t.ReusedTokens.HasValue ? Fmt.Number(t.ReusedTokens.Value / t.PromptTokens.Value * 100) : Fmt.Missing;
        T((t.PromptTokens.HasValue ? Fmt.Number(t.PromptTokens.Value / 1_000_000, 1) : Fmt.Missing) + " M prompt tokens · " + share + " % cached", Pad, y + 36, Body);
        return TotalsHeight;
    }
}
