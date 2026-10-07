using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for /tmp paths that arrive by EXPANSION (<c>d=/tmp; … $d/f</c>). The emitter
/// rewrites a literal <c>/tmp/f</c> to <c>$env:TEMP</c> on Windows; an expanded one used to reach
/// cmdlets, <c>cd</c>, <c>[ -e ]</c> and <c>&lt;</c> unmapped and resolve to <c>C:\tmp</c>. Each script
/// MIXES the literal and the expanded spelling of one file, so it fails on the old code whether or
/// not this machine has a C:\tmp (missing: the write fails; present: the two spellings disagree).
/// Nothing prints <c>$$</c>, so the oracle output is deterministic.
/// </summary>
public class TmpPathDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Tmp_ExpandedAndLiteral_NameOneFile()
        => Eq("d=/tmp; f=psb_rt_$$\n"
            + "echo x > $d/$f; echo y >> \"$d/$f\"\n"
            + "cat /tmp/$f\n"
            + "[ -e $d/$f ] && echo test-e\n"
            + "[[ -f \"$d/$f\" ]] && echo dbl-f\n"
            + "while read -r l; do echo \"read $l\"; done < $d/$f\n"
            + "rm $d/$f\n"
            + "[ -e /tmp/$f ] || echo gone");

    [SkippableFact]
    public Task Tmp_ExpandedDirectoryOps_LandWhereLiteralLooks()
        => Eq("d=/tmp; f=psb_rtd_$$\n"
            + "mkdir $d/$f && touch $d/$f/t\n"
            + "[ -e /tmp/$f/t ] && echo mkdir-touch\n"
            + "cp /tmp/$f/t $d/$f/u && mv $d/$f/u $d/$f/v && [ -e /tmp/$f/v ] && echo cp-mv\n"
            + "find $d/$f -name v | wc -l\n"
            + "(cd $d/$f && [ -e t ] && echo cd)\n"
            + "rm -r $d/$f; [ -e /tmp/$f ] || echo gone");
}
