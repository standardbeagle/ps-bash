using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// An EXIT trap must not change the script's exit status, and a bare <c>exit</c> inside it uses the status from
/// BEFORE the handler ran. Oracle: bash 5.2 (oils builtin-trap.spec "trap EXIT return status ignored",
/// "bare exit in trap EXIT uses the status from before the trap", "bare exit in a function called from trap EXIT").
/// </summary>
[Trait("Category", "Integration")]
public class TrapExitStatusEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static async Task<(int Exit, string Out)> RunAsync(string script)
    {
        var psi = PsBashTestProcess.Create(["-c", script], ipcEndpoint: IpcEndpoint);
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(60));
        return (exit, stdout.Replace("\r\n", "\n"));
    }

    [SkippableTheory]
    // The handler's `return 42` is ignored: the script's own status (0) stands.
    [InlineData("cleanup() { echo \"cleanup [$@]\"; return 42; }\ntrap 'cleanup x y z' EXIT", 0, "cleanup [x y z]\n")]
    // `true; exit` in the handler leaves with the status from before the trap, not `true`'s 0.
    [InlineData("trap 'true; exit' EXIT\nexit 3", 3, "")]
    // Same through a function the handler calls.
    [InlineData("cleanup() { echo cleanup; true; exit; }\ntrap cleanup EXIT\nfalse", 1, "cleanup\n")]
    // An ordinary handler keeps an explicit `exit 7`.
    [InlineData("trap 'echo bye' EXIT\necho work\nexit 7", 7, "work\nbye\n")]
    public async Task ExitTrap_DoesNotChangeTheScriptStatus(string script, int expectedExit, string expectedOut)
    {
        var (exit, stdout) = await RunAsync(script);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOut, stdout);
    }

    [SkippableFact]
    public async Task BareExit_OutsideAnyTrap_UsesTheLastCommandStatus()
    {
        var (exit, _) = await RunAsync("false\nexit");
        Assert.Equal(1, exit);
    }
}
