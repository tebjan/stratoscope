namespace Stratoscope.Core.Engines;

/// <summary>What one read of an engine's metrics came to.</summary>
/// <param name="Status">The HTTP status of the metrics request (0 when nothing answered). 429 is the router's per-model request limit.</param>
/// <param name="Ok">True when the engine answered with a JSON document.</param>
/// <param name="Snapshot">The translated document. Null when the engine did not answer, or (reading a server directly) when the document is not an engine's metrics.</param>
internal readonly record struct EngineRead(int Status, bool Ok, FetchFailure Failure, string? Detail, EngineSnapshot? Snapshot);

/// <summary>
/// The part every engine behind a llama-swap style router has in common: ask the router's <c>/running</c> which model runs, wait while it
/// loads, read the engine through the router at <c>/upstream/&lt;model&gt;/</c> (or directly when there is no router), keep the last data
/// when the router's request limit is reached, and name why nothing answers. A subclass only reads and translates its engine's metrics.
/// </summary>
/// <remarks>
/// Speed rule (canonical text; the four engines repeat it): name the source of every value; never estimate.
/// When several requests run together, the headline decode speed (and the headline prefill speed while reading) is the engine's own total over
/// all requests. Each engine reports that total itself:
///  - Strata: live.tok_s, the total of the generating requests (serve/server.py _tok_s). Only when the engine has no total yet (the newest request
///    still reads its prompt) is the sum of the derived per-slot speeds used (StrataProvider.cs, the branch where rates.Sum() is taken). Prefill:
///    live.prefill_tok_s_mean, the engine's mean; a value derived from the progress between polls only when that is missing.
///  - Basalt: live.tok_s, which the engine sums over its slots (BasaltProvider.cs, the speed line).
///  - llama.cpp: /metrics llamacpp:predicted_tokens_seconds, the server-wide decode rate over all slots (prompt_tokens_seconds for prefill). It
///    needs the server's --metrics flag. No sum is used (LlamaCppProvider.cs, the DecodeTokensPerSecond line).
///  - custom: /ui/state tick.sample.decode_tps and tick.sample.prefill_tps, the engine's counter deltas over all requests between samples
///    (CustomProvider.cs). The per-request sum is used only when the sample has no rate.
/// A request's own speed is shown on its own row only. Never add up the per-request rows to make the headline unless the engine has no total.
/// </remarks>
public abstract class RoutedProvider : IEngineProvider
{
    protected readonly EngineConfig config;
    protected readonly IJsonSource http;
    private EngineSnapshot? lastSnapshot;
    private string? lastModelId;

    private readonly ClientConfigReader? clients;

    /// <param name="clients">Where to look for the summarise threshold of the AI clients that use this engine. Null reads no client config.</param>
    protected RoutedProvider(EngineConfig config, IJsonSource http, ClientConfigReader? clients = null)
    {
        this.config = config;
        this.http = http;
        this.clients = clients;
        BaseForLinks = config.Url ?? config.Router ?? Config.DefaultUrl;
    }

    public string? LastError { get; protected set; }

    /// <summary>The engine's name as the panel shows it (for example "Strata"), and in "Open Strata UI".</summary>
    public abstract string Brand { get; }

    /// <summary>The address of the engine's own server (the router's <c>/upstream/&lt;model&gt;</c>, or the configured URL), for links.</summary>
    protected string BaseForLinks { get; set; }

    /// <summary>
    /// Reads and translates the engine at <paramref name="engineBase"/>. <paramref name="routedId"/> is the model id when the router names the
    /// engine, which then counts as an engine whatever it sends; null reads a server directly, where a document that is no engine's gives no snapshot.
    /// </summary>
    internal abstract Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation);

    // MARK: Polling

    private string? lastEngineBase;

    /// <summary>The address the last full poll read the engine at, so a light counter read goes to the same place. Null before one succeeded.</summary>
    public async Task<EngineCounters?> ReadCountersAsync(CancellationToken cancellation) =>
        lastEngineBase is null ? null : await ReadCountersFromAsync(lastEngineBase, cancellation).ConfigureAwait(false);

    /// <summary>A light read of the cumulative counters at <paramref name="engineBase"/>; an engine that has them for the speed curves overrides it. Never throws.</summary>
    internal virtual Task<EngineCounters?> ReadCountersFromAsync(string engineBase, CancellationToken cancellation) => Task.FromResult<EngineCounters?>(null);

    public async Task<EngineSnapshot?> PollAsync(CancellationToken cancellation)
    {
        LastError = null;
        if (config.Router is null) return await PollDirect(cancellation).ConfigureAwait(false);

        var router = config.Router;
        var running = await http.GetAsync(Endpoints.Join(router, "running"), Json.RunningType, cancellation).ConfigureAwait(false);
        if (!running.Ok)
        {
            // Without an explicitly configured server address there is nothing else to ask: no guessing at localhost.
            if (config.Url != null) return await PollDirect(cancellation).ConfigureAwait(false);
            LastError = Describe(running.Failure, running.Detail, router);
            return null;
        }
        var first = running.Body!.Running?.FirstOrDefault(r => r != null);
        if (first is null)
        {
            if (config.Url != null)
            {
                var direct = await PollDirect(cancellation).ConfigureAwait(false);
                if (direct != null) return direct;
                LastError = null;
            }
            lastSnapshot = null; lastModelId = null;
            return Placeholder("-", EngineStatus.NoModel, null);
        }
        var id = first.Model ?? "Model";          // what the router's URLs and the clients' configs use
        var title = Title(first);                 // what the user sees: the friendly name from the router's config, else the id
        var state = first.State ?? "?";
        if (state != "ready") return Placeholder(title, EngineStatus.Loading, "State: " + state);

        var engineBase = Endpoints.Join(router, "upstream", id).AbsoluteUri.TrimEnd('/');
        lastEngineBase = engineBase;
        BaseForLinks = engineBase;
        var read = await ReadAsync(engineBase, id, cancellation).ConfigureAwait(false);
        if (read.Status == 429)
        {
            // The router's per-model request limit is reached. Keep the last data and say so.
            if (lastSnapshot != null && lastModelId == id) return lastSnapshot.WithRouterLimited();
            return Placeholder(title, EngineStatus.Loading, "Router request limit reached");
        }
        if (!read.Ok || read.Snapshot is null) return Placeholder(title, EngineStatus.Error, "Server not responding");
        var snapshot = read.Snapshot;
        snapshot.Model = title;
        lastSnapshot = snapshot; lastModelId = id;
        return snapshot;
    }

    /// <summary>The model's name as the router shows it: llama-swap's <c>name</c> from its config, and the model id when it has none.</summary>
    internal static string Title(RunningModel running) =>
        !string.IsNullOrWhiteSpace(running.Name) ? running.Name.Trim() : running.Model ?? "Model";

    /// <summary>Without a router: ask the engine's server directly. If nothing answers, the engine counts as not running.</summary>
    private async Task<EngineSnapshot?> PollDirect(CancellationToken cancellation)
    {
        var address = config.Url ?? Config.DefaultUrl;
        var read = await ReadAsync(address, null, cancellation).ConfigureAwait(false);
        if (!read.Ok || read.Snapshot is null)
        {
            LastError = read.Ok ? "The answer from " + address + "/metrics is not a " + Brand + " metrics document." : Describe(read.Failure, read.Detail, address);
            return null;
        }
        BaseForLinks = address; lastEngineBase = address;
        lastSnapshot = read.Snapshot; lastModelId = null;
        return read.Snapshot;
    }

    protected EngineSnapshot Placeholder(string model, EngineStatus status, string? reason) =>
        new EngineSnapshot(Brand, model, status) { LoadingReason = reason, Links = Links(engineUi: false) };

    /// <summary>Short English reason, independent of the Windows display language.</summary>
    public static string Describe(FetchFailure failure, string? detail, string target) =>
        failure switch
        {
            FetchFailure.Refused => "Connection refused at " + target + ". Is the engine, or the SSH forward to it, running?",
            FetchFailure.Timeout => "No answer from " + target + " in time.",
            FetchFailure.NameNotResolved => "Host name not found: " + target,
            FetchFailure.Status => target + " answered with " + detail + ".",
            FetchFailure.NotJson => "The answer from " + target + " is not JSON.",
            _ => "Cannot read " + target + (detail != null ? ": " + detail : "."),
        };

    // MARK: Helpers for the subclasses

    protected List<EngineLink> Links(bool engineUi = true)
    {
        var links = new List<EngineLink>();
        if (engineUi) links.Add(new EngineLink("Open " + Brand + " UI", BaseForLinks + "/"));
        if (config.Router != null) links.Add(new EngineLink("Open router UI", config.Router + "/ui/"));
        if (!string.IsNullOrEmpty(config.Webui)) links.Add(new EngineLink("Open chat UI", config.Webui!));
        var log = CurrentLogPath();
        if (log != null) links.Add(new EngineLink("Open log", log));
        return links;
    }

    /// <summary>The engine's log: the configured path, otherwise none (there is no standard place on Windows).</summary>
    internal string? CurrentLogPath() => string.IsNullOrEmpty(config.Log) ? null : config.Log;

    /// <summary>
    /// No engine's metrics have such a value. It comes from the configs of the clients that talk to this server and use this model; where those
    /// say nothing (or disagree), the user's own <c>compact_at</c> is the fallback.
    /// </summary>
    protected double? CompactAt(string model)
    {
        var endpoints = new[] { config.Router, config.Url ?? Config.DefaultUrl, BaseForLinks }.Where(e => e != null).Select(e => e!);
        return clients?.CompactAt(endpoints, config.ClientModel ?? model) ?? config.CompactAt;
    }

    /// <summary>The time a request finished, from the engine's seconds since 1970 (UTC); null for a missing, non-finite or absurd value.</summary>
    protected static DateTime? FinishedAt(double? epochSeconds) =>
        epochSeconds is double time && double.IsFinite(time) && time > 0 && time < 253_402_300_000 ? DateTime.UnixEpoch.AddSeconds(time) : null;

    /// <summary>A section the engine sent as {} (or not at all) is no section: records compare by value, so it equals a fresh one.</summary>
    protected static bool IsEmpty<T>(T? section) where T : class, new() => section is null || section.Equals(new T());
}
