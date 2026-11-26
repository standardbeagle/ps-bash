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
