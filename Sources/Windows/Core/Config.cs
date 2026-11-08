using System.Globalization;
using System.IO;

namespace Stratoscope.Core;

/// <summary>One engine to watch.</summary>
public sealed class EngineConfig
{
    public string Name { get; set; } = "Strata";
    /// <summary>"strata" (default) or "demo".</summary>
    public string Engine { get; set; } = "strata";
    /// <summary>The engine server, for example http://127.0.0.1:8095. Used when no router is set, or as fallback.</summary>
    public string? Url { get; set; }
    /// <summary>A llama-swap style router in front of the engine.</summary>
    public string? Router { get; set; }
    /// <summary>The engine log file (only readable when the engine runs on this machine).</summary>
    public string? Log { get; set; }
    /// <summary>A chat UI to link to.</summary>
    public string? Webui { get; set; }
    /// <summary>Demo engine only: prefill, decode or idle.</summary>
    public string? Scenario { get; set; }
    /// <summary>The model id your client config uses for this engine, when it differs from the name the engine reports.</summary>
    public string? ClientModel { get; set; }
    /// <summary>The fallback for the summarise marker (the global <c>compact_at</c>), used when no client config says.</summary>
    public double? CompactAt { get; set; }
    /// <summary>False when the config files of clients such as Kilo and OpenCode must not be read (<c>client_configs</c>: none).</summary>
    public bool ReadClientConfigs { get; set; } = true;
}

/// <summary>Where in the taskbar the readout sits.</summary>
public enum ReadoutPosition
{
    /// <summary>Centred in the free gap between the last task-button cluster and the notification area (default); next to the notification area when the gap is too small.</summary>
    Center,
    /// <summary>Directly left of the notification area.</summary>
    Tray,
    /// <summary>Right after the last task button, so it moves with the buttons.</summary>
    AfterButtons,
    /// <summary>At the left edge of the taskbar, in the free space left of centred buttons.</summary>
    Left,
}

/// <summary>
/// Settings. Precedence for each value: environment variable, then the JSON file, then the default.
///
///     STRATOSCOPE_ENGINE      strata (default) | demo
///     STRATOSCOPE_URL         engine server (Strata default: http://127.0.0.1:8095)
///     STRATOSCOPE_ROUTER_URL  llama-swap style router in front of the engine ("none" disables)
///     STRATOSCOPE_LOG         engine log file
///     STRATOSCOPE_WEBUI_URL   adds an "Open chat UI" link
///     STRATOSCOPE_COMPACT_AT  token count at which your client summarises the chat; drawn as a marker on the context bar
///                             (fallback for when no client config can be read)
///     STRATOSCOPE_CLIENT_MODEL    the model id your client config uses for this engine, if it differs from the name the engine reports
///     STRATOSCOPE_CLIENT_CONFIGS  "none" stops Stratoscope from reading the config files of clients such as Kilo and OpenCode
///     STRATOSCOPE_READOUT_POSITION  center (default) | tray (next to the notification area) | after-buttons | left
///     STRATOSCOPE_UI_SCALE    auto (default) or 100 to 250
///     STRATOSCOPE_CONFIG      path of the JSON file (default: %LOCALAPPDATA%\Stratoscope\config.json)
///
/// The JSON file may hold a "servers" list (objects with name, engine, url, router, log, webui, client_model) to watch several engines.
/// Setting STRATOSCOPE_URL or STRATOSCOPE_ROUTER_URL watches that one engine instead of the list.
/// </summary>
public sealed class Config
{
    public List<EngineConfig> Servers { get; } = new List<EngineConfig>();
    public double? CompactAt { get; set; }
    /// <summary>False when <c>client_configs</c> is "none": the config files of Kilo and OpenCode are not read.</summary>
    public bool ReadClientConfigs { get; set; } = true;
    public ReadoutPosition ReadoutPosition { get; set; } = ReadoutPosition.Center;
    /// <summary>Overrides the Windows display scale for this app (100 to 250), or null to follow Windows ("auto").</summary>
    public int? UiScalePercent { get; set; }
    /// <summary>Things in the settings that could not be used, in English, for the menu and --dump.</summary>
    public List<string> Problems { get; } = new List<string>();
    /// <summary>The file that was read (or would be read).</summary>
    public string? Path { get; set; }

    public const string DefaultUrl = "http://127.0.0.1:8095";

    /// <summary>The file that "Open settings file" creates when there is none: the defaults, spelled out.</summary>
    public const string Template =
        "{\r\n" +
        "  \"servers\": [\r\n" +
        "    { \"name\": \"Strata\", \"url\": \"" + DefaultUrl + "\" }\r\n" +
        "  ]\r\n" +
        "}\r\n";

    public static string DefaultPath(IDictionary<string, string?>? environment = null)
    {
        var overridden = environment != null && environment.TryGetValue("STRATOSCOPE_CONFIG", out var p) ? p : null;
        if (!string.IsNullOrEmpty(overridden)) return overridden!;
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stratoscope", "config.json");
    }

    public static IDictionary<string, string?> CurrentEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        return env;
    }

    public static Config Load(IDictionary<string, string?>? environment = null, string? path = null)
    {
        var env = environment ?? CurrentEnvironment();
        var config = new Config { Path = path ?? DefaultPath(env) };
        var file = new ConfigFile();
        if (File.Exists(config.Path))
        {
            try
            {
                var parsed = Json.Deserialize(File.ReadAllText(config.Path, System.Text.Encoding.UTF8), Json.ConfigType);
                if (parsed == null) config.Problems.Add("The settings file is not a JSON object: " + config.Path);
                else file = parsed;
            }
            catch (IOException e) { config.Problems.Add("Cannot read " + config.Path + ": " + e.Message); }
            catch (UnauthorizedAccessException e) { config.Problems.Add("Cannot read " + config.Path + ": " + e.Message); }
        }

        // The environment variable wins over the value in the file.
        string? Pick(string envKey, string? fromFile) => env.TryGetValue(envKey, out var v) && !string.IsNullOrEmpty(v) ? v : fromFile;

        // Settings that apply to every server.
        var compact = Pick("STRATOSCOPE_COMPACT_AT", file.CompactAt);
        if (compact != null && double.TryParse(compact, NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) config.CompactAt = k;
        if (string.Equals(Pick("STRATOSCOPE_CLIENT_CONFIGS", file.ClientConfigs)?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) config.ReadClientConfigs = false;

        var position = Pick("STRATOSCOPE_READOUT_POSITION", file.ReadoutPosition)?.Trim().ToLowerInvariant();
        if (position == "after-buttons") config.ReadoutPosition = ReadoutPosition.AfterButtons;
        else if (position == "left") config.ReadoutPosition = ReadoutPosition.Left;
        else if (position == "tray") config.ReadoutPosition = ReadoutPosition.Tray;
        else if (position != null && position != "center") config.Problems.Add("Ignored readout_position \"" + position + "\": use center, tray, after-buttons or left.");

        var scale = Pick("STRATOSCOPE_UI_SCALE", file.UiScale)?.Trim().ToLowerInvariant();
        if (scale != null && scale != "auto")
        {
            if (int.TryParse(scale.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) && percent >= 100 && percent <= 250) config.UiScalePercent = percent;
            else config.Problems.Add("Ignored ui_scale \"" + scale + "\": use auto or a number from 100 to 250.");
        }

        var defaults = new EngineConfig
        {
            Engine = Pick("STRATOSCOPE_ENGINE", file.Engine) ?? "strata",
            Log = Pick("STRATOSCOPE_LOG", file.Log),
            Webui = Pick("STRATOSCOPE_WEBUI_URL", file.Webui),
            Scenario = Pick("STRATOSCOPE_SCENARIO", file.Scenario),
            ClientModel = Pick("STRATOSCOPE_CLIENT_MODEL", file.ClientModel),
        };

        bool envEngine = env.TryGetValue("STRATOSCOPE_URL", out var eu) && !string.IsNullOrEmpty(eu)
            || env.TryGetValue("STRATOSCOPE_ROUTER_URL", out var er) && !string.IsNullOrEmpty(er);
        var listed = (file.Servers ?? new List<ServerFile?>()).Where(s => s != null).Select(s => s!).ToList();
        if (!envEngine && listed.Count > 0)
        {
            foreach (var item in listed)
            {
                var server = new EngineConfig
                {
                    Engine = item.Engine ?? defaults.Engine,
                    Url = item.Url,
                    Router = item.Router,
                    Log = item.Log ?? defaults.Log,
                    Webui = item.Webui ?? defaults.Webui,
                    Scenario = item.Scenario ?? defaults.Scenario,
                    ClientModel = item.ClientModel ?? defaults.ClientModel,
                };
                Finish(config, server, item.Name);
            }
        }
        else
        {
            defaults.Url = Pick("STRATOSCOPE_URL", file.Url);
            defaults.Router = Pick("STRATOSCOPE_ROUTER_URL", file.Router);
            Finish(config, defaults, null);
        }
        return config;
    }

    /// <summary>Normalises the addresses, names the server, and adds it.</summary>
    private static void Finish(Config config, EngineConfig server, string? name)
    {
        server.Url = Address(config, server.Url, "url");
        server.Router = Address(config, server.Router, "router");
        if (server.Router == "none") server.Router = null;
        if (server.Url == "none") server.Url = null;
        server.Engine = server.Engine.ToLowerInvariant();
        server.CompactAt = config.CompactAt;
        server.ReadClientConfigs = config.ReadClientConfigs;
        if (server.Engine == "strata" && server.Url == null && server.Router == null) server.Url = DefaultUrl;
        server.Name = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : DefaultName(server);
        config.Servers.Add(server);
    }

    private static string? Address(Config config, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value!.Trim();
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return "none";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return value.TrimEnd('/');
        config.Problems.Add("Ignored " + key + " \"" + value + "\": it is not an http or https address.");
        return null;
    }

    private static string DefaultName(EngineConfig server)
    {
        if (server.Engine == "demo") return "Demo";
        var address = server.Router ?? server.Url;
        return address != null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : "Strata";
    }
}
