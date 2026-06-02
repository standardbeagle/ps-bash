using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// JSON (de)serialization for <see cref="FilterSpec"/>. A filter file is either a single
/// spec object or an array of them. Uses a source-generated context: <c>PsBash.Shell</c>
/// publishes with <c>PublishAot=true</c> (reflection serialization disabled), so a
/// reflection-based parse would throw at every filter load. Property names are camelCase
/// and matched case-insensitively (so <c>onSuccess</c> / <c>OnSuccess</c> both bind).
/// </summary>
public static class FilterJson
{
    /// <summary>
    /// Parse a filter file body. Accepts a single object or a JSON array. Returns the
    /// specs found (empty for <c>null</c>/whitespace). Throws <see cref="JsonException"/>
    /// on malformed JSON — callers decide whether to skip the offending file.
    /// </summary>
    public static IReadOnlyList<FilterSpec> ParseFile(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        var firstToken = json.AsSpan().TrimStart();
        if (firstToken.Length > 0 && firstToken[0] == '[')
        {
            return JsonSerializer.Deserialize(json, FilterJsonContext.Default.FilterSpecArray) ?? [];
        }

        var single = JsonSerializer.Deserialize(json, FilterJsonContext.Default.FilterSpec);
        return single is null ? [] : [single];
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FilterSpec))]
[JsonSerializable(typeof(FilterSpec[]))]
internal partial class FilterJsonContext : JsonSerializerContext;
