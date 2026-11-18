using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A command word that names one of PowerShell's built-in aliases is an ordinary bash word: a function, a program on
/// PATH, or "command not found" (127). REGRESSION: the bash runspace kept PowerShell's ~135 aliases, and PowerShell
/// resolves an alias first, so <c>write -- $x</c> ran Write-Output and printed <c>--</c> (oils hay.spec.sh, Windows
/// fleet s22e), <c>r</c> re-ran the previous command, <c>iex</c> evaluated its argument as PowerShell. Whether a word
/// is ALSO a program depends on the machine (the GitHub Windows runner has R's <c>R.exe</c> and Ruby's <c>ri</c> on PATH,
/// so bash runs them there), so the loop runs with a PATH that holds no program: an alias name is then "not found"
/// on every machine, and resolving it as an alias still shows. Rule: <c>PsBash.Core.PowerShellAliases</c>.
/// </summary>
public class PowerShellAliasWordTests
{
    [SkippableFact]
    public Task PowerShellAliasNames_AreCommandNotFound() => AssertOracle.EqualAsync(
        "PATH=/nonexistent-psbash-no-programs\n"
        + "for c in gci sls iex icm r h ni ri del erase ren select measure group foreach gcm saps; do\n"
        + "  type -t \"$c\" >/dev/null 2>&1; t=$?; \"$c\" x >/dev/null 2>&1; echo \"$c type=$t run=$?\"\n"
        + "done\n"
        + "iex 'echo injected' 2>/dev/null; echo \"iex=$?\"",
        TimeSpan.FromSeconds(60));

    // A function of the same name is still found first, and a PATH program after it, as in bash.
    [SkippableFact]
    public Task FunctionNamedLikeAnAlias_Runs() => AssertOracle.EqualAsync(
        "gci() { echo \"fn gci $*\"; }; gci -- a b; r() { echo \"fn r\"; }; r; type -t gci",
        TimeSpan.FromSeconds(30));
}
