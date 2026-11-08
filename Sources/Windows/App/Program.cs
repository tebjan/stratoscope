using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Stratoscope.Core;

namespace Stratoscope.Windows;

internal static class Program
{
    private const string HelpText =
        "Stratoscope for Windows. Without arguments it runs in the notification area and the taskbar.\r\n" +
        "\r\n" +
        "  --dump [--config FILE]                   poll once and print the title and a few fields\r\n" +
        "  --snapshot OUT.png [--dark|--light] [--server N] [--config FILE]\r\n" +
        "                                           poll once and draw the panel to a PNG (no window)\r\n" +
        "  --snapshot-readout OUT.png [--scale 1.5] [--dark|--light] [--width-limit PX] [--config FILE]\r\n" +
        "                                           draw the taskbar readout on a mock taskbar\r\n" +
        "  --render-images DIR [--only a,b] [--dpi 96,144]\r\n" +
        "                                           render every image of docs/img offscreen from the demo engine (scripts/windows/screenshots.ps1)\r\n" +
        "  --dpi-report                             DPI of every monitor and the sizes the app would use (read-only)\r\n" +
        "  --probe-taskbar                          what the app sees of the taskbar and where the readout would go (read-only)\r\n" +
        "  --measure                                pixel measurements and contrast table of the layout\r\n" +
        "  --trace-panel                            replay idle, one, three, three with one finishing, idle through the offscreen panel and print what each update did\r\n" +
        "  --write-icon OUT.ico                     write the application icon\r\n" +
        "  --quit                                   ask the running instance to exit (exit code 1 if none runs)\r\n" +
        "  --version\r\n";

    [STAThread]
    private static int Main(string[] args)
    {
        // Generated from the csproj (ApplicationHighDpiMode = PerMonitorV2): the process asks Windows for per-monitor DPI before it creates any window.
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal)) return Tool(args);
            return RunTray();
        }
        catch (Exception e)
        {
            Log("fatal", e);
            return 1;
        }
    }

    // MARK: The app

    private static int RunTray()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "Local\\Stratoscope.Windows.Tray", out created))
        {
            if (!created) return 0;   // one instance per user session
            Application.ThreadException += (s, e) => Log("ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("unhandled", e.ExceptionObject as Exception);
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Run(new TrayApp(null));
            mutex.ReleaseMutex();
        }
        return 0;
    }

    /// <summary>A short error log next to the settings, because there is no console and no window to show a failure in.</summary>
    private static void Log(string kind, Exception? error)
    {
        try
        {
            var dir = Path.GetDirectoryName(Config.DefaultPath(null))!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + kind + ": " + error + "\r\n", Encoding.UTF8);
        }
        catch (Exception) { }
    }

    // MARK: Command-line tools

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static int Tool(string[] args)
    {
        Native.AttachConsole(-1);   // print into the console that started us, if any
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        var cmd = args[0];
        switch (cmd)
        {
            case "--version": Console.WriteLine(AppInfo.Label); return 0;
            case "--help": Console.Write(HelpText); return 0;
            case "--write-icon":
                IconRenderer.WriteAppIcon(args[1], new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
                return 0;
            case "--render-images":
                return ImageSet.Run(args[1], Option(args, "--only")?.Split(','),
                    Option(args, "--dpi")?.Split(',').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList());
            case "--dpi-report": return DpiReport(args);
            case "--measure": return Measure();
            case "--trace-panel": return ImageSet.TracePanel(Console.Out);
            case "--probe-taskbar": return ProbeTaskbar(args);
            case "--quit":
                // Asks the running instance of this user to exit. Exit code 1 when none is running.
                return EventWaitHandle.TryOpenExisting(TrayApp.QuitEventName, out var quit) && quit.Set() ? 0 : 1;
            case "--dump": return Dump(args);
            case "--snapshot": return Snapshot(args);
            case "--snapshot-readout": return ReadoutSnapshot(args);
            default:
                Console.Error.Write(HelpText);
                return 2;
        }
    }

    private static List<ServerMonitor> PollOnce(Config config, int rounds)
    {
        var monitors = new List<ServerMonitor>();
        using (var source = new HttpJsonSource())
        {
            foreach (var s in config.Servers) monitors.Add(new ServerMonitor(s.Name, EngineRegistry.Make(s, source)));
            for (int i = 0; i < rounds; i++)
            {
                System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.WhenAll(monitors.Select(m => m.PollAsync(CancellationToken.None)))).GetAwaiter().GetResult();
                if (i + 1 < rounds) Thread.Sleep(700);
            }
        }
        return monitors;
    }

    private static bool Dark(string[] args) => Flag(args, "--dark") || (!Flag(args, "--light") && (Environment.GetEnvironmentVariable("STRATOSCOPE_DARK") != null || WindowsTheme.AppsDark()));

    private static int Dump(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var monitors = PollOnce(config, 1);
        Console.WriteLine("config: " + config.Path + (File.Exists(config.Path ?? "") ? "" : " (not found, defaults used)"));
        foreach (var p in config.Problems) Console.WriteLine("config problem: " + p);
        foreach (var m in monitors)
        {
            var s = m.Snapshot;
            if (s == null) { Console.WriteLine("[" + Display.Word(m.State) + "] " + m.Name + ": " + m.Error); continue; }
            Console.WriteLine("[" + Display.Title(s) + "] " + s.Model);
            Console.WriteLine("  server: " + m.Name + " | status: " + Display.Word(m.State) + " | queued: " + s.Queued + (s.RouterLimited ? " | router limit reached" : ""));
            Console.WriteLine("  context: " + Fmt.Number(s.Context.Used) + " of " + Fmt.Number(s.Context.Max) + " | recent requests: " + s.Recent.Count);
            if (s.System != null) Console.WriteLine("  system: RAM " + Fmt.Gib(s.System.RamUsedBytes) + " of " + Fmt.Gib(s.System.RamTotalBytes) + " GB | GPU " + Fmt.Number(s.System.GpuPercent) + " % | CPU " + Fmt.Number(s.System.CpuPercent) + " %");
            var readout = Display.Readout(s);
            Console.WriteLine("  readout: " + (readout.Value.Length > 0 ? readout.Value + " " + readout.Unit : "(glyph only)"));
        }
        Console.WriteLine("servers: " + monitors.Count + " | taskbar found: " + (TaskbarProbe.FindTaskbar() != IntPtr.Zero));
        return 0;
    }

    private static int Snapshot(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var dark = Dark(args);
        var monitors = PollOnce(config, 2);
        var index = Servers.Clamp(int.TryParse(Option(args, "--server"), out var n) ? n : Servers.Primary(monitors), monitors.Count);
        var model = PanelModelBuilder.Panel(monitors, index, DateTime.UtcNow);
        model.VersionLabel = "Stratoscope " + AppInfo.Version;
        var items = PanelModelBuilder.Readout(monitors);
        SnapshotRenderer.Panel(args[1], model, items, dark);
        return 0;
    }

    private static int ReadoutSnapshot(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var monitors = PollOnce(config, 2);
        var items = PanelModelBuilder.Readout(monitors);
        var scale = float.TryParse(Option(args, "--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1f;
        int? limit = int.TryParse(Option(args, "--width-limit"), out var w) ? w : (int?)null;
        var options = new SnapshotRenderer.StripOptions
        {
            Centred = Option(args, "--alignment") != "left",
            Widgets = Flag(args, "--widgets"),
            Position = config.ReadoutPosition,
        };
        var where = Option(args, "--position");
        if (where == "after-buttons") options.Position = ReadoutPosition.AfterButtons;
        else if (where == "left") options.Position = ReadoutPosition.Left;
        else if (where == "tray") options.Position = ReadoutPosition.Tray;
        SnapshotRenderer.Readout(args[1], items, scale, Dark(args), limit, options);
        return 0;
    }

    /// <summary>Read-only: every monitor with its DPI, and the size the panel would get on it. Nothing is shown on screen.</summary>
    private static int DpiReport(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var settings = new RegistrySystemSettings();
        var ui = new UiController(settings, SystemMonitors.Instance, () => config.UiScalePercent, () => new Point(0, 0));
        Console.WriteLine("Windows text size: " + (int)(settings.TextScale * 100) + " % | high contrast: " + settings.HighContrast + " | animations: " + settings.Animations
            + " | apps theme: " + (settings.AppsDark ? "dark" : "light") + " | taskbar theme: " + (settings.SystemDark ? "dark" : "light"));
        Console.WriteLine("ui_scale: " + (config.UiScalePercent.HasValue ? config.UiScalePercent + " %" : "auto"));
        int oldDpi;
        using (var probe = new System.Windows.Forms.Form()) { var _ = probe.Handle; oldDpi = probe.DeviceDpi; }
        Console.WriteLine("Control.DeviceDpi of a new form: " + oldDpi + " (what the first version of the panel used for its size, on every monitor)");

        // The height of the panel's content, from one read-only poll of the configured servers.
        var monitors = PollOnce(config, 1);
        var model = PanelModelBuilder.Panel(monitors, Servers.Primary(monitors), DateTime.UtcNow);
        float content;
        using (var scratch = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(scratch))
            content = new PanelPainter(g, Palette.For(false), model).Paint();
        Console.WriteLine("panel content: " + PanelPainter.Width + " x " + content + " logical px (" + (model.Snapshot != null ? model.Snapshot.Model : "no answer") + ")");

        foreach (var mon in SystemMonitors.All())
        {
            var scale = PanelSizing.Scale(mon.Dpi);
            var anchor = new Rectangle(mon.Bounds.Right - (int)(300 * scale), mon.Bounds.Bottom - (int)(44 * scale), (int)(120 * scale), (int)(38 * scale));
            var layout = PanelLayout.For(anchor, content, TaskbarEdge.Bottom, ui);
            var logicalW = mon.Bounds.Width / scale; var logicalH = mon.Bounds.Height / scale;
            Console.WriteLine("monitor " + (mon.Primary ? "(primary) " : "") + mon.Bounds.Width + "x" + mon.Bounds.Height + " at " + mon.Bounds.X + "," + mon.Bounds.Y
                + " | DPI " + mon.Dpi + " = " + (int)Math.Round(scale * 100) + " % | logical " + Math.Round(logicalW) + "x" + Math.Round(logicalH)
                + " | work area " + mon.WorkArea.Width + "x" + mon.WorkArea.Height);
            Console.WriteLine("    panel before: " + (int)Math.Ceiling(PanelPainter.Width * (oldDpi / 96f)) + " x " + (int)Math.Ceiling(content * (oldDpi / 96f))
                + " px | panel now: " + layout.Size.Width + " x " + layout.Size.Height + " px at " + layout.Location.X + "," + layout.Location.Y
                + (layout.Scrolls ? " (scrolls: content is " + (int)Math.Ceiling(content * layout.Scale) + " px tall)" : "")
                + " | readout height " + (int)Math.Round(48 * scale) + " px");
        }
        return 0;
    }

    /// <summary>Read-only: what the app sees of the taskbar and where it would put the readout. Nothing is drawn or changed.</summary>
    private static int ProbeTaskbar(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var info = TaskbarProbe.Read(ReadoutHost.ReadoutClass);
        if (info == null) { Console.WriteLine("no taskbar"); return 1; }
        info.Buttons = info.Buttons.Concat(TaskbarProbe.ButtonRectangles(info.Window, info.Notification)).ToList();
        Console.WriteLine("taskbar " + info.Bounds + " dpi " + info.Dpi + " edge " + info.Edge + " hidden " + info.Hidden + " auto-hide " + info.AutoHide + " centred " + info.Centred + " dark " + info.SystemDark);
        Console.WriteLine("notification area " + info.Notification);
        foreach (var b in info.Buttons) Console.WriteLine("  button " + b);
        var items = PanelModelBuilder.Readout(PollOnce(config, 1));
        var desired = ReadoutPainter.Measure(items, UiMetrics.At(info.Dpi), info.Bounds.Height);
        foreach (var item in items) Console.WriteLine("  item " + item.State + " " + item.Text.Value + " " + item.Text.Unit);
        var place = TaskbarLayout.Place(info.ToGeometry(), config.ReadoutPosition, desired);
        Console.WriteLine("readout " + desired + " at " + config.ReadoutPosition + " goes to " + (place.HasValue ? place.Value.ToString() : "nowhere (no room)"));
        return 0;
    }

    /// <summary>The numbers behind the "Design rules" table in docs/WINDOWS.md: where the glyph and the text block sit, and the contrast of every colour.</summary>
    private static int Measure()
    {
        Console.WriteLine("Anchor grid of the readout item (device pixels from its left edge; g is the gap either side of the anchor):");
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            var mm = UiMetrics.At(dpi);
            var g = SnapshotRenderer.StateRows()[1].Items[0];
            var geo = ReadoutPainter.GeometryOf(g, mm);
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "  {0,3} %: width {1}, left margin {2}, right margin {3}, g {4}, graphics end {5}, anchor {6}, text and number slot start {7}, unit x {8}, text slot end {9}",
                dpi * 100 / 96, geo.Width, geo.GlyphX, geo.Width - geo.TextRight, mm.Px(ReadoutPainter.AnchorGap), geo.GlyphRight, geo.Anchor, geo.TextX, geo.UnitX, geo.TextRight));
        }
        Console.WriteLine("Last pixel column of the glyph from the item's left edge, and the end of the glyph slot (anchor minus gap):");
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            Console.WriteLine("  " + dpi * 100 / 96 + " %: " + string.Join(", ", new[] { ServerState.Reading, ServerState.Writing, ServerState.Offline, ServerState.Loading, ServerState.Error }.Select(s => s + " " + Measurements.GlyphRightEdge(dpi, s).LastColumn)) + " | slot end " + Measurements.GlyphRightEdge(dpi, ServerState.Ready).SlotEnd);
        Console.WriteLine("Clear space between the glyph and the first pixel of the number (device pixels):");
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            Console.WriteLine("  " + dpi * 100 / 96 + " %: " + string.Join(", ", new[] { "9", "99", "999", "3345", "9999", "10k" }.Select(v => v + " = " + Measurements.NumberClearance(dpi, v))));
        Console.WriteLine("Centring (device pixels; tolerance 0.5):");
        foreach (var dpi in new[] { 96, 120, 144, 168, 192, 240 })
            foreach (var dark in new[] { true, false })
                foreach (var number in new[] { true, false })
                {
                    var c = Measurements.MeasureCentring(dpi, dark, number);
                    Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "  {0,3} % {1,-5} {2,-12} height {3,3}: value {4:0.00} to {5:0.00}, offset from the centre line {6:+0.00;-0.00;0.00}; glyph {7:0.00} to {8:0.00}, offset {9:+0.00;-0.00;0.00}",
                        dpi * 100 / 96, dark ? "dark" : "light", c.Case, c.Height, c.ValueTop, c.ValueBottom, c.ValueOffset, c.GlyphTop, c.GlyphBottom, c.GlyphOffset));
                }
        Console.WriteLine("Contrast (WCAG 2 ratio, required):");
        foreach (var r in Measurements.ContrastTable())
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  {0,-14} {1,-34} {2,5:0.00}:1 (>= {3:0.0}) {4}", r.Surface, r.Name, r.Ratio, r.Required, r.Passes ? "ok" : "FAIL"));
        Console.WriteLine("Fonts: text " + Typography.TextFamily + ", number " + Typography.StrongFamily);
        Console.WriteLine("Text sharpness (share of text pixels at 90 % opacity or more among all text pixels; higher is crisper):");
        var kept = ReadoutPainter.TextHint;
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            foreach (var hint in new[] { System.Drawing.Text.TextRenderingHint.AntiAlias, System.Drawing.Text.TextRenderingHint.AntiAliasGridFit, System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit })
            {
                ReadoutPainter.TextHint = hint;
                var items = SnapshotRenderer.StateRows()[1].Items;
                var mm = UiMetrics.At(dpi, 1.0, true, true);
                using (var bmp = ReadoutPainter.Render(items, mm, ReadoutPainter.Measure(items, mm)))
                    Console.WriteLine("  " + dpi * 100 / 96 + " % " + hint + ": " + Measurements.Sharpness(bmp).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
        ReadoutPainter.TextHint = kept;
        var keptContrast = ReadoutPainter.TextContrast;
        Console.WriteLine("Alpha curve on the rendered text (grid-fitted grey-scale), power 1 = none:");
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            foreach (var power in new[] { 0.0, 0.85, 0.7, 0.55 })
            {
                ReadoutPainter.TextContrast = power;
                var items = SnapshotRenderer.StateRows()[1].Items;
                var mm = UiMetrics.At(dpi, 1.0, true, true);
                using (var bmp = ReadoutPainter.Render(items, mm, ReadoutPainter.Measure(items, mm)))
                    Console.WriteLine("  " + dpi * 100 / 96 + " % power " + (power == 0 ? "off" : power.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)) + ": " + Measurements.Sharpness(bmp).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
        ReadoutPainter.TextContrast = keptContrast;
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var rh = ReadoutPainter.RhythmFor(UiMetrics.At(dpi), (int)Math.Round(48 * dpi / 96.0));
            Console.WriteLine("Rhythm at " + dpi + " DPI: block " + rh.BlockTop + " to " + rh.BlockBottom + ", big baseline " + rh.BigBaseline + ", caption baseline " + rh.CaptionBaseline + ", optical centre " + rh.OpticalCentre);
        }
        return 0;
    }
}
