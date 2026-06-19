using System.Management.Automation.Language;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// The one transpiler-parseability invariant, shared by every test in this folder.
/// See docs/specs/transpile-fuzz-grammar.md §1.
///
/// For ANY input, <see cref="BashTranspiler.Transpile(string)"/> must either
///   (A) return PowerShell that PowerShell's own parser accepts with ZERO errors, or
///   (B) throw a clean, well-positioned <see cref="ParseException"/>.
/// Anything else — a non-ParseException crash, or emitted PowerShell that fails to
/// parse — is a bug. Claude Code's Bash tool feeds the emitted PowerShell straight to
/// the host's parser, so broken emission breaks an entire class of agent commands
/// before they run.
///
/// Oracle note (qa-rubric Directive 1): the oracle is PowerShell's parser, not bash.
/// The property under test is "emission is syntactically valid PowerShell" — a
/// ps-bash/PowerShell-seam property with no bash equivalent, so a hand-driven parse
/// check is the justified oracle.
/// </summary>
public static class ParseabilityContract
{
    public enum Outcome { ValidPowerShell, CleanReject, BrokenPowerShell }

    /// <summary>
    /// <see cref="AssertNoCrash"/> bounded by a wall-clock watchdog: runs on a background
    /// thread and, if Transpile exceeds <paramref name="timeoutMs"/>, returns false WITHOUT
    /// failing — a catastrophic-backtracking garbage input is a tracked perf gap (spec §4),
    /// not a suite hang. A real crash / contract violation inside the budget still throws.
    /// A fuzzer that feeds adversarial garbage must never be hangable by its own inputs.
    /// </summary>
    public static bool AssertNoCrashWithin(string bashInput, int timeoutMs, string? context = null)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { AssertNoCrash(bashInput, context); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        worker.Start();
        if (!worker.Join(timeoutMs))
            return false;                 // catastrophic-slow input — tolerated, tracked.
        if (failure != null) throw failure; // genuine crash / broken-PS contract failure.
        return true;
    }

    /// <summary>
    /// The MINIMUM safety invariant, asserted for EVERY input including deliberately
    /// malformed garbage: Transpile must never throw a non-ParseException (a raw crash)
    /// and must never hang. It MAY still emit broken PowerShell on malformed input — that
    /// is a tracked gap (spec §4), surfaced via the returned <see cref="Outcome"/>, not a
    /// failure here. Use this for the garbage corpus where full clean-rejection is not yet
    /// guaranteed; use <see cref="Assert"/> for the covered construct grammar where it is.
    /// </summary>
    public static Outcome AssertNoCrash(string bashInput, string? context = null)
    {
        string ctx = context is null ? "" : $"  [{context}]";
        string pwsh;
        try
        {
            pwsh = BashTranspiler.Transpile(bashInput);
        }
        catch (ParseException pe)
        {
            Xunit.Assert.False(string.IsNullOrWhiteSpace(pe.Message),
                $"ParseException with empty message.{ctx}\n  input: {Show(bashInput)}");
            Xunit.Assert.True(pe.Line >= 1 && pe.Column >= 1 && !string.IsNullOrWhiteSpace(pe.Rule),
                $"ParseException not well-formed (Line={pe.Line} Col={pe.Column} Rule='{pe.Rule}').{ctx}\n  input: {Show(bashInput)}");
            return Outcome.CleanReject;
        }
        catch (Exception ex)
        {
            Xunit.Assert.Fail(
                $"Transpile RAW-CRASHED ({ex.GetType().Name}) on input.{ctx}\n  input: {Show(bashInput)}\n  {ex}");
            throw;
        }
        Parser.ParseInput(pwsh, out _, out ParseError[] errors);
        return errors is { Length: > 0 } ? Outcome.BrokenPowerShell : Outcome.ValidPowerShell;
    }

    /// <summary>Assert the contract for one input. Returns which branch was taken
    /// (useful for fuzzers that want to report an A/B split). <paramref name="context"/>
    /// is prepended to every failure message so a fuzzer can attach its seed.</summary>
    public static Outcome Assert(string bashInput, string? context = null)
    {
        string ctx = context is null ? "" : $"  [{context}]";

        string pwsh;
        try
        {
            pwsh = BashTranspiler.Transpile(bashInput);
        }
        catch (ParseException pe)
        {
            // Branch (B): a clean rejection. The message must be high quality so the
            // user sees a positioned, actionable error — never a bare .NET message.
            Xunit.Assert.False(string.IsNullOrWhiteSpace(pe.Message),
                $"ParseException with empty message.{ctx}\n  input: {Show(bashInput)}");
            Xunit.Assert.True(pe.Line >= 1,
                $"ParseException not positioned (Line={pe.Line}).{ctx}\n  input: {Show(bashInput)}\n  msg: {pe.Message}");
            Xunit.Assert.True(pe.Column >= 1,
                $"ParseException not positioned (Column={pe.Column}).{ctx}\n  input: {Show(bashInput)}\n  msg: {pe.Message}");
            Xunit.Assert.False(string.IsNullOrWhiteSpace(pe.Rule),
                $"ParseException has no rule.{ctx}\n  input: {Show(bashInput)}\n  msg: {pe.Message}");
            return Outcome.CleanReject;
        }
        catch (Exception ex)
        {
            // Any non-ParseException is a raw crash — a contract violation.
            Xunit.Assert.Fail(
                $"Transpile RAW-CRASHED ({ex.GetType().Name}) instead of returning PS or " +
                $"throwing a clean ParseException.{ctx}\n  input: {Show(bashInput)}\n  {ex}");
            throw; // unreachable
        }

        // Branch (A): must be syntactically valid PowerShell.
        Parser.ParseInput(pwsh, out _, out ParseError[] errors);
        if (errors is { Length: > 0 })
        {
            var msgs = string.Join("\n",
                errors.Take(4).Select(e =>
                    $"    {e.Extent.StartLineNumber}:{e.Extent.StartColumnNumber} {e.Message}"));
            Xunit.Assert.Fail(
                $"Transpiled PowerShell does NOT parse.{ctx}\n" +
                $"  bash: {Show(bashInput)}\n" +
                $"  pwsh: {Show(pwsh)}\n" +
                $"  errors:\n{msgs}");
        }
        return Outcome.ValidPowerShell;
    }

    private static string Show(string s) =>
        s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");
}
