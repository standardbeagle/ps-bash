using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsBash.Shell;

/// <summary>
/// Provides command flag specifications for tab completion.
/// Data is embedded as a JSON resource and loaded at startup.
/// </summary>
public static class FlagSpecs
{
    private static readonly Dictionary<string, FlagSpec[]> Data = Load();

    /// <summary>
    /// Gets flag specifications for a command.
    /// </summary>
    /// <param name="command">The command name (e.g., "ls", "grep").</param>
    /// <returns>Array of flag specs, or null if command not found.</returns>
    public static IReadOnlyList<FlagSpec>? GetFlags(string command) =>
        Data.TryGetValue(command, out var specs) ? specs : null;

    /// <summary>
    /// Gets all command names that have flag specifications.
    /// </summary>
    public static IReadOnlyCollection<string> Commands => Data.Keys;

    private static Dictionary<string, FlagSpec[]> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "PsBash.Shell.Resources.FlagSpecs.json";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Resource not found - return empty dict rather than failing
            return new Dictionary<string, FlagSpec[]>(StringComparer.Ordinal);
        }

        var json = new StreamReader(stream).ReadToEnd();
        var node = JsonNode.Parse(json);
        if (node is not JsonObject jsonObject)
            return new Dictionary<string, FlagSpec[]>(StringComparer.Ordinal);

        var result = new Dictionary<string, FlagSpec[]>(StringComparer.Ordinal);
        foreach (var property in jsonObject)
        {
            if (property.Value is not JsonArray array)
                continue;

            var specs = new List<FlagSpec>();
            foreach (var item in array)
            {
                if (item is not JsonObject obj ||
                    obj["flag"] is not JsonValue flagValue ||
                    obj["desc"] is not JsonValue descValue)
                    continue;

                specs.Add(new FlagSpec(
                    flagValue.GetValue<string>(),
                    descValue.GetValue<string>()
                ));
            }

            result[property.Key!] = specs.ToArray();
        }

        return result;
    }
}

/// <summary>
/// Describes a single command flag with its description.
/// </summary>
/// <param name="Flag">The flag name (e.g., "-a", "--all").</param>
/// <param name="Desc">Human-readable description of what the flag does.</param>
public sealed record FlagSpec(string Flag, string Desc);
