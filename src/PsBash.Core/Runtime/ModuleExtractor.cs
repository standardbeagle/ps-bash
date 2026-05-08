using System.Security.Cryptography;

namespace PsBash.Core.Runtime;

public static class ModuleExtractor
{
    private const string ModulePrefix = "PsBash.Module/";

    /// <summary>
    /// Extracts the embedded PsBash module to a temp directory and returns the path to PsBash.psd1.
    /// Uses a version-stamped directory so concurrent processes don't conflict.
    /// Thread-safe: uses a lock file to serialize extraction across processes.
    /// Auto-discovers all embedded resources under the PsBash.Module/ prefix so new
    /// submodule files only need a csproj EmbeddedResource entry — no code changes here.
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

            foreach (var resourceName in GetModuleResourceNames(asm))
            {
                var relativePath = resourceName.Substring(ModulePrefix.Length)
                    .Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.Combine(dir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using var stream = asm.GetManifestResourceStream(resourceName)!;
                using var dest = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.CopyTo(dest);
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

    private static IEnumerable<string> GetModuleResourceNames(System.Reflection.Assembly asm) =>
        asm.GetManifestResourceNames()
           .Where(n => n.StartsWith(ModulePrefix, StringComparison.Ordinal))
           .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Computes a SHA256 hash over all embedded module resources.
    /// </summary>
    private static string ComputeEmbeddedHash(System.Reflection.Assembly asm)
    {
        using var sha = SHA256.Create();
        using var combined = new MemoryStream();
        foreach (var resourceName in GetModuleResourceNames(asm))
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            stream.CopyTo(combined);
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
