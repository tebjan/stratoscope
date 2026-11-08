namespace Stratoscope.Core;

/// <summary>
/// An adapter between one inference engine and Stratoscope.
///
/// To support a new engine: implement this interface, translate the engine's metrics into an
/// <see cref="EngineSnapshot"/>, and register it in <see cref="EngineRegistry"/>.
/// Report only what the engine really knows; leave everything else null.
/// </summary>
public interface IEngineProvider
{
    /// <summary>
    /// Called every couple of seconds. Returns null when the engine is not running; <see cref="LastError"/> then says why.
    /// </summary>
    Task<EngineSnapshot?> PollAsync(CancellationToken cancellation);

    /// <summary>Why the last poll returned null, in English. Null when it did not.</summary>
    string? LastError { get; }
}

public static class EngineRegistry
{
    public static readonly string[] Available = { Engines.Strata.StrataProvider.Id, Engines.Demo.DemoProvider.Id };

    public static IEngineProvider Make(EngineConfig config, IJsonSource source)
    {
        switch (config.Engine.ToLowerInvariant())
        {
            case Engines.Demo.DemoProvider.Id:
                return new Engines.Demo.DemoProvider(config.Scenario);
            default:
                var clients = config.ReadClientConfigs ? new ClientConfigReader(configHome: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) : null;
                return new Engines.Strata.StrataProvider(config, source, clients: clients);
        }
    }
}
