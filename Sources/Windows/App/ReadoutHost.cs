using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>
/// Puts the readout into the Windows taskbar.
///
/// Windows 11 has no supported API for third-party taskbar content (deskbands are gone), so this does what the other
/// taskbar tools do: a borderless, layered child window of the taskbar window (Shell_TrayWnd), placed in free space (TaskbarLayout).
/// The geometry is checked a few times a second, so a change of DPI, monitor, taskbar position, auto-hide, theme or task-button count
/// is followed, and a restart of explorer.exe (the TaskbarCreated message) re-creates the window.
///
/// Everything runs on its own thread with its own message loop: the taskbar and this thread share an input queue while
/// the child window exists, so this thread never waits for the UI thread or the network.
/// </summary>
internal sealed class ReadoutHost : IDisposable
{
    public const string ControlClass = "Stratoscope.TaskbarControl";
    public const string ReadoutClass = "Stratoscope.TaskbarReadout";
    private const uint WM_UPDATE = Native.WM_APP + 1;
    private const int TimerMilliseconds = 500;

    private static readonly ConcurrentDictionary<IntPtr, ReadoutHost> Hosts = new ConcurrentDictionary<IntPtr, ReadoutHost>();
    private static readonly Native.WndProc Proc = WindowProc;
    private static bool classesRegistered;

    private readonly SynchronizationContext? ui;
    private readonly UiController controller;
    private readonly object gate = new object();
    private Thread? thread;
    private IntPtr control;
    private IntPtr readout;
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
    /// <summary>The window handle of the readout (zero while not created). For checks from outside.</summary>
    public IntPtr ReadoutWindow => readout;
    public IntPtr ControlWindow => control;

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
        if (control != IntPtr.Zero) Native.PostMessage(control, WM_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Redraws everything, for example after the user changed the UI scale. Thread-safe.</summary>
    public void Refresh()
    {
        lock (gate) lastSignature = "";
        if (control != IntPtr.Zero) Native.PostMessage(control, WM_UPDATE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        var c = control;
        if (c != IntPtr.Zero) Native.PostMessage(c, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        thread?.Join(3000);
    }

    // MARK: Thread

    private void Run(ManualResetEventSlim ready)
    {
        RegisterClasses();
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        control = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, ControlClass, "Stratoscope taskbar control", Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (control != IntPtr.Zero)
        {
            Hosts[control] = this;
            Native.SetTimer(control, new IntPtr(1), TimerMilliseconds, IntPtr.Zero);
        }
        ready.Set();
        if (control == IntPtr.Zero) return;
        Reconcile(true);
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        DestroyReadout(); tipWindow.Dispose();
        Hosts.TryRemove(control, out _);
    }

    private static void RegisterClasses()
    {
        if (classesRegistered) return;
        var cursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW);
        foreach (var name in new[] { ControlClass, ReadoutClass })
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)), lpfnWndProc = Proc, hInstance = Native.GetModuleHandle(null),
                hCursor = cursor, lpszClassName = name,
            };
            Native.RegisterClassEx(ref wc);   // a second call for the same class fails harmlessly
        }
        classesRegistered = true;
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        foreach (var host in Hosts.Values)
        {
            if (hWnd == host.control) return host.ControlMessage(hWnd, msg, wParam, lParam);
            if (hWnd == host.readout) return host.ReadoutMessage(hWnd, msg, wParam, lParam);
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Changed(string reason) => Post(() => EnvironmentChanged?.Invoke(reason));

    private IntPtr ControlMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (taskbarCreated != 0 && msg == taskbarCreated) { DestroyReadout(); Reconcile(true); Changed("taskbar re-created"); return IntPtr.Zero; }
        switch (msg)
        {
            case WM_UPDATE: Reconcile(false); return IntPtr.Zero;
            case Native.WM_TIMER:
                if (wParam.ToInt64() == 2) AnimateStep(); else if (wParam.ToInt64() == 3) ShowTip(); else Reconcile(false);
                return IntPtr.Zero;
            case Native.WM_DISPLAYCHANGE: Reconcile(true); Changed("display change"); return IntPtr.Zero;
            case Native.WM_DPICHANGED: Reconcile(true); Changed("dpi change"); return IntPtr.Zero;
            case Native.WM_THEMECHANGED: Reconcile(true); Changed("theme change"); return IntPtr.Zero;
            case Native.WM_SETTINGCHANGE: Reconcile(true); Changed("setting change"); return IntPtr.Zero;
            case Native.WM_CLOSE: Native.KillTimer(hWnd, new IntPtr(1)); DestroyReadout(); Native.DestroyWindow(hWnd); return IntPtr.Zero;
            case Native.WM_DESTROY: Native.PostQuitMessage(0); return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static Point ClientPoint(IntPtr lParam) => new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));

    private int ItemAt(Point p)
    {
        for (int i = 0; i < bounds.Count; i++) if (bounds[i].Contains(p)) return i;
        return -1;
    }

    private IntPtr ReadoutMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE: return Native.MA_NOACTIVATE;   // never take focus
            case Native.WM_MOUSEMOVE:
                {
                    var index = ItemAt(ClientPoint(lParam));
                    if (!tracking)
                    {
                        var track = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)), dwFlags = Native.TME_LEAVE, hwndTrack = hWnd };
                        tracking = Native.TrackMouseEvent(ref track);
                    }
                    if (index != hover) { hover = index; Redraw(); ArmTip(index); }
                    return IntPtr.Zero;
                }
            case Native.WM_MOUSELEAVE:
                tracking = false; hover = -1; pressed = -1; Redraw(); HideTip();
                return IntPtr.Zero;
            case Native.WM_LBUTTONDOWN:
                pressed = ItemAt(ClientPoint(lParam)); Redraw(); HideTip();
                return IntPtr.Zero;
            case Native.WM_LBUTTONUP:
                {
                    var index = ItemAt(ClientPoint(lParam));
                    var was = pressed;
                    pressed = -1; Redraw();
                    if (index >= 0 && index == was)
                    {
                        var rect = bounds[index]; rect.Offset(currentScreenRect.Location);
                        Post(() => ItemClicked?.Invoke(index, rect));
                    }
                    return IntPtr.Zero;
                }
            case Native.WM_RBUTTONUP:
                {
                    var p = new Native.POINT { X = ClientPoint(lParam).X, Y = ClientPoint(lParam).Y };
                    Native.ClientToScreen(hWnd, ref p);
                    var point = new Point(p.X, p.Y);
                    Post(() => ContextMenuRequested?.Invoke(point));
                    return IntPtr.Zero;
                }
            case Native.WM_SETCURSOR:
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, hover >= 0 ? Native.IDC_HAND : Native.IDC_ARROW));
                return new IntPtr(1);
            case Native.WM_DESTROY:
                readout = IntPtr.Zero; lastSignature = "";
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
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
        readout = IntPtr.Zero; lastSignature = ""; hover = pressed = -1; tracking = false; HideTip();
        lock (gate) currentScreenRect = Rectangle.Empty;
        if (h != IntPtr.Zero && Native.IsWindow(h)) Native.DestroyWindow(h);   // destroys its tooltip too (owned window)
    }

    private void Hide()
    {
        if (readout != IntPtr.Zero && Native.IsWindow(readout)) Native.ShowWindow(readout, Native.SW_HIDE);
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
        if (readout == IntPtr.Zero) return;
        List<ReadoutItem> current; lock (gate) current = items;
        if (current.Count == 0 || placer.Current is not Rectangle rect) return;
        var list = new List<Rectangle>();
        // Hover and pressed only change the pixels, never the position.
        using (var bmp = ReadoutPainter.Render(current, metrics, rect.Size, list, hitTestable: true, hover: hover, pressed: pressed))
            Present(readout, bmp, lastClient ?? rect.Location);
        windowUpdates++; Trace("readout paint: hover or pressed changed");
    }

    private IntPtr GetTaskbar() => TaskbarProbe.FindTaskbar();

    private void Reconcile(bool force)
    {
        List<ReadoutItem> current; bool show; ReadoutPosition where;
        lock (gate) { current = items; show = wanted; where = position; }
        var info = TaskbarProbe.Read(ReadoutClass);
        if (info != null) { edge = info.Edge; dpi = info.Dpi; }   // the panel opened from the tray icon needs these too
        if (info != null && info.Dpi != lastDpi) { lastDpi = info.Dpi; force = true; Changed("taskbar dpi " + info.Dpi); }
        if (!show || current.Count == 0 || info == null || info.Hidden) { StepPlacer(null, true); return; }
        RefreshButtons(info);
        // The first place is only worked out once the task buttons are known (a few moments at start), so it does not have to move at once.
        if (!buttonsKnown && (DateTime.UtcNow - startedAt).TotalSeconds < 6) return;
        info.Buttons = info.Buttons.Concat(uiaButtons).ToList();

        var m = controller.ForDpi(info.Dpi);
        lock (gate) metrics = m;
        // The size comes from the reserved widths (never from the content) and the target from the geometry alone.
        var desired = ReadoutPainter.Measure(current, m, info.Bounds.Height);
        var place = TaskbarLayout.Place(info.ToGeometry(), where, desired);
        if (place == null) { StepPlacer(null, false); return; }
        var origin = new Native.POINT { X = place.Value.Left, Y = place.Value.Top };
        Native.ScreenToClient(info.Window, ref origin);
        var target = new Rectangle(origin.X, origin.Y, place.Value.Width, place.Value.Height);

        if (readout != IntPtr.Zero && (!Native.IsWindow(readout) || Native.GetParent(readout) != info.Window)) { DestroyReadout(); placer.Step(DateTime.UtcNow, null); }
        var metricsChanged = m.Signature != lastMetricsSignature;
        lastMetricsSignature = m.Signature;
        if (TracePath != null)
            geometryNote = "notification area from " + info.Notification.Left + ", buttons " + info.Buttons.Count + " to " + (info.Buttons.Count == 0 ? 0 : info.Buttons.Where(b => b.Right <= info.Notification.Left + 1).Select(b => b.Right).DefaultIfEmpty(0).Max());
        var action = StepPlacer(target, metricsChanged || readout == IntPtr.Zero);
        if (placer.Current is not Rectangle rect) return;

        if (readout == IntPtr.Zero)
        {
            readout = Native.CreateWindowEx(Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE, ReadoutClass, "Stratoscope readout",
                Native.WS_CHILD | Native.WS_CLIPSIBLINGS, 0, 0, rect.Width, rect.Height, info.Window, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (readout == IntPtr.Zero) return;
            force = true;
        }

        var signature = string.Join("|", current.Select(i => i.Text.Value + "," + i.Text.Unit + "," + i.State + "," + (i.Fraction.HasValue ? (int)(i.Fraction.Value * 100) : -1)))
            + "#" + rect.Width + "x" + rect.Height + "@" + m.Signature;
        var screen = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(info.Window, ref screen);
        lock (gate) currentScreenRect = new Rectangle(rect.X + screen.X, rect.Y + screen.Y, rect.Width, rect.Height);   // follows a taskbar that slides, without a move
        if (!force && action.Kind == PlaceKind.None && signature == lastSignature)
        {
            KeepOnTop(info.Window);
            return;
        }

        // A move of the same size is eased (or jumps once when animations are off); anything else is one update of the layered window.
        var easeFrom = action.Kind == PlaceKind.Move && !metricsChanged && lastSize == rect.Size && controller.Current.Animations && lastClient.HasValue ? lastClient : null;
        if (easeFrom.HasValue) { animFrom = easeFrom.Value; animTo = rect.Location; animStart = Environment.TickCount64; Native.SetTimer(control, new IntPtr(2), 16, IntPtr.Zero); }
        else Native.KillTimer(control, new IntPtr(2));
        var list = new List<Rectangle>();
        using (var bmp = ReadoutPainter.Render(current, m, rect.Size, list, hitTestable: true, hover: hover, pressed: pressed))
            Present(readout, bmp, easeFrom ?? rect.Location);
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
        Native.SetWindowText(readout, string.Join("; ", current.Select(i => i.Spoken)));
        Native.ShowWindow(readout, Native.SW_SHOWNOACTIVATE);
        KeepOnTop(info.Window);
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
        if (readout == IntPtr.Zero || !animFrom.HasValue || !animTo.HasValue) { Native.KillTimer(control, new IntPtr(2)); return; }
        var t = Math.Min(1.0, (Environment.TickCount64 - animStart) / 160.0);
        var eased = 1 - Math.Pow(1 - t, 3);
        var p = new Point((int)Math.Round(animFrom.Value.X + (animTo.Value.X - animFrom.Value.X) * eased), (int)Math.Round(animFrom.Value.Y + (animTo.Value.Y - animFrom.Value.Y) * eased));
        if (p != lastClient)
        {
            Native.SetWindowPos(readout, IntPtr.Zero, p.X, p.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            setWindowPosCalls++;
            lastClient = p;
        }
        if (t >= 1.0) { Native.KillTimer(control, new IntPtr(2)); animFrom = null; }
    }

    /// <summary>The XAML layer of the taskbar can come to the front; keep the readout above it without activating anything.</summary>
    private void KeepOnTop(IntPtr taskbar)
    {
        if (readout == IntPtr.Zero || Native.GetWindow(taskbar, Native.GW_CHILD) == readout) return;
        Native.SetWindowPos(readout, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
        if (index >= 0 && control != IntPtr.Zero) Native.SetTimer(control, new IntPtr(3), 700, IntPtr.Zero);
    }

    private void HideTip()
    {
        if (control != IntPtr.Zero) Native.KillTimer(control, new IntPtr(3));
        tipWindow.Hide();
        tipItem = -1;
    }

    private void ShowTip()
    {
        if (control != IntPtr.Zero) Native.KillTimer(control, new IntPtr(3));
        List<ReadoutItem> current; lock (gate) current = items;
        if (SuppressTips || tipItem < 0 || tipItem >= current.Count || tipItem >= bounds.Count || readout == IntPtr.Zero) return;
        var text = current[tipItem].Tip;
        if (string.IsNullOrEmpty(text)) return;       // nothing to add to what the item says: no tooltip
        var item = bounds[tipItem]; item.Offset(currentScreenRect.Location);
        var work = System.Windows.Forms.Screen.FromRectangle(item).WorkingArea;
        // Above the item (below on a taskbar at the top of the screen), in the palette of the panel.
        var metrics = controller.ForDpi(dpi);
        tipWindow.Show(text, item, work, preferAbove: edge != TaskbarEdge.Top, metrics.Panel, metrics.Scale);
    }


    /// <summary>Gives the window its pixels: a premultiplied 32-bit bitmap through UpdateLayeredWindow, so per-pixel transparency works.</summary>
    internal static void Present(IntPtr window, Bitmap bmp, Point positionInParent)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = bmp.Width, biHeight = -bmp.Height, biPlanes = 1, biBitCount = 32,
        };
        var dib = Native.CreateDIBSection(memDc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = IntPtr.Zero;
        try
        {
            if (dib == IntPtr.Zero) return;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bits, Math.Min(bytes.Length, bmp.Width * 4 * bmp.Height));
            }
            finally { bmp.UnlockBits(data); }
            old = Native.SelectObject(memDc, dib);
            var dst = new Native.POINT { X = positionInParent.X, Y = positionInParent.Y };
            var size = new Native.SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            Native.UpdateLayeredWindow(window, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
