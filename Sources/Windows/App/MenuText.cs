namespace Stratoscope.Windows;

/// <summary>The fixed titles of the right-click menu and the panel's rows. One place, so the casing rule can be checked on all of them.</summary>
internal static class MenuText
{
    public const string ShowStatus = "Show status";
    public const string Settings = "Settings";
    public const string StartWithWindows = "Start with Windows";
    public const string Theme = "Theme";
    public const string ThemeSystem = "System";
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";
    public const string OpenSettingsFile = "Open settings file";
    public const string ReloadSettings = "Reload settings";
    public const string CheckForUpdates = "Check for updates";
    public const string Quit = "Quit Stratoscope";
    public const string SettingsProblem = "Settings: ";

    public static IEnumerable<string> All => new[] { ShowStatus, Settings, StartWithWindows, Theme, ThemeSystem, ThemeLight, ThemeDark, OpenSettingsFile, ReloadSettings, CheckForUpdates, Quit, SettingsProblem };
}
