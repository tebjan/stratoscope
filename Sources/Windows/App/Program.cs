using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Stratoscope.Core;
using static Stratoscope.Windows.CommandLine;
using static Windows.Win32.PInvoke;

namespace Stratoscope.Windows;

internal static class Program
{
    private const string HelpText =
        "Stratoscope for Windows. Without arguments it shows its readout in the taskbar.\r\n" +
        "\r\n" +
        "  --dump [--config FILE]                   poll once and print the title and a few fields\r\n" +
        "  --detect                                 find the servers of this computer and print them as the settings entries (read-only)\r\n" +
        "  --dpi-report                             DPI of every monitor and the sizes the app would use (read-only)\r\n" +
        "  --probe-taskbar                          what the app sees of the taskbar and where the readout would go (read-only)\r\n" +
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
            LogToFile();
            return Run();
        }
        catch (Exception e)
        {
            Log("fatal", e);
            return 1;
        }
    }

    // MARK: The app

    private static int Run()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "Local\\Stratoscope.Windows", out created))
        {
            if (!created) return 0;   // one instance per user session
            Application.ThreadException += (s, e) => Log("ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("unhandled", e.ExceptionObject as Exception);
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Run(new ReadoutApp(null));
            mutex.ReleaseMutex();
        }
        return 0;
    }

    /// <summary>The app has no console and no window to show a failure in: what goes to <see cref="Trace"/> lands in error.log next to the settings.</summary>
    private static void LogToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(Config.DefaultPath(null))!;
            Directory.CreateDirectory(dir);
            Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(dir, "error.log")) { TraceOutputOptions = TraceOptions.DateTime });
            Trace.AutoFlush = true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    private static void Log(string kind, Exception? error) => Trace.TraceError("{0}: {1}", kind, error);

    // MARK: Command-line tools

    private static int Tool(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);   // print into the console that started us, if any
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        var cmd = args[0];
        switch (cmd)
        {
            case "--version": Console.WriteLine(AppInfo.Label); return 0;
            case "--help": Console.Write(HelpText); return 0;
            case "--dpi-report": return DpiReport(args);
            case "--probe-taskbar": return ProbeTaskbar(args);
            case "--quit":
                // Asks the running instance of this user to exit. Exit code 1 when none is running.
                return EventWaitHandle.TryOpenExisting(ReadoutApp.QuitEventName, out var quit) && quit.Set() ? 0 : 1;
            case "--dump": return Dump(args);
            case "--detect": return Detect();
            default:
                Console.Error.Write(HelpText);
                return 2;
        }
    }

    /// <summary>What the app finds by itself when nobody said where the server is, as the entries of the settings file. Exit code 1 when nothing is found.</summary>
    private static int Detect()
    {
        using var probe = new HttpJsonSource(TimeSpan.FromSeconds(2));
        var clients = new ClientConfigReader(configHome: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        var found = ServerDetection.FindAsync(probe, clients, CancellationToken.None).GetAwaiter().GetResult();
        var servers = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in found)
        {
            var entry = new System.Text.Json.Nodes.JsonObject();
            if (s.Router != null) entry["router"] = s.Router;
            if (s.Url != null) entry["url"] = s.Url;
            servers.Add(entry);
        }
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["servers"] = servers }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return found.Count > 0 ? 0 : 1;
    }

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
            if (s.System is { } sys) Console.WriteLine("  gpu: " + Fmt.Number(sys.GpuTemperatureCelsius) + " C | " + Fmt.Number(sys.GpuPowerWatts) + " of " + Fmt.Number(sys.GpuPowerLimitWatts) + " W | VRAM " + Fmt.Gib(sys.GpuMemoryUsedBytes) + " of " + Fmt.Gib(sys.GpuMemoryTotalBytes) + " GB");
            if (s.Live is { } live)
                Console.WriteLine("  live: read " + Fmt.Number(live.PromptRead) + " of " + Fmt.Number(live.PromptTotal) + " (" + (live.Percent.HasValue ? live.Percent + " %" : Fmt.Missing) + ") | prefill " + Fmt.Number(live.PrefillTokensPerSecond)
                    + " t/s | decode " + Fmt.Number(live.DecodeTokensPerSecond) + " t/s | generated " + Fmt.Number(live.Generated) + " of " + Fmt.Number(live.MaxTokens) + " | reused " + Fmt.Number(live.ReusedEstimate)
                    + " | cache " + (Display.Progress(s)?.Hit?.Label() ?? Fmt.Missing));
            Console.WriteLine("  finished: " + string.Join(", ", s.Recent.Take(5).Select(r => Fmt.Number(r.PromptTokens) + "/" + Fmt.Number(r.ReusedTokens) + " " + (r.Hit?.Label() ?? Fmt.Missing))));
            if (s.Info is { } info)
                Console.WriteLine("  engine: " + s.Engine + " " + (info.Version ?? Fmt.Missing) + " | KV " + (info.KvFormat ?? Fmt.Missing) + " | draft " + (info.DraftWindow ?? Fmt.Missing) + " | expert cache " + Fmt.Number(info.ExpertCacheMib) + " MiB | expert hit " + Fmt.Number(info.ExpertHitRate * 100) + " %");
            if (s.Cache is { } cache) Console.WriteLine("  conversation cache: budget " + Fmt.Gib(cache.BudgetBytes) + " GB | slots " + Fmt.Number(cache.Slots));
            var readout = Display.Readout(s);
            Console.WriteLine("  readout: " + (readout.Value.Length > 0 ? readout.Value + " " + readout.Unit : "(glyph only)"));
        }
        Console.WriteLine("servers: " + monitors.Count + " | taskbar found: " + !TaskbarProbe.FindTaskbar().IsNull);
        return 0;
    }
    /// <summary>Read-only: every monitor with its DPI, and the size the panel would get on it. Nothing is shown on screen.</summary>
    private static int DpiReport(string[] args)
    {
        var config = Config.Load(null, Option(args, "--config"));
        var settings = new WindowsSystemSettings();
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
}
