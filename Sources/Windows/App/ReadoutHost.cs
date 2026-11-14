using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Stratoscope.Core;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;
using DiagnosticsTrace = System.Diagnostics.Trace;

namespace Stratoscope.Windows;

/// <summary>
/// Puts the readout into the Windows taskbar.
///
/// Windows 11 has no supported API for third-party taskbar content (deskbands are gone), so this does what the other
/// taskbar tools do: a borderless, layered child window of the taskbar window (Shell_TrayWnd), placed in free space (TaskbarLayout).
/// The geometry is checked a few times a second, so a change of DPI, monitor, taskbar position, auto-hide, theme or task-button count
/// is followed, and a restart of explorer.exe (the TaskbarCreated message) re-creates the window. Why the readout is not shown, when it
/// is not, goes to the error log once per change, and the user is told in one sentence.
///
/// Everything runs on its own thread with its own message loop: the taskbar and this thread share an input queue while
/// the child window exists, so this thread never waits for the UI thread or the network.
/// </summary>
internal sealed class ReadoutHost : IDisposable
{
    public const string ControlClass = "Stratoscope.TaskbarControl";
    public const string ReadoutClass = "Stratoscope.TaskbarReadout";
    private const uint WM_UPDATE = WM_APP + 1;
    private const int TimerMilliseconds = 500;

    private static readonly ConcurrentDictionary<HWND, ReadoutHost> Hosts = new ConcurrentDictionary<HWND, ReadoutHost>();
    private static readonly WNDPROC Proc = WindowProc;
    private static readonly LRESULT Handled = new LRESULT(0);
    private static bool classesRegistered;

    private readonly SynchronizationContext? ui;
    private readonly UiController controller;
    private readonly object gate = new object();
    private Thread? thread;
    private HWND control;
    private HWND readout;
    private uint taskbarCreated;

    // Data handed in from the UI thread.
    private List<ReadoutItem> items = new List<ReadoutItem>();
    private bool wanted;
    private ReadoutPosition position = ReadoutPosition.Tray;

    // State of the host thread.
    private string lastSignature = "";
    private List<Rectangle> bounds = new List<Rectangle>();
    private Rectangle currentScreenRect;
    private List<Rectangle> uiaButtons = new List<Rectangle>();
    private DateTime buttonsAt = DateTime.MinValue;
    private int buttonsBusy;
    private volatile bool buttonsKnown;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private int hover = -1, pressed = -1;
    private bool tracking;
    private int lastDpi = 96;
    private UiMetrics metrics = new UiMetrics();
    private volatile TaskbarEdge edge = TaskbarEdge.Bottom;
    private volatile int dpi = 96;

    /// <summary>An item was clicked: its index and its rectangle in screen pixels.</summary>
    public event Action<int, Rectangle>? ItemClicked;
    /// <summary>The right mouse button was released over the readout: the screen position.</summary>
    public event Action<Point>? ContextMenuRequested;
    /// <summary>Something about the taskbar or the display changed (the reason). Raised on the UI thread.</summary>
    public event Action<string>? EnvironmentChanged;

    public ReadoutHost(SynchronizationContext? uiContext, UiController uiController) { ui = uiContext; controller = uiController; }

    public TaskbarEdge Edge => edge;
    public int Dpi => dpi;
    /// <summary>The metrics the readout was last drawn with.</summary>
    public UiMetrics Metrics { get { lock (gate) return metrics; } }
    /// <summary>The readout's rectangle on screen, or empty while it is not shown.</summary>
    public Rectangle ScreenRect { get { lock (gate) return currentScreenRect; } }

    public void Start()
    {
        var ready = new ManualResetEventSlim(false);
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Stratoscope taskbar readout" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(5000);
    }

    /// <summary>Hands in what to show. Thread-safe; returns at once.</summary>
    public void Update(IList<ReadoutItem> newItems, bool show, ReadoutPosition where)
    {
        lock (gate) { items = newItems.ToList(); wanted = show; position = where; }
        if (!control.IsNull) PostMessage(control, WM_UPDATE, 0, 0);
    }

    /// <summary>Redraws everything, for example after the user changed the UI scale. Thread-safe.</summary>
    public void Refresh()
    {
        lock (gate) lastSignature = "";
        if (!control.IsNull) PostMessage(control, WM_UPDATE, 0, 0);
    }

    public void Dispose()
    {
        if (!control.IsNull) PostMessage(control, WM_CLOSE, 0, 0);
        thread?.Join(3000);
    }

    // MARK: Thread

    private void Run(ManualResetEventSlim ready)
    {
        RegisterClasses();
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        control = LayeredWindows.Create(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, ControlClass, "Stratoscope taskbar control", WINDOW_STYLE.WS_POPUP, 0, 0, HWND.Null);
        if (!control.IsNull)
        {
            Hosts[control] = this;
            SetTimer(control, 1, TimerMilliseconds, null);
        }
        ready.Set();
        if (control.IsNull) return;
        Reconcile(true);
        while (GetMessage(out var msg, HWND.Null, 0, 0).Value > 0)
        {
            TranslateMessage(in msg);
            DispatchMessage(in msg);
        }
        DestroyReadout(); tipWindow.Dispose(); statusWindow.Dispose();
        Hosts.TryRemove(control, out _);
    }

    private static void RegisterClasses()
    {
        if (classesRegistered) return;
        LayeredWindows.RegisterClass(ControlClass, Proc);
        LayeredWindows.RegisterClass(ReadoutClass, Proc);
        classesRegistered = true;
    }

    private static LRESULT WindowProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        foreach (var host in Hosts.Values)
        {
            if (hWnd == host.control) return host.ControlMessage(hWnd, msg, wParam, lParam);
            if (hWnd == host.readout) return host.ReadoutMessage(hWnd, msg, wParam, lParam);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Changed(string reason) => Post(() => EnvironmentChanged?.Invoke(reason));

    private LRESULT ControlMessage(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (taskbarCreated != 0 && msg == taskbarCreated) { DestroyReadout(); Reconcile(true); Changed("taskbar re-created"); return Handled; }
        switch (msg)
        {
            case WM_UPDATE: Reconcile(false); return Handled;
            case WM_TIMER:
                if (wParam.Value == 2) AnimateStep(); else if (wParam.Value == 3) ShowTip(); else Reconcile(false);
                return Handled;
            case WM_DISPLAYCHANGE: Reconcile(true); Changed("display change"); return Handled;
            case WM_DPICHANGED: Reconcile(true); Changed("dpi change"); return Handled;
            case WM_THEMECHANGED: Reconcile(true); Changed("theme change"); return Handled;
            case WM_SETTINGCHANGE: Reconcile(true); Changed("setting change"); return Handled;
            case WM_CLOSE: KillTimer(hWnd, 1); DestroyReadout(); DestroyWindow(hWnd); return Handled;
            case WM_DESTROY: PostQuitMessage(0); return Handled;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static Point ClientPoint(LPARAM lParam) => new Point((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));

    private int ItemAt(Point p)
    {
        for (int i = 0; i < bounds.Count; i++) if (bounds[i].Contains(p)) return i;
        return -1;
    }

    private LRESULT ReadoutMessage(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE: return new LRESULT((nint)MA_NOACTIVATE);   // never take focus
            case WM_MOUSEMOVE:
                {
                    var index = ItemAt(ClientPoint(lParam));
                    if (!tracking)
                    {
                        var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE, hwndTrack = hWnd };
                        tracking = TrackMouseEvent(ref track);
                    }
                    if (index != hover) { hover = index; Redraw(); ArmTip(index); }
                    return Handled;
                }
            case WM_MOUSELEAVE:
                tracking = false; hover = -1; pressed = -1; Redraw(); HideTip();
                return Handled;
            case WM_LBUTTONDOWN:
                pressed = ItemAt(ClientPoint(lParam)); Redraw(); HideTip();
                return Handled;
            case WM_LBUTTONUP:
                {
                    var index = ItemAt(ClientPoint(lParam));
                    var was = pressed;
                    pressed = -1; Redraw();
                    if (index >= 0 && index == was)
                    {
                        var rect = bounds[index]; rect.Offset(currentScreenRect.Location);
                        Post(() => ItemClicked?.Invoke(index, rect));
                    }
                    return Handled;
                }
            case WM_RBUTTONUP:
                {
                    var point = ClientPoint(lParam);
                    ClientToScreen(hWnd, ref point);
                    Post(() => ContextMenuRequested?.Invoke(point));
                    return Handled;
                }
            case WM_SETCURSOR:
                SetCursor(LoadCursor(HINSTANCE.Null, hover >= 0 ? IDC_HAND : IDC_ARROW));
                return new LRESULT(1);
            case WM_DESTROY:
                readout = HWND.Null; lastSignature = "";
                return Handled;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Post(Action action)
    {
        if (ui != null) ui.Post(_ => action(), null);
        else action();
    }

    // MARK: Placement

    private void DestroyReadout()
    {
        var h = readout;
        readout = HWND.Null; lastSignature = ""; hover = pressed = -1; tracking = false; HideTip();
        lock (gate) currentScreenRect = Rectangle.Empty;
        if (!h.IsNull && IsWindow(h)) DestroyWindow(h);   // destroys its tooltip too (owned window)
    }

    private void Hide()
    {
        if (!readout.IsNull && IsWindow(readout)) ShowWindow(readout, SHOW_WINDOW_CMD.SW_HIDE);
        statusWindow.Hide();
        lock (gate) currentScreenRect = Rectangle.Empty;
        lastSignature = "";
    }

    private void RefreshButtons(TaskbarInfo info)
    {
        if ((DateTime.UtcNow - buttonsAt).TotalSeconds < 3 || Interlocked.CompareExchange(ref buttonsBusy, 1, 0) != 0) return;
        buttonsAt = DateTime.UtcNow;
        var window = info.Window; var notification = info.Notification;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { uiaButtons = TaskbarProbe.ButtonRectangles(window, notification); }
            finally { buttonsKnown = true; Interlocked.Exchange(ref buttonsBusy, 0); }
        });
    }

    private void Redraw()
    {
        if (readout.IsNull) return;
        List<ReadoutItem> current; lock (gate) current = items;
        if (current.Count == 0 || placer.Current is not Rectangle rect) return;
        var list = new List<Rectangle>();
        // Hover and pressed only change the pixels, never the position.
        using (var bmp = ReadoutPainter.Render(current, metrics, rect.Size, list, hitTestable: true, hover: hover, pressed: pressed))
            LayeredWindows.Present(readout, bmp, lastClient ?? rect.Location);
        windowUpdates++; Trace("readout paint: hover or pressed changed");
    }

    private void Reconcile(bool force)
    {
        List<ReadoutItem> current; bool show; ReadoutPosition where;
        lock (gate) { current = items; show = wanted; where = position; }
        var info = TaskbarProbe.Read(ReadoutClass);
        if (info != null) { edge = info.Edge; dpi = info.Dpi; }   // the panel needs these too
        if (info != null && info.Dpi != lastDpi) { lastDpi = info.Dpi; force = true; Changed("taskbar dpi " + info.Dpi); }
        if (show && current.Count > 0 && info == null)
            Report("The taskbar window (Shell_TrayWnd) was not found, so the readout cannot be placed.",
                "Stratoscope is running, but Windows' taskbar could not be found, so there is nowhere to show the readout yet. It appears as soon as the taskbar is there.");
        if (!show || current.Count == 0 || info == null || info.Hidden) { StepPlacer(null, true); return; }
        RefreshButtons(info);
        // The first place is only worked out once the task buttons are known (a few moments at start), so it does not have to move at once.
        if (!buttonsKnown && (DateTime.UtcNow - startedAt).TotalSeconds < 6) return;
        info.Buttons = info.Buttons.Concat(uiaButtons).ToList();

        SeeTaskbar();
        var m = controller.ForDpi(info.Dpi);
        lock (gate) metrics = m;
        // The size comes from the reserved widths (never from the content) and the target from the geometry alone.
        var desired = ReadoutPainter.Measure(current, m, info.Bounds.Height);
        var place = TaskbarLayout.Place(info.ToGeometry(), where, desired);
        if (place == null)
        {
            Report("There is no room for the readout: taskbar " + info.Bounds + " at the " + info.Edge + " edge, notification area " + info.Notification + ", " + info.Buttons.Count + " task buttons.",
                info.Edge == TaskbarEdge.Left || info.Edge == TaskbarEdge.Right
                    ? "Stratoscope is running, but it shows its readout on a taskbar at the bottom or the top of the screen, and yours is at the side."
                    : "Stratoscope is running, but there is not enough free room next to the clock on your taskbar. The readout appears as soon as there is room.");
            StepPlacer(null, false);
            return;
        }
        var origin = place.Value.Location;
        ScreenToClient(info.Window, ref origin);
        var target = new Rectangle(origin.X, origin.Y, place.Value.Width, place.Value.Height);

        if (!readout.IsNull && (!IsWindow(readout) || GetParent(readout) != info.Window)) { DestroyReadout(); placer.Step(DateTime.UtcNow, null); }
        var metricsChanged = m.Signature != lastMetricsSignature;
        lastMetricsSignature = m.Signature;
        if (TracePath != null)
            geometryNote = "notification area from " + info.Notification.Left + ", buttons " + info.Buttons.Count + " to " + (info.Buttons.Count == 0 ? 0 : info.Buttons.Where(b => b.Right <= info.Notification.Left + 1).Select(b => b.Right).DefaultIfEmpty(0).Max());
        var action = StepPlacer(target, metricsChanged || readout.IsNull);
        if (placer.Current is not Rectangle rect) return;

        if (readout.IsNull)
        {
            readout = LayeredWindows.Create(WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE, ReadoutClass, "Stratoscope readout",
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_CLIPSIBLINGS, rect.Width, rect.Height, info.Window);
            if (readout.IsNull)
            {
                Report("The readout window could not be created, Win32 error " + Marshal.GetLastPInvokeError() + ".",
                    "Stratoscope is running, but Windows would not let it create the readout window.");
                return;
            }
            force = true;
        }
        Report(null);

        // (the per-request bars are part of it: a request that starts, ends, or moves its bar by a pixel's worth repaints the readout)
        var signature = string.Join("|", current.Select(i => i.Text.Value + "," + i.Text.Unit + "," + i.State + "," + (i.Fraction.HasValue ? (int)(i.Fraction.Value * 100) : -1)
                + "," + string.Join(";", i.Bars.Select(b => (b.Reading ? "r" : "d") + (b.Reading ? (int)(b.Fraction * 100)
                    : b.TokensPerSecond is double v && i.ScaleMax > 0 ? (int)(Math.Min(1, v / i.ScaleMax) * 100) : -1)))))
            + "#" + rect.Width + "x" + rect.Height + "@" + m.Signature;
        var screen = Point.Empty;
        ClientToScreen(info.Window, ref screen);
        lock (gate) currentScreenRect = new Rectangle(rect.X + screen.X, rect.Y + screen.Y, rect.Width, rect.Height);   // follows a taskbar that slides, without a move
        if (!force && action.Kind == PlaceKind.None && signature == lastSignature)
        {
            KeepOnTop(info.Window);
            UpdateStatus();
            return;
        }

        // A move of the same size is eased (or jumps once when animations are off); anything else is one update of the layered window.
        var easeFrom = action.Kind == PlaceKind.Move && !metricsChanged && lastSize == rect.Size && controller.Current.Animations && lastClient.HasValue ? lastClient : null;
        if (easeFrom.HasValue) { animFrom = easeFrom.Value; animTo = rect.Location; animStart = Environment.TickCount64; SetTimer(control, 2, 16, null); }
        else KillTimer(control, 2);
        var list = new List<Rectangle>();
        using (var bmp = ReadoutPainter.Render(current, m, rect.Size, list, hitTestable: true, hover: hover, pressed: pressed))
            LayeredWindows.Present(readout, bmp, easeFrom ?? rect.Location);
        windowUpdates++;
        if (TracePath != null)
            Trace("readout paint: " + (force ? "forced (" + (metricsChanged ? "scale or theme" : "new window or environment change") + ")"
                : action.Kind != PlaceKind.None ? "placement " + action.Kind : "content changed") + " | " + string.Join(" ; ", current.Select(i => i.Text.Value + " " + i.State))
                + " | window " + currentScreenRect.X + "," + currentScreenRect.Y + " " + currentScreenRect.Width + "x" + currentScreenRect.Height
                + " | glyph centre x (screen px) " + string.Join(" ; ", current.Select((i, k) => (currentScreenRect.X + list[k].X + ReadoutPainter.GlyphPosition(i, m).GlyphCentre).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
        lastClient = easeFrom ?? rect.Location; lastSize = rect.Size;
        bounds = list;
        if (signature != lastSignature && action.Kind == PlaceKind.None) contentUpdates++;
        lastSignature = signature;
        // The accessible name of the window is what a screen reader says: one sentence per server.
        SetWindowText(readout, string.Join("; ", current.Select(i => i.Spoken)));
        ShowWindow(readout, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        KeepOnTop(info.Window);
        UpdateStatus();
    }

    private readonly TipWindow statusWindow = new TipWindow();
    private string? statusText;
    private DateTime statusUntil;

    /// <summary>
    /// Puts a short status next to the readout for one minute from now (null takes it away). Asking again with the same text does not start
    /// the minute again. Thread-safe; the host shows and hides it on its own thread.
    /// </summary>
    public void ShowStatus(string? text)
    {
        lock (gate)
        {
            if (text == statusText) return;
            statusText = text;
            statusUntil = DateTime.UtcNow.AddMinutes(1);
        }
    }

    /// <summary>
    /// Looks at the colour really on screen behind the readout: the corner of its own window, which is transparent there, shows the taskbar. A tinted
    /// or translucent taskbar is not the plain colour its theme suggests, and the glyph and text are checked against what is seen. The colour is
    /// rounded, so a slow fade does not repaint the readout every half second.
    /// </summary>
    private void SeeTaskbar()
    {
        Rectangle own;
        lock (gate) own = currentScreenRect;
        if (own.IsEmpty || readout.IsNull || !IsWindowVisible(readout)) return;
        if (LayeredWindows.ColorAt(new Point(own.X + 1, own.Y + 2)) is not Color seen) return;
        int Round(int v) => Math.Min(255, (v + 4) / 8 * 8);
        controller.TaskbarSeen = Color.FromArgb(255, Round(seen.R), Round(seen.G), Round(seen.B));
    }

    private void UpdateStatus()
    {
        string? text; DateTime until; Rectangle anchor;
        lock (gate) { text = statusText; until = statusUntil; anchor = currentScreenRect; }
        var wanted = text != null && DateTime.UtcNow < until && !readout.IsNull && !anchor.IsEmpty && IsWindowVisible(readout);
        if (!wanted) { if (statusWindow.IsShown) statusWindow.Hide(); return; }
        if (statusWindow.IsShown) return;
        var m = controller.ForDpi(dpi);
        statusWindow.Show(text!, anchor, System.Windows.Forms.Screen.FromRectangle(anchor).WorkingArea, preferAbove: edge != TaskbarEdge.Top, m.Panel, m.Scale);
    }

    private string? lastReport;
    private DateTime? unavailableSince;
    private bool unavailableShown;

    /// <summary>The readout has not been shown for a while: one sentence in plain words for the user, once per session. Raised on the UI thread.</summary>
    public event Action<string>? Unavailable;

    /// <summary>
    /// Writes why the readout is not shown to the error log, once per change, and tells the user in plain words when it stays that way for
    /// 20 seconds. <c>technical</c> null says that the readout is placed.
    /// </summary>
    private void Report(string? technical, string? plain = null)
    {
        if (technical == null) { lastReport = null; unavailableSince = null; return; }
        if (technical != lastReport) { lastReport = technical; DiagnosticsTrace.TraceWarning("The readout is not shown: " + technical); }
        unavailableSince ??= DateTime.UtcNow;
        if (unavailableShown || plain == null || (DateTime.UtcNow - unavailableSince.Value).TotalSeconds < 20) return;
        unavailableShown = true;
        Post(() => Unavailable?.Invoke(plain));
    }

    // MARK: Stable position

    private readonly ReadoutPlacer placer = new ReadoutPlacer();
    private string lastMetricsSignature = "";
    private Size lastSize;
    private Point? lastClient, animFrom, animTo = null;
    private long animStart;
    private int windowUpdates, contentUpdates, setWindowPosCalls;
    private string geometryNote = "";
    private DateTime statsAt = DateTime.UtcNow;
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("STRATOSCOPE_TRACE_MOVES") == "1" ? System.IO.Path.Combine(AppContext.BaseDirectory, "readout-moves.log") : null;

    /// <summary>Asks the placer, hides the window when it says so, and records every real change of position when tracing is on.</summary>
    private PlaceAction StepPlacer(Rectangle? target, bool immediate)
    {
        var action = placer.Step(DateTime.UtcNow, target, immediate);
        if (action.Kind == PlaceKind.Hide) { Hide(); lastClient = null; }
        if (action.Kind != PlaceKind.None) Trace(action.Kind + " " + (action.Kind == PlaceKind.Hide ? "" : action.Rect.ToString()) + (immediate ? " (immediate)" : "") + " | " + geometryNote);
        if (TracePath != null && (DateTime.UtcNow - statsAt).TotalMinutes >= 1)
        {
            statsAt = DateTime.UtcNow;
            Trace("stats: shows " + placer.Shows + ", moves " + placer.Moves + ", absorbed flaps " + placer.Absorbed + ", hides " + placer.Hides + ", content updates " + contentUpdates + ", window updates " + windowUpdates + ", SetWindowPos " + setWindowPosCalls);
        }
        return action;
    }

    /// <summary>True when STRATOSCOPE_TRACE_MOVES=1: callers build their trace lines only then.</summary>
    internal static bool Tracing => TracePath != null;

    internal static void Trace(string line)
    {
        if (TracePath == null) return;
        try { System.IO.File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line + Environment.NewLine); }
        catch (System.IO.IOException) { }
    }

    /// <summary>One frame of the ease to a new position: SetWindowPos only when the position really changes.</summary>
    private void AnimateStep()
    {
        if (readout.IsNull || !animFrom.HasValue || !animTo.HasValue) { KillTimer(control, 2); return; }
        var t = Math.Min(1.0, (Environment.TickCount64 - animStart) / 160.0);
        var eased = 1 - Math.Pow(1 - t, 3);
        var p = new Point((int)Math.Round(animFrom.Value.X + (animTo.Value.X - animFrom.Value.X) * eased), (int)Math.Round(animFrom.Value.Y + (animTo.Value.Y - animFrom.Value.Y) * eased));
        if (p != lastClient)
        {
            SetWindowPos(readout, HWND.Null, p.X, p.Y, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            setWindowPosCalls++;
            lastClient = p;
        }
        if (t >= 1.0) { KillTimer(control, 2); animFrom = null; }
    }

    /// <summary>The XAML layer of the taskbar can come to the front; keep the readout above it without activating anything.</summary>
    private void KeepOnTop(HWND taskbar)
    {
        if (readout.IsNull || GetWindow(taskbar, GET_WINDOW_CMD.GW_CHILD) == readout) return;
        SetWindowPos(readout, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    // MARK: Tooltip (the themed TipWindow, 700 ms after the pointer rests on an item, above it, never while the panel is open)

    private readonly TipWindow tipWindow = new TipWindow();
    private int tipItem = -1;
    /// <summary>True while the panel is open: there is no tooltip then.</summary>
    public volatile bool SuppressTips;

    private void ArmTip(int index)
    {
        HideTip();
        tipItem = index;
        if (index >= 0 && !control.IsNull) SetTimer(control, 3, 700, null);
    }

    private void HideTip()
    {
        if (!control.IsNull) KillTimer(control, 3);
        tipWindow.Hide();
        tipItem = -1;
    }

    private void ShowTip()
    {
        if (!control.IsNull) KillTimer(control, 3);
        List<ReadoutItem> current; lock (gate) current = items;
        if (SuppressTips || tipItem < 0 || tipItem >= current.Count || tipItem >= bounds.Count || readout.IsNull) return;
        var text = current[tipItem].Tip;
        if (string.IsNullOrEmpty(text)) return;       // nothing to add to what the item says: no tooltip
        var item = bounds[tipItem]; item.Offset(currentScreenRect.Location);
        var work = System.Windows.Forms.Screen.FromRectangle(item).WorkingArea;
        // Above the item (below on a taskbar at the top of the screen), in the palette of the panel.
        var metrics = controller.ForDpi(dpi);
        tipWindow.Show(text, item, work, preferAbove: edge != TaskbarEdge.Top, metrics.Panel, metrics.Scale);
    }
}
