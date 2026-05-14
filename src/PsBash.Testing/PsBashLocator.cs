namespace PsBash.Testing;

/// <summary>
/// Build configuration the test assembly was compiled in. Used to prefer the
/// matching ps-bash launcher build.
/// </summary>
public enum BuildConfig
{
    /// <summary>Auto-detect from the test assembly's output path; fall back to Debug.</summary>
    Auto = 0,
    Debug = 1,
    Release = 2,
}

/// <summary>
/// Resolves the ps-bash launcher binary built by the PsBash.Shell project.
///
/// Unified replacement (REFACTOR-3) for the three near-identical copies that
/// previously lived as <c>ProcessRunHelper.ResolveLauncherPath</c>,
/// <c>ModeRunner.FindPsBash</c>, and <c>BashOracleFixture.FindPsBash</c> — each
/// of which walked up from the test bin dir and probed Debug/Release with
/// subtly different ordering.
///
/// Probe strategy:
///   1. Determine the preferred configuration (explicit, or auto-detected from
///      the test assembly's own output path — CI builds Release only).
///   2. Walk up from PsBash.Shell/bin/&lt;config&gt;/ over every TFM directory,
///      preferred config first, then the alternate, so a dev box with only one
///      configuration built still resolves without a rebuild.
///   3. On non-Windows prefer the ELF binary (<c>ps-bash</c>) over the PE
///      <c>ps-bash.exe</c> so Process.Start can exec it directly.
/// </summary>
public static class PsBashLocator
{
    private static readonly string ShellProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell"));

    /// <summary>
    /// Returns the resolved launcher path, or <c>null</c> when no build output
    /// is found (binary not built yet). Callers that require the binary should
    /// use <see cref="ResolveRequired"/>.
    /// </summary>
    /// <param name="config">
    /// Preferred build configuration. <see cref="BuildConfig.Auto"/> detects it
    /// from the running test assembly's output path.
    /// </param>
    public static string? Resolve(BuildConfig config = BuildConfig.Auto)
    {
        var preferred = config switch
        {
            BuildConfig.Debug => "Debug",
            BuildConfig.Release => "Release",
            _ => DetectConfiguration(),
        };
        var binName = OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash";

        foreach (var cfg in new[] { preferred, preferred == "Release" ? "Debug" : "Release" })
        {
            var configDir = Path.Combine(ShellProjectDir, "bin", cfg);
            if (!Directory.Exists(configDir)) continue;
            foreach (var tfmDir in Directory.EnumerateDirectories(configDir))
            {
                var candidate = Path.Combine(tfmDir, binName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="Resolve"/> but throws when the launcher is not found —
    /// for suites (e.g. Escalation) where a missing binary is a hard failure,
    /// not a skip.
    /// </summary>
    public static string ResolveRequired(BuildConfig config = BuildConfig.Auto)
        => Resolve(config)
           ?? throw new InvalidOperationException(
               $"ps-bash launcher not found under {Path.Combine(ShellProjectDir, "bin")}. " +
               "Build PsBash.Shell before running this test suite.");

    /// <summary>
    /// Detects the build configuration from the test assembly's output path.
    /// CI builds Release only; a dev box may have built either or both.
    /// </summary>
    private static string DetectConfiguration()
        => AppContext.BaseDirectory
               .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .FirstOrDefault(p => p.Equals("Release", StringComparison.OrdinalIgnoreCase)
                                 || p.Equals("Debug", StringComparison.OrdinalIgnoreCase))
           ?? "Debug";
}
