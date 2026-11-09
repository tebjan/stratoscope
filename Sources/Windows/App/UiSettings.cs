using System.Drawing;
using Stratoscope.Core;
using Windows.UI.ViewManagement;

namespace Stratoscope.Windows;

/// <summary>What Windows currently says about looks and accessibility. Faked in tests.</summary>
internal interface ISystemSettings
{
    /// <summary>The apps use the dark theme.</summary>
    bool AppsDark { get; }
    /// <summary>The taskbar and notification area use the dark theme.</summary>
    bool SystemDark { get; }
    bool HighContrast { get; }
    /// <summary>Client-area animations are on (Settings > Accessibility > Visual effects).</summary>
    bool Animations { get; }
    /// <summary>Settings > Accessibility > Text size, as a factor: 1.0 to 2.25.</summary>
    double TextScale { get; }
    /// <summary>The accent colour (Settings > Personalization > Colors).</summary>
    Color Accent { get; }
}

/// <summary>The real settings: the Windows Runtime <c>UISettings</c> for text size and animations, the registry for the themes.</summary>
internal sealed class WindowsSystemSettings : ISystemSettings
{
    private readonly UISettings ui = new UISettings();

    public Color Accent => SystemColors.Highlight;
    public bool AppsDark => WindowsTheme.AppsDark();
    public bool SystemDark => WindowsTheme.SystemDark();
    public bool HighContrast => System.Windows.Forms.SystemInformation.HighContrast;
    public bool Animations => ui.AnimationsEnabled;
    public double TextScale => Math.Clamp(ui.TextScaleFactor, 1.0, 2.25);
}

/// <summary>Everything a window needs to draw itself at the current display scale and theme.</summary>
internal sealed class UiMetrics
{
    public int Dpi { get; set; } = 96;
    public double TextScale { get; set; } = 1.0;
    public bool AppsDark { get; set; }
    public bool SystemDark { get; set; }
    public bool HighContrast { get; set; }
    public bool Animations { get; set; }
    public Color Accent { get; set; } = Color.FromArgb(0, 120, 212);

    /// <summary>Layout scale: device pixels per epx (effective pixel, 96 DPI = 1).</summary>
    public float Scale => Math.Max(96, Dpi) / 96f;
    /// <summary>Text scale on top of the layout scale (Settings > Accessibility > Text size).</summary>
    public float TextFactor => Scale * (float)TextScale;
    /// <summary>The scale of the panel, whose layout is made of text lines: it grows with the text size too.</summary>
    public float PanelScale => TextFactor;

    public Palette Panel => Palette.For(AppsDark, HighContrast);
    /// <summary>The colour on screen behind the readout, when it has been looked at.</summary>
    public Color? TaskbarSeen { get; set; }

    public Palette Taskbar => Palette.Taskbar(SystemDark, HighContrast, TaskbarSeen);

    /// <summary>Device pixels for a size in epx, on the 1-pixel grid.</summary>
    public int Px(float epx) => (int)Math.Round(epx * Scale);

    public string Signature =>
        string.Join("|", Dpi, TextScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), AppsDark, SystemDark, HighContrast, Animations, Accent.ToArgb(), TaskbarSeen?.ToArgb());

    public static UiMetrics At(int dpi, double textScale = 1.0, bool appsDark = false, bool systemDark = false, bool highContrast = false) =>
        new UiMetrics { Dpi = dpi, TextScale = textScale, AppsDark = appsDark, SystemDark = systemDark, HighContrast = highContrast };
}

/// <summary>
/// Puts the monitors, the Windows settings and the user's override together, and says when something that matters for drawing has changed.
/// Every window asks it for its metrics; every event source (WM_DPICHANGED, WM_DISPLAYCHANGE, WM_SETTINGCHANGE, SystemEvents,
/// TaskbarCreated, a timer) only calls <see cref="Notify"/>.
/// </summary>
internal sealed class UiController
{
    private readonly ISystemSettings settings;
    private readonly IMonitors monitors;
    private readonly Func<int?> scalePercent;
    private readonly Func<Point> probe;
    private readonly Func<ThemeChoice> theme;
    private string last = "";

    public UiController(ISystemSettings settings, IMonitors monitors, Func<int?> uiScalePercent, Func<Point> probePoint, Func<ThemeChoice>? panelTheme = null)
    {
        this.settings = settings; this.monitors = monitors; scalePercent = uiScalePercent; probe = probePoint;
        theme = panelTheme ?? (() => ThemeChoice.System);
    }

    /// <summary>Raised on the thread that calls <see cref="Notify"/>, with the reason, when the metrics are no longer what they were.</summary>
    public event Action<string>? Changed;

    /// <summary>The colour the taskbar really has behind the readout, as the readout host last looked at it (null: not looked at yet).</summary>
    public Color? TaskbarSeen { get; set; }

    public ISystemSettings Settings => settings;
    public IMonitors Monitors => monitors;

    /// <summary>The metrics for things that appear at a screen point (an anchor of the panel, the taskbar, the tray).</summary>
    public UiMetrics At(Point screenPoint) => ForDpi(monitors.DpiAt(screenPoint));

    /// <summary>The metrics for a monitor DPI that is already known (the user's override, if any, wins).</summary>
    public UiMetrics ForDpi(int monitorDpi)
    {
        var percent = scalePercent();
        return new UiMetrics
        {
            Dpi = percent.HasValue ? (int)Math.Round(96 * percent.Value / 100.0) : monitorDpi,
            TextScale = settings.TextScale,
            // The user's choice is for the panel; the readout sits on the taskbar, whose colour is Windows' own.
            AppsDark = theme() switch { ThemeChoice.Light => false, ThemeChoice.Dark => true, _ => settings.AppsDark },
            SystemDark = settings.SystemDark, TaskbarSeen = TaskbarSeen,
            HighContrast = settings.HighContrast, Animations = settings.Animations, Accent = settings.Accent,
        };
    }

    /// <summary>The metrics for the taskbar's monitor.</summary>
    public UiMetrics Current => At(probe());

    /// <summary>Call after anything that could have changed the look. Returns true (and raises <see cref="Changed"/>) when it did, or when forced (monitors or taskbar moved).</summary>
    public bool Notify(string reason, bool force = false)
    {
        var signature = Current.Signature;
        if (signature == last && !force) return false;
        last = signature;
        Changed?.Invoke(reason);
        return true;
    }
}
