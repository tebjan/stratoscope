using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Stratoscope.Windows;

/// <summary>Where the taskbar is and what it looks like right now. Read-only: it only asks Windows.</summary>
internal sealed class TaskbarInfo
{
    public IntPtr Window { get; set; }
    public Rectangle Bounds { get; set; }
    public Rectangle Notification { get; set; }
    public Rectangle Monitor { get; set; }
    public Rectangle WorkArea { get; set; }
    public int Dpi { get; set; } = 96;
    public TaskbarEdge Edge { get; set; } = TaskbarEdge.Bottom;
    public bool AutoHide { get; set; }
    /// <summary>The taskbar is not on screen right now (auto-hidden, or a full-screen app covers it).</summary>
    public bool Hidden { get; set; }
    public bool SystemDark { get; set; }
    /// <summary>Taskbar buttons are centred (Settings > Personalization > Taskbar > Taskbar alignment).</summary>
    public bool Centred { get; set; } = true;
    public bool WidgetsOn { get; set; }
    /// <summary>The buttons left of the notification area, from UI Automation, plus other tools' windows.</summary>
    public List<Rectangle> Buttons { get; set; } = new List<Rectangle>();

    public TaskbarLayout.Geometry ToGeometry() => new TaskbarLayout.Geometry { Bounds = Bounds, Notification = Notification, Buttons = Buttons, Dpi = Dpi, Edge = Edge };
}

internal static class TaskbarProbe
{
    public const string TaskbarClass = "Shell_TrayWnd";
    private const string Advanced = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // Window classes of the taskbar's own plumbing: these are not "other content" for placement purposes.
    private static readonly HashSet<string> Plumbing = new HashSet<string>(StringComparer.Ordinal)
    {
        "Windows.UI.Composition.DesktopWindowContentBridge", "Windows.UI.Input.InputSite.WindowClass", "Windows.UI.Core.CoreWindow",
        "TrayNotifyWnd", "TrayDummySearchControl", "Start", "ReBarWindow32", "MSTaskSwWClass", "MSTaskListWClass",
    };

    public static IntPtr FindTaskbar() => Native.FindWindow(TaskbarClass, null);

    /// <summary>Taskbar alignment from the registry: TaskbarAl is 0 for left-aligned and 1 (the default) for centred.</summary>
    public static bool CentredButtons()
    {
        try { return !(Registry.GetValue(Advanced, "TaskbarAl", 1) is int v && v == 0); }
        catch (Exception) { return true; }
    }

    public static bool WidgetsButton()
    {
        try { return Registry.GetValue(Advanced, "TaskbarDa", 1) is int v && v != 0; }
        catch (Exception) { return false; }
    }

    public static TaskbarInfo? Read(string ownClass, bool? darkOverride = null)
    {
        var tb = FindTaskbar();
        if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out var r)) return null;
        var info = new TaskbarInfo { Window = tb, Bounds = r.ToRectangle() };
        info.Dpi = SystemMonitors.OfWindow(tb);

        var monitor = Native.MonitorFromWindow(tb, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
        if (Native.GetMonitorInfo(monitor, ref mi)) { info.Monitor = mi.rcMonitor.ToRectangle(); info.WorkArea = mi.rcWork.ToRectangle(); }
        else { info.Monitor = Screen(info.Bounds); info.WorkArea = info.Monitor; }
        info.Edge = TaskbarLayout.EdgeOf(info.Bounds, info.Monitor);

        var notify = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Right > nr.Left) info.Notification = nr.ToRectangle();
        else info.Notification = new Rectangle(info.Bounds.Right - TaskbarLayout.Scale(240, info.Dpi), info.Bounds.Top, TaskbarLayout.Scale(240, info.Dpi), info.Bounds.Height);

        var abd = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = tb };
        info.AutoHide = ((int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd) & Native.ABS_AUTOHIDE) != 0;
        info.Hidden = TaskbarLayout.IsHidden(info.Bounds, info.Monitor, info.Edge, Native.IsWindowVisible(tb));
        info.SystemDark = darkOverride ?? WindowsTheme.SystemDark();
        info.Centred = CentredButtons();
        info.WidgetsOn = WidgetsButton();
        info.Buttons = OtherChildren(tb, info, ownClass);
        return info;
    }

    private static Rectangle Screen(Rectangle around) =>
        System.Windows.Forms.Screen.FromRectangle(around).Bounds;

    /// <summary>Visible child windows of the taskbar that are not plumbing and not ours (other taskbar tools), as rectangles.</summary>
    private static List<Rectangle> OtherChildren(IntPtr taskbar, TaskbarInfo info, string ownClass)
    {
        var list = new List<Rectangle>();
        Native.EnumChildWindows(taskbar, (h, _) =>
        {
            if (Native.GetParent(h) != taskbar || !Native.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(128);
            Native.GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (Plumbing.Contains(cls) || cls == ownClass) return true;
            if (!Native.GetWindowRect(h, out var r) || r.Right <= r.Left) return true;
            if (r.Left >= info.Notification.Left) return true;
            list.Add(r.ToRectangle());
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// The task, start, search and widgets buttons of the Windows 11 taskbar are drawn by XAML and have no windows of their own,
    /// so their rectangles come from UI Automation (read-only). Returns those left of the notification area.
    /// </summary>
    public static List<Rectangle> ButtonRectangles(IntPtr taskbar, Rectangle notification)
    {
        var list = new List<Rectangle>();
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
            {
                var rect = b.Current.BoundingRectangle;
                if (rect.IsEmpty || double.IsInfinity(rect.Right) || double.IsInfinity(rect.Left)) continue;
                var r = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
                if (r.Left >= notification.Left - 2 || r.Right > notification.Left) continue;   // the buttons of the notification area
                list.Add(r);
            }
        }
        catch (Exception) { }
        return list;
    }
}
