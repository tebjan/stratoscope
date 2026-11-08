namespace Stratoscope.Core;

/// <summary>
/// The casing rule for text the user reads: a line starts with a capital letter. Words after a separator inside a line stay as they
/// are, and so do units and the Mac app's short labels ("t/s", "avg"). Every display function ends here, so the rule is in one place.
/// </summary>
public static class Casing
{
    private static readonly string[] Kept = { "avg ", "t/s", "ms ", "kb ", "mib ", "gib " };

    /// <summary>The line with its first letter in capitals, unless it starts with a digit, a symbol or a kept short label.</summary>
    public static string First(string line)
    {
        if (line.Length == 0 || !char.IsLower(line[0])) return line;
        foreach (var kept in Kept)
            if (line.StartsWith(kept, StringComparison.Ordinal) || line.Equals(kept.TrimEnd(), StringComparison.Ordinal)) return line;
        return char.ToUpperInvariant(line[0]) + line.Substring(1);
    }

    /// <summary>Joins the non-empty parts with the middle dot the panel uses, and capitalises the first.</summary>
    public static string Join(params string?[] parts) => First(string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p))));

    /// <summary>Joins lines with line breaks, each capitalised, skipping the empty ones. Null when nothing is left.</summary>
    public static string? Lines(params string?[] lines)
    {
        var kept = lines.Where(l => !string.IsNullOrEmpty(l)).Select(l => First(l!)).ToList();
        return kept.Count == 0 ? null : string.Join("\n", kept);
    }
}
