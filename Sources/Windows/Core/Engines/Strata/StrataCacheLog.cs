using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Stratoscope.Core.Engines.Strata;

/// <summary>
/// Rebuilds the list of parked conversations from the Strata log. Port of StrataCacheLog.swift.
///
/// Strata's /metrics reports how many conversations are parked, but not which ones. The log records every park,
/// restore and eviction, so the list is reconstructed from it. The id K&lt;n&gt; is just a running number of parks.
/// The entry count is trimmed to the number /metrics reports.
/// </summary>
public sealed class StrataCacheLog
{
    private static Regex Rx(string pattern) => new Regex(pattern, RegexOptions.CultureInvariant);
    private static readonly Regex ReParked = Rx(@"conversation cache: parked (\d+) tokens in [\d.]+ ms; parked=(\d+) bytes=(\d+) evictions=(\d+) snapshot_bytes=(\d+)");
    private static readonly Regex ReRestored = Rx(@"conversation cache: restored (\d+) tokens .*?parked=(\d+)");
    private static readonly Regex ReBorrowed = Rx(@"conversation cache: borrowed \d+ tokens .*?parked=(\d+)");
    private static readonly Regex ReEvicted = Rx(@"conversation cache: evicted (\d+) parked conversation");
    private static readonly Regex ReDropped = Rx(@"conversation cache: dropped (\d+) superseded .*?parked=(\d+)");

    private readonly Func<DateTime> clock;
    private long offset;
    private int seq;
    private bool primed;
    private int pendingDrop;
    private string? lastPath;

    public StrataCacheLog(Func<DateTime>? clock = null) { this.clock = clock ?? (() => DateTime.UtcNow); }

    public List<CacheEntry> Entries { get; private set; } = new List<CacheEntry>();

    /// <summary>True once a log file was opened, so the entry list means something.</summary>
    public bool Readable { get; private set; }

    private static long[]? Ints(Regex re, string s)
    {
        var m = re.Match(s);
        if (!m.Success) return null;
        var values = new List<long>();
        for (int i = 1; i < m.Groups.Count; i++) if (long.TryParse(m.Groups[i].Value, out var v)) values.Add(v);
        return values.ToArray();
    }

    private void Reset() { Entries = new List<CacheEntry>(); primed = false; pendingDrop = 0; }
    private void Reconcile(long parked) { while (Entries.Count > parked && Entries.Count > 0) Entries.RemoveAt(0); }

    /// <summary>Feeds one log line. <paramref name="known"/> is false while catching up on old history (no timestamps then).</summary>
    public void Handle(string line, bool known)
    {
        if (!line.Contains("conversation cache")) return;
        if (line.Contains("sized from the free RAM") || line.Contains("cache off")) { Entries = new List<CacheEntry>(); pendingDrop = 0; return; }
        long[]? v;
        if ((v = Ints(ReParked, line)) != null)
        {
            var n = v[0];
            for (int i = 0; i < pendingDrop; i++)   // superseded copy of the same conversation: the largest smaller one
            {
                int best = -1;
                for (int j = 0; j < Entries.Count; j++)
                    if (Entries[j].Tokens <= n && (best < 0 || Entries[j].Tokens > Entries[best].Tokens)) best = j;
                if (best >= 0) Entries.RemoveAt(best);
            }
            pendingDrop = 0;
            seq += 1;
            Entries.Add(new CacheEntry(seq, n, v[4], known ? clock() : (DateTime?)null));
            Reconcile(v[1]);
            return;
        }
        if ((v = Ints(ReRestored, line)) != null)
        {
            var i = Entries.FindIndex(e => e.Tokens == v[0]);
            if (i >= 0) Entries.RemoveAt(i);
            pendingDrop = 0; Reconcile(v[1]);
            return;
        }
        if ((v = Ints(ReBorrowed, line)) != null) { Reconcile(v[0]); return; }
        if ((v = Ints(ReEvicted, line)) != null)
        {
            for (long i = 0; i < Math.Min(v[0], Entries.Count); i++) Entries.RemoveAt(0);
            return;
        }
        if ((v = Ints(ReDropped, line)) != null) pendingDrop = (int)v[0];
    }

    /// <summary>Reads whatever was appended to the log since the last call. Read-only; the engine may keep writing.</summary>
    public void Update(string? path)
    {
        if (path == null) { Readable = false; return; }
        if (path != lastPath) { lastPath = path; offset = 0; Reset(); }
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Readable = true;
                var size = fs.Length;
                if (size < offset) { offset = 0; Reset(); }
                var start = offset;
                if (!primed && size > 8_000_000) start = size - 8_000_000;   // only the recent history on first read
                fs.Seek(start, SeekOrigin.Begin);
                var data = new byte[size - start];
                int read = 0;
                while (read < data.Length)
                {
                    var n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var lastNewline = Array.LastIndexOf(data, (byte)'\n', Math.Max(0, read - 1));
                if (read == 0 || lastNewline < 0) return;
                offset = start + lastNewline + 1;
                var known = primed;
                foreach (var line in Encoding.UTF8.GetString(data, 0, lastNewline).Split('\n')) Handle(line.TrimEnd('\r'), known);
                primed = true;
            }
        }
        catch (IOException) { Readable = false; }
        catch (UnauthorizedAccessException) { Readable = false; }
    }
}
