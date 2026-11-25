using System.Management.Automation;
using PsBash.Core;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="HostTerminal"/>: in a runspace whose host has a terminal, a program whose output goes straight to the host
/// and reads no pipeline input runs THERE (the host's <see cref="IHostTerminal"/>), with its status carried back - and
/// everything else runs on pipes exactly as before. The host here is a recorder; <c>/bin/sh</c>, by path, stands in for any program (a bare <c>sh</c> is ps-bash's own alias).
/// </summary>
public class HostTerminalTests
{
    /// <summary>Records each launch; exits with N when the argv holds <c>exit N</c>; declines when told to.</summary>
    private sealed class RecordingTerminal : IHostTerminal
    {
        public List<HostTerminalLaunch> Launches { get; } = [];
        public bool Decline { get; init; }

        public int? Run(HostTerminalLaunch launch, CancellationToken stop)
        {
            if (Decline) return null;
            Launches.Add(launch);
            var exit = launch.Arguments.LastOrDefault(a => a.StartsWith("exit ", StringComparison.Ordinal));
            return exit is null ? 0 : int.Parse(exit["exit ".Length..]);
        }
    }

    private static (PowerShell Pwsh, RecordingTerminal Terminal) Setup(bool decline = false)
    {
        Skip.If(OperatingSystem.IsWindows(), "sh is the stand-in program");
        var pwsh = PwshTestFixture.Create();
        var terminal = new RecordingTerminal { Decline = decline };
        HostTerminal.Register(pwsh.Runspace, terminal);
        pwsh.AddScript("[PsBash.Cmdlets.HostTerminalLookup]::Install($ExecutionContext.InvokeCommand)").Invoke();
        pwsh.Commands.Clear();
        return (pwsh, terminal);
    }

    private static List<string> Run(PowerShell pwsh, string script)
    {
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        var output = pwsh.AddScript(script).Invoke().Select(o => o?.ToString() ?? "").ToList();
        pwsh.Commands.Clear();
        return output;
    }

    private static object? LastExitCode(PowerShell pwsh) => pwsh.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");

    [SkippableFact]
    public void AProgramWritingToTheHost_RunsOnTheTerminal_WithItsStatus()
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;

        Run(pwsh, "/bin/sh -c 'exit 3'");

        var launch = Assert.Single(terminal.Launches);
        Assert.Equal("/bin/sh", launch.Path);
        Assert.Equal(["-c", "exit 3"], launch.Arguments);
        Assert.Equal(3, LastExitCode(pwsh));
    }

    [SkippableTheory]
    [InlineData("$x = /bin/sh -c 'echo captured'; $x", "captured")]
    [InlineData("$(/bin/sh -c 'echo subexpr')", "subexpr")]
    [InlineData("/bin/sh -c 'echo piped' | ForEach-Object { $_ }", "piped")]
    [InlineData("'input' | /bin/sh -c cat", "input")]
    public void CapturedPipedOrFedOutput_KeepsItsPipes(string script, string expected)
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;

        Assert.Equal([expected], Run(pwsh, script));
        Assert.Empty(terminal.Launches);
    }

    [SkippableFact]
    public void EveryProgramOfALoopThatWritesToTheHost_RunsOnTheTerminal()
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;

        Run(pwsh, "foreach ($i in 1, 2) { /bin/sh -c \"exit 0\" }; if ($true) { /bin/sh -c 'exit 0' }; function f { /bin/sh -c 'exit 0' }; f");

        Assert.Equal(4, terminal.Launches.Count);
    }

    [SkippableTheory]
    [InlineData("/bin/sh -c 'exit 3' && 'and'", new string[0])]
    [InlineData("/bin/sh -c 'exit 3' || 'or'", new[] { "or" })]
    [InlineData("/bin/sh -c 'exit 0' && 'and'", new[] { "and" })]
    public void PowerShellChains_SeeTheProgramsStatusIn_DollarQuestion(string script, string[] expected)
    {
        var (pwsh, _) = Setup();
        using var __ = pwsh;

        Assert.Equal(expected, Run(pwsh, script));
    }

    [SkippableTheory]
    [InlineData("true && /bin/sh -c 'exit 0'", 1)]
    [InlineData("/bin/sh -c 'exit 2' && /bin/sh -c 'exit 0'", 1)]
    [InlineData("/bin/sh -c 'exit 2' || /bin/sh -c 'exit 0'", 2)]
    [InlineData("for i in 1 2 3; do /bin/sh -c 'exit 0'; done", 3)]
    [InlineData("env BT_HOST_TERMINAL=1 /bin/sh -c 'exit 0'", 1)]
    [InlineData("x=$(/bin/sh -c 'echo no'); echo \"$x\" | cat", 0)]
    [InlineData("command /bin/sh -c 'exit 0'", 1)]
    [InlineData("/bin/sh -c 'echo x' > /dev/null", 0)]
    [InlineData("/bin/sh -c 'echo x' | cat", 0)]
    [InlineData("/bin/sh -c 'echo x' 2>&1", 0)]
    public void BashLines_RouteTheSamePrograms(string bash, int launches)
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;

        Run(pwsh, BashTranspiler.Transpile(bash));

        Assert.Equal(launches, terminal.Launches.Count);
    }

    [SkippableFact]
    public void AnEnvPrefix_ReachesTheProgramsEnvironment()
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;

        Run(pwsh, BashTranspiler.Transpile("env BT_HOST_TERMINAL=yes /bin/sh -c 'exit 0'"));

        var launch = Assert.Single(terminal.Launches);
        Assert.Equal("yes", launch.Environment["BT_HOST_TERMINAL"]);
    }

    // An embedding runs one runspace per terminal: the stand-in for a program runs in the runspace that looked it up, so its
    // status lands there, never in the runspace that first ran the same program.
    [SkippableFact]
    public void TheSameProgramInTwoRunspaces_SetsEachRunspacesOwnStatus()
    {
        var (a, ta) = Setup();
        var (b, tb) = Setup();
        using var _ = a;
        using var __ = b;

        Run(a, "/bin/sh -c 'exit 3'");
        Run(b, "/bin/sh -c 'exit 4'");
        Run(a, "/bin/sh -c 'exit 5'");

        Assert.Equal(5, LastExitCode(a));
        Assert.Equal(4, LastExitCode(b));
        Assert.Equal(2, ta.Launches.Count);
        Assert.Single(tb.Launches);
    }

    // An embedding (BeagleTerm's session server) runs the session in-process: a nested `sh`/`bash` hands its status back
    // as bash does, with or without a host terminal. REPORTED: `sh -c "exit 7"` read 0 there.
    [SkippableTheory]
    [InlineData(true, "sh -c \"exit 7\"; echo \"$?\"")]
    [InlineData(false, "sh -c \"exit 7\"; echo \"$?\"")]
    [InlineData(true, "bash -c 'exit 7'; echo \"$?\"")]
    [InlineData(true, "sh -c 'exit 7' || echo \"or $?\"")]
    public void ANestedShellsStatus_ReachesTheSession(bool withTerminal, string bash)
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;
        if (!withTerminal) HostTerminal.Unregister(pwsh.Runspace);

        var output = Run(pwsh, BashTranspiler.Transpile(bash));

        Assert.Equal(bash.Contains("||") ? "or 7" : "7", output.LastOrDefault());
        Assert.Empty(terminal.Launches);
    }

    [SkippableFact]
    public void ATerminalThatDeclines_LeavesTheProgramOnPipes()
    {
        var (pwsh, terminal) = Setup(decline: true);
        using var _ = pwsh;

        Assert.Equal(["hi"], Run(pwsh, "/bin/sh -c 'echo hi'"));
        Assert.Empty(terminal.Launches);
    }

    [SkippableFact]
    public void ARunspaceWithNoTerminal_IsUntouched()
    {
        var (pwsh, terminal) = Setup();
        using var _ = pwsh;
        HostTerminal.Unregister(pwsh.Runspace);

        Assert.Equal(["hi"], Run(pwsh, "/bin/sh -c 'echo hi'"));
        Assert.Empty(terminal.Launches);
    }
}

