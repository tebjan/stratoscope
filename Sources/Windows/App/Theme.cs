using System.Drawing;
using Microsoft.Win32;
using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>WCAG 2 contrast maths: relative luminance and the contrast ratio between two opaque colours.</summary>
internal static class Contrast
{
    public const double SmallText = 4.5;     // WCAG AA, normal text
    public const double LargeTextAndGraphics = 3.0;   // WCAG AA, large text and user-interface graphics

    private static double Channel(int v)
    {
        var c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    /// <summary>The colour as it looks over a background (alpha applied).</summary>
    public static Color Over(Color fg, Color bg)
    {
        var a = fg.A / 255.0;
        return Color.FromArgb(255, (int)Math.Round(fg.R * a + bg.R * (1 - a)), (int)Math.Round(fg.G * a + bg.G * (1 - a)), (int)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    public static double Ratio(Color fg, Color bg)
    {
        var l1 = Luminance(Over(fg, bg)); var l2 = Luminance(bg);
        var hi = Math.Max(l1, l2); var lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>The colour itself if it already reaches the ratio on the background; otherwise the nearest darker (on a light
    /// background) or lighter (on a dark one) shade of it that does.</summary>
    public static Color Ensure(Color c, Color bg, double ratio)
    {
        if (Ratio(c, bg) >= ratio) return c;
        var towardsWhite = Luminance(bg) < 0.5;
        var target = towardsWhite ? Color.White : Color.Black;
        for (int step = 1; step <= 50; step++)
        {
            var t = step * 0.02;
            var mixed = Color.FromArgb(c.A, (int)Math.Round(c.R + (target.R - c.R) * t), (int)Math.Round(c.G + (target.G - c.G) * t), (int)Math.Round(c.B + (target.B - c.B) * t));
            if (Ratio(mixed, bg) >= ratio) return mixed;
        }
        return target;
    }
}

/// <summary>
/// The colours. They start from the Mac app's system colours (light and dark variants) and are adjusted, only where they must be,
/// to reach WCAG AA on the background they are drawn on: 3:1 for graphics (dots, rings, bars, glyphs), 4.5:1 for text. In
/// Windows high-contrast mode every colour is a system colour and the states differ by shape and by their words.
/// </summary>
internal sealed class Palette
{
    public bool Dark { get; }
    public bool HighContrast { get; }
    public Color Background { get; }
    public Color Orange { get; }
    public Color Blue { get; }
    public Color Green { get; }
    public Color Yellow { get; }
    public Color Teal { get; }
    public Color Purple { get; }
    public Color Indigo { get; }
    public Color Mint { get; }
    public Color Red { get; }
    public Color Gray { get; }
    /// <summary>The Mac's labelColor: text.</summary>
    public Color Label { get; }
    /// <summary>Secondary text (captions, units): about 70 % of the label colour, still 4.5:1.</summary>
    public Color Dim { get; }
    public Color TertiaryLabel { get; }
    public Color Accent { get; }

    private Palette(bool dark, Color background, bool highContrast)
    {
        Dark = dark; Background = background; HighContrast = highContrast;
        if (highContrast)
        {
            var text = SystemColors.WindowText;
            Label = text; Dim = text; TertiaryLabel = SystemColors.GrayText; Accent = SystemColors.Highlight;
            Orange = Blue = Green = Yellow = Teal = Purple = Indigo = Mint = Red = text;
            Gray = SystemColors.GrayText;
            Background = SystemColors.Window;
            return;
        }
        Color G(Color c) => Contrast.Ensure(c, background, Contrast.LargeTextAndGraphics);
        Orange = G(dark ? Color.FromArgb(255, 159, 10) : Color.FromArgb(255, 149, 0));
        Blue = G(dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 122, 255));
        Green = G(dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89));
        Yellow = G(dark ? Color.FromArgb(255, 214, 10) : Color.FromArgb(255, 204, 0));
        Teal = G(dark ? Color.FromArgb(64, 200, 224) : Color.FromArgb(48, 176, 199));
        Purple = G(dark ? Color.FromArgb(191, 90, 242) : Color.FromArgb(175, 82, 222));
        Indigo = G(dark ? Color.FromArgb(94, 92, 230) : Color.FromArgb(88, 86, 214));
        Mint = G(dark ? Color.FromArgb(99, 230, 226) : Color.FromArgb(0, 199, 190));
        Red = G(dark ? Color.FromArgb(255, 69, 58) : Color.FromArgb(255, 59, 48));
        Gray = G(Color.FromArgb(142, 142, 147));
        Label = dark ? Color.FromArgb(217, 255, 255, 255) : Color.FromArgb(217, 0, 0, 0);
        Dim = Contrast.Ensure(dark ? Color.FromArgb(166, 255, 255, 255) : Color.FromArgb(158, 0, 0, 0), background, Contrast.SmallText);
        TertiaryLabel = Contrast.Ensure(dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(120, 0, 0, 0), background, Contrast.SmallText);
        Accent = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 122, 255);
    }

    private static readonly Dictionary<string, Palette> Cache = new Dictionary<string, Palette>();

    private static Palette Get(bool dark, bool taskbar, bool highContrast, Color? seen = null)
    {
        var key = (dark ? "d" : "l") + (taskbar ? "t" : "p") + (highContrast ? "h" : "n");
        lock (Cache)
        {
            if (highContrast) return new Palette(dark, Color.Black, true);   // system colours can change at any time: never cached
            if (seen.HasValue) return new Palette(dark, seen.Value, false);  // the colour really seen behind the readout: never cached
            if (!Cache.TryGetValue(key, out var p))
            {
                var bg = taskbar ? (dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243)) : (dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(245, 245, 245));
                Cache[key] = p = new Palette(dark, bg, false);
            }
            return p;
        }
    }

    /// <summary>The palette of the panel (and of the Mac look) on its background.</summary>
    public static Palette For(bool dark, bool highContrast = false) => Get(dark, false, highContrast);

    /// <summary>
    /// The palette for things drawn on the taskbar and the notification area. <paramref name="seen"/> is the colour actually on screen behind
    /// the readout (a tinted or translucent taskbar is not the plain colour the theme name suggests); the colours are checked against that.
    /// </summary>
    public static Palette Taskbar(bool dark, bool highContrast = false, Color? seen = null) => Get(dark, true, highContrast, seen);

    // Compatibility with the code that predates the split.
    public static Palette Light => For(false);
    public static Palette DarkPalette => For(true);

    /// <summary>The colour with its opacity set to <paramref name="fraction"/>: the Mac's withAlphaComponent.</summary>
    public static Color Alpha(Color c, double fraction) => Color.FromArgb((int)Math.Round(255 * fraction), c);

    /// <summary>Black on light, white on dark: the Mac's labelColor at a chosen opacity (bar tracks, separators, outlines).</summary>
    /// <summary>The foreground colour's opacity for a hovered and for a pressed element: a step that shows at a glance, not a box.</summary>
    public const double HoverAlpha = 0.11, PressedAlpha = 0.17;

    public Color Faint(double fraction) => Alpha(HighContrast ? SystemColors.WindowText : Dark ? Color.White : Color.Black, fraction);

    /// <summary>A colour used for text: adjusted to 4.5:1 on this palette's background if it is not already.</summary>
    public Color TextOf(Color c) => HighContrast ? Label : (c == Label || c == Dim || c == TertiaryLabel) ? c : Contrast.Ensure(c, Background, Contrast.SmallText);

    public Color HitColor(CacheHit? hit)
    {
        switch (hit)
        {
            case CacheHit.Hit: return Green;
            case CacheHit.Partial: return Yellow;
            case CacheHit.Miss: return Orange;
            default: return Gray;
        }
    }

    public Color StateColor(ServerState state)
    {
        switch (state)
        {
            case ServerState.Loading: return Yellow;
            case ServerState.Ready: return Green;
            case ServerState.Reading: return Orange;
            case ServerState.Thinking:
            case ServerState.Writing: return Blue;
            case ServerState.Error: return Red;
            default: return Gray;      // connecting, offline, waiting for a model
        }
    }
}

/// <summary>What Windows says about light and dark. Both settings are read from the registry on each call (cheap).</summary>
internal static class WindowsTheme
{
    private const string Key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps use the dark theme (Settings, Personalization, Colors, "Choose your default app mode").</summary>
    public static bool AppsDark() => ReadLight("AppsUseLightTheme") == 0;

    /// <summary>True when the taskbar and the notification area use the dark theme ("Choose your default Windows mode").</summary>
    public static bool SystemDark() => ReadLight("SystemUsesLightTheme") == 0;

    private static int ReadLight(string name)
    {
        try { return Registry.GetValue(Key, name, 1) is int v ? v : 1; }
        catch (Exception) { return 1; }
    }
}
