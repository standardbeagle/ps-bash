using System.Management.Automation;
using PsBash.Core;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A Windows batch file (<c>npm.cmd</c>, <c>npx.cmd</c>) gets each argument as the literal word bash gave it: PowerShell's
/// launcher passed <c>'a&amp;b'</c> to <c>cmd.exe</c> unquoted, which ran <c>b</c> (CVE-2024-24576). bash's guarantee,
/// that a quoted word reaches the program unchanged, is the one pinned; there is no batch file for the bash oracle to run,
/// so the assertions are hand-written. The batch file hands <c>%*</c> to a program, as npm.cmd does; pwsh's
/// <c>-File</c> binds the arguments after the file to <c>$args</c> as literal strings. Spec <c>program-files.md</c>
/// §Batch files.
/// </summary>
[Trait("Platform", "Windows")]
public class BatchFileLaunchTests
{
    private static readonly string[] Words = ["plain", "a b", "a&b", "a|b", "a^b", "a<b>c", "(x)", "%PATH%", "x\"y", "tr sp\\", ""];

    /// <summary>The words above as bash single-quoted words (a <c>'</c> in none of them).</summary>
    private static string BashWords => string.Join(" ", Words.Select(w => "'" + w + "'"));

    private static string Expected => string.Join("\n", Words.Select(w => "<" + w + ">"));

    private static (string Dir, string Tool) MakeTool()
    {
        string? pwsh = ExecutableLookup.Find("pwsh", (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        Skip.If(pwsh is null, "pwsh is not on PATH");
        string dir = Directory.CreateTempSubdirectory("psbash-bat").FullName;
        File.WriteAllText(Path.Combine(dir, "argv.ps1"), "foreach ($a in $args) { '<' + $a + '>' }\n");
        string tool = Path.Combine(dir, "tool.cmd");
        File.WriteAllText(tool, "@echo off\r\n\"" + pwsh + "\" -NoProfile -NonInteractive -File \"%~dp0argv.ps1\" %*\r\nexit /b 3\r\n");
        return (dir, BashPath.ToShell(tool));
    }

    private static PowerShell Session()
    {
        var pwsh = PwshTestFixture.Create(ShellSecretsSession.UseShellEnvironmentDrive);
        pwsh.AddScript("[PsBash.Cmdlets.ShellSecretsSession]::Install($ExecutionContext.InvokeCommand)").Invoke();
        pwsh.Commands.Clear();
        return pwsh;
    }

    private static string Bash(PowerShell pwsh, string bash)
    {
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        var output = pwsh.AddScript(BashTranspiler.Transpile(bash)).Invoke().Select(o => o?.ToString() ?? "");
        pwsh.Commands.Clear();
        return string.Join("\n", output).TrimEnd('\n').Replace("\r", "");
    }

    [SkippableTheory]
    [InlineData("{0} {1}; echo \"status=$?\"")]            // a command word (the lookup hook)
    [InlineData("{0} {1} 2>&1; echo \"status=$?\"")]       // stderr merged (Invoke-BashNative)
    [InlineData("command {0} {1}; echo \"status=$?\"")]    // run by name (InnerCommand)
    [InlineData("env {0} {1}; echo \"status=$?\"")]
    public void ABatchFile_GetsEachWordLiterally_AndItsStatus(string shape)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        var (dir, tool) = MakeTool();
        try
        {
            using var pwsh = Session();
            Assert.Equal(Expected + "\nstatus=3", Bash(pwsh, string.Format(shape, "'" + tool + "'", BashWords)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [SkippableFact]
    public void ALineBreakInAnArgument_IsRefused_NotRunAsASecondLine()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        var (dir, tool) = MakeTool();
        try
        {
            using var pwsh = Session();
            string output = Bash(pwsh, $"'{tool}' $'one\\necho INJECTED' 2>&1; echo \"status=$?\"");
            Assert.DoesNotContain("<", output);
            Assert.DoesNotContain("\nINJECTED", "\n" + output);
            Assert.EndsWith("status=126", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [SkippableFact]
    public void ABatchFile_GetsTheSessionsGrantedSecret()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "batch files are Windows-only");
        string name = "PSB_SECRET_BAT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string dir = Directory.CreateTempSubdirectory("psbash-bat").FullName;
        try
        {
            string tool = Path.Combine(dir, "key.cmd");
            File.WriteAllText(tool, "@echo off\r\necho key=%" + name + "%\r\n");
            var secrets = new ShellSecrets();
            secrets.Set(name, "sk-canary-bat");
            var pwsh = PwshTestFixture.Create(ShellSecretsSession.UseShellEnvironmentDrive);
            using (pwsh)
            {
                ShellSecrets.Register(pwsh.Runspace, secrets);
                pwsh.AddScript("[PsBash.Cmdlets.ShellSecretsSession]::Install($ExecutionContext.InvokeCommand)").Invoke();
                Assert.Equal("key=sk-canary-bat", Bash(pwsh, $"'{BashPath.ToShell(tool)}'"));
                Assert.Null(Environment.GetEnvironmentVariable(name));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
