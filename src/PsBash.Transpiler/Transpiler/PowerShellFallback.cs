using System.Text.RegularExpressions;
using PsBash.Core.Parser;

namespace PsBash.Core.Transpiler;

/// <summary>
/// The ONE policy for running text the bash parser rejected as PowerShell instead. Bash text is never PowerShell code
/// (docs/specs/emitter-strategy.md §Bash data never becomes PowerShell code), so this is the only door: text that does
/// not parse as bash runs as PowerShell only when it looks unambiguously like PowerShell (<see cref="LooksLikePowerShell"/>),
/// the error is not one bash itself reports as a shell error (an invalid <c>shopt</c>), and
/// <c>PSBASH_NO_PS_FALLBACK</c> is not set. Everything else is reported as the bash parse error it is.
/// REGRESSION: <c>flock f -c "$cmd"</c> ran ANY unparseable string as PowerShell (<c>catch { return bash; }</c>).
/// Callers: the launcher's <c>-c</c> / script text (PsBash.Shell Program.cs) and <c>flock -c</c>. Guard
/// BashDataNeverCodeGuardTests.ParseFailureFallsBackToPowerShellOnlyThroughOnePolicy.
/// </summary>
public static class PowerShellFallback
{
    /// <summary>The opt-out: when truthy, a bash parse error is always reported.</summary>
    public const string OptOutVariable = "PSBASH_NO_PS_FALLBACK";

    /// <summary>
    /// True when <paramref name="text"/>, which the bash parser rejected with <paramref name="error"/>, may run as
    /// PowerShell as written.
    /// </summary>
    public static bool Allows(string text, ParseException error)
        => error.Rule != "shell-option" && !OptedOut() && LooksLikePowerShell(text);

    private static bool OptedOut()
    {
        var value = Environment.GetEnvironmentVariable(OptOutVariable)?.Trim();
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Heuristic: does <paramref name="input"/> look like PowerShell rather than bash? Only consulted AFTER the bash
    /// parser has already failed, so it need not exclude valid bash: it only has to tell "PowerShell the user meant" from
    /// "a bash typo". The signals are constructs that are unambiguously PowerShell and NOT valid bash (so
    /// <c>[ $a -eq $b ]</c>, which both share, is deliberately not one). Pure string/regex, so it stays AOT-safe.
    /// </summary>
    public static bool LooksLikePowerShell(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        // A Pascal-case hyphenated cmdlet token: Get-Content, Test-Path, New-Item, ... Bash command names are lower-case,
        // so a Capitalized-Capitalized token is a strong PowerShell signal.
        if (Regex.IsMatch(input, @"\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b"))
            return true;

        // A .NET static member access: [System.IO.Path]::GetFullPath(...), [Console]::...
        if (Regex.IsMatch(input, @"\[[\w.]+\]::"))
            return true;

        // PowerShell control flow with a brace block: `if (...) { }`, `while (...) {`, `foreach (...) {`, `switch (...) {`.
        // Bash uses `if ...; then ... fi` / `do ... done` and would never put a `{` block directly after `(...)`.
        if (Regex.IsMatch(input, @"\b(if|elseif|while|foreach|switch)\b\s*\([^)]*\)\s*\{", RegexOptions.Singleline))
            return true;

        // A param block (script/function signature) at the start.
        if (Regex.IsMatch(input, @"^\s*param\s*\(", RegexOptions.IgnoreCase))
            return true;

        // Pipeline automatic variable property access ($_.Prop) or $PSItem.
        if (Regex.IsMatch(input, @"\$_\.\w|\$PSItem\b"))
            return true;

        return false;
    }
}
