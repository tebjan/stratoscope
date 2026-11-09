using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>What the command-line modes of the app and of the development tools share: options, flags and one poll of the configured servers.</summary>
internal static class CommandLine
{
    public static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static bool Flag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    /// <summary>Polls every configured server <paramref name="rounds"/> times (a pause between rounds) and returns the monitors with their last answer.</summary>
    public static List<ServerMonitor> PollOnce(Config config, int rounds)
    {
        var monitors = new List<ServerMonitor>();
        using (var source = new HttpJsonSource())
        {
            foreach (var s in config.Servers) monitors.Add(new ServerMonitor(s.Name, EngineRegistry.Make(s, source)));
            for (int i = 0; i < rounds; i++)
            {
                Task.Run(() => Task.WhenAll(monitors.Select(m => m.PollAsync(CancellationToken.None)))).GetAwaiter().GetResult();
                if (i + 1 < rounds) Thread.Sleep(700);
            }
        }
        return monitors;
    }
}
