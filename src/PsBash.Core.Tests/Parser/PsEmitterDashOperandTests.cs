using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The operands PowerShell's binder never hands a PowerShell callee as written (a bare <c>--</c> is consumed, <c>--%</c>
/// stops parsing, <c>-x:y</c> splits into <c>-x:</c> and <c>y</c>) are single-quoted for every unmapped command, because a
/// bash function and the stand-in for a program (<c>#!</c> file, background job) are PowerShell code.
/// <para>
/// Emitter-layer pins: bash's own answers (<c>f -x:y -- 1</c> sees <c>[-x:y][--][1]</c>) are in
/// <c>ScriptFileExecutionDifferentialTests</c> and the function argv differential below them; a source guard keeps the
/// stand-ins on literal argv (<c>StandInArgvGuardTests</c>).
/// </para>
/// </summary>
public class PsEmitterDashOperandTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash)!;

    [Theory]
    [InlineData("f() { :; }\nf -x:y -- 1", "f '-x:y' '--' 1")]
    [InlineData("node -e 'x' -- 1", "node -e 'x' '--' 1")]
    [InlineData("dotnet build -p:Configuration=Release", "dotnet build '-p:Configuration=Release'")]
    [InlineData("tool --% raw", "tool '--%' raw")]
    [InlineData("java -Xlint:all -e", "java '-Xlint:all' -e")]
    public void Transpile_UnmappedCommand_QuotesOperandsTheBinderWouldRewrite(string bash, string expected)
        => Assert.EndsWith(expected, Emit(bash));

    [Theory]
    [InlineData("Get-Item -Path:x -- y", "Get-Item -Path:x -- y")]
    [InlineData("Get-ChildItem -Recurse", "Get-ChildItem -Recurse")]
    public void Transpile_PowerShellVerbNounCommand_KeepsParameterSyntax(string bash, string expected)
        => Assert.Equal(expected, Emit(bash).Trim());

    [Fact]
    public void Transpile_ScriptFileRunInPlace_QuotesEveryDashOperandOfTheBashCmdlet()
        => Assert.EndsWith("bash ./s.sh '-e' '-x:y' '--' 1", Emit("./s.sh -e -x:y -- 1"));

    [Theory]
    [InlineData("--", true)]
    [InlineData("--%", true)]
    [InlineData("--%x", true)]
    [InlineData("-x:y", true)]
    [InlineData("--opt=a:b", true)]
    [InlineData("-e", false)]
    [InlineData("--long", false)]
    [InlineData("-", false)]
    public void BinderRewritesOperand_NamesTheWordsPowerShellChanges(string word, bool rewritten)
        => Assert.Equal(rewritten, PsEmitter.BinderRewritesOperand(word));

    [Theory]
    [InlineData("Get-ChildItem", true)]
    [InlineData("Invoke-BashNative", true)]
    [InlineData("Test-NetConnection", true)]
    [InlineData("apt-get", false)]
    [InlineData("docker-compose", false)]
    [InlineData("x86_64-linux-gnu-gcc", false)]
    [InlineData("Get-", false)]
    [InlineData("node", false)]
    [InlineData("A-B", false)]
    public void IsPowerShellCommandName_IsVerbNounOnly(string name, bool expected)
        => Assert.Equal(expected, PsEmitter.IsPowerShellCommandName(name));
}
