using System.Diagnostics;
using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class InteractiveShellTests
{
    [SkippableFact]
    public async Task InteractiveMode_LaunchesHostAndPassesThroughExitCode()
    {
        var psi = PsBashTestProcess.Create(["-i"]);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ps-bash");

        await process.StandardInput.WriteLineAsync("exit 42");
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        Assert.Equal(42, process.ExitCode);
    }

    [SkippableFact]
    public async Task InteractiveMode_DoesNotRequireCommand()
    {
        var psi = PsBashTestProcess.Create(["-i"]);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ps-bash");

        await process.StandardInput.WriteLineAsync("exit 0");
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
    }
}

public class AliasExpansionTests
{
    [Fact]
    public void ExpandAliases_NoAliases_ReturnsInputUnchanged()
    {
        var result = InteractiveShell.ExpandAliases("ls -la");
        Assert.Equal("ls -la", result);
    }

    [Fact]
    public void ExpandAliases_SimpleAlias_ExpandsFirstWord()
    {
        // This test relies on the static Aliases dictionary being populated
        // We can't easily test this in isolation without refactoring,
        // so we test through ProcessAliasCommand + ExpandAliases
        InteractiveShell.ProcessAliasCommand("alias ll='ls -la'");
        var result = InteractiveShell.ExpandAliases("ll /tmp");
        Assert.Equal("ls -la /tmp", result);
    }

    [Fact]
    public void ExpandAliases_UnknownCommand_ReturnsInputUnchanged()
    {
        InteractiveShell.ProcessAliasCommand("alias ll='ls -la'");
        var result = InteractiveShell.ExpandAliases("cat file.txt");
        Assert.Equal("cat file.txt", result);
    }

    [Fact]
    public void ExpandAliases_AliasWithPipe_ExpandsBeforePipe()
    {
        InteractiveShell.ProcessAliasCommand("alias lc='ls -la | wc -l'");
        var result = InteractiveShell.ExpandAliases("lc");
        Assert.Equal("ls -la | wc -l", result);
    }

    [Fact]
    public void ProcessAliasCommand_SetsAlias()
    {
        InteractiveShell.ProcessAliasCommand("alias gs='git status'");
        var result = InteractiveShell.ExpandAliases("gs");
        Assert.Equal("git status", result);
    }

    [Fact]
    public void ProcessAliasCommand_UnaliasRemovesAlias()
    {
        InteractiveShell.ProcessAliasCommand("alias temp='echo hi'");
        InteractiveShell.ProcessAliasCommand("unalias temp");
        var result = InteractiveShell.ExpandAliases("temp something");
        Assert.Equal("temp something", result);
    }

    [Fact]
    public void ProcessAliasCommand_NotAliasCommand_ReturnsOriginal()
    {
        var result = InteractiveShell.ProcessAliasCommand("ls -la");
        Assert.Equal("ls -la", result);
    }

    // ── source / . recognizer (TryGetInteractiveSourceTarget) ────────────────
    // Regression coverage for the interactive `source FILE` alias gap: only the
    // simple single-file form is intercepted (so its aliases reach the in-process
    // table); complex forms fall through to Invoke-BashSource.

    [Fact]
    public void TryGetInteractiveSourceTarget_SourceWithAbsolutePath_Resolves()
    {
        var abs = OperatingSystem.IsWindows() ? "C:/tmp/aliases.sh" : "/tmp/aliases.sh";
        var ok = InteractiveShell.TryGetInteractiveSourceTarget($"source {abs}", out var path);
        Assert.True(ok);
        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("aliases.sh", path);
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_DotFormWithAbsolutePath_Resolves()
    {
        var abs = OperatingSystem.IsWindows() ? "C:/tmp/dot.sh" : "/tmp/dot.sh";
        var ok = InteractiveShell.TryGetInteractiveSourceTarget($". {abs}", out var path);
        Assert.True(ok);
        Assert.EndsWith("dot.sh", path);
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_TildeDotfile_HasSeparatorBeforeName()
    {
        // The user-reported case: `source ~/.psbashrc`. Regression: the parser
        // consumes the '/' after '~', so the resolver must reinsert a separator.
        // Before the fix this produced `<home>.psbashrc` (no separator) — File.Exists
        // failed and the intercept silently fell through to Invoke-BashSource, so
        // source'd aliases never reached the interactive table.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.GetFullPath(Path.Combine(home, ".psbashrc"));
        var ok = InteractiveShell.TryGetInteractiveSourceTarget("source ~/.psbashrc", out var path);
        Assert.True(ok);
        Assert.Equal(expected, path);
        Assert.DoesNotContain("~", path);
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_TildeNestedPath_PreservesSeparators()
    {
        // A multi-segment tilde path must keep every separator, including the one
        // the parser dropped right after '~'.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.GetFullPath(Path.Combine(home, "psb-probe-dir", "probe.sh"));
        var ok = InteractiveShell.TryGetInteractiveSourceTarget("source ~/psb-probe-dir/probe.sh", out var path);
        Assert.True(ok);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_NonSourceCommand_ReturnsFalse()
    {
        Assert.False(InteractiveShell.TryGetInteractiveSourceTarget("echo hi", out _));
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_ExtraArguments_ReturnsFalse()
    {
        // Two operands (file + positional arg) must fall through to Invoke-BashSource.
        Assert.False(InteractiveShell.TryGetInteractiveSourceTarget("source a.sh extra", out _));
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_WithRedirect_ReturnsFalse()
    {
        Assert.False(InteractiveShell.TryGetInteractiveSourceTarget("source a.sh > out.txt", out _));
    }

    [Fact]
    public void TryGetInteractiveSourceTarget_VariablePath_ReturnsFalse()
    {
        // A path needing variable expansion is too complex to resolve here.
        Assert.False(InteractiveShell.TryGetInteractiveSourceTarget("source $RC", out _));
    }
}

public class ResolveCommandTests
{
    [Fact]
    public void ResolveCommand_AbsolutePath_ExistingFile_ReturnsPath()
    {
        var exe = Environment.GetCommandLineArgs()[0];
        if (!Path.IsPathRooted(exe)) exe = Path.GetFullPath(exe);
        var result = InteractiveShell.ResolveCommand(exe, null);
        Assert.Equal(exe, result);
    }

    [Fact]
    public void ResolveCommand_AbsolutePath_Nonexistent_ReturnsNull()
    {
        var result = InteractiveShell.ResolveCommand("C:\\nonexistent\\binary.exe", null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCommand_KnownSystemCommand_FindsExe()
    {
        // Use a command known to exist on all platforms where tests run.
        // dotnet is the most reliable because the test runner itself needs it.
        var result = InteractiveShell.ResolveCommand("dotnet", null);
        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void ResolveCommand_KnownSystemCommand_FindsCmdExtension()
    {
        var result = InteractiveShell.ResolveCommand("hostname", null);
        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void ResolveCommand_NonexistentCommand_ReturnsNull()
    {
        var result = InteractiveShell.ResolveCommand("definitely_not_a_real_command_xyz", null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCommand_ResolvesInWorkingDir()
    {
        using var tmp = new TempDir();
        // Use extensionless file on Unix, .cmd on Windows for cross-platform coverage
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tmp.Path, "test-script.cmd");
            File.WriteAllText(scriptPath, "@echo hi\n");
            var result = InteractiveShell.ResolveCommand("test-script", tmp.Path);
            Assert.NotNull(result);
            Assert.True(File.Exists(result));
            Assert.EndsWith("test-script.cmd", result, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var scriptPath = Path.Combine(tmp.Path, "test-script");
            File.WriteAllText(scriptPath, "#!/bin/sh\necho hi\n");
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserExecute);
            var result = InteractiveShell.ResolveCommand("test-script", tmp.Path);
            Assert.NotNull(result);
            Assert.True(File.Exists(result));
        }
    }

    [Fact]
    public void ResolveCommand_WithWorkDir_SearchesWorkDirFirst()
    {
        using var tmp = new TempDir();
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tmp.Path, "unique-test-cmd-1234.cmd");
            File.WriteAllText(scriptPath, "@echo hi\n");
            var result = InteractiveShell.ResolveCommand("unique-test-cmd-1234", tmp.Path);
            Assert.NotNull(result);
            Assert.True(File.Exists(result));
        }
        else
        {
            var scriptPath = Path.Combine(tmp.Path, "unique-test-cmd-1234");
            File.WriteAllText(scriptPath, "#!/bin/sh\necho hi\n");
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserExecute);
            var result = InteractiveShell.ResolveCommand("unique-test-cmd-1234", tmp.Path);
            Assert.NotNull(result);
            Assert.True(File.Exists(result));
        }
    }

    [SkippableFact]
    public void ResolveCommand_CmdFile_FoundOnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PATHEXT .cmd is Windows-only");
        using var tmp = new TempDir();
        var cmdPath = Path.Combine(tmp.Path, "myapp.cmd");
        File.WriteAllText(cmdPath, "@echo hi\n");
        var result = InteractiveShell.ResolveCommand("myapp", tmp.Path);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public void ResolveCommand_Ps1File_FoundOnWindows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PATHEXT .ps1 is Windows-only");
        using var tmp = new TempDir();
        var ps1Path = Path.Combine(tmp.Path, "myapp.ps1");
        File.WriteAllText(ps1Path, "Write-Host hi\n");
        var result = InteractiveShell.ResolveCommand("myapp", tmp.Path);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public void ResolveCommand_PrefersExeOverCmd()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PATHEXT priority is Windows-only");
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "myapp.cmd"), "@echo cmd\n");
        File.WriteAllText(Path.Combine(tmp.Path, "myapp.exe"), "fake");
        var result = InteractiveShell.ResolveCommand("myapp", tmp.Path);
        Assert.NotNull(result);
        Assert.EndsWith(".exe", result, StringComparison.OrdinalIgnoreCase);
    }

    private class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
