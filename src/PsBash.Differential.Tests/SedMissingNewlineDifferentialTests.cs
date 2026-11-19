using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for GNU sed's output on input whose last line has no newline. GNU keeps the missing newline on
/// the BUFFER holding that line (<c>chomped</c>, carried by <c>h H g G x N n</c>): only a write of a pattern/hold space
/// (<c>p P w W s///p</c>, the autoprint) leaves it out, and the next write to the same output supplies it first; the
/// text sed makes itself (<c>= l F i a c</c>) always ends in a newline; <c>r R e</c> copy bytes verbatim.
/// REGRESSION: the sink gave the LAST output record the input's terminator state whatever it was, so
/// <c>printf 'a\nb' | sed -n '$='</c> printed <c>2</c> with no newline (GNU <c>2\n</c>), <c>$a X</c> / <c>$c X</c> /
/// <c>$i X</c> / <c>$l</c> lost theirs, <c>-n 1p</c> / <c>1h;$!d;x</c> / <c>$r f</c> gained or lost one, and
/// <c>w</c> files always ended in a newline. <c>q</c> settles stdout's owed newline and autoprints WITH one
/// (<c>$q</c> gave <c>a\nb</c>, GNU <c>a\nb\n</c>; <c>Q</c> does neither). A <c>D</c> restart writes its pass's
/// <c>i p P =</c> output and keeps its appends (<c>sed 'N;P;D'</c> printed only the last line). Bytes are shown with
/// <c>od -c</c>. Not covered (known gap): <c>w /dev/stdout</c>, which GNU gives its own owed newline on the shared stream
/// (<c>printf 'a\nb' | sed '$w /dev/stdout'</c> is <c>a\nbb</c>).
/// </summary>
public class SedMissingNewlineDifferentialTests
{
    private const string Show = "eval \"$src | sed $s\" > o 2>&1; echo \"## $s <- $src: $(od -An -c o | tr -s ' ' | tr -d '\\n')\"\n";

    [SkippableFact]
    public Task Pipeline_LastLineWithoutNewline_EveryOutputKind()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "printf 'R1\\nR2' > rf; printf 'R1\\nR2\\n' > rt\n"
            + "for s in \"-n '\\$='\" \"'\\$='\" \"-n '='\" \"'\\$a X'\" \"'\\$i X'\" \"'\\$c X'\" \"'1a X'\" \"-n '\\$l'\" \"'\\$l'\" "
            + "\"-n '1p'\" \"-n '\\$p'\" \"'\\$p'\" \"-n 'p;p'\" \"'\\$P'\" \"'N;P;D'\" \"'\\$!N;P'\" \"'s/b/B/p'\" \"-n 's/a/A/p'\" "
            + "\"'1h;\\$!d;x'\" \"'1h;\\$!d;G'\" \"'\\$!d;H;x'\" \"'\\$!d;g'\" \"'1!G;h;\\$!d'\" \"-n 'x;\\$p'\" \"'\\$r rf'\" \"'\\$r rt'\" \"'1r rf'\" "
            + "\"'R rf'\" \"'\\$R rf'\" \"'\\$q'\" \"'1q'\" \"'2q'\" \"'\\$d'\" \"'n;d'\" \"'\\$!n'\" \"'y/ab/AB/'\" \"-n '\\$F'\" "
            // `e COMMAND` is pinned at the cmdlet layer (SedGnuCommandsTests): here its nested shell inherits the
            // harness's PSBASH_DEBUG trace through `2>&1`.
            + "\"-n '\\$p;\\$q'\" \"'\\$p;\\$q'\" \"'\\$=;\\$q'\" \"'\\$Q'\" \"'\\$p;\\$Q'\" \"-e '\\$r rf' -e '\\$q'\" "
            + "\"'\\$!N;P;D'\" \"-e '1{N;a X' -e '};P;D'\" \"-e '1{N;i X' -e '};P;D'\"; do\n"
            + "  for src in \"printf 'a\\nb'\" \"printf 'a\\nb\\n'\" \"printf a\" \"{ printf 'a\\n'; printf b; }\"; do\n"
            + "    " + Show
            + "  done\n"
            + "done\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(180));

    [SkippableFact]
    public Task Literal_DollarEquals_TheReportedCase()
        => AssertOracle.EqualAsync(
            "printf 'a\\nb' | sed -n '$=' | od -An -c\n"
            + "printf 'a\\nb' | sed '$a\\\nX' | od -An -c\n"
            + "printf 'a\\nb' | sed -n '$l' | od -An -c\n"
            + "printf 'a\\nb' | sed 'N;P;D' | od -An -c\n");

    [SkippableFact]
    public Task Files_InPlace_WFiles_AndNullData()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "printf 'r1\\nr2' > rr; printf 'r1\\nr2\\n' > rt\n"
            + "sed -n '$=' rr | od -An -c\n"
            + "sed p rr rr | od -An -c\n"
            + "sed -n 2p rr rr | od -An -c\n"
            + "sed -n 2p rr rt | od -An -c\n"
            + "sed -s -n '$=' rr rr | od -An -c\n"
            + "sed '$a X' rr rt | od -An -c\n"
            + "cp rr e1; sed -i '$a X' e1; od -An -c e1\n"
            + "cp rr e2; sed -i 's/r/R/' e2; od -An -c e2\n"
            + "cp rr e3; sed -i -n '$=' e3; od -An -c e3\n"
            + "sed -n 'w w1' rr; od -An -c w1\n"
            + "sed -n '1w w2' rr; od -An -c w2\n"
            + "sed -n 'w w3\n$=' rr | od -An -c; od -An -c w3\n"
            + "printf 'a\\0b' | sed -z -n '$=' | od -An -c\n"
            + "printf 'a\\0b' | sed -z 'p' | od -An -c\n"
            + "printf 'a\\nb' | sed -n '$=' - | od -An -c\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));
}
