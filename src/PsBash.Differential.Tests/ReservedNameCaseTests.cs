using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A lowercase spelling of a reserved name (<c>path</c>, <c>tmp</c>, <c>os</c>, <c>lines</c>) is its own variable, as in
/// bash. On Windows the environment is case-insensitive and bash variables live in it, so <c>path=abc</c> used to
/// overwrite <c>PATH</c> (the next command was "not found") and <c>tmp=zz</c> moved temp files into <c>./zz/</c>.
/// See <c>BashNameCase</c>.
/// </summary>
public class ReservedNameCaseTests
{
    [SkippableFact]
    public Task PathAssignment_LeavesCommandLookupAlone() => AssertOracle.EqualAsync(
        "path=abc; echo \"$path\"; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task ForLoopVariable_NamedPath_LeavesCommandLookupAlone() => AssertOracle.EqualAsync(
        "for path in a b; do echo $path; done; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task TmpAssignment_DoesNotMoveTempFiles() => AssertOracle.EqualAsync(
        "tmp=zz_rn; echo $tmp; f=$(mktemp); [ -e \"$f\" ] && echo made; rm -f \"$f\"; [ -d zz_rn ] || echo no-dir");

    [SkippableFact]
    public Task OsAndUserAndHome_AreOrdinaryVariables() => AssertOracle.EqualAsync(
        "os=x; user=me; home=/nowhere; echo $os $user $home; cd && echo cd-ok");

    [SkippableFact]
    public Task DeclareP_PrintsTheBashName() => AssertOracle.EqualAsync(
        "path=b; declare -p path");

    [SkippableFact]
    public Task Exported_ListedByEnvUnderTheBashName() => AssertOracle.EqualAsync(
        "export path=x; env | grep -c '^path='");

    [SkippableFact]
    public Task Unset_RemovesOnlyTheLowercaseVariable() => AssertOracle.EqualAsync(
        "path=x; unset path; echo \"[${path-unset}]\"; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task Read_IntoReservedSpelling() => AssertOracle.EqualAsync(
        "read path <<< v; echo $path; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task Indirection_ThroughReservedSpelling() => AssertOracle.EqualAsync(
        "path=v; ref=path; echo ${!ref}");

    [SkippableFact]
    public Task PrintfV_IntoReservedSpelling() => AssertOracle.EqualAsync(
        "printf -v path %s v; echo $path; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task Arithmetic_OnReservedSpelling() => AssertOracle.EqualAsync(
        "lines=4; echo $((lines*2)); ((lines++)); echo $lines; let lines+=1; echo $lines");

    [SkippableFact]
    public Task Nounset_UnsetReservedSpelling_IsUnbound() => AssertOracle.EqualAsync(
        "set -u; ( echo \"$tmp\" ) 2>/dev/null || echo unbound");

    [SkippableFact]
    public Task Local_ReservedSpelling_ThenRealPathStillWorks() => AssertOracle.EqualAsync(
        "f() { local path=loc; echo $path; }; f; command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task EnvPrefix_ReservedSpelling_DoesNotBreakTheCommand() => AssertOracle.EqualAsync(
        "path=x command -v ls >/dev/null && echo ok");

    [SkippableFact]
    public Task VMinus_TestsTheLowercaseVariable() => AssertOracle.EqualAsync(
        "[[ -v path ]] || echo unset; path=; [[ -v path ]] && echo set");

    [SkippableFact]
    public Task NamePrefix_ListsTheBashName() => AssertOracle.EqualAsync(
        "path=1; pathy=2; echo ${!pat*}");

    [SkippableFact]
    public Task Getopts_IntoReservedSpelling() => AssertOracle.EqualAsync(
        "getopts a: os -a x; echo $os $OPTARG");

    // The emitter's name-keyed tables hold store names; these look a word up in them (BashNameCase.StoreNameComparer).

    [SkippableFact]
    public Task RuntimeExportAppend_ReservedSpelling_LeavesPathAlone() => AssertOracle.EqualAsync(
        "path=a; n=path; export \"$n+=:b\"; echo \"$path\"; [ \"$PATH\" = a:b ] && echo PATH-CHANGED || echo ok");

    [SkippableFact]
    public Task UnsetNameref_ReservedSpelling_UnsetsTheTarget() => AssertOracle.EqualAsync(
        "x=1; declare -n tmp=x; unset tmp; echo \"[${x-unset}]\"");

    [SkippableFact]
    public Task UnsetN_ReservedSpelling_DropsTheReference() => AssertOracle.EqualAsync(
        "y=1; declare -n lines=y; unset -n lines; lines=5; echo \"y=$y lines=$lines\"");

    [SkippableFact]
    public Task Xtrace_ReservedSpelling_TracesTheBashName() => AssertOracle.EqualAsync(
        "set -x; path=abc; set +x");

    [SkippableFact]
    public Task LocalP_ReservedSpelling_ListsTheLocal() => AssertOracle.EqualAsync(
        "f() { local path=q; local -p path; }; f");

    [SkippableFact]
    public Task NamerefToItself_ReservedSpelling_IsRefused() => AssertOracle.EqualAsync(
        "declare -n path=path 2>/dev/null; echo $?; declare -p path 2>/dev/null || echo none");
}
