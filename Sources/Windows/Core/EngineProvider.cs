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
    public static readonly string[] Available = { Engines.Strata.StrataProvider.Id, Engines.Basalt.BasaltProvider.Id, Engines.LlamaCpp.LlamaCppProvider.Id, Engines.Custom.CustomProvider.Id, Engines.Demo.DemoProvider.Id };

    /// <summary>
    /// "demo" is the demo engine, "basalt" reads Basalt only, "llamacpp" reads llama-server only. Anything else, "strata" (the default) included,
    /// asks the engine which kind it is and reads Strata, Basalt or llama.cpp accordingly, so one router can serve all three.
    /// </summary>
    public static IEngineProvider Make(EngineConfig config, IJsonSource source)
    {
        switch (config.Engine.ToLowerInvariant())
        {
            case Engines.Demo.DemoProvider.Id:
                return new Engines.Demo.DemoProvider(config.Scenario);
            case Engines.Basalt.BasaltProvider.Id:
                return new Engines.Basalt.BasaltProvider(config, source, ClientsFor(config));
            case Engines.LlamaCpp.LlamaCppProvider.Id:
                return new Engines.LlamaCpp.LlamaCppProvider(config, source, ClientsFor(config));
            case Engines.Custom.CustomProvider.Id:
                return new Engines.Custom.CustomProvider(config, source, ClientsFor(config));
            default:
                return new Engines.AutoEngineProvider(config, source, clients: ClientsFor(config));
        }
    }

    private static ClientConfigReader? ClientsFor(EngineConfig config) =>
        config.ReadClientConfigs ? new ClientConfigReader(configHome: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) : null;
}
