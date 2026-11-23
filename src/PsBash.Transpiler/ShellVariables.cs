using System.Runtime.InteropServices;

namespace PsBash.Core;

/// <summary>
/// The variables bash itself defines: what <c>compgen -v</c> lists in <c>env -i bash --norc</c>. ONE table, two kinds:
/// <list type="bullet">
/// <item><b>Seeded</b> (<see cref="Seeded"/>): a value bash binds when a shell starts, after which it is an ordinary
/// variable the script reads, assigns, exports or unsets (<c>IFS</c>, <c>PPID</c>, <c>SHLVL</c>, <c>OPTIND</c>,
/// <c>HOSTTYPE</c>, ...). ps-bash keeps scalars in the environment (<c>BashVariableStore</c>), so the host binds them
/// there at shell start (<see cref="SeedShellStart"/>, from <c>SdkWorker</c>), with bash's export attribute.</item>
/// <item><b>Computed</b> (<see cref="Computed"/>): a value worked out on each read (<c>RANDOM</c>, <c>SECONDS</c>,
/// <c>BASHPID</c>, <c>BASH_SUBSHELL</c>, the call-stack and directory-stack arrays, ...). The emitter maps each read
/// (<c>PsBuild.TryMapSpecialVar</c> for a scalar, <c>PsBuild.ShellArrayExpr</c> for an array) and
/// <c>Get-BashIndirectValue</c> does for <c>${!ref}</c>.</item>
/// </list>
/// The guard <c>ShellVariablesGuardTests</c> lists every name bash defines and checks each is in exactly one kind (or
/// on the short, reasoned list of names ps-bash does not provide). Spec: <c>docs/specs/environment-model.md</c>.
/// </summary>
public static class ShellVariables
{
    /// <summary>The bash release ps-bash reports (<c>$BASH_VERSION</c>; <c>BASH_VERSINFO</c> is its parts).</summary>
    public const string BashVersion = "5.2.37(1)-release";

    /// <summary>
    /// The launcher's own parent process id, handed to the host in the launcher's environment block (the host cannot
    /// see who started the launcher). <see cref="SeedShellStart"/> takes it as <c>PPID</c> and removes it.
    /// </summary>
    public const string LauncherParentPidVariable = "PSBASH_PPID";

    /// <summary>When bash binds a seeded variable: always (<c>bind_variable</c>), or only when the environment it
    /// inherited lacks it (<c>set_if_not</c>).</summary>
    public enum Bind { Always, IfUnset }

    /// <summary>One seeded variable. <paramref name="Value"/> gets the inherited value (null when absent).</summary>
    public sealed record SeededVariable(string Name, Bind When, bool Exported, Func<string?, string> Value);

    /// <summary>The variables bound at shell start (bash <c>variables.c</c> <c>initialize_shell_variables</c>).</summary>
    public static readonly IReadOnlyList<SeededVariable> Seeded =
    [
        new("IFS", Bind.Always, false, _ => " \t\n"),
        new("OPTIND", Bind.Always, false, _ => "1"),
        new("OPTERR", Bind.Always, false, _ => "1"),
        new("PS4", Bind.IfUnset, false, _ => "+ "),
        new("SHLVL", Bind.Always, true, NextShellLevel),
        new("PPID", Bind.Always, false, _ => ParentPid()),
        new("BASH", Bind.Always, false, _ => ShellPath()),
        new("BASH_VERSION", Bind.Always, false, _ => BashVersion),
        new("SHELL", Bind.IfUnset, false, _ => DefaultShell()),
        new("HOSTNAME", Bind.IfUnset, false, _ => Environment.MachineName.ToLowerInvariant()),
        new("HOSTTYPE", Bind.IfUnset, false, _ => HostType()),
        new("MACHTYPE", Bind.IfUnset, false, _ => MachType()),
        new("OSTYPE", Bind.IfUnset, false, _ => OsType()),
        new("COMP_WORDBREAKS", Bind.IfUnset, false, _ => " \t\n\"'><=;|&(:"),
    ];

    /// <summary>The variables computed on each read. Arrays: <c>PsBuild.ShellArrayExpr</c>; scalars:
    /// <c>PsBuild.TryMapSpecialVar</c>.</summary>
    public static readonly IReadOnlyList<string> Computed =
    [
        "BASHPID", "BASHOPTS", "SHELLOPTS", "BASH_SUBSHELL", "EPOCHREALTIME", "EPOCHSECONDS", "EUID", "UID",
        "LINENO", "RANDOM", "SRANDOM", "SECONDS", "PWD",
        // arrays
        "BASH_ALIASES", "BASH_LINENO", "BASH_SOURCE", "BASH_VERSINFO", "DIRSTACK", "FUNCNAME", "GROUPS", "PIPESTATUS",
    ];

    /// <summary>The names bash keeps unexported even when the inherited environment had them (bound always).</summary>
    public static IEnumerable<string> AlwaysUnexported
    {
        get
        {
            foreach (var v in Seeded)
                if (v.When == Bind.Always && !v.Exported) yield return v.Name;
        }
    }

    /// <summary>
    /// A shell starts (the process environment is the launcher's block, and <see cref="ShellExports.Reset"/> has
    /// marked all of it exported): bind every seeded variable as bash does, with bash's export attribute. A name bound
    /// only when unset keeps an inherited value, and its attribute (exported).
    /// </summary>
    public static void SeedShellStart()
    {
        string? launcherParent = Environment.GetEnvironmentVariable(LauncherParentPidVariable);
        if (launcherParent is not null) ProcessScalars.Set(LauncherParentPidVariable, null);
        t_launcherParent = launcherParent;
        try
        {
            foreach (var v in Seeded)
            {
                string? inherited = Environment.GetEnvironmentVariable(v.Name);
                if (v.When == Bind.IfUnset && inherited is not null) continue;
                ProcessScalars.Set(v.Name, v.Value(inherited)); // the shell's store: its one writer of the environment
                if (v.Exported) ShellExports.Export(v.Name);
                else ShellExports.Unexport(v.Name);
            }
        }
        finally { t_launcherParent = null; }
    }

    [ThreadStatic] private static string? t_launcherParent;

    /// <summary>bash: the inherited <c>SHLVL</c> plus one; 1 when it is absent or not a number (and 0 below zero).</summary>
    public static string NextShellLevel(string? inherited)
    {
        if (!long.TryParse(inherited?.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long level))
            return "1";
        if (level < 0) return "0";
        level++;
        // bash warns "shell level (N) too high, resetting to 1" past 1000.
        return level >= 1000 ? "1" : level.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>$PPID</c>: the process that started the shell. The shell is the launcher (its host is shared), so the
    /// launcher's parent when the launcher handed it over (<see cref="LauncherParentPidVariable"/>), else this
    /// process's parent; 1 (the process that adopts orphans on Unix) when that is gone.
    /// </summary>
    private static string ParentPid()
    {
        if (int.TryParse(t_launcherParent, out int fromLauncher) && fromLauncher > 0)
            return fromLauncher.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (Platform.ProcessInfo.OwnParentPid() ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><c>$HOSTTYPE</c>: the machine's architecture as bash names it.</summary>
    public static string HostType() => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";

    /// <summary><c>$MACHTYPE</c>: <c>arch-vendor-os</c> as bash's configure names it.</summary>
    public static string MachType() => HostType() + (OperatingSystem.IsWindows() ? "-pc-msys" : OperatingSystem.IsMacOS() ? "-apple-darwin" : "-pc-linux-gnu");

    /// <summary><c>$OSTYPE</c> as bash's build for the OS names it (Git for Windows' bash says <c>msys</c>).</summary>
    public static string OsType() => OperatingSystem.IsWindows() ? "msys" : OperatingSystem.IsMacOS() ? "darwin" : "linux-gnu";

    private static string? s_shellPath;

    /// <summary><c>$BASH</c>: the shell's own program, the <c>ps-bash</c> launcher beside this process (shell form).</summary>
    public static string ShellPath() => s_shellPath ??= BashPath.ToShell(LauncherPath() ?? Environment.ProcessPath ?? "ps-bash");

    private static string? LauncherPath()
    {
        string? self = Environment.ProcessPath;
        string? dir = string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(self);
        if (dir is null) return null;
        string launcher = Path.Combine(dir, OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash");
        return File.Exists(launcher) ? launcher : null;
    }

    private static string? s_defaultShell;

    /// <summary>
    /// <c>$SHELL</c> when the environment has none: bash sets it at startup (not exported) to the login shell of the
    /// user's passwd entry. On Unix that entry's shell (<c>/bin/sh</c> when it cannot be read); on Windows, which has no
    /// passwd, this shell itself: the <c>ps-bash</c> launcher beside the running process.
    /// </summary>
    public static string DefaultShell() => s_defaultShell ??= BashPath.ToShell(ComputeDefaultShell());

    private static string ComputeDefaultShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                string user = Environment.UserName;
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    var fields = line.Split(':');
                    if (fields.Length >= 7 && fields[0] == user && fields[6].Length > 0) return fields[6];
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Platform.SystemTools.PosixShell;
        }
        return LauncherPath() ?? Environment.ProcessPath ?? "ps-bash";
    }
}
