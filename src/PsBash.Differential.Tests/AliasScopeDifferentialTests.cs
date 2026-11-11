using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Where an alias applies. bash expands an alias only for a command word it reads literally, and an alias defined in
/// a subshell (<c>$( )</c>, <c>( )</c>) ends with it. ps-bash runs an alias as a global function of its name: before,
/// <c>cmd=myalias; $cmd</c> found that function (bash: command not found, 127), and <c>echo $(alias hi=…); hi</c>
/// kept the function after the substitution (bash: command not found).
/// </summary>
public class AliasScopeDifferentialTests
{
    private const string Expand = "shopt -s expand_aliases\n";

    [SkippableTheory]
    [InlineData("alias ea='echo'\ncmd=ea\nea X\n$cmd X 2>/dev/null\necho st=$?")]
    [InlineData("alias ls2='echo listed'\nc=ls2; $c 2>/dev/null; echo st=$?; ls2")]
    [InlineData("echo \"[$(alias sayhi='echo hello')]\"\nsayhi 2>/dev/null\necho st=$?")]
    [InlineData("alias hi='echo outer'\nx=$(alias hi='echo sub')\nhi\n( unalias hi )\nhi\n(alias hi='echo inner')\nhi")]
    public Task Alias_AppliesOnlyWhereBashExpandsIt(string script) => AssertOracle.EqualAsync(Expand + script);

    /// <summary>
    /// An alias holding part of a compound command: bash reads the alias text in place of the word. Before, these were
    /// parse errors (<c>{</c>, <c>(</c>, <c>(( ))</c> after an empty alias) or ran the loop text through eval with the
    /// rest of the line quoted (<c>e_ done</c>: "Expected 'done'").
    /// </summary>
    [SkippableTheory]
    [InlineData("alias LEFT='{'\nLEFT echo one; echo two; }")]
    [InlineData("alias LEFT='('\nLEFT echo one; echo two )\necho st=$?")]
    [InlineData("alias a=\na (( v = 2 ))\necho \"done $v\"")]
    [InlineData("alias e_='for i in 1 2 3; do echo $i;'\ne_ done")]
    [InlineData("alias e_='for i in 1 2 3; do echo '\ne_ $i; done")]
    [InlineData("alias FOR1='for '\nalias FOR2='FOR1 '\nalias eye1='i '\nalias eye2='eye1 '\nalias IN='in '\nalias onetwo='$one \"2\" '\none=1\nFOR2 eye2 IN onetwo 3; do echo $i; done")]
    [InlineData("alias ls='echo ls-l'\nalias L='{'\nL ls; echo x; }")]
    public Task Alias_HoldingPartOfACompoundCommand_IsReadAsItsText(string script) => AssertOracle.EqualAsync(Expand + script);
}
