using System.Management.Automation.Language;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Parse-oracle for the command wrappers Claude Code's Bash tool wraps every
/// command in. The transpiler emits PowerShell; the host then hands that string
/// to PowerShell's parser. If the emission is syntactically invalid PowerShell,
/// EVERY Bash-tool command fails before running with
/// "ps-bash: parse error: ..." (SdkWorker catches the ParseException).
///
/// Oracle note (qa-rubric Directive 1): the assertion oracle here is PowerShell's
/// own parser, not bash. The behavior under test is "the emitted PowerShell is
/// syntactically valid", which is a ps-bash/PowerShell-seam property with no bash
/// equivalent — hand-driven parse check justified per the exception list.
///
/// This complements the exact-string assertions in
/// PsBash.Core.Tests/Transpiler/BashTranspilerTests.cs: those pin the shape, this
/// proves the shape parses. The wrapper is an EXTERNAL, changing input, so a
/// parse-level guard catches a new wrapper variant that a string match would miss.
///
/// Root cause this guards (fixed): a multi-pair bare assignment
/// (`TEMP=x TMP=y`) inside a `&&` / `||` chain emitted `[void](a; b)`, but
/// PowerShell's grouping `(...)` cannot hold a `;`-separated statement list
/// ("Missing closing ')'"). The fix emits `[void]$(...)` for the multi-statement
/// case.
/// </summary>
public class WrapperParseabilityTests
{
    private static void AssertTranspilesToValidPowerShell(string bashInput)
    {
        var pwsh = BashTranspiler.Transpile(bashInput);

        Parser.ParseInput(pwsh, out _, out var parseErrors);

        if (parseErrors is { Length: > 0 })
        {
            var msgs = string.Join(Environment.NewLine,
                parseErrors.Select(e =>
                    $"  {e.Extent.StartLineNumber}:{e.Extent.StartColumnNumber} {e.Message}"));
            Assert.Fail(
                $"Transpiled PowerShell does not parse.{Environment.NewLine}" +
                $"  bash input: {bashInput}{Environment.NewLine}" +
                $"  transpiled: {pwsh}{Environment.NewLine}" +
                $"  errors:{Environment.NewLine}{msgs}");
        }
    }

    [Theory]
    // The live failure: multi-var env-setup assignment in a && chain.
    [InlineData("TEMP='C:\\Users\\me\\Temp' TMP='C:\\Users\\me\\Temp' && echo hi")]
    // The same with a `|| true` guard before it (the full preamble shape).
    [InlineData("shopt -u extglob 2>/dev/null || true && "
              + "TEMP='C:\\Users\\me\\Temp' TMP='C:\\Users\\me\\Temp' && echo hi")]
    // Plain multi-var assignment in a chain (unquoted values).
    [InlineData("A=1 B=2 && echo hi")]
    // Three pairs.
    [InlineData("A=1 B=2 C=3 && echo hi")]
    // Multi-var in an || chain (other branch of EmitAndOrList).
    [InlineData("A=1 B=2 || echo hi")]
    // Single var must stay valid (regression guard for the cheaper grouping form).
    [InlineData("A=1 && echo hi")]
    // The previously-captured wrapper shape (force-clobber + eval + pwd).
    [InlineData("shopt -u extglob 2>/dev/null || true && eval 'echo hi' < /dev/null && pwd -P >| /tmp/cwd")]
    // The CURRENT full Claude Code wrapper (2026-05-28): snapshot bootstrap with
    // the TEMP/TMP multi-var env-setup inserted — the exact shape that broke.
    [InlineData("shopt -u extglob 2>/dev/null || true && "
              + "TEMP='C:\\Temp' TMP='C:\\Temp' && "
              + "eval 'echo hi' < /dev/null && pwd -P >| /tmp/cwd")]
    public void Transpile_BashToolWrapper_EmitsParseablePowerShell(string bashInput)
    {
        AssertTranspilesToValidPowerShell(bashInput);
    }

    [Theory]
    // A quoted literal nested inside `$( … )` inside a double-quoted assignment.
    // The inner literal used to be emitted with a backtick escape (`" / ``), which
    // the OUTER string scanner consumed — ending the inner string early and failing
    // the whole file with "The string is missing the terminator". A pure-literal word
    // is now emitted as a SINGLE-quoted PowerShell string, which nests safely.
    // Found by sweeping real .sh files: backup.sh, stop-hook.sh, resolve-port.sh.
    [InlineData(@"X=""$(grep ""a\""b"" f)""")]
    [InlineData(@"X=""$(grep ""[\""x]"" f)""")]
    [InlineData(@"X=""$(echo ""a\`b"")""")]
    // The `'"'"'` single-quote-escape idiom inside a nested command substitution.
    [InlineData(@"X=""$(grep 'a'""'""'b' f)""")]
    public void Transpile_QuotedLiteralNestedInCommandSub_EmitsParseablePowerShell(
        string bashInput)
    {
        AssertTranspilesToValidPowerShell(bashInput);
    }
}
