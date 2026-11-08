using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Stratoscope.Core;
using Stratoscope.Core.Engines.Demo;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Windows;

/// <summary>
/// Every image of the Windows docs, rendered offscreen from the demo engine with neutral server names and a fixed clock, so a second run
/// gives the same pixels. No engine, no network, no window. <c>scripts/windows/screenshots.ps1</c> runs it.
/// </summary>
internal static class ImageSet
{
    /// <summary>The "now" of every image: ages and "since" lines do not depend on the day the images are made.</summary>
    private static readonly DateTime Reference = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private const string Server = "Workstation";

    public static readonly string[] Names =
    {
        "panel-dark", "panel-light", "panel-decode-dark", "panel-decode-light", "panel-parallel-dark", "panel-parallel-light", "panel-parallel3-dark", "panel-parallel3-light", "hover-states", "chart-slots", "readout-zoom", "readout-slots", "readout-numbers", "readout-glyph-sizes", "panel-hover-dark", "panel-hover-light", "panel-tooltips", "panel-offline", "panel-no-model",
        "readout-states", "readout-scales", "readout-layouts",
    };

    private static EngineSnapshot Demo(string scenario) => new DemoProvider(scenario, () => Reference).Snapshot();

    /// <summary>
    /// One engine with four batch slots as requests come and go: idle, one running, three running, two running (one of the three finished),
    /// idle again. The panel changes only its live block through them (<see cref="TracePanel"/> and the layout test).
    /// </summary>
    internal static List<(string Label, EngineSnapshot Snapshot)> Sequence()
    {
        BatchSlot Idle(int i) => new BatchSlot { Index = i, State = "idle", HeldTokens = 41_430 };
        BatchSlot Decoding(int i, double rate) => new BatchSlot { Index = i, State = "decoding", PromptTokens = 38_000 + i * 1_900, Generated = 1_840 - i * 310, TokensPerSecond = rate, ElapsedSeconds = 94 - i * 17 };
        EngineSnapshot Make(string scenario, ServerState state, params BatchSlot[] slots)
        {
            var s = Demo(scenario);
            s.Slots = slots.ToList();
            if (s.Live != null) s.Live.Running = slots.Count(x => x.IsActive) > 1 ? slots.Count(x => x.IsActive) : null;
            return s;
        }
        var idle = Demo("idle"); idle.Slots = Enumerable.Range(0, 4).Select(Idle).ToList();
        var one = Make("decode", ServerState.Writing, Decoding(0, 128.4), Idle(1), Idle(2), Idle(3));
        var three = Make("parallel3", ServerState.Writing, Decoding(0, 128.4), Decoding(1, 104.2), Decoding(2, 96.7), Idle(3));
        var finishing = Make("parallel3", ServerState.Writing, Decoding(0, 128.4), Decoding(1, 104.2), Idle(2), Idle(3));
        finishing.Live!.DecodeTokensPerSecond = 128.4 + 104.2;
        finishing.Recent.Insert(0, new RequestStats { PromptTokens = 39_800, ReusedTokens = 36_700, PromptRead = 3_100, PromptMilliseconds = 12_600, OutputTokens = 640, DecodeTokensPerSecond = 96.7, DurationSeconds = 19, FinishedAt = Reference, Finish = "stop" });
        var idleAgain = Demo("idle"); idleAgain.Slots = Enumerable.Range(0, 4).Select(Idle).ToList();
        return new List<(string, EngineSnapshot)> { ("idle", idle), ("1 running", one), ("3 running", three), ("2 running", finishing), ("idle", idleAgain) };
    }

    /// <summary>The distance of every section below the live block from the bottom edge of the window, in logical pixels: what stays on its screen pixels when the live block changes.</summary>
    internal static string BelowLive(OffscreenPanel panel)
    {
        var height = panel.Layout!.ContentHeight;
        return string.Join(", ", panel.Sections.Where(s => s.Name != "Live").Select(s => s.Name + " " + (height - s.Top).ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The panel of a sequence of snapshots on the offscreen path, one update per snapshot; prints what each update did. Returns 0 when every
    /// change was at most one size and position change and one paint, no erase, and the same data painted nothing.
    /// </summary>
    public static int TracePanel(TextWriter output)
    {
        var ok = true;
        using (var panel = new OffscreenPanel())
        {
            var sequence = Sequence();
            foreach (var (label, snapshot) in sequence)
            {
                var update = panel.Show(label, Model(snapshot, Display.StateOf(snapshot)));
                output.WriteLine(update + " height=" + panel.Layout!.Size.Height + " | below the live block, distance from the bottom edge: " + BelowLive(panel));
                ok &= update.PositionChanges <= 1 && update.Paints == 1 && update.Erases == 0;
            }
            // The same data once more (a poll with the same numbers): nothing is painted and nothing moves.
            var last = sequence[^1].Snapshot;
            var again = panel.Show("same data again", Model(last, Display.StateOf(last)));
            output.WriteLine(again);
            ok &= again.PositionChanges == 0 && again.Paints == 0 && again.Erases == 0;
        }
        return ok ? 0 : 1;
    }

    private static PanelModel Model(EngineSnapshot? snapshot, ServerState state, string? error = null)
    {
        var model = new PanelModel { Snapshot = snapshot, State = state, ServerName = Server, Error = error, Now = Reference, VersionLabel = "Stratoscope 0.1.4" };
        model.Tabs.Add(new TabInfo(Server, state, Display.Word(state)));
        return model;
    }

    private static void WithPanel(string path, EngineSnapshot? snapshot, ServerState state, bool dark, string? error = null, bool hovered = false)
    {
        var model = Model(snapshot, state, error);
        // Hover: the first link row, the request under the pointer in both charts and the dots, and the first readout item;
        // focus (keyboard): the Quit row.
        if (hovered) { model.Hover = 0; model.Focus = 1; model.HoverRequest = 3; model.Accent = Color.FromArgb(0, 120, 212); }
        var polled = state != ServerState.Connecting;
        var items = SnapshotRenderer.Items((Server, state, snapshot));
        SnapshotRenderer.Panel(path, model, items, dark, readoutHover: hovered ? 0 : -1);
    }

    /// <summary>Renders the images named in <paramref name="only"/> (all when empty) into <paramref name="directory"/>. Returns 0, or 2 for an unknown name.</summary>
    public static int Run(string directory, IList<string>? only, IList<int>? dpis)
    {
        Directory.CreateDirectory(directory);
        var wanted = only is { Count: > 0 } ? only.Select(n => n.Trim().ToLowerInvariant()).ToList() : Names.ToList();
        var unknown = wanted.Where(n => !Names.Contains(n)).ToList();
        if (unknown.Count > 0) { Console.Error.WriteLine("Unknown image: " + string.Join(", ", unknown) + ". Known: " + string.Join(", ", Names)); return 2; }
        string P(string name) => Path.Combine(directory, "windows-" + name + ".png");

        foreach (var name in wanted)
            switch (name)
            {
                case "panel-dark": WithPanel(P(name), Demo("prefill"), ServerState.Reading, true); break;
                case "panel-light": WithPanel(P(name), Demo("prefill"), ServerState.Reading, false); break;
                case "panel-decode-dark": WithPanel(P(name), Demo("decode"), ServerState.Writing, true); break;
                case "panel-decode-light": WithPanel(P(name), Demo("decode"), ServerState.Writing, false); break;
                case "panel-parallel-dark": WithPanel(P(name), Demo("parallel"), ServerState.Writing, true); break;
                case "panel-parallel-light": WithPanel(P(name), Demo("parallel"), ServerState.Writing, false); break;
                case "panel-parallel3-dark": WithPanel(P(name), Demo("parallel3"), ServerState.Writing, true); break;
                case "panel-parallel3-light": WithPanel(P(name), Demo("parallel3"), ServerState.Writing, false); break;
            case "chart-slots": SnapshotRenderer.ChartsSheet(P(name), Model(Demo("idle"), ServerState.Ready), false); break;
                case "readout-zoom": SnapshotRenderer.ZoomSheet(P(name)); break;
                case "readout-slots": SnapshotRenderer.SlotsSheet(P(name)); break;
            case "readout-numbers": SnapshotRenderer.SlotsSheet(P(name), numbers: true); break;
                case "readout-glyph-sizes": SnapshotRenderer.GlyphSizesSheet(P(name)); break;
                case "hover-states": SnapshotRenderer.HoverSheet(P(name), Model(Demo("idle"), ServerState.Ready)); break;
                case "panel-hover-dark": WithPanel(P(name), Demo("idle"), ServerState.Ready, true, hovered: true); break;
                case "panel-hover-light": WithPanel(P(name), Demo("idle"), ServerState.Ready, false, hovered: true); break;
                case "panel-tooltips": SnapshotRenderer.TooltipsSheet(P(name), Model(Demo("idle"), ServerState.Ready), false); break;
                case "panel-offline":
                    WithPanel(P(name), null, ServerState.Offline, false, StrataProvider.Describe(FetchFailure.Refused, null, "http://127.0.0.1:8095"));
                    break;
                case "panel-no-model": WithPanel(P(name), new EngineSnapshot("Strata", "-", EngineStatus.NoModel), ServerState.NoModel, false); break;
                case "readout-states": SnapshotRenderer.StatesSheet(P(name)); break;
                case "readout-scales": SnapshotRenderer.ScalesSheet(P(name), dpis); break;
                case "readout-layouts": SnapshotRenderer.LayoutsSheet(P(name)); break;
            }
        return 0;
    }
}
