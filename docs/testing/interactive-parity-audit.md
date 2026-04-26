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
