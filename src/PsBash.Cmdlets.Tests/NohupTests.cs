using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>`nohup`: GNU exit codes (125 own failure, 127 not found); redirects arrive as private --psbash-* flags.</summary>
public class NohupTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public NohupTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private int Code(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript($"Invoke-BashNohup {args} 2>$null | Out-Null; $global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        return (int)r[0].BaseObject;
    }

    [Fact] public void No_operand_is_125() => Assert.Equal(125, Code(""));
    [Fact] public void Unknown_command_is_127() => Assert.Equal(127, Code("'--psbash-out' '/dev/null' 'psbash-no-such-cmd-xyz'"));

    /// <summary>
    /// A bare name is looked up in $PATH only, never the working directory: bash's `nohup zzplant` beside an executable
    /// ./zzplant fails "No such file or directory", 127 (checked by hand against bash 5.2 / coreutils nohup). nohup
    /// searched the working directory first, so a cloned repo could plant a program, or the interpreter of a #! script,
    /// that ran in place of the real one.
    /// </summary>
    [Fact]
    public void A_program_in_the_working_directory_is_not_run_by_bare_name()
    {
        string dir = Directory.CreateTempSubdirectory("psbash-nohup-plant-").FullName;
        try
        {
            const string name = "psbash-planted-nohup-7310";
            string marker = Path.Combine(dir, "ran");
            if (OperatingSystem.IsWindows())
                File.WriteAllText(Path.Combine(dir, name + ".cmd"), "@echo planted> \"" + marker + "\"\r\n");
            else
            {
                string script = Path.Combine(dir, name);
                File.WriteAllText(script, "#!/bin/sh\necho planted > '" + marker + "'\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var pwsh = _fixture.AcquireFresh();
            string here = PsBash.Core.Parser.PsBuild.SingleQuote(dir);
            var r = pwsh.AddScript($"Push-Location -LiteralPath {here}; try {{ Invoke-BashNohup '--psbash-out' '/dev/null' '{name}' 2>$null | Out-Null; $global:LASTEXITCODE }} finally {{ Pop-Location }}").Invoke();
            pwsh.Commands.Clear();
            Assert.Equal(127, (int)r[0].BaseObject);
            Assert.False(File.Exists(marker), "the planted program ran");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
