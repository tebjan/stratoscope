using System.IO;
using System.Text.Json;

namespace Stratoscope.Core;

// Where an AI client summarises ("compacts") the conversation is the client's decision, and no engine reports it. Some clients write what
// they need to work it out into their own config file: the address of the server they talk to and, per model, the context size and the
// output size. This file reads those numbers, read-only, and nothing else. It never touches API keys. Port of ClientLimits.swift.

/// <summary>One model as a client config describes it.</summary>
/// <param name="Client">"Kilo" or "OpenCode".</param>
/// <param name="BaseUrl">The server the client sends requests to.</param>
/// <param name="Model">The model id the client uses.</param>
public sealed record ClientModelLimit(string Client, string BaseUrl, string Model, double Context, double Output)
{
    /// <summary>
    /// Kilo and OpenCode summarise when the conversation no longer leaves room for an answer: context size minus output size.
    /// Observed with Kilo (65,536 for 90,112 and 24,576). Other clients may use another rule and need their own reader.
    /// </summary>
    public double CompactAt => Context - Output;
}

public static class ClientLimits
{
    // JSON with comments and trailing commas, the way these configs are written: the library reads that itself.
    private static readonly JsonDocumentOptions Options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Reads the provider and model limits out of a Kilo or OpenCode config. Both layouts are understood:
    /// <c>provider.&lt;id&gt;.options.baseURL</c> and <c>providers.&lt;id&gt;.settings.baseURL</c>. Broken text or an unknown layout gives an empty list.
    /// </summary>
    public static List<ClientModelLimit> Parse(string? text, string client)
    {
        var found = new List<ClientModelLimit>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿'), Options);
            foreach (var key in new[] { "provider", "providers" })
            {
                if (Member(document.RootElement, key) is not { ValueKind: JsonValueKind.Object } providers) continue;
                foreach (var entry in providers.EnumerateObject())
                {
                    var provider = entry.Value;
                    if (provider.ValueKind != JsonValueKind.Object) continue;
                    var options = Member(provider, "options") is { ValueKind: JsonValueKind.Object } o ? o
                        : Member(provider, "settings") is { ValueKind: JsonValueKind.Object } s ? s : (JsonElement?)null;
                    var address = options is { } opts ? Member(opts, "baseURL") ?? Member(opts, "baseUrl") : null;
                    if (address is not { ValueKind: JsonValueKind.String } || Member(provider, "models") is not { ValueKind: JsonValueKind.Object } models) continue;
                    foreach (var model in models.EnumerateObject())
                    {
                        if (model.Value.ValueKind != JsonValueKind.Object || Member(model.Value, "limit") is not { ValueKind: JsonValueKind.Object } limit) continue;
                        if (Number(limit, "context") is not double context || Number(limit, "output") is not double output) continue;
                        var id = Member(model.Value, "modelID") is { ValueKind: JsonValueKind.String } named ? named.GetString()! : model.Name;
                        found.Add(new ClientModelLimit(client, address.Value.GetString()!, id, context, output));
                    }
                }
            }
        }
        catch (JsonException) { return new List<ClientModelLimit>(); }
        return found;
    }

    private static JsonElement? Member(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? value : (JsonElement?)null;

    private static double? Number(JsonElement parent, string name) =>
        Member(parent, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var value) && double.IsFinite(value) ? value : (double?)null;

    /// <summary>
    /// The summarise threshold for the model that is running, or null. Only configs that talk to this engine (same host and port) and name
    /// this model count. When several do and they disagree, or none does, nobody knows: null, no marker.
    /// </summary>
    public static double? CompactAt(IEnumerable<ClientModelLimit> limits, IEnumerable<string> endpoints, string model)
    {
        var wanted = endpoints.Select(HostAndPort).Where(e => e != null).ToHashSet();
        var values = limits
            .Where(l => l.Model == model && HostAndPort(l.BaseUrl) is string at && wanted.Contains(at) && l.CompactAt > 0)
            .Select(l => l.CompactAt).ToHashSet();
        return values.Count == 1 ? values.First() : (double?)null;
    }

    /// <summary>"127.0.0.1:8080" for any spelling of that address (localhost, default ports); null if it is not a URL.</summary>
    internal static string? HostAndPort(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "::1" || host == "[::1]") host = "127.0.0.1";
        return host + ":" + (uri.Port > 0 ? uri.Port : 80);
    }
}

/// <summary>Finds the config files of the clients we know and caches what they say until a file changes.</summary>
public sealed class ClientConfigReader
{
    private readonly List<(string Client, string Path)> sources;
    private readonly Dictionary<string, (DateTime Modified, List<ClientModelLimit> Limits)> cache = new Dictionary<string, (DateTime, List<ClientModelLimit>)>();

    /// <summary>
    /// <paramref name="home"/>: the user's profile folder (default: the current user's). <paramref name="configHome"/>: where the clients keep their
    /// config. Kilo and OpenCode use the <c>.config</c> folder in the profile on Windows too; pass <c>XDG_CONFIG_HOME</c> when it is set.
    /// </summary>
    public ClientConfigReader(string? home = null, string? configHome = null)
    {
        home = string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
        var root = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
        sources = new List<(string, string)>
        {
            ("Kilo", Path.Combine(root, "kilo", "kilo.jsonc")),
            ("Kilo", Path.Combine(root, "kilo", "kilo.json")),
            ("OpenCode", Path.Combine(root, "opencode", "opencode.json")),
            ("OpenCode", Path.Combine(root, "opencode", "opencode.jsonc")),
        };
    }

    public List<ClientModelLimit> Limits()
    {
        var all = new List<ClientModelLimit>();
        foreach (var (client, path) in sources)
        {
            if (!File.Exists(path)) { cache.Remove(path); continue; }
            var modified = File.GetLastWriteTimeUtc(path);
            if (cache.TryGetValue(path, out var hit) && hit.Modified == modified) { all.AddRange(hit.Limits); continue; }
            string text;
            try { text = File.ReadAllText(path, System.Text.Encoding.UTF8); }
            catch (IOException) { text = ""; }
            catch (UnauthorizedAccessException) { text = ""; }
            var parsed = ClientLimits.Parse(text, client);
            cache[path] = (modified, parsed);
            all.AddRange(parsed);
        }
        return all;
    }

    public double? CompactAt(IEnumerable<string> endpoints, string model) => ClientLimits.CompactAt(Limits(), endpoints, model);
}
