# Bash feature coverage (measured)

How much of bash does ps-bash actually reproduce? This is a **measured** answer from a probe, not a claim.
It is the seed corpus for `spec-suite.md`.

## Published specs this is measured against

ps-bash has no spec of its own to implement; it began as "bash" and the oracle is real bash. The published
references that define "all the features of bash":

- GNU Bash Reference Manual (the 5.2 edition): <https://www.gnu.org/software/bash/manual/bash.html>
- POSIX Shell Command Language (the portable subset): <https://pubs.opengroup.org/onlinepubs/9699919799/utilities/V3_chap02.html>
- Oils/OSH spec tests (pinned to bash 4.4, the largest public conformance corpus): the `spec/` tests of the oils repo.

Known intentional departures are in `intentional-differences.md`; fd and `BASH_SOURCE` behavior is in
`shell-fds-and-bash-source.md`.

## Method

`tests/bash-coverage/snippets.txt` holds 344 one-feature snippets (id `@@` script) grouped by manual chapter:
quoting, expansion, parameter operators, arithmetic, arrays, redirection, builtins, control flow, special variables,
job control, globbing.
`tests/bash-coverage/driver.sh <dir> <out> <skip>` runs each snippet by sourcing it in a subshell with `</dev/null`
and writes `id|rc|output`. The reference is bash 5.2 under WSL (`oracle-bash-5.2.txt`). Run ps-bash with
`tman dev tests/bash-coverage/driver.sh <dir-of-split-snippets> <out> ""` and compare per id.

Caveats: a few multi-line snippets were split imperfectly (several `var.*` ids hold the wrong snippet), so those
rows prove only that the construct does not crash; rows that print an absolute path differ by the Windows drive form
(`C:/` vs `/`) and are not counted below as gaps.

## Result

First measurement (the run this document was written from): 218 of 344 snippets byte-identical to bash.

Re-measured 2026-11-18 on `main` (354 snippet files as split from `snippets.txt`, each run as its own script,
`res=$(bash "$f" 2>&1 </dev/null)`, in ps-bash and in bash 5.2): **301 of 354 byte-identical.** The other 53 are
listed under "Open" below. The sourcing driver (`( . "$f" )`) does not finish on ps-bash: `help cd | head -1` ends
the driver loop, and so does a later `set` snippet; that is a ps-bash bug in its own right, not a property of the
snippets.

### Fixed by this work (each has an oracle test + cassette)

| Gap | Symptom |
|---|---|
| `false` in `eval`/`source` under `set -e` | threw `PsBash.FalseErrexit`, killed the whole shell |
| `cmd 2>&1 \| head -1` when the first line is stderr | the rest of the script never ran (pipeline stop swallowed) |
| `case` pattern with an expansion (`$i`, `"$i"`, `*" $i "*`) | never matched (pattern was the literal text `$i`) |
| unquoted `${a[@]}` beside another argument | "Cannot convert System.Object[] to String" |
| `echo {1..5} {a..e}` (two brace words) | same binding failure |
| `let` | assigned PowerShell variables the `$env:` reads never saw |
| `${!name}` | treated as array keys; now indirect expansion |
| `eval echo \$$a` | emitted unparseable PowerShell |
| a sourced/eval'd pipeline | output ended each line with CR LF (fused batch) |
| `getopts` | command not found; now a builtin with silent mode, clusters, `OPTIND`/`OPTARG` |
| `"${a[@]}"` / `"$@"` beside other words | joined into one argument; now one argument per element (commands, `for` lists, array literals) |

### Open (re-measured 2026-11-18)

Missing builtins (command not found): `bind caller disown enable logout suspend`; `help` (not probed in isolation,
see above). `coproc` and `select … done <<< word` are parse errors. Now present: `getopts ulimit complete compgen
history fc builtin`, history expansion (`!!` stays literal in a script, as in bash).

Semantics gaps (the construct runs but differs):

- Arrays / parameters: `declare -A m=([x y]=1)` (a key with a blank), `${!:+word}` (an operator on `$!` emits a
  broken command), `echo ${@:2:2} ${*: -1}` (binding error), `${n@a}` (empty).
- Special variables not provided: `BASH_ALIASES DIRSTACK GROUPS PPID SHLVL`; `$IFS` reads empty before it is
  assigned (splitting still uses the default); `( UID=5 )` gives no readonly error.
- Traps: a `DEBUG` trap set and used on ONE line (`trap 'echo D' DEBUG; echo a`) does not run (multi-line scripts
  do: `DebugTrapDifferentialTests`); `( trap 'echo err' ERR; false )` does not run the trap.
- xtrace: `PS4` assigned in a subshell is not used (`( PS4='>> '; set -x; echo a )` traces with `+ `).
- Pipelines: `nosuchcmd 2>&1 | head -1; echo ${PIPESTATUS[0]}` is 0 (bash 127); no `SIGPIPE` status 141
  (`intentional-differences.md`).
- Redirection: `exec 5<> f` is a parse error; `{ …; } 3>&1 1>&2 2>&3 | sort` orders the two lines differently.
- Diagnostics text: shell messages other than "command not found" print `bash: ` or `ps-bash: ` where bash prints
  `SCRIPT: line N: ` (readonly variable, unbound variable, `${x:?}`, `alias`/`type` not found, `no match`,
  `Permission denied`, `cannot overwrite existing file`, redirect failures; `error-handling.md` §2.6); `1/0` lacks
  `(error token is "0")`; `shift 2` past the end prints a message (bash is silent, status 1); an empty command's
  syntax error has ps-bash's wording.
- extglob without `shopt -s extglob` on an earlier line is accepted (bash: syntax error); see
  `intentional-differences.md`.

Fixed since the first measurement (were listed here as gaps, now byte-identical): `pipefail`, `PIPESTATUS`,
`set -u` (apart from the message prefix), `set -x` trace, `set -o` listing, `shopt -p`, `test -v`, `printf %q` and
`%(…)T`, `read -d`, `trap` for EXIT / RETURN / signals, sparse arrays, `declare -p`, `export -n`, `local` dynamic
scope, `BASHOPTS SHELLOPTS LINENO OPTIND TIMEFORMAT`, `time` formatting, `exec N>file`, `/dev/fd/N`, `{var}>file`,
`globstar`, `nocaseglob`, extglob pathname expansion, here-document into a function, `unset -f`.

### Windows-environment differences (not counted as bugs)

Absolute paths print with a drive (`C:/`; `/tmp` is the Windows temp directory), `ln -s` needs a privilege, there are
no file mode bits (`[ -x f ]` is true for any file, `./f` is `Permission denied`), `~+` and `OLDPWD` carry the drive.

## Next

This probe finds breadth, one snippet per feature. The conformance suite in `spec-suite.md` adds depth: the Oils
cases per feature, run in-host, with expected output recorded from real bash. Each gap above becomes an XFAIL case
there until fixed, and the fix removes the marker.
