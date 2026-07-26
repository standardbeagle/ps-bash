# Real-world `.sh` corpus sweep (2026-07-25)

Method: transpile every `.sh` file on the dev machine (Git-for-Windows +
`~/.claude` plugin scripts, 100 files) and check two things per file —
`BashTranspiler.Transpile` does not throw, and `[Parser]::ParseInput` on the
result reports zero errors. See the `real-world-sh-corpus-sweep` memory for the
runner snippet.

**Result: 72 → 95 files transpile to valid PowerShell.** Bash-stage parse
failures 14 → 2 (and both survivors are now clean `ParseException`s with an
accurate message, not crashes).

The sweep also led — via oracle-diffing each fix rather than stopping at
"it parses now" — to three RUNTIME bugs it does not itself measure, the
largest being that POSIX bracket classes never worked in any command.

Every fix below was confirmed byte-identical against the bash oracle before
landing. Note how many were **silent wrong output** rather than parse errors —
the parse-error count badly understates the damage this sweep found.

## Fixed

| Shape | Symptom | Commit |
|---|---|---|
| `case x in a) ;; *) …` | empty arm body swallowed its own `;;` → parse error on EVERY Bash-tool command (the shell snapshot's `pkill` guard uses it) | `ba91e36` |
| `[[ $x =~ ^(a\|b)$ ]]` | regex truncated to `^` on the `&&` path (**silent**); "Expected `then`" inside `if` | `f501957` |
| `A=(\n x\n y\n)` | `)` unconsumed; `A=(x\ny)` dropped an element and ran `y` as a command (**silent**) | `355677b` |
| `declare -a A=(x y)`, `readonly A=(x y)` | array dropped AND the following command swallowed (**silent**) | `355677b` |
| `local -a A=(…)` | attribute flag taken as a variable name → junk `$-a = $-a` | `355677b` |
| `done <<< "$var"` | here-string dropped, loop read nothing (**silent**) | `2926419` |
| `for x in one` | emitted `foreach ($x in one)` → "one: command not found" | `2926419` |
| `while read a b` | only the LAST name bound; `$a` unset (**silent**) | `2926419` |
| `${value%\'}` | `\X` compiled to `\\X`, so the quote-strip idiom never matched (**silent**) | `986a6d4` |
| `[ -z "${V:-}" ]` | tested the literal text `($env:V ?? "")` — always false (**silent**) | `5ab55e5` |
| `cmd \|\| case … esac` | compound command is not a valid pipeline-chain operand → parse error | `5ab55e5` |
| `[[ ( a && b ) ]]` | grouping parens dropped the group's operands (**silent**) | `d0f9d7a` |
| `[ -o NAME ]` | unimplemented operator emitted adjacent values → parse error; now `$false` + stderr diagnostic | `d0f9d7a` |
| `X="$(grep "a:b" f)"` | assignment split at the colon; `:b f)` torn out of the command (**silent**) | `9af314a` |
| `PATH=~/bin:~/x` | emitted `$HOMEbin` — not a valid variable reference | `9af314a` |
| `grep "^${field}:"` | suffix-less `${name}` before `:` not braced → PS drive-reference misparse | `803645e` |
| `eval "…" \| sort` | a statement-list SIMPLE command is not a valid pipeline stage | `803645e` |
| `X="$(grep "a\"b" f)"` | backtick escape eaten by the enclosing string → unparseable | `ce1dd7d` |

### Runtime bugs found while oracle-diffing the above

| Shape | Symptom | Commit |
|---|---|---|
| `grep '[[:digit:]]'`, `sed 's/[[:space:]]\+//'`, `awk '/[[:digit:]]/'` | POSIX bracket classes never worked in ANY command — .NET reads `[[:digit:]]` as the set `[:digt`, so they matched nothing and reported no error (**silent**) | `ce1dd7d` |
| `printf … \| grep 'a'; printf … \| grep -E 'b'` | binder-swallowed-flag recovery read a DIFFERENT statement's text, so the second grep lost its `-E` and ran as basic regex (**silent**) | `ce1dd7d` |

The POSIX-class bug masked the flag-recovery one: with the classes matching
nothing, a basic-vs-extended regex mix-up made no visible difference.

## Open

Still failing in the sweep; each needs its own diagnosis.

1. **`backup.sh`, `stop-hook.sh`** — "string is missing the terminator". The
   sibling `resolve-port.sh` was fixed by `ce1dd7d` (nest-depth escaping); these
   two still fail, so they carry a further variant of the nested-quote seam —
   both embed multi-line `awk` / `sed` program text. Not yet reduced.
2. **`test-worktrack-vite-smoke.sh`** — "An empty pipe element is not allowed".
   The sibling `detect-project-type.sh` was fixed by `803645e`; this one has a
   different shape, not yet reduced.
3. **`sqlite3_analyzer.sh`** — "Expected `}` but got EOF" at line 900; a large
   generated file, not yet reduced.
4. **`run-worktrack-vite-smoke.sh`** — `$(( $(date +%s) + 60 ))`: a command
   substitution inside arithmetic is rejected by the typed arithmetic parser.
   Clean error, but bash accepts it.

## Test-suite observations

- `PromptRenderingIntegrationTests.CtrlC_MidInput_ShellRemainsAlive` timed out
  (8 s prompt deadline, 0 chars received) during a run competing with 8 concurrent
  SDK hosts, and passed both isolated and in a quiet full run. Load-sensitive PTY
  timing, not a product break — but it is exactly the flake qa-rubric Directive 2
  says to quarantine rather than ignore if it recurs.
- Running `PsBash.Host.Tests` immediately after another suite can hang the test
  harness: a persisted `ps-bash-host` inherits the runner's redirected stdout, so
  the parent never sees EOF (the `daemon-c-pipe-inheritance-hang` class). Killing
  stray hosts between suites avoids it. Worth checking whether a host spawn path
  still leaks the inherited handle.

## Not a product bug

- `[[ $x =~ ^a\sb ]]` — bash's ERE has no `\s`; .NET regex does. An engine
  difference, same class as the `PIPESTATUS` entries in `known-issues.md`.
- `$HOME` renders as a Windows path (`C:\Users\…`), so a value built from `~`
  differs textually from bash even when structurally correct.
