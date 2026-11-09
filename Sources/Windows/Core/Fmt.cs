using System.Globalization;

namespace Stratoscope.Core;

/// <summary>Number formatting. English, independent of the Windows display language. Mirrors Format.swift.</summary>
public static class Fmt
{
    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The marker for a value the engine did not report.</summary>
    public const string Missing = "–";   // en dash

    /// <summary>Grouped number with fixed decimals; halves round to even like the Mac app. Null or non-finite gives the dash.</summary>
    public static string Number(double? v, int digits = 0)
    {
        if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return Missing;
        var rounded = Math.Round(v.Value, digits, MidpointRounding.ToEven);
        return rounded.ToString("N" + digits, Locale);
    }

    /// <summary>
    /// Speed for the tray title: at least three digits wide (figure spaces) and "0 t/s" instead of a dash,
    /// so the title does not jump when the speed goes from 0 to 222.
    /// </summary>
    public static string Speed(double? v)
    {
        var n = Number(Math.Max(0, v ?? 0));
        return new string(' ', Math.Max(0, 3 - n.Length)) + n + " t/s";
    }

    public static string Gib(double? bytes) => bytes is null ? Missing : Number(bytes.Value / 1_073_741_824.0, 1);

    public static string Duration(double seconds)
    {
        var t = Math.Max(0, (int)seconds);
        int h = t / 3600, m = t % 3600 / 60, s = t % 60;
        if (h > 0) return h + " h " + m + " min";
        return m > 0 ? m + " min " + s + " s" : s + " s";
    }
}
