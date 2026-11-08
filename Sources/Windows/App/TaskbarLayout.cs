using System.Drawing;
using Stratoscope.Core;

namespace Stratoscope.Windows;

internal enum TaskbarEdge { Left, Top, Right, Bottom }

/// <summary>
/// The rectangle maths for the taskbar readout and for the panel that opens from it. Pure functions of pixel
/// rectangles, so they are unit-tested without a taskbar. All rectangles are in screen pixels.
/// </summary>
internal static class TaskbarLayout
{
    /// <summary>The share of the taskbar width the readout may use when the position of the task buttons is unknown.</summary>
    public const double FallbackLeftShare = 0.70;

    /// <summary>The smallest readout worth showing, in logical pixels.</summary>
    public const int MinimumWidth = 64;

    /// <summary>The edge of the monitor the taskbar sits on.</summary>
    public static TaskbarEdge EdgeOf(Rectangle taskbar, Rectangle monitor)
    {
        if (taskbar.Width >= taskbar.Height)
            return taskbar.Top + taskbar.Height / 2 < monitor.Top + monitor.Height / 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        return taskbar.Left + taskbar.Width / 2 < monitor.Left + monitor.Width / 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    public static int Scale(int logical, int dpi) => (int)Math.Round(logical * dpi / 96.0);

    /// <summary>
    /// Where the readout goes on a horizontal taskbar: right-aligned, directly left of the notification area, vertically
    /// centred, never over the task buttons. Returns null when it does not fit, or when the taskbar is vertical
    /// (the notification-area icon is then the only display).
    /// </summary>
    /// <param name="taskbar">The taskbar window.</param>
    /// <param name="notification">The notification area (clock and tray icons), the right limit.</param>
    /// <param name="taskButtons">The union of the start, search, widget and task buttons that lie left of the notification area, or null when unknown.</param>
    /// <param name="desired">The size the readout would like.</param>
    /// <param name="dpi">The DPI of the taskbar's monitor.</param>
    public static Rectangle? PlaceReadout(Rectangle taskbar, Rectangle notification, Rectangle? taskButtons, Size desired, int dpi, TaskbarEdge edge)
    {
        if (edge == TaskbarEdge.Left || edge == TaskbarEdge.Right) return null;
        if (desired.Width <= 0 || desired.Height <= 0 || taskbar.Width <= 0 || taskbar.Height <= 0) return null;
        var gap = Scale(8, dpi);
        var right = notification.Width > 0 ? notification.Left - gap : taskbar.Right - gap;
        int left;
        if (taskButtons is Rectangle b && b.Width > 0) left = Math.Max(taskbar.Left, b.Right + gap);
        else left = taskbar.Left + (int)(taskbar.Width * FallbackLeftShare);
        var available = right - left;
        if (available < Scale(MinimumWidth, dpi)) return null;
        var width = Math.Min(desired.Width, available);
        var height = Math.Min(desired.Height, taskbar.Height);
        return new Rectangle(right - width, taskbar.Top + (taskbar.Height - height) / 2, width, height);
    }

    /// <summary>What is known about the taskbar when the readout is placed. All rectangles in screen pixels.</summary>
    public sealed class Geometry
    {
        public Rectangle Bounds { get; set; }
        public Rectangle Notification { get; set; }
        /// <summary>Every start, search, widgets, task and other-tool button left of the notification area.</summary>
        public List<Rectangle> Buttons { get; set; } = new List<Rectangle>();
        public int Dpi { get; set; } = 96;
        public TaskbarEdge Edge { get; set; } = TaskbarEdge.Bottom;
    }

    /// <summary>
    /// Groups buttons that touch or nearly touch into clusters, left to right. A centred Windows 11 taskbar has the main cluster (start,
    /// search, tasks) in the middle and, with widgets on, one small cluster at the far left.
    /// </summary>
    public static List<Rectangle> Clusters(IEnumerable<Rectangle> buttons, int dpi)
    {
        var maxGap = Scale(24, dpi);
        var result = new List<Rectangle>();
        foreach (var b in buttons.Where(b => b.Width > 0).OrderBy(b => b.Left))
        {
            if (result.Count > 0 && b.Left - result[result.Count - 1].Right <= maxGap)
                result[result.Count - 1] = Rectangle.FromLTRB(result[result.Count - 1].Left, Math.Min(result[result.Count - 1].Top, b.Top), Math.Max(result[result.Count - 1].Right, b.Right), Math.Max(result[result.Count - 1].Bottom, b.Bottom));
            else result.Add(b);
        }
        return result;
    }

    /// <summary>True when the taskbar is not on screen: auto-hidden (slid out) or covered by a full-screen app.</summary>
    public static bool IsHidden(Rectangle taskbar, Rectangle monitor, TaskbarEdge edge, bool windowVisible)
    {
        if (!windowVisible) return true;
        var shown = Rectangle.Intersect(taskbar, monitor);
        var horizontal = edge == TaskbarEdge.Top || edge == TaskbarEdge.Bottom;
        var thickness = horizontal ? taskbar.Height : taskbar.Width;
        var visible = horizontal ? shown.Height : shown.Width;
        return visible < thickness / 2;
    }

    /// <summary>
    /// Where the readout goes, by the user's choice. Never over another item: when the chosen place has no room, the readout goes to the
    /// place next to the notification area, and when that has no room either it is not shown.
    /// <para>tray: directly left of the notification area. after-buttons: right after the last task button, so it moves with the buttons.
    /// left: at the left edge, in the free space left of centred buttons (after a widgets button if there is one); a left-aligned
    /// taskbar has none.</para>
    /// </summary>
    public static Rectangle? Place(Geometry g, ReadoutPosition position, Size desired)
    {
        if (g.Edge == TaskbarEdge.Left || g.Edge == TaskbarEdge.Right) return null;
        var gap = Scale(8, g.Dpi);
        var min = Scale(MinimumWidth, g.Dpi);
        var clusters = Clusters(g.Buttons.Where(b => b.Right <= g.Notification.Left + 1), g.Dpi);
        Rectangle? union = clusters.Count == 0 ? (Rectangle?)null : Rectangle.FromLTRB(g.Bounds.Left, g.Bounds.Top, clusters.Max(c => c.Right), g.Bounds.Bottom);
        Rectangle? Row(int x, int width) => new Rectangle(x, g.Bounds.Top + (g.Bounds.Height - Math.Min(desired.Height, g.Bounds.Height)) / 2, width, Math.Min(desired.Height, g.Bounds.Height));

        if (position == ReadoutPosition.Center && clusters.Count > 0)
        {
            // Centred in the gap between the right edge of the last button cluster and the notification area; the position next to the
            // notification area (below) when the gap cannot hold the readout.
            var gapLeft = clusters.Max(c => c.Right) + gap;
            var gapRight = (g.Notification.Width > 0 ? g.Notification.Left : g.Bounds.Right) - gap;
            var room = gapRight - gapLeft;
            if (room >= Math.Max(min, desired.Width)) return Row(gapLeft + (room - desired.Width) / 2, desired.Width);
        }
        else if (position == ReadoutPosition.AfterButtons && clusters.Count > 0)
        {
            var x = clusters.Max(c => c.Right) + gap;
            var room = (g.Notification.Width > 0 ? g.Notification.Left : g.Bounds.Right) - gap - x;
            if (room >= min) return Row(x, Math.Min(desired.Width, room));
        }
        else if (position == ReadoutPosition.Left && clusters.Count > 0)
        {
            var main = clusters.OrderByDescending(c => c.Width).First();
            var occupied = clusters.Where(c => c.Right < main.Left).Select(c => c.Right).DefaultIfEmpty(g.Bounds.Left).Max();
            var x = Math.Max(g.Bounds.Left, occupied) + gap;
            var room = main.Left - gap - x;
            if (room >= min) return Row(x, Math.Min(desired.Width, room));
        }
        return PlaceReadout(g.Bounds, g.Notification, union, desired, g.Dpi, g.Edge);
    }

    /// <summary>
    /// Where the panel opens: next to the anchor (an item of the readout or the tray icon), on the side facing the
    /// screen, kept inside the work area of the monitor.
    /// </summary>
    public static Point PlacePanel(Rectangle anchor, Size panel, Rectangle workArea, TaskbarEdge edge, int margin = 8)
    {
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = anchor.Left + anchor.Width / 2 - panel.Width / 2; y = anchor.Bottom + margin; break;
            case TaskbarEdge.Left:
                x = anchor.Right + margin; y = anchor.Top + anchor.Height / 2 - panel.Height / 2; break;
            case TaskbarEdge.Right:
                x = anchor.Left - panel.Width - margin; y = anchor.Top + anchor.Height / 2 - panel.Height / 2; break;
            default:
                x = anchor.Left + anchor.Width / 2 - panel.Width / 2; y = anchor.Top - panel.Height - margin; break;
        }
        x = Math.Max(workArea.Left + margin, Math.Min(x, workArea.Right - panel.Width - margin));
        y = Math.Max(workArea.Top + margin, Math.Min(y, workArea.Bottom - panel.Height - margin));
        return new Point(x, y);
    }
}
