using System.Diagnostics;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Identifies the kind of bash available on the current host.
/// </summary>
public enum BashHostKind
{
    /// <summary>No bash found.</summary>
    None,

    /// <summary>Native bash found on PATH or via BASH env var.</summary>
    Native,

    /// <summary>Bash available via wsl.exe on Windows.</summary>
    Wsl,
}

/// <summary>
/// Describes an available bash interpreter.
/// </summary>
/// <param name="Kind">The source of this bash host.</param>
/// <param name="Path">
/// Executable path for <see cref="BashHostKind.Native"/>;
/// "wsl.exe" for <see cref="BashHostKind.Wsl"/>;
/// null for <see cref="BashHostKind.None"/>.
/// </param>
/// <param name="Version">Version string from <c>bash --version</c>; empty when unavailable.</param>
/// <param name="Locale">Locale string from <c>locale</c>; empty when unavailable.</param>
public sealed record BashHost(
    BashHostKind Kind,
    string? Path,
    string Version,
    string Locale)
{
    /// <summary>Singleton representing no bash.</summary>
    public static readonly BashHost None = new(BashHostKind.None, null, string.Empty, string.Empty);

    /// <summary>True when bash is available (Kind != None).</summary>
    public bool IsAvailable => Kind != BashHostKind.None;
}

/// <summary>
/// Probes the current host for a usable bash interpreter.
///
/// Probe order:
///   1. BASH environment variable (explicit override).
///   2. bash / bash.exe on PATH.
///   3. wsl.exe -e bash (Windows only).
///
/// Returns <see cref="BashHost.None"/> when nothing is found.
/// </summary>
public static class BashLocator
{
    private static BashHost? _cached;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the best available bash host, probing once and caching the result.
    /// Thread-safe.
    /// </summary>
    public static BashHost Find()
    {
        if (_cached is not null) return _cached;
        lock (_lock)
        {
            _cached ??= Probe();
            return _cached;
        }
    }

    /// <summary>
    /// Resets the cached result. Intended for tests that need to re-probe
    /// after modifying environment variables.
    /// </summary>
    internal static void ResetCache() => _cached = null;

    private static BashHost Probe()
    {
        // 1. BASH env var override
        var envBash = Environment.GetEnvironmentVariable("BASH");
        if (!string.IsNullOrEmpty(envBash) && File.Exists(envBash))
        {
            var (version, locale) = QueryBash(envBash, "-c");
            if (!string.IsNullOrEmpty(version))
                return new BashHost(BashHostKind.Native, envBash, version, locale);
        }

        // 2. bash on PATH
        var pathBash = FindOnPath(OperatingSystem.IsWindows() ? "bash.exe" : "bash");
        if (pathBash is not null)
        {
            var (version, locale) = QueryBash(pathBash, "-c");
            if (!string.IsNullOrEmpty(version))
                return new BashHost(BashHostKind.Native, pathBash, version, locale);
        }

        // 3. wsl.exe -e bash (Windows only)
        if (OperatingSystem.IsWindows())
        {
            var (version, locale) = QueryWslBash();
            if (!string.IsNullOrEmpty(version))
                return new BashHost(BashHostKind.Wsl, "wsl.exe", version, locale);
        }

        return BashHost.None;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = System.IO.Path.Combine(dir, executable);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Runs <c>bash -c 'echo $BASH_VERSION; locale'</c> (or the WSL equivalent)
    /// and returns (version, locale). Returns ("", "") on failure or when the
    /// process does not exit within 3 seconds.
    /// </summary>
    private static (string Version, string Locale) QueryBash(string executable, string firstArg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // Use a single -c invocation that emits BASH_VERSION on line 1 and
            // LANG (locale summary) on line 2 — avoids needing the `locale` command.
            psi.ArgumentList.Add(firstArg);
            psi.ArgumentList.Add("echo $BASH_VERSION; echo ${LANG:-}");

            using var proc = Process.Start(psi);
            if (proc is null) return (string.Empty, string.Empty);

            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.StandardError.BaseStream.CopyToAsync(Stream.Null);

                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return (string.Empty, string.Empty);
                }

                var output = stdoutTask.GetAwaiter().GetResult();
                var lines = output.Split('\n', StringSplitOptions.None);
                var version = lines.Length > 0 ? lines[0].Trim('\r').Trim() : string.Empty;
                var locale = lines.Length > 1 ? lines[1].Trim('\r').Trim() : string.Empty;

                // Bash version strings start with digits; reject if empty or clearly wrong.
                if (string.IsNullOrEmpty(version) || !char.IsDigit(version[0]))
                    return (string.Empty, string.Empty);

                return (version, locale);
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Probes WSL bash via <c>wsl.exe -e bash -c 'echo $BASH_VERSION; echo ${LANG:-}'</c>.
    /// </summary>
    private static (string Version, string Locale) QueryWslBash()
    {
        var wslExe = FindOnPath("wsl.exe");
        if (wslExe is null)
        {
            // Try default location
            wslExe = @"C:\Windows\System32\wsl.exe";
            if (!File.Exists(wslExe)) return (string.Empty, string.Empty);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = wslExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("echo $BASH_VERSION; echo ${LANG:-}");

            using var proc = Process.Start(psi);
            if (proc is null) return (string.Empty, string.Empty);

            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.StandardError.BaseStream.CopyToAsync(Stream.Null);

                if (!proc.WaitForExit(8000)) // WSL startup is slower
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return (string.Empty, string.Empty);
                }

                var output = stdoutTask.GetAwaiter().GetResult();
                var lines = output.Split('\n', StringSplitOptions.None);
                var version = lines.Length > 0 ? lines[0].Trim('\r').Trim() : string.Empty;
                var locale = lines.Length > 1 ? lines[1].Trim('\r').Trim() : string.Empty;

                if (string.IsNullOrEmpty(version) || !char.IsDigit(version[0]))
                    return (string.Empty, string.Empty);

                return (version, locale);
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> configured to run a bash script
    /// using the given <paramref name="host"/>. Returns null for Kind=None.
    /// </summary>
    public static ProcessStartInfo? BuildPsi(BashHost host, string script)
    {
        if (!host.IsAvailable) return null;

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        if (host.Kind == BashHostKind.Wsl)
        {
            psi.FileName = host.Path!;
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
        }
        else
        {
            psi.FileName = host.Path!;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
        }

        return psi;
    }
}
