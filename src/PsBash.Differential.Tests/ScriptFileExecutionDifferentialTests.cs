using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Running script files the way bash does. Before: <c>echo x | bash s.sh</c> gave the script no stdin (the launcher
/// forwarded it for <c>-c</c> only); on Windows a <c>#!</c> file with no extension opened the "Pick an app" dialog
/// and reported 0; <c>PATH=d:$PATH</c> made one entry <c>d:C:\…</c> (Windows separates with <c>;</c>), hiding both
/// directories; and <c>type -P</c> printed <c>hello</c> for a PATH hit instead of <c>d/hello</c>.
/// stdout + exit status + the final tree are compared; stderr is not.
/// </summary>
public class ScriptFileExecutionDifferentialTests
{
    private const string Setup =
        "mkdir d; printf '#!/bin/sh\\necho \"args: $# [$1] [$2]\"\\nwhile read -r l; do echo \"in: $l\"; done\\nexit 7\\n' > d/hello; "
        + "chmod +x d/hello; printf 'read -r l; echo \"got $l\"\\n' > r.sh; printf 'plain\\n' > doc; chmod -x doc";

    private static Task Eq(string command) => EqualAsync(Setup, command);

    [SkippableTheory]
    [InlineData("echo piped | bash r.sh; bash r.sh < doc; echo st=$?")]
    [InlineData("printf 'a\\nb\\n' | ./d/hello x 'y z'; echo st=$?")]
    [InlineData("PATH=\"d:$PATH\"; echo q | hello; echo st=$?; x=$(hello </dev/null); echo \"cap=$x\"")]
    [InlineData("PATH=d:$PATH; type -P hello; type -a hello | head -1; type -t hello")]
    [InlineData("./doc; echo st=$?")]
    public Task ScriptFile_RunsLikeBash(string command) => Eq(command);
}
