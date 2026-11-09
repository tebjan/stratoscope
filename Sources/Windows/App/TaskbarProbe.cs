using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using static Windows.Win32.PInvoke;

namespace Stratoscope.Windows;

/// <summary>Where the taskbar is and what it looks like right now. Read-only: it only asks Windows.</summary>
internal sealed class TaskbarInfo
{
    public HWND Window { get; set; }
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

    public static HWND FindTaskbar() => FindWindow(TaskbarClass, null);

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
        if (tb.IsNull || !GetWindowRect(tb, out var r)) return null;
        var info = new TaskbarInfo { Window = tb, Bounds = r };
        info.Dpi = SystemMonitors.OfWindow(tb);

        var monitor = MonitorFromWindow(tb, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref mi)) { info.Monitor = mi.rcMonitor; info.WorkArea = mi.rcWork; }
        else { info.Monitor = Screen(info.Bounds); info.WorkArea = info.Monitor; }
        info.Edge = TaskbarLayout.EdgeOf(info.Bounds, info.Monitor);

        var notify = FindWindowEx(tb, HWND.Null, "TrayNotifyWnd", null);
        if (!notify.IsNull && GetWindowRect(notify, out var nr) && nr.Width > 0) info.Notification = nr;
        else info.Notification = new Rectangle(info.Bounds.Right - TaskbarLayout.Scale(240, info.Dpi), info.Bounds.Top, TaskbarLayout.Scale(240, info.Dpi), info.Bounds.Height);

        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = tb };
        info.AutoHide = ((uint)SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0;
        info.Hidden = TaskbarLayout.IsHidden(info.Bounds, info.Monitor, info.Edge, IsWindowVisible(tb));
        info.SystemDark = darkOverride ?? WindowsTheme.SystemDark();
        info.Centred = CentredButtons();
        info.WidgetsOn = WidgetsButton();
        info.Buttons = OtherChildren(tb, info, ownClass);
        return info;
    }

    private static Rectangle Screen(Rectangle around) =>
        System.Windows.Forms.Screen.FromRectangle(around).Bounds;

    /// <summary>Visible child windows of the taskbar that are not plumbing and not ours (other taskbar tools), as rectangles.</summary>
    private static List<Rectangle> OtherChildren(HWND taskbar, TaskbarInfo info, string ownClass)
    {
        var list = new List<Rectangle>();
        EnumChildWindows(taskbar, (h, _) =>
        {
            if (GetParent(h) != taskbar || !IsWindowVisible(h)) return true;
            Span<char> name = stackalloc char[128];
            var cls = new string(name[..GetClassName(h, name)]);
            if (Plumbing.Contains(cls) || cls == ownClass) return true;
            if (!GetWindowRect(h, out var r) || r.Width <= 0) return true;
            if (r.left >= info.Notification.Left) return true;
            list.Add(r);
            return true;
        }, 0);
        return list;
    }

    /// <summary>
    /// The task, start, search and widgets buttons of the Windows 11 taskbar are drawn by XAML and have no windows of their own,
    /// so their rectangles come from UI Automation (read-only). Returns those left of the notification area.
    /// </summary>
    public static List<Rectangle> ButtonRectangles(HWND taskbar, Rectangle notification)
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
