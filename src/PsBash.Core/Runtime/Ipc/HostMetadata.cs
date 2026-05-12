using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Sidecar JSON record written beside the canonical endpoint when a host
/// starts. Lets a launcher distinguish a stale endpoint (no live process) from
/// an obsolete-but-running host (process alive but wrong protocol/build), and
/// verify ownership before any invasive cleanup. Per
/// <c>docs/specs/host-lifecycle-contract.md</c>.
/// </summary>
/// <remarks>
/// Owner identity is stored as a plain user name in this revision; SID/uid
/// support is a known gap deferred to a follow-on task. The launcher still
/// matches against <see cref="Environment.UserName"/>, which is sufficient
/// for the current single-user-per-machine assumption.
/// </remarks>
public sealed record HostMetadata(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("executablePath")] string ExecutablePath,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("buildIdentity")] string BuildIdentity,
    [property: JsonPropertyName("transportScheme")] string TransportScheme,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("owner")] string Owner)
{
    /// <summary>
    /// Path of the metadata sidecar derived from a resolved endpoint. For
    /// <c>unix</c>, the sidecar lives next to the socket so a single ownership
    /// check covers both. For <c>pipe</c>, Windows named pipes are kernel
    /// namespace objects with no filesystem location, so the sidecar is parked
    /// under <c>%TEMP%/ps-bash/&lt;pipe-name&gt;.host.json</c>.
    /// </summary>
    public static string PathFor(string scheme, string endpoint)
    {
        if (scheme == "unix") return endpoint + ".host.json";
        if (scheme == "pipe")
        {
            var dir = Path.Combine(Path.GetTempPath(), "ps-bash");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, endpoint + ".host.json");
        }
        throw new ArgumentException($"Unknown scheme '{scheme}' (expected unix or pipe).", nameof(scheme));
    }

    /// <summary>
    /// Atomically write the sidecar via temp-file + replace so a reader
    /// can never observe a torn JSON document. Caller owns the deletion of
    /// the file on graceful shutdown via <see cref="Remove"/>.
    /// </summary>
    public void Write(string scheme, string endpoint)
    {
        var path = PathFor(scheme, endpoint);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, HostMetadataJsonContext.Default.HostMetadata));
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    /// <summary>
    /// Read and parse the sidecar. Returns <c>null</c> when the file is
    /// absent OR malformed — both states are "no trustworthy ownership
    /// information," and callers must fall through to the no-metadata
    /// policy (treat endpoint as stale-or-orphaned). Returning null instead
    /// of throwing keeps the launcher's startup path simple.
    /// </summary>
    public static HostMetadata? TryRead(string scheme, string endpoint)
    {
        var path = PathFor(scheme, endpoint);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, HostMetadataJsonContext.Default.HostMetadata);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Best-effort removal of the sidecar. Used by hosts on graceful exit
    /// and by launchers cleaning a verified-stale endpoint.
    /// </summary>
    public static void Remove(string scheme, string endpoint)
    {
        try
        {
            var path = PathFor(scheme, endpoint);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* sidecar leak is recoverable; next launcher overwrites */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(HostMetadataJsonContext.Default.Options)
    {
        WriteIndented = true,
    };
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="HostMetadata"/>.
/// Required because <c>PsBash.Shell</c> is published with <c>PublishAot=true</c>,
/// which sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>. Without a
/// source-generated context, <c>JsonSerializer.Deserialize&lt;HostMetadata&gt;</c>
/// throws <c>InvalidOperationException: Reflection-based serialization has been disabled</c>
/// at every host-metadata read — which crashes <c>ps-bash.exe</c> on first run
/// and breaks every differential test that spawns the launcher.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(HostMetadata))]
internal partial class HostMetadataJsonContext : JsonSerializerContext
{
}
