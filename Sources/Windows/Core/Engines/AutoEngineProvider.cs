using System.Text.Json.Serialization;
using Stratoscope.Core.Engines.Basalt;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Core.Engines;

/// <summary>Basalt's and Strata's <c>GET /health</c> both name themselves in <c>service</c>.</summary>
public sealed record EngineHealth
{
    [JsonPropertyName("service")] public string? Service { get; init; }
}

/// <summary>
/// The default provider: a router lists engines of different kinds under one address, so which one answers is asked of the engine itself
/// (<c>/health</c> names its <c>service</c>, once per engine address) and its metrics are then read by the provider for that engine.
/// An engine that does not name itself is read as Strata.
/// </summary>
public sealed class AutoEngineProvider : RoutedProvider
{
    private readonly StrataProvider strata;
    private readonly BasaltProvider basalt;
    private readonly Dictionary<string, bool> isBasalt = new();
    private RoutedProvider current;

    public AutoEngineProvider(EngineConfig config, IJsonSource http, Func<DateTime>? clock = null, ClientConfigReader? clients = null)
        : base(config, http, clients)
    {
        strata = new StrataProvider(config, http, clock, clients);
        basalt = new BasaltProvider(config, http, clients);
        current = strata;
    }

    public override string Brand => current.Brand;

    internal override async Task<EngineRead> ReadAsync(string engineBase, string? routedId, CancellationToken cancellation)
    {
        if (!isBasalt.TryGetValue(engineBase, out var known))
        {
            var health = await http.GetAsync(Endpoints.Join(engineBase, "health"), Json.HealthType, cancellation).ConfigureAwait(false);
            known = health.Ok && string.Equals(health.Body!.Service, BasaltProvider.Id, StringComparison.OrdinalIgnoreCase);
            if (health.Ok)
            {
                if (isBasalt.Count >= 16) isBasalt.Clear();
                isBasalt[engineBase] = known;
            }
        }
        current = known ? basalt : strata;
        var read = await current.ReadAsync(engineBase, routedId, cancellation).ConfigureAwait(false);
        if (!read.Ok) isBasalt.Remove(engineBase);     // the engine behind this address may have been replaced: ask again next time
        return read;
    }
}
