using System.IO;

namespace Stratoscope.Windows;

/// <summary>
/// Start with Windows: a shortcut in the user's Startup folder, the way the installer sets it up. No administrator rights, nothing in
/// the registry. The menu switches it on and off.
/// </summary>
internal static class Autostart
{
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Stratoscope.lnk");

    public static bool IsEnabled => File.Exists(ShortcutPath);

    /// <summary>Creates or removes the shortcut. Returns false when Windows would not let it.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (!enabled) { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); return true; }
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                var exe = System.Windows.Forms.Application.ExecutablePath;
                dynamic link = shell.CreateShortcut(ShortcutPath);
                link.TargetPath = exe;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.Description = "Stratoscope, started at logon";
                link.IconLocation = exe + ",0";
                link.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Runtime.InteropServices.COMException || e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }
}
