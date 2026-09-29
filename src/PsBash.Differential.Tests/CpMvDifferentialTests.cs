using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for the cp/mv data-loss fixes: each script builds a small tree in a
/// scratch dir, runs the operation, and prints the exit status plus the resulting tree
/// (<c>find . | sort</c>) and file contents. Real bash and ps-bash must agree byte for byte.
///
/// Diagnostics go to <c>2&gt;/dev/null</c>: GNU prints operands as typed while ps-bash prints
/// the resolved absolute path, and message wording is not the contract here — exit status and
/// what survives on disk are (the bugs were silent deletions).
/// </summary>
public class CpMvDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // Runs `body` in a fresh scratch dir; prints the tree and cleans up.
    private static string InTree(string body) =>
        "d=$(mktemp -d) && cd \"$d\" && { " + body + "; find . | sort; }; cd /; rm -rf \"$d\"";

    [SkippableFact] public Task Mv_SourceIntoItsOwnParent_KeepsSource() =>
        Eq(InTree("mkdir -p p/src && echo a > p/src/f; mv p/src p 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Mv_DirOntoNonEmptyDir_Refused() =>
        Eq(InTree("mkdir -p s t/s && echo k > t/s/keep; mv s t 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Mv_DirOntoEmptyDir_Replaces() =>
        Eq(InTree("mkdir -p s t/s && echo a > s/f; mv s t 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Mv_DirOntoFile_Refused() =>
        Eq(InTree("mkdir dd && echo x > ff; mv dd ff 2>/dev/null; echo rc=$?; cat ff"));

    [SkippableFact] public Task Mv_DirIntoItsOwnSubdir_Refused() =>
        Eq(InTree("mkdir -p s/sub; mv s s/sub 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Mv_SeveralSourcesToMissingDest_MovesNothing() =>
        Eq(InTree("echo 1 > m1; echo 2 > m2; mv m1 m2 nope 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Cp_RecursiveForce_KeepsDestinationOnlyFiles() =>
        Eq(InTree("mkdir -p src/proj dst/proj && echo n > src/proj/new && echo old > dst/proj/only && echo o > dst/proj/new; " +
                  "cp -rf src/proj dst 2>/dev/null; echo rc=$?; cat dst/proj/new dst/proj/only"));

    [SkippableFact] public Task Cp_SeveralSourcesToMissingDest_Errors() =>
        Eq(InTree("echo a > a; echo b > b; cp a b result 2>/dev/null; echo rc=$?"));

    [SkippableFact] public Task Cp_SeveralSourcesToFile_Errors() =>
        Eq(InTree("echo a > a; echo b > b; echo r > result; cp a b result 2>/dev/null; echo rc=$?; cat result"));

    [SkippableFact] public Task Cp_RecursiveNoClobber_TraversesExistingSubtree() =>
        Eq(InTree("mkdir -p s/x t/s/x && echo new1 > s/x/f1 && echo new2 > s/x/f2 && echo old1 > t/s/x/f1; " +
                  "cp -rn s t 2>/dev/null; echo rc=$?; cat t/s/x/f1 t/s/x/f2"));

    [SkippableFact] public Task Cp_FileOntoItself_Fails() =>
        Eq(InTree("echo a > sf; cp sf sf 2>/dev/null; echo rc=$?; cat sf"));

    // Deliberately NOT here: `cp -r se se/x`. Both refuse with exit 1, but GNU creates se/x/se (and
    // se/x/se/x) before it notices the recursion; ps-bash refuses up front and writes nothing.
    // Covered by Cp_DirIntoItself_RefusesAndDoesNotRecurse in the Cmdlets tests.
}
