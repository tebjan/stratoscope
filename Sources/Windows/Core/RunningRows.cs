namespace Stratoscope.Core;

/// <summary>How a row of one running request is drawn in the panel.</summary>
public enum RunningRowStyle
{
    /// <summary>State word and speed, a bar, and its numbers under the bar.</summary>
    Full,
    /// <summary>State word with its count and the speed on one line, a thin bar under it. No caption line.</summary>
    Compact,
    /// <summary>The bar alone (many requests); the numbers are in the tooltip of the row.</summary>
    BarOnly,
}

/// <param name="Pitch">The distance from the top of one row to the top of the next, in epx.</param>
/// <param name="Bar">The height of the bar, in epx.</param>
public readonly record struct RunningRowLayout(double Pitch, RunningRowStyle Style, double Bar);

/// <summary>
/// The per-request rows of the panel live block. The rows share an area of fixed height, so the panel never changes size with the number
/// of requests (the same idea as the taskbar readout, <c>RequestBars</c>: more requests, smaller and tighter rows). Because the height is
/// fixed the relation is linear: the pitch is the area divided by the count, at most <see cref="MaxPitch"/> (one request is not taller than
/// a row ever was), and the style falls back to a thinner row where text no longer fits. Mirrored in <c>RunningRows.swift</c>.
/// </summary>
public static class RunningRows
{
    /// <summary>At most this many rows, as the readout bars (<see cref="RequestBars.MaxBars"/>).</summary>
    public const int MaxRows = RequestBars.MaxBars;
    /// <summary>The height of the rows area, in epx (a multiple of 4).</summary>
    public const double AreaHeight = 96;
    /// <summary>The tallest a row gets: the height of a row with its caption and the gap below it.</summary>
    public const double MaxPitch = 40;
    /// <summary>The smallest pitch of a row with a caption (word, bar, caption).</summary>
    public const double FullPitch = 36;
    /// <summary>The smallest pitch of a row with text (word and bar): 12 epx text and a 4 epx bar.</summary>
    public const double CompactPitch = 24;

    public static RunningRowLayout For(int count)
    {
        var n = Math.Clamp(count, 1, MaxRows);
        var pitch = Math.Min(MaxPitch, AreaHeight / n);
        if (pitch >= FullPitch) return new RunningRowLayout(pitch, RunningRowStyle.Full, 6);
        if (pitch >= CompactPitch) return new RunningRowLayout(pitch, RunningRowStyle.Compact, 4);
        var gap = Math.Max(2, Math.Round(pitch / 4));
        return new RunningRowLayout(pitch, RunningRowStyle.BarOnly, Math.Min(8, pitch - gap));
    }

    /// <summary>
    /// The token limit the running requests share, shown once instead of on every row: set when every generating request has a limit and they
    /// are all the same. Null when limits differ or one is unknown (then each row keeps its own).
    /// </summary>
    public static double? SharedLimit(IEnumerable<BatchSlot> running)
    {
        double? limit = null;
        foreach (var slot in running.Where(s => s.IsDecoding))
        {
            if (!(slot.MaxTokens > 0)) return null;
            if (limit.HasValue && limit != slot.MaxTokens) return null;
            limit = slot.MaxTokens;
        }
        return limit;
    }
}
