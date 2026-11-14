namespace Stratoscope.Windows;

/// <summary>The fixed titles of the panel's rows. One place, so the casing rule can be checked on all of them.</summary>
internal static class MenuText
{
    public const string Settings = "Settings";
    public const string StartWithWindows = "Start with Windows";
    public const string Theme = "Theme";
    public const string ThemeSystem = "System";
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";
    public const string HistorySmoothing = "History smoothing";
    public const string SmoothingOff = "Off";
    public const string SmoothingNormal = "Normal";
    public const string SmoothingHigh = "High";
    public const string SmoothingHighest = "Highest";
    public const string OpenSettingsFile = "Open settings file";
    public const string ReloadSettings = "Reload settings";
    public const string CheckForUpdates = "Check for updates";
    public const string Quit = "Quit Stratoscope";

    public static IEnumerable<string> All => new[] { Settings, StartWithWindows, Theme, ThemeSystem, ThemeLight, ThemeDark, HistorySmoothing, SmoothingOff, SmoothingNormal, SmoothingHigh, SmoothingHighest, OpenSettingsFile, ReloadSettings, CheckForUpdates, Quit };
}
