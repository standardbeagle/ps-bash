# XFAIL triage (oils corpus)

Priority is set by who runs the shell: **agents run non-interactive scripts and `-c` snippets** (pipelines, variables,
arrays, parameter expansion, tests, redirects, exit statuses, cd, background jobs, parse errors). Features only a person
at a terminal uses come last and are fixed only when cheap.

Labels: XPASS (passes; delete marker) / ARTIFACT (expectation nondeterministic or other-shell-only) / WINDOWS (an OS difference: marked `platform-windows-<topic>`, not `gap-*`, and listed under "Platform (Windows)" in intentional-differences.md) /
GAP (real difference). Counts are `## XFAIL ps-bash` lines per file (2026-11-06, ~714 total).

## Tier 1: agent-facing scripting (fix first)
assign-extended 17, vars-special 13, pipeline 12, var-ref 12, nameref 12, array-assoc 11, array 10, dbracket 10,
regex 10, parse-errors 10, builtin-set 10, builtin-meta-assign 10, builtin-cd 14, builtin-process 14, background 11,
bugs 11, nul-bytes 11.

## Tier 2: shell builtins used in scripts, less often
builtin-trap-bash 16 (trap DEBUG/ERR/RETURN), builtin-type-bash 12.

## Tier 3: interactive-only (deprioritized; agents do not use these)
builtin-dirs 13 (pushd/popd/dirs), alias 12 (interactive expansion), ble-idioms 12 (line-editor idioms), builtin-fc 11,
builtin-completion 11, interactive 10, history/readline/prompt files.

## Method
Strip a file's markers in a scratch edit, run `tman test-proj src/PsBash.Spec.Tests --filter "DisplayName~oils/<f>.spec"`,
read the `[Fail]` cases, confirm each against `wsl.exe -d Ubuntu-24.04 -e bash`, then label. Restore with `git checkout`.
Per-case labels are added below as files are worked.

## Findings so far (2026-11-06)

| File | Case(s) | Label | Note |
|---|---|---|---|
| pipeline | `! cmd \|\| x`, `! { }`, `! ( )` | FIXED | negated command lost its stdout (`[void]`); markers removed |
| pipeline | Nested pipelines, `PIPESTATUS` family | GAP | arg-only cmdlets (`seq`, `date`, `id`, `hostname`, ...) never run when the upstream stage emits nothing and error ("cannot be bound") when it emits records; fix = accept and ignore pipeline input in those cmdlets; low agent value |
| pipeline | SIGPIPE 141, lastpipe | GAP-large | process model |
| nameref | 12 cases | GAP-large | namerefs are resolved statically at emit time (`ResolveNameref`); a name forwarded through a second function (`f3 "$@"`) or `a[2]` targets need a runtime nameref |
| vars-special | `$PPID`, `$RANDOM`, `$HOME` unset, HOSTNAME/OSTYPE, OILS_VERSION | ARTIFACT/WINDOWS | recorded values or other-shell-only |
| vars-special | `$_` family (4), `$BASHPID` | GAP | emitter hot path; only `echo` sets `$_`; plan: transpile-time flag when the script mentions `$_` |
