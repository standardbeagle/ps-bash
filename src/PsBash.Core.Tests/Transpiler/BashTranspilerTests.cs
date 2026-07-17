using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

public class BashTranspilerTests
{
    [Fact]
    public void SimpleEcho_PassesThrough()
    {
        Assert.Equal("Invoke-BashEcho hello", BashTranspiler.Transpile("echo hello"));
    }

    // Regression: Claude Code's Bash-tool prelude wraps every command in
    // `shopt ... 2>/dev/null || true && eval ... && pwd -P >| /tmp/x`.
    // Two bugs surfaced live:
    //   (a) `cmd || true` emitted `cmd || $global:LASTEXITCODE = 0; [void]$true`
    //       which PowerShell rejects: the `||` only consumes `$global:LASTEXITCODE`
    //       and `= 0` is leftover junk.
    //   (b) `>|` (force-clobber redirect) was lexed as `>` then `|`, producing
    //       `Invoke-BashRedirect -Path |` (empty pipe element).
    [Fact]
    public void OrTrue_InAndOrChain_ProducesParseableOperand()
    {
        var result = BashTranspiler.Transpile("cmd || true");
        // The `true` operand must be a single PowerShell expression; a script
        // block invocation `& { ... }` is one such expression.
        Assert.Contains("|| $($global:LASTEXITCODE = 0; [void]$true)", result);
    }

    // ===================== H1: single-quote escaping at the bash/PS seam =====================
    //
    // Several sites dropped raw bash text into a PowerShell '...' literal; an embedded ' broke
    // out of the literal (Directive 12: transpiled output must not be hijackable). Each now
    // routes through SqEsc, doubling ' -> '' so the value stays inert. The fragments below are
    // the escaped form (a'b -> a''b inside the literal).

    [Fact]
    public void Transpile_RegexMatchPatternWithQuote_EscapesSingleQuote()
    {
        var result = BashTranspiler.Transpile("[[ $x =~ \"a'b\" ]]");
        Assert.Contains("-match 'a''b'", result);
    }

    // ===================== Emitted-PS correctness regressions =====================
    // Each of these previously emitted unparseable or semantically-wrong PowerShell,
    // slipping past the parseability fuzz layer (which had no execution-semantics oracle).

    // #1: a variable in command position emitted the unparseable `$env:CMD hello`.
    // A value is not a command — it needs the `&` call operator.
    [Fact]
    public void Transpile_VariableInCommandPosition_UsesCallOperator()
    {
        var result = BashTranspiler.Transpile("CMD=echo; $CMD hello");
        Assert.Contains("& $env:CMD hello", result);
    }

    // #2: array elements were blindly single-quoted around EmitWord, storing the
    // literal string "$env:x" for a "$x" element and breaking on $'a\'b'.
    [Fact]
    public void Transpile_ArrayElementWithVar_ExpandsInsteadOfLiteral()
    {
        var result = BashTranspiler.Transpile("arr=(\"$x\" b)");
        Assert.Contains("@(\"$env:x\",\"b\")", result);
        Assert.DoesNotContain("'$env:x'", result);
    }

    // #3: subscript assignment stored the literal $env:x for a $x value.
    [Fact]
    public void Transpile_SubscriptAssignmentWithVar_ExpandsInsteadOfLiteral()
    {
        var result = BashTranspiler.Transpile("declare -A m; m[k]=$x");
        Assert.Contains("$m['k'] = \"$env:x\"", result);
        Assert.DoesNotContain("= '$env:x'", result);
    }

    // #5: ${x:-fallback} inside an expanding heredoc emitted $env:x:-fallback
    // (the whole operator body was treated as the variable name).
    [Fact]
    public void Transpile_ExpandingHeredocParamExpansion_ExpandsOperator()
    {
        var result = BashTranspiler.Transpile("cat <<EOF\nv=${x:-fallback}\nEOF");
        Assert.Contains("$(($env:x ?? \"fallback\"))", result);
        Assert.DoesNotContain("$env:x:-fallback", result);
    }

    // #4: case arms emitted no break, so PowerShell's switch ran EVERY matching
    // clause; bash runs only the first. Overlapping patterns are the failure case.
    [Fact]
    public void Transpile_CaseArms_EmitBreakSoOnlyFirstMatchRuns()
    {
        var result = BashTranspiler.Transpile("case $x in a) echo A;; b) echo B;; esac");
        Assert.Contains("'a' { Invoke-BashEcho A; break }", result);
        Assert.Contains("'b' { Invoke-BashEcho B; break }", result);
    }

    [Fact]
    public void Transpile_GlobEqualsPatternWithQuote_EscapesSingleQuote()
    {
        var result = BashTranspiler.Transpile("[[ $x == \"a'*\" ]]");
        Assert.Contains("-like 'a''*'", result);
    }

    [Fact]
    public void Transpile_CasePatternWithQuote_EscapesSingleQuote()
    {
        var result = BashTranspiler.Transpile("case $x in \"a'b\") echo hi;; esac");
        // Bash strips the pattern's quotes ("a'b" matches the string a'b); the embedded
        // single quote is doubled so the PS clause literal does not break out.
        Assert.Contains("'a''b'", result);
    }

    [Fact]
    public void Transpile_ReadonlyPlainValue_StillSingleQuoted()
    {
        // Guards the readonly value path (SqEsc hardening) — a plain value is unchanged.
        Assert.Contains("-Value 'ab'", BashTranspiler.Transpile("readonly X=ab"));
    }

    // ===================== declare -i: arithmetic RHS, not silent 0 =====================

    [Fact]
    public void Transpile_DeclareIntExpression_RoutesThroughArith()
    {
        // `declare -i n=2+3` must evaluate the RHS (bash sets n=5), not collapse to 0.
        Assert.Equal("[int]$global:n = (Invoke-BashArith '2+3')",
            BashTranspiler.Transpile("declare -i n=2+3"));
    }

    [Fact]
    public void Transpile_DeclareIntLiteral_EmittedDirectly()
    {
        // A plain integer literal keeps the direct, allocation-free form.
        Assert.Equal("[int]$global:m = 5", BashTranspiler.Transpile("declare -i m=5"));
    }

    // Regression: a path-like `.sh` script invocation must run through `bash`, not be emitted
    // bare. Bare `./x.sh` makes PowerShell treat the file as a "document": it ShellExecutes one
    // standalone (never running the script body) and ERRORS inside a pipeline ("Cannot run a
    // document in the middle of a pipeline"). Routing through bash runs it and composes in a pipe.
    [Fact]
    public void LocalShellScript_RelativePath_RoutedThroughBash()
    {
        Assert.Equal("bash ./scripts/test.sh", BashTranspiler.Transpile("./scripts/test.sh"));
    }

    [Fact]
    public void LocalShellScript_InPipeline_RoutedThroughBash()
    {
        var result = BashTranspiler.Transpile("./scripts/test.sh foo | tail -30");
        Assert.Equal("bash ./scripts/test.sh foo | Invoke-BashTail -30", result);
    }

    [Fact]
    public void LocalShellScript_AbsoluteDotDotAndQuotedTildePaths_RoutedThroughBash()
    {
        Assert.StartsWith("bash /opt/x.sh", BashTranspiler.Transpile("/opt/x.sh"));
        Assert.StartsWith("bash ../build.sh", BashTranspiler.Transpile("../build.sh"));
        // A QUOTED tilde is a literal path (bash does not home-expand inside quotes), so it routes
        // through bash. An UNQUOTED ~/x.sh is parsed as a tilde expansion ($HOME) and keeps the
        // pre-existing tilde-path behavior — not rewritten (conservative: only plain literal paths).
        Assert.Equal("bash '~/dotfiles/setup.bash'", BashTranspiler.Transpile("'~/dotfiles/setup.bash'"));
    }

    [Fact]
    public void LocalShellScript_QuotedPath_RoutedThroughBashNotCallOperator()
    {
        // A quoted command word would normally get the `& ` call operator; a quoted script path
        // takes the `bash ` prefix instead so it still executes correctly.
        Assert.Equal("bash './build.sh'", BashTranspiler.Transpile("'./build.sh'"));
    }

    [Fact]
    public void BareShScriptWithoutPath_NotRewritten_MatchesBashPathLookup()
    {
        // A bare `x.sh` (no directory component) is a PATH/command lookup in bash, not a local
        // file run — so it is NOT rewritten to `bash x.sh`.
        Assert.DoesNotContain("bash ", BashTranspiler.Transpile("foo.sh arg"));
    }

    [Fact]
    public void ForceClobberRedirect_TreatedAsPlainStdoutRedirect()
    {
        var result = BashTranspiler.Transpile("echo hi >| out.txt");
        Assert.Equal("Invoke-BashEcho hi | Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void InputRedirectFromDevNull_DroppedNotEmittedAsGetContentNull()
    {
        // `Get-Content $null` throws "Cannot bind argument to parameter 'Path'
        // because it is null." — so drop the redirect entirely.
        var result = BashTranspiler.Transpile("eval 'echo hi' < /dev/null");
        Assert.DoesNotContain("Get-Content $null", result);
    }

    [Fact]
    public void MsysDrivePathInRedirectTarget_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("echo hi > /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashRedirect -Path C:\\Users\\andyb\\foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void MsysDrivePathInRedirectTarget_PreservedLiteral_WhenUnixPathsOff()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "0");
        try
        {
            var result = BashTranspiler.Transpile("echo hi > /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashRedirect -Path /c/Users/andyb/foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    // Regression: MSYS drive paths were translated only in redirect targets, not
    // in plain command operands. `cat /c/Users/andyb/foo` passed `/c/Users/...`
    // through verbatim; the runtime resolved it against the current drive and got
    // `C:\c\Users\...` ("No such file or directory"). Captured live from the Claude
    // Code Bash tool, which runs with PSBASH_UNIX_PATHS=1.
    [Fact]
    public void MsysDrivePathInCommandOperand_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("cat /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashCat C:\\Users\\andyb\\foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void MsysDrivePathInCommandOperand_PreservedLiteral_WhenUnixPathsOff()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "0");
        try
        {
            var result = BashTranspiler.Transpile("cat /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashCat /c/Users/andyb/foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    // WSL-style drive paths (/mnt/c/...) must translate the same as MSYS /c/...,
    // since LLMs emit both. Operand + cd both go through the shared WindowsPath
    // mapper now.
    [Fact]
    public void WslDrivePathInCommandOperand_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("cat /mnt/c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashCat C:\\Users\\andyb\\foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void WslDrivePathInCdTarget_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("cd /mnt/c/Users/andyb");
            Assert.Contains("$__psbash_cd_target = 'C:\\Users\\andyb'", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void WslDrivePathInRedirectTarget_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("echo hi > /mnt/c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashRedirect -Path C:\\Users\\andyb\\foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    // Same gap for `cd`: `cd /c/Users/andyb` resolved `/c/Users/andyb` against the
    // current drive via [Path]::GetFullPath -> `C:\c\Users\andyb`, which does not
    // exist, so every `cd /c/...` failed with "No such file or directory".
    [Fact]
    public void MsysDrivePathInCdTarget_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("cd /c/Users/andyb");
            Assert.Contains("$__psbash_cd_target = 'C:\\Users\\andyb'", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void MsysDrivePathInCdTarget_PreservedLiteral_WhenUnixPathsOff()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "0");
        try
        {
            var result = BashTranspiler.Transpile("cd /c/Users/andyb");
            Assert.Contains("$__psbash_cd_target = '/c/Users/andyb'", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    // Regression: a compound command (for/while/if/case, subshell, brace group)
    // used as a pipeline stage emits PowerShell *statements* — e.g. a `for` loop
    // becomes `$__psbash_iter = 0; foreach (...) { ... }`. PowerShell cannot pipe a
    // bare statement: `foreach (...) {} | Invoke-BashSort` is a parse error
    // ("An empty pipe element is not allowed"). The stage must be wrapped in
    // `& { ... }` so it becomes a pipeable command (and matches bash, which runs
    // every pipe stage in its own subshell). Captured live from the Claude Code
    // Bash tool running `for d in ...; do echo ...; done | <cmd>`.
    [Fact]
    public void Transpile_ForLoopPipedIntoCommand_WrapsLoopInScriptBlockSoPipeIsValid()
    {
        var result = BashTranspiler.Transpile("for d in a b; do echo $d; done | sort");
        // The loop stage must be wrapped so the pipe has a real left operand.
        // The broken form started with `$__psbash_iter = 0; foreach (...) {...} | ...`,
        // a bare statement piped — which fails to parse. The wrapped form starts `& {`.
        Assert.StartsWith("& {", result);
        Assert.Contains("foreach (", result);
        Assert.Contains("} | Invoke-BashSort", result);
    }

    // Same defect for a brace group as the FIRST pipe stage (previously only
    // wrapped at stage index > 0). `{ echo a; echo b; } | sort` must wrap.
    [Fact]
    public void Transpile_BraceGroupAsFirstPipeStage_WrapsInScriptBlock()
    {
        var result = BashTranspiler.Transpile("{ echo a; echo b; } | sort");
        Assert.StartsWith("& {", result);
        Assert.Contains("| Invoke-BashSort", result);
    }

    // Subshell as the first pipe stage emits `try { } finally { }`, also not a
    // valid bare pipeline segment. Must wrap.
    [Fact]
    public void Transpile_SubshellAsFirstPipeStage_WrapsInScriptBlock()
    {
        var result = BashTranspiler.Transpile("(echo a; echo b) | sort");
        Assert.StartsWith("& {", result);
        Assert.Contains("| Invoke-BashSort", result);
    }

    [Fact]
    public void ClaudeCodeBashWrapper_TranspilesWithoutEmptyPipeOrAssignmentJunk()
    {
        // Captured live from Claude Code Bash tool argv tracing.
        var input = "shopt -u extglob 2>/dev/null || true && eval 'echo hi' < /dev/null && pwd -P >| /tmp/cwd";
        var result = BashTranspiler.Transpile(input);
        // The two specific failures we observed must not appear:
        Assert.DoesNotContain("|| $global:LASTEXITCODE = 0;", result);
        Assert.DoesNotContain("Invoke-BashRedirect -Path |", result);
        // And the force-clobber must produce a real path arg.
        Assert.Contains("Invoke-BashRedirect -Path", result);
    }

    // Regression: Claude Code's Bash tool prepends an env-setup preamble that
    // sets TEMP and TMP in one bare assignment before the real command:
    //   ... && TEMP='C:\...\Temp' TMP='C:\...\Temp' && <cmd>
    // A multi-pair ShAssignment emits "$env:A = ..; $env:B = .." — two statements
    // joined by "; ". In a && / || chain the emitter wrapped that as
    //   [void]($env:A = ..; $env:B = ..)
    // but PowerShell's grouping `(...)` cannot hold a ';'-separated statement
    // list, so the host's PowerShell parser rejected it ("Missing closing ')'")
    // and EVERY Bash-tool command failed. The fix uses `[void]$(...)` (a
    // subexpression, which does allow a statement list) for the multi-pair case.
    // The fixed string below was verified to parse AND run in pwsh 7.
    [Fact]
    public void Transpile_MultiVarAssignmentInAndChain_UsesSubexpressionNotGrouping()
    {
        var result = BashTranspiler.Transpile("A=1 B=2 && echo hi");
        Assert.Equal("[void]$($env:A = \"1\"; $env:B = \"2\") && Invoke-BashEcho hi", result);
    }

    // Single-pair assignment is a single statement — valid inside `(...)`. Keep
    // the cheaper grouping form (bit-identical to the prior emission) so the
    // ExportWithAnd_WrapsInVoid contract is preserved.
    [Fact]
    public void Transpile_SingleVarAssignmentInAndChain_StaysInVoidGrouping()
    {
        var result = BashTranspiler.Transpile("A=1 && echo hi");
        Assert.Equal("[void]($env:A = \"1\") && Invoke-BashEcho hi", result);
    }

    // Three-pair assignment: still one subexpression, all statements inside.
    [Fact]
    public void Transpile_ThreeVarAssignmentInAndChain_UsesSubexpression()
    {
        var result = BashTranspiler.Transpile("A=1 B=2 C=3 && echo hi");
        Assert.Equal("[void]$($env:A = \"1\"; $env:B = \"2\"; $env:C = \"3\") && Invoke-BashEcho hi", result);
    }

    // The full env-setup wrapper shape captured live from the Claude Code Bash
    // tool: a `|| true` guard, then the multi-var TEMP/TMP assignment, then the
    // real command. The multi-var assignment must use the parseable `[void]$(...)`
    // form, never the unparseable `[void](...; ...)` grouping.
    [Fact]
    public void Transpile_ClaudeCodeEnvSetupWrapper_ProducesParseablePowerShell()
    {
        var input = "shopt -u extglob 2>/dev/null || true && "
                  + "TEMP='C:\\Users\\me\\Temp' TMP='C:\\Users\\me\\Temp' && echo hi";
        var result = BashTranspiler.Transpile(input);
        // The TEMP/TMP assignment is a two-statement subexpression, not a grouping.
        Assert.Contains("[void]$($env:TEMP = 'C:\\Users\\me\\Temp'; $env:TMP = 'C:\\Users\\me\\Temp')", result);
        // The pre-fix unparseable shape must never reappear.
        Assert.DoesNotContain("[void]($env:TEMP", result);
    }

    [Fact]
    public void DevNullWithEnvVar_TransformsBoth()
    {
        // RC-7: bare unquoted $FOO operand → word-split splat (oracle:
        // Differential_UnquotedVar_WordSplitsOnSpaces). The 2>$null redirect
        // is appended after the splat command wrapper.
        var result = BashTranspiler.Transpile("echo $FOO 2> /dev/null");
        Assert.Equal(
            "& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:FOO)) " +
            "{ @() } else { @($env:FOO -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
            "Invoke-BashEcho @__bashsplat0 } 2>$null",
            result);
    }

    [Fact]
    public void ExportAndEchoVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("export FOO=bar");
        Assert.Equal("$env:FOO = \"bar\"", result);
    }

    [Fact]
    public void TmpPathWithGrep_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat /tmp/log.txt | grep error");
        // All-mapped, terminal-bound pipeline → fused lane (PERF phase 2).
        Assert.Equal("Invoke-BashFusedPipeline { Invoke-BashCat $env:TEMP\\log.txt | Invoke-BashGrep error }", result);
    }

    [Fact]
    public void FileTestWithVar_TransformsBoth()
    {
        // RC-7: the `echo $MSG` operand is a bare unquoted env var → word-split
        // splat, wrapped in $(& { ... }) as a single and-or-list element.
        var result = BashTranspiler.Transpile("[ -f /etc/config ] && echo $MSG");
        Assert.Equal("$(if ((Test-Path \"/etc/config\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && $(& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:MSG)) { @() } else { @($env:MSG -split '\\s+' | Where-Object { $_ -ne '' }) }); Invoke-BashEcho @__bashsplat0 })", result);
    }

    [Fact]
    public void HomePathWithPipe_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("ls ~/.config | head -n 5");
        Assert.Equal("Invoke-BashLs $HOME\\.config | Invoke-BashHead -n 5", result);
    }

    [Fact]
    public void ComplexPipeline_TransformsAll()
    {
        var result = BashTranspiler.Transpile("cat /tmp/data.csv | grep -v header | sort | uniq | wc -l");
        // All-mapped, terminal-bound pipeline → fused lane (PERF phase 2).
        Assert.Equal(
            "Invoke-BashFusedPipeline { Invoke-BashCat $env:TEMP\\data.csv | Invoke-BashGrep -v header | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l }",
            result);
    }

    [Fact]
    public void ExportQuotedValue_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("export NODE_ENV=\"production\"");
        Assert.Equal("$env:NODE_ENV = \"production\"", result);
    }

    [Fact]
    public void DevNullRedirectWithStderrMerge_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("cmd > /dev/null 2>&1");
        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void EnvVarDoesNotDoubleTransform()
    {
        var result = BashTranspiler.Transpile("export FOO=bar && echo $FOO");
        Assert.Contains("$env:FOO = \"bar\"", result);
        Assert.Contains("$env:FOO", result);
        Assert.DoesNotContain("$env:env:", result);
    }

    [Fact]
    public void PipeSedAndAwk_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat file | sed 's/old/new/' | awk '{print $1}'");
        Assert.Equal("Invoke-BashCat file | Invoke-BashSed 's/old/new/' | Invoke-BashAwk '{print $1}'", result);
    }

    [Fact]
    public void AwkWithFlags_PreservesExpression()
    {
        var result = BashTranspiler.Transpile("echo \"a,b,c\" | awk -F, '{print $1, $3}'");
        Assert.Equal("Invoke-BashEcho \"a,b,c\" | Invoke-BashAwk \"-F,\" '{print $1, $3}'", result);
    }

    [Fact]
    public void FileTestEmptyVar_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("[ -z \"$HOME\" ] && echo empty");
        Assert.Equal("$(if (([string]::IsNullOrEmpty($HOME))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && Invoke-BashEcho empty", result);
    }

    [Fact]
    public void FileTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f ./README.md ] && echo \"exists\"");
        Assert.Equal("$(if ((Test-Path \"./README.md\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && Invoke-BashEcho \"exists\"", result);
    }

    [Fact]
    public void DirTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -d ./src ] && echo \"is dir\"");
        Assert.Equal("$(if ((Test-Path \"./src\" -PathType Container)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && Invoke-BashEcho \"is dir\"", result);
    }

    [Fact]
    public void ExportWithAnd_WrapsInVoid()
    {
        // RC-7: `echo $FOO` operand → word-split splat, wrapped in $(& { ... })
        // as a single and-or-list element after the [void] export assignment.
        var result = BashTranspiler.Transpile("export FOO=\"bar\" && echo $FOO");
        Assert.Equal(
            "[void]($env:FOO = \"bar\") && $(& { $__bashsplat0 = " +
            "@(if ([string]::IsNullOrEmpty($env:FOO)) { @() } " +
            "else { @($env:FOO -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
            "Invoke-BashEcho @__bashsplat0 })",
            result);
    }

    [Fact]
    public void FileTestWithOr_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f missing ] || echo \"not found\"");
        Assert.Equal("$(if ((Test-Path \"missing\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) || Invoke-BashEcho \"not found\"", result);
    }

    [Fact]
    public void TmpPath_TransformsToEnvTemp()
    {
        var result = BashTranspiler.Transpile("echo /tmp/test");
        Assert.Contains("$env:TEMP", result);
    }

    // `eval` with a fully static body (no $-expansions, command sub, arithmetic,
    // process sub, glob, or brace expansion) is reconstructed and re-transpiled
    // inline at parse time — bypassing Invoke-BashEval entirely. The runtime
    // cmdlet path is reserved for bodies with runtime expansions whose value
    // can only be known when the script actually runs.
    [Fact]
    public void Transpile_EvalWithSingleQuotedLiteral_InlinesAtParseTime()
    {
        // `eval 'echo hello'` — quote removal yields `echo hello`, which is
        // re-parsed and emitted as a normal mapped-command call.
        var result = BashTranspiler.Transpile("eval 'echo hello'");
        Assert.Equal("Invoke-BashEcho hello", result);
    }

    [Fact]
    public void Transpile_EvalWithDoubleQuotedStaticString_InlinesAtParseTime()
    {
        // `eval "echo hello"` — same body as the single-quoted case after
        // quote removal; inline-transpiled identically.
        var result = BashTranspiler.Transpile("eval \"echo hello\"");
        Assert.Equal("Invoke-BashEcho hello", result);
    }

    // Multiple args: bash's `eval` joins arg values with a single space before
    // re-parsing. The emitter joins the reconstructed sources and re-transpiles
    // inline when every arg is static.
    [Fact]
    public void Transpile_EvalWithMultipleStaticArgs_InlinesJoinedSource()
    {
        var result = BashTranspiler.Transpile("eval echo hi");
        Assert.Equal("Invoke-BashEcho hi", result);
    }

    // `eval "$(cmd)"` — the canonical fnm/direnv/venv-activation pattern. The
    // command substitution is emitted as a normal pwsh subexpression so pwsh
    // expands it at runtime; the resulting string is re-transpiled in
    // TranspileContext.Eval inline in the generated script.
    [Fact]
    public void Transpile_EvalWithCommandSubstitution_EmitsRuntimeSubexpression()
    {
        var result = BashTranspiler.Transpile("eval \"$(fnm env --shell bash)\"");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
        // The $(...) inside the eval arg becomes a pwsh subexpression that
        // calls the mapped cmdlet at runtime. fnm isn't a mapped command so
        // it stays bare, but the surrounding $(...) shape must be there.
        Assert.Contains("$(", result);
        Assert.Contains("--shell", result);
    }

    [Fact]
    public void Transpile_EvalWithMappedCommandSubstitution_TranspilesInner()
    {
        // printf IS mapped, so the inner $(printf 'x=5') becomes
        // $(Invoke-BashPrintf 'x=5') — pwsh evaluates that at runtime, then
        // the inline eval block transpiles it as a bare assignment
        // (`x=5` → `$env:x = "5"`) and Invoke-Expressions it.
        var result = BashTranspiler.Transpile("eval \"$(printf 'x=5')\"");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
        Assert.Contains("Invoke-BashPrintf", result);
    }

    [Fact]
    public void Transpile_EvalWithBackquoteCommandSub_EmitsRuntimeSubexpression()
    {
        var result = BashTranspiler.Transpile("eval `fnm env --shell bash`");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
        Assert.Contains("$(", result);
    }

    // Arithmetic expansion in eval: $((1+2)) becomes a pwsh subexpression that
    // evaluates the arithmetic at runtime and feeds the resulting string to
    // the inline eval block.
    [Fact]
    public void Transpile_EvalWithArithmeticExpansion_EmitsRuntimeSubexpression()
    {
        var result = BashTranspiler.Transpile("eval \"echo $((1 + 2))\"");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
    }

    // Variable references inside the eval string: the emitter forwards them
    // to pwsh inside the eval arg string so pwsh interpolates at runtime, and
    // the inline eval block then re-transpiles the joined string.
    [Fact]
    public void Transpile_EvalWithVariableReference_ForwardsToRuntimeEval()
    {
        var result = BashTranspiler.Transpile("eval \"export X=$HOME\"");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
        // $HOME stays as $HOME in the pwsh string (kept-as-is special var).
        Assert.Contains("$HOME", result);
    }

    [Fact]
    public void Transpile_EvalWithUserVariable_ForwardsToRuntimeEval()
    {
        var result = BashTranspiler.Transpile("eval \"export X=$MYVAR\"");
        Assert.Contains("BashTranspiler, PsBash.Transpiler]::Transpile", result);
        Assert.Contains("Invoke-Expression", result);
        Assert.Contains("$env:MYVAR", result);
    }

    [Fact]
    public void Transpile_EvalWithBracedVarSub_InlinesAndExpandsInnerVar()
    {
        // Single quotes around the body keep `${USER}` literal at the OUTER
        // parse — quote removal hands the eval body `echo ${USER}` to the
        // recursive transpile, where ${USER} is resolved to a normal pwsh env
        // reference. The variable expansion is therefore deferred to pwsh
        // runtime, not to a runtime eval cmdlet.
        var result = BashTranspiler.Transpile("eval 'echo ${USER}'");
        // RC-7: the inlined `echo ${USER}` has a bare unquoted env-var operand
        // → word-split splat (oracle: Differential_UnquotedVar_WordSplitsOnSpaces).
        Assert.Equal(
            "& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:USER)) " +
            "{ @() } else { @($env:USER -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
            "Invoke-BashEcho @__bashsplat0 }",
            result);
    }

    // Static nested eval: the outer body is fully static, so reconstruction
    // hands the inner `eval "echo hi"` to the recursive transpile, which
    // inlines that level too. Nested static eval collapses to the innermost
    // command at parse time.
    [Fact]
    public void Transpile_NestedStaticEval_InlinesAllLevels()
    {
        var result = BashTranspiler.Transpile("eval 'eval \"echo hi\"'");
        Assert.Equal("Invoke-BashEcho hi", result);
    }

    // Bare `eval` with no args is a no-op in bash. We emit the cmdlet name
    // alone — the cmdlet exits 0 with no work.
    [Fact]
    public void Transpile_EvalNoArgs_EmitsNoOpSuccess()
    {
        var result = BashTranspiler.Transpile("eval");
        Assert.Equal("$global:LASTEXITCODE = 0", result);
    }

    // Recorded fnm output (`fnm env --shell bash` on Windows). The eval target
    // contains `export NAME=value` lines and is exactly what activation tools
    // produce. Verify the OUTER eval call emits the runtime dispatch — the
    // INNER bash content runs through TranspileContext.Eval at runtime.
    [Fact]
    public void Transpile_EvalWithFnmFixture_EmitsRuntimeCall()
    {
        // Real fnm env --shell bash output (trimmed to the relevant exports).
        var fnmBody = "export PATH=\"/c/Users/me/AppData/Local/fnm_multishells/12345_1234567890:$PATH\"\n" +
                      "export FNM_MULTISHELL_PATH=\"/c/Users/me/AppData/Local/fnm_multishells/12345_1234567890\"\n" +
                      "export FNM_VERSION_FILE_STRATEGY=\"local\"";

        // Verify the fnm body itself transpiles cleanly under TranspileContext.Eval —
        // this is what the inline runtime eval block will do after pwsh expands $(fnm env ...).
        var transpiled = BashTranspiler.Transpile(fnmBody, TranspileContext.Eval);
        Assert.Contains("$env:PATH", transpiled);
        Assert.Contains("$env:FNM_MULTISHELL_PATH", transpiled);
        Assert.Contains("$env:FNM_VERSION_FILE_STRATEGY", transpiled);
    }

    // TranspileContext.Eval must force every mapped command — including those
    // that have pwsh built-in aliases (ls, cat, echo, sort, diff, cp, mv, rm,
    // mkdir, sleep, pwd) — to emit as Invoke-Bash*. The in-process eval host
    // (Invoke-BashEval cmdlet) cannot depend on the host's alias table having
    // PsBash loaded.
    [Fact]
    public void Transpile_LsWithFlags_UnderEval_EmitsInvokeBashLs()
    {
        var result = BashTranspiler.Transpile("ls -la", TranspileContext.Eval);
        Assert.Equal("Invoke-BashLs -la", result);
    }

    [Fact]
    public void Transpile_Sleep_UnderEval_EmitsInvokeBashSleep()
    {
        var result = BashTranspiler.Transpile("sleep 1", TranspileContext.Eval);
        Assert.Equal("Invoke-BashSleep 1", result);
    }

    [Fact]
    public void Transpile_Diff_UnderEval_EmitsInvokeBashDiff()
    {
        var result = BashTranspiler.Transpile("diff a b", TranspileContext.Eval);
        Assert.Equal("Invoke-BashDiff a b", result);
    }

    [Fact]
    public void Transpile_Pwd_UnderEval_EmitsInvokeBashPwd()
    {
        var result = BashTranspiler.Transpile("pwd", TranspileContext.Eval);
        Assert.Equal("Invoke-BashPwd", result);
    }

    // Existing Default-context behavior must be preserved bit-for-bit.
    [Fact]
    public void Transpile_LsWithFlags_DefaultContext_MatchesNoContextOverload()
    {
        var withCtx = BashTranspiler.Transpile("ls -la", TranspileContext.Default);
        var noCtx = BashTranspiler.Transpile("ls -la");
        Assert.Equal(noCtx, withCtx);
    }

    [Fact]
    public void Transpile_ContextScope_RestoredAfterCall()
    {
        // Call under Eval, then default overload must still behave as Default.
        BashTranspiler.Transpile("ls", TranspileContext.Eval);
        var result = BashTranspiler.Transpile("ls");
        // Default behavior today rewrites standalone ls to Invoke-BashLs; the
        // important check is that the call succeeded and produced the normal
        // emission (no leakage of Eval-specific state breaks anything).
        Assert.Equal("Invoke-BashLs", result);
    }

    // Regression: f() { return 42; }; f; echo $? — process was exiting 42 instead of 0.
    // `return N` inside a function body must emit $global:LASTEXITCODE = N; return
    // so the function exits with N as its exit status without exiting the shell.
    [Fact]
    public void Transpile_FunctionReturnN_EmitsLastExitCodeAssignThenReturn()
    {
        var result = BashTranspiler.Transpile("f() { return 42; }; f; echo $?");
        // The function body must set LASTEXITCODE, not call exit
        Assert.Contains("$global:LASTEXITCODE = 42; return", result);
        // Must not use bare exit which would terminate the ps-bash process
        Assert.DoesNotContain("exit 42", result);
    }

    [Fact]
    public void Transpile_ReturnWithNoArg_EmitsBareReturn()
    {
        var result = BashTranspiler.Transpile("f() { return; }");
        // return with no arg: just return (no LASTEXITCODE assignment)
        Assert.Contains("{ return }", result);
        Assert.DoesNotContain("$global:LASTEXITCODE", result);
    }

    // ===================== Redirects on compound commands (silent-data-loss regression) =====================
    // Only Command.Simple/Subshell carried a Redirects field, so a trailing redirect on a
    // while/for/if/case/brace-group was silently dropped: `while read line; do ...; done < in`
    // read unbound stdin, `for ...; done > out` went to the console, etc. The compound now
    // wraps in `& { ... }` and applies the redirect (input via Get-Content, output via
    // Invoke-BashRedirect). Assertions below check the emitted PS carries the redirect.

    [Fact]
    public void Transpile_WhileReadWithInputRedirect_FeedsFileViaGetContent()
    {
        var result = BashTranspiler.Transpile("while read line; do echo $line; done < input.txt");
        // The file must be fed into the loop, not left unbound.
        Assert.StartsWith("Get-Content input.txt |", result);
    }

    [Fact]
    public void Transpile_ForInWithOutputRedirect_RoutesThroughInvokeBashRedirect()
    {
        var result = BashTranspiler.Transpile("for x in 1 2; do echo $x; done > out.txt");
        Assert.Contains("| Invoke-BashRedirect -Path out.txt", result);
        // The loop must be grouped so the redirect covers the whole construct.
        Assert.StartsWith("& {", result);
    }

    [Fact]
    public void Transpile_IfWithOutputRedirect_RoutesThroughInvokeBashRedirect()
    {
        var result = BashTranspiler.Transpile("if true; then echo hi; fi > log.txt");
        Assert.Contains("| Invoke-BashRedirect -Path log.txt", result);
        Assert.StartsWith("& {", result);
    }

    [Fact]
    public void Transpile_BraceGroupWithOutputRedirect_RoutesThroughInvokeBashRedirect()
    {
        var result = BashTranspiler.Transpile("{ echo a; echo b; } > out.txt");
        Assert.Contains("| Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_CaseWithOutputRedirect_RoutesThroughInvokeBashRedirect()
    {
        var result = BashTranspiler.Transpile("case $x in a) echo hi;; esac > out.txt");
        Assert.Contains("| Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_ForArithWithOutputRedirect_RoutesThroughInvokeBashRedirect()
    {
        var result = BashTranspiler.Transpile("for ((i=0;i<3;i++)); do echo $i; done > out.txt");
        Assert.Contains("| Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_WhileReadRedirectThenPipe_KeepsBothRedirectAndPipe()
    {
        // Cascade repro: the dropped redirect used to also swallow the `| sort` stage.
        var result = BashTranspiler.Transpile("while read a; do echo $a; done < f | sort");
        Assert.Contains("Get-Content f |", result);
        Assert.Contains("Invoke-BashSort", result);
    }

    [Fact]
    public void Transpile_ForWithoutRedirect_IsNotWrappedInScriptBlock()
    {
        // A non-redirected compound must be unchanged (no spurious `& { }` grouping).
        var result = BashTranspiler.Transpile("for x in 1 2; do echo $x; done");
        Assert.DoesNotContain("Invoke-BashRedirect", result);
        Assert.StartsWith("$__psbash_iter", result);
    }

    // Documents the stdin-delivery limitation of ApplyCompoundRedirects: an input redirect on a
    // NON-while-read compound emits the SAME `Get-Content <file> | & { ... }` shape as the identical
    // subshell construct. A bare inner `read` does not auto-drain $input in either — a pre-existing,
    // codebase-wide limitation (parity with EmitSubshell), NOT a regression from this fix. This test
    // pins the parity so a future stdin bridge lands uniformly for both compound and subshell.
    [Fact]
    public void Transpile_IfWithInputRedirect_MatchesSubshellStdinModel()
    {
        var compound = BashTranspiler.Transpile("if true; then read line; echo $line; fi < input.txt");
        var subshell = BashTranspiler.Transpile("(read line; echo $line) < input.txt");
        // Both place the file on the group's pipeline via Get-Content.
        Assert.StartsWith("Get-Content input.txt | & {", compound);
        Assert.StartsWith("Get-Content input.txt | & {", subshell);
    }
}
