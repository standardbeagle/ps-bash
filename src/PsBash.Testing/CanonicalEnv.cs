namespace PsBash.Testing;

/// <summary>
/// Builds the canonical, reproducible environment block for golden/oracle
/// process spawns (QA rubric Directive 6: "FIX TERM. FIX LANG. ISOLATE HOME").
///
/// Why this exists: golden files freeze ps-bash output for later byte-comparison.
/// If the spawned interpreter inherits the developer's (or the CI runner's)
/// shell environment, env-derived values — <c>$USER</c>, <c>$HOME</c>,
/// <c>$LANG</c>, locale-sensitive formatting — leak into the frozen output.
/// A golden recorded on a dev box then fails on a CI runner with a different
/// <c>$USER</c>. The fix is to spawn under a known, fixed whitelist so the
/// output is the same on every machine.
///
/// Two variants:
///   <see cref="ForBash"/>    — pure canonical whitelist, for the real-bash side.
///   <see cref="ForPsBash"/>  — canonical whitelist PLUS the .NET runtime
///                              discovery vars the framework-dependent ps-bash
///                              launcher/host needs to start at all.
///
/// Both are meant to be passed to <see cref="ProcessSpawn.RunAsync"/> with
/// <c>canonicalizeEnv: true</c>, which clears the inherited block before
/// applying these.
/// </summary>
public static class CanonicalEnv
{
    /// <summary>Fixed username so <c>$USER</c> is byte-stable across machines.</summary>
    public const string CanonicalUser = "psbash-test";

    /// <summary>Fixed locale so locale-sensitive formatting is byte-stable.</summary>
    public const string CanonicalLang = "C.UTF-8";

    /// <summary>Fixed terminal type (Directive 6).</summary>
    public const string CanonicalTerm = "xterm-256color";

    /// <summary>
    /// Minimal PATH for the bash side — the standard POSIX system directories.
    /// bash itself and the coreutils it shells out to live here.
    /// </summary>
    private static readonly string MinimalPosixPath =
        string.Join(System.IO.Path.PathSeparator, new[]
        {
            "/usr/local/sbin", "/usr/local/bin", "/usr/sbin",
            "/usr/bin", "/sbin", "/bin",
        });

    /// <summary>
    /// The pure canonical whitelist for the real-bash side of the oracle.
    /// <paramref name="homeDir"/> is isolated to a per-test temp directory so
    /// no real dotfiles or history leak in.
    /// </summary>
    public static Dictionary<string, string> ForBash(string homeDir)
    {
        if (string.IsNullOrEmpty(homeDir))
            throw new ArgumentException("homeDir must be a non-empty path.", nameof(homeDir));

        return new Dictionary<string, string>
        {
            ["PATH"] = MinimalPosixPath,
            ["HOME"] = homeDir,
            ["USER"] = CanonicalUser,
            ["LOGNAME"] = CanonicalUser,
            ["LANG"] = CanonicalLang,
            ["LC_ALL"] = CanonicalLang,
            ["TERM"] = CanonicalTerm,
            ["TMPDIR"] = homeDir,
            ["TEMP"] = homeDir,
            ["TMP"] = homeDir,
        };
    }

    /// <summary>
    /// The canonical whitelist for the ps-bash side. Starts from
    /// <see cref="ForBash"/> then overlays the variables the
    /// framework-dependent ps-bash launcher and its spawned host need to
    /// locate the .NET runtime — clearing the inherited block wholesale (per
    /// the task risk note) would otherwise leave the apphost unable to start.
    ///
    /// What ps-bash itself reads from the environment (audited via
    /// <c>grep PSBASH_</c> across Launcher/Host/Core): <c>PSBASH_HOST</c>,
    /// <c>PSBASH_HOST_DETACH</c>, <c>PSBASH_TIMEOUT</c>, <c>PSBASH_DEBUG</c>,
    /// <c>PSBASH_HOME</c>, <c>PSBASH_IPC_ENDPOINT</c>, etc. None of those are
    /// preserved here: the launcher sets <c>PSBASH_HOST_DETACH</c> on the host
    /// itself, derives its IPC endpoint internally, and resolves its host
    /// binary by path — so a cleared block plus this overlay is exactly the
    /// "clean PSI env + selective overlay" the risk note calls for. Callers
    /// that need <c>PSBASH_TIMEOUT</c> / <c>PSBASH_DEBUG</c> layer those on top
    /// via the spawn's <c>env</c> parameter.
    /// </summary>
    public static Dictionary<string, string> ForPsBash(string homeDir)
    {
        if (string.IsNullOrEmpty(homeDir))
            throw new ArgumentException("homeDir must be a non-empty path.", nameof(homeDir));

        // Strategy: preserve inherited env + override the leakage-surface vars.
        //
        // The original design cleared the inherited block and rebuilt from a
        // whitelist. That worked for the bash side (a leaf process with a
        // small env footprint) but failed for the ps-bash side: .NET +
        // PowerShell SDK needed ~30 Windows-specific vars (PATHEXT,
        // PSModulePath, ProgramFiles*, PROCESSOR_*, etc.) and the whitelist
        // kept growing as dev boxes and CI runners turned out to have
        // different env shapes. Diverging "CI vs local" passes was the cost
        // — see commits 85c9b5e and e38ac83 which kept enumerating more vars.
        //
        // The corrected design (this method): start from the inherited block,
        // then explicitly OVERRIDE the identity/locale vars that would
        // otherwise leak the host machine's identity into recorded goldens.
        // Every var the runtime needs flows through automatically; every var
        // that surfaces in test output (`echo $USER`, `printenv HOME`,
        // locale-sensitive `printf`) gets a canonical value. Plus we strip
        // CI-runner-identity vars (GITHUB_*, RUNNER_*) that wouldn't be
        // present locally so CI parity holds in both directions.
        //
        // ForBash (the real-bash leaf process) keeps the strict whitelist —
        // bash has no .NET runtime to satisfy and its env footprint is small.
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var k = kv.Key?.ToString();
            var v = kv.Value?.ToString();
            if (k != null && v != null) env[k] = v;
        }

        // Canonical overrides — values that show up in golden output.
        env["USER"] = CanonicalUser;
        env["LOGNAME"] = CanonicalUser;
        env["USERNAME"] = CanonicalUser; // Windows analogue.
        env["LANG"] = CanonicalLang;
        env["LC_ALL"] = CanonicalLang;
        env["TERM"] = CanonicalTerm;
        env["HOME"] = homeDir;
        env["USERPROFILE"] = homeDir; // Windows analogue — PS module discovery roots at the test home.
        env["TMPDIR"] = homeDir;
        env["TEMP"] = homeDir;
        env["TMP"] = homeDir;

        // Strip identity / CI vars that would otherwise differ between local
        // and CI, polluting golden output via `env` or `printenv` calls or
        // shifting test behavior conditionally on CI presence.
        foreach (var name in new[]
        {
            "COMPUTERNAME", "HOSTNAME", "HOMEPATH", "HOMEDRIVE",
            "GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_SHA", "GITHUB_REF",
            "GITHUB_WORKFLOW", "GITHUB_RUN_ID", "GITHUB_RUN_NUMBER", "GITHUB_ACTOR",
            "GITHUB_EVENT_NAME", "GITHUB_TOKEN", "GITHUB_WORKSPACE",
            "GITHUB_HEAD_REF", "GITHUB_BASE_REF", "GITHUB_PATH", "GITHUB_ENV",
            "RUNNER_OS", "RUNNER_TEMP", "RUNNER_WORKSPACE", "RUNNER_TOOL_CACHE",
            "RUNNER_ARCH", "RUNNER_NAME", "RUNNER_DEBUG",
            "CI", "CONTINUOUS_INTEGRATION", "AGENT_TOOLSDIRECTORY",
            // ps-bash test-infra vars that would leak per-test paths.
            "PSBASH_DEBUG", "PSBASH_IPC_ENDPOINT", "PSBASH_TEST_START_TIMEOUT_SEC",
            "PSBASH_HOST", "PSBASH_HOST_DETACH", "PSBASH_TRACE_STARTUP",
            "PSBASH_HISTORY_PATH", "PSBASH_HOME",
        })
        {
            env.Remove(name);
        }

        // Replace PATH with the minimal POSIX + system dirs the runtime needs.
        // The inherited PATH includes machine-specific directories (VS install,
        // runner tool caches, NuGet global tools) that would surface in
        // `echo $PATH` goldens and cause non-deterministic tool resolution
        // (e.g. picking up an unrelated `node` from the runner image).
        var minimalPath = MinimalPosixPath;
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            env["DOTNET_ROOT"] = dotnetRoot;
            minimalPath = dotnetRoot + System.IO.Path.PathSeparator + minimalPath;
        }
        if (OperatingSystem.IsWindows())
        {
            // System32 is required for kernel32.dll / Win32 API surface.
            var systemRoot = env.TryGetValue("SystemRoot", out var sr) ? sr : "C:\\Windows";
            var winPath = string.Join(System.IO.Path.PathSeparator, new[]
            {
                System.IO.Path.Combine(systemRoot, "System32"),
                systemRoot,
                System.IO.Path.Combine(systemRoot, "System32", "Wbem"),
                System.IO.Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"),
            });
            minimalPath = winPath + System.IO.Path.PathSeparator + minimalPath;
        }
        else
        {
            // No DOTNET_ROOT — try to find dotnet on the inherited PATH and
            // prepend its directory so the canonical PATH still resolves it.
            var dotnetExe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in inheritedPath.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (File.Exists(System.IO.Path.Combine(dir, dotnetExe)))
                {
                    minimalPath = dir + System.IO.Path.PathSeparator + minimalPath;
                    break;
                }
            }
        }
        env["PATH"] = minimalPath;

        return env;
    }
}
