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
    /// Extracts the embedded PsBash module to a temp directory and returns the path to PsBash.psd1.
    /// Uses a version-stamped directory so concurrent processes don't conflict.
    /// </summary>
    public static string ExtractEmbedded()
    {
        var asm = typeof(ModuleExtractor).Assembly;
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", $"module-{version}");
        var marker = Path.Combine(dir, ".extracted");

        // Invalidate cache if the assembly has been rebuilt since extraction.
        var asmPath = asm.Location;
        if (File.Exists(marker) && !string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
        {
            var asmTime = File.GetLastWriteTimeUtc(asmPath);
            var markerTime = File.GetLastWriteTimeUtc(marker);
            if (asmTime > markerTime)
                File.Delete(marker);
        }

        // Skip if already extracted for this version
        if (File.Exists(marker))
            return Path.Combine(dir, "PsBash.psd1");

        Directory.CreateDirectory(dir);
        foreach (var file in ModuleFiles)
        {
            var destPath = Path.Combine(dir, file);
            using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
            using var dest = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(dest);
        }

        // Write marker after all files extracted successfully
        File.WriteAllText(marker, version);
        return Path.Combine(dir, "PsBash.psd1");
    }
}
