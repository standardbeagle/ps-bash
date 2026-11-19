using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A child run in another directory (<c>env -C DIR</c>, <c>find -execdir</c>) sees that directory through every
/// view: <c>pwd</c>, <c>ls</c>, a program's cwd. $PWD itself is not changed (GNU env does not update it), and the
/// caller's directory is back afterwards. REGRESSION: <c>env -C sub pwd</c> printed the caller's directory (only the
/// PowerShell location moved), and the missing-directory error quoted the name with curly quotes (GNU: quoteaf).
/// Paths are compared by their last component so the Windows and Linux absolute forms do not matter.
/// </summary>
public class ChildDirectoryDifferentialTests
{
    [SkippableFact]
    public Task EnvChdir_And_FindExecdir_RunTheChildInTheDirectory() =>
        AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\"\nmkdir -p sub/in && touch sub/in/x\n"
            + "basename \"$(env -C sub pwd)\"\nenv -C sub ls\nenv --chdir=sub/in pwd | sed 's|.*/||'\n"
            + "env -C nosuch pwd; echo \"rc=$?\"\n"
            + "find . -name x -execdir pwd \\; | sed 's|.*/||'\n"
            + "[ \"$(env -C sub printenv PWD)\" = \"$PWD\" ] && echo 'PWD kept'\n"
            + "[ \"$(pwd)\" = \"$PWD\" ] && echo back");
}
