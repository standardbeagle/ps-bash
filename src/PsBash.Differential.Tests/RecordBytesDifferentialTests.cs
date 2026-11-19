using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for the ONE record → bytes rule (<c>PsBash.Core.StdoutRecord.Bytes</c>, cmdlet side
/// <c>BashRuntime.RecordBytes</c>): a plain record is its text plus the boundary newline even when the text already
/// ends in one; an exact-bytes (<c>printf</c>) record is its text. REGRESSION: the host renderer used that rule but the
/// readers (SplitRecord, ConvertTo-BashCapture, redirect/tee, od, wc, mapfile, bash stdin, stderr …) gave such a text
/// no boundary, so <c>jq -r '"a\n"'</c>, <c>basename $'a\n'</c> and <c>echo $'a\n'</c> printed two lines on the screen
/// and one into <c>| cat</c>, <c>&gt; f</c> and <c>$(…)</c>; <c>echo "$x" &gt;&amp;2</c> lost a line;
/// <c>{ read -n 1; cat; }</c> printed a blank line bash never wrote; and <c>source &lt;(printf 'echo a'; printf 'b\n')</c>
/// ran <c>echo a</c> then <c>b</c> (records joined with a newline each, bash: <c>echo ab</c>) and dropped the sourced
/// commands' stdout. Bytes are shown with
/// <c>od -c</c> so the oracle's trailing-whitespace trim cannot hide a lost newline (guard <c>RecordBytesGuardTests</c>).
/// </summary>
public class RecordBytesDifferentialTests
{
    [SkippableFact]
    public Task Producers_ScreenPipeFileAndCapture_AgreeFor0To2TrailingNewlines()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "for n in 0 1 2; do\n"
            + "  nl=$(printf '%*s' \"$n\" '' | sed 's/ /\\\\n/g')\n"
            + "  for c in \"jq -rn '\\\"a$nl\\\"'\" \"basename \\$'a$nl'\" \"echo \\$'a$nl'\" \"printf 'a$nl'\"; do\n"
            + "    eval \"$c\" > o; echo \"## $c >f: $(od -An -c o | tr -s ' ' | tr -d '\\n')\"\n"
            + "    eval \"$c\" | cat > o; echo \"## $c |cat: $(od -An -c o | tr -s ' ' | tr -d '\\n')\"\n"
            // `| wc -l` is a known gap (wc still uses the reader rule; its owner routes it through RecordBytes).
            + "    eval \"$c\" | grep -c ''\n"
            + "    v=$(eval \"$c\"; echo .); printf '%s' \"$v\" > o; echo \"## $c \\$(): $(od -An -c o | tr -s ' ' | tr -d '\\n')\"\n"
            + "    eval \"$c\"; echo \"## $c screen END\"\n"
            + "  done\n"
            + "done\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(120));

    [SkippableFact]
    public Task LiteralProducers_Pipe_Capture_Stderr_AndReaders()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            // Literal (not eval'd) pipelines: the fused in-process lane and the per-command readers.
            + "jq -rn '\"a\\n\"' | od -An -c\n"
            + "jq -rn '\"a\\n\"' | grep -c ''\n"
            + "jq -rn '\"a\\n\"' | tail -n 1 | od -An -c\n"
            + "jq -rn '\"a\\n\"' | sed = | od -An -c\n"
            + "jq -rn '\"a\\n\"' | tr a b | od -An -c\n"
            + "basename $'a\\n' | tee t | od -An -c; od -An -c t\n"
            + "v=$(basename $'a\\n'; echo .); echo \"[$v]\"\n"
            + "x=$'a\\n'; { echo \"$x\" >&2; } 2> e; od -An -c e\n"
            + "printf 'abc\\n' | { read -n 1 y; cat; } > o; od -An -c o\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task ScriptFromProcessSubstitutionOrStdin_IsTheProducersBytes()
        => AssertOracle.EqualAsync(
            "source <(printf 'echo a'; printf 'b\\n')\n"
            + "source <(printf 'echo x\\n'; printf 'echo y')\n"
            + "source <(echo 'echo hi')\n" // the sourced commands' stdout was dropped
            + "{ printf 'echo a'; printf 'b\\n'; } | bash\n",
            timeout: TimeSpan.FromSeconds(60));
}
