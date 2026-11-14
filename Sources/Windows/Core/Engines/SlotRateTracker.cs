namespace Stratoscope.Core.Engines;

/// <summary>
/// The speed of each batch slot from a cumulative count the engine reports per slot (tokens generated, or prompt tokens computed), taken between
/// polls: the oldest sample of the last few seconds against the newest, needing at least a second between them. A slot that has fewer samples,
/// or one that started a new request (its count fell, or time did not advance), has no speed yet. Used by engines whose slots carry the count but
/// not a usable speed of their own (Strata: its own <c>tok_s</c> counts from before the slot's first token; llama-server: no speed per slot).
/// One tracker per count; the engine adapter calls <see cref="Update"/> once per poll.
/// </summary>
internal sealed class SlotRateTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(4), MinSpan = TimeSpan.FromSeconds(1);
    private readonly Dictionary<int, List<(DateTime At, double Value)>> history = new();

    /// <param name="slots">The slots that run in the phase this tracker measures, each with its cumulative count (null when the slot does not say).
    /// A slot that is not listed (it ended, or moved on to another phase) loses its history.</param>
    /// <returns>Tokens per second by slot id, only for slots that have a speed.</returns>
    public Dictionary<int, double> Update(IEnumerable<(int Id, double? Value)> slots, DateTime now)
    {
        var rates = new Dictionary<int, double>();
        var list = slots.ToList();
        var live = list.Select(s => s.Id).ToHashSet();
        foreach (var gone in history.Keys.Where(k => !live.Contains(k)).ToList()) history.Remove(gone);
        foreach (var (id, value) in list)
        {
            if (value is not double count) continue;
            if (!history.TryGetValue(id, out var samples)) history[id] = samples = new List<(DateTime, double)>();
            if (samples.Count > 0 && (count < samples[^1].Value || now <= samples[^1].At)) samples.Clear();      // a new request in this slot
            samples.Add((now, count));
            samples.RemoveAll(h => now - h.At > Window);
            var oldest = samples[0];
            if (now - oldest.At >= MinSpan && count >= oldest.Value) rates[id] = (count - oldest.Value) / (now - oldest.At).TotalSeconds;
        }
        return rates;
    }
}
