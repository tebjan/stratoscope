using System.Reflection;

namespace Stratoscope.Windows;

internal static class AppInfo
{
    public const string Name = "Stratoscope";
    public const string ReleasesUrl = "https://github.com/yosephyne/stratoscope/releases/latest";

    /// <summary>The version from the assembly (the build sets it from the release tag); "dev" for an unversioned build.</summary>
    public static string Version
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational)) return "dev";
            var plus = informational!.IndexOf('+');
            return plus > 0 ? informational.Substring(0, plus) : informational;
        }
    }

    /// <summary>The short commit the build came from, if the build recorded one.</summary>
    public static string Commit
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational.Substring(plus + 1, Math.Min(7, informational.Length - plus - 1)) : "";
        }
    }

    public static string Label => Name + " " + Version + (Commit.Length > 0 ? " · " + Commit : "");
}
