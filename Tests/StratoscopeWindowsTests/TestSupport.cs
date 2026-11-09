using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Stratoscope.Tests;

/// <summary>What a correct implementation derives from a payload (the *.expected.json files). A null value means "unknown".</summary>
internal sealed record ExpectedFile
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("queued")] public int? Queued { get; init; }
    [JsonPropertyName("percent")] public int? Percent { get; init; }
    [JsonPropertyName("context_used")] public double? ContextUsed { get; init; }
    [JsonPropertyName("context_max")] public double? ContextMax { get; init; }
    [JsonPropertyName("recent")] public int? Recent { get; init; }
    [JsonPropertyName("last_request_hit")] public string? LastRequestHit { get; init; }
    [JsonPropertyName("running_cache_hit")] public string? RunningCacheHit { get; init; }
    [JsonPropertyName("cache_slots")] public int? CacheSlots { get; init; }
    [JsonPropertyName("cache_parked")] public int? CacheParked { get; init; }
    [JsonPropertyName("batch_slots")] public int? BatchSlots { get; init; }
    [JsonPropertyName("has_system")] public bool HasSystem { get; init; }
    [JsonPropertyName("has_totals")] public bool HasTotals { get; init; }
}

internal static class Fixtures
{
    private static readonly JsonSerializerOptions ExpectedOptions = new() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string Text(string name) => File.ReadAllText(Path.Combine(Dir, name), System.Text.Encoding.UTF8);

    /// <summary>A recorded /metrics payload as the app reads it.</summary>
    public static StrataMetrics Metrics(string name) => Json.Deserialize(Text(name), Json.MetricsType) ?? throw new InvalidDataException(name);

    public static ExpectedFile Expected(string name) => JsonSerializer.Deserialize<ExpectedFile>(Text(name.Replace(".json", ".expected.json")), ExpectedOptions)!;

    public static IEnumerable<string> Payloads() =>
        Directory.GetFiles(Dir, "*.expected.json").Select(p => Path.GetFileName(p).Replace(".expected.json", ".json"));
}

/// <summary>One scripted answer of a fake engine.</summary>
internal readonly record struct FakeAnswer(int Status, string? Body, FetchFailure Failure = FetchFailure.None);

/// <summary>Answers GET requests from a table, so the provider logic runs without a network. Bodies are real JSON text, read by the real parser.</summary>
internal sealed class FakeSource : IJsonSource
{
    private readonly Dictionary<string, Func<FakeAnswer>> routes = new();
    public List<string> Requested { get; } = new();

    public FakeSource Route(string url, Func<FakeAnswer> answer) { routes[url] = answer; return this; }
    public FakeSource Json(string url, string fixture) => Route(url, () => new FakeAnswer(200, Fixtures.Text(fixture)));
    public FakeSource Text(string url, string json) => Route(url, () => new FakeAnswer(200, json));
    public FakeSource Status(string url, int status) => Route(url, () => new FakeAnswer(status, null, FetchFailure.Status));
    public FakeSource Down(string url) => Route(url, () => new FakeAnswer(0, null, FetchFailure.Refused));

    public Task<JsonResponse<T>> GetAsync<T>(Uri url, JsonTypeInfo<T> type, CancellationToken cancellation) where T : class
    {
        Requested.Add(url.AbsoluteUri);
        var answer = routes.TryGetValue(url.AbsoluteUri, out var make) ? make() : new FakeAnswer(0, null, FetchFailure.Refused);
        if (answer.Failure != FetchFailure.None)
            return Task.FromResult(new JsonResponse<T> { Status = answer.Status, Failure = answer.Failure, Detail = answer.Status > 0 ? "HTTP " + answer.Status : null });
        var body = Stratoscope.Core.Json.Deserialize(answer.Body, type);
        return Task.FromResult(body == null
            ? new JsonResponse<T> { Status = answer.Status, Failure = FetchFailure.NotJson, Detail = "not a JSON object" }
            : new JsonResponse<T> { Status = answer.Status, Body = body });
    }
}
