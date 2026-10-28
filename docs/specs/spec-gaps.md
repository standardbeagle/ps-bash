# Spec gaps by area

Known ps-bash/bash 5.2 divergences recorded as XFAIL in the spec corpus, grouped by the area (spec file) they were found in. An id is `gap-<kind>-<area>`; a case marked with it is a verified failure, not yet root-caused. Fix a gap, then re-run with `PSBASH_SPEC_TRIAGE=1` to unmark the cases that now pass (an XFAIL that passes fails the suite).

| Id | File | Cases |
|---|---|---|
| `gap-differential-nativestdiobytes` | `spec/differential/nativestdiobytes.spec.sh` | 46 |
| `gap-oils-array-assoc` | `spec/oils/array-assoc.spec.sh` | 36 |
| `gap-oils-arith` | `spec/oils/arith.spec.sh` | 33 |
| `gap-oils-array` | `spec/oils/array.spec.sh` | 33 |
| `gap-oils-builtin-completion` | `spec/oils/builtin-completion.spec.sh` | 33 |
| `gap-oils-brace-expansion` | `spec/oils/brace-expansion.spec.sh` | 29 |
| `gap-oils-builtin-printf` | `spec/oils/builtin-printf.spec.sh` | 29 |
| `gap-oils-prompt` | `spec/oils/prompt.spec.sh` | 28 |
| `gap-oils-alias` | `spec/oils/alias.spec.sh` | 27 |
| `gap-oils-array-sparse` | `spec/oils/array-sparse.spec.sh` | 27 |
| `gap-oils-sh-options` | `spec/oils/sh-options.spec.sh` | 27 |
| `gap-oils-vars-special` | `spec/oils/vars-special.spec.sh` | 26 |
| `gap-oils-assign-extended` | `spec/oils/assign-extended.spec.sh` | 26 |
| `gap-oils-var-ref` | `spec/oils/var-ref.spec.sh` | 26 |
| `gap-oils-builtin-trap` | `spec/oils/builtin-trap.spec.sh` | 26 |
| `gap-oils-builtin-read` | `spec/oils/builtin-read.spec.sh` | 24 |
| `gap-oils-nameref` | `spec/oils/nameref.spec.sh` | 23 |
| `gap-oils-builtin-type-bash` | `spec/oils/builtin-type-bash.spec.sh` | 20 |
| `gap-oils-ysh-builtin-private` | `spec/oils/ysh-builtin-private.spec.sh` | 19 |
| `gap-oils-builtin-kill` | `spec/oils/builtin-kill.spec.sh` | 19 |
| `gap-oils-builtin-bracket` | `spec/oils/builtin-bracket.spec.sh` | 19 |
| `gap-oils-builtin-trap-bash` | `spec/oils/builtin-trap-bash.spec.sh` | 19 |
| `gap-oils-builtin-set` | `spec/oils/builtin-set.spec.sh` | 19 |
| `gap-oils-builtin-dirs` | `spec/oils/builtin-dirs.spec.sh` | 18 |
| `gap-oils-var-op-patsub` | `spec/oils/var-op-patsub.spec.sh` | 18 |
| `gap-oils-ble-idioms` | `spec/oils/ble-idioms.spec.sh` | 18 |
| `gap-oils-regex` | `spec/oils/regex.spec.sh` | 18 |
| `gap-oils-dbracket` | `spec/oils/dbracket.spec.sh` | 18 |
| `gap-oils-builtin-trap-err` | `spec/oils/builtin-trap-err.spec.sh` | 18 |
| `gap-oils-background` | `spec/oils/background.spec.sh` | 16 |
| `gap-oils-var-op-bash` | `spec/oils/var-op-bash.spec.sh` | 16 |
| `gap-oils-globignore` | `spec/oils/globignore.spec.sh` | 16 |
| `gap-oils-builtin-cd` | `spec/oils/builtin-cd.spec.sh` | 16 |
| `gap-oils-assign` | `spec/oils/assign.spec.sh` | 14 |
| `gap-oils-bugs` | `spec/oils/bugs.spec.sh` | 14 |
| `gap-oils-ysh-string` | `spec/oils/ysh-string.spec.sh` | 14 |
| `gap-oils-pipeline` | `spec/oils/pipeline.spec.sh` | 14 |
| `gap-oils-builtin-process` | `spec/oils/builtin-process.spec.sh` | 14 |
| `gap-oils-builtin-meta` | `spec/oils/builtin-meta.spec.sh` | 13 |
| `gap-oils-var-op-test` | `spec/oils/var-op-test.spec.sh` | 13 |
| `gap-oils-parse-errors` | `spec/oils/parse-errors.spec.sh` | 12 |
| `gap-oils-nul-bytes` | `spec/oils/nul-bytes.spec.sh` | 12 |
| `gap-oils-builtin-special` | `spec/oils/builtin-special.spec.sh` | 12 |
| `gap-oils-builtin-meta-assign` | `spec/oils/builtin-meta-assign.spec.sh` | 11 |
| `gap-oils-interactive` | `spec/oils/interactive.spec.sh` | 11 |
| `gap-oils-builtin-fc` | `spec/oils/builtin-fc.spec.sh` | 11 |
| `gap-oils-sh-usage` | `spec/oils/sh-usage.spec.sh` | 11 |
| `gap-oils-tilde` | `spec/oils/tilde.spec.sh` | 11 |
| `gap-oils-ysh-assign` | `spec/oils/ysh-assign.spec.sh` | 11 |
| `gap-oils-ysh-builtins` | `spec/oils/ysh-builtins.spec.sh` | 11 |
| `gap-oils-ysh-expr` | `spec/oils/ysh-expr.spec.sh` | 11 |
| `gap-oils-ysh-options` | `spec/oils/ysh-options.spec.sh` | 11 |
| `gap-differential-bashparitygap` | `spec/differential/bashparitygap.spec.sh` | 11 |
| `gap-oils-errexit` | `spec/oils/errexit.spec.sh` | 10 |
| `gap-oils-glob` | `spec/oils/glob.spec.sh` | 10 |
| `gap-oils-errexit-osh` | `spec/oils/errexit-osh.spec.sh` | 10 |
| `gap-oils-dparen` | `spec/oils/dparen.spec.sh` | 9 |
| `gap-oils-word-split` | `spec/oils/word-split.spec.sh` | 9 |
| `gap-oils-extglob-files` | `spec/oils/extglob-files.spec.sh` | 9 |
| `gap-oils-ysh-augmented` | `spec/oils/ysh-augmented.spec.sh` | 9 |
| `gap-oils-builtin-umask` | `spec/oils/builtin-umask.spec.sh` | 9 |
| `gap-oils-var-sub-quote` | `spec/oils/var-sub-quote.spec.sh` | 9 |
| `gap-oils-builtin-eval-source` | `spec/oils/builtin-eval-source.spec.sh` | 9 |
| `gap-oils-builtin-echo` | `spec/oils/builtin-echo.spec.sh` | 9 |
| `gap-oils-array-literal` | `spec/oils/array-literal.spec.sh` | 9 |
| `gap-oils-builtin-history` | `spec/oils/builtin-history.spec.sh` | 9 |
| `gap-oils-sh-options-bash` | `spec/oils/sh-options-bash.spec.sh` | 8 |
| `gap-oils-var-op-strip` | `spec/oils/var-op-strip.spec.sh` | 8 |
| `gap-oils-ysh-with-sh` | `spec/oils/ysh-with-sh.spec.sh` | 8 |
| `gap-oils-toysh` | `spec/oils/toysh.spec.sh` | 8 |
| `gap-oils-array-assign` | `spec/oils/array-assign.spec.sh` | 8 |
| `gap-oils-exit-status` | `spec/oils/exit-status.spec.sh` | 8 |
| `gap-oils-blog1` | `spec/oils/blog1.spec.sh` | 8 |
| `gap-oils-ysh-json` | `spec/oils/ysh-json.spec.sh` | 8 |
| `gap-oils-redirect` | `spec/oils/redirect.spec.sh` | 8 |
| `gap-oils-builtin-bash` | `spec/oils/builtin-bash.spec.sh` | 8 |
| `gap-oils-redirect-multi` | `spec/oils/redirect-multi.spec.sh` | 8 |
| `gap-oils-builtin-bind` | `spec/oils/builtin-bind.spec.sh` | 8 |
| `gap-oils-loop` | `spec/oils/loop.spec.sh` | 7 |
| `gap-oils-ysh-builtin-shopt` | `spec/oils/ysh-builtin-shopt.spec.sh` | 7 |
| `gap-oils-paren-ambiguity` | `spec/oils/paren-ambiguity.spec.sh` | 7 |
| `gap-oils-strict-options` | `spec/oils/strict-options.spec.sh` | 7 |
| `gap-oils-array-compat` | `spec/oils/array-compat.spec.sh` | 7 |
| `gap-oils-quote` | `spec/oils/quote.spec.sh` | 7 |
| `gap-oils-ysh-expr-arith` | `spec/oils/ysh-expr-arith.spec.sh` | 7 |
| `gap-oils-ble-features` | `spec/oils/ble-features.spec.sh` | 7 |
| `gap-oils-ysh-regex` | `spec/oils/ysh-regex.spec.sh` | 6 |
| `gap-oils-arith-context` | `spec/oils/arith-context.spec.sh` | 6 |
| `gap-oils-assign-deferred` | `spec/oils/assign-deferred.spec.sh` | 6 |
| `gap-oils-command_` | `spec/oils/command_.spec.sh` | 6 |
| `gap-oils-builtin-vars` | `spec/oils/builtin-vars.spec.sh` | 6 |
| `gap-oils-builtin-type` | `spec/oils/builtin-type.spec.sh` | 6 |
| `gap-oils-process-sub` | `spec/oils/process-sub.spec.sh` | 6 |
| `gap-oils-ysh-multiline` | `spec/oils/ysh-multiline.spec.sh` | 5 |
| `gap-oils-toysh-posix` | `spec/oils/toysh-posix.spec.sh` | 5 |
| `gap-oils-shell-grammar` | `spec/oils/shell-grammar.spec.sh` | 5 |
| `gap-oils-extglob-match` | `spec/oils/extglob-match.spec.sh` | 5 |
| `gap-oils-ble-unset` | `spec/oils/ble-unset.spec.sh` | 5 |
| `gap-oils-hay` | `spec/oils/hay.spec.sh` | 5 |
| `gap-oils-explore-parsing` | `spec/oils/explore-parsing.spec.sh` | 5 |
| `gap-oils-ysh-builtin-trap` | `spec/oils/ysh-builtin-trap.spec.sh` | 4 |
| `gap-oils-blog-other1` | `spec/oils/blog-other1.spec.sh` | 4 |
| `gap-oils-serialize` | `spec/oils/serialize.spec.sh` | 4 |
| `gap-oils-command-sub` | `spec/oils/command-sub.spec.sh` | 4 |
| `gap-oils-builtin-getopts` | `spec/oils/builtin-getopts.spec.sh` | 4 |
| `gap-oils-type-compat` | `spec/oils/type-compat.spec.sh` | 4 |
| `gap-oils-ysh-scope` | `spec/oils/ysh-scope.spec.sh` | 4 |
| `gap-oils-var-op-slice` | `spec/oils/var-op-slice.spec.sh` | 4 |
| `gap-oils-nix-idioms` | `spec/oils/nix-idioms.spec.sh` | 4 |
| `gap-oils-ysh-unicode` | `spec/oils/ysh-unicode.spec.sh` | 3 |
| `gap-oils-ysh-command-sub` | `spec/oils/ysh-command-sub.spec.sh` | 3 |
| `gap-oils-ysh-builtin-process` | `spec/oils/ysh-builtin-process.spec.sh` | 3 |
| `gap-oils-temp-binding` | `spec/oils/temp-binding.spec.sh` | 3 |
| `gap-oils-var-op-len` | `spec/oils/var-op-len.spec.sh` | 3 |
| `gap-oils-arith-dynamic` | `spec/oils/arith-dynamic.spec.sh` | 3 |
| `gap-oils-assign-dialects` | `spec/oils/assign-dialects.spec.sh` | 3 |
| `gap-oils-ysh-list` | `spec/oils/ysh-list.spec.sh` | 3 |
| `gap-differential-bashsource` | `spec/differential/bashsource.spec.sh` | 3 |
| `gap-differential-backgroundjobprocess` | `spec/differential/backgroundjobprocess.spec.sh` | 3 |
| `gap-oils-var-sub` | `spec/oils/var-sub.spec.sh` | 3 |
| `gap-oils-zsh-assoc` | `spec/oils/zsh-assoc.spec.sh` | 3 |
| `gap-oils-sh-func` | `spec/oils/sh-func.spec.sh` | 3 |
| `gap-oils-builtin-misc` | `spec/oils/builtin-misc.spec.sh` | 3 |
| `gap-oils-ysh-proc` | `spec/oils/ysh-proc.spec.sh` | 3 |
| `gap-oils-ysh-builtin-error` | `spec/oils/ysh-builtin-error.spec.sh` | 3 |
| `gap-oils-here-doc` | `spec/oils/here-doc.spec.sh` | 3 |
| `gap-oils-globstar` | `spec/oils/globstar.spec.sh` | 3 |
| `gap-oils-glob-bash` | `spec/oils/glob-bash.spec.sh` | 3 |
| `gap-oils-posix` | `spec/oils/posix.spec.sh` | 3 |
| `gap-oils-func-parsing` | `spec/oils/func-parsing.spec.sh` | 3 |
| `gap-oils-for-expr` | `spec/oils/for-expr.spec.sh` | 3 |
| `gap-oils-fatal-errors` | `spec/oils/fatal-errors.spec.sh` | 3 |
| `gap-oils-empty-bodies` | `spec/oils/empty-bodies.spec.sh` | 3 |
| `gap-oils-divergence` | `spec/oils/divergence.spec.sh` | 3 |
| `gap-oils-command-parsing` | `spec/oils/command-parsing.spec.sh` | 3 |
| `gap-oils-command-sub-ksh` | `spec/oils/command-sub-ksh.spec.sh` | 3 |
| `gap-oils-ysh-bugs` | `spec/oils/ysh-bugs.spec.sh` | 2 |
| `gap-oils-ysh-builtin-eval` | `spec/oils/ysh-builtin-eval.spec.sh` | 2 |
| `gap-oils-ysh-dict` | `spec/oils/ysh-dict.spec.sh` | 2 |
| `gap-oils-xtrace` | `spec/oils/xtrace.spec.sh` | 2 |
| `gap-oils-ysh-expr-bool` | `spec/oils/ysh-expr-bool.spec.sh` | 2 |
| `gap-oils-ysh-namespaces` | `spec/oils/ysh-namespaces.spec.sh` | 2 |
| `gap-oils-redirect-command` | `spec/oils/redirect-command.spec.sh` | 2 |
| `gap-oils-redir-order` | `spec/oils/redir-order.spec.sh` | 2 |
| `gap-oils-print-source-code` | `spec/oils/print-source-code.spec.sh` | 2 |
| `gap-oils-osh-bugs` | `spec/oils/osh-bugs.spec.sh` | 2 |
| `gap-oils-ysh-var-sub` | `spec/oils/ysh-var-sub.spec.sh` | 2 |
| `gap-oils-known-differences` | `spec/oils/known-differences.spec.sh` | 2 |
| `gap-oils-ysh-printing` | `spec/oils/ysh-printing.spec.sh` | 2 |
| `gap-oils-hay-isolation` | `spec/oils/hay-isolation.spec.sh` | 2 |
| `gap-oils-arg-parse` | `spec/oils/arg-parse.spec.sh` | 2 |
| `gap-oils-append` | `spec/oils/append.spec.sh` | 2 |
| `gap-oils-ysh-slice-range` | `spec/oils/ysh-slice-range.spec.sh` | 2 |
| `gap-oils-ysh-user-feedback` | `spec/oils/ysh-user-feedback.spec.sh` | 1 |
| `gap-oils-ysh-TODO-deprecate` | `spec/oils/ysh-TODO-deprecate.spec.sh` | 1 |
| `gap-oils-ysh-stdlib` | `spec/oils/ysh-stdlib.spec.sh` | 1 |
| `gap-oils-ysh-for` | `spec/oils/ysh-for.spec.sh` | 1 |
| `gap-oils-ysh-methods` | `spec/oils/ysh-methods.spec.sh` | 1 |
| `gap-oils-ysh-regex-api` | `spec/oils/ysh-regex-api.spec.sh` | 1 |
| `gap-oils-ysh-old` | `spec/oils/ysh-old.spec.sh` | 1 |
| `gap-oils-ysh-regex-bytes-chars` | `spec/oils/ysh-regex-bytes-chars.spec.sh` | 1 |
| `gap-differential-backgroundjob` | `spec/differential/backgroundjob.spec.sh` | 1 |
| `gap-oils-interactive-parse` | `spec/oils/interactive-parse.spec.sh` | 1 |
| `gap-oils-ysh-builtin-help` | `spec/oils/ysh-builtin-help.spec.sh` | 1 |
| `gap-differential-compoundstdin` | `spec/differential/compoundstdin.spec.sh` | 1 |
| `gap-differential-execfd` | `spec/differential/execfd.spec.sh` | 1 |
| `gap-differential-globbraceexpansion` | `spec/differential/globbraceexpansion.spec.sh` | 1 |
| `gap-differential-pathnameexpansion` | `spec/differential/pathnameexpansion.spec.sh` | 1 |
| `gap-differential-processsubfixtures` | `spec/differential/processsubfixtures.spec.sh` | 1 |
| `gap-differential-redirection` | `spec/differential/redirection.spec.sh` | 1 |
| `gap-differential-sed` | `spec/differential/sed.spec.sh` | 1 |
| `gap-differential-variableexpansion` | `spec/differential/variableexpansion.spec.sh` | 1 |
| `gap-oils-blog2` | `spec/oils/blog2.spec.sh` | 1 |
| `gap-oils-bool-parse` | `spec/oils/bool-parse.spec.sh` | 1 |
| `gap-oils-builtin-times` | `spec/oils/builtin-times.spec.sh` | 1 |
| `gap-oils-history-expand` | `spec/oils/history-expand.spec.sh` | 1 |
| `gap-oils-if_` | `spec/oils/if_.spec.sh` | 1 |
| `gap-oils-introspect` | `spec/oils/introspect.spec.sh` | 1 |
| `gap-oils-let` | `spec/oils/let.spec.sh` | 1 |
| `gap-oils-nocasematch-match` | `spec/oils/nocasematch-match.spec.sh` | 1 |
| `gap-oils-shell-bugs` | `spec/oils/shell-bugs.spec.sh` | 1 |
| `gap-oils-spec-harness-bug` | `spec/oils/spec-harness-bug.spec.sh` | 1 |
| `gap-oils-unicode` | `spec/oils/unicode.spec.sh` | 1 |
| `gap-oils-vars-bash` | `spec/oils/vars-bash.spec.sh` | 1 |
| `gap-oils-ysh-blocks` | `spec/oils/ysh-blocks.spec.sh` | 1 |
| `gap-oils-ysh-cmd-lang` | `spec/oils/ysh-cmd-lang.spec.sh` | 1 |
| `gap-oils-zsh-idioms` | `spec/oils/zsh-idioms.spec.sh` | 1 |

Total: 1562 cases in 187 areas.

## Fix priority

Ranked by how often real scripts hit the feature (likelihood), how bad the failure is (silent wrong output or a leaked
PowerShell artifact beats a missing diagnostic), and how big the fix looks. Rows marked *probed* were reproduced against
bash 5.2 in WSL; the rest are estimates from case names.

| # | Gap | Probed behaviour (ps-bash vs bash) | Cases | Severity | Size |
|---|---|---|---|---|---|
| 1 | Brace expansion followed by `$var` | `x=1; echo {a,b}$x` prints `a$env:x b$env:x`, bash `a1 b1` | brace-expansion | high: emitter leaks PowerShell text | small |
| 2 | `${v/pat/rep}` glob patterns and `#`/`%` anchors | `${v/c*/XX}` unchanged; `${s/#xx/yy}` and `${s/%xx/yy}` ignore the anchor | var-op-patsub 18 | high: silent wrong output | small |
| 3 | printf precision, `%*.*s`, `'c` | `%6.4d`, `%5.2s` ignore precision; `%*.*s` printed literally; `printf %d \'a` gives 0, bash 97 | builtin-printf 29 | high: silent wrong output | small |
| 4 | `${u=z}` visible to later words of the same command | `echo ${u=z} $u` prints `z`, bash `z z` | var-op-test 13 | medium | small |
| 5 | Brace range with step | `{a..e..2}` ignores the step, bash `a c e` | brace-expansion | medium | small |
| 6 | Sparse arrays: `unset a[i]`, `${!a[@]}`, negative indexes | `${!a[@]}` after `unset a[1]` prints `0 1 2` (bash `0 2`) | array 33, array-sparse 27, array-assoc 36 | medium | medium: storage is dense |
| 7 | Tilde expansion in assignments and defaults | `x=foo:~`, `${undef:-~}`, `~user` | tilde 11 | medium | small |
| 8 | `[[ ]]` syntax errors, numeric edge cases | accepts malformed `[[ ]]` | dbracket 18 | low: bad input only | small |
| 9 | Arithmetic: `${..}`, subscripts, `base#` | evaluator limits | arith 33 | medium | medium |
| 10 | PIPESTATUS with `lastpipe`, `\|&`, SIGPIPE | known deferred | pipeline 14 | low | large |
| 11 | trap, kill, set, read, nameref details | many small differences | ~100 | low to medium | mixed |
| 12 | Interactive-only: prompt, completion, history, fc, sh-options | not reachable from scripts | ~120 | low | large |

Work order: 1 to 5 first (small, high likelihood, silent wrong output), then 7 and 8, then 6 and 9.

### Status and the discovery pass (agent-style scripts)

Rows 1, 2, 3 and 5 are fixed; row 4 (`${u=z} $u` in one command) is open. The oils corpus is a conformance list, not a
likelihood list, so the next pass ran idiomatic agent scripts (functions, heredocs, arrays, background jobs, redirects,
getopts) under real bash and ps-bash and diffed them. That found bugs the corpus never reached, all fixed with
regression tests:

| Fixed | Was |
|---|---|
| `a[$i]=v`, `a[i+1]=v`, `a[n]=v` past the end, `a[i]+=v` | subscript stored as the literal text `$i`; an index at `Count` threw |
| `arr=($(cmd))`, `arr=($x)` | one element, not word-split |
| `$1 $# $@ $* $? $$` in an unquoted heredoc | printed literally |
| unquoted `${v#p} ${v%p} ${v/a/b} ${v:o} ${v^^}` | one word, not split |
| `kill $!`, `kill %1`, `kill -0 $!`; `wait` after kill | "No such process"; wait returned 0 (bash 143) |
| `$((10#$x))`, `$((16#$h))` | parse error |
| `sed '/re/,$p'`, `sed '/re/,3p'` | "unknown command" |
| `seq -s, / -s' ' / -f FMT` | rejected |
| function defined inside a function | local to the outer function |
| `bash -c 'echo hi'; echo next` | `hinext` (last newline dropped) |
| `f() { sed ...; }; cmd | f` (also `grep`, `read`, `mapfile`, `<<<`) | the pipe never reached the body: empty stdin |
| `"$@"`, `$cmd`, `"${cmd[@]}"` as the command | `false` / `true` / `echo` / `test` / `[` "command not found" |
| `f "${arr[@]}"` | one space-joined argument, not N |
| `${a[@]/o/0}`, `${a[@]%o}`, `${a[@]#?}`, `${a[@]^^}` | operator ignored (whole array printed) |
| `${s#"${s%%[![:space:]]*}"}` (trim idiom), `[!x]` / `[[:class:]]` in strip globs | did not trim; first `]` closed the bracket |
| `${x//$'\n'/,}`, `${x//$nl/,}`, `${x//$empty/r}` | matched the source text; empty pattern matched everywhere |
| `${#@}`, `${#*}`; `$(( ${m[$k]:-0} + 1 ))`, `$(( a[1] ))`, bare `RANDOM` / `SECONDS` in `$(( ))` | parse error or 0 |
| `unset 'a[-1]'`; `kill -9999` | ignored; treated as TERM |
| `read -d`, lone-var IFS trim, `${!#}` / `${!n}`, `${@:$#}`, `${a[@]:$i:2}`, `${a[@]:-w}` | wrong value or error |
| `exit N` in an EXIT trap | trap saw `$?` = 0 |
| `declare -r` / `typeset -r` (ignored on a nameref, as bash does) | variable stayed writable |
| `((a[i]++))`, `$(( a[i] + 1 ))`, a bare name as a subscript (`a[n-1]`) | element ignored or parse error |
| array written inside a function (assign, append, element, `unset 'a[n-1]'`, `mapfile`, `read -a`, `printf -v`) | caller never saw it |
| `[[ -v arr[key] ]]`, `[[ -v arr ]]` | always false |
| `${s//\\/\\\\}`, `${s//\"/\\\"}` (the `json_escape` idiom) | backslash kept literally, so nothing escaped |
| `${PIPESTATUS[*]}` after a plain command; pipeline ending in `cmd >/dev/null` | stale value; NullReferenceException |
| `r=$(false \| cat) \|\| echo caught` | `\|\|` never fired (assignment status not bridged) |
| `${x:?}` inside a pipeline stage or `$( )` | ended the whole script, not just that subshell |
| `declare -p` of a readonly or `declare -x` variable | printed `declare --` |

Still open from that pass (bash is the oracle): `set -x` traces statement-level commands whose words render without running anything (no `$(...)`/arithmetic/glob words, no `++` nesting levels, no `+ y=$((..))`); `declare -p` with no name lists only the environment; `declare -f` prints the emitted PowerShell, not the bash source; `PIPESTATUS` after an `if` whose failed condition ran no branch is `0`, not the condition's status; `$LINENO` / `$BASH_COMMAND` inside a `trap ... ERR` action give line 1 / empty (the action is transpiled apart from the script; commands carry no source line); sparse arrays are padded, not sparse (`${#a[@]}` counts the gap, `unset a[i]` leaves a hole); `local -n r=$1` works only when the script calls the function with literal names (one body variant per name; `f "$x"` falls back to a plain local); `BASH_VERSION` / `BASH_VERSINFO` now claim
bash 5.2 compatibility (like a User-Agent string); the real version is `$PsBashVersion`.
