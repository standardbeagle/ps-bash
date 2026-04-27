# Interactive Parity Audit

QA audit sections per qa-rubric Directive 10 template.

---

### FEATURE: source / .

**EXISTING TESTS** (file:test):
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceEnvFile_SetsEnvVarInCallerScope`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourcePs1File_DotSourcesNatively`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceWithArguments_SetsPositionalParams`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceBashScript_TranspilesAndEvals`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceNonExistentPs1File_WritesError`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceNonExistentShFile_WritesError`
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceShFile_RelativePath_ResolvesAgainstCwd` (added)
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:SourceWithMultipleArguments_SetsAllPositionalParams` (added)
- `src/PsBash.Cmdlets.Tests/InvokeBashSourceCommandTests.cs:NestedSource_ScriptASourcesScriptB_BExportsVisibleInCaller` (added)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SourceShFile_EmitsInvokeBashSource`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_DotSourceShFile_EmitsInvokeBashSource`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SourceShFileWithArgs_EmitsInvokeBashSourceWithArgs`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `SourceBashScript_TranspilesAndEvals` sources whitespace-only files via `IsNullOrWhiteSpace` guard in cmdlet |
| Large input | NO | gap — no test streams a large .sh file through `Invoke-BashSource` |
| Unicode | NO | gap — no test uses non-ASCII content in a sourced script |
| CRLF input | NO | gap — `StreamReader` will pass CRLF to transpiler; untested |
| Broken pipe | N/A | source is not a pipeline consumer; skip justified |
| Slow reader | N/A | source reads from file, not pipeline; skip justified |
| Signal during execute | NO | gap — no test for Ctrl-C mid-source |
| Exit code propagation | PARTIAL | `SourceNonExistentShFile_WritesError` tests error path; no test for `source` returning non-zero from a failing script |
| Stderr interleave | NO | gap — no test for `source script.sh 2>&1` |
| Working dir state | YES | `SourceShFile_RelativePath_ResolvesAgainstCwd` |
| Environment leak | YES | `SourceEnvFile_SetsEnvVarInCallerScope` — env var is visible in caller (intentional leak) |
| Quoting / injection | NO | gap — no test for a source path containing spaces or injection chars |
| Platform-locked file | N/A | source reads; does not write or lock; skip justified |
| Missing target | YES | `SourceNonExistentPs1File_WritesError`, `SourceNonExistentShFile_WritesError` |
| Recursion depth | YES | `NestedSource_ScriptASourcesScriptB_BExportsVisibleInCaller` — one level of nesting |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | NO | gap — no canary or shell test exercises `source` via `ps-bash -c "source file.sh"` |
| M2 stdin pipe | NO | gap — no test pipes `echo "source file.sh"` through ps-bash stdin |
| M3 file arg | NO | gap — no test where the outer script passed to ps-bash contains a `source` call |
| M4 interactive | NO | gap — no PTY test for interactive `source` at the REPL prompt |
| M5 Invoke-BashEval | NO | gap — no test evaluates a `source` expression via `Invoke-BashEval` |
| M6 Invoke-BashSource | YES | all `InvokeBashSourceCommandTests` tests use M6 directly |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? NO
- WHY: `Invoke-BashSource` is a ps-bash-specific cmdlet with no direct bash subprocess equivalent for in-process scope sharing. Differential tests would require a bash oracle for env-var visibility, which differs by design (bash uses subprocess subshell, ps-bash uses shared runspace scope). Exception per Directive 1: behavior is ps-bash-specific.

**KNOWN BUGS / RISKS**:
- `.ps1` dot-source does not pass `$global:BashPositional`; positional args are silently ignored for `.ps1` files — `InvokeBashSourceCommand.cs:37-44`
- Single-quote escaping in nested source paths uses `\'` (bash-style) inside the transpiled script body; PowerShell uses `''` — mismatch may cause parse errors for paths with single quotes when sourced via a script that itself calls `source`
- Flaky macOS risk noted in MEMORY.md — `Invoke-BashSource` tests on macOS; no known current repro but quarantine tag should be applied if flakes emerge

**PRIORITY GAPS** (top 3 max):
1. M1/M2/M3 mode coverage: add at least one canary or shell-level test that invokes `source` via a spawn mode so that transpiler-to-runtime integration is validated end-to-end (not just the cmdlet in isolation)
2. Source path with spaces/special chars: add a test where the sourced file path contains a space; the current single-quote escaping in `InvokeBashSourceCommand.cs:39` would fail for a path like `/tmp/my script.sh`
3. Exit-code propagation from failing sourced script: add a test where the sourced `.sh` contains a failing command and assert that the error surfaces to the caller

---

### FEATURE: if/elif/else/fi

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_IfThenFi_ReturnsIfNodeWithOneArm`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_IfThenElseFi_ReturnsIfNodeWithElse`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_IfElifElseFi_ReturnsIfNodeWithMultipleArms`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_IfTestConstruct_ParsesTestAsBoolExpr`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_NestedIf_ParsesCorrectly`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_IfWithMultipleBodyCommands_ReturnsCommandList`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MissingFi_ThrowsParseExceptionWithLineAndColumn`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MissingThen_ThrowsParseException`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ElifMissingFi_ThrowsParseException`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MultilineIfError_ReportsLine2`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfThenFi_EmitsIfBlock`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfThenElseFi_EmitsIfElseBlock`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfElifElseFi_EmitsFullChain`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfFileTest_EmitsTestPath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfDirTest_EmitsTestPathContainer`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_IfWithMultipleBodyCommands_EmitsAll`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NestedIf_EmitsNestedBlocks`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_CompoundAndCondition`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_PlainCommandConditionFails`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_ElifChainWithTestExpr`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `Parse_EmptyInput_ReturnsNull` (parser layer) |
| Large input | NO | gap — no test for an if-body with a large heredoc or many commands |
| Unicode | NO | gap — no test with non-ASCII in condition string or body |
| CRLF input | NO | gap — parser CRLF handling untested for if-blocks |
| Broken pipe | N/A | if is a control-flow construct, not a pipeline consumer; skip justified |
| Slow reader | N/A | same reason as broken pipe; skip justified |
| Signal during execute | NO | gap — no Ctrl-C test mid-if-body |
| Exit code propagation | YES | `Differential_If_PlainCommandConditionFails` — false exits 1, drives else branch |
| Stderr interleave | NO | gap — no test for `2>&1` inside if-body |
| Working dir state | NO | gap — no test that cd inside if-body persists |
| Environment leak | NO | gap — no test that VAR=x inside if-body leaks |
| Quoting / injection | YES | `Differential_If_ElifChainWithTestExpr` — quoted `"$x"` in `[ ... ]` |
| Platform-locked file | N/A | if is pure control flow; skip justified |
| Missing target | PARTIAL | `Parse_MissingFi_ThrowsParseExceptionWithLineAndColumn` (parse error); no runtime missing-cmd test |
| Recursion depth | NO | gap — no deeply nested if chain test |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | `Differential_If_*` tests invoke ps-bash with `-c`; oracle runs bash `-c` |
| M2 stdin pipe | NO | gap — no test pipes an if-script through ps-bash stdin |
| M3 file arg | NO | gap — no test passes a .sh file containing if/elif to ps-bash |
| M4 interactive | NO | gap — no PTY test for multi-line if entered at the REPL |
| M5 Invoke-BashEval | NO | gap — no eval test for if expressions |
| M6 Invoke-BashSource | NO | gap — no sourced script test containing if/elif |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 3 differential tests added in `ControlFlowDifferentialTests.cs`: compound-and condition, plain-command condition fails, elif chain with `[ ... ]` test expr

**KNOWN BUGS / RISKS**:
- Compound condition `if cmd1 && cmd2` in the if-condition position: the `&&` inside `if (...)` is emitted as PowerShell `-and` only when both sides are `BoolExpr`; if one side is a plain `Simple` command the `-and` is dropped and only the last command's exit code is tested — `PsEmitter.cs:EmitIf`
- `[[ ... ]]` extended test with `=~` regex in an if condition: the regex match result feeds the if condition but `$Matches` is not set, breaking `${BASH_REMATCH}` references in the then-body

**PRIORITY GAPS** (top 3 max):
1. Compound condition `if cmd1 && cmd2` parity: add emitter unit test that confirms `-and` is emitted between two plain commands in an if-condition (currently no such test; `Differential_If_CompoundAndCondition` covers the oracle, but the emitter unit test is missing)
2. M4 interactive multi-line if: add a PTY test that types an if/elif/fi over multiple lines at the REPL and checks prompt handling between then-body lines
3. Environment-variable persistence from if-body: add a test verifying that `VAR=x` assigned inside an if-then branch is visible after `fi`

---

### FEATURE: for loops

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ForInWords_ReturnsForInNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ForImplicitArgs_ReturnsForInWithEmptyList`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ForArith_ReturnsForArithNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ForInWithNewlines_ReturnsForInNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ForMissingDone_ThrowsParseException`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MissingDoInFor_ThrowsParseException`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInWords_EmitsForeach`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInNumbers_EmitsForeach`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInGlob_EmitsResolvePath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForImplicitArgs_EmitsArgsIteration`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForArith_EmitsCStyleFor`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForIn_LoopVarNotEnvVar`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForIn_SimilarVarNameNotClobbered`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInGlobCharClass_EmitsResolvePath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForIn_ContainsIterGuard`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForArith_ContainsIterGuard`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_For_BraceRangeExpansion`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_For_BreakExitsEarly`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_For_CStyle`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | PARTIAL | `Parse_ForImplicitArgs_ReturnsForInWithEmptyList` (empty list); no test for `for x in; do` empty body |
| Large input | NO | gap — no test iterating over 10 000+ items |
| Unicode | NO | gap — no test with non-ASCII item values in the list |
| CRLF input | NO | gap — CRLF in for-loop body untested |
| Broken pipe | N/A | for is not a pipeline consumer; skip justified |
| Slow reader | N/A | same; skip justified |
| Signal during execute | NO | gap — no Ctrl-C test mid-iteration |
| Exit code propagation | YES | `Differential_For_BreakExitsEarly` — echo exit code 0 after break |
| Stderr interleave | NO | gap — no test for stderr inside loop body |
| Working dir state | NO | gap — no test that cd inside loop body persists |
| Environment leak | NO | gap — no test that loop variable is visible after done |
| Quoting / injection | YES | `Transpile_ForIn_LoopVarNotEnvVar` — loop var emitted as $x not $env:x |
| Platform-locked file | N/A | for is pure control flow; skip justified |
| Missing target | YES | `Parse_ForMissingDone_ThrowsParseException` |
| Recursion depth | NO | gap — no nested loop depth test |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap |
| M3 file arg | NO | gap |
| M4 interactive | NO | gap — no PTY test for multi-line for at REPL |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 3 differential tests: brace-range expansion, break exits early, C-style for

**KNOWN BUGS / RISKS**:
- The iter-guard (`$__psbash_iter`) is shared across nested loops: an inner loop's counter increments the outer counter, so a nested loop with 1000 outer x 200 inner iterations would hit the 100 000 limit at outer=500 — `PsEmitter.cs:EmitForIn`
- `for x in $list` where `$list` contains items with spaces: the emitter wraps the var in `$env:list` which PowerShell treats as a single string, not an array — word-splitting semantics differ from bash

**PRIORITY GAPS** (top 3 max):
1. break/continue oracle tests: `Differential_For_BreakExitsEarly` covers break; add a continue test (`Differential_For_ContinueSkipsIteration`) to verify continue skips body but does not exit the loop
2. Nested loop iter-guard collision: add an emitter unit test with a nested for-in and assert that the inner loop uses a separate counter variable, not the same `$__psbash_iter`
3. `for x in $var` word-split parity: add a differential test where `list="a b c"` and `for x in $list` must iterate over three items; this is a known semantic gap vs bash

---

### FEATURE: while/until loops

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_WhileTrue_ReturnsWhileNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_WhileReadLine_ReturnsWhileNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_Until_ReturnsWhileNodeWithIsUntilTrue`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_WhileWithTestExpr_ReturnsWhileWithBoolExprCond`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_WhileWithNewlines_ReturnsWhileNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MissingDoInWhile_ThrowsParseException`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_MissingDone_ThrowsParseExceptionNotStackOverflow`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileTrue_EmitsWhileLoop`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileCmd_EmitsWhileLoop`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_UntilCmd_EmitsNegatedWhileLoop`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileReadLine_EmitsForEachObjectPipeline`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileReadLine_DoesNotReplaceSimilarVarNames`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileFileTest_EmitsWhileWithTestPath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_UntilFileTest_EmitsNegatedWhileWithTestPath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileMultipleBodyCommands_EmitsAll`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileReadDashR_EmitsForEachObject`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileTrue_ContainsIterGuard`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileReadLine_NoIterGuard`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_WhileRead_StripsTrailingNewlineBeforeSplit`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_Until_BasicLoop`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_While_ContinueSkipsIteration`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | NO | gap — no test for `while false; do true; done` (zero iterations) |
| Large input | NO | gap — no test streaming 10 MB through `while read line` |
| Unicode | NO | gap — no test with non-ASCII lines in `while read` |
| CRLF input | PARTIAL | `Transpile_WhileRead_StripsTrailingNewlineBeforeSplit` strips `\n`; CRLF (`\r\n`) untested |
| Broken pipe | NO | gap — no test where the while-read source closes its pipe early |
| Slow reader | NO | gap — no test for a slow-writing producer with while-read consumer |
| Signal during execute | NO | gap — no Ctrl-C test mid-while |
| Exit code propagation | YES | `Differential_While_ContinueSkipsIteration` — continue must not change exit code of body |
| Stderr interleave | NO | gap — no test for stderr inside while body |
| Working dir state | NO | gap — no test for cd inside while body |
| Environment leak | NO | gap — no test for variable set inside while body visible after done |
| Quoting / injection | PARTIAL | `Transpile_WhileReadLine_DoesNotReplaceSimilarVarNames` |
| Platform-locked file | N/A | while is control flow; skip justified |
| Missing target | YES | `Parse_MissingDone_ThrowsParseExceptionNotStackOverflow`, `Parse_MissingDoInWhile_ThrowsParseException` |
| Recursion depth | NO | gap — no deeply nested while test |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap |
| M3 file arg | NO | gap |
| M4 interactive | NO | gap — no PTY test for multi-line while at REPL |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 2 differential tests: until basic loop, while continue skips iteration

**KNOWN BUGS / RISKS**:
- `while true` emits `$__psbash_iter` guard but infinite loops are a valid pattern; users must set `PSBASH_MAX_ITERATIONS` to avoid the limit firing — documented workaround but not tested
- `while read line` pipeline path (`ForEach-Object`) does not propagate the producer's exit code to `$?` after the pipeline; bash propagates the last command's exit code — semantic gap

**PRIORITY GAPS** (top 3 max):
1. Zero-iteration while: add a differential test for `while false; do echo body; done` — body must not execute and exit code must be 1 (from the failed condition)
2. `while read` CRLF stripping: extend `Transpile_WhileRead_StripsTrailingNewlineBeforeSplit` to verify `\r\n` input also produces clean lines (currently only `\n` is tested)
3. Infinite-loop guard documentation test: add a test that sets `PSBASH_MAX_ITERATIONS=5` and verifies the loop throws after 5 iterations (currently the guard is tested only for the default value)

---

### FEATURE: Functions

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_FunctionKeywordForm_ReturnsShFunction`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_FunctionParensForm_ReturnsShFunction`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_FunctionParensWithSpace_ReturnsShFunction`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_FunctionWithLocalVars_ReturnsShFunctionWithLocalAssignment`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_FunctionWithMultipleCommands_ReturnsShFunctionWithCommandList`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_LocalAssignment_ReturnsShAssignmentWithIsLocal`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionKeywordForm_EmitsPsFunction`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionParensForm_EmitsPsFunction`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionParensWithSpace_EmitsPsFunction`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionWithLocalVars_EmitsLocalAssignment`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionCallingFunction_EmitsNestedCalls`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_FunctionWithMultilineBody_EmitsFunction`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_Function_ReturnSetsExitCode`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_Function_ArgExpansion`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_Function_Recursion`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | NO | gap — no test for a function with an empty body `f() {}` |
| Large input | NO | gap — no test for a function called in a tight loop with large args |
| Unicode | NO | gap — no test with non-ASCII in function name or args |
| CRLF input | NO | gap — CRLF in function body untested |
| Broken pipe | N/A | functions are not pipeline consumers by default; skip justified |
| Slow reader | N/A | same; skip justified |
| Signal during execute | NO | gap — no Ctrl-C test mid-function-body |
| Exit code propagation | YES | `Differential_Function_ReturnSetsExitCode` — `return 42; echo $?` must print 42 |
| Stderr interleave | NO | gap — no test for stderr inside function body |
| Working dir state | NO | gap — no test for cd inside function body |
| Environment leak | PARTIAL | `Transpile_FunctionWithLocalVars_EmitsLocalAssignment` — local vars; no test for non-local var leaking from function |
| Quoting / injection | YES | `Differential_Function_ArgExpansion` — quoted args with spaces via $1/$@ |
| Platform-locked file | N/A | functions are pure control flow; skip justified |
| Missing target | NO | gap — no test for calling an undefined function |
| Recursion depth | YES | `Differential_Function_Recursion` — factorial(4) via recursive call |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap |
| M3 file arg | NO | gap |
| M4 interactive | NO | gap — no PTY test for defining and calling a function at the REPL |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 3 differential tests: return sets $?, arg expansion ($1/$@/$#), recursion (factorial)

**KNOWN BUGS / RISKS**:
- `local` is emitted as a plain assignment (`$x = value`) in PowerShell scope; PowerShell functions do not have bash-style local scope — a `local` variable inside a function IS visible to nested functions called from it, but IS also visible after the function returns via the outer scope if the variable was never declared before — semantic gap vs bash
- `return N` is emitted via PowerShell `return` which exits the function and sets `$LASTEXITCODE` only via a workaround; if `set -e` / `$ErrorActionPreference = 'Stop'` is active, a `return 1` may throw instead of setting `$?` — `PsEmitter.cs:EmitSimple` return handling
- Recursive functions: each recursive call emits a new PowerShell function frame; tail recursion is not optimized; deep recursion (>500 levels) will hit PowerShell's default stack limit

**PRIORITY GAPS** (top 3 max):
1. `local` scope isolation: add a differential test verifying that a `local x=inner` inside a function does not clobber an outer `x=outer` variable — this is the most impactful semantic gap between bash local and PowerShell scope
2. Undefined function call error: add a test that calls a function before it is defined and asserts a non-zero exit code with an error on stderr (ps-bash must not silently succeed)
3. `$@` in function with quoted args containing spaces: extend `Differential_Function_ArgExpansion` to call `greet "hello world" foo` and verify `$1` is `hello world` (one word, not two)

---

### FEATURE: case/esac

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SimpleCase_EmitsSwitch`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CaseMultiplePatterns_EmitsSeparateClauses`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CaseDefaultStar_EmitsDefault`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NestedCase_EmitsNestedSwitch`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CaseWithGlobPattern_EmitsWildcard`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_SimpleMatch`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_NoMatch_NoOutput`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_WildcardDefault`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_AlternatePatterns`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_GlobPattern`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_VariableSubject`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_QuotedSubject`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_LeadingParenForm`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_ExitCodePropagation`
- `src/PsBash.Differential.Tests/CaseEsacDifferentialTests.cs:Differential_Case_NoFallthrough_OnlyFirstMatchFires`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | PARTIAL | `Differential_Case_NoMatch_NoOutput` — no arms match, output is empty; no test for `case $x in esac` (zero arms) |
| Large input | NO | gap — no test for a case subject containing a 10 MB string |
| Unicode | NO | gap — no test with non-ASCII in case subject or pattern |
| CRLF input | NO | gap — CRLF in case body untested |
| Broken pipe | N/A | case is not a pipeline consumer; skip justified |
| Slow reader | N/A | case is not a pipeline consumer; skip justified |
| Signal during execute | NO | gap — no Ctrl-C test mid-case-body |
| Exit code propagation | YES | `Differential_Case_ExitCodePropagation` — last body command exit code propagates via $? |
| Stderr interleave | NO | gap — no test for stderr inside a case arm body |
| Working dir state | NO | gap — no test for cd inside a case arm body |
| Environment leak | NO | gap — no test that a variable set inside a case arm body is visible after esac |
| Quoting / injection | YES | `Differential_Case_QuotedSubject` — double-quoted $x as subject; `Differential_Case_VariableSubject` — bare $x expansion |
| Platform-locked file | N/A | case is pure control flow; skip justified |
| Missing target | PARTIAL | `Parse_MissingFi`-equivalent: no test for missing `esac`; parser does throw on malformed case |
| Recursion depth | NO | gap — no test for a case nested 5+ levels deep |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap |
| M3 file arg | NO | gap |
| M4 interactive | NO | gap — no PTY test for multi-line case at REPL |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 10 differential tests in `CaseEsacDifferentialTests.cs`: simple match, no match, wildcard default, alternate patterns, glob pattern, variable subject, quoted subject, leading-paren form, exit code propagation, no-fallthrough

**KNOWN BUGS / RISKS**:
- Bare literal case subject (`case hello in`) was emitted as `switch (hello)` — PowerShell treated `hello` as a command name rather than a string, producing a non-zero exit code and no output. Fixed in this audit task (DART-swq8znLUb1BN) via `EmitCaseExpr` helper in `PsEmitter.cs:EmitCase`.
- `;&` (fallthrough) and `;;&` (continue-matching) terminators are NOT parsed — `BashParser.cs:ParseCaseArm` only consumes `;;`; a script using `;&` hangs ps-bash because the parser's state machine never advances past the `;&` token.
- Pattern matching is case-sensitive in both bash and ps-bash (PowerShell `switch` is case-insensitive by default; ps-bash uses `-Exact` mode implicitly via single-quoted patterns, but `-Wildcard` mode inherits PowerShell case-insensitivity — potential mismatch for glob patterns on case-sensitive filesystems).
- Variable patterns (`case $x in $y)`) are not supported: patterns are collected as raw strings at parse time; `$y` would appear as a literal string in the pattern, not expand the variable — `BashParser.cs:ConsumeCasePattern`.

**PRIORITY GAPS** (top 3 max):
1. `;&` fallthrough support: the parser hangs on `;&` — the token is not consumed, looping forever; fix requires adding `SemiAmp` token kind to the lexer, updating `ParseCaseArm` to detect it, and emitting a `; fallthrough` or explicit next-arm invocation in `EmitCase`
2. PowerShell `-Wildcard` case sensitivity: add a differential test with a mixed-case subject like `case FILE.TXT in *.txt) echo hit ;; esac` — bash matches (glob is case-sensitive on Linux), PowerShell `-Wildcard` matches case-insensitively on Windows; the test will reveal the platform divergence
3. Variable pattern expansion: add a parser test that verifies `case $x in $y)` produces a `CaseArm` whose pattern is the literal string `$y`, then add a differential test to document the known semantic gap vs bash

---

### FEATURE: Logical operators (&amp;&amp;, ||, !)

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Emit_AndOrList_EmitsPassthrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Emit_AndOrList_OrIf_EmitsPassthrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ThreeCommandAndOrList_CorrectPrecedence`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_TestOrEcho_Passthrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NegatedCommand_EmitsExitCodeNegation`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NegatedPipeline_EmitsExitCodeNegation`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_If_AndCondition_TrueAndTrue_EmitsAndExpr`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_If_AndCondition_FalseAndTrue_EmitsAndExpr`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_If_OrCondition_FalseOrTrue_EmitsOrExpr`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_If_AndCondition_BoolExprAndBoolExpr_EmitsAndWithTestExprs`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_If_AndCondition_WithElse_EmitsCorrectBranches`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_CompoundAndCondition`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_CompoundOrCondition`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_CompoundAndConditionFirstFails`
- `src/PsBash.Differential.Tests/ControlFlowDifferentialTests.cs:Differential_If_CompoundAndConditionBoolExpr`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_BasicAnd_BothSucceed`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_And_ShortCircuitOnFailure`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_BasicOr_FallbackOnFailure`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_Or_ShortCircuitOnSuccess`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_MixedChain_LeftAssociative`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_MixedChain_FirstArmSucceeds`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_Not_NegatesFalseAndChainsAnd` (Golden — known bug)
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_Not_NegatesPipelineExitCode` (Golden — known bug)
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_ExitCodeAfterSuccessfulChain`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_ExitCodeAfterFailedLastArm`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_SetE_FalseOrTrueSurvives`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_TestExprAndBraceGroup`
- `src/PsBash.Differential.Tests/LogicalOperatorDifferentialTests.cs:Differential_AndOr_ThreeCommandChain_Recovers`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | N/A | &&/|| require two operands; empty-operand is a parse error, justified skip |
| Large input | NO | gap — no test streaming large input through a `grep … && wc -l` chain |
| Unicode | NO | gap — no test with non-ASCII words on either side of && / || |
| CRLF input | NO | gap — no test where a heredoc or file containing CRLF is the left side of && |
| Broken pipe | NO | gap — no test where the left side of && is a pipeline with a broken-pipe exit |
| Slow reader | N/A | &&/|| are sequence operators, not pipeline stages; skip justified |
| Signal during execute | NO | gap — no Ctrl-C test mid-chain |
| Exit code propagation | YES | `Differential_AndOr_ExitCodeAfterSuccessfulChain`, `Differential_AndOr_ExitCodeAfterFailedLastArm` |
| Stderr interleave | NO | gap — no test for stderr from left arm of && polluting right arm |
| Working dir state | NO | gap — no test for `cd /tmp && pwd` verifying cd persisted |
| Environment leak | NO | gap — no test for `VAR=x cmd && echo $VAR` checking var is not leaked |
| Quoting / injection | NO | gap — no test with a space-containing arg on the right side of && |
| Platform-locked file | N/A | &&/|| are pure control flow; skip justified |
| Missing target | PARTIAL | `Differential_AndOr_And_ShortCircuitOnFailure` (false as stand-in for missing cmd); no test for actual missing command |
| Recursion depth | N/A | &&/|| chains are linear; skip justified |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap |
| M3 file arg | NO | gap |
| M4 interactive | NO | gap — no PTY test for a multi-arm chain at the REPL |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 13 differential tests: 11 live EqualAsync, 2 GoldenAsync (known bugs)
- If-condition &&/|| covered by ControlFlowDifferentialTests (4 tests); standalone covered here

**KNOWN BUGS / RISKS**:
- `! cmd && cmd2` emitter bug: `! cmd` emits `cmd; $global:LASTEXITCODE = if ($?) { 1 } else { 0 }` which inserts a `;` before `&&`; PowerShell pipeline-chain operators (`&&`/`||`) cannot follow `;` in an expression context — results in PS syntax error. Bug captured in `Differential_Not_NegatesFalseAndChainsAnd` golden — `PsEmitter.cs:EmitPipeline` around line 2064.
- `! pipeline; echo $?` LASTEXITCODE misread: the emitter uses `$LASTEXITCODE` (unqualified) in the subsequent echo, which resolves to the raw pipeline exit code rather than the negated value stored in `$global:LASTEXITCODE`. Bug captured in `Differential_Not_NegatesPipelineExitCode` golden — `PsEmitter.cs:EmitSimpleVar` / `EmitPipeline` negation path.
- `BoolExpr` and `ShAssignment` in chains are wrapped in `[void](...)` to suppress PS output — `PsEmitter.cs:EmitAndOrList` lines 1991-2001. This is correct for output suppression but means the exit code must come from `$global:LASTEXITCODE` after the void call. If PS's `&&` operator sees a `[void]` expression that throws, the right arm may or may not run depending on error action preference — not tested.
- `set -e` + `||` interaction: the emitter translates `set -e` to `$ErrorActionPreference = 'Stop'`. PowerShell's `&&`/`||` are pipeline chain operators that operate on `$?`, not on thrown exceptions; a failing command under Stop mode throws before `||` can suppress it. `Differential_AndOr_SetE_FalseOrTrueSurvives` passes today but via bash builtin `false` (not `$ErrorActionPreference`) — real external commands may behave differently.

**PRIORITY GAPS** (top 3 max):
1. Fix `! cmd` in AndOrList chains: `EmitPipeline` negation appends `; $global:LASTEXITCODE = …` making `&&`/`||` syntactically invalid. The fix is to emit the negation as part of the AndOrList arm rather than appending to the command string — e.g. wrap in `& { false; if ($?) { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 } }`.
2. `$?` / `$LASTEXITCODE` after `!` pipeline: `EmitSimpleVar` emits `$LASTEXITCODE` without `$global:` prefix when the var appears in the same statement; the negation writes to `$global:LASTEXITCODE` but the subsequent `echo $?` reads from PS's local `$LASTEXITCODE` which is still the raw value — fix by consistently using `$global:LASTEXITCODE` throughout.
3. `cd /tmp && pwd` working-dir persistence: add a differential test verifying that the left arm of `&&` can change directory and the right arm sees the new `$PWD` — this exercises whether ps-bash's pwsh worker correctly propagates `cd` across a chain boundary.

---

### FEATURE: Glob + Brace Expansion

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_Braces_ReturnsBraceTokens`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_EmptyBraces_LexesAsWord`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_EmptyBracesInXargs_LexesAsWord`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_GlobStar_ProducesGlobPart`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_GlobCharClass_ProducesGlobPart`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ExtGlob_ProducesGlobPart`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedTuple_EmitsArray`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedRange_EmitsPsRange`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedRangeLeadingZeros_EmitsZeroPaddedArray`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedTupleWithPrefixSuffix_EmitsExpandedArray`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedRangeWithPrefix_EmitsExpandedArray`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeLeadingZero_EmitsStringArray`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeNoLeadingZero_EmitsRangeOperator`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeWithStep_ExpandsCorrectly`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeDefaultStep_Works`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeReverseDefaultStep_Works`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeStepDivisible_IncludesEnd`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeNonDivisibleStep_DoesNotOvershoot`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BraceRangeNonDivisibleStepReverse_DoesNotOvershoot`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobStar_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobQuestionMark_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobCharClass_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobMixedWithLiteral_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobStandalone_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobPrefix_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GlobSuffix_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ExtGlob_PassesThrough`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ExtendedGlob_EmitsLike`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_AwkWithCommaInsideBraces_NotBraceExpanded`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_AwkInPipelineWithFlagAndCommaExpression_NotBraceExpanded`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_AwkWithMultipleFields_NotBraceExpanded`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInGlob_EmitsResolvePath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ForInGlobCharClass_EmitsResolvePath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceTuple_ExpandsToWords`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceRange_IntegerSequence`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceRange_ZeroPadded`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceRange_WithStep`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceTuple_WithPrefixAndSuffix`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceTuple_InsideDoubleQuotes_NoExpansion`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceTuple_TrailingComma_EmptyItem`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceRange_LetterRange_KnownGap` (golden)
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceTuple_NestedBraces_KnownGap` (golden)
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_Glob_NoMatchPassthrough`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_Glob_NegatedCharClass_NoMatch_Passthrough`
- `src/PsBash.Differential.Tests/GlobBraceExpansionDifferentialTests.cs:Differential_BraceRange_Descending`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `Transpile_BracedTuple_EmitsArray` — empty string item via trailing comma `{a,}` |
| Large input | NO | gap — no test streams a large number of brace-expanded items through a pipeline |
| Unicode | PARTIAL | `Differential_BraceRange_LetterRange_KnownGap` documents non-ASCII range; non-ASCII in brace items untested |
| CRLF input | N/A | brace expansion is a word-transform, not a line-reader; skip justified |
| Broken pipe | N/A | brace expansion produces arguments, not a pipeline consumer; skip justified |
| Slow reader | N/A | brace expansion is pure word transformation, no I/O; skip justified |
| Signal during execute | N/A | brace expansion completes before any exec; skip justified |
| Exit code propagation | YES | `Differential_BraceRange_IntegerSequence` — echo exits 0; any mis-expansion would produce wrong output not wrong exit code |
| Stderr interleave | N/A | brace expansion is a word transform; produces no stderr of its own; skip justified |
| Working dir state | N/A | brace expansion does not interact with cwd; skip justified |
| Environment leak | YES | `Differential_BraceTuple_InsideDoubleQuotes_NoExpansion` — quoting in DQ context must not leak brace expansion |
| Quoting / injection | YES | `Differential_BraceTuple_InsideDoubleQuotes_NoExpansion` — `"{a,b}"` must emit `{a,b}` literal; `Transpile_AwkWithCommaInsideBraces_NotBraceExpanded` — awk `{print $1}` must not brace-expand |
| Platform-locked file | N/A | brace expansion has no file I/O; skip justified |
| Missing target | YES | `Differential_Glob_NoMatchPassthrough` — glob with no match returns literal (nullglob off) |
| Recursion depth | YES | `Differential_BraceTuple_NestedBraces_KnownGap` — nested `{a,b{1,2},c}` documents recursion gap; golden records current behavior |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all differential tests invoke ps-bash -c |
| M2 stdin pipe | NO | gap — no test pipes `echo "echo {a,b,c}"` through ps-bash stdin |
| M3 file arg | NO | gap — no test passes a script file containing brace expansions |
| M4 interactive | NO | gap — no PTY test for brace expansion at the REPL prompt |
| M5 Invoke-BashEval | NO | gap — no test evaluates a brace expansion via `Invoke-BashEval` |
| M6 Invoke-BashSource | NO | gap — no test sources a script file containing brace expansions |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 10 live-diff (EqualAsync) + 2 golden (GoldenAsync) = 12 differential cases
- GoldenAsync used for: letter range (`{a..e}`) and nested braces (`{a,b{1,2},c}`) — both are known bugs per Directive 1 exception: bash has no equivalent correct output from ps-bash

**KNOWN BUGS / RISKS**:
- Letter range `{a..e}` not implemented: `ParseBraceExpansion` in `BashParser.cs:1772` only calls `int.TryParse`; non-integer ranges fall through to comma-split producing literal text `a..e` — `BraceRange_LetterRange.golden.txt` records current output
- Nested brace expansion `{a,b{1,2},c}` broken: `ParseBraceExpansion` in `BashParser.cs:1788` uses `inner.Split(',')` without depth-awareness; commas inside nested braces are split at the wrong level, producing `a,c} b{1,c} 2,c}` — `BraceTuple_NestedBraces.golden.txt` records current output
- Glob expansion at runtime (`*`, `?`, `[abc]`) is delegated to `Resolve-BashGlob` in `PsBash.psm1`; extglob patterns (`+(*.py)`) are passed through by the emitter (`GlobPart.Pattern`) but `Resolve-BashGlob` does not implement extglob matching — untested runtime behavior
- `{a}` (single-item, no comma, no `..`) is treated as a literal brace group by `IsBraceExpansion` (correctly returns false); emitted as the literal string `{a}` by the emitter — matches bash behavior; covered implicitly by the lexer treating `{` as `LBrace` only when `IsBraceExpansion` returns false

**PRIORITY GAPS** (top 3 max):
1. Letter range support: extend `ParseBraceExpansion` in `BashParser.cs` to handle `char.TryParse` for single-character range operands and emit a `BracedRange` variant with char arithmetic (`(char)((int)'a' + i)`) — unblocks `{a..z}`, `{A..Z}`, `{a..e}` cases
2. Nested brace expansion: replace `inner.Split(',')` in `ParseBraceExpansion` with a depth-aware comma splitter that only splits at brace-depth 0 — required for `{a,b{1,2},c}` and similar patterns common in shell scripts
3. Extglob runtime coverage: add differential tests for `+(pattern)`, `*(pattern)`, `?(pattern)` against a real directory in a temp folder so `Resolve-BashGlob` is exercised; currently only the emitter passthrough is tested

---

### FEATURE: test expressions ([ ] and [[ ]])

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BoolExpr_FileTest_EmitsTestPath`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BoolExpr_StringEq_EmitsDashEq`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BoolExpr_IntComparison_EmitsIntOperator`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_DoubleBracket_Regex_EmitsDashMatch`
- `src/PsBash.Differential.Tests/TestExpressionDifferentialTests.cs` — 17 new differential tests

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `Differential_StringTest_ZeroLength_EmptyIsTrue` (`[ -z "" ]`) |
| Large input | NO | gap — not applicable for boolean expressions |
| Unicode | NO | gap — `[[ "héllo" =~ hé ]]` not tested |
| CRLF | NO | gap |
| Broken pipe | NO | not applicable |
| Slow reader | NO | not applicable |
| Signal during execute | NO | not applicable |
| Exit code propagation | YES | `Differential_IntTest_GtTrue`, `Differential_FileTest_NonexistentFile_IsFalse` |
| STDERR interleave | NO | gap |
| Working dir state | NO | not applicable |
| Environment leak | YES | `Differential_QuotedUndefinedVar_ZeroLengthTest_IsTrue` (unset var) |
| Quoting / injection | YES | `Differential_QuotedUndefinedVar_ZeroLengthTest_IsTrue` (axis 12) |
| Platform-locked file | NO | not applicable |
| Missing target | YES | `Differential_FileTest_NonexistentFile_IsFalse`, `Differential_Negation_BracketNot_FileTest` |
| Recursion depth | NO | not applicable |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all EqualAsync tests use `-c` mode |
| M2 stdin | NO | gap |
| M3 file | NO | gap |
| M4 interactive | NO | gap |
| M5 Invoke-BashEval | NO | gap |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 13 live-diff (EqualAsync) + 4 golden (GoldenAsync) = 17 differential cases
- GoldenAsync used for: `[ ! -f ... ]`, `! false`, `[[ -f x && -d y ]]`, standalone `[ ] || echo` — all are known bugs per Directive 1 exception

**KNOWN BUGS / RISKS**:
- `EmitTestArg` was not quoting string literals in `[string]::IsNullOrEmpty(x)` — fixed in this audit (single-quoted now); `$`-prefixed args still passed bare (correct for variables)
- `[ ! -f x ]` — `!` inside `[ ]` is parsed as inner word; emitter's negation suffix emits `;` before `&&` in containing pipeline chain, causing PS syntax error — `Differential_Negation_BracketNot_FileTest.golden.txt`
- `! false` in if-condition — negation suffix uses `; $LASTEXITCODE = if ($?) { 1 } else { 0 }` but the `;` breaks PS pipeline-chain context — `Differential_Negation_PipelineNegated_False.golden.txt`
- `[[ -f x && -d y ]]` — `&&` inside `[[ ]]` not properly short-circuit evaluated by `SplitLogical` emitter path — `Differential_DoubleBracket_LogicalAnd_FalseAndTrue_IsFalse.golden.txt`
- Standalone `[ -f x ] || cmd` — BoolExpr exit-code propagation to `||` chain incorrect — `Differential_Standalone_FileTest_OrChain_NonexistentFallsBack.golden.txt`

**PRIORITY GAPS** (top 3 max):
1. Fix `!` negation in if-conditions and pipeline chains: the negation suffix must not insert `;` before `&&`/`||` operators (see `EmitPipeline` negation path in `PsEmitter.cs`)
2. Fix `[[ expr && expr ]]` compound: `SplitLogical` must handle `&&`/`||` inside double-bracket, treating them as logical short-circuit rather than passing them as literal words
3. Fix standalone `BoolExpr` exit-code propagation into `&&`/`||` chains: the `[void](...)` wrapper discards the bool result; need to emit `if (Test-Path ...) { }; $LASTEXITCODE = if ($?) { 0 } else { 1 }` pattern

---

### FEATURE: Command substitution ($(cmd), backtick, $((arith)), process substitution)

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CommandSub_SimpleCommand_Passthrough` (line 627)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CommandSub_InnerPipeline_TranspilesInnerCommands` (line 635)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BacktickCommandSub_NormalizedToDollarParen` (line 643)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_AssignmentWithCommandSub_EmitsEnvAssignment` (line 651)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NestedCommandSub_EmitsCorrectNesting` (line 659)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_BasicAddition` (line 1243)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_LiteralAddition` (line 1250)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_Multiplication` (line 1257)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_InAssignment` (line 1341)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_Power` (line 1348)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_Modulo` (line 1355)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ArithSub_NestedInString` (line 1362)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_DiffWithTwoInputProcessSubs` (line 1523)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_OutputProcessSub` (line 1530)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NestedProcessSub` (line 1537)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ProcessSubWithPipe` (line 1546)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_GrepWithProcessSub` (line 1553)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PasteWithProcessSubs` (line 2278)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_ProcessSubWithSemicolon` (line 2285)
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PasteWithSemicolonProcessSubs` (line 2292)
- `src/PsBash.Differential.Tests/SeedDifferentialTests.cs:Differential_CommandSubstitution_NestedQuoting` (golden; line 96)
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_BasicEcho_ExpandsAtRuntime`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_TrailingNewlineStripped`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_NestedThreeLevels`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_PipelineInside`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_BacktickForm_SameAsParenForm`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_ArithSub_LiteralAddition_ProducesInteger`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_ArithSub_VsCommandSubSubshell_BothProduceResult`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_InsideDoubleQuotes_NoWordSplit`
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_ProcessSub_DiffTwoSources_ProducesDiff` (golden)
- `src/PsBash.Differential.Tests/CommandSubstitutionDifferentialTests.cs:Differential_CommandSub_EmptyOutput_YieldsEmptyString`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `Differential_CommandSub_EmptyOutput_YieldsEmptyString` — $(true) yields empty |
| Large input | NO | gap — no test of $(...) capturing >10 MB of output |
| Unicode | NO | gap — no test of command sub on command that emits unicode/emoji |
| CRLF input | NO | gap — no test of command sub output with \r\n line endings |
| Broken pipe | NO | gap — not applicable directly; command sub captures output fully |
| Slow reader | NO | gap — not applicable; command sub buffers output before assignment |
| Signal during execute | NO | gap — Ctrl-C inside $(...) not tested |
| Exit code propagation | YES | `Differential_ArithSub_VsCommandSubSubshell_BothProduceResult` (Axis 8): command sub does not propagate inner exit code to outer $?; outer command's exit code prevails |
| Stderr interleave | NO | gap — no test of $(...) capturing both stdout and stderr via 2>&1 inside the substitution |
| Working dir state | NO | gap |
| Environment leak | NO | gap — no test that VAR=x $(cmd) does not leak VAR to outer scope |
| Quoting / injection | YES | `Differential_CommandSub_InsideDoubleQuotes_NoWordSplit` (Axis 12): double-quoted $(...) suppresses word-splitting |
| Platform-locked file | NO | not applicable |
| Missing target | YES | `Differential_CommandSub_EmptyOutput_YieldsEmptyString`: $(true) has no output; ProcessSub golden verifies diff handles missing-like path |
| Recursion depth | YES | `Differential_CommandSub_NestedThreeLevels` (Axis 15): three-level $(echo $(echo deep)) |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all EqualAsync/GoldenAsync tests use ps-bash -c mode |
| M2 stdin | NO | gap |
| M3 file | NO | gap |
| M4 interactive | NO | gap |
| M5 Invoke-BashEval | NO | gap — command sub inside Invoke-BashEval not tested |
| M6 Invoke-BashSource | NO | gap |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 9 live-diff (EqualAsync) + 2 golden (GoldenAsync) = 11 differential cases in `CommandSubstitutionDifferentialTests.cs`
- Plus 1 golden in `SeedDifferentialTests.cs` = 12 differential cases total for this feature
- GoldenAsync used for: `ProcessSub_DiffTwoSources` (temp-file paths vary per run) and `CommandSubstitution_NestedQuoting` (date output changes yearly)

**KNOWN BUGS / RISKS**:
- Subshell at end of a pipeline (`cmd | (...)`) emits `try { Push-Location ... } finally { Pop-Location }` as a pipeline stage, which PowerShell rejects because `try` is not a pipeline-compatible expression — `EmitSubshell` in `PsEmitter.cs`; the subshell must be wrapped in `& { ... }` to be a valid pipeline stage
- `$( (1+2) )` (CommandSub containing subshell arithmetic) hits the same subshell-in-pipeline emission bug; the test works because `$( echo $((1+2)) )` avoids the broken subshell path
- Exit code of inner command in `$(...)` is silently discarded by PowerShell's subexpression; bash has the same behavior so the semantics are correct, but no explicit test asserts that `$(false); echo $?` reports `0` rather than `1`
- Process substitution `>(cmd)` (output direction) — emitter maps it to `Invoke-ProcessSub` with a temp file, but the consumer command must read from that file after the process sub command finishes; potential race if the consumer reads before ps-bash finishes writing

**PRIORITY GAPS** (top 3 max):
1. Fix `EmitSubshell` so a subshell used as a pipeline stage emits `& { ... }` instead of `try { Push-Location } finally { Pop-Location }` — blocks any pipeline ending in `(...)` and any `pipe | (cmd sub)` pattern
2. Add differential test for `$(false); echo $?` to assert that inner command's exit code does not leak into the outer `$?` (explicit Axis 8 verification at runtime level, not just transpile)
3. Add large-output test: `x=$(seq 1 100000); echo "${#x}"` — verifies command sub captures large output without truncation or buffer overflow through the PowerShell subexpression path

---

### FEATURE: Redirections (>, >>, <, 2>&1, heredoc, herestring)

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_RedirectToFile_ReturnsWordsAndRedirect` — Great token for `>`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_DGreat_ReturnsAppendRedirect` — DGreat token for `>>`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_DLess_ReturnsHereDocOperator` — DLess token for `<<`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_DLessDash_ReturnsStripTabsHereDoc` — DLessDash for `<<-`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_LessAnd_ReturnsDupInputRedirect` — LessAnd for `<&`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_GreatAnd_ReturnsDupOutputRedirect` — GreatAnd for `>&`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_IoNumber_BeforeGreat` — IoNumber reclassification for `2>`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_IoNumber_BeforeLess` — IoNumber reclassification for `0<`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_IoNumber_BeforeGreatAnd` — IoNumber reclassification for `2>&`
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_DigitWord_NotBeforeRedirect_StaysWord` — digit without adjacent redirect stays Word
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeAmpersand_EmitsStderrMerge` — `|&` emits as `2>&1 |`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | PARTIAL | `/dev/null` discard tested via `Differential_Redirect_DevNull_DiscardsOutput`; no test for `< /dev/null` no-input path in `EmitSimple` |
| Large input | NO | gap — no test streams >10 MB through a redirect; `Invoke-BashRedirect` and `Get-Content` file mode untested at scale |
| Unicode | NO | gap — no test redirects or reads a file containing BOM, non-ASCII, or emoji |
| CRLF input | NO | gap — no test verifies heredoc body with CRLF is handled correctly |
| Broken pipe | NO | gap — no test for reader closing early when stdout is redirected to a file that fills |
| Slow reader | N/A | file redirect is not a streaming consumer; skip justified |
| Signal during execute | NO | gap — no test for Ctrl-C mid-redirect |
| Exit code propagation | PARTIAL | `Differential_Redirect_Stderr_DevNull_SuppressesError` checks `$?` after `2>/dev/null`; no test for `>>` failing due to permission denied |
| Stderr interleave | YES | `Differential_Redirect_StderrToStdout_PipedToCat` tests `>&2 2>&1 \| cat`; `Transpile_PipeAmpersand_EmitsStderrMerge` tests `\|&` |
| Working dir state | NO | gap — no test verifies redirect target path is resolved against current `$PWD` after `cd` |
| Environment leak | N/A | redirect does not create new env vars; skip justified |
| Quoting / injection | PARTIAL | variable target tested via `Differential_Redirect_VariableTarget_WritesCorrectFile`; no test for target containing spaces or semicolons |
| Platform-locked file | NO | gap — Windows: no test for redirect to a file locked by another process |
| Missing target | YES | `Differential_Redirect_Stderr_DevNull_SuppressesError` uses a non-existent command; `EmitSimple` `< /dev/null` drops the redirect (documented) |
| Recursion depth | N/A | redirect is not recursive; skip justified |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all `Differential_Redirect_*` EqualAsync tests exercise `-c` mode via `BashOracleFixture` |
| M2 stdin pipe | NO | gap — no test pipes a script with redirections through ps-bash stdin |
| M3 file arg | NO | gap — no test runs a `.sh` file containing redirections passed as arg to ps-bash |
| M4 interactive | NO | gap — no PTY test for `echo hello > /tmp/x` at interactive REPL |
| M5 Invoke-BashEval | NO | gap — no test evaluates a redirect expression via `Invoke-BashEval` |
| M6 Invoke-BashSource | NO | gap — no test sources a script that uses redirections |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 11 live-diff (EqualAsync) + 1 golden (GoldenAsync) = 12 differential cases in `RedirectionDifferentialTests.cs`
- GoldenAsync used for: expanding heredoc `cat <<EOF\nhello $USER\nEOF` — $USER is machine-specific (Directive 1 exception: non-deterministic output)

**KNOWN BUGS / RISKS**:
- `EmitSimple` stderrRedirect handling for `>&2` / `1>&2`: uses `ForEach-Object { [Console]::Error.WriteLine($_) }` — this drops the original `BashText` typed object; pipeline object preservation is broken for this path
- Brace group redirect `{ ...; } > file` uses `EmitSubshell` Invoke-BashRedirect path, but `EmitBraceGroup` returns the body without redirect support; in `EmitSimple` a `BraceGroup` falls through to the general path where `Redirect?` file redirect handling applies — need to verify via differential test
- `2 >file` (digit with space before `>`) must be treated as arg "2" + stdout redirect, not fd 2 redirect; the IoNumber adjacency check in `BashLexer.TryReclassifyIoNumber` handles this but it is only tested at the lexer level, not via an emitter or differential test
- `<<-EOF` (strip-tabs heredoc) — `DLessDash` token is lexed correctly but no emitter or differential test verifies that leading tabs are stripped from the heredoc body before emission
- `&>file` and `&>>file` (bash-specific combined stdout+stderr redirect): not handled in `EmitRedirect`; these are parsed as `&` (background) + `>file` rather than as combined redirects — known gap

**PRIORITY GAPS** (top 3 max):
1. Add differential test for `<<-EOF` strip-tabs heredoc: verify leading tabs are removed from body before `cat` receives it (`Differential_Redirect_Heredoc_StripTabs`)
2. Add differential test for `&>file` combined redirect: bash allows `&>file` as shorthand for `>file 2>&1`; ps-bash currently treats `&` as background operator — this is a parsing gap (no IoNumber/GreatAnd token for `&>`)
3. Add emitter-level test `Transpile_EchoRedirectStdoutFile_EmitsInvokeBashRedirect` to cover `EmitRedirect` path for `>file` at the transpile layer (currently only tested end-to-end via differential)

---

### FEATURE: Pipes (| and |&)

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_SimplePipeline_ReturnsPipelineNode`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_ThreeCommandPipeline_ReturnsThreeChildren`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_PipeAmpersand_RecognizedAsStderrPipe`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_NegatedSimpleCommand_ReturnsPipelineWithNegatedTrue`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_NegatedPipeline_ReturnsPipelineWithNegatedTrue`
- `src/PsBash.Core.Tests/Parser/BashParserTests.cs:Parse_PipelineWithAndOr_PipelineBindsTighter`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_LsPipeGrep_EmitsMappedPipeline`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_CatPipeHeadPipeSort_EmitsMultiStagePipeline`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeAmpersand_EmitsStderrMerge`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_NegatedPipeline_EmitsExitCodeNegation`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_EchoPipeWcL_EmitsMeasureObject`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeToRev_EmitsInvokeBashRev`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeToJqWithFilter_EmitsInvokeBashJq`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeToNlWithFlags_EmitsInvokeBashNl`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeToColumnWithFlag_EmitsInvokeBashColumn`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeToTee_EmitsInvokeBashTee`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_AwkInPipelineWithFlagAndCommaExpression_NotBraceExpanded`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PasteAsPipeTarget`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BashPipeToGrep_EmitsMappedPipeline`
- `src/PsBash.Differential.Tests/SeedDifferentialTests.cs:Differential_Pipes_TwoStage`
- `src/PsBash.Differential.Tests/SeedDifferentialTests.cs:Differential_ExitCodePropagation_PipefailOff`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_TwoStage_EchoWcW`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_ThreeStage_PrintfGrepWcL`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_WithArgs_PrintfSort`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_PipeAmp_StderrMergedToStdout`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_InCondition_GrepQMatches`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_InCondition_GrepQNoMatch`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_ExitCode_LastCmdWins_PipefailOff`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_ExitCode_PipefailOn_FirstFailurePropagates`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_BrokenPipe_HeadClosesEarly`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_ObjectPreservation_GrepThenSort`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_FourStage_SortHeadTr`
- `src/PsBash.Differential.Tests/PipeDifferentialTests.cs:Differential_Pipe_FeedWhileLoop`

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | PARTIAL | `Differential_Pipe_ThreeStage_PrintfGrepWcL` — grep on non-matching input produces empty output; no dedicated empty-producer test |
| Large input | NO | gap — no test streams >= 10 MB through a pipeline |
| Unicode | NO | gap — no test with non-ASCII or emoji in piped text |
| CRLF input | NO | gap — no test feeding CRLF-terminated lines through a pipeline |
| Broken pipe | YES (skip) | `Differential_Pipe_BrokenPipe_HeadClosesEarly` — test present but permanently skipped on Windows (known SIGPIPE hang); see MEMORY.md |
| Slow reader | NO | gap — no test with a blocking consumer; unbounded buffering risk untested |
| Signal during execute | NO | gap — no Ctrl-C test mid-pipeline |
| Exit code propagation | YES | `Differential_Pipe_ExitCode_LastCmdWins_PipefailOff`, `Differential_Pipe_ExitCode_PipefailOn_FirstFailurePropagates` (golden documenting known gap) |
| Stderr interleave | YES (golden) | `Differential_PipeAmp_StderrMergedToStdout` — golden captures |& behavior; wording differs between bash and ps-bash |
| Working dir state | NO | gap — no test verifying cd inside a pipeline stage persists |
| Environment leak | NO | gap — no test for `VAR=x cmd | next` env isolation |
| Quoting / injection | YES | `Differential_Pipe_WithArgs_PrintfSort`, `Differential_Pipe_ObjectPreservation_GrepThenSort` — spaces in piped text, no word-splitting |
| Platform-locked file | N/A | pipeline is not a file operation; skip justified |
| Missing target | PARTIAL | `Differential_PipeAmp_StderrMergedToStdout` — `ls /nonexistent` tests error path via |&; no test for missing pipe-target command |
| Recursion depth | N/A | pipelines are flat sequences; skip justified |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | all `Differential_Pipe_*` tests invoke ps-bash with `-c`; oracle runs bash `-c` |
| M2 stdin pipe | NO | gap — no test pipes a pipe-containing script through ps-bash stdin |
| M3 file arg | NO | gap — no test passes a .sh file with pipeline commands to ps-bash |
| M4 interactive | NO | gap — no PTY test for pipeline at the REPL |
| M5 Invoke-BashEval | NO | gap — no eval test for pipe expressions |
| M6 Invoke-BashSource | NO | gap — no sourced script test containing pipeline commands |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 11 differential tests in `PipeDifferentialTests.cs`: 8 EqualAsync (clean parity), 2 GoldenAsync (known bugs: grep -q exit code propagation and grep -q output suppression), 1 GoldenAsync (|& wording differs), 1 skipped (broken pipe hangs on Windows)

**KNOWN BUGS / RISKS**:
- `grep -q` exit code not propagated through PowerShell pipeline chain: `cmd | grep -q pat && arm` fires the wrong arm because `$LASTEXITCODE` is not reliably set after a pipeline segment ends — `PipeDifferentialTests.cs:Differential_Pipe_InCondition_GrepQNoMatch` (golden)
- `grep -q` still emits pipeline objects even in quiet mode: BashObjects from the upstream `echo` pass through `Invoke-BashGrep -q` and appear on stdout, leaking to the terminal — `PipeDifferentialTests.cs:Differential_Pipe_InCondition_GrepQMatches` (golden)
- Broken pipe hangs forever on Windows: `yes | head -n 3` never terminates because Windows has no SIGPIPE. The `yes` producer is not killed when `head` closes stdin. See MEMORY.md "Windows process death" — `PipeDifferentialTests.cs:Differential_Pipe_BrokenPipe_HeadClosesEarly` (skipped)
- `set -o pipefail` standalone does not implement PIPESTATUS tracking: `false | true; echo $?` with pipefail on still prints 0 instead of 1 because ps-bash maps pipefail only as part of `set -euo pipefail` and does not track per-stage exit codes — `PipeDifferentialTests.cs:Differential_Pipe_ExitCode_PipefailOn_FirstFailurePropagates` (golden)
- `|&` wording differs: stderr from `ls /nonexistent` on ps-bash uses PowerShell error message format rather than the Unix `ls: cannot access` format; bytes differ even though the merge path works — `PipeDifferentialTests.cs:Differential_PipeAmp_StderrMergedToStdout` (golden)

**PRIORITY GAPS** (top 3 max):
1. Fix `grep -q` exit code propagation in pipeline chains: `Invoke-BashGrep` must set `$global:LASTEXITCODE` based on whether any line matched, not rely on the implicit PowerShell `$?` after a pipeline segment. This breaks `cmd | grep -q pat && arm` patterns used extensively in scripts.
2. Fix broken-pipe termination on Windows: implement a Job Object or parent-PID poller to kill the producer process when the consumer closes its stdin, so `yes | head -n 3` and similar patterns terminate. See MEMORY.md "Windows process death" and "Process spawn contract".
3. Implement PIPESTATUS tracking: record each pipeline stage's exit code in `$global:BashPipeStatus` and use the rightmost non-zero code when `set -o pipefail` is active. Currently `false | true` always reports 0 with pipefail on.

---



**EXISTING TESTS** ( file:test ) :
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVar_EmitsEnvVar`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarWithDefault_EmitsNullCoalescing`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVarQuestionMark_EmitsLastExitCode`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarLength_EmitsLength`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVarAt_EmitsArgs`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVarHash_EmitsArgsCount`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVarDollarDollar_EmitsPid`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PositionalVar1_EmitsArgsIndex`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PositionalVar9_EmitsArgsIndex`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVar0_EmitsMyCommand`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarAssignDefault_EmitsNullCoalescingAssign`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarAlternative_EmitsConditional`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarError_EmitsThrow`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarSuffixRemoval_EmitsReplace`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarPrefixRemoval_EmitsReplace`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarInsideDoubleQuotes_EmitsEnvVar`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SpecialVarStar_EmitsArgs`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarHomePsBuiltin_EmitsHomeDirect`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarInsideDoubleQuotes_EmitsDollarEnv`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarDefaultInsideDoubleQuotes_EmitsSubexpression`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarSuffixRemovalInsideDoubleQuotes_EmitsSubexpression`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarLengthInsideDoubleQuotes_EmitsSubexpression`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarPrefixRemovalInsideDoubleQuotes_EmitsSubexpression`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_BracedVarAlternativeInsideDoubleQuotes_EmitsSubexpression`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_SimpleBracedVarInsideDoubleQuotes_NoSubexpression`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_BasicVar_Expands`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_BracedVar_Expands`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_UndefinedVar_ExpandsEmpty`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_DefaultOperator_UnsetVar`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_DefaultOperator_SetVar_ReturnsVar`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_AssignDefault_UnsetVar_AssignsAndReturns`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_AlternativeOperator_SetVar`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_AlternativeOperator_UnsetVar`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_Substring_OffsetAndLength`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_Substring_OffsetOnly`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SuffixRemove_Shortest`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SuffixRemove_Longest` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_PrefixRemove_Shortest`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_PrefixRemove_Longest` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SubstituteFirst` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SubstituteAll`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_CaseUpperAll`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_CaseLowerAll`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_CaseUpperFirst`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_CaseLowerFirst`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_Length_StringVar`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_ExitCode_True`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_ExitCode_False`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_At_PositionalParams` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_Hash_Count` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_Positional_1to3` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_SpecialVar_Pid_IsNonEmptyInteger` ( golden -- PID differs per process )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_QuotedAt_PreservesSpacesInArgs` ( golden -- known bug )
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_QuotedVar_SpacesPreserved`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_Injection_VarContainsDollarParen_NoExec`
- `src/PsBash.Differential.Tests/VariableExpansionDifferentialTests.cs:Differential_Injection_VarContainsSemicolon_NoSplit`

**FAILURE-SURFACE COVERAGE** ( per Directive 3 axes ) :
| Axis | Covered? | Test ref / gap note |
| ------ | ---------- | --------------------- |
| Empty input | PARTIAL | `Differential_UndefinedVar_ExpandsEmpty` -- unset var ; no test for `x=""; echo ${x:-default}` |
| Large input | NO | gap -- no test streams large value through `${#var}` or substitution |
| Unicode | NO | gap -- `${x^^}` on unicode: PS `.ToUpper()` and bash `^^` differ for non-ASCII ; untested |
| CRLF input | NO | gap -- no test for variable containing `\r\n` through substitution |
| Broken pipe | N/A | variable expansion is not a pipeline source ; skip justified |
| Slow reader | N/A | not a pipeline consumer ; skip justified |
| Signal during execute | NO | gap -- no test for Ctrl-C during `set --` + loop |
| Exit code propagation | YES | `Differential_SpecialVar_ExitCode_True`, `Differential_SpecialVar_ExitCode_False` |
| Stderr interleave | NO | gap -- no test for `${VAR:?msg}` writing error to stderr |
| Working dir state | N/A | variable expansion does not depend on cwd ; skip justified |
| Environment leak | YES | `Differential_UndefinedVar_ExpandsEmpty` -- uses unique env name to avoid cross-test contamination |
| Quoting / injection | YES | `Differential_Injection_VarContainsDollarParen_NoExec`, `Differential_Injection_VarContainsSemicolon_NoSplit`, `Differential_QuotedVar_SpacesPreserved` |
| Platform-locked file | N/A | no file operations ; skip justified |
| Missing target | YES | `Differential_DefaultOperator_UnsetVar`, `Differential_AlternativeOperator_UnsetVar` -- undefined var |
| Recursion depth | NO | gap -- no test for nested command substitution inside `${}` ( e.g. `${$(echo x):-default}` ) |

**MODE COVERAGE** ( per Directive 4 modes ) :
| Mode | Covered? | Test ref / gap note |
| ------ | ---------- | --------------------- |
| M1 -c | YES | all differential tests use `ps-bash -c "script"` via `BashOracleFixture.RunOneAsync` |
| M2 stdin pipe | NO | gap -- no test pipes a script containing `${}` via stdin |
| M3 file arg | NO | gap -- no test passes a `.sh` file containing variable expansion |
| M4 interactive | NO | gap -- no PTY test for interactive variable expansion at REPL |
| M5 Invoke-BashEval | NO | gap -- no test evaluates `${}` forms via `Invoke-BashEval` cmdlet |
| M6 Invoke-BashSource | NO | gap -- no test sources a `.sh` file that uses `${}` operators |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 32 differential tests: 25 use EqualAsync ( live bash diff ) , 7 use GoldenAsync ( known bugs documented )

**KNOWN BUGS / RISKS**:
- `${var%%pat}` with wildcard ( e.g. `%%.*` ) : emits `($env:x -replace  ' .*$ ' , '  ' )` -- regex `.*$` greedily matches entire string, not just suffix. `PsEmitter.cs:EmitBracedVar` ~line 1864.
- `${var##pat}` with wildcard ( e.g. `##*.` ) : emits `($env:x -replace  ' ^*. ' , '  ' )` -- `*.` is invalid regex ( unescaped `*` ) . No glob-to-regex translation. `PsEmitter.cs:EmitBracedVar` ~line 1876.
- `${var/pat/rep}` first-occurrence: PS `-replace` is always global ; bash `${x/l/L}` replaces only first match. Produces "heLLo" instead of "heLlo". `PsEmitter.cs:EmitBracedVar` ~line 1892.
- `set -- a b c`: emits `$global:BashPositional = @(a, b, c)` -- bare words unquoted ; PS parse error ( exit 1 ) . Should be `@( ' a ' ,  ' b ' ,  ' c ' )`. Breaks `$#`, `$@`, `$1..$9`, `"$@"` split. `PsEmitter.cs` set-handling in `EmitSimple`.
- `$_` ( last argument ) : maps to `$global:BashLastArg` but nothing in the runtime sets this global -- always null.
- `$-` ( shell flags ) : maps to `$global:BashFlags` but the module does not maintain this global -- always null.
- `set -u` ( nounset ) : accessing undefined var throws in ps-bash with different error message than bash ' s `unbound variable` -- stderr and exit code may differ.
- Negative substring offset `${var: -3}` (space before `-` required in bash): emitter offset parsing accepts `-` digit but negative `.Substring()` throws in .NET -- untested.

**PRIORITY GAPS** (top 3 max):
1. Fix `set -- args` emitter: quote string literal values in the emitted `@( ' a ' , ' b ' , ' c ' )` array. Single root cause that breaks four test categories ($#, $@, $1..$9, "$@" splitting).
2. Fix `${var/pat/rep}` first-occurrence: use `[regex]::Replace(str, pattern, replacement, 1)` to limit to one replacement.
3. Fix glob-wildcard in `##`/`%%` pattern removal: translate bash glob pattern to .NET regex before emitting `-replace` (`*` -> `.*`, `?` -> `.`, literal `.` -> `\.`).

---

### FEATURE: Background jobs (&, wait, wait $!, jobs, kill)

**EXISTING TESTS** (file:test):
- `src/PsBash.Core.Tests/Parser/BashLexerTests.cs:Tokenize_Ampersand_ReturnsAmpToken`
- `src/PsBash.Core.Tests/Parser/PsEmitterTests.cs:Transpile_PipeAmpersand_EmitsStderrMerge`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Background_TrueAndWait_ExitsZero`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Background_LastPidIsNonEmpty`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Background_WaitAll_ReturnsAfterCompletion`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Wait_NoJobs_ExitsZero`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Background_WaitLastPid_Continues`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Jobs_NoProcesses_EmitsNothing`
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Kill_ListSignals_Golden` (golden)
- `src/PsBash.Differential.Tests/BackgroundJobDifferentialTests.cs:Differential_Background_QuotedArg_Golden` (golden)

**FAILURE-SURFACE COVERAGE** (per Directive 3 axes):
| Axis | Covered? | Test ref / gap note |
|------|----------|---------------------|
| Empty input | YES | `Differential_Wait_NoJobs_ExitsZero` — wait with no jobs exits cleanly |
| Large input | NO | gap — no test for a background job producing large stdout |
| Unicode | NO | gap — no test for backgrounded command with non-ASCII output |
| CRLF input | N/A | background job spawns child process; CRLF normalization not applicable at this layer |
| Broken pipe | NO | gap — no test where a backgrounded command's stdout pipe breaks |
| Slow reader | N/A | background stdout is swallowed by async reads in Invoke-BashBackground; not applicable |
| Signal during execute | NO | gap — no test for kill -TERM on a background PID; SIGHUP not deliverable on Windows (MEMORY.md) |
| Exit code propagation | YES | `Differential_Background_TrueAndWait_ExitsZero` — wait exit code is 0 after job succeeds |
| Stderr interleave | NO | gap — no test for 2>&1 from a background job |
| Working dir state | NO | gap — no test that cd inside a background job does not affect the foreground shell |
| Environment leak | NO | gap — no test that env mutations in a background job are isolated |
| Quoting / injection | YES | `Differential_Background_QuotedArg_Golden` — args with spaces survive & emission |
| Platform-locked file | N/A | background jobs do not lock files; skip justified |
| Missing target | YES | `Differential_Jobs_NoProcesses_EmitsNothing` — jobs with empty list; `kill -l` lists signals |
| Recursion depth | NO | gap — no test for nested background jobs (cmd & within a background job) |

**MODE COVERAGE** (per Directive 4 modes):
| Mode | Covered? | Test ref / gap note |
|------|----------|---------------------|
| M1 -c | YES | EqualAsync tests use ps-bash -c; golden tests use RunOneAsync -c |
| M2 stdin pipe | NO | gap — no test pipes a script with & through ps-bash stdin |
| M3 file arg | NO | gap — no test passes a .sh file with background jobs to ps-bash |
| M4 interactive | NO | gap — no PTY test for backgrounding at the REPL (fg/bg/jobs at interactive prompt) |
| M5 Invoke-BashEval | NO | gap — no test evaluates a & command via Invoke-BashEval cmdlet |
| M6 Invoke-BashSource | NO | gap — no test sources a script that backgrounds a command |

**ORACLE STATUS**:
- DIFFERENTIAL TESTS PRESENT? YES
- 8 differential tests added in `BackgroundJobDifferentialTests.cs`
- 6 use EqualAsync (skip on Windows without WSL bash oracle; live bash diff on Linux/macOS CI)
- 2 use GoldenAsync (frozen output for platform-independent validation):
  - `Differential_Kill_ListSignals_Golden`: kill -l signal list differs between bash and ps-bash; golden freezes ps-bash output
  - `Differential_Background_QuotedArg_Golden`: background job stdout is not forwarded to parent by Invoke-BashBackground architecture; golden freezes foreground-only output

**KNOWN BUGS / RISKS**:
- Background job stdout/stderr is not forwarded to the foreground shell: `Invoke-BashBackground` spawns a child pwsh process with `BeginOutputReadLine`/`BeginErrorReadLine` to prevent deadlocks, but no event handler forwards child output to the parent's stdout — `PsBash.psm1:12524-12525`. Makes `cmd & ; wait` output-silent in ps-bash vs bash.
- `$!` in bash is the background job PID; ps-bash emits `$global:BashBgLastPid` which is set correctly after `Invoke-BashBackground`, but `wait $!` emits `Invoke-BashWait $global:BashBgLastPid` — the `$!` is expanded at transpile-time to a PS expression, not at runtime after the job is registered. This means `pid=$!; wait $pid` may work but `wait $!` inline may reference stale state — `PsEmitter.cs:EmitSimpleVar ~line 1782`.
- `jobs` output format differs from bash: ps-bash emits `[N]\tStatus\tPID\tProcessName`; bash emits `[N]+ Running/Done cmd`. Format mismatch would fail EqualAsync if job list is non-empty.
- `kill -l` signal list in ps-bash is hardcoded in `Invoke-BashKill`; does not match the platform's actual signal table (e.g., macOS has SIGUSR1=30, Linux has SIGUSR1=10).
- `fg`, `bg` builtins are stub implementations in Invoke-BashFg/Invoke-BashBg; job control signal delivery (SIGCONT, SIGSTOP) is not implemented on Windows.

**PRIORITY GAPS** (top 3 max):
1. Background job stdout forwarding: wire an `OutputDataReceived` event handler in `Invoke-BashBackground` to forward child stdout lines to the foreground shell's stdout stream, matching bash semantics where background job output appears in the terminal.
2. `wait $!` inline parity: verify that `wait $!` correctly waits for the PID stored in `$global:BashBgLastPid` at runtime (not transpile-time). Add an EqualAsync test `wait $!; echo after` to cover the round-trip.
3. `jobs` format: either normalize `Invoke-BashJobs` output to match bash format `[N]+ Running/Done cmd`, or document the format difference and add a GoldenAsync test for non-empty job list so format regressions are caught.
