using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Stratoscope.Windows;

/// <summary>
/// The panel that opens from the taskbar readout or the notification-area icon: the Mac menu, drawn the same way.
/// Borderless, never in the taskbar, closes on Esc and when it loses focus.
///
/// Size and DPI: the panel is sized from the monitor the anchor is on (<see cref="PanelLayout"/>), before the window exists there,
/// never from <c>Control.DeviceDpi</c>, which WinForms on .NET Framework reports as 96 whatever the monitor. WM_DPICHANGED
/// (the window was moved to another monitor, or the display scale changed while it is open) lays it out again.
/// </summary>
internal sealed class StatusPanel : Form, IPanelSurface
{
    private readonly Func<PanelModel> modelSource;
    // The panel never paints into its window on its own: every change is composed as a whole frame offscreen first (ApplyFrame) and
    // shown by the presenter in one operation. The window does not erase its background (WM_ERASEBKGND answers 1) and repaints from the
    // frame it is showing. The counters below are what the trace reports per update.
    private readonly PanelPresenter presenter;
    private int positionChanges, paints, erases;
    private bool presenting;
    private readonly Action<PanelAction, int> onAction;
    private readonly UiController ui;
    // Data tooltips: shown after 700 ms on an element in a TipWindow (themed, rounded, no native tooltip), the text built on
    // hover and kept per element until the data changes.
    private readonly TipWindow tip = new TipWindow();
    private readonly System.Windows.Forms.Timer tipTimer = new System.Windows.Forms.Timer { Interval = 700 };
    private readonly Dictionary<(TipKind, int), string?> tipTexts = new Dictionary<(TipKind, int), string?>();
    private EngineSnapshotKey tipSource;
    private int tipIndex = -1;
    private int hoverRequest = -1;
    private Rectangle anchor;
    private TaskbarEdge edge = TaskbarEdge.Bottom;
    private PanelLayout layout = new PanelLayout(96, 1f, new Size(352, 300), Point.Empty, Rectangle.Empty, 300f);
    private float scroll;
    private DateTime hiddenAt = DateTime.MinValue;
    private List<PanelHit> hits = new List<PanelHit>();
    private List<TipArea> tips = new List<TipArea>();
    private int hover = -1, pressed = -1, focus = -1;
    private bool relayouting;

    /// <summary>The identity of the data the cached tooltip texts were built from.</summary>
    private readonly struct EngineSnapshotKey
    {
        public readonly object? Snapshot; public readonly DateTime Now;
        public EngineSnapshotKey(object? snapshot, DateTime now) { Snapshot = snapshot; Now = now; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public StatusPanel(Func<PanelModel> model, Action<PanelAction, int> action, UiController uiController)
    {
        modelSource = model;
        onAction = action;
        ui = uiController;
        presenter = new PanelPresenter(this);
        Text = "Stratoscope";
        AccessibleName = "Stratoscope";
        AccessibleRole = AccessibleRole.Window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        // The frame is complete before it is drawn, so there is nothing to double-buffer: OnPaint copies it to the window.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Deactivate += (s, e) => HidePanel();
        tipTimer.Tick += (s, e) => ShowTip();
    }

    /// <summary>The layout the panel has (or would have) for its current anchor. For tests and diagnostics.</summary>
    internal PanelLayout CurrentLayout => layout;
    internal float ScrollOffset => scroll;
    internal int FocusIndex => focus;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowAttributes();
    }

    /// <summary>Round corners (Windows 11) and a dark frame when apps use the dark theme. Both are best effort.</summary>
    private void ApplyWindowAttributes()
    {
        try
        {
            int round = 2;           // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int dark = ui.Current.AppsDark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
    }

    public bool IsOpen => Visible;

    /// <summary>Composes the panel again for the current state and shows it if anything on it changed. The reason (the calling method) goes to the trace.</summary>
    private void Repaint([System.Runtime.CompilerServices.CallerMemberName] string why = "") => ApplyFrame(why);

    public void HidePanel()
    {
        if (!Visible) return;
        hiddenAt = DateTime.UtcNow;
        HideTip();
        Hide();
    }

    /// <summary>Opens the panel next to <paramref name="anchorRect"/> (screen pixels), or closes it when it is open.</summary>
    public void Toggle(Rectangle anchorRect, TaskbarEdge taskbarEdge)
    {
        if (Visible) { HidePanel(); return; }
        // The click that closed the panel by taking focus away must not open it again.
        if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300) return;
        anchor = anchorRect; edge = taskbarEdge;
        scroll = 0; focus = -1; hover = -1; pressed = -1;
        // Size, place and compose it first, while it has no window: the window is then created on the monitor it belongs on and its first
        // paint is the finished frame.
        ApplyFrame("open");
        ApplyWindowAttributes();
        Show();
        Activate();
        ApplyFrame("opened");   // a window that was already on another monitor has received WM_DPICHANGED by now; make sure the layout matches
    }

    /// <summary>Shows new data: one frame, one size change when the height changed, nothing when the pixels are the same. Keeps it anchored.</summary>
    public void Refresh(bool relayout = true)
    {
        if (!Visible) return;
        ApplyFrame("data");
    }

    /// <summary>Re-reads the display and Windows settings: size, fonts, position, colours.</summary>
    public void ApplyEnvironment()
    {
        if (!Visible) return;
        ApplyWindowAttributes();
        ApplyFrame("environment");
    }

    internal PanelLayout Compute(Rectangle anchorRect, TaskbarEdge taskbarEdge, float? contentHeight = null)
    {
        var height = contentHeight ?? MeasureContent(modelSource(), ui.At(new Point(anchorRect.Left + anchorRect.Width / 2, anchorRect.Top + anchorRect.Height / 2)).Panel).Height;
        return PanelLayout.For(anchorRect, height, taskbarEdge, ui);
    }

    private (float Height, List<string> Texts) MeasureContent(PanelModel model, Palette palette)
    {
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
        {
            var painter = new PanelPainter(g, palette, model);
            var h = painter.Paint();
            return (h, painter.Texts);
        }
    }

    private void Relayout() => ApplyFrame("layout");

    /// <summary>
    /// One update of the panel, in this order: the content height and with it the final rectangle, the complete frame composed offscreen,
    /// then the presenter changes the window once (size and position in one call, only when they differ) and shows the frame in one
    /// operation (only the rows that differ, nothing at all when none does).
    /// </summary>
    private PanelUpdate? ApplyFrame(string reason)
    {
        if (relayouting) return null;
        relayouting = true;
        try
        {
            var metrics = ui.At(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
            var model = modelSource();
            model.Hover = hover; model.Pressed = pressed; model.Focus = focus; model.Accent = metrics.Accent; model.HoverRequest = hoverRequest;
            var (height, texts) = MeasureContent(model, metrics.Panel);
            layout = PanelLayout.For(anchor, height, edge, ui);
            // The position is the one the layout gives: the edge next to the taskbar stays where it is when the height changes.
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
            var painter = PanelFrame.Compose(presenter.Target(layout.Size), model, metrics.Panel, layout, scroll);
            hits = painter.Hits; tips = painter.Tips;
            var description = string.Join(". ", texts.Where(t => t.Trim().Length > 0));
            if (description != AccessibleDescription) AccessibleDescription = description;
            presenting = true;
            try { return presenter.Update(reason, layout.Bounds); }
            finally { presenting = false; }
        }
        finally { relayouting = false; }
    }

    PanelSurfaceCounters IPanelSurface.Counters => new PanelSurfaceCounters(positionChanges, paints, erases);

    void IPanelSurface.SetBounds(Rectangle bounds) => base.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    void IPanelSurface.Present(Bitmap frame, Rectangle dirty)
    {
        // Not visible: the first paint after Show draws this frame. Visible: paint the rows that changed now, in this call.
        if (!IsHandleCreated || !Visible) return;
        Invalidate(dirty);
        Update();
    }

    internal float MaxScroll => Math.Max(0, layout.ContentHeight - layout.Size.Height / layout.Scale);

    protected override void WndProc(ref Message m)
    {
        const int WM_DPICHANGED = 0x02E0, WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F, WM_WINDOWPOSCHANGED = 0x0047;
        switch (m.Msg)
        {
            case WM_DPICHANGED:
                // The window moved to a monitor with another scale, or the scale changed under it. Do not let WinForms resize it:
                // lay it out for the monitor the anchor is on.
                if (Visible) ApplyFrame("dpi");
                return;
            case WM_ERASEBKGND:
                erases++;
                m.Result = (IntPtr)1;      // the frame covers every pixel: nothing to erase, nothing to clear first
                return;
            case WM_PAINT: paints++; break;
            case WM_WINDOWPOSCHANGED: positionChanges++; break;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>Draws the frame that is shown, as it is: one copy, no clearing, no painting of content.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (!presenting && ReadoutHost.Tracing) ReadoutHost.Trace("panel paint: system (window uncovered)");
        var frame = presenter.Shown;
        if (frame == null) return;
        // One GDI call from the pixels of the frame, top-down, into the part of the window that is being painted (the clip). Measured: this
        // takes about 2 ms for a full panel, DrawImage of the same bitmap about 15 ms.
        var header = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = frame.Width, biHeight = -frame.Height, biPlanes = 1, biBitCount = 32 };
        var hdc = e.Graphics.GetHdc();
        try { SetDIBitsToDevice(hdc, 0, 0, (uint)frame.Width, (uint)frame.Height, 0, 0, 0, (uint)frame.Height, presenter.ShownBytes, ref header, 0); }
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, uint width, uint height, int xSrc, int ySrc, uint startScan, uint lines, byte[] bits, ref Native.BITMAPINFOHEADER info, uint colourUse);

    // MARK: Keyboard

    protected override bool ProcessDialogKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var shift = (keyData & Keys.Shift) != 0;
        if (key == Keys.Tab) { MoveFocus(shift ? -1 : 1); return true; }
        if (key == Keys.Down || key == Keys.Up) { MoveFocus(key == Keys.Down ? 1 : -1); return true; }
        return base.ProcessDialogKey(keyData);
    }

    internal void MoveFocus(int step)
    {
        if (hits.Count == 0) return;
        focus = focus < 0 ? (step > 0 ? 0 : hits.Count - 1) : (focus + step + hits.Count) % hits.Count;
        EnsureVisible(hits[focus].Area);
        Repaint();
    }

    private void EnsureVisible(RectangleF area)
    {
        var viewport = layout.Size.Height / layout.Scale;
        if (area.Top < scroll) scroll = area.Top - 8;
        else if (area.Bottom > scroll + viewport) scroll = area.Bottom - viewport + 8;
        scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.KeyCode, e.Control)) e.Handled = true;
    }

    /// <summary>Keyboard use: Esc closes, Enter and Space activate the focused row, Left and Right switch servers, Page Up and Down scroll, Ctrl+Q quits.</summary>
    internal bool HandleKey(Keys key, bool control)
    {
        if (key == Keys.Escape) { HidePanel(); return true; }
        if (control && key == Keys.Q) { onAction(PanelAction.Quit, 0); return true; }
        if ((key == Keys.Enter || key == Keys.Space) && focus >= 0 && focus < hits.Count) { Activate(hits[focus]); return true; }
        if (key == Keys.Left || key == Keys.Right)
        {
            var model = modelSource();
            if (model.Tabs.Count > 1)
            {
                var next = Core.Servers.Clamp(model.Selected + (key == Keys.Left ? -1 : 1), model.Tabs.Count);
                onAction(PanelAction.SelectTab, next);
                return true;
            }
        }
        if (key == Keys.PageDown || key == Keys.PageUp)
        {
            ScrollBy((key == Keys.PageDown ? 1 : -1) * layout.Size.Height / layout.Scale * 0.8f);
            return true;
        }
        return false;
    }

    /// <summary>Scrolls the content by a distance in logical pixels (positive: down), within its limits.</summary>
    internal void ScrollBy(float logical)
    {
        scroll = Math.Max(0, Math.Min(MaxScroll, scroll + logical));
        Repaint();
    }

    private void Activate(PanelHit hit)
    {
        if (hit.Action != PanelAction.SelectTab) HidePanel();
        onAction(hit.Action, hit.Index);
    }

    // MARK: Mouse

    private int HitAt(Point p)
    {
        var logical = new PointF(p.X / layout.Scale, p.Y / layout.Scale + scroll);
        for (int i = 0; i < hits.Count; i++) if (hits[i].Area.Contains(logical)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = HitAt(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != hover) { hover = i; Repaint(); }
        UpdateTipTarget(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != -1) { hover = -1; Repaint(); }
        UpdateTipTarget(null);
    }

    // MARK: Data tooltips

    private int TipAt(Point p)
    {
        var logical = new PointF(p.X / layout.Scale, p.Y / layout.Scale + scroll);
        for (int i = tips.Count - 1; i >= 0; i--) if (tips[i].Rect.Contains(logical)) return i;
        return -1;
    }

    /// <summary>The text of a tip area. Built on first use and kept until the data it was built from changes.</summary>
    internal string? TipTextOf(TipArea area)
    {
        var model = modelSource();
        var key = new EngineSnapshotKey(model.Snapshot, model.Now);
        if (!ReferenceEquals(key.Snapshot, tipSource.Snapshot)) { tipTexts.Clear(); tipSource = key; }
        if (!tipTexts.TryGetValue((area.Kind, area.Index), out var text))
            tipTexts[(area.Kind, area.Index)] = text = PanelPainter.TipText(model, area);
        return text;
    }

    /// <summary>The pointer moved to another element (or left): marks the request in the charts, restarts the delay, hides a shown tooltip.</summary>
    private void UpdateTipTarget(Point? pointer)
    {
        var i = pointer.HasValue ? TipAt(pointer.Value) : -1;
        var request = i >= 0 && PanelPainter.IsRequestTip(tips[i].Kind) ? tips[i].Index : -1;
        if (request != hoverRequest) { hoverRequest = request; Repaint(); }
        if (i == tipIndex) return;
        HideTip();
        tipIndex = i;
        if (i >= 0) { tipTimer.Stop(); tipTimer.Start(); }
    }

    private void ShowTip()
    {
        tipTimer.Stop();
        if (tipIndex < 0 || tipIndex >= tips.Count || !Visible) return;
        var text = TipTextOf(tips[tipIndex]);
        if (string.IsNullOrEmpty(text)) return;
        // Below the element (never over it), centred on it, in the palette and scale of the panel.
        var area = tips[tipIndex].Rect; var s = layout.Scale;
        var anchorScreen = RectangleToScreen(new Rectangle((int)(area.X * s), (int)((area.Y - scroll) * s), (int)(area.Width * s), (int)(area.Height * s)));
        tip.Show(text, anchorScreen, Screen.FromControl(this).WorkingArea, preferAbove: false, ui.At(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2)).Panel, s);
    }

    private void HideTip()
    {
        tipTimer.Stop();
        tip.Hide();
        tipIndex = -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        HideTip();
        if (e.Button == MouseButtons.Left) { pressed = HitAt(e.Location); Repaint(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var was = pressed;
        pressed = -1;
        if (e.Button != MouseButtons.Left) { Repaint(); return; }
        var i = HitAt(e.Location);
        if (i >= 0 && i == was) Activate(hits[i]);
        else Repaint();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!layout.Scrolls) return;
        HideTip();
        scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120f * 48f));
        Repaint();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tipTimer.Dispose(); tip.Dispose(); presenter.Dispose(); }
        base.Dispose(disposing);
    }

    // MARK: Accessibility: every element with a tooltip is a child with the same text as its name

    protected override AccessibleObject CreateAccessibilityInstance() => new PanelAccessible(this);

    private sealed class PanelAccessible : ControlAccessibleObject
    {
        private readonly StatusPanel panel;
        public PanelAccessible(StatusPanel owner) : base(owner) { panel = owner; }
        public override int GetChildCount() => panel.tips.Count;
        public override AccessibleObject GetChild(int index) => index >= 0 && index < panel.tips.Count ? new TipAccessible(this, panel, index) : null!;
    }

    private sealed class TipAccessible : AccessibleObject
    {
        private readonly PanelAccessible parent; private readonly StatusPanel panel; private readonly int index;
        public TipAccessible(PanelAccessible p, StatusPanel owner, int i) { parent = p; panel = owner; index = i; }
        public override AccessibleObject Parent => parent;
        public override AccessibleRole Role => AccessibleRole.StaticText;
        public override string? Name { get => index < panel.tips.Count ? panel.TipTextOf(panel.tips[index])?.Replace('\n', '.') : null; set { } }
        public override Rectangle Bounds
        {
            get
            {
                if (index >= panel.tips.Count) return Rectangle.Empty;
                var r = panel.tips[index].Rect; var s = panel.layout.Scale;
                return panel.RectangleToScreen(new Rectangle((int)(r.X * s), (int)((r.Y - panel.scroll) * s), (int)(r.Width * s), (int)(r.Height * s)));
            }
        }
    }

    protected override bool ShowWithoutActivation => false;
}
