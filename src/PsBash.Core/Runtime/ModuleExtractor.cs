using System.Security.Cryptography;

namespace PsBash.Core.Runtime;

public static class ModuleExtractor
{
    private static readonly string[] ModuleFiles =
    [
        "PsBash.psd1",
        "PsBash.psm1",
        "PsBash.Format.ps1xml",
    ];

    /// <summary>
    /// File name of the binary cmdlets module extracted alongside the psm1.
    /// </summary>
    public const string CmdletsDllFileName = "PsBash.Cmdlets.dll";

    /// <summary>
    /// Logical resource-name prefix for the embedded per-TFM Cmdlets DLLs.
    /// The full name is <c>{prefix}{tfm}/PsBash.Cmdlets.dll</c> — e.g.
    /// <c>PsBash.Module/cmdlets/net10.0/PsBash.Cmdlets.dll</c>.
    /// </summary>
    private const string CmdletsResourcePrefix = "PsBash.Module/cmdlets/";

    /// <summary>
    /// Returns the absolute path to the extracted TFM-matching PsBash.Cmdlets.dll
    /// for the module version embedded in this assembly. Does NOT trigger
    /// extraction — call <see cref="ExtractEmbedded"/> first. The path is
    /// deterministic so callers can hand it to PowerShell as a known import
    /// target with no probing.
    /// </summary>
    public static string GetCmdletsDllPath()
    {
        var asm = typeof(ModuleExtractor).Assembly;
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", $"module-{version}");
        return Path.Combine(dir, CmdletsDllFileName);
    }

    /// <summary>
    /// Resolves the manifest-resource name of the Cmdlets DLL that matches the
    /// running framework. Prefers an exact TFM match (e.g. net10.0), falling
    /// back to the highest available embedded variant when the exact moniker
    /// is not embedded. Returns null when no Cmdlets DLL is embedded at all.
    /// </summary>
    private static string? ResolveCmdletsResourceName(System.Reflection.Assembly asm)
    {
        var cmdletsResources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(CmdletsResourcePrefix, StringComparison.Ordinal) &&
                        n.EndsWith("/" + CmdletsDllFileName, StringComparison.Ordinal))
            .ToArray();

        if (cmdletsResources.Length == 0)
            return null;

        var runningTfm = GetRunningTfm();
        var exact = cmdletsResources.FirstOrDefault(n =>
            string.Equals(TfmOf(n), runningTfm, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // No exact match: pick the highest netN.0 variant available.
        return cmdletsResources
            .OrderByDescending(n => ParseNetMajor(TfmOf(n)))
            .First();
    }

    /// <summary>
    /// Every embedded resource in the matched-TFM cmdlets folder as
    /// (resourceName, fileName) pairs — the PsBash.Cmdlets.dll plus any embedded
    /// dependency assemblies (Strata.* + Spectre.Console in UseStrata builds).
    /// Empty when no cmdlets DLL is embedded.
    /// </summary>
    private static IEnumerable<(string Resource, string FileName)> EnumerateCmdletResources(System.Reflection.Assembly asm)
    {
        var cmdletsResource = ResolveCmdletsResourceName(asm);
        if (cmdletsResource is null)
            yield break;

        var prefix = CmdletsResourcePrefix + TfmOf(cmdletsResource) + "/";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                yield return (name, name.Substring(prefix.Length));
        }
    }

    /// <summary>Extracts the TFM segment from a Cmdlets resource name.</summary>
    private static string TfmOf(string resourceName)
    {
        // "PsBash.Module/cmdlets/net10.0/PsBash.Cmdlets.dll" -> "net10.0"
        var inner = resourceName.Substring(CmdletsResourcePrefix.Length);
        var slash = inner.IndexOf('/');
        return slash < 0 ? inner : inner.Substring(0, slash);
    }

    /// <summary>Parses the major version out of a "netN.0" moniker; 0 if unrecognized.</summary>
    private static int ParseNetMajor(string tfm)
    {
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return 0;
        var rest = tfm.Substring(3);
        var dot = rest.IndexOf('.');
        if (dot > 0)
            rest = rest.Substring(0, dot);
        return int.TryParse(rest, out var major) ? major : 0;
    }

    /// <summary>
    /// Detects the running framework moniker (e.g. "net10.0", "net8.0") from
    /// <see cref="Environment.Version"/>. ps-bash only ships netN.0 TFMs, so
    /// the moniker is always "net{Major}.0".
    /// </summary>
    private static string GetRunningTfm()
    {
        // Environment.Version on .NET 5+ reports the runtime version
        // (e.g. 10.0.0). RuntimeInformation.FrameworkDescription would also
        // work but needs string parsing; Environment.Version is structured.
        return $"net{Environment.Version.Major}.0";
    }

    /// <summary>
    /// Extracts the embedded PsBash module to a temp directory and returns the path to PsBash.psd1.
    /// Also extracts the TFM-matching PsBash.Cmdlets.dll alongside it (see
    /// <see cref="GetCmdletsDllPath"/>). Uses a version-stamped directory so
    /// concurrent processes don't conflict.
    /// Thread-safe: uses a lock file to serialize extraction across processes.
    /// </summary>
    public static string ExtractEmbedded()
    {
        var asm = typeof(ModuleExtractor).Assembly;
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", $"module-{version}");
        var marker = Path.Combine(dir, ".extracted");
        var psd1Path = Path.Combine(dir, "PsBash.psd1");

        // Invalidate cache if embedded resource content has changed.
        if (File.Exists(marker))
        {
            var storedHash = File.ReadAllText(marker).Trim();
            var currentHash = ComputeEmbeddedHash(asm);
            if (storedHash != currentHash)
            {
                try { File.Delete(marker); }
                catch (IOException) { /* another process may be extracting */ }
            }
        }

        // Skip if already extracted for this version
        if (File.Exists(marker))
            return psd1Path;

        Directory.CreateDirectory(dir);

        // Use a lock file to serialize extraction across concurrent processes.
        var lockPath = Path.Combine(dir, ".lock");
        try
        {
            using var lockFile = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose);

            // Re-check marker after acquiring lock — another process may have finished.
            if (File.Exists(marker))
                return psd1Path;

            foreach (var file in ModuleFiles)
            {
                var destPath = Path.Combine(dir, file);
                using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
                using var dest = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.CopyTo(dest);
            }

            // Extract the TFM-matching PsBash.Cmdlets.dll AND any embedded
            // dependency assemblies in the same cmdlets folder (e.g. Strata.* +
            // Spectre.Console, present only in UseStrata builds) alongside the
            // psm1. The host imports PsBash.Cmdlets.dll from this deterministic
            // path (see GetCmdletsDllPath); PowerShell's path-based Import-Module
            // (LoadFrom) then resolves the cmdlet's own dependencies from beside
            // it. Non-Strata builds embed only PsBash.Cmdlets.dll.
            foreach (var (resName, fileName) in EnumerateCmdletResources(asm))
            {
                var destPath = Path.Combine(dir, fileName);
                using var depStream = asm.GetManifestResourceStream(resName)!;
                using var depDest = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                depStream.CopyTo(depDest);
            }

            // Write content hash as marker after all files extracted successfully
            var hash = ComputeEmbeddedHash(asm);
            File.WriteAllText(marker, hash);
        }
        catch (IOException)
        {
            // Another process holds the lock and is extracting. Wait for marker.
            WaitForMarker(marker);
        }

        return psd1Path;
    }

    /// <summary>
    /// Computes a SHA256 hash over all embedded module resources, including the
    /// TFM-matching Cmdlets DLL. Including the DLL means a rebuilt cmdlets
    /// assembly invalidates the extracted-module cache.
    /// </summary>
    private static string ComputeEmbeddedHash(System.Reflection.Assembly asm)
    {
        using var sha = SHA256.Create();
        using var combined = new MemoryStream();
        foreach (var file in ModuleFiles)
        {
            using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
            stream.CopyTo(combined);
        }

        // Hash every embedded cmdlet-folder resource (the DLL + any deps) so a
        // rebuilt cmdlets assembly OR a changed dependency invalidates the cache.
        foreach (var (resName, _) in EnumerateCmdletResources(asm))
        {
            using var depStream = asm.GetManifestResourceStream(resName)!;
            depStream.CopyTo(combined);
        }

        combined.Position = 0;
        var bytes = sha.ComputeHash(combined);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Wait for another process to finish extraction (up to 10 seconds).
    /// </summary>
    private static void WaitForMarker(string marker)
    {
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(marker))
                return;
            Thread.Sleep(100);
        }
        // If marker never appears, proceed anyway — the files may be partially extracted
        // but pwsh will fail with a clear error rather than a mysterious lock exception.
    }
}
