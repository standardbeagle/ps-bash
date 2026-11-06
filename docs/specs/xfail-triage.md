# XFAIL triage (oils corpus)

Priority is set by who runs the shell: **agents run non-interactive scripts and `-c` snippets** (pipelines, variables,
arrays, parameter expansion, tests, redirects, exit statuses, cd, background jobs, parse errors). Features only a person
at a terminal uses come last and are fixed only when cheap.

Labels: XPASS (passes; delete marker) / ARTIFACT (expectation nondeterministic or other-shell-only) / WINDOWS /
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
