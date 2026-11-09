using System.Net;
using System.Net.Sockets;

namespace Stratoscope.Core;

/// <summary>
/// Finds the servers of this computer when nobody has said where they are (no settings file, no environment variable): the
/// addresses the user's clients (Kilo, OpenCode) already use, then the usual local ports. A router answers <c>/running</c>, a Strata
/// server answers <c>/metrics</c>; nothing else is asked, nothing is written, and only this computer and the private network are asked.
/// </summary>
public static class ServerDetection
{
    /// <summary>Tried after the clients' own addresses: Strata's own port, then the ports of llama-swap and of the setups of this project.</summary>
    public static readonly string[] UsualAddresses =
    {
        "http://127.0.0.1:8095", "http://127.0.0.1:18080", "http://127.0.0.1:8080", "http://127.0.0.1:9000",
    };

    /// <summary>
    /// Every server that answers, as a settings entry. A router in front of an engine is the server to watch (it knows the model name and the
    /// loading state), so engines are only listed when no router answers.
    /// </summary>
    public static async Task<List<EngineConfig>> FindAsync(IJsonSource source, ClientConfigReader? clients, CancellationToken cancellation)
    {
        var addresses = Candidates(clients);
        var probes = await Task.WhenAll(addresses.Select(address => Probe(source, address, cancellation))).ConfigureAwait(false);
        var found = probes.Where(p => p != null).Select(p => p!).ToList();
        return found.Any(s => s.Router != null) ? found.Where(s => s.Router != null).ToList() : found;
    }

    /// <summary>The addresses to ask, once each: what the clients use first, then the usual ones. Only local and private-network addresses.</summary>
    internal static List<string> Candidates(ClientConfigReader? clients)
    {
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fromClients = clients?.Limits().Select(l => l.BaseUrl) ?? Enumerable.Empty<string>();
        foreach (var raw in fromClients.Concat(UsualAddresses))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !IsLocalNetwork(uri)) continue;
            var origin = uri.GetLeftPart(UriPartial.Authority);      // "http://127.0.0.1:18080/v1" is the server at "http://127.0.0.1:18080"
            if (seen.Add(origin)) addresses.Add(origin);
        }
        return addresses;
    }

    /// <summary>This computer (localhost, 127.x, ::1) or a private address: 10.x, 172.16 to 31, 192.168.x, and 100.64 to 127 (Tailscale).</summary>
    internal static bool IsLocalNetwork(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    private static async Task<EngineConfig?> Probe(IJsonSource source, string address, CancellationToken cancellation)
    {
        var running = await source.GetAsync(Endpoints.Join(address, "running"), Json.RunningType, cancellation).ConfigureAwait(false);
        if (running.Ok) return new EngineConfig { Router = address };
        var metrics = await source.GetAsync(Endpoints.Join(address, "metrics"), Json.MetricsType, cancellation).ConfigureAwait(false);
        return metrics.Ok && metrics.Body!.Engine is { } engine && !engine.Equals(new EngineDto()) ? new EngineConfig { Url = address } : null;
    }
}
