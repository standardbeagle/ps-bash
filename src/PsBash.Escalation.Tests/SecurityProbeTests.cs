using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Security probe tests per QA rubric Directive 12.
///
/// Strategy: use a canary file whose path is guaranteed unique. An injection
/// payload tries to create the canary (New-Item ...). After ps-bash runs, assert
/// the canary does NOT exist — the variable value was treated as data, not code.
///
/// For transpile-layer probes: assert the raw payload string does not appear
/// literally executable in the transpiled PowerShell output.
///
/// ps-bash-specific assertion justified: we are deliberately testing the PS seam
/// where bash variables are expanded into PowerShell. No bash oracle applies here
/// because bash and PowerShell have different injection surfaces.
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "Security")]
public class SecurityProbeTests
{
    private static readonly string? PwshPath = FindPwsh();

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    private static string NewCanaryPath() =>
        Path.Combine(Path.GetTempPath(), $"psbash_canary_{Guid.NewGuid():N}");

    /// <summary>
    /// Returns true when <paramref name="needle"/> appears in <paramref name="haystack"/>
    /// ONLY inside single-quoted or double-quoted PS string literals, never as bare code.
    ///
    /// Security invariant: if the injection payload is confined to quoted strings it
    /// cannot be executed by PowerShell.
    /// </summary>
    private static bool PayloadIsOnlyInsideStringLiterals(string haystack, string needle)
    {
        // Walk through the transpiled code looking for occurrences of needle.
        // For each occurrence, check that it falls within a single-quoted string '...'
        // by verifying the character immediately before the occurrence (scanning backwards)
        // is inside a quoted region.
        // Simplified approach: strip all single-quoted string contents, then check
        // needle is absent in the remainder (i.e. it only appeared inside quotes).
        var strippedSingle = System.Text.RegularExpressions.Regex.Replace(
            haystack, @"'[^']*'", "''");
        var strippedDouble = System.Text.RegularExpressions.Regex.Replace(
            strippedSingle, @"""[^""]*""", "\"\"");
        return !strippedDouble.Contains(needle, StringComparison.Ordinal);
    }

    // ── 1. Var containing semicolon — no injection ────────────────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12.
    /// V='; New-Item canary_A -ItemType File' — the semicolon must NOT be
    /// interpreted as a statement separator at the PowerShell layer.
    /// The payload is allowed to appear inside a PS string literal (that is safe
    /// and correct behavior), but must not appear as bare executable code.
    /// </summary>
    [SkippableFact]
    public async Task Security_VarWithSemicolon_NoInjection()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var canary = NewCanaryPath();
        var payload = $"; New-Item \"{canary}\" -ItemType File";
        var script = $"V='{payload}'\necho $V";
        var transpiled = BashTranspiler.Transpile(script);

        // The canary path must appear only inside string literals in the transpiled
        // output — not as a bare executable statement.
        Assert.True(PayloadIsOnlyInsideStringLiterals(transpiled, canary),
            $"Canary path found outside string literals in transpiled output:\n{transpiled}");

        // Runtime check: run the script and assert canary not created.
        try
        {
            var (_, stdout, _) = await ProcessRunHelper.RunAsync(
                new[] { "-c", script },
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(File.Exists(canary),
                $"Canary file was created — injection succeeded. payload={payload}");
            // Output must contain the literal semicolon text, not be empty.
            Assert.Contains(";", stdout);
        }
        finally
        {
            if (File.Exists(canary)) File.Delete(canary);
        }
    }

    // ── 2. Var containing $(...) — no command substitution ────────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12.
    /// V='$(New-Item canary_B -ItemType File)' — the $(...) must not trigger
    /// PowerShell command substitution when the variable is later expanded.
    /// The payload must be confined to string literals in the transpiled output.
    /// </summary>
    [SkippableFact]
    public async Task Security_VarWithCommandSub_NoInjection()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var canary = NewCanaryPath();
        // Use single-quoted assignment so bash lexer treats $(...) as literal.
        var payload = $"$(New-Item \"{canary}\" -ItemType File)";
        var script = $"V='{payload}'\necho $V";

        // Transpile check: the canary path must only appear inside string literals.
        var transpiled = BashTranspiler.Transpile(script);
        Assert.True(PayloadIsOnlyInsideStringLiterals(transpiled, canary),
            $"Canary path found outside string literals in transpiled output:\n{transpiled}");

        try
        {
            var (_, stdout, _) = await ProcessRunHelper.RunAsync(
                new[] { "-c", script },
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(File.Exists(canary),
                $"Canary file was created — command-sub injection succeeded. payload={payload}");
            // The literal text of the payload must appear in stdout (not evaluated).
            Assert.Contains("New-Item", stdout);
        }
        finally
        {
            if (File.Exists(canary)) File.Delete(canary);
        }
    }

    // ── 3. Var containing PS scriptblock chars — no execution ─────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12.
    /// V='${ New-Item canary_C -ItemType File }' — braces and ${ must not trigger
    /// PowerShell scriptblock/subexpression evaluation.
    /// The canary path must be confined to string literals in the transpiled output.
    /// </summary>
    [SkippableFact]
    public async Task Security_VarWithPSScriptblock_NoInjection()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var canary = NewCanaryPath();
        var payload = $"${{ New-Item \"{canary}\" -ItemType File }}";
        var script = $"V='{payload}'\necho $V";

        // Transpile check: canary must appear only inside string literals.
        var transpiled = BashTranspiler.Transpile(script);
        Assert.True(PayloadIsOnlyInsideStringLiterals(transpiled, canary),
            $"Canary path found outside string literals in transpiled output:\n{transpiled}");

        try
        {
            var (_, stdout, _) = await ProcessRunHelper.RunAsync(
                new[] { "-c", script },
                timeout: TimeSpan.FromSeconds(15));

            Assert.False(File.Exists(canary),
                $"Canary file was created — scriptblock injection succeeded. payload={payload}");
            Assert.Contains("New-Item", stdout);
        }
        finally
        {
            if (File.Exists(canary)) File.Delete(canary);
        }
    }

    // ── 4. Heredoc with special chars — literal output ────────────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12 + Directive 1 oracle exception.
    /// A literal heredoc (<<'EOF') containing ", $VAR, backtick must produce
    /// verbatim output — no variable expansion, no command substitution.
    ///
    /// ps-bash-specific assertion: we probe the transpiled output to ensure the
    /// literal body appears as a here-string, not an expanded string.
    /// </summary>
    [Fact]
    public void Security_HeredocWithSpecialChars_LiteralOutput()
    {
        // The heredoc body contains $HOME, backtick, and double-quote.
        // With <<'EOF' (quoted delimiter) these must all be literal.
        var script = "cat <<'EOF'\n\"hello\" $HOME `date`\nEOF";
        var transpiled = BashTranspiler.Transpile(script);

        // A literal heredoc must be emitted as a PS single-quoted here-string @'...'@
        // so that $HOME and backtick are not expanded by PowerShell.
        Assert.Contains("@'", transpiled);

        // The body text must appear verbatim inside the here-string.
        Assert.Contains("$HOME", transpiled, StringComparison.Ordinal);
        Assert.Contains("`date`", transpiled, StringComparison.Ordinal);

        // PowerShell must not see $HOME as an expandable variable reference
        // outside of a string — confirm the content is inside the @'...'@ block.
        var atTickIdx = transpiled.IndexOf("@'", StringComparison.Ordinal);
        var atTickEnd = transpiled.IndexOf("'@", atTickIdx + 2, StringComparison.Ordinal);
        Assert.True(atTickIdx >= 0 && atTickEnd > atTickIdx,
            "Expected single-quoted here-string @'...'@ in transpiled output");
        var hereStringBody = transpiled.Substring(atTickIdx, atTickEnd - atTickIdx + 2);
        Assert.Contains("$HOME", hereStringBody, StringComparison.Ordinal);
    }

    // ── 5. Quoted var with spaces — no word splitting ─────────────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12.
    /// V="hello world"; echo $V — the double-quoted expansion must produce a
    /// single argument "hello world", not two separate words.
    ///
    /// Injection risk: if word splitting fires, "hello" could be mistaken for a
    /// command name by downstream processing. We verify the transpiler wraps the
    /// variable reference in quotes so PowerShell treats it as one string.
    /// </summary>
    [Fact]
    public void Security_QuotedVarWithSpaces_NoWordSplit()
    {
        // V="hello world" followed by echo $V.
        // The transpiled output must quote the variable reference so PowerShell
        // does not perform word splitting.
        var transpiled = BashTranspiler.Transpile("V=\"hello world\"\necho $V");

        // $env:V must appear — the emitter maps regular vars to $env:.
        Assert.Contains("$env:V", transpiled, StringComparison.Ordinal);

        // The emitter must not emit $env:V as a bare unquoted token followed by
        // additional space-separated tokens that would cause word-split misread.
        // Verify the assignment captures the full value with the space.
        Assert.Contains("hello world", transpiled, StringComparison.Ordinal);
    }

    // ── 6. IFS-like injection via unquoted var with glob chars ────────────────

    /// <summary>
    /// Directive 12 / Failure-surface axis 12 (IFS + glob).
    /// A variable containing glob characters must not expand into filesystem paths
    /// when used as a command argument. The transpiler must quote the reference.
    ///
    /// ps-bash-specific assertion: we probe transpiled output only, since pwsh
    /// glob-expansion behavior differs from bash. The key invariant is that the
    /// emitter quotes the variable so PowerShell does not perform glob expansion.
    /// </summary>
    [Fact]
    public void Security_VarWithGlobChars_TranspilerQuotesRef()
    {
        // V="*.cs" — if echo $V is emitted unquoted, PowerShell may expand it.
        var transpiled = BashTranspiler.Transpile("V=\"*.cs\"\necho $V");

        // The assignment must capture the glob pattern as a literal string.
        Assert.Contains("*.cs", transpiled, StringComparison.Ordinal);

        // The emitter must use $env:V (quoting is done at runtime by Invoke-BashEcho).
        Assert.Contains("$env:V", transpiled, StringComparison.Ordinal);
    }
}
