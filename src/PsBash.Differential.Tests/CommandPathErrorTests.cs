using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A command word with a slash names a file, so a failure is the exec error, not "command not found": a missing file
/// is "No such file or directory" (127), a directory "Is a directory" (126), and a component over 255 bytes in an
/// existing directory "File name too long" (126; oils command_ "Name too long"). Every one was "command not found"
/// (127). Recorded from bash 5.2 (cassette).
/// </summary>
public class CommandPathErrorTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"; ";
    private const string Done = "; cd /; rm -rf \"$W\"";

    [SkippableFact]
    public Task SlashCommandWord_ThatCannotRun_ReportsTheExecError()
        => AssertOracle.EqualAsync(Work + "mkdir d; long=$(printf '%0300d' 0); "
            + "./nosuch 2>e; echo \"missing $? $(grep -c 'No such file or directory' e)\"; "
            + "./d 2>e; echo \"dir $? $(grep -c 'Is a directory' e)\"; "
            + "./$long 2>e; echo \"long $? $(grep -c 'File name too long' e)\"; "
            + "./x/$long/y 2>e; echo \"past-missing $? $(grep -c 'No such file or directory' e)\"; "
            + "nosuch_cmd_q 2>e; echo \"bare $? $(grep -c 'command not found' e)\"" + Done,
            timeout: TimeSpan.FromSeconds(30));
}
