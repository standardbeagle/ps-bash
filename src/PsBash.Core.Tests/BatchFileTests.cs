using System.Diagnostics;
using PsBash.Core.Platform;
using PsBash.Core.Runtime.Processes;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <see cref="BatchFile"/>: the <c>cmd.exe</c> command line that runs a <c>.cmd</c> / <c>.bat</c> so every argument
/// arrives as its literal text (CVE-2024-24576). Not a bash surface (bash has no batch files): bash's own guarantee
/// is the one pinned, that a quoted word reaches the program unchanged. Spec <c>program-files.md</c> §Batch files.
/// </summary>
public class BatchFileTests
{
    private const string Script = @"C:\tools\tool.cmd";

    private static string Line(params string[] args) => BatchFile.Arguments(Script, args);

    [Fact]
    public void ThePrefix_DisablesAutoRunAndDelayedExpansion_AndQuotesTheScript()
    {
        Assert.Equal("/e:ON /v:OFF /d /c \"\"C:\\tools\\tool.cmd\"\"", Line());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("C:/x/y.txt", "C:/x/y.txt")]
    [InlineData(@"C:\x\y", @"C:\x\y")]
    [InlineData("--flag=1", "\"--flag=1\"")]              // `=` separates arguments for cmd: quoted
    [InlineData("", "\"\"")]                              // kept, not dropped
    [InlineData("a b", "\"a b\"")]
    [InlineData("a&b", "\"a&b\"")]                        // never a second command
    [InlineData("a|b", "\"a|b\"")]
    [InlineData("a^b", "\"a^b\"")]
    [InlineData("a<b>c", "\"a<b>c\"")]
    [InlineData("(x)", "\"(x)\"")]
    [InlineData("!x!", "\"!x!\"")]
    [InlineData("x\"y", "\"x\"\"y\"")]                    // a quote is doubled
    [InlineData("a\\\"b", "\"a\\\\\"\"b\"")]              // and the backslashes before it
    [InlineData("tr\\", "\"tr\\\\\"")]                    // a trailing backslash cannot escape the closing quote
    [InlineData("%PATH%", "\"%%cd:~,%PATH%%cd:~,%\"")]     // %NAME% never expands
    [InlineData("é", "é")]
    public void AnArgument_IsQuotedUnlessEveryCharacterIsInert(string arg, string expected)
    {
        Assert.Equal("/e:ON /v:OFF /d /c \"\"C:\\tools\\tool.cmd\" " + expected + "\"", Line(arg));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\0b")]
    public void ALineBreak_IsRefused_NotTruncated(string arg)
    {
        Assert.Throws<ArgumentException>(() => Line("ok", arg));
    }

    [Fact]
    public void APercentInTheScriptPath_DoesNotExpand()
    {
        Assert.StartsWith("/e:ON /v:OFF /d /c \"\"C:\\100%%cd:~,%x%%cd:~,%\\t.cmd\"", BatchFile.Arguments(@"C:\100%x%\t.cmd", []));
    }

    /// <summary>
    /// The choke point every child goes through (<see cref="JobProcess"/>): a <c>.cmd</c> is never handed to
    /// <c>CreateProcess</c> as it is, where Windows would run it in <c>cmd.exe</c> with C runtime quoting and
    /// <c>a&amp;b</c> would run <c>b</c>.
    /// </summary>
    [Fact]
    public void JobProcess_StartsABatchFileThroughCmd_WithItsArgumentsEscaped()
    {
        string line = JobProcess.CommandLineFor(new JobProcessStart(Script, ["a&b", "%PATH%"]));
        Assert.Equal(WindowsCommandLine.Build(SystemTools.WindowsCommandProcessor, []) + " "
            + BatchFile.Arguments(Script, ["a&b", "%PATH%"]), line);
    }

    [Fact]
    public void JobProcess_RefusesAPreQuotedCommandLineForABatchFile()
    {
        Assert.Throws<ArgumentException>(() =>
            JobProcess.CommandLineFor(new JobProcessStart(@"C:\t\x.BAT", []) { RawArguments = "a&b" }));
    }

    [Fact]
    public void JobProcess_LeavesAnyOtherProgramAsItWas()
    {
        Assert.Equal(WindowsCommandLine.Build(@"C:\t\x.exe", ["a&b"]),
            JobProcess.CommandLineFor(new JobProcessStart(@"C:\t\x.exe", ["a&b"])));
    }

    /// <summary><see cref="BatchFile.Prepare"/>: a direct <c>Process.Start</c> of a <c>.cmd</c> given an ArgumentList
    /// (the pager, the REPL's direct launch, an assist provider).</summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Prepare_RewritesADirectStart_SoEachArgumentArrives()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        string dir = Directory.CreateTempSubdirectory("psbash-bat").FullName;
        try
        {
            var psi = new ProcessStartInfo(MakeTool(dir));
            foreach (var w in Words) psi.ArgumentList.Add(w);
            BatchFile.Prepare(psi);
            Assert.Equal(SystemTools.WindowsCommandProcessor, psi.FileName);
            Assert.Empty(psi.ArgumentList);
            AssertEachWordArrives(psi);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Prepare_RefusesAPreQuotedCommandLine_AndLeavesOtherProgramsAlone()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        Assert.Throws<ArgumentException>(() => BatchFile.Prepare(new ProcessStartInfo(@"C:\t\x.cmd", "a&b")));
        var exe = new ProcessStartInfo(@"C:\t\x.exe");
        exe.ArgumentList.Add("a&b");
        BatchFile.Prepare(exe);
        Assert.Equal(@"C:\t\x.exe", exe.FileName);
        Assert.Equal(["a&b"], exe.ArgumentList);
    }

    private static readonly string[] Words = ["plain", "a b", "a&b", "a|b", "a^b", "a<b>c", "(x)", "!PATH!", "%PATH%", "x\"y", "tr sp\\", ""];

    /// <summary>A batch file that hands <c>%*</c> to a program, as npm.cmd does; pwsh's <c>-File</c> binds the
    /// arguments after the file to <c>$args</c> as literal strings.</summary>
    private static string MakeTool(string dir)
    {
        string? pwsh = ExecutableLookup.Find("pwsh", (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        Skip.If(pwsh is null, "pwsh is not on PATH");
        File.WriteAllText(Path.Combine(dir, "argv.ps1"), "foreach ($a in $args) { '<' + $a + '>' }\n");
        string bat = Path.Combine(dir, "tool.cmd");
        File.WriteAllText(bat, "@echo off\r\n\"" + pwsh + "\" -NoProfile -NonInteractive -File \"%~dp0argv.ps1\" %*\r\n");
        return bat;
    }

    private static void AssertEachWordArrives(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding ??= System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding ??= System.Text.Encoding.UTF8;
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000));
        Assert.Equal("", stderr);
        Assert.Equal(Words.Select(a => "<" + a + ">"), stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>
    /// End to end, as npm.cmd does: the batch file hands <c>%*</c> to a program, which must see each argument exactly.
    /// pwsh's <c>-File</c> binds the arguments after the file to <c>$args</c> as literal strings.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void ThroughCmd_EachArgumentReachesTheProgramTheBatchFileRuns()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        string? pwsh = ExecutableLookup.Find("pwsh", (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        Skip.If(pwsh is null, "pwsh is not on PATH");
        string dir = Directory.CreateTempSubdirectory("psbash-bat 100%").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "argv.ps1"), "foreach ($a in $args) { '<' + $a + '>' }\n");
            string bat = Path.Combine(dir, "tool.cmd");
            File.WriteAllText(bat, "@echo off\r\n\"" + pwsh + "\" -NoProfile -NonInteractive -File \"%~dp0argv.ps1\" %*\r\n");
            string[] args = ["plain", "a b", "a&b", "a|b", "a^b", "a<b>c", "(x)", "!PATH!", "%PATH%", "x\"y", "tr sp\\", ""];

            var psi = new ProcessStartInfo(SystemTools.WindowsCommandProcessor, BatchFile.Arguments(bat, args))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));
            Assert.Equal("", stderr);
            Assert.Equal(args.Select(a => "<" + a + ">"), stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(0, p.ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
