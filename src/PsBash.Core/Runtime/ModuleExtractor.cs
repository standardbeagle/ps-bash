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
    /// Idempotent — safe to call on every run.
    /// </summary>
    public static string ExtractEmbedded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "module");
        Directory.CreateDirectory(dir);
        var asm = typeof(ModuleExtractor).Assembly;
        foreach (var file in ModuleFiles)
        {
            using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
            using var dest = new FileStream(
                Path.Combine(dir, file), FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(dest);
        }
        return Path.Combine(dir, "PsBash.psd1");
    }
}
