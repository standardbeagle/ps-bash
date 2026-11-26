using System.Management.Automation;
using PsBash.Core;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="ShellSecrets"/>: a secret an embedding leased to ONE session runspace is read there, handed to exactly the
/// children it is granted to, listed masked, and never enters the process environment that every session of the host
/// process shares. ps-bash-specific surface (bash has no per-session secret store), so hand-written assertions; the bash
/// semantics they lean on (export, <c>NAME=v cmd</c>, <c>env -i</c>, subshells) are bash's. <c>/bin/sh</c>, by path,
/// stands in for any program (a bare <c>sh</c> is ps-bash's own nested shell). Spec: environment-model.md section 13.
/// </summary>
public class ShellSecretsTests
{
    private const string Value = "sk-canary-7f3a";

    private static PowerShell Session(ShellSecrets? secrets)
    {
        var pwsh = PwshTestFixture.Create(ShellSecretsSession.UseShellEnvironmentDrive);
        if (secrets is not null) ShellSecrets.Register(pwsh.Runspace, secrets);
        pwsh.AddScript("[PsBash.Cmdlets.ShellSecretsSession]::Install($ExecutionContext.InvokeCommand)").Invoke();
        pwsh.Commands.Clear();
        return pwsh;
    }

    private static string Bash(PowerShell pwsh, string bash) => Ps(pwsh, BashTranspiler.Transpile(bash));

    private static string Ps(PowerShell pwsh, string script)
    {
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        var output = pwsh.AddScript(script).Invoke().Select(o => o?.ToString() ?? "");
        pwsh.Commands.Clear();
        return string.Join("\n", output).TrimEnd('\n');
    }

    private static string Unique(string stem) => "PSB_SECRET_" + stem + "_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public void ASecret_IsReadInItsOwnSessionOnly_AndNeverEntersTheProcessEnvironment()
    {
        var name = Unique("READ");
        var secrets = new ShellSecrets();
        secrets.Set(name, Value);
        using var mine = Session(secrets);
        using var other = Session(null);

        Assert.Equal(Value, Bash(mine, $"echo \"${name}\""));
        Assert.Equal(Value, Ps(mine, $"$env:{name}"));
        Assert.Equal(Value, Bash(mine, $"printenv {name}"));          // read by name
        Assert.Equal("[]", Bash(other, $"echo \"[${name}]\""));       // another session of the same process: nothing
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void AScriptsAssignment_StaysInTheSession_AndUnsetDropsIt()
    {
        var name = Unique("ASSIGN");
        var secrets = new ShellSecrets();
        secrets.Set(name, Value);
        using var pwsh = Session(secrets);

        Assert.Equal("other", Bash(pwsh, $"{name}=other; echo \"${name}\""));
        Assert.Null(Environment.GetEnvironmentVariable(name));
        Assert.True(secrets.TryGet(name, out var now));
        Assert.Equal("other", now);

        // A subshell's assignment ends with it, as any variable's does.
        Assert.Equal("other", Bash(pwsh, $"( {name}=inner ); echo \"${name}\""));
        // A temporary `NAME=v cmd` puts the session's value back.
        Bash(pwsh, $"{name}=temp true");
        Assert.Equal("other", Bash(pwsh, $"echo \"${name}\""));

        Assert.Equal("[]", Bash(pwsh, $"unset {name}; echo \"[${name}]\""));
        Assert.DoesNotContain(name, secrets.Names);
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Listings_ShowTheNameWithTheMask_NeverTheValue()
    {
        var name = Unique("LIST");
        var secrets = new ShellSecrets();
        secrets.Set(name, Value);
        using var pwsh = Session(secrets);

        string masked = name + "=" + ShellSecrets.Mask;
        Assert.Equal(masked, Bash(pwsh, $"env | grep '^{name}='"));
        Assert.Equal(masked, Bash(pwsh, $"printenv | grep '^{name}='"));
        string set = Bash(pwsh, $"set | grep '^{name}='");     // `set` quotes a value it would not read back bare
        Assert.Contains(ShellSecrets.Mask, set);
        Assert.DoesNotContain(Value, set);
        string declared = Bash(pwsh, $"declare -p {name}");
        Assert.Contains(ShellSecrets.Mask, declared);
        Assert.DoesNotContain(Value, declared);
        Assert.Equal(ShellSecrets.Mask, Ps(pwsh, $"(Get-ChildItem Env: | Where-Object Name -eq '{name}').Value"));
    }

    [SkippableFact]
    public void AChild_GetsExactlyTheGrantedSecrets_ByGrantNotByExport()
    {
        Skip.If(OperatingSystem.IsWindows(), "/bin/sh is the stand-in program");
        var granted = Unique("GRANTED");
        var held = Unique("HELD");
        var secrets = new ShellSecrets();
        secrets.Set(granted, Value);
        secrets.Set(held, "held-value", granted: false);
        using var pwsh = Session(secrets);
        string probe = $"/bin/sh -c 'echo \"g=${granted} h=${held}\"'";

        // A program PowerShell would start itself (no stderr merge) still gets its environment from ps-bash.
        Assert.Equal($"g={Value} h=", Bash(pwsh, probe));
        // `export NAME` does not grant.
        Assert.Equal($"g={Value} h=", Bash(pwsh, $"export {held}; {probe}"));
        // `NAME=v cmd` passes it to that command only.
        Assert.Equal($"g={Value} h=held-value", Bash(pwsh, $"{held}=\"${held}\" {probe}"));
        Assert.Equal($"g={Value} h=", Bash(pwsh, probe));
        // `env -i` gives it to nobody; `env -u` takes it away.
        Assert.Equal("g= h=", Bash(pwsh, $"env -i {probe}"));
        Assert.Equal("g= h=", Bash(pwsh, $"env -u {granted} {probe}"));
        Assert.Null(Environment.GetEnvironmentVariable(granted));
        Assert.Null(Environment.GetEnvironmentVariable(held));
    }

    [SkippableFact]
    public void ARevokedSecret_IsGoneForTheNextCommand()
    {
        Skip.If(OperatingSystem.IsWindows(), "/bin/sh is the stand-in program");
        var name = Unique("REVOKE");
        var secrets = new ShellSecrets();
        secrets.Set(name, Value);
        using var pwsh = Session(secrets);

        Assert.Equal(Value, Bash(pwsh, $"/bin/sh -c 'echo \"${name}\"'"));
        secrets.Set(name, "rotated");
        Assert.Equal("rotated", Bash(pwsh, $"/bin/sh -c 'echo \"${name}\"'"));
        secrets.Remove(name);
        Assert.Equal("[]", Bash(pwsh, $"/bin/sh -c 'echo \"[${name}]\"'"));
        Assert.Equal("[]", Bash(pwsh, $"echo \"[${name}]\""));
    }
}
