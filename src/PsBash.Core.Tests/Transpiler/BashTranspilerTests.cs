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

    [Fact]
    public void DevNullWithEnvVar_TransformsBoth()
    {
        // RC-7: bare unquoted $FOO operand → word-split splat (oracle:
        // Differential_UnquotedVar_WordSplitsOnSpaces). The 2>$null redirect
        // is appended after the splat command wrapper.
        var result = BashTranspiler.Transpile("echo $FOO 2> /dev/null");
        Assert.Equal(
            "& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:FOO)) " +
            "{ @() } else { @($env:FOO -split '\\s+') }); " +
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
        Assert.Equal("Invoke-BashCat $env:TEMP\\log.txt | Invoke-BashGrep error", result);
    }

    [Fact]
    public void FileTestWithVar_TransformsBoth()
    {
        // RC-7: the `echo $MSG` operand is a bare unquoted env var → word-split
        // splat, wrapped in $(& { ... }) as a single and-or-list element.
        var result = BashTranspiler.Transpile("[ -f /etc/config ] && echo $MSG");
        Assert.Equal("$(if ((Test-Path \"/etc/config\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && $(& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:MSG)) { @() } else { @($env:MSG -split '\\s+') }); Invoke-BashEcho @__bashsplat0 })", result);
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
        Assert.Equal(
            "Invoke-BashCat $env:TEMP\\data.csv | Invoke-BashGrep -v header | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l",
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
            "else { @($env:FOO -split '\\s+') }); " +
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
            "{ @() } else { @($env:USER -split '\\s+') }); " +
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
}
