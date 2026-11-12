using System.Globalization;

namespace Stratoscope.Core;

/// <summary>
/// The Prometheus text format (<c>name value</c> or <c>name{label="x"} value</c> per line, <c>#</c> comments), read into a table. One reader
/// for every engine whose metrics are Prometheus text (llama.cpp, the custom 27B server); it was lifted out of the llama.cpp adapter.
/// </summary>
public static class PrometheusText
{
    /// <summary>
    /// The finite values of every series whose name starts with <paramref name="prefix"/>, keyed by the series text without the prefix and
    /// with its labels as sent (<c>admission_blocked{reason="kv"}</c>). A value that is not a finite number is left out (unknown).
    /// </summary>
    public static Dictionary<string, double> Parse(string? text, string prefix)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return values;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            // The series ends at the closing brace of its labels, else at the first space; the value follows (then an optional timestamp).
            var close = line.IndexOf('}');
            var split = close >= 0 ? close + 1 : line.IndexOf(' ');
            if (split <= prefix.Length) continue;
            var rest = line.Substring(split).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length == 0) continue;
            if (double.TryParse(rest[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                values[line.Substring(prefix.Length, split - prefix.Length)] = value;
        }
        return values;
    }
}
