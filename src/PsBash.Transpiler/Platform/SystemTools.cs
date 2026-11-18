namespace PsBash.Core.Platform;

/// <summary>
/// The ONE answer to "where does this Unix OS keep its own <c>stat</c> / <c>id</c> / <c>ps</c>?" (spec
/// <c>docs/specs/README.md</c> §Platform dependencies; guard <c>PlatformDependencyGuardTests</c>: no other product
/// file names a system tool directory).
/// <para>
/// REGRESSION: callers spawned <c>/usr/bin/stat</c> by absolute path. Alpine (busybox) keeps <c>stat</c> and
/// <c>ps</c> in <c>/bin</c>, so <c>ls -l</c> lost every owner name there. The OS's own tool is still wanted rather
/// than the first on PATH: callers pick the flag dialect by OS (GNU <c>stat -c</c>, BSD <c>stat -f</c>), and a
/// macOS PATH often puts Homebrew's GNU coreutils first.
/// </para>
/// </summary>
public static class SystemTools
{
    /// <summary>Where a Unix OS installs its base tools, in search order: <c>/usr/bin</c> (glibc distros, macOS),
    /// <c>/bin</c> (Alpine/busybox), then the admin tools (macOS <c>sysctl</c> is in <c>/usr/sbin</c>).</summary>
    public static IReadOnlyList<string> SystemDirectories { get; } = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>The POSIX shell: every supported Unix has it at this path (it is where <c>system(3)</c> and
    /// <c>posix_spawn</c> of a script look).</summary>
    public const string PosixShell = "/bin/sh";

    /// <summary>The host's own bash by absolute path, then <see cref="PosixShell"/> for a host with no bash at all
    /// (Alpine). Absolute only: a PATH search could find a ps-bash installed AS <c>bash</c>.</summary>
    public static IReadOnlyList<string> HostShellCandidates { get; } =
        ["/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", PosixShell];

    /// <summary>Absolute path of the OS's own <paramref name="name"/>: the first runnable file in
    /// <see cref="SystemDirectories"/>. Null on Windows and when no system directory has it. PATH is not
    /// consulted.</summary>
    public static string? Find(string name) =>
        OperatingSystem.IsWindows() ? null : Find(name, SystemDirectories);

    /// <summary><see cref="Find(string)"/> over <paramref name="directories"/> (test seam).</summary>
    public static string? Find(string name, IEnumerable<string> directories) =>
        ExecutableLookup.Find(name, directories);

    /// <summary><see cref="Find(string)"/>, else the bare name (the spawn then searches PATH): a usable
    /// <c>FileName</c> on every Unix.</summary>
    public static string Resolve(string name) => Find(name) ?? name;
}
