namespace Stratoscope.Core;

/// <summary>What one server looks like to the user, from least to most active. Drives dots, words and the tray icon.</summary>
public enum ServerState
{
    Connecting,
    /// <summary>Nothing answers at the configured address.</summary>
    Offline,
    Loading,
    /// <summary>The router answers but has no model loaded.</summary>
    NoModel,
    /// <summary>The router answers but the engine behind it does not, or the app hit an unexpected error.</summary>
    Error,
    Ready,
    Reading,
    Thinking,
    Writing,
}

/// <summary>The combined progress of requests running together: the fraction and whether it is measured against real limits.</summary>
public readonly record struct CombinedProgress(double Fraction, bool IsLimit);

/// <summary>
/// The text of one server in the taskbar readout: the speed and its unit while something runs, nothing otherwise (the glyph alone says
/// the state; the words are in the tooltip and the screen reader name).
/// </summary>
public sealed class ReadoutText
{
    public ReadoutText(string value, string unit) { Value = value; Unit = unit; }
    /// <summary>The speed while something runs ("247"), empty otherwise.</summary>
    public string Value { get; }
    /// <summary>The unit after the speed ("t/s"), empty when there is no speed.</summary>
    public string Unit { get; }
    public static ReadoutText None { get; } = new ReadoutText("", "");
}

/// <summary>
/// The words and numbers that every UI shows the same way. Mirrors menuBarTitle() and Panels.word() of the Mac app.
/// States are named in plain words (reading, thinking, writing, ready, waiting, offline); the metric names Prefill and Decode
/// appear only where a metric is meant, never as a letter badge.
/// </summary>
public static class Display
{
    /// <summary>The server's state; null means "no answer".</summary>
    public static ServerState StateOf(EngineSnapshot? s)
    {
        if (s is null) return ServerState.Offline;
        switch (s.Status)
        {
            case EngineStatus.Loading: return ServerState.Loading;
            case EngineStatus.NoModel: return ServerState.NoModel;
            case EngineStatus.Error: return ServerState.Error;
            case EngineStatus.Prefill: return ServerState.Reading;
            case EngineStatus.Thinking: return ServerState.Thinking;
            case EngineStatus.Decode: return ServerState.Writing;
            default: return ServerState.Ready;
        }
    }

    public static string Word(ServerState state)
    {
        switch (state)
        {
            case ServerState.Connecting: return "Connecting";
            case ServerState.Offline: return "Offline";
            case ServerState.Loading: return "Loading";
            case ServerState.NoModel: return "No model";
            case ServerState.Error: return "Error";
            case ServerState.Reading: return "Reading";
            case ServerState.Thinking: return "Thinking";
            case ServerState.Writing: return "Writing";
            default: return "Ready";
        }
    }

    /// <summary>The tray title: the speed that matters right now, plus the queue length.</summary>
    public static string Title(EngineSnapshot s)
    {
        var queue = s.Queued > 0 ? " +" + s.Queued : "";
        switch (s.Status)
        {
            case EngineStatus.Loading: return "Loading";
            case EngineStatus.NoModel: return "No model";
            case EngineStatus.Error: return "Error";
            case EngineStatus.Idle: return "Ready";
            case EngineStatus.Prefill: return Fmt.Speed(s.Live?.PrefillTokensPerSecond) + queue;
            default: return Fmt.Speed(s.Live?.DecodeTokensPerSecond) + queue;
        }
    }

    /// <summary>The speed as the readout shows it: plain digits, no separator, no decimal ("26", "3345"), at most four characters: "10k" from 9,999.5 t/s.</summary>
    public static string ReadoutNumber(double speed)
    {
        var v = Math.Max(0, speed);
        if (v < 9999.5) return Math.Round(v).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return Math.Min(99, (int)Math.Round(v / 1000)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k";
    }

    /// <summary>Short state for a tab or tooltip: "reading 247 t/s", "writing 26 t/s +1", "ready", "offline".</summary>
    public static string Brief(EngineSnapshot? s, bool polled = true, bool faulted = false)
    {
        if (faulted) return Word(ServerState.Error);
        if (!polled && s is null) return Word(ServerState.Connecting);
        if (s is null) return Word(ServerState.Offline);
        var queue = s.Queued > 0 ? " +" + s.Queued : "";
        switch (s.Status)
        {
            case EngineStatus.Prefill: return "Reading " + Fmt.Number(s.Live?.PrefillTokensPerSecond) + " t/s" + queue;
            case EngineStatus.Thinking: return "Thinking " + Fmt.Number(s.Live?.DecodeTokensPerSecond) + " t/s" + queue;
            case EngineStatus.Decode: return "Writing " + Fmt.Number(s.Live?.DecodeTokensPerSecond) + " t/s" + queue;
            default: return Word(StateOf(s));
        }
    }

    /// <summary>
    /// The text of one taskbar readout item: the speed and its unit while a prompt is read or tokens are written (the total of all running
    /// requests), otherwise nothing. Never a placeholder or a state word: a server that is not busy, or busy without a speed yet, has the
    /// glyph only.
    /// </summary>
    public static ReadoutText Readout(EngineSnapshot? s)
    {
        if (s is null) return ReadoutText.None;
        ReadoutText Run(double? speed) => speed.HasValue && double.IsFinite(speed.Value) ? new ReadoutText(ReadoutNumber(speed.Value), "t/s") : ReadoutText.None;
        switch (s.Status)
        {
            case EngineStatus.Prefill: return Run(s.Live?.PrefillTokensPerSecond);
            case EngineStatus.Thinking:
            case EngineStatus.Decode: return Run(s.Live?.DecodeTokensPerSecond);
            default: return ReadoutText.None;
        }
    }

    /// <summary>What a screen reader says for one server: "Workstation, writing 26 tokens per second". Also the tooltip of the readout item.</summary>
    public static string Spoken(string name, EngineSnapshot? s, bool polled = true, bool faulted = false)
    {
        if (faulted) return name + ", error";
        if (s is null) return name + ", " + (polled ? "offline" : "connecting");
        switch (s.Status)
        {
            case EngineStatus.Prefill:
                var pct = s.Live?.Percent;
                return name + ", reading the prompt" + (pct.HasValue ? " " + pct + " percent" : "") + (s.Live?.PrefillTokensPerSecond.HasValue == true ? ", " + Fmt.Number(s.Live!.PrefillTokensPerSecond) + " tokens per second" : "");
            case EngineStatus.Thinking: return name + ", thinking" + SpokenSpeed(s);
            case EngineStatus.Decode: return name + ", writing" + SpokenSpeed(s);
            case EngineStatus.Loading: return name + ", loading the model";
            case EngineStatus.NoModel: return name + ", waiting for a model";
            case EngineStatus.Error: return name + ", not responding";
            default:
                return name + ", ready" + (s.Info?.BatchSlots is double slots && slots > 1 ? ", " + Fmt.Number(slots) + " parallel requests" : "");
        }
    }

    private static string SpokenSpeed(EngineSnapshot s) =>
        s.Live?.DecodeTokensPerSecond.HasValue == true ? " " + Fmt.Number(s.Live!.DecodeTokensPerSecond) + " tokens per second" : "";

    /// <summary>
    /// The progress of one request that runs in a batch slot, 0..1. Reading: the share of its prompt. Generating: against its token limit when
    /// the engine reports one for the slot; otherwise against the largest generated count among the running requests (<paramref name="longest"/>),
    /// which says how far this request is relative to the others, not how far it is from its end.
    /// </summary>
    public static double SlotFraction(BatchSlot slot, double longest)
    {
        double F(double? part, double? whole) => part.HasValue && whole > 0 ? Math.Clamp(part.Value / whole.Value, 0, 1) : 0;
        if (slot.IsReading) return F(slot.PromptRead, slot.PromptTokens);
        return slot.MaxTokens > 0 ? F(slot.Generated, slot.MaxTokens) : F(slot.Generated, longest);
    }

    /// <summary>
    /// The combined progress of the running requests for the main bar. Reading only: all prompt tokens read over all prompt tokens. Otherwise
    /// the generating requests: all tokens generated over all token limits when every one has a limit, else the mean of their relative lengths.
    /// <see cref="CombinedProgress.IsLimit"/> says which.
    /// </summary>
    public static CombinedProgress Combined(IReadOnlyList<BatchSlot> running)
    {
        var decoding = running.Where(s => s.IsDecoding).ToList();
        if (decoding.Count == 0)
        {
            var reading = running.Where(s => s.IsReading).ToList();
            var total = reading.Sum(s => s.PromptTokens ?? 0);
            return new CombinedProgress(total > 0 ? Math.Clamp(reading.Sum(s => s.PromptRead ?? 0) / total, 0, 1) : 0, true);
        }
        if (decoding.All(s => s.MaxTokens > 0))
            return new CombinedProgress(Math.Clamp(decoding.Sum(s => s.Generated ?? 0) / decoding.Sum(s => s.MaxTokens!.Value), 0, 1), true);
        var longest = decoding.Max(s => s.Generated ?? 0);
        return new CombinedProgress(longest > 0 ? decoding.Average(s => SlotFraction(s, longest)) : 0, false);
    }

    /// <summary>While a prompt is read: the fraction done and the cache quality, for the progress bar in the icon. Otherwise null.</summary>
    public static (double Fraction, CacheHit? Hit)? Progress(EngineSnapshot s)
    {
        if (!s.IsReading || s.Live is null) return null;
        var fraction = (s.Live.Percent ?? 0) / 100.0;
        var hit = s.Live.PromptTotal is double total && s.Live.ReusedEstimate is double reused ? CacheHits.Classify(total, reused) : (CacheHit?)null;
        return (fraction, hit);
    }
}
