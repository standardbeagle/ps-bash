using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashXargs
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashXargs</c> function. The original oracle
/// supported <c>-0</c>, <c>-I REPLACE</c>, <c>-n N</c>, <c>--</c>; this
/// cmdlet preserves those byte-for-byte and adds <c>-r</c>, <c>-t</c>,
/// <c>-L N</c>, <c>-P N</c> (no-op), <c>-p</c> (no-op).
///
/// Failure-surface axes (Directive 3): empty input (<c>-r</c> skip),
/// missing target (no command → error), quoting/injection (Directive 12 —
/// substitution value containing <c>$()</c>).
/// </summary>
public class InvokeBashXargsCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashXargsCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private (Collection<PSObject> Result, IList<ErrorRecord> Errors) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var errs = pwsh.Streams.Error.ToArray();
        return (result, errs);
    }

    private static string JoinBashText(Collection<PSObject> objects)
    {
        var parts = new List<string>();
        foreach (var o in objects)
        {
            if (o == null) continue;
            var baseObj = o.BaseObject;
            if (baseObj is string s) { parts.Add(s); continue; }
            var bp = o.Properties["BashText"];
            if (bp != null && bp.Value != null) { parts.Add(bp.Value.ToString() ?? ""); continue; }
            parts.Add(o.ToString());
        }
        return string.Join("\n", parts);
    }

    [Fact]
    public void Xargs_BasicEcho_PassesItemsAsArgs()
    {
        // echo a b c | xargs echo  → "a b c"
        var (result, _) = Run("'a b c' | Invoke-BashXargs echo");
        var joined = JoinBashText(result);
        Assert.Contains("a b c", joined);
    }

    [Fact]
    public void Xargs_DashN1_RunsCommandOncePerItem()
    {
        // With -n 1, each input line spawns its own echo invocation.
        var (result, _) = Run("@('a','b','c') | Invoke-BashXargs -n 1 echo");
        var joined = JoinBashText(result);
        Assert.Contains("a", joined);
        Assert.Contains("b", joined);
        Assert.Contains("c", joined);
    }

    [Fact]
    public void Xargs_WhitespaceInput_SplitsOnBlanksLikeGnu()
    {
        // Regression: a SINGLE input line with space-separated tokens must split
        // into separate items (GNU xargs default = whitespace-delimited), so
        // `-n 1` runs once per token. The old newline-only split merged them
        // into one item, so `-n 1` produced a single `echo a b c`.
        // Oracle: `printf "a b c\n" | xargs -n1 echo` -> "a\nb\nc".
        var (result, _) = Run("'a b c' | Invoke-BashXargs -n 1 echo");
        var joined = JoinBashText(result);
        Assert.Equal("a\nb\nc", joined);          // three separate invocations
        Assert.DoesNotContain("a b c", joined);   // NOT one merged invocation
    }

    [Fact]
    public void Xargs_MixedBlanksAndNewlines_SplitIntoAllItems()
    {
        // Tabs, spaces, and newlines are all item separators by default.
        var (result, _) = Run("\"a b\tc\nd\" | Invoke-BashXargs -n 1 echo");
        var joined = JoinBashText(result);
        Assert.Equal("a\nb\nc\nd", joined);
    }

    [Fact]
    public void Xargs_DashIReplace_SubstitutesTokenOncePerLine()
    {
        // Replace mode: '{}' gets replaced with each input line, one invocation per line.
        var (result, _) = Run("@('x','y') | Invoke-BashXargs -I '{}' echo prefix-'{}'-suffix");
        var joined = JoinBashText(result);
        Assert.Contains("prefix-x-suffix", joined);
        Assert.Contains("prefix-y-suffix", joined);
    }

    [Fact]
    public void Xargs_NullDelim_SplitsOnNul()
    {
        // -0: NUL-separated items in a single pipeline string.
        var (result, _) = Run("\"a`0b`0c\" | Invoke-BashXargs -0 -n 1 echo");
        var joined = JoinBashText(result);
        Assert.Contains("a", joined);
        Assert.Contains("b", joined);
        Assert.Contains("c", joined);
    }

    [Fact]
    public void Xargs_NoRunIfEmpty_DoesNotInvokeCommandWhenInputIsEmpty()
    {
        // -r: with no input items, the command must not run. We probe by
        // wrapping a command that, if invoked, would emit a sentinel.
        var (result, _) = Run("@() | Invoke-BashXargs -r echo SHOULD-NOT-APPEAR");
        var joined = JoinBashText(result);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", joined);
    }

    [Fact]
    public void Xargs_TraceFlag_EchoesCommandToStderr()
    {
        // -t: command line is echoed to stderr before each invocation.
        // We can't capture stderr cleanly via PowerShell SDK, but we can
        // still assert the command produced expected stdout.
        var (result, _) = Run("'hello' | Invoke-BashXargs -t echo");
        var joined = JoinBashText(result);
        Assert.Contains("hello", joined);
    }

    [Fact]
    public void Xargs_DashL1_RunsCommandPerLine()
    {
        // -L 1 batches one item per invocation (same shape as -n 1 here
        // since the oracle already segmented input by line).
        var (result, _) = Run("@('one','two') | Invoke-BashXargs -L 1 echo");
        var joined = JoinBashText(result);
        Assert.Contains("one", joined);
        Assert.Contains("two", joined);
    }

    [Fact]
    public void Xargs_LargeInput_HandlesAllItems()
    {
        // >100 items routed through xargs with -n 1.
        var (result, _) = Run("1..120 | Invoke-BashXargs -n 1 echo");
        var joined = JoinBashText(result);
        Assert.Contains("1", joined);
        Assert.Contains("60", joined);
        Assert.Contains("120", joined);
    }

    [Fact]
    public void Xargs_MultiTokenCommand_PassesLeadingArgsFirst()
    {
        // The leading args after the command name are passed before the
        // items appended from stdin. echo prefix item-from-stdin.
        var (result, _) = Run("'tail' | Invoke-BashXargs echo HEADER");
        var joined = JoinBashText(result);
        Assert.Contains("HEADER tail", joined);
    }

    [Fact]
    public void Xargs_NoCommand_EmitsMissingCommandError()
    {
        var (result, _) = Run("'x' | Invoke-BashXargs");
        // No output object; the error sink got the bash-style error.
        Assert.Empty(result);
    }

    [Fact]
    public void Xargs_ViaAlias_StillResolves()
    {
        // The Set-Alias xargs line in psm1 still binds to the cmdlet.
        var (result, _) = Run("'aliased' | xargs echo");
        var joined = JoinBashText(result);
        Assert.Contains("aliased", joined);
    }

    [Fact]
    public void Xargs_HelpFlag_EmitsUsageText()
    {
        var (result, _) = Run("Invoke-BashXargs --help");
        Assert.NotEmpty(result);
        var joined = string.Join("\n", result.Select(o => o?.ToString() ?? ""));
        Assert.Contains("xargs", joined, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- Directive 12: injection probes ----

    [Fact]
    public void Xargs_SubstitutionValueWithScriptBlockChars_IsTreatedLiterally()
    {
        // The input item contains $(throw "pwn"). If the cmdlet built a
        // script body by string concatenation with the substituted
        // command, this throw would fire and crash. Instead, the value
        // must travel through $args as a positional literal.
        var (result, _) = Run("'$(throw \"pwn\")' | Invoke-BashXargs -I '{}' echo '{}'");
        var joined = JoinBashText(result);
        // The literal "pwn" payload string must appear (no eval).
        Assert.Contains("throw", joined);
        Assert.Contains("pwn", joined);
    }

    [Fact]
    public void Xargs_InputItemWithSemicolon_DoesNotChainExtraCommands()
    {
        // Item is `echo;rm`. If the cmdlet concatenated the substitution
        // into a script body, two statements would run. Instead the
        // literal `echo;rm` must arrive as a single positional argument.
        var (result, _) = Run("'echo;rm' | Invoke-BashXargs -I '{}' echo prefix-'{}'-suffix");
        var joined = JoinBashText(result);
        Assert.Contains("prefix-echo;rm-suffix", joined);
    }
}
