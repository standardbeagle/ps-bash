using System.Reflection;

namespace PsBash.Host.Runtime;

/// <summary>
/// Extracts the embedded SdkRunspaceSetup.ps1 setup script next to the extracted
/// PsBash module files so SdkRunspace.Create can dot-source it instead of
/// embedding the script body as a C# string.
///
/// Lives alongside ModuleExtractor's output (ps-bash/module-{version}/). Cache
/// invalidation is by assembly LastWriteTimeUtc — when the host assembly is
/// newer than the extracted file, re-extract. This mirrors the temp-file
/// convention in .claude/rules/temp-files.md.
/// </summary>
internal static class RunspaceSetupExtractor
{
    internal const string ResourceName = "PsBash.Host/SdkRunspaceSetup.ps1";
    internal const string FileName = "SdkRunspaceSetup.ps1";

    /// <summary>
    /// Extract SdkRunspaceSetup.ps1 into <paramref name="moduleDir"/> and
    /// return the absolute path to it. Idempotent across processes.
    /// </summary>
    public static string Extract(string moduleDir)
    {
        Directory.CreateDirectory(moduleDir);
        var destPath = Path.Combine(moduleDir, FileName);

        var asm = typeof(RunspaceSetupExtractor).Assembly;
        var asmTimestamp = File.Exists(asm.Location)
            ? File.GetLastWriteTimeUtc(asm.Location)
            : DateTime.UtcNow;

        // Skip if already extracted and not stale (assembly is older than dest).
        if (File.Exists(destPath))
        {
            var destTimestamp = File.GetLastWriteTimeUtc(destPath);
            if (destTimestamp >= asmTimestamp)
                return destPath;
        }

        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {ResourceName}. " +
                "Check PsBash.Host.csproj <EmbeddedResource> registration.");

        // FileShare.ReadWrite so parallel processes don't lock each other out
        // (see .claude/rules/temp-files.md "Concurrency").
        try
        {
            using var dest = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.CopyTo(dest);
        }
        catch (IOException)
        {
            // Another process won the write race; assume the file they wrote is
            // a valid copy of the same embedded resource for this assembly.
        }

        return destPath;
    }

    /// <summary>
    /// Returns the embedded SdkRunspaceSetup.ps1 content as a string. Used by
    /// the syntax-check unit test so it can run the PowerShell parser against
    /// the resource directly without round-tripping through disk.
    /// </summary>
    internal static string ReadEmbedded()
    {
        var asm = typeof(RunspaceSetupExtractor).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {ResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
