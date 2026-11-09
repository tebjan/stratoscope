using System.Drawing;

namespace Stratoscope.Windows;

internal enum PlaceKind { None, Show, Move, Hide }

internal readonly record struct PlaceAction(PlaceKind Kind, Rectangle Rect);

/// <summary>
/// Decides when the taskbar readout moves. The target rectangle comes from the taskbar geometry alone (never from what the readout shows),
/// in the client coordinates of the taskbar window, so a taskbar that slides in or out takes the readout with it without a move.
/// A new target is acted on only if it differs from the current rectangle by at least <see cref="MinimumMove"/> device pixels and has
/// stayed there for <see cref="Settle"/>: a tray icon that comes and goes within a second, a clock that changes its width, or a button
/// that is added never makes the readout jump around; one change gives one move. Pure and clocked from outside, so a taskbar simulator can
/// replay sequences of changes in a unit test.
/// </summary>
internal sealed class ReadoutPlacer
{
    public const int MinimumMove = 4;
    /// <summary>A new target must hold this long before the readout moves. The Windows privacy indicator (microphone or location in use) pushes the notification area 48 px to the left for 4 to 25 s at a time; that is not a change of the taskbar.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    /// <summary>No place for the readout (and no order to hide it) for this long before it is hidden.</summary>
    public static readonly TimeSpan HideSettle = TimeSpan.FromSeconds(1);

    private Rectangle? current;
    private Rectangle? pending;
    private DateTime pendingSince;
    private DateTime? hidePendingSince;

    /// <summary>Where the readout is (client coordinates of the taskbar), or null while it is hidden.</summary>
    public Rectangle? Current => current;
    public int Shows { get; private set; }
    public int Moves { get; private set; }
    public int Hides { get; private set; }
    /// <summary>Changes of the target that went away again before the settle time (a flap of the taskbar): not followed.</summary>
    public int Absorbed { get; private set; }

    private static int Distance(Rectangle a, Rectangle b) =>
        Math.Max(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)), Math.Max(Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height)));

    /// <summary>
    /// One look at the geometry. <paramref name="target"/> is null when the readout has no place (taskbar hidden or too narrow).
    /// <paramref name="immediate"/> skips the settling: a change of DPI or text size, a new window.
    /// </summary>
    public PlaceAction Step(DateTime now, Rectangle? target, bool immediate = false)
    {
        if (target is null)
        {
            pending = null;
            if (current is null) { hidePendingSince = null; return new PlaceAction(PlaceKind.None, Rectangle.Empty); }
            // No place for it right now. Unless told to hide at once (the taskbar is hidden, the readout is switched off), it keeps its last
            // good place for the settling time first: a glance at the taskbar in the middle of a change must not make it blink.
            if (!immediate)
            {
                hidePendingSince ??= now;
                if (now - hidePendingSince.Value < HideSettle) return new PlaceAction(PlaceKind.None, Rectangle.Empty);
            }
            hidePendingSince = null;
            current = null; Hides++;
            return new PlaceAction(PlaceKind.Hide, Rectangle.Empty);
        }
        hidePendingSince = null;
        var t = target.Value;
        if (current is null)
        {
            current = t; pending = null; Shows++;
            return new PlaceAction(PlaceKind.Show, t);
        }
        if (immediate && current.Value != t)
        {
            current = t; pending = null; Moves++;
            return new PlaceAction(PlaceKind.Move, t);
        }
        if (Distance(current.Value, t) < MinimumMove) { if (pending != null) Absorbed++; pending = null; return new PlaceAction(PlaceKind.None, Rectangle.Empty); }
        if (pending is null || Distance(pending.Value, t) >= MinimumMove) { pending = t; pendingSince = now; return new PlaceAction(PlaceKind.None, Rectangle.Empty); }
        if (now - pendingSince < Settle) return new PlaceAction(PlaceKind.None, Rectangle.Empty);
        current = t; pending = null; Moves++;
        return new PlaceAction(PlaceKind.Move, t);
    }
}
