using System.Drawing;
using System.Runtime.InteropServices;

namespace Stratoscope.Windows;

/// <summary>
/// What the windows need to know about the monitors. One source for the panel, the readout and the tray icon:
/// the effective DPI of the monitor an item appears on. It is asked for a screen point, never taken from a window
/// that may have been created on another monitor.
/// </summary>
internal interface IMonitors
{
    /// <summary>The effective DPI (96 at 100 %, 144 at 150 %) of the monitor that contains the point.</summary>
    int DpiAt(Point screenPoint);
    /// <summary>The work area (the monitor without the taskbar) of the monitor that contains the point, in screen pixels.</summary>
    Rectangle WorkAreaAt(Point screenPoint);
}

internal sealed class MonitorInfo
{
    public IntPtr Handle { get; set; }
    public Rectangle Bounds { get; set; }
    public Rectangle WorkArea { get; set; }
    public int Dpi { get; set; }
    public bool Primary { get; set; }
}

/// <summary>The real monitors, through documented Win32 calls (GetDpiForMonitor, MONITORINFO). Read-only.</summary>
internal sealed class SystemMonitors : IMonitors
{
    public static readonly SystemMonitors Instance = new SystemMonitors();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorProc proc, IntPtr data);
    private delegate bool EnumMonitorProc(IntPtr monitor, IntPtr dc, ref Native.RECT rect, IntPtr data);

    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>The effective DPI of a monitor: what Settings > Display > Scale says (100 % is 96).</summary>
    public static int OfMonitor(IntPtr monitor)
    {
        try
        {
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0 && x >= 96) return (int)x;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        try { var system = GetDpiForSystem(); if (system >= 96) return (int)system; }
        catch (EntryPointNotFoundException) { }
        return 96;
    }

    /// <summary>The DPI of the monitor a window is mostly on.</summary>
    public static int OfWindow(IntPtr window) =>
        window == IntPtr.Zero ? 96 : OfMonitor(Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST));

    public int DpiAt(Point p) => OfMonitor(Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST));

    public Rectangle WorkAreaAt(Point p)
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetMonitorInfo(monitor, ref info) ? info.rcWork.ToRectangle() : System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
    }

    /// <summary>Every monitor, with its physical bounds, work area and DPI.</summary>
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumMonitorProc visit = (IntPtr monitor, IntPtr dc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (Native.GetMonitorInfo(monitor, ref info))
                list.Add(new MonitorInfo
                {
                    Handle = monitor, Bounds = info.rcMonitor.ToRectangle(), WorkArea = info.rcWork.ToRectangle(),
                    Dpi = OfMonitor(monitor), Primary = (info.dwFlags & 1) != 0,
                });
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, visit, IntPtr.Zero);
        return list;
    }
}

/// <summary>The size of the panel at a DPI, as pure functions.</summary>
internal static class PanelSizing
{
    public static float Scale(int dpi) => Math.Max(96, dpi) / 96f;

    /// <summary>The panel's client size in physical pixels for a content height in logical pixels (not capped).</summary>
    public static Size ClientSize(float logicalHeight, int dpi) => ClientSize(logicalHeight, Scale(dpi));

    public static Size ClientSize(float logicalHeight, float scale) =>
        new Size((int)Math.Ceiling(PanelPainter.Width * scale), (int)Math.Ceiling(logicalHeight * scale));

    /// <summary>The distance kept from the taskbar and the screen edge.</summary>
    public static int Margin(int dpi) => (int)Math.Round(8 * Scale(dpi));

    /// <summary>The tallest the panel may be: the work area less the margin above and below. A taller panel scrolls.</summary>
    public static int MaxHeight(Rectangle workArea, int margin) => Math.Max(1, workArea.Height - 2 * margin);
}

/// <summary>Where and how large the panel is when it opens next to an anchor.</summary>
internal sealed class PanelLayout
{
    public PanelLayout(int dpi, float scale, Size size, Point location, Rectangle workArea, float contentHeight)
    {
        Dpi = dpi; Scale = scale; Size = size; Location = location; WorkArea = workArea; ContentHeight = contentHeight;
    }
    public int Dpi { get; }
    /// <summary>Device pixels per logical pixel of the panel: DPI scale times the Windows text size.</summary>
    public float Scale { get; }
    public Size Size { get; }
    public Point Location { get; }
    public Rectangle WorkArea { get; }
    /// <summary>The height of all the content, in logical pixels.</summary>
    public float ContentHeight { get; }
    public Rectangle Bounds => new Rectangle(Location, Size);
    /// <summary>The content is taller than the window: it scrolls.</summary>
    public bool Scrolls => ContentHeight * Scale > Size.Height + 0.5f;

    /// <summary>
    /// The layout for an anchor (screen pixels): the monitor the anchor is on decides the DPI, the work area and where the panel goes.
    /// A panel taller than the work area is capped to it.
    /// </summary>
    public static PanelLayout For(Rectangle anchor, float logicalHeight, TaskbarEdge edge, UiController ui)
    {
        var centre = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
        var metrics = ui.At(centre);
        return Compute(anchor, logicalHeight, edge, metrics.Dpi, metrics.PanelScale, ui.Monitors.WorkAreaAt(centre));
    }

    /// <summary>The layout from plain values: no monitors, no window. The offscreen path and the tests use it.</summary>
    public static PanelLayout Compute(Rectangle anchor, float logicalHeight, TaskbarEdge edge, int dpi, float scale, Rectangle work)
    {
        var margin = PanelSizing.Margin(dpi);
        var size = PanelSizing.ClientSize(logicalHeight, scale);
        size.Height = Math.Min(size.Height, PanelSizing.MaxHeight(work, margin));
        size.Width = Math.Min(size.Width, Math.Max(1, work.Width - 2 * margin));
        return new PanelLayout(dpi, scale, size, TaskbarLayout.PlacePanel(anchor, size, work, edge, margin), work, logicalHeight);
    }
}
