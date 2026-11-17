using System;
using System.IO;
using PsBash.Core;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <see cref="ProgramFile"/>: how a resolved program file is started. The stand-in text and argv are ps-bash's own
/// surface (no bash equivalent); the behavior they produce is pinned against bash by
/// <c>Differential.Tests/ScriptFileExecutionDifferentialTests</c>.
/// </summary>
public class ProgramFileTests
{
    private static string TempDir(string tag) => Directory.CreateTempSubdirectory(tag).FullName;

    // Windows: a PATH hit it can only "open" (no PATHEXT extension, no #! line) is not a command; a #! script runs
    // through its interpreter; a PATHEXT program starts as it is.
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Plan_Windows_DocumentIsNotExecutable_ShebangRunsThroughItsInterpreter_ExeStartsAsIs()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PATHEXT and #! decide on Windows; Unix uses the execute bit.");
        string dir = TempDir("psbash-plan");
        try
        {
            string doc = Path.Combine(dir, "LICENSE"), md = Path.Combine(dir, "README.md");
            string script = Path.Combine(dir, "tool"), exe = Path.Combine(dir, "tool.EXE");
            File.WriteAllText(doc, "MIT License\n"); File.WriteAllText(md, "# readme\n");
            File.WriteAllText(script, "#!/bin/sh\necho hi\n"); File.WriteAllText(exe, "MZ");
            Assert.False(ProgramFile.Plan(doc)!.Runnable);
            Assert.False(ProgramFile.Plan(md)!.Runnable);
            var launch = ProgramFile.Plan(script)!;
            Assert.True(launch.Runnable);
            Assert.True(launch.Shell);
            Assert.Null(ProgramFile.Plan(exe));
            Assert.Null(ProgramFile.Plan(Path.Combine(dir, "missing"))); // unreadable: fails the way it always did
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Unix: the kernel runs #! itself; a file with no execute bit is "Permission denied" (PowerShell would hand it to
    // xdg-open and report 0).
    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void Plan_Unix_OnlyAFileWithNoExecuteBitNeedsAnAnswer()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes; Windows uses PATHEXT and #! instead.");
        if (OperatingSystem.IsWindows()) return; // tells the platform analyzer File.SetUnixFileMode is reachable only on Unix
        string dir = TempDir("psbash-xbit");
        try
        {
            string doc = Path.Combine(dir, "doc"), tool = Path.Combine(dir, "tool"), group = Path.Combine(dir, "group");
            foreach (var f in new[] { doc, tool, group }) File.WriteAllText(f, "#!/bin/sh\necho hi\n");
            File.SetUnixFileMode(doc, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(group, UnixFileMode.UserRead | UnixFileMode.GroupExecute);
            Assert.False(ProgramFile.Plan(doc)!.Runnable);
            Assert.Null(ProgramFile.Plan(tool));
            Assert.Null(ProgramFile.Plan(group));
            Assert.True(ProgramFile.HasExecuteBit(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The stand-in a resolved #! file runs as, in place of the resolved command.
    [Theory]
    [InlineData("/bin/sh", "Invoke-BashBash 'C:\\t\\it''s' @args")]
    [InlineData("/usr/bin/env bash", "Invoke-BashBash 'C:\\t\\it''s' @args")]
    [InlineData("/bin/bash -e", "Invoke-BashBash '-e' 'C:\\t\\it''s' @args")]
    [InlineData("/usr/bin/env -S python3 -u", "& 'python3' '-u' 'C:\\t\\it''s' @args")]
    [InlineData("/usr/local/bin/node", "& 'node' 'C:\\t\\it''s' @args")]
    public void StandInScript_RunsTheInterpreterWithTheFileThenTheArguments(string shebang, string run)
        => Assert.Equal(
            "$__psbash_in = @($input); if ($__psbash_in.Count -gt 0) { $__psbash_in | " + run + " } else { " + run + " }",
            ProgramFile.FromShebang(@"C:\t\it's", shebang).StandInScript("ignored"));

    [Fact]
    public void FromShebang_EnvWithNoProgram_IsPermissionDenied()
    {
        var launch = ProgramFile.FromShebang("/t/x", "/usr/bin/env");
        Assert.False(launch.Runnable);
        Assert.Equal(ProgramLaunch.PermissionDeniedScript("./x"), launch.StandInScript("./x"));
    }

    [Fact]
    public void PermissionDeniedScript_QuotesTheNameAndSets126()
        => Assert.Equal(
            "Write-Error -Message 'bash: /it''s/LICENSE: Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126",
            ProgramLaunch.PermissionDeniedScript("/it's/LICENSE"));

    // A site that must start a process itself (nohup): the launcher for a shell, the interpreter found on PATH
    // otherwise, then the #! options and the file.
    [Fact]
    public void CommandLine_ShellUsesTheLauncher_InterpreterIsSearched_MissingInterpreterIsNull()
    {
        var shell = ProgramFile.FromShebang("/t/s", "/bin/bash -e").CommandLine("LAUNCHER", []);
        Assert.Equal("LAUNCHER", shell.Program);
        Assert.Equal(new[] { "-e", "/t/s" }, shell.Args);

        var missing = ProgramFile.FromShebang("/t/p", "/usr/bin/env psbash-no-such-interpreter-xyz").CommandLine("LAUNCHER", [Path.GetTempPath()]);
        Assert.Null(missing.Program);
        Assert.Equal(new[] { "/t/p" }, missing.Args);

        Assert.Null(ProgramFile.FromShebang("/t/x", "/usr/bin/env").CommandLine("LAUNCHER", []).Program);
    }

    [Theory]
    [InlineData("./README.md", "/t/README.md", "./README.md")]
    [InlineData("hello", "/t/b/hello", "/t/b/hello")]
    public void Shown_IsTheWordAsTypedWithASlash_ElseTheFileFound(string typed, string path, string expected)
        => Assert.Equal(expected, ProgramFile.Shown(typed, path));

    [Fact]
    public void TryReadShebang_ReturnsTheLineWithoutCarriageReturn()
    {
        string dir = TempDir("psbash-shebang");
        try
        {
            string a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b");
            File.WriteAllText(a, "#!/usr/bin/env bash\r\necho hi\r\n"); File.WriteAllText(b, "echo no shebang\n");
            Assert.True(ProgramFile.TryReadShebang(a, out var sa));
            Assert.Equal("/usr/bin/env bash", sa);
            Assert.True(ProgramFile.TryReadShebang(b, out var sb));
            Assert.Null(sb);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
