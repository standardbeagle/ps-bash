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
        var env = ForBash(homeDir);

        // Force the per-invocation host lifetime for the whole test suite. In
        // production `-c` defaults to the shared Daemon host (reused across
        // launchers for speed), but a persisted daemon outlives each test and
        // would accumulate orphan hosts on isolated per-test endpoints, adding
        // CPU contention and non-determinism. PSBASH_PER_INVOCATION=1 gives each
        // spawned ps-bash its own private host, killed on dispose — deterministic
        // cleanup and the isolation the oracle/differential suites already assume.
        env["PSBASH_PER_INVOCATION"] = "1";

        // The ps-bash launcher is a framework-dependent apphost: it must find
        // the shared .NET runtime. DOTNET_ROOT (and the dotnet dir on PATH) is
        // how a non-default install location is discovered. Preserve whatever
        // the test host is using so the launcher starts under the canonical
        // block too.
        // Windows: PowerShell / .NET runtime requires a handful of system
        // environment variables that are normally guaranteed by the OS but
        // get stripped by canonicalizeEnv. Without them, the host process
        // (ps-bash-host, which boots a PowerShell runspace) fails to
        // initialize core assemblies and never reaches the accept-connection
        // state — observed as a 10s HostUnavailableException on Windows CI.
        // Preserve the inherited values; they are machine-stable enough
        // (e.g. C:\Windows) not to leak meaningful state into goldens.
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[]
            {
                // OS-supplied identity / install location vars — required for
                // kernel32.dll / mscoree loader paths, %TEMP%-style expansion,
                // and basic Win32 API surface.
                "SystemRoot", "windir", "SystemDrive", "ComSpec", "OS",
                "PATHEXT", "NUMBER_OF_PROCESSORS",
                "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
                "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
                // User-profile vars — PowerShell module discovery + module
                // cache, .NET runtime config probing.
                "USERPROFILE", "APPDATA", "LOCALAPPDATA", "USERNAME",
                "HOMEDRIVE", "HOMEPATH", "COMPUTERNAME",
                "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
                "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
                "CommonProgramW6432", "PUBLIC", "ALLUSERSPROFILE",
                // PowerShell-specific.
                "PSModulePath", "POWERSHELL_DISTRIBUTION_CHANNEL",
                "POWERSHELL_TELEMETRY_OPTOUT", "POWERSHELL_UPDATECHECK",
                "PSExecutionPolicyPreference",
                // .NET / build telemetry.
                "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT",
                "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT",
                "DOTNET_ROLL_FORWARD",
            })
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v)) env[name] = v;
            }
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            env["DOTNET_ROOT"] = dotnetRoot;
            if (Directory.Exists(dotnetRoot))
                env["PATH"] = dotnetRoot + System.IO.Path.PathSeparator + env["PATH"];
        }
        else
        {
            // No DOTNET_ROOT set — fall back to the dotnet on the inherited
            // PATH so a default-location install is still reachable.
            var dotnetExe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in inheritedPath.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                if (File.Exists(System.IO.Path.Combine(dir, dotnetExe)))
                {
                    env["PATH"] = dir + System.IO.Path.PathSeparator + env["PATH"];
                    break;
                }
            }
        }

        return env;
    }
}
