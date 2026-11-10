
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Stratoscope.Core.Engines;
using Stratoscope.Core.Engines.Basalt;

namespace Stratoscope.Core;

/// <summary>
/// JSON in, typed records out, with System.Text.Json (part of .NET, no package) and its source generator, so there is no reflection and
/// the code is trim- and Native-AOT-safe. The repo rule "a missing, null or wrong-typed value is unknown, never 0" is enforced here once,
/// so the models (Dto.cs) are plain records with nullable properties:
/// <list type="bullet">
/// <item>a missing key or a JSON null leaves the property null;</item>
/// <item>a value of the wrong type (an object where a string was expected, "fast" where a number was, a number too large for a double, a
/// number where a section was expected) is skipped, the property stays null and the rest of the document is still read;</item>
/// <item>a number sent as text is not a number, like on the Mac;</item>
/// <item>text that is not JSON, truncated JSON, an array where an object is expected, or nesting deeper than 64 levels gives null.</item>
/// </list>
/// System.Text.Json is strict by design and has no "ignore type mismatches" switch, so the tolerance is the small converters below.
/// Comments and trailing commas (for the settings file) and the byte order mark are handled by the library's own options and reader.
/// </summary>
public static class Json
{
    /// <summary>Parses <paramref name="text"/>, or returns null when it is not a JSON object of that shape.</summary>
    public static T? Deserialize<T>(string? text, JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize(text.TrimStart('﻿'), type); }
        catch (JsonException) { return null; }
    }

    public static JsonTypeInfo<RunningResponse> RunningType => StratoscopeJson.Default.RunningResponse;
    public static JsonTypeInfo<StrataMetrics> MetricsType => StratoscopeJson.Default.StrataMetrics;
    public static JsonTypeInfo<BasaltMetrics> BasaltMetricsType => StratoscopeJson.Default.BasaltMetrics;
    /// <summary>Basalt's <c>/slots</c> is a top-level array.</summary>
    public static JsonTypeInfo<List<BasaltSlotDto?>> BasaltSlotsType => StratoscopeJson.Default.ListBasaltSlotDto;
    public static JsonTypeInfo<EngineHealth> HealthType => StratoscopeJson.Default.EngineHealth;
}

/// <summary>The generated serializer metadata for every document the app reads.</summary>
[JsonSourceGenerationOptions(
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    MaxDepth = 64,
    Converters = [typeof(TolerantNumberConverter), typeof(TolerantStringConverter)])]
[JsonSerializable(typeof(RunningResponse))]
[JsonSerializable(typeof(StrataMetrics))]
[JsonSerializable(typeof(BasaltMetrics))]
[JsonSerializable(typeof(List<BasaltSlotDto?>))]
[JsonSerializable(typeof(EngineHealth))]
internal sealed partial class StratoscopeJson : JsonSerializerContext;

/// <summary>A number into a <c>double?</c>. Text, booleans, objects, arrays and numbers a double cannot hold give null.</summary>
internal sealed class TolerantNumberConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options) => throw new NotSupportedException();
}

/// <summary>A string. A number or a boolean is read as its text (200 and "200" mean the same in the settings); objects and arrays give null.</summary>
internal sealed class TolerantStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : reader.TryGetDouble(out var real) ? real.ToString(CultureInfo.InvariantCulture) : null;
            case JsonTokenType.True: return "true";
            case JsonTokenType.False: return "false";
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default: return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => throw new NotSupportedException();
}

/// <summary>A section of a document: an object, or unknown (null) when the engine sent something else in its place.</summary>
internal sealed class TolerantObject<T> : JsonConverter<T> where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            if (reader.TokenType == JsonTokenType.StartArray) reader.Skip();
            return null;
        }
        return (T?)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T)));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => throw new NotSupportedException();
}

/// <summary>A list of sections. Elements that are not objects are null; anything but an array is unknown (null).</summary>
internal sealed class TolerantList<T> : JsonConverter<List<T?>> where T : class
{
    public override List<T?>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject) reader.Skip();
            return null;
        }
        var list = new List<T?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject) list.Add((T?)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T))));
            else { if (reader.TokenType == JsonTokenType.StartArray) reader.Skip(); list.Add(null); }
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<T?> value, JsonSerializerOptions options) => throw new NotSupportedException();
}
