using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Stratoscope.Core;
using static Stratoscope.Windows.CommandLine;

namespace Stratoscope.Windows;

/// <summary>The development tools: images, measurements and traces that draw offscreen and never show a window.</summary>
internal static class ToolsProgram
{
    private const string HelpText =
        "Stratoscope development tools for Windows.\r\n" +
        "\r\n" +
        "  --snapshot OUT.png [--dark|--light] [--server N] [--config FILE]\r\n" +
        "                                           poll once and draw the panel to a PNG (no window)\r\n" +
        "  --snapshot-readout OUT.png [--scale 1.5] [--dark|--light] [--width-limit PX] [--config FILE]\r\n" +
        "                                           draw the taskbar readout on a mock taskbar\r\n" +
        "  --render-images DIR [--only a,b] [--dpi 96,144]\r\n" +
        "                                           render every image of docs/img offscreen from the demo engine (scripts/windows/screenshots.ps1)\r\n" +
        "  --measure                                pixel measurements and contrast table of the layout\r\n" +
        "  --trace-panel                            replay idle, one, three, three with one finishing, idle through the offscreen panel and print what each update did\r\n" +
        "  --write-icon OUT.ico                     write the application icon\r\n" +
        "  --installer-images DIR                   draw the artwork of the Setup (scripts/windows/installer)\r\n";

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        switch (args.Length > 0 ? args[0] : "--help")
        {
            case "--installer-images":
                InstallerImages.Write(args[1]);
                return 0;
            case "--write-icon":
                IconRenderer.WriteAppIcon(args[1], new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
                return 0;
            case "--render-images":
                return ImageSet.Run(args[1], Option(args, "--only")?.Split(','),
                    Option(args, "--dpi")?.Split(',').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList());
            case "--measure": return Measure();
            case "--trace-panel": return ImageSet.TracePanel(Console.Out);
            case "--snapshot": return Snapshot(args);
            case "--snapshot-readout": return ReadoutSnapshot(args);
            case "--help": Console.Write(HelpText); return 0;
            default:
                Console.Error.Write(HelpText);
                return 2;
        }
    }

    private static bool Dark(string[] args) => Flag(args, "--dark") || (!Flag(args, "--light") && (Environment.GetEnvironmentVariable("STRATOSCOPE_DARK") != null || WindowsTheme.AppsDark()));

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
        Console.WriteLine("Request bars, n = 1 to 8: bar height / gap / stack height in device pixels:");
        foreach (var dpi in new[] { 96, 144, 192 })
            Console.WriteLine("  " + dpi * 100 / 96 + " %: " + string.Join(" | ", Enumerable.Range(1, 8).Select(n =>
            {
                var rows = ReadoutPainter.BarRows(n, UiMetrics.At(dpi));
                return n + ": " + rows.Bar + (rows.Extra > 0 ? "+1 (" + rows.Extra + "x)" : "") + "/" + rows.Gap + "/" + rows.Total;
            })));
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
