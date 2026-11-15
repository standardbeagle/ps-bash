using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Words that assign while they expand inside a pipe stage, the last stage of a lastpipe pipeline, pathname expansion
/// of redirect targets, and a named-fd here-string on a compound command. Recorded from bash 5.2 (cassettes).
/// </summary>
public class StageExpansionAndRedirectGlobTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"; ";
    private const string Done = "; cd /; rm -rf \"$W\"";

    /// <summary>
    /// Every pipe stage is a subshell: <c>${cmd=echo} hi | wc -l</c> and <c>: $((n=5)) | cat</c> assign nothing in the
    /// shell. An in-process stage expanded its words in the shell itself (oils pipeline "Evaluation of argv[0] in pipeline occurs in child").
    /// </summary>
    [SkippableFact]
    public Task Pipeline_AssigningWordInAStage_DoesNotAssignInTheShell()
        => Same("echo ${cmd=echo} hi | wc -l; : $((n=5)) | cat; echo x | echo ${m:=3} >/dev/null; echo \"cmd=${cmd-unset} n=${n-unset} m=${m-unset}\"");

    /// <summary>
    /// <c>shopt -s lastpipe</c>: the last stage runs in the shell, so an array it appends to is the shell's (oils blog1
    /// "Julia example"). The append landed in a child scope and the array stayed empty.
    /// </summary>
    [SkippableFact]
    public Task Lastpipe_ArrayAppendInTheLastStage_ReachesTheShell()
        => Same("shopt -s lastpipe; a=(); printf '%s\\n' x 'y z' | while read -r l; do a+=(\"$l\"); done; echo \"${#a[@]} ${a[1]}\"");

    /// <summary>
    /// A redirect target from an unquoted variable is pathname-expanded like any word: <c>&gt; two-$star</c> with
    /// star='*' writes two-bar; several matches is an ambiguous redirect
    /// (status 1). The value reached the file system as the literal two-* (oils redirect-multi).
    /// </summary>
    [SkippableFact]
    public Task Redirect_TargetFromAVariable_IsPathnameExpanded()
        => Same(Work + "touch two-bar m-1 m-2; star='*'; echo hi > two-$star; echo status=$?; cat two-bar; "
              + "{ echo x > m-$star; } 2>/dev/null; echo status=$?; cat m-1 m-2" + Done);

    /// <summary>
    /// The word after <c>&gt;&amp;</c> is pathname-expanded before it is read as a descriptor: with a file named 10,
    /// <c>&gt;&amp; 1*</c> is <c>&gt;&amp; 10</c> (oils redirect-multi "Non-file redirects don't respect glob args").
    /// </summary>
    [SkippableFact]
    public Task Redirect_DupTargetGlob_NamesTheMatchedDescriptor()
        => Same(Work + "touch 10; exec 10>&1; echo via-fd-10 >& 1*; echo done; exec 10>&-" + Done);

    /// <summary>
    /// <c>{ ...; } {abc}&lt;&lt;&lt; w</c> opens a descriptor of its own (10 up) holding the here-string, stores its
    /// number in abc and leaves it open after the group (oils toysh "{abc}&lt;&lt;&lt; walrus"); it was a parse error.
    /// </summary>
    [SkippableFact]
    public Task NamedFdHereString_OnABraceGroup_OpensADescriptor()
        => Same("{ cat <&$abc; } {abc}<<< walrus; echo \"abc ${abc:+set}\"; exec {abc}<&-");
}
