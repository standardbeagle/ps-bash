using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="ProgramLookup"/> and the stdin of a program started outside the foreground (<see cref="NativeStdinBridge"/>).
/// These pin ps-bash's own surface (a lookup hook's stand-in, the process stdin); behavior vs bash is Differential
/// <c>RawBytesDifferentialTests</c> and Shell.Tests <c>JobStdinEndToEndTests</c> (qa-rubric Directive 1).
/// </summary>
public class ProgramLookupTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ProgramLookupTests(SharedPwshFixture fixture) => _fixture = fixture;

    // A program that exists on every platform the suite runs on.
    private static readonly string Program = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
        : "/bin/sh";

    // The premise of Core.Tests ProgramLookupGuardTests: InvokeCommand.GetCommand runs the lookup hook (and so may see a
    // stand-in), the Get-Command cmdlet does not; ProgramLookup sees the program through either. The hook here stands
    // in as ps-bash's do (GetVerbFallbackGuard, JobProgramLookup): not while NativeRunner.LookingUpRaw.
    [Fact]
    public void ALookupHooksStandIn_IsSeenByGetCommandOnly_AndProgramLookupSeesPastIt()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("""
            param($p)
            $ExecutionContext.InvokeCommand.PostCommandLookupAction = {
                param($n, $e)
                if (-not [PsBash.Core.NativeRunner]::LookingUpRaw -and $e.Command -is [System.Management.Automation.ApplicationInfo]) {
                    $e.CommandScriptBlock = { 'standin' } } }
            try {
                (Get-Command $p).CommandType.ToString()
                $ExecutionContext.InvokeCommand.GetCommand($p, 'All').CommandType.ToString()
                $plain = [PsBash.Cmdlets.ProgramLookup]::Plain($ExecutionContext, $p, [string[]]@('x'))
                if ($plain) { $plain.CommandType.ToString() } else { 'null' }
            } finally { $ExecutionContext.InvokeCommand.PostCommandLookupAction = $null }
            """).AddArgument(Program);
        var result = pwsh.Invoke().Select(o => o.ToString()).ToArray();
        Assert.False(pwsh.HadErrors, string.Join("\n", pwsh.Streams.Error));
        Assert.Equal("Application", result[0]);
        Assert.Equal("Function", result[1]);
        // Null only while another test keeps an exported scalar with an invalid byte (ChildNeedsExactBytes).
        Assert.True(result[2] == "Application" || (result[2] == "null" && ProcessScalars.KeepsAnyExported), result[2]);
    }

    // The shell's stand-in sends a program whose argv has a byte the launcher's UTF-8 would turn into U+FFFD to
    // Invoke-BashNative. REGRESSION: `s=$'\xff'; /usr/bin/printf %s "$s"` wrote ef bf bd.
    [SkippableFact]
    public void Plain_AnArgumentWithAnInvalidByte_IsNotForPowerShellsLauncher()
    {
        Skip.If(ProcessScalars.EnvironmentHoldsEveryString, "a child gets UTF-16 here: no byte is lost by PowerShell's launcher");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("param($p, $a) [PsBash.Cmdlets.ProgramLookup]::Plain($ExecutionContext, $p, [string[]]@($a)) -eq $null")
            .AddArgument(Program).AddArgument("a" + RawBytes.ToMarker(0xFF));
        Assert.True((bool)pwsh.Invoke().Single().BaseObject);
    }

    // Outside the foreground (a job's runspace has its own scalars) the PROCESS stdin is never swapped. REGRESSION:
    // `/usr/bin/sleep 3 & sleep 1; /usr/bin/cat` under a launcher stdin: the job's swap gave cat the job's empty pipe.
    [Fact]
    public void Enter_WithTheScalarsOfAJob_LeavesTheProcessStdin()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(BashJobRunspaces).TypeHandle);
        using var runspace = RunspaceFactory.CreateRunspace();
        var previous = Runspace.DefaultRunspace;
        Runspace.DefaultRunspace = runspace;
        try
        {
            using (ScalarOverlay.ForCommand(ignoreEnvironment: false, [], []).AttachScoped(runspace))
            {
                Assert.NotNull(ScalarOverlay.Current);
                Assert.Null(NativeStdinBridge.Enter(new StdinCursor()));
            }
        }
        finally { Runspace.DefaultRunspace = previous; }
    }

    // A program started by ps-bash outside the foreground reads its compound stdin through a pipe of its own, and what
    // it did not read goes back, as through the process-stdin bridge (bash: `{ head -c1; cat; } < f` gives h, then i).
    [SkippableFact]
    public void ForChild_TheChildReadsItsPipe_AndTheRestGoesBack()
    {
        Skip.If(OperatingSystem.IsWindows(), "/usr/bin/head -c1 reads one byte; the bridge itself is the same on Windows");
        var cursor = new StdinCursor();
        cursor.Enqueue(BashRuntime.TextRecord("abc", unterminated: true));
        var bridge = NativeStdinBridge.ForChild(cursor);
        Assert.NotNull(bridge);
        string output;
        var psi = new ProcessStartInfo("/usr/bin/head") { ArgumentList = { "-c1" } };
        using (var child = ChildStdio.Start(psi, new ChildStdioMode(Stdin: bridge!.ChildStdin)))
        {
            Assert.Null(child.StandardInput);
            output = child.StandardOutput.ReadToEnd();
            Assert.True(child.WaitForExit(30_000), "head never exited");
        }
        bridge.Exit();
        Assert.Equal("a", output);
        Assert.True(cursor.TryDequeue(out var rest));
        Assert.Equal("bc", BashRuntime.GetBashText(rest));
        Assert.False(cursor.TryDequeue(out _));
    }
}
