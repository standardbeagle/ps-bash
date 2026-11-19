using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for a pipeline record whose text ends in SEVERAL newlines. REGRESSION: line consumers split a
/// record with <c>text.TrimEnd('\n').Split('\n')</c>, which strips every trailing newline, so the record's trailing
/// EMPTY lines vanished: <c>printf 'a\n\n' | grep -c ''</c> was 1 (bash 2), and the same in sed, cat -n/-A, nl, cut,
/// rev, tac, sort, uniq, head, tail, shuf, column, rg, fold, expand, comm …; <c>tr</c> emitted <c>"b\n"</c> as a plain
/// record (read back as ONE line), and a piece joined to such a record (<c>printf x; printf 'a\n\n'</c>) lost its
/// empty line even in awk. An empty unterminated record (<c>printf ''</c>) counted as one empty line.
/// Fixed at the class: one splitter, <c>BashRuntime.SplitRecord</c> (drop ONE own terminator, then split), and
/// <c>BashRuntime.BytesRecord</c> for producers (guard <c>RecordSplitGuardTests</c>). Output goes to a file and is
/// shown with <c>od -c</c> so the oracle's trailing-whitespace trim cannot hide a lost empty line.
/// </summary>
public class RecordTrailingEmptyLinesDifferentialTests
{
    private const string Show = "eval \"$src | $c\" > o 2>&1; echo \"## $c <- $src: $(od -An -c o | tr -s ' ' | tr -d '\\n')\"\n";

    [SkippableFact]
    public Task LineConsumers_KeepARecordsTrailingEmptyLines()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            // `sed -n '$='` on an unterminated last line: SedMissingNewlineDifferentialTests.
            + "for c in \"grep -c ''\" \"grep -n ''\" 'sed =' 'cat -n' 'cat -A' 'cat' 'nl -ba' 'cut -c1' "
            + "'tr a b' 'tr a b | wc -l' 'rev' 'tac' 'sort' 'uniq -c' 'head -n 9' 'tail -n 9' 'tail -n +1' 'shuf | sort' "
            + "'column -t' \"rg -c ''\" \"rg -n ''\" 'fold' 'expand' 'unexpand' 'paste -' 'comm - /dev/null' "
            + "'awk {print\\ NR\\ \\$0}' 'wc -l'; do\n"
            + "  for src in \"printf 'a\\n\\n'\" \"printf 'a\\n\\n\\n'\" \"printf '\\n\\n'\" \"printf 'a\\n\\nb\\n\\n'\" "
            + "\"printf 'a\\nb'\" \"printf ''\" \"{ printf x; printf 'a\\n\\n'; }\"; do\n"
            + "    " + Show
            + "  done\n"
            + "done\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(180));

    [SkippableFact]
    public Task LiteralPipelines_KeepARecordsTrailingEmptyLines()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            // Literal (not eval'd) pipelines: the shape the fused in-process lane takes.
            + "printf 'b\\n\\n' | sort > o; od -An -c o\n"
            + "printf 'a\\n\\n\\n' | uniq > o; od -An -c o\n"
            + "printf 'a\\n\\n' | tail -n 2 > o; od -An -c o\n"
            + "printf 'a\\n\\n' | tac > o; od -An -c o\n"
            + "printf 'a\\n\\n' | cut -c1 > o; od -An -c o\n"
            + "printf 'a\\n\\n' | nl -ba > o; od -An -c o\n"
            + "printf 'a\\n\\n' | grep -c ''\n"
            + "printf 'a\\n\\n' | sed -n '$='\n"
            + "printf 'a\\n\\n' | tr a b | grep -c ''\n"
            + "printf 'a\\n\\n' | head -n -1 > o; od -An -c o\n"
            + "printf 'a\\nb\\n\\n\\n' | head -n -2 > o; od -An -c o\n"
            + "printf 'x\\ny\\n' > t; printf 'a\\n\\n' | rg -f - t | wc -l\n"
            + "printf 'a\\n\\n' | while IFS= read -r l; do echo \"[$l]\"; done\n"
            + "printf '' | sed p > o; od -An -c o; echo \"|$?\"\n"
            + "{ printf 'a\\n'; printf ''; } | sed p > o; od -An -c o\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));
}
