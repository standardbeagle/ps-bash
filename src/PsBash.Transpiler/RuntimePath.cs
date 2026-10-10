namespace PsBash.Core;

/// <summary>
/// The ONE runtime path policy: how a path operand that reaches a command at run time — literal or
/// produced by expansion (<c>d=/tmp; cat $d/f</c>) — maps to a native path. Every resolver goes
/// through it (cmdlet operands via <c>FileSystemHelpers.NormalizeOperandPath</c> /
/// <c>ProviderPath</c>, the psm1 helpers and emitted <c>cd</c> / <c>[ -e ]</c> via
/// <c>BashRuntime.MapPath</c>), so a redirect, <c>cat</c>, <c>rm</c> and <c>cd</c> always agree on
/// which file a spelling means.
/// <para>
/// On Windows: <c>/tmp[/…]</c> → <c>$env:TEMP[\…]</c> (the same directory the emitter's literal
/// <c>/tmp/</c> rewrite, <c>PsBuild.TempDirExpr</c>, picks — so <c>&gt; /tmp/f</c> and
/// <c>&gt; $d/f</c> are one file), then <see cref="WindowsPath.Normalize"/> (unix drive paths,
/// native drive canonicalization). Off Windows: unchanged — <c>/tmp</c> and <c>/c/…</c> are real
/// paths there.
/// </para>
/// </summary>
public static class RuntimePath
{
    public static string Map(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.OperatingSystem.IsWindows())
            return path ?? string.Empty;
        if (WindowsPath.TryMapTmpRoot(path, TempDir(), out var tmp))
            return tmp;
        return WindowsPath.Normalize(path);
    }

    /// <summary>
    /// The inverse of the <c>/tmp</c> mapping, for TEXT comparisons: a value at or under the temp
    /// directory (<c>$PWD</c> after <c>cd /tmp</c>, a literal <c>/tmp/x</c> the emitter rewrote) as
    /// bash spells it, <c>/tmp[/…]</c> with forward slashes. False off Windows or outside the temp dir.
    /// </summary>
    public static bool TryUnmapTmp(string? value, out string bashPath)
    {
        bashPath = value ?? string.Empty;
        return System.OperatingSystem.IsWindows() && WindowsPath.TryUnmapTmpRoot(value, TempDir(), out bashPath);
    }

    /// <summary><c>$env:TEMP</c> as the emitted <c>TempDirExpr</c> reads it (live: a script may change
    /// it), falling back to the platform temp directory.</summary>
    private static string TempDir()
    {
        var t = System.Environment.GetEnvironmentVariable("TEMP");
        return string.IsNullOrEmpty(t) ? System.IO.Path.GetTempPath() : t;
    }
}
