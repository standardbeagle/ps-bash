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
    // A #! script's stdin is the producer's exact bytes: an unterminated last line stays unterminated (read fails on
    // it), and a record already ending in a newline gains no second one (the stand-in was handed text strings).
    [InlineData("echo -n x | ./d/hello; printf 'p\\nq' | ./d/hello; PATH=\"d:$PATH\"; printf 'r\\n' | hello; echo st=$?")]
    [InlineData("PATH=\"d:$PATH\"; echo q | hello; echo st=$?; x=$(hello </dev/null); echo \"cap=$x\"")]
    [InlineData("PATH=d:$PATH; type -P hello; type -a hello | head -1; type -t hello")]
    [InlineData("./doc; echo st=$?")]
    // The script's argv is the one typed: `-e` was taken by the bash cmdlet's binder (dropped), `-x:y` split, `--`
    // consumed; in a background job `-e` was "ambiguous" (the job's program stand-in splatted $args).
    [InlineData("./d/hello -e -x:y </dev/null; ./d/hello -- -e </dev/null; ./d/hello -e x </dev/null & wait; echo st=$?")]
    public Task ScriptFile_RunsLikeBash(string command) => Eq(command);

    // `command FILE` and `nohup FILE` resolved the file themselves and handed it straight to the OS (or to PowerShell
    // as a resolved command), skipping the #! dispatch only a NAME lookup applied: on Windows `command ./d/hello 2>&1`
    // opened the "Open with" dialog and hung, `nohup ./d/hello` failed ("not a valid Win32 application") and wrote
    // nothing, and `nohup ./doc` left no message in its stderr file. Both now ask ProgramFile (program-files.md).
    private const string BadInterpreter = "printf '#!/nonexistent/interp\\necho hi\\n' > d/bad; chmod +x d/bad; ";

    [SkippableTheory]
    [InlineData("printf 'a\\nb\\n' | command ./d/hello x 'y z'; echo st=$?")]
    [InlineData("command ./d/hello q </dev/null 2>&1; echo st=$?")]
    [InlineData("PATH=\"d:$PATH\"; command hello p </dev/null; echo st=$?")]
    [InlineData("command ./doc; echo st=$?; command ./doc 2>/dev/null; echo st=$?")]
    [InlineData(BadInterpreter + "command ./d/bad; echo st=$?; ./d/bad; echo st=$?")]
    public Task Command_ScriptFile_RunsLikeBash(string command) => Eq(command);

    // `nohup CMD >log 2>&1 &` returns before its child finishes: wait (bounded) for the line the script writes. The
    // file then holds exactly that output, compared in the tree snapshot. (A foreground `nohup CMD` is a separate,
    // older difference: GNU waits for CMD and returns its status, ps-bash's nohup always detaches and returns 0.)
    [SkippableTheory]
    [InlineData("nohup ./d/hello n >out 2>&1 </dev/null & "
        + "i=0; until grep -q args out 2>/dev/null || [ $i -ge 400 ]; do sleep 0.1; i=$((i+1)); done; cat out")]
    [InlineData("nohup ./doc >o 2>e </dev/null; echo st=$?; cat e")]
    [InlineData(BadInterpreter + "nohup ./d/bad >o 2>e </dev/null; echo st=$?; cat e")]
    public Task Nohup_ScriptFile_RunsLikeGnuNohup(string command) => EqualAsync(Setup, command, timeout: TimeSpan.FromSeconds(90));
}
