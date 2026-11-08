using System.Net;
using System.Text.Json.Serialization.Metadata;

namespace Stratoscope.Core;

public enum FetchFailure
{
    None,
    /// <summary>Nothing listens at that address.</summary>
    Refused,
    /// <summary>No answer in time.</summary>
    Timeout,
    /// <summary>The host name is unknown.</summary>
    NameNotResolved,
    /// <summary>The server answered with a status other than 2xx.</summary>
    Status,
    /// <summary>The answer is not a JSON document of the expected shape.</summary>
    NotJson,
    Other,
}

/// <summary>The outcome of one GET request.</summary>
public sealed class JsonResponse<T> where T : class
{
    public int Status { get; init; }
    public T? Body { get; init; }
    public FetchFailure Failure { get; init; }
    public string? Detail { get; init; }
    public bool Ok => Failure == FetchFailure.None && Body != null;
}

/// <summary>
/// How an adapter reads JSON from its engine. The only operation is GET: Stratoscope never changes anything on the engine.
/// </summary>
public interface IJsonSource
{
    Task<JsonResponse<T>> GetAsync<T>(Uri url, JsonTypeInfo<T> type, CancellationToken cancellation) where T : class;
}

/// <summary>Builds the addresses of an engine's endpoints from its base address, with the framework's <see cref="Uri"/>: segments are escaped, never concatenated.</summary>
public static class Endpoints
{
    /// <summary>The base address with every segment appended: <c>Join("http://r:1/x", "upstream", "a b", "metrics")</c> is <c>http://r:1/x/upstream/a%20b/metrics</c>.</summary>
    public static Uri Join(string baseAddress, params string[] segments)
    {
        var root = new Uri(baseAddress.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(root, string.Join("/", segments.Select(Uri.EscapeDataString)));
    }
}

/// <summary>The real thing: HttpClient, a short timeout, no system proxy (engines run on this machine or behind an SSH forward).</summary>
public sealed class HttpJsonSource : IJsonSource, IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    public HttpJsonSource(TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(2.5);
        client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };
    }

    public async Task<JsonResponse<T>> GetAsync<T>(Uri url, JsonTypeInfo<T> type, CancellationToken cancellation) where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status < 200 || status > 299)
                return new JsonResponse<T> { Status = status, Failure = FetchFailure.Status, Detail = "HTTP " + status };
            var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            var body = Json.Deserialize(text, type);
            return body == null
                ? new JsonResponse<T> { Status = status, Failure = FetchFailure.NotJson, Detail = "not a JSON object" }
                : new JsonResponse<T> { Status = status, Body = body };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new JsonResponse<T> { Failure = FetchFailure.Timeout };
        }
        catch (HttpRequestException e)
        {
            return new JsonResponse<T> { Failure = Classify(e), Detail = e.InnerException?.Message ?? e.Message };
        }
    }

    private static FetchFailure Classify(HttpRequestException e) =>
        e.HttpRequestError switch
        {
            HttpRequestError.ConnectionError => FetchFailure.Refused,
            HttpRequestError.NameResolutionError => FetchFailure.NameNotResolved,
            _ => FetchFailure.Other,
        };

    public void Dispose() => client.Dispose();
}
