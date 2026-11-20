using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The shell diagnostics of <c>let</c> and <c>.</c>/<c>source</c> carry the prefix (<c>s.sh: line 3: let: …</c>) and
/// bash's words. docs/specs/error-handling.md "Diagnostic prefix". Recorded from bash 5.2 (cassette).
/// </summary>
public class LetSourceDiagnosticDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";

    /// <summary>
    /// <c>let</c> syntax errors in bash's words, with the prefix and the <c>let:</c> label: <c>let '1+'</c> ended the
    /// whole transpile (no line of the script ran). The statuses are printed at the end: each report is the only output
    /// until then.
    /// </summary>
    [SkippableFact]
    public Task Let_SyntaxError_IsTheShellsDiagnostic()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "let '1+'; r1=$?\n"
            + "let 'x=='; r2=$?\n"
            + "let 'a b c'; r3=$?\n"
            + "let '(1+2'; r4=$?\n"
            + "let '1 @ 2'; r5=$?\n"
            + "v='1+ '; let \"$v\"; r6=$?\n"
            + "echo \"rc=$r1 $r2 $r3 $r4 $r5 $r6\"\n"
            + "EOF\n"
            + "bash s.sh 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// <c>let</c> alone, <c>.</c>/<c>source</c> alone (prefixed message, then the usage line WITHOUT the prefix, status
    /// 2) and a missing sourced file (it read <c>ps-bash: ./nofile_zz: …</c>).
    /// </summary>
    [SkippableFact]
    public Task LetAndSource_WithNoOperandOrAMissingFile_AreTheShellsDiagnostics()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "let; echo \"rc=$?\"\n"
            + ".; echo \"rc=$?\"\n"
            + "source; echo \"rc=$?\"\n"
            + ". ./nofile_zz; echo \"rc=$?\"\n"
            + ". 2>err; echo \"rc=$?\"; cat err\n"
            + "EOF\n"
            + "bash s.sh 2>&1\n"
            + "bash -c 'let; echo \"rc=$?\"; source; echo \"rc=$?\"' 2>&1" + Done,
            timeout: TimeSpan.FromSeconds(60));
}
