using System.Text.Json.Serialization;
using Stratoscope.Core.Engines.Basalt;
using Stratoscope.Core.Engines.LlamaCpp;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Core.Engines;

/// <summary>Basalt's and Strata's <c>GET /health</c> both name themselves in <c>service</c>. llama-server's is just <c>{"status":"ok"}</c>.</summary>
public sealed record EngineHealth
{
    [JsonPropertyName("service")] public string? Service { get; init; }
}

/// <summary>
/// The default provider: a router lists engines of different kinds under one address, so which one answers is asked of the engine itself
/// (once per engine address) and its metrics are then read by the provider for that engine. <c>/health</c> names its <c>service</c> for Basalt
/// and Strata. An engine whose health names nothing is asked for <c>/props</c>: only a llama-server answers with its slot count and
/// generation settings. An engine that is none of these is read as Strata.
/// </summary>
public sealed class AutoEngineProvider : RoutedProvider
{
    private enum Kind { Strata, Basalt, LlamaCpp }

    private readonly StrataProvider strata;
    private readonly BasaltProvider basalt;
    private readonly LlamaCppProvider llamaCpp;
    private readonly Dictionary<string, Kind> kinds = new();
    private RoutedProvider current;

    public AutoEngineProvider(EngineConfig config, IJsonSource http, Func<DateTime>? clock = null, ClientConfigReader? clients = null)
        : base(config, http, clients)
    {
        strata = new StrataProvider(config, http, clock, clients);
        basalt = new BasaltProvider(config, http, clients);
        llamaCpp = new LlamaCppProvider(config, http, clients);
        current = strata;
    }

    public override string Brand => current.Brand;

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        if (!kinds.TryGetValue(engineBase, out var kind))
        {
            var health = await http.GetAsync(Endpoints.Join(engineBase, "health"), Json.HealthType, cancellation).ConfigureAwait(false);
            var cacheable = health.Ok;
            kind = Kind.Strata;
            if (health.Ok)
            {
                var service = health.Body!.Service;
                if (string.Equals(service, BasaltProvider.Id, StringComparison.OrdinalIgnoreCase)) kind = Kind.Basalt;
                else if (string.IsNullOrWhiteSpace(service))
                {
                    // Unnamed: a llama-server, or an engine that does not name itself. A refused or timed-out probe says nothing, so it is asked again.
                    var props = await http.GetAsync(Endpoints.Join(engineBase, "props"), Json.LlamaPropsType, cancellation).ConfigureAwait(false);
                    if (props.Ok && props.Body!.IsLlamaServer) kind = Kind.LlamaCpp;
                    else cacheable = props.Ok || props.Failure is FetchFailure.Status or FetchFailure.NotJson;
                }
            }
            if (cacheable)
            {
                if (kinds.Count >= 16) kinds.Clear();
                kinds[engineBase] = kind;
            }
        }
        current = kind switch { Kind.Basalt => basalt, Kind.LlamaCpp => llamaCpp, _ => strata };
        var read = await current.ReadAsync(engineBase, routedId, cancellation).ConfigureAwait(false);
        if (!read.Ok) kinds.Remove(engineBase);     // the engine behind this address may have been replaced: ask again next time
        return read;
    }
}
