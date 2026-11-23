# Emitter Strategy Specification

This document describes how `PsEmitter` translates parsed bash AST nodes into
PowerShell text. The emitter lives in `src/PsBash.Transpiler/Parser/PsEmitter.cs`.

---

## 1. The Passthrough Principle

The emitter's sole job for pipe-target commands (grep, head, cut, awk, etc.) is
to **map the bash command name to an `Invoke-Bash*` PowerShell function and
forward every argument verbatim**. The runtime functions defined in
`PsBash.psm1` are responsible for parsing flags and implementing behaviour.

The emitter **MUST NOT**:

- Translate bash flags to PowerShell named parameters (e.g., do not convert
  `-d ","` to `-Delimiter ","`).
- Do partial flag extraction (e.g., do not pull `-n 5` out of `head` args and
  re-emit it as `-n 5` while dropping other args).
- Assume which flags a command supports -- forward all of them and let the
  runtime sort it out.

There are no exceptions -- every mapped command uses the same `EmitPassthrough`
path.

---

## 2. Command Mapping (`TryEmitMappedCommand`)

`TryEmitMappedCommand` is called in two contexts:

1. **Pipe targets** -- every command after the first in a pipeline.
2. **Standalone commands** -- any `Command.Simple` whose command name is in the
   mapping table and is **not** a PowerShell builtin alias (see Section 2.2).

It inspects the command name and dispatches to `EmitPassthrough`. Every mapped
command uses the same `EmitPassthrough` path (every `OrderedArgCommands` member — now including
`echo` and `find` — single-quotes all of its dash words, still via `EmitPassthrough`).

**Source of truth:** the `switch (name)` in `PsEmitter.TryEmitMappedCommand`
(**99 command-name cases**). The table below is the complete list as of this
writing — `validate-specs` check 2 diffs it against the switch, so a new case
belongs here in the same commit.

### 2.1 Mapped Commands (complete, 99 names)

| Bash command | PowerShell function      |
|--------------|--------------------------|
| `grep`       | `Invoke-BashGrep`        |
| `head`       | `Invoke-BashHead`        |
| `tail`       | `Invoke-BashTail`        |
| `wc`         | `Invoke-BashWc`          |
| `sort`       | `Invoke-BashSort`        |
| `uniq`       | `Invoke-BashUniq`        |
| `sed`        | `Invoke-BashSed`         |
| `awk`        | `Invoke-BashAwk`         |
| `cut`        | `Invoke-BashCut`         |
| `xargs`      | `Invoke-BashXargs`       |
| `tr`         | `Invoke-BashTr`          |
| `tee`        | `Invoke-BashTee`         |
| `echo`       | `Invoke-BashEcho`        |
| `printf`     | `Invoke-BashPrintf`      |
| `cat`        | `Invoke-BashCat`         |
| `ls`         | `Invoke-BashLs`          |
| `find`       | `Invoke-BashFind`        |
| `stat`       | `Invoke-BashStat`        |
| `cp`         | `Invoke-BashCp`          |
| `mv`         | `Invoke-BashMv`          |
| `rm`         | `Invoke-BashRm`          |
| `mkdir`      | `Invoke-BashMkdir`       |
| `rmdir`      | `Invoke-BashRmdir`       |
| `touch`      | `Invoke-BashTouch`       |
| `ln`         | `Invoke-BashLn`          |
| `ps`         | `Invoke-BashPs`          |
| `rev`        | `Invoke-BashRev`         |
| `nl`         | `Invoke-BashNl`          |
| `diff`       | `Invoke-BashDiff`        |
| `comm`       | `Invoke-BashComm`        |
| `column`     | `Invoke-BashColumn`      |
| `join`       | `Invoke-BashJoin`        |
| `paste`      | `Invoke-BashPaste`       |
| `jq`         | `Invoke-BashJq`          |
| `date`       | `Invoke-BashDate`        |
| `seq`        | `Invoke-BashSeq`         |
| `expr`       | `Invoke-BashExpr`        |
| `du`         | `Invoke-BashDu`          |
| `tree`       | `Invoke-BashTree`        |
| `env`        | `Invoke-BashEnv`         |
| `basename`   | `Invoke-BashBasename`    |
| `dirname`    | `Invoke-BashDirname`     |
| `pwd`        | `Invoke-BashPwd`         |
| `hostname`   | `Invoke-BashHostname`    |
| `whoami`     | `Invoke-BashWhoami`      |
| `fold`       | `Invoke-BashFold`        |
| `expand`     | `Invoke-BashExpand`      |
| `unexpand`   | `Invoke-BashUnexpand`    |
| `strings`    | `Invoke-BashStrings`     |
| `split`      | `Invoke-BashSplit`       |
| `tac`        | `Invoke-BashTac`         |
| `base64`     | `Invoke-BashBase64`      |
| `od`         | `Invoke-BashOd`          |
| `chmod`      | `Invoke-BashChmod`       |
| `md5sum`     | `Invoke-BashMd5sum`      |
| `sha1sum`    | `Invoke-BashSha1sum`     |
| `sha256sum`  | `Invoke-BashSha256sum`   |
| `file`       | `Invoke-BashFile`        |
| `rg`         | `Invoke-BashRg`          |
| `gzip`       | `Invoke-BashGzip`        |
| `tar`        | `Invoke-BashTar`         |
| `yq`         | `Invoke-BashYq`          |
| `xan`        | `Invoke-BashXan`         |
| `sleep`      | `Invoke-BashSleep`       |
| `time`       | `Invoke-BashTime`        |
| `which`      | `Invoke-BashWhich`       |
| `ping`       | `Invoke-BashPing`        |
| `tracert`    | `Invoke-BashTraceroute`  |
| `traceroute` | `Invoke-BashTraceroute`  |
| `uname`      | `Invoke-BashUname`       |
| `less`       | `Invoke-BashLess`        |
| `more`       | `Invoke-BashMore`        |
| `browse`     | `Invoke-BashBrowse`      |
| `readlink`   | `Invoke-BashReadlink`    |
| `mktemp`     | `Invoke-BashMktemp`      |
| `realpath`   | `Invoke-BashRealpath`    |
| `install`    | `Invoke-BashInstall`     |
| `shuf`       | `Invoke-BashShuf`        |
| `id`         | `Invoke-BashId`          |
| `kill`       | `Invoke-BashKill`        |
| `type`       | `Invoke-BashType`        |
| `command`    | `Invoke-BashCommand`     |
| `test`       | `Invoke-BashTest`        |
| `let`        | `Invoke-BashLet`         |
| `eval`       | `Invoke-BashEval`        |
| `trap`       | `Invoke-BashTrap`        |
| `unset`      | `Invoke-BashUnset`       |
| `shift`      | `Invoke-BashShift`       |
| `shopt`      | `Invoke-BashShopt`       |
| `tput`       | `Invoke-BashTput`        |
| `yes`        | `Invoke-BashYes`         |
| `pushd`      | `Invoke-BashPushd`       |
| `popd`       | `Invoke-BashPopd`        |
| `dirs`       | `Invoke-BashDirs`        |
| `mapfile`    | `Invoke-BashMapfile`     |
| `readarray`  | `Invoke-BashMapfile`     |
| `bash`       | `Invoke-BashBash`        |
| `wait`       | `Invoke-BashWait`        |
| `jobs`       | `Invoke-BashJobs`        |
| `fg`         | `Invoke-BashFg`          |
| `bg`         | `Invoke-BashBg`          |

Commands not in this table are emitted via the general `Emit` path (i.e., they
are not rewritten).

### 2.2 Standalone Mapping

When a mapped command appears as a standalone statement (not a pipe target),
`EmitSimple` also routes it through `TryEmitMappedCommand` -- unless the command
name is in the `PsBuiltinAliases` set. PowerShell has built-in aliases for
common commands (`echo`, `cat`, `ls`, `cd`, `pwd`, `mkdir`, `cp`, `mv`, `rm`,
`sort`, `diff`, `sleep`) that resolve correctly without rewriting. These are
excluded from standalone mapping: the module defines each as an alias of its `Invoke-Bash*`
command, so the word already reaches the runtime. PowerShell's own aliases of those names are gone (2.3).

For all other mapped commands used standalone (e.g., `grep file.txt`, `awk '{print}'
data.csv`), the emitter rewrites them to `Invoke-Bash*` calls so the runtime
function handles flag parsing.

### 2.3 PowerShell's own aliases are not bash commands

A bash runspace holds no PowerShell alias except the runtime's own (definition `Invoke-Bash*`, such as the
`PsBuiltinAliases` above, which the module redefines). PowerShell resolves an alias before a function or a program,
so with `CreateDefault`'s ~135 aliases every unmapped word that named one ran a cmdlet: `write -- $x` ran
`Write-Output` and printed `--` (oils `hay.spec.sh`, Windows fleet s22e), `r` re-ran the previous command,
`iex 'cmd'` evaluated PowerShell, `where` hid `where.exe`. In bash such a word is a function, a program on PATH, or
"command not found" (127).

**Invariant: every `InitialSessionState` a runspace runs bash code in drops the aliases that are not the runtime's
own before the module's go in.** Rule: `PsBash.Core.PowerShellAliases.IsBashRuntimeAlias`. Builders:
`SdkRunspace.CreateBaseSessionState` (the shell), `BashJobRunspaces.InitialState` (in-process jobs),
`New-BashLazyStdin` (an unbounded pipe producer), the last two via `PsBash.Cmdlets.BashSessionState.RemoveForeignAliases`.
Entries are removed by index: the core snap-in's ignore `Remove(name, type)`. The module (`PsBash.psm1`) removes only
the aliases it replaces, because imported into a user's own pwsh it must leave `gci` and `%` alone. ps-bash's own
PowerShell therefore names every command in full. Guards: `Core.Tests/PowerShellAliasGuardTests` (every builder
strips), `Host.Tests/ForeignAliasTests` (the base state holds none; no psm1/ps1 command uses an alias),
`Cmdlets.Tests/BashSessionStateTests`; behavior `Differential.Tests/PowerShellAliasWordTests`.

### 2.4 A script function wins over the runtime's aliases

bash runs a function before a builtin or a program (`true() { echo 'true func'; }; true hi` prints `true func`), but the
runtime's own aliases (`Set-Alias -Name 'true' …` in PsBash.psm1) outrank a PowerShell function. So a call of a
function whose name the runtime aliases names the function explicitly:
`& ((Get-Command -Name 'true' -CommandType Function -ErrorAction SilentlyContinue) ?? 'true') hi` (the alias is the
fallback before the definition runs and after `unset -f`). Rule: `PsEmitter.IsAliasedCommandName` = the psm1's list
(`Parser/ModuleAliases.cs`) or a mapped name. The inline `true` / `:` / `false` forms (`if true` -> `$true`,
`| true` -> Out-Null) stand in for the builtin only: `PsEmitter.IsConstantStatusBuiltin` refuses a name the script
defines as a function. Guards: `Core.Tests/FunctionShadowsModuleAliasGuardTests` (the list equals the psm1's; every
alias loses to a function); behavior `Differential.Tests/CommandLookupDifferentialTests.Function_WinsOverAnAliasedCommand`.

### 2.5 A quoted builtin name is the builtin

**Invariant: quoting a command name (`'local'`, `"eval"`, `\export`, `b\reak`) only escapes alias expansion; the
builtin of that name still runs, exactly as behind `builtin`.** The quote-removed static spelling is the name, read in
one place per layer: `BashParser.EscapedControlBuiltin` for `break`/`continue`/`return`/`exit` (the AST the loop and
function analyses read), and `PsEmitter.UnquoteBuiltinName` (right after `ExpandAliases` in `EmitSimple`) for every
other name in `BashBuiltins.Names`. The bare word it makes is `PsEmitter.NoAliasWord`: alias expansion passes it over,
also where `EmitSimple` re-enters itself for part of the command (bash never expands a quoted word:
`alias eval='echo no'; 'eval' 'echo x'` prints x). A program or function name keeps its quotes (it runs either way).

A declaration builtin (`export`/`local`/`declare`/`typeset`/`readonly`) quoted is the WRAPPER form, not the bare
declaration: its operands are ordinary words (bash 5.2: with x='a b', `'export' v=$x` exports `v=a` and `b`;
`'declare' -a A=(x y)` is a syntax error, status 2). The parser applies the wrapper rules
(`BashParser.QuotedDeclarationStaysACommand`, as `SkipDeclarationWrappers` for `builtin`/`command`): with no
expansion and no array initializer it is the declaration (`'local' v=3`, same result either way); otherwise it stays a
plain command, which `UnquoteBuiltinName` turns into `builtin NAME ...` for `TryEmitDynamicAssignmentBuiltin`.
REGRESSION: every emitter-implemented builtin spelled quoted was `command not found` (127); then the quoted
declaration took the bare path (`[a b]`) and an aliased quoted name ran as a program. Guards:
`Core.Tests/Parser/QuotedBuiltinNameTests`, `ControlBuiltinWrapperTests`; behavior
`Differential.Tests/QuotedBuiltinNameDifferentialTests`.

### 2.6 Bash text run at run time: one call

**Invariant: bash text known only at run time (`eval "$c"`, the command `fc -s` re-runs) runs through ONE emitted
call, `PsBuild.RunBashTextCall`, made by `PsEmitter.RunTimeBashTextCall`.** It brings the nesting bound (5; `history -s
'fc -s'; fc -s` fails instead of recursing), the call site's loop mode (a `break` in the text leaves the loop), the
calling function's locals, and the return check (a `return` in the text leaves the function). The runner
`PsBuild.RunBashTextAtRunTime` is private; its only other caller, `BuiltinAsSourceScript`, runs a static builtin command
whose own emitted form makes the call. `fc` is `PsBuild.FcTakeRerun` (the cmdlet leaves its choice in
`$global:__BashFcRerun`) followed by that call. With a redirect (`eval "$c" 2>/dev/null`, `fc -s >f`) the text sits in the redirect's block, which passes a return on (§2.7). REGRESSION: fc's own copy of eval's call site missed all four
(`history -s 'return 7'; f() { fc -s; echo notreached; }` printed notreached), and a redirected `eval 'return 7' 2>/dev/null` in a function never returned. Guard:
`Core.Tests/RunBashTextCallGuardTests`; behavior `Differential.Tests/FcRerunDifferentialTests`.

### 2.7 A redirect's script block passes a return on

**Invariant: a body put in a PowerShell script block for its redirects (`{ ...; } 2>x`, `while ... done <f`, `if ... fi >x`,
`case ... esac 2>x`, `{ ...; } 3>x` / `{fd}>x`, `eval '...' <<< x`, `return 6 </dev/null`, `eval "$c" <f`) or as errexit's
final and-or operand (`[[ 1 ]] && { return 6; }` under `set -e` / an ERR trap) is built by ONE primitive, `PsEmitter.RedirectGroup`.** A
PowerShell `return` leaves only the block it is in, so when the body emitted a return that leaves a function, eval text or
a subshell (`PsEmitter.LeavingReturn` counts each; `EmitCommand` marks where a command began), the block is
`PsBuild.GroupPassingReturn`: `$global:__PsBashGroupReturn` is cleared before it, raised when the body starts and
cleared at its end (`PsBuild.GroupBody`), and still raised after it only when a return skipped the end, which then
returns again (a redirect that fails before the body runs leaves it clear: `{ return 6; } >/nonexistent/x` goes on). A body
with no return keeps the plain block. A function or subshell definition keeps its own returns (they leave it, not the
block around it). Not passed on, as in bash 5.2: break/continue (PowerShell's labelled break leaves blocks by itself), a
function call with a redirect, and `source f 2>x` (its return ends the file). REGRESSION: `f() { { return 6; } 2>/dev/null;
echo no; }` printed no and returned 0 (bash: 6), and the same for every compound command, `( { exit 3; } 2>x; echo no )`
and `set -e` in a subshell; under `set -e` or an ERR trap `[[ 1 ]] && { return 6; }` printed no, and `return 6 <f` /
`eval 'return 7' <f` failed the parse of the whole script. Guard: `Core.Tests/RedirectGroupGuardTests` (source inventory + emitted shapes); behavior
`Differential.Tests/RedirectGroupReturnDifferentialTests`.

**Conditions and errexit wrappers keep a return at statement position.** A condition that runs a command (`if return 6;
then`, `while ! return 6`, `until return 6`, `if [[ 1 ]] && return 6`) is hoisted to a statement before the `if`/loop
test (`PsEmitter.EmitHoistedCondition`); only a test that runs no command stays in expression position
(`PsEmitter.EmitExpressionCondition`, which refuses any other, so `PsBuild.ExitCodeTest`'s `(& { })` never holds a
command). Every errexit / ERR-trap wrapper (`PsBuild.ErrexitSuppressed`, `ErrexitSuppressedIfOn` for `!`,
`ErrexitAndOrList` for `&&`/`||` left operands, `ErrexitLifted`, `ErrexitSuppressedFrom`) is a try/finally, which a
return passes through; the one that is a block (`ErrexitFinalOperand`) goes through `RedirectGroup`. Guard:
`RedirectGroupGuardTests.ErrexitWrappersPassAReturnOrAreBlockMakers` (a PsBuild errexit wrapper that makes a block must
be a listed block maker) and the condition shapes in `Transpile_ReturnInAnErrexitCondition_StaysAStatement`.

**A redirected compound's failed open is checked after the statement.** Under errexit / an ERR trap, a compound
command (brace group, if, while/until, for, for-arith, case) whose redirect opens a file, stdin, stdout or stderr
(`PsEmitter.OpensFile`), is followed by `PsBuild.RedirectFailureChecked` (`PsEmitter.EmitRedirectedCompound`). So is a
builtin or assignment whose own status errexit ignores (`: > /nodir/x`, `true < missing`, `local v=1 2>/nodir/e`); other
simple commands are checked on their status. The open failure raises `PsBuild.RedirectFailedVariable`:
Invoke-BashRedirect for an output file, the failure branch of `PsBuild.InputRedirectGuard` /
`SimpleInputRedirectGuard` for `< file`. Invoke-BashRedirect does not decide errexit itself, so this check is the only
errexit decision for a failed open. The exit/return is never inside the guard's `& { }` block, where it would leave only the
block in a function or subshell. A nested failure the body handled (`{ { cat; } < missing || :; } < in`) is cleared by
the outer command's own guard or Invoke-BashRedirect. REGRESSION: the input guard lost its errexit exit and nothing
checked after the compound, so `set -e; { { cat; } < missing; } 2>/dev/null; echo no` printed no. Guard:
`RedirectGroupGuardTests.Transpile_RedirectedCompoundUnderErrexit_IsFollowedByTheOpenFailureCheck`; behavior
`Differential.Tests/ErrexitInputRedirectTests`.

**errexit's final and-or operand streams.** Each and-or operand marks its own start (as `EmitCommand` does for a
command), so only a return in THAT operand makes it pass one on. The operand stays the dot-sourced block
(`ErrexitFinalOperand`) with `GroupBody` inside, and the `GroupPassingReturn` check wraps the whole errexit list
(`EmitAndOrStatement`): the final operand is the last thing the list runs, so the check after it is the operand's. It is
never put in `$( )`, which would hold back every line of `set -e; cond && npm start` until it ended. REGRESSION: the operand
compared against the enclosing body's mark, so any earlier `x || return 1` made every later operand "pass", in `$( )`.
Guard: `RedirectGroupGuardTests.Transpile_ErrexitFinalOperand*`; behavior
`RedirectGroupReturnDifferentialTests.ErrexitFinalOperand_AfterAnEarlierReturn_RunsAndStillPassesAReturn`.

**The emitter's thread state is per unit and per scope.** The emitter's `[ThreadStatic]` fields go back to their first
values at the start of every top-level `Transpile` (`PsEmitter.ResetUnitState`; a nested one, for static eval text, keeps
them, and shares the caller's attribute and nameref sets so what the text declares holds after it: `_inlineEvalText`;
in a function that text parses as the body does, `BashParser.Parse(text, inFunctionBody: true)`), except the caller's inputs to it (`InheritedPipefail`, `EvalLocalNames`, ...). Every nested emission scope (`$( )`,
`<( )`/`>( )`, a pipe stage, a `&` job, a subshell, a function body) saves and restores the scoped fields through ONE
struct, `PsEmitter.EmitterScope` (`Enter()` ... `Restore()` in a finally), never ad-hoc locals. Name sequences that must
stay distinct across units nesting at run time (`__psbash_evin_prev`, `__psbash_pso`) carry a salt from the unit's text,
so a reset counter cannot repeat a name an outer unit uses. REGRESSION: `_leavingReturns` was never reset and not
restored after `$( )`, a pipe stage or a job, so the same script emitted different PowerShell depending on what was
transpiled before it on the thread. Guards: `Core.Tests/EmitterStateGuardTests` (every `[ThreadStatic]` field of PsEmitter
and PsBuild is reset by `ResetUnitState` or exempt with a reason), `Spec.Tests/TranspileDeterminismTests` (every spec case
and eval fixture transpiles to the same text after any other unit).

---

## 3. `EmitPassthrough`

```
EmitPassthrough(cmdlet, args) -> "cmdlet arg1 arg2 ..."
```

1. If `args` is empty, return just the cmdlet name.
2. Otherwise, iterate args, call `EmitWord` on each, and space-join them after
   the cmdlet name.
3. If an emitted arg triggers `NeedsPassthroughQuoting`, wrap it in double
   quotes.

**`NeedsPassthroughQuoting`** returns `true` when an argument:

- Starts with `-` (is a flag), AND
- Contains a comma (`,`) -- PowerShell array separator -- OR
- Contains braces (`{` or `}`) -- PowerShell scriptblock delimiters.
- Already-quoted arguments (starting with `"` or `'`) are skipped.

**Bare comma literal (any word, not just flags).** In PowerShell argument mode an
unquoted `a,b` is an ARRAY. bash treats `,` as an ordinary character, so
`sed -n 725,750p f` reached `Invoke-BashSed` as `@(725,'750p')` ("Cannot convert
'System.Object[]' to the type 'System.String' required by parameter 'Arguments'").
`QuoteCommaLiteralWord` (applied in `EmitPassthrough`, the general external-command
path, the RC-7 splat path, and redirect targets via `EmitArgWord`) therefore quotes
any argument word whose `WordPart.Literal` parts contain `,`:

- pure-literal word → one single-quoted string (`'725,750p'`; `/tmp/` and `/c/…`
  path transforms applied first);
- word mixing the comma literal with variables / quoted parts (`$x,y`, `a,"b"`)
  → flattened to ONE double-quoted string (`"$env:x,y"`);
- flag-shaped words keep the historical `"-F,"` double-quote wrap above;
- a glob word (`ls *.c,x`, `cat f*,g`) is expanded by the shell before the command runs (see
  "Pathname expansion" in §4): the pattern is ONE single-quoted string (`'*.c,x'`), so the comma stays
  literal as in bash and never becomes a PowerShell array;
- words with a process-sub or brace part are left alone (`{a,b}` is a `BracedTuple`,
  never a comma literal);
- already-quoted words (`"a,b"`, `'a,b'`) and backslash-escaped commas are untouched.

Example: `awk -F,` emits as `Invoke-BashAwk '-F,'` (awk is on `OrderedArgCommands`, so every
flag is single-quoted — this is also what lets a repeated `-v a=1 -v b=2` reach the cmdlet
instead of tripping the binder's "parameter 'V' is specified more than once").
Outside that set, a comma flag keeps the historical double-quote wrap (`"-F,"`) to prevent
PowerShell from splitting on the comma. `xargs -I{}` emits as `Invoke-BashXargs '-I{}'` (xargs is on the ordered-arg path, below).

`xargs`, `time`, `env` (and the `-exec`/`-execdir`/`-ok` argv of `find`) run a FOREIGN command
line, so the flags of the command they run are as dangerous as their own: an inner `-a`
(`xargs -0 basename -a`) prefix-matched `Invoke-BashXargs`'s `-Arguments` and died with
"Missing an argument for parameter 'Arguments'"; `time echo -e` / `find -exec grep -i` hit the
ambiguous `-Error*` / `-Information*` common parameters; `env X=1 grep -i` had its `-i` taken
as env's own decoy switch. `xargs`, `time` and `env` are therefore on
`PsEmitter.OrderedArgCommands` (every dash-leading literal is single-quoted, so it reaches
`Arguments` verbatim and in order — see runtime-functions.md "Shared argument parser"), and so
is `find`: its expression language (`-o`/`-a`, `!`, `\( \)`, the argv between `-exec` and its `;` / `+`)
arrives whole and in order, which replaced the per-flag `FindForceQuoteFlags` set (only `-o`/`-a`)
and the `FindExecArgvIndices` special case for the `-exec` argv. `find` is not getopt, so its
cmdlet scans `[-H|-L|-P] [-D opts] [-Olevel] [PATH...] [EXPRESSION]` itself.
`grep`, `sed` and `rg` joined the set with the search/edit batch: their repeated `-e`, `-ve`/`-ie`
bundles, `-ftemplate.txt`, `-i.bak`, `-A2`, `-NUM` and `--` all reach the cmdlet as written (the
psm1 `Invoke-BashGrep`/`Invoke-BashSed` proxies remain only for direct PowerShell calls and forward
literal strings). The cmdlets' scans stop parsing their own options at the first operand (the command name);
everything after is the inner argv. Ordered-arg commands also single-quote a bare literal
containing a comma (`xargs -d , echo`): unquoted it is a PowerShell array / parse error.
`command` is on the set too: its cmdlet reads `-p -v -V` only up to the first operand and runs the
rest as the inner command's argv.
`bash` is on the same set: the script's own args (`bash s.sh -v -e -c x` → `$1=-v $2=-e $3=-c $4=x`)
and the args after `-c CMD NAME` are positional in bash, so they arrive single-quoted; the
cmdlet recognises `--help`/`--version` only in bash's option zone (before the script /
command string; `InvokeBashBashCommand.OptionZoneHas`) and forwards the rest verbatim to a
child ps-bash, whose launcher (`ShellArgs.Parse`) ends option parsing at the script path.
`echo`, `printf`, `test` and `ls` are on the set as well (batch 5): the three builtins keep bash's OWN option
rules in their scanners (echo: leading `-[neE]+` run only; printf: `-v VAR` / `--`; test: no options at all), and
the emitter no longer special-cases echo (the old `-e`/`-E`/`--` force-quote set is gone — `find`'s infix
`-o`/`-a` is the only force-quote left).

---

### 3.1 Operands PowerShell's binder rewrites

A bash function, a `#!` file's stand-in and a background job's program stand-in are PowerShell code, so their
arguments pass through the binder: a bare `--` is consumed, `--%` stops parsing, `-x:y` arrives as `-x:` plus `y`, and
a splatted `$args` re-binds `-e` to the callee's `-ErrorAction`. Two rules close the class:

- **Emitter.** For every unmapped command, a dash word that `PsEmitter.BinderRewritesOperand` names (`--`, `--%…`, a
  dash word containing `:`) is single-quoted. A native program gets the same text either way. A dynamic command name,
  a merged runner and a script file run in place (`./x.sh` → `bash ./x.sh`) quote every dash word. A Verb-Noun
  PowerShell command (`PsEmitter.IsPowerShellCommandName`: `Get-Item -Path:x`) keeps its parameter syntax.
- **Stand-ins.** PowerShell that receives a bash command's `$args` passes them on as literal strings,
  `PsBuild.ArgvLiterals` then `PsBuild.ArgvSplat` (`$__psbash_argv = [string[]]$args … @__psbash_argv`), never `@args`.

Guards: `Core.Tests/StandInArgvGuardTests` (no `@args` in product code; the autoload wrapper is exempt, because it
runs a PowerShell cmdlet), `Core.Tests/Parser/PsEmitterDashOperandTests`. Against bash:
`BashParityGapDifferentialTests.Function_ReceivesDashOperandsAsTyped`, `ScriptFileExecutionDifferentialTests`.

## 4. `EmitSimple` -- The Main Command Emitter

`EmitSimple` handles `Command.Simple` nodes (single commands that are not
pipelines or control flow). It processes several special forms before falling
through to general word emission.

### HereDoc

If the command has a heredoc attached:

- **Expanding** (`<<EOF`): emits `@"\n{body}\n"@ | cmd` with bash variable
  references (`$VAR`, `${VAR}`) translated to `$env:VAR` via
  `TranslateHereDocVars`.
- **Literal** (`<<'EOF'`): emits `@'\n{body}\n'@ | cmd` with no variable
  translation.

The heredoc body is piped into the command. The command is that pipe's stage (as is a `< file` command, after
`Invoke-BashCat f`), so it is emitted under `WithPipeStageFeed`: any script block its emission wraps it in (the run-time
`exec` branch of `$SH >out <<EOF`, a splat hoist) forwards `$input` to the command inside, or the command reads nothing
(`g=-; cat $g <<EOF` or `< in`, the oils xtrace spec `$SH >stdout 2>stderr <<'EOF'`; Differential
`RunTimeCommandNameTests.HereDocument_ReachesACommandInsideAWrapper`; guard `Core.Tests/Parser/StdinFeedWrapperGuardTests`).

### Here-string (`<<<`)

Not handled as a separate case in `EmitSimple` -- here-strings are parsed as
heredocs with the word as the body, so they follow the same heredoc path.

On a **compound** command (`while …; done <<< "$x"`, `for`, `if`, `case`,
`{ …; }`, a subshell) the here-string is parsed by `ParseTrailingRedirects` into a
`Redirect` whose `Op` is `"<<<"` and whose `Here` field carries the already-built
`HereDoc`. `ApplyCompoundRedirects` / `EmitSubshell` then treat it as the group's
stdin, exactly like `<`:

```
@"\n{body}\n"@ | Emit-BashLine | & { <emitted compound> }
```

Both the simple-command and compound paths build the literal through the single
`EmitHereDocLiteral` helper, so variable translation and the
here-string-terminator collision fallback cannot drift apart. (`<<-` tab
stripping is not an emitter concern: the parser strips the tabs before it builds
the `HereDoc`, so `Body` already holds exactly what bash stores.)

> Before this, `<<<` was not a compound-redirect operator at all: the operator and
> its word were left unconsumed, the here-string was silently dropped (the loop read
> nothing), and in some surroundings the stray tokens emitted an empty PowerShell pipe
> element that broke the parse of the whole file.

### Input redirect (`< file`)

`< file` on a command becomes `Get-Content file | cmd`. The redirect is removed
from the command's redirect list, and the rest of the command is re-emitted via
recursive `EmitSimple`.

### `declare`

- `declare -A name` -> `$name = @{}` (empty hashtable)
- `declare -a name` -> `$name = @()` (empty array)
- `declare -i name` -> `[int]$name = 0`

An ARRAY INITIALIZER (`declare -a A=(x y)`, `readonly A=(x y)`) never reaches this
path: the parser turns it into a `ShAssignment` so the elements survive. See
`parser-grammar.md` §3.3. Trade-off: `readonly` then emits a plain assignment, so
const-ness is lost where the value was previously lost entirely.

### `for x in LIST`

Every bare literal in the list is quoted, including the **single-item** case (`for x in one` ->
`foreach ($x in 'one')`). Emitting the bare word made PowerShell invoke it as a command ("one: command not
found"); only the multi-item path used to quote, which is what hid the bug. A STATIC word (literals, `\x`
escapes, `'…'`, `"…"` without expansions) is emitted as its VALUE through `PsBuild.SingleQuote`
(`ForListItem` → `TryGetStaticArgValue`), never by re-quoting `EmitWord`'s command-mode text: `a\ b`
renders as the bareword ``a` b``, and wrapping that in `'…'` kept the backtick (`for c in grep\ x` ran
``grep` x``), and a word PowerShell reads as a number lost its spelling (`for i in 007` gave `7`). Other
words (`$x`, `"a$y"`, `(…)`, `@(…)`) keep their emitted PowerShell value.

An UNQUOTED `$(cmd)` / `` `cmd` `` word is word-split like an unquoted `$x`: bash expands it to TEXT,
splits that text on `$IFS`, then glob-expands each word. The capture array only splits on LINES, so
`for f in $(echo a b c)` used to iterate once over `a b c`. Such a word emits
`@(ConvertTo-BashWords <the substitution's text>)` (`IsPureUnquotedCommandSubWord`; a quoted
`"$(cmd)"` or a substitution glued into a longer word `x$(cmd)y` stays one word). The cmdlet
(`ConvertToBashWordsCommand`, splitter = `BashWordSplitter`) follows bash's IFS rules (unset IFS =
space/tab/newline; IFS whitespace folds and trims; every non-whitespace IFS character delimits and keeps
empty fields, a single trailing delimiter adds none; empty IFS splits nothing), yields NO word for an
empty / blank result (zero iterations), and expands each word containing `* ? [` against the filesystem
(nullglob off: no match keeps the literal word; matches of a relative pattern are printed relative).
A list with any split word (`$a`, `$(cmd)`, a glob word) is emitted as ONE flat `@( item; item; … )`:
PowerShell's comma operator does not splice (`'a',@('b','c')` is two elements), which is why
`for w in $a z` ran `x y` and `z` as two items. Whether a list word is a glob is decided from its PARTS
(`IsGlobWord`: an unquoted `GlobPart`), never from its emitted text — the `-replace '(\r?\n)+$'` inside a
quoted capture used to make `for f in "$(cmd)"` look like a glob. An unquoted `$x` splits on `$IFS` and
its words are pathname-expanded (`ConvertTo-BashWords`, below).

### Pathname expansion (`ConvertTo-BashGlob`)

bash's expansion order ends `… word splitting -> pathname expansion -> quote removal`, and the SHELL does
it: `echo *` prints file names because `echo` is handed names. The emitter used to pass a glob word through
as a literal and rely on each mapped cmdlet to expand it (some did, with their own dialect: hidden files
included, ABSOLUTE paths, `[set]` unsupported by some), while `echo`, `printf`, user functions, native
programs, `for` lists and array assignments never expanded at all — `echo *` printed `*`, `for f in *`
iterated over absolute Windows paths including `.hid`.

**Decision: every consumer gets names, expanded at the emitter step.** A glob word (`IsGlobWord`: a word
with an unquoted `*`, `?` or `[set]` whose other parts are literals, quotes, expansions or a tilde; extglob,
process substitution and quoted-brace mixes excluded) becomes `@(ConvertTo-BashGlob <pattern>)`:

```powershell
echo *.txt    ->  & { $__bashsplat0 = @(ConvertTo-BashGlob '*.txt'); Invoke-BashEcho @__bashsplat0 }
for f in *    ->  foreach ($f in @(ConvertTo-BashGlob '*')) { … }
arr=(a *.txt) ->  $arr = @("a"; @(ConvertTo-BashGlob '*.txt'))
set -- *.txt  ->  $global:BashPositional = @(@(ConvertTo-BashGlob '*.txt'))
echo sub/{a,b}* -> … @(@('sub/a*','sub/b*') | ConvertTo-BashGlob) …   (braces first, then glob)
```

It rides the RC-7 splat hoist (`IsUnquotedSplitWord` / `EmitCommandWithSplatArgs`, and the flat-list forms
for `for`, arrays and `set --`), so an empty result (nullglob) is zero arguments and a pipe-stage input is
forwarded (`$input | cmd @__bashsplat0`). The pattern text (`EmitGlobPatternExpr`) is bash's own dialect:
glob parts verbatim, every quoted or escaped `* ? [ \` backslash-escaped (`"*"`, `\*`, `'a?'`), quoted
expansions escaped at run time through `[PsBash.Cmdlets.BashGlobText]::Escape`, unquoted `$x` / `$(cmd)`
spliced in active, `/tmp/` and a drive prefix mapped like any path word (forward slashes), and the `/` the
parser eats after `~` re-added. A word with no unquoted glob part is unchanged (`echo "*"`, `echo \*`).

The engine (`Cmdlets/BashGlob.cs`, `ConvertTo-BashGlob`, fnmatch core shared with `ls -I` as `LsGlob`):
`/`-separated components matched directory by directory (`*` `?` `[set]` `[!set]` `[^set]` ranges
`[[:class:]]`); a name that starts with `.` is matched only by a component with a literal leading `.`
(or `shopt -s dotglob`); `.` and `..` are never matched by a glob (bash 5.2 `globskipdots`); matches are
sorted ordinally per directory, depth first; a trailing `/` keeps only directories and the slash; results
keep the spelling of the pattern (relative stays relative, `sub/*` -> `sub/a`); no match keeps the literal
word (escapes removed) unless `nullglob` (no word) or `failglob` (`bash: no match: PAT`, status 1, the
command is aborted). `shopt` options live per RUNSPACE (`InvokeBashShoptCommand.IsEnabled`), not per
process, so `shopt -s nullglob` in one `-c` command cannot leak into the next on a pooled host.

**Why not "expand only for the consumers that do not glob themselves":** the cmdlets that do (`cat`, `ls`,
`grep`, `cp`, …) expanded differently — `ls *` listed hidden files, `grep -l y *` printed absolute paths —
so two expansions with two dialects lived side by side and the two sets (`cat` globs only `*`/`?`, `ls`
also `[`) drifted. Expanding once, in the shell, and handing every command plain names makes the result
identical for all of them and the decision invisible to the cmdlet. They keep their own globbing for
DIRECT PowerShell calls (`Invoke-BashCat *.txt`) only: a runspace that runs transpiled bash carries
`$global:__BashOperandsExpanded` (set by the host, `SdkRunspace`, and the background-job wrapper), and
there `FileSystemHelpers.ResolveOperandPaths` / `ResolveOperands` / `IsCmdletGlobOperand` take every operand
literally — `head 'two-*'` is "cannot open 'two-*'", never `two-bar`. On a direct call
`ResolveOperandPaths` still prefers a name that literally exists (`a[1]` next to `a1`).

Words that are NOT expanded here: assignment values (`x=*`), `case` patterns, `[[ ]]` operands, redirect
targets, here-strings. An unquoted `$x` / `$(cmd)` result is split and expanded by
`ConvertTo-BashWords` (below), which uses the same engine.

### `read`

- `read [-r] [-p "prompt"] VAR` -> `Invoke-BashRead [-p "prompt"] VAR` (the
  runtime cmdlet sets `$VAR` in the caller's scope via `Set-Variable -Scope 1`).
- **`while read a b …` binds EVERY name**, not just the last. The line is trimmed and
  split with a count limit, whose last field reproduces bash's rule that the LAST
  variable absorbs the remainder; names past the end of the field list get `''`.
  The splitter is built from the command's `IFS=` prefix: `null` or an all-whitespace
  IFS folds runs of whitespace (`-split '\s+', N`); a custom IFS becomes the same
  delimiter regex the runtime read cmdlet builds (`InvokeBashReadCommand.SplitByIfsForRead`),
  so `while IFS=: read -r a b` splits on `:` and the fast path cannot drift from the
  `read` builtin. The field array must be built with `@( … )` — a `$( … )`
  subexpression collapses a one-element split to a scalar string, and `$str[0]` is
  then its first *character*. The single-variable form keeps the direct
  `${VAR} = $_` binding.
- A bare `IFS=, read -r a b c` (not a loop) is a normal simple command; the
  `IFS=` env pair wraps it, and the runtime cmdlet reads `$IFS` and applies
  `SplitByIfsForRead` for the multi-variable remainder rule.
- A **compound used as a pipe target** (`echo hi | { read y; …; }`) opens a *stdin scope*
  — see "Compound-command stdin" below — so the body runs ONCE with the piped records
  available to `read` AND to every other stdin-reading command (bash's fd-0 semantics).
  `Invoke-BashRead` takes one LINE from `$global:__BashStdIn` when it has no pipeline input.
  A per-line `ForEach-Object` envelope would run a multi-command body once per line,
  which is not bash.
- Flags `-r`, `-p PROMPT`, and `-a ARR` are recognized by the runtime; other
  flags are currently ignored.
- Under an interactive PTY, `Invoke-BashRead` reads from
  `[Console]::In.ReadLine()` so it blocks on the PTY slave fd. `Read-Host` is
  not usable in the interactive PTY host runspace because
  `ExitTrackingHost.ReadLine()` throws `NotSupportedException`.

### Compound-command stdin (shared cursor)

A compound (`{ …; }`, `( … )`, `if`, `for`, `while`, `case`) has ONE stdin that every command
inside shares, advancing one cursor: `printf 'b\na\n' | { sort; }`, `{ read x; sort; } < f`,
`cat f | if true; then sort; fi`, `{ cat; } <<< hi`, `( cat ) <<EOF`. PowerShell's `& { … }`
passes its pipeline to `$input` only, so the emitter builds the sharing itself
(`PsBuild.StdinScope` / `PsBuild.StdinFeed`, `PsEmitter._inStdinScope`):

- **Scope.** A compound opens a stdin scope when it is a pipe stage after the first
  (`EmitCompoundStage`) or has an input redirect `< file` / `<<<` / `<<` (`EmitCompoundBody`,
  `ApplyCompoundRedirects`, `EmitSubshell`). The scope wrapper saves the previous
  `$global:__BashStdIn`, fills a fresh `Queue[object]` from `$input` (the ORIGINAL records:
  typed objects and unterminated `printf` records survive), runs the body in `try`, and restores
  the previous queue in `finally`, so a nested compound stage never clobbers its parent's stdin.
  `< /dev/null` is a scope fed nothing. The queue is filled eagerly (a PowerShell script block
  starts only after its upstream finished), so a general infinite producer does not terminate. The
  exception is a literal `yes ...` / `seq ...` piped into a compound (`LazyProducerText`): the scope
  (`PsBuild.StdinScopeLazy`) runs the producer on a background runspace behind a `StdinCursor` (lazy pull +
  pushback for `read`) via `New-BashLazyStdin`, and its `finally` closes the cursor, which stops the producer —
  `yes | { head -n1; }` and `yes | { read a; read b; }` terminate. The launcher's own stdin uses the same
  `StdinCursor` over forwarded frames (runtime-functions.md "Deliberately left"). Arbitrary producers
  (`cmd | { head -n1; }`) stay eager.
- **Readers.** Inside a scope `EmitSimpleFed` prefixes a stdin-reading simple command with the lazy
  feed `& { while ($q.TryDequeue([ref]$r)) { $r } } | cmd`; `read` and the feed advance the same
  cursor, so `{ read x; sort; }` sorts the REST. **Every take from a shared stdin is ONE atomic
  `TryDequeue`** (`StdinCursor.TryDequeue`, `SharedStdin.TryTake`): a background job reads the same cursor
  (`BackgroundJobStdin`), and `Count` then `Dequeue` let it take the record in between, so `Dequeue` threw
  at end of input (guards `SharedStdinTakeGuardTests`, `SharedStdinTakeTests`). Which commands read stdin is a syntactic estimate
  (`StdinReaders.ShouldFeed`, the emitter never parses flags): `tr xargs tee mapfile` always;
  `cat head tail wc sort uniq cut nl rev tac fold paste join comm base64 md5sum … less more` when
  there is no file operand (value-taking flags like `head -n 2`, `sort -k 2` are skipped, a lone
  `-` always feeds); `grep sed awk rg jq yq` when the only positional is the pattern/program (or
  `-e`/`-f` supplied it); builtins (`echo printf cd test …`) and file mutators never. A command
  that is not a ps-bash command nor a builtin is wrapped in `PsBuild.NativeStdinScope`:
  `Enter-BashNativeStdin NAME` — when NAME resolves to an application (not a function/alias) — makes an
  OS pipe the PROCESS stdin and pumps the shared stdin into it (`NativeStdinBridge`);
  `Exit-BashNativeStdin` restores it, cancels the pump and puts the unread bytes back in front. The
  program starts at once and the command ends when the program does. (The old `if (queue.Count -gt 0)
  { feed | prog }` form blocked before the program started, so under an open, silent launcher stdin —
  an agent's shell tool — every native hung.) Commands with an env prefix, their own heredoc /
  `< file`, or a statement-list / RC-7 splat emission are left alone (a pipe / heredoc / `< file`
  feeding a native outside any scope gets `ConvertTo-BashNativeStdin`, see "Pipeline emission"). `cmd < /dev/null` runs inside
  `PsBuild.EmptyStdinScope` (an empty queue, dot-sourced) so every reader in it — natives included —
  sees end of input; the redirect used to be dropped, which meant the LIVE launcher stdin.
- **Redirects on statement-list builtins.** `cd`, `eval`, `source`/`.`, `read`, `readonly`, `set` emit
  statements, not one pipeable command; their redirect tail is applied to a dot-sourced block
  (`PsBuild.WithRedirectTail`: `. { … } 2>$null`) so side effects stay in the caller's scope.
  **INVARIANT: a command's redirect tail is written ONCE, by the caller of the form that emits it, and
  grouped only around a statement list** (`PsEmitter.WithRedirectTailOn`). A single `Invoke-Bash*`
  command (`read`) takes the tail bare: `cat f | . { Invoke-BashRead w } 2>$null` read nothing,
  because the group does not hand its pipe to the command inside. A wrapper that re-emits its command
  (`builtin`) passes the command on WITHOUT its tail (every caller of `TryEmitMappedCommand` appends it).
  Written twice (`. { … } 2>$null 2>$null`), PowerShell refuses the WHOLE script. A form that only
  reports (top-level `return`, `exit 1 2`) takes its redirects too. A pipe stage that is an expression
  (`$( … )`) is wrapped `& { }`, because PowerShell allows an expression only as a first element.
  A state builtin behind `builtin`/`command` (`echo x | builtin read v`) is still a subshell stage
  (`WrappedCommandName`). Guards `Host.Tests/BuiltinRedirectParseabilityTests` (every builtin x
  wrapper x position x redirect), `Differential.Tests/BuiltinWrapperRedirectDifferentialTests`.
- **Pipelines inside a scope.** Only the FIRST stage reads the scope; later stages read the pipe
  (`{ grep a | sort; }`). A fused-lane pipeline is not formed inside a scope (the compiled cores
  know no feed).
- **`while read`.** The loop reads the shared stdin ONE LINE per pass
  (`PsBuild.SharedStdinNextLine` = `SharedStdinLine.Next`, the cursor `read` advances too), so
  `while read a; do read b; …; done`, a `cat` / `head -1` / nested read loop in the body take the
  lines after the loop's, as in bash. An unterminated record is joined with the next one
  (`{ printf a; echo b; }` reads `ab`). Two forms keep iterating records instead, because nothing
  else reads them: a pipe stage whose body is PROVEN to read nothing streams per record (next
  section), and a loop whose only redirect is its own `< f` / `<<<` / heredoc with such a body
  iterates that source inline. The proof is the stdin probe (next section, rule 4). Only the plain
  line loop is this form: a `read` flag that takes a value (`-u FD`, `-n N`, `-N N`, `-t SECS`,
  `-d DELIM`, `-a ARR`, `-p`, `-i`) makes it a generic `while` whose condition runs `Invoke-BashRead`
  (`IsWhileRead`; the flag's value used to be taken for a variable name, so `while read -u 3 a; …;
  done 3< f` read stdin into `$3`).

### A pipe stage is a subshell

bash runs every stage of a multi-stage pipeline in a subshell, the last one included unless `lastpipe` is set, so
nothing a stage does reaches the caller. ps-bash runs stages in-process, so a stage that can change shell state gets
the subshell guard. Two kinds of stage get it:

- a compound stage (`EmitCompoundStage`);
- a simple stage whose command is in `SubshellStageBuiltins` (also behind `builtin` / `command`): `.`, `source`, `eval`,
  `cd`, `set`, `shift`, `export`, `declare`, `unset`, `umask`, `readonly`, `mapfile`, `trap`, `alias`, and so on. It
  is rewritten to a `Command.Subshell` with `KeepsLoopLevel`.

Loop control in a stage ends only that stage, never the caller's loop. A compound stage starts with no loop (`break`
there warns, status 0). A simple stage keeps the loop level, so `echo q | break` in a loop ends the stage silently.
`exec CMD` in a stage or `( )` reads the stdin it inherits (`EmitSimpleFed`) unless it has its own here-document or
`<` file. A here-string's word is one expanded, quote-removed word (`HereDoc.Word`, `PsBuild.HereStringValue`) plus
a newline. It is never re-scanned from raw text, globbed or brace-expanded. Oracle:
`ShellLanguageGapDifferentialTests.PipeStage_*`, `Subshell_*`, `Exec_*`, `HereString_*`.

### Stage stdin (one model for every pipe stage)

bash connects a pipe to the WHOLE next stage's fd 0. In PowerShell the records reach only `$input`
of the stage's OWN script block: any block nested inside it — a redirect's dot-sourced `. { }`,
`ShellStateGuard`, a loop, a function call, a `$( )` — sees an empty `$input`. Every bug of this
family came from a wrapper sitting between the stage block and the reader: (a) a `;` in a quoted
argument made a stage a statement list wrapped in `& { }` that dropped the input (43cf8c88);
(b) a here-doc / here-string on a stage was dropped; (c) a `while read` stage collected all its
input before running (cdacbec1); (d) `| while read …; done > f` put the loop inside the redirect's
`. { }`, so it saw an empty `$input`; (e) a `read` in a read-loop body saw no stdin because the
loop iterated `$input` itself and opened no scope.

The rule: **the stage's own block hands its input on immediately, through one of three `PsBuild`
primitives, and every reader consumes the shared cursor — no reader names `$input`.**

1. `StdinScope` / `StdinScopeLazy` — a compound stage after the first (`EmitCompoundStage` →
   `StageStdin`, for EVERY compound form: `{ }`, `( )`, loops, `if`, `case`, `select`, `[[ ]]`,
   `(( ))`, a function definition) turns the input into `$global:__BashStdIn` first; wrappers
   (redirect tail, subshell guard, pipefail `Invoke-BashPipeStage`) go inside or outside it, never
   between. The one exception is a `while read` stage whose body is proven to read nothing (rule 4): it runs per record
   behind `PipeGate` (`StreamingReadLoopStage`, output redirects applied to that stage).
2. `ForwardStageInput` — a simple stage that must sit in a script block (env prefix, splat prelude,
   subshell guard for `${x=…}` / a parameter error, pipefail with a redirect tail) pipes the
   block's input into its one command.
3. `DrainStageInput` — a stage whose stdin is not the pipe (its own `< f` / heredoc, a command
   that never reads stdin) drops the records and runs once.

4. **The stdin probe** — whether code reads the stdin it inherits is asked in ONE place,
   `PsEmitter.ProbeStdinScope` (`Parser/PsEmitter.StdinProbe.cs`), and the question is "is this code
   PROVEN to read nothing?", never "can I see a reader?". Three constructs ask it: a read loop
   (stream per record / iterate its file inline, or read the shared cursor), a function body (plain,
   or `FunctionStdinScope` so `echo x | f` hands the pipe on), and the whole script
   (`TranspileProbingLauncherStdin`: forward the launcher's stdin or not). They used to look for a
   reader in the emitted text, each with its own marker list, so a reader the text does not show
   read nothing: `r() { read b; }; … | while read a; do r; done` streamed past r's read,
   `f() { g; }` dropped the pipe g reads, a script that only sources a file got no stdin. The probe
   emits the code inside a scope and answers "reads" when the text shows a reader
   (`PsBuild.ReadsSharedStdin`) OR any simple command in it is not proven clean
   (`NoteStdinUse` → `StdinReaders.MayReadStdin`). Proven clean: an assignment; a command with a
   stdin of its own (`< f`, a here-doc, `< /dev/null` — the `ssh … < /dev/null` idiom keeps the loop
   streaming); a builtin or ps-bash command that does not read (`echo`, `cat FILE`, `command -v x`);
   a function this unit defines exactly once, before the call, whose body was itself proven clean
   (`IsStdinFreeFunction`). Everything else is a reader: a program, a function of another unit (a
   sourced file, an earlier command line) or defined twice / later / recursively, `eval`,
   `source` / `.`, `exec`, `bash` / `sh`, `env` / `time` / `timeout` wrappers, `command prog`, a
   run-time command name, `read` / `mapfile`, and so any `$( )`, background job, condition or
   pipeline first stage that holds one. At run time nothing is detected at all: a function, eval
   text and a sourced file read `$global:__BashStdIn` when one is installed (eval and
   `Invoke-BashSource` transpile their text inside a scope then), so once the enclosing construct
   has opened the cursor every reader behind any indirection shares it.

`… | source f` / `… | . f` is a stage like `… | eval`: `EmitEvalWithStdin` opens the scope the
file's commands read.

**Background jobs inherit the stdin of the compound command that starts them**
(`echo hi | { cat & wait; }` prints `hi`; `cat &` in a read loop's body takes the lines the loop
has not read): `Invoke-BashBackground` hands the job's runspace the SAME cursor
(`BackgroundJobStdin.Inherit`; a plain queue is first replaced by a `StdinCursor`, whose buffer is
locked, because two runspaces now read it). The shell's own stdin is not inherited
(`StdinCursor.IsShellStdin`, set for the launcher's cursor): with job control off bash gives a job
at the top of a script `/dev/null`, so it gets an empty cursor. A job run in a host process of its
own (`-Isolated`, `PSBASH_BG_MODE=process`) cannot share the cursor and reads end of input.

`PsBuild` is the only file whose emitted text names `$input`, and the probe file the only one that
asks whether emitted text reads stdin (`PsBuildStageStdinGuardTests` scans the transpiler for
both). The matrix compound form × wrapper context × reader kind (visible readers, and the kinds the
emitter cannot see into: function, nested function, function of another unit, `source`, `eval`,
background job, program, `read -u N`, `read -n N`, run-time name, `$( )`, env prefix) is pinned in
`StageStdinMatrixTests` (Core) and `StageStdinMatrixDifferentialTests` (oracle).

Known limits. (1) A stage that reads the shared cursor and whose producer is not a literal
`yes`/`seq` is filled eagerly (PowerShell starts a script block's end block only after its upstream
ends). Since rule 4 that is every read-loop stage whose body is not proven clean — one that runs a
program or a function of another unit included — so such a loop sees its lines only once the
producer has finished, and an endless general producer into it does not terminate. Streaming those
needs the producer on another thread with the shell's state; a body that must stream can give its
commands a stdin of their own (`cmd < /dev/null`). (2) The proof trusts a unit's single definition
of a function: text run through `eval` / `source` that REDEFINES it as a reader after the unit was
transpiled is not seen.

Known divergences (oracle bash 5.2 / GNU coreutils 9.4): `{ head -n1; cat; } < file` prints `1 2 3`
in bash (GNU head seeks a regular file back) but `1` here — the same as bash when the data comes
from a PIPE, which is what ps-bash models (`printf '1\n2\n3\n' | { head -n1; cat; }` matches);
commands emitted as a statement list (env prefix, `& { …splat… }`) are never FED the scope (a
function or eval text behind one still reads the cursor at run time); `bash -c 'read z'` in a
scope is not fed (only `bash` with no script operand is), so the nested shell reads nothing.

### `[[ … =~ … ]]` and `BASH_REMATCH`

`[[ $s =~ PATTERN ]]` emits a subexpression that evaluates `-match` AND copies the
automatic `$Matches` hashtable into `$global:BASH_REMATCH`, so `${BASH_REMATCH[1]}`
and friends resolve — bash fills `BASH_REMATCH` on a successful `=~` and clears it on
failure. The parser reads the whole right-hand side as a raw regex operand BEFORE the
grouping-paren branch, so a pattern that STARTS with `(` (`(a)(b)`) is not mistaken
for a test grouping.

### `[ … ]` (single bracket) vs the `test` cmdlet

`[ ]` is translated natively (`TranslateTestCondition`) for the common shapes, but bash picks a 3-word
list's shape from the MIDDLE word, so `arity3` (`A op B` with `=`, `==`, `!=`, `-eq…-ge`, `-nt -ot -ef`, `-a`,
`-o`) is decided before any word-0 heuristic (negation, unary file test): `[ -f = -f ]` compares two strings,
`[ -z -a -z ]` is the AND of two non-empty words. A single-bracket list the translator does not model
(`\( … \)` grouping, `-o NAME`, `-v`, any unknown unary) runs `Invoke-BashTest` with the words verbatim through
`PsBuild.ExitCodeTest` instead of degrading to `$false` plus a diagnostic; `[[ ]]` keeps the diagnostic (the
cmdlet has no `[[` grammar).

### `:` / `true` / `false`

All three are no-argument builtins that ignore their arguments but still EXPAND them: `EmitNoOpArgPrelude` emits `[void](word);` for every non-literal argument (`: ${x:=5}` assigns, `: $(cmd)` runs cmd), then sets `$global:LASTEXITCODE` (`:`/`true` = 0, `false` = 1, with the errexit behaviour above). `:` was "command not found". As a `while`/`if` condition (`EmitWhileCondition` / `EmitCondition`) a bare `:` is the constant `$true`, and as a pipe target (`echo hi | :`) it is `Out-Null`.

**Redirections are performed even though nothing is written.** bash opens a redirect target for any
simple command: `: > f` creates or truncates f, `true >> f` creates it, `false > f` creates it and
fails, a bare `> f` (no command word) is the same as `: > f`, and a target in a missing directory fails
the command with status 1. The emitter used to drop the redirects of the three builtins and emitted a
bare `> f` as ` | Invoke-BashRedirect -Path f` (an empty pipe element: a parse error that failed the
whole script). `EmitNoOpRedirectTouches` renders each file-opening redirect (`>`, `>>`, `&>`, `&>>`,
`>&file`, any fd; not `/dev/null`, `>&2`, `2>&1`) as `PsBuild.TouchRedirectTarget` =
`@() | Invoke-BashRedirect -Path f [-Append]` (the cmdlet opens its target in `BeginProcessing`), after
the argument expansions and left to right; a preceding `2>/dev/null` silences the later targets'
failure (`: 2>/dev/null > d/f` is quiet, `: > d/f 2>/dev/null` is not, as in bash). The `true` / `:` /
bare-redirect body sets status 0 first, opens the targets, and ENDS in
`if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true }`
when it had targets, so a failed open flips `$?` for a following `||` / `if`. `Invoke-BashRedirect`
reports an unopenable target as `bash: d/f: No such file or directory` (also `Is a directory`,
`Permission denied`) with `$global:LASTEXITCODE = 1` and stops the upstream stages (`UpstreamStop`, the
`Select-Object -First` mechanism — not an error, so an emitted `try { }` is not aborted), so the upstream
command never runs and the script continues (this applies to `echo hi > nodir/f` too; it used to print
PowerShell's own exception text and leave status 0). Under `set -e` it is a terminating error. The failure
message names the shell, not the script path and line that bash prints for a non-interactive shell
(stderr text differs; status and tree match).

### Output redirects: the per-fd model

`AppendRedirectTail` applies a command's stdout/stderr redirects as bash does: left to right, per fd,
LAST one wins, `2>&1` copying fd 1's destination at that point (`2>&1 >o` sends stderr to the ORIGINAL
stdout, `>o 2>&1` to `o`). Every FILE destination — stderr too — goes through one
`Invoke-BashRedirect` stage (`PsBuild.RedirectStage`): stderr enters it via a `2>&1` merge and is
written byte-faithfully (LF, message only) instead of by PowerShell's native `2> f` (CRLF,
`Invoke-BashLs:` prefix). Only `>$null`, `2>$null` and the `2>&1` merge stay native, each at most once
(PowerShell rejects redirecting a stream twice, which made `cmd >/dev/null >/dev/null` a parse error
for the whole script). Superseded file targets are still opened, as bash opens them (`echo x >a >b`
leaves an empty `a`): `-Truncate` / `-Touch`. A tail with a redirect outside the model (user fds,
`<&`, numeric merges other than `2>&1`/`>&2`) keeps the legacy emission. `>&2` is part of the model: fd 1 copies fd 2's
CURRENT target (`>&2 2>/dev/null` still shows the line, `2>/dev/null >&2` discards it, `2>f >&2` writes both to
f), and stdout bound for the terminal's stderr ends the tail with `PsBuild.StdoutToStderrStage`: each record becomes
an error-stream record (`Write-BashHostStderr`), so an ENCLOSING `2>/dev/null` / `2>&1` / `2> f` applies to it and
`True` stays true. A bare `2>&1` is `PsBuild.MergeStderrIntoStdout` (`| Invoke-BashRedirect -MergeErrors`): the merged
records become stdout TEXT, so an enclosing stage no longer reads them as stderr.

### `set`

- `set -e` / `set -o errexit` -> `$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true` (+ create `$global:__BashErrexitSuppress` if absent)
- `set +e` / `set +o errexit` -> `$ErrorActionPreference = 'Continue'; $global:__BashErrexit = $false`
- `set -x` / `set -o xtrace` -> `Set-PSDebug -Trace 1`. In a unit that can turn xtrace on (`PsEmitter.CanSetXtrace`), each statement is preceded by
  `Write-BashXtrace <fields>` (`PsEmitter.TryEmitXtrace`). The trace line shows the command's FIELDS after
  expansion.
  - A word the command splits (`IsUnquotedSplitWord`) is passed as the same `EmitUnquotedSplitWordArray` the command
    splats (`XtraceFields`). So `$unset -c 'echo 2'` traces `+ -c 'echo 2'`, an empty `"$@"` traces nothing, and
    `""` traces `''`.
  - An assignment is passed as `PsBuild.XtraceAssignment` and traces `+ v='p q'`.
  - Only words that expand without side effects are traced (`XtracePure`). Words with a command or arithmetic
    substitution, an assigning or failing `${…}` operator, `$RANDOM` or a glob would run twice, so their command
    goes untraced.
  - Also untraced: pipelines, array assignments, and the commands inside an eval.
  - Differential: `XtraceFieldsDifferentialTests`.
- `set -u` / `set -o nounset` (any bundling) -> `$global:__BashNounset = $true`. A unit that can turn nounset on (`PsEmitter.CanSetNounset`) emits plain `$x` / `${x}` reads as a runtime check that throws `BashParamErrorException` ("x: unbound variable") when the flag is on and the variable is unset; `${x:-d}`-style forms and positional parameters are not checked. The check is per command string: a later `-c` does not inherit it.
- `set -C` / `set -o noclobber` -> `$global:__BashNoclobber`; `Invoke-BashRedirect` refuses a truncating `>` onto an existing file unless `-Force` (`>|`).
- Combined flags (e.g., `set -euo pipefail`) are decomposed.
- `set -o "$opt"` / `set "$flags"` (an option word known only at run time) runs `set` then, as its static form with
  the words it got (`PsBuild.BuiltinAtRunTime`, the runner of a builtin named at run time, see "General fallback").

**Invariant: a per-unit mode is decided on the parsed commands, never on the source text.** Whether a unit pays for
per-stage pipeline status (`pipefail`, `PIPESTATUS`), xtrace lines or nounset checks is `PsEmitter.CanSetPipefail` /
`ReadsPipestatus` / `CanSetXtrace` / `CanSetNounset` (`PsEmitter.ShellOptions.cs`, one predicate
`CanSetShellOption`), the `CanSetTrap` pattern: an operand of `set` / `shopt` naming the option or known only at run
time, a call of a function the unit defines or a run-time command name with such an operand, eval text, a trap action
and an alias (static text parsed and judged the same way; text known only at run time counts for pipefail only, whose
capture is cheap). Turning an option off (`set +o xtrace`, `+x`) never turns its mode on. A text scan for `pipefail` turned the mode on
for `grep -c pipefail f && echo y`; a regex for `set -x` missed `c=set; $c -x`. Guards:
`Core.Tests/PipefailModeEmitterTests`, Differential `PipefailPositionDifferentialTests`,
`DynamicBuiltinDifferentialTests.XtraceNounset_TurnedOnThroughARunTimeWord`.
**errexit checks** (`PsEmitter.EmitStatement`, `PsBuild.Errexit*`). Emitted only when the script itself
contains `set -e`/`-o errexit` (`PsEmitter.EnablesErrexit`); every other script is byte-identical. Each
command at STATEMENT position (`EmitBody`/`EmitCommandList`: script top level, if/loop/case/function/group/
subshell bodies) that bash's errexit applies to — a simple command (not a status-neutral builtin such as
`export`/`local`/`set`), a pipeline (last stage's status), `[ ]`, `(( ))`, a subshell, `X=$(…)` — is followed by
`if ($global:__BashErrexit -and -not $global:__BashErrexitSuppress -and $global:LASTEXITCODE -ne 0) { exit … }`
(a scoped `return` inside a subshell). bash's exempt contexts emit no check inside (`_errexitExemptDepth`)
AND raise the runtime `$global:__BashErrexitSuppress` counter, so a FUNCTION called there runs past its
own failures: if/elif/while/until conditions, non-final `&&`/`||` operands, `!`, pipeline stages, command
and process substitutions. An `&&`/`||` list leaves only if its final command ran and failed
(`$global:__BashErrexitTail`). An untaken `if` and a finished `while`/`until` set status 0, so a function
ending in one does not leak the failed condition's code to its caller's check.

**A stage inherits `-e`.** A compound or function stage of a pipeline at an armed statement is a subshell that
inherits `-e`: its statements carry checks, and the stage lifts the pipeline's suppression around them
(`PsBuild.ErrexitLifted`). The lift is ABSOLUTE — the pipeline records the counter before raising it
(`PsBuild.ErrexitSuppressedFrom`, `$__psbash_eb<n>`) and a stage sets the counter back to that value, restoring
what it found when it ends. Never a decrement: stages of one pipeline run interleaved (a streamed read loop
handles a record while a compound producer is still inside its own lift), two decrements gave -1, and a
non-zero counter reads as "suppressed" — `{ echo three; echo four; } | while read l; do echo "[$l]"; false; done`
ran on to `[four]`. This holds for every read-loop form (streamed, shared cursor, redirected, in a group, in a
function); a `lastpipe` last stage lifts the same way and, being the shell itself, its failure ends the script.
Guard: the errexit rows of `StageStdinMatrixTests` / `StageStdinMatrixDifferentialTests`.

**Conditions and chain status** (`PsEmitter.EmitIfFromArm`, `EmitWhileLoop`, `EmitAndOrList`). An
if/elif/while/until condition is a `compound_list` (`while a; b; do` — status of the last command). A
condition that RUNS A COMMAND (`IsHoistedCondition`: anything but a pure `[ ]`/`[[ ]]`/`(( ))`/`true`/
`false`) is hoisted to statement position — `cond; if ($global:LASTEXITCODE -eq 0) { … }`
(`PsBuild.HoistedCondition` + `LastStatusTest`) — because in expression position
(`PsBuild.ExitCodeTest`) its output must be `[void]`-ed, and bash prints it (`if echo in; then …`). A hoisted
`elif` nests in the previous arm's `else` (it runs only after every earlier condition failed); a hoisted
loop is `while ($true) { cond; if (failed) { restore body status; break }; body }`, so `continue` re-runs
the condition and the loop's status is the last body command's (0 if none), never the failed condition's.
An untaken hoisted `if` sets status 0. The `cmd && break` / `cmd || exit 1` keyword form hoists the same
way (`grep pat f || exit 1` prints the matches). In a `&&`/`||` chain every non-final operand is followed
by `PsBuild.SignalFailIfFailed()`: PowerShell's chain operators test `$?`, which a bash function's
`return 3` or a subshell's `exit 4` leaves true; the bridge fails the chain on `-not $?` OR a non-zero
exit code.

The `$global:__BashErrexit` guard variable prevents strict-mode crashes when checking `$?` in error handlers. When `set -e` is active, PowerShell's `Set-StrictMode -Version Latest` would throw on null property accesses in conditions like `if [ $? -ne 0 ]`. The guard allows the emitter to conditionally suppress strict-mode behavior around exit-code checks.

### `cd`

`EmitCd` resolves the target against `[System.Environment]::CurrentDirectory` and, on success,
updates `$global:__PsBashCwd`, `CurrentDirectory`, `$env:PWD`, and `Set-Location`. Every
successful `cd` first records the directory it is leaving into `$env:OLDPWD` (captured before
`CurrentDirectory` is overwritten) — this is what makes `cd -` work.

- `cd` (no args) / `cd ~` -> `$HOME`
- `cd -` -> `$env:OLDPWD`, and like bash echoes the directory it lands in (`Write-Output`).
  When `$OLDPWD` is unset it fails with `cd: OLDPWD not set` instead of resolving an empty path.
- A missing directory emits `cd: <target>: No such file or directory` and sets `$LASTEXITCODE = 1`.
- **Bare Windows drive paths** (`cd C:\Users\andyb\work`) are reconstructed by
  `TryReconstructWindowsPath`. Bash's lexer treats each `\X` as an escape and drops the
  backslash (`C:\Users` decomposes to `Literal("C:")` + `EscapedLiteral("U")` + `Literal("sers")`),
  which is correct POSIX but useless on Windows. When a `cd` operand is a plain word (no
  variable/command-sub/glob/tilde part), the emitter restores the backslash for every "useless"
  escape (an escaped ordinary character = a separator) while keeping genuine bash escapes
  (`\ ` space, `\"`, `\'`, `\$`, `` \` ``). The word is only claimed when the reconstruction still
  contains a backslash — so `my\ dir` (→ `my dir`, no separator) stays on the normal emission
  path. Forward-slash drive paths (`cd C:/Users/...`) already resolve as-is and are untouched.

### `source` / `.`

- `source file.sh` -> `. file.ps1` (extension rewritten)

### Env pairs

If a command has leading environment variable assignments
(`NAME=value cmd args`), they are emitted as `$env:NAME = "value"; cmd args`
(a save / set / `try { cmd } finally { restore }` block, `WrapEnvPairs`), so the value is visible to that
command only and never leaks. The block also exports the names for the command
(`PsBuild.TempExportBegin` before the `try`, `PsBuild.TempExportEnd` first in the `finally`): a prefix
binding is in the command's environment even when the variable is otherwise unexported
(`environment-model.md` §3). `export` / `declare -x` / `declare +x` / `export -n` append
`PsBuild.MarkExported` / `MarkUnexported`, and `set -a` / `set +a` prepend `PsBuild.SetAllExport`.

**On a pipe-target stage** (`seq 1 40 | COLUMNS=60 column`, `echo x | FOO=1 env | grep FOO`) the
prefix used to be DROPPED: `EmitPipelineStages` sent a stage straight to `TryEmitMappedCommand`, which
never looks at env pairs. `EmitSimplePipeStage` now emits the bare stage and the pipeline wraps it with
`WrapEnvPairsForPipeStage`:

```
… | & { $__saved_COLUMNS = $env:COLUMNS; try { $env:COLUMNS = "60"; $input | Invoke-BashColumn } finally { $env:COLUMNS = $__saved_COLUMNS; } }
```

The block is a script block, and PowerShell hands a block's pipeline input to its `$input` only, never
to the commands inside, so the stage's input is forwarded explicitly (`$input | stage`). The variable is
restored after the stage (`echo x | FOO=1 cat; echo "[$FOO]"` prints `[]`). Limits: the block collects
its input before the stage runs (an unbounded producer such as `yes | FOO=1 head -n1` does not stream
into it), and a stage cmdlet must accept pipeline input — `Invoke-BashEnv` now declares and ignores a
stdin sink (it rejected `echo x | env`).

### General fallback

Words are emitted in order via `EmitWord`, redirects appended via
`EmitRedirect`.

**A command named at run time** (`"$@"`, `$cmd`, `command NAME`, `builtin NAME`) is `& (Resolve-BashCommand …)`,
looked up as bash does: function, builtin, PATH. **Invariant: one table, `BashBuiltins`, decides how a builtin runs
when its name is known only then, and it agrees with the static emitter.** A builtin the emitter implements itself
(`BashBuiltins.RunAsSource`: `eval`, `.`, `source`, `set`, `exec`, `exit`, `local`, `declare`, `export`, `alias`, …)
runs as its own static form, transpiled at run time from its words in the caller's scope
(`PsBuild.BuiltinAsSourceScript`, the same runner as `eval`); the call-stack builtins (`return`, `break`, `continue`,
`:`, `true`, `false`, `builtin`) are handled in `Resolve-BashCommand`; every other builtin is the run-time command the
static emitter reaches (`Invoke-Bash<Name>`). Before, the run-time lookup knew only the run-time commands, so
`run() { "$@"; }; run eval 'echo E'` was 127. A run-time name with only redirections (`c=exec; $c 3>f`) emits both
forms behind `PsBuild.RunTimeNameBranch` (`exec` makes them permanent), except in a pipe-target stage: a subshell,
whose input reaches only the stage's own block. **A run-time name as a pipe stage** (`echo x | $c`) is resolved with
`-Piped`: it gets the pipe only when it reads stdin (`StdinReaders.ShouldFeed`, the rule a literal name gets), otherwise the
records are drained and it runs once; piped unconditionally, `c="seq 2"; echo x | $c` failed to bind the record and never ran
(oracle `RuntimeCommandParityDifferentialTests.DynamicCommandName_Piped_RunsACommandThatDoesNotReadStdin`). Guards: `Core.Tests/BuiltinDispatchGuardTests`
(every name in exactly one group; the third group's static form is `Invoke-Bash<Name>`), Differential
`DynamicBuiltinDifferentialTests.EveryBuiltin_NamedAtRunTime_IsFound` (the fixed bash 5.2 `compgen -b` list through
`"$@"`, `$c` and `command`: never 127). `break`/`continue` named at run time do what loop control does at that call site: when the unit names either as
data (`c=break`, `run continue`; `PsEmitter.NamesLoopControl`, decided on the parsed words) the lookup gets
`-LoopMode` (the keyword in a loop, bash's warning with no loop in reach, the end of a subshell), else the mode
eval/source text inherited (`PsBuild.RunTimeLoopControl`). A bare PowerShell `break` left any loop on the call stack
and ended the whole script when there was none. A function body that names a command at run time restores the
caller's `local` slots too (`PsBuild.PublishParkedLocals`: `h() { c=local; $c v=1; }` no longer leaks `v`).

### Unquoted variable word-splitting (RC-7)

In bash, an **unquoted** parameter expansion (`$x`, not `"$x"`) undergoes word
splitting on IFS, and an empty result contributes **no word at all**:
`x=; echo start $x end` prints `start end` (one space), not `start  end`.

When a command **operand** (never the command word itself) is a pure unquoted
ordinary variable -- a lone `WordPart.SimpleVarSub`, or a suffix-less
`WordPart.BracedVarSub`, whose name is an ordinary variable rather than a bash
special parameter (`$@`, `$*`, `$#`, `$?`, `$1`..`$9`, etc., which keep their
own dedicated expansions) -- the emitter hoists it to a temp variable holding
the word-split array and PowerShell-splats it:

```powershell
& { $__bashsplat0 = @(ConvertTo-BashWords $env:x); cmd a @__bashsplat0 b }
```

`ConvertTo-BashWords` (`PsBuild.WordSplitArray`) splits on `$IFS` (unset = space/tab/newline; IFS
whitespace folds and is trimmed, an empty or blank value gives NO word) and then pathname-expands every
word that has a glob character with the same engine as `ConvertTo-BashGlob` — `v='*'; echo $v` lists the
directory, as bash does. (The hoist used to be `-split '\s+'`: whitespace only, no globbing.)

The `& { ... }` wrapper keeps the temp assignment from leaking into a
surrounding pipeline or and-or list.

**Known gap — evaluation ORDER.** The hoisted temp assignments run in the
prelude, i.e. *before the command*, whereas bash expands arguments strictly
left to right. That is invisible unless an argument BEFORE the splatted one has
a side effect, which in practice means an arithmetic substitution:

```bash
x=5; echo $((x++)) $x     # bash: "5 6"   ps-bash: "5 5"
```

`$x` is captured into `$__bashsplat0` in the prelude, before `Invoke-BashArith`
increments it. Fixing this properly means hoisting *every* argument into an
ordered temp whenever any operand needs the splat — which changes argument
semantics for arrays and quoted words, so it is deliberately not done for a case
this narrow. Only side-effecting expansions (`$((x++))`, `$((x=…))`) are
affected; a plain command substitution earlier in the same command is fine
because it does not mutate a variable the later operand reads.

**Command substitutions are split words too.** A command operand that is a single unquoted
`$(cmd)` / `` `cmd` `` (`IsUnquotedSplitWord` = pure variable, pure command substitution OR glob word) is
hoisted the same way, with `@(ConvertTo-BashWords <text>)` as the temp's value (IFS split, then glob
expansion; see "`for x in LIST`"): `printf '%s\n' $(echo a b)` prints `a` and `b`, an empty result
contributes no argument (it used to pass a spurious `$null`), and `"$(cmd)"` stays one argument.
**On a pipe-target stage the splat block forwards its input** (`& { $__s = …; $input | cmd @__s }`,
`_pipeStageFeedsInput`, cleared inside command / process substitution bodies by `EmitCaptured`): the
hoist wraps the stage in a script block, which used to swallow the pipe, so
`printf 'a\nb\n' | grep $x` printed nothing. The `@(...)` around the expansion is required:
assigning a bare empty result collapses to `$null`, and
splatting `$null` injects one spurious empty argument. This applies to both the
general fallback path (`EmitSimple`) and the mapped passthrough path
(`EmitPassthrough`), via `IsPureUnquotedVarWord` / `EmitCommandWithSplatArgs`.

---

## 5. `EmitPipeline`

`EmitPipeline` processes `Command.Pipeline` nodes:

1. The **first** command is always emitted via the general `Emit` dispatcher
   (not mapped).
2. **Subsequent** commands (pipe targets) are first tried through
   `TryEmitMappedCommand`. If the command is recognized, the mapped form is
   used. Otherwise, the general `Emit` path is used, prefixed with a stdin
   conversion stage (`NativeStdinFeedStage`). PowerShell's native binder
   *formats* objects piped to a native program — a BashObject would reach it as
   its property table (`BashText NoTrai…`) — and ends every record with `\r\n`
   (every `\n` became `\r\n`, an unterminated last line gained a newline, non-UTF-8
   bytes were re-encoded; `ssh host 'bash -s' < script` failed with `$'hostname\r'`).
   bash pipes bytes, so a command word that could be a program
   (`StdinReaders.Kind.Native`, not mapped) gets `ConvertTo-BashNativeStdin 'NAME' |`:
   when NAME resolves to an application at run time each record becomes its exact
   bytes (`NativeStdinBridge.RecordBytes`, written raw and streamed because a
   `byte[]` is the one object PowerShell does not format); a function, alias or
   cmdlet of that name — and any builtin or computed command word — keeps
   `ForEach-Object { Get-BashText $_ } | ` (`Get-BashText` is identity on a string
   and on any object with no `BashText`), so the rule is safe for every non-mapped
   pipe target. The same stage follows a heredoc / here-string
   (`TryEmitHereDoc`) and a `< file` (`TryEmitInputRedirect`).
   A `< file` is a byte stream, so every input redirect — simple command,
   subshell, brace group, `while read` loop source — reads the file with
   `Invoke-BashCat FILE -RawStdinBytes` (no CRLF / BOM rewriting, the missing
   final newline kept), not `Get-Content`; a file OPERAND of `cat` and the line
   tools keep the "CRLF / BOM are transparent" policy. Known gap (the OUTPUT
   side): PowerShell line-decodes a native program's stdout, so a CR before an LF
   is dropped and a missing final newline added in a pipe, `>` or `$(…)`
   (`NativeStdioBytesTests`, skipped cases) — byte-exact output needs ps-bash to
   run the program itself.
   A simple pipe-target stage is emitted by `EmitSimplePipeStage`; with an env prefix it is wrapped in
   a save/set/restore script block that forwards `$input` (see "Env pairs"), and a stage that needs the
   RC-7 splat hoist forwards `$input` inside its block.
3. Pipe operators: `|` emits as ` | `, `|&` emits as `PsBuild.MergeStderrIntoStdout` + ` | `
   (` 2>&1 | Invoke-BashRedirect -MergeErrors | `): the next stage reads each stderr write as a STDOUT record with the
   write's own bytes (`printf x >&2` stays unterminated), never a raw ErrorRecord, whose text already holds its `\n`
   and so read as one more empty line under the record -> bytes rule. Guard `Core.Tests/StderrMergeGuardTests`.
   Under per-stage status (pipefail / `PIPESTATUS`) the pipeline is `PsBuild.PipefailPipeline` around
   `PsBuild.PipefailStage` stages. **Invariant: it is ONE pipeline element** (a dot-sourced block: the caller's scope,
   streaming output), so it is valid PowerShell in every position a pipeline stands in: after `&&`/`||`, in `$( )`, as a
   condition, negated. The old `$__pf = [int[]]…; …` statement list was "Unexpected token '='" after `&&`. Guard:
   `Host.Tests/PipefailParseabilityTests` (PowerShell's parser over a corpus of positions).
4. A **compound** stage (`subshell` / `brace group` / loop / `if` / `case`) emits
   PowerShell *statements*, not a pipeable expression, so it is wrapped in
   `& { … }` (PowerShell rejects `foreach (…) {} | sort` with "An empty pipe
   element is not allowed"). A compound stage after the first additionally opens a
   stdin scope (drains `$input` into `$global:__BashStdIn`) so EVERY stdin-reading
   command inside sees the piped records (see "Compound-command stdin").
5. **A stage starts with the status from before the pipeline.** bash runs each stage in a subshell of the shell as
   it was, so a bare `exit`/`return` or `$?` there sees the pipeline's entry status (`false; { exit; } | cat` is
   PIPESTATUS `1 0`; `true; false | { exit; }` is 0). PowerShell starts the stages interleaved, and by the time a
   stage's body runs the stages ahead have reset (`Invoke-BashPipeStage`, a cmdlet's start) or set the status. So
   every stage whose body is shell code (a compound stage, and a function/eval/assigning stage the emitter scopes)
   starts with `PsBuild.SeedStageStatus`, emitted only by `PsEmitter.SeedStageStatus`, and `EmitPipeline` then wraps
   the pipeline in `PsBuild.WithPipeEntryStatus` (one dot-sourced block, like `PipefailPipeline`), which captures
   `$__psbash_pst` before any stage starts; a pipeline inside a stage captures its own. A simple command stage needs
   no seed: it sets its own status, and its words see the entry status: under per-stage status the first
   `Invoke-BashPipeStage` reads it as it starts (keyed by the run's slot array), every stage restores it for its own
   start, and the first stage's commands run from it (`false; echo $? | cat` prints 1); a stopped stage never reports
   it as its own. Guards `Core.Tests/BashTranspilerReviewRegressionTests`
   (`Transpile_PipeStageWithShellBody_StartsWithTheStatusBeforeThePipeline`), `Host.Tests/PipefailParseabilityTests`.

Note: standalone commands are also mapped via `EmitSimple` (see Section 2.2),
so mapping applies to both pipe targets and standalone invocations.

### Fused-pipeline lane (PERF phase 2)

> **Where the code lives.** Detection is `Parser/FusedLane.cs` (allowlist, the
> `PSBASH_FUSED` kill switch, the unbounded-stage guard, the inner-text builder) —
> extracted from `PsEmitter.cs` so the emitter file stays navigable. The two
> `[ThreadStatic]` fields the decision reads (`_captureDepth`, the
> `FusionEnabledOverride` test seam) stay on `PsEmitter`, which is what mutates
> them, and are **passed in** — so "when does fusion apply" has exactly one home.

When **every** stage of a pipeline maps to an allowlisted line-oriented command
(`FusedLane.FusePipelineAllowlist`: cat grep sed head tail wc sort uniq tr cut seq
rev tac nl), the whole pipeline is **plain `|`** (no `|&`), has **no per-stage
redirect / heredoc / env-prefix**, is **not negated**, and is **terminal-bound**
(`_captureDepth == 0` — not inside a command / process substitution), `EmitPipeline`
wraps the otherwise-identical emitted text in a single call:

```
Invoke-BashFusedPipeline { <inner pipeline text> }
```

The runtime cmdlet runs the inner pipeline host-side and returns its output in a
few large batched frames instead of one IPC frame per line — the phase-1
profile's dominant bottleneck (per-output-line framing back to the launcher).
Because the wrapper runs the **identical** inner text, byte-fidelity is guaranteed
by construction.

#### Compiled line-stream cores (phase 2b) — the *fully compiled* lane

Batching the frames (phase 2a) still ran the inner **PowerShell pipeline**: one
`PSCustomObject` per line, plus per-stage pipeline dispatch. Phase 2b removes the
pipeline engine from the hot path entirely. Each allowlisted command has a
**compiled C# streaming core** — an `ILineStreamStage` (`src/PsBash.Cmdlets/LineStreamStages.cs`
+ `src/PsBash.Cmdlets/LineStream/*.cs`) that consumes `IEnumerable<string>` and
yields `IEnumerable<string>`. No per-line object allocation, no pipeline dispatch,
no ETS.

- **Dispatch** — `LineStreamRegistry.TryCreate(name, argv, out stage, resolvePath)`. All 14
  allowlisted names now have a core (S1 cat/seq/rev/head/wc/grep/sed; S2 sort/uniq
  + the `cat FILE` producer; S3 tr/cut/tail/tac/nl), so a chain no longer declines
  because of an *arbitrary* missing stage.
- **Per-argv certification, not per-command.** A core accepts only the argv subset
  whose byte-parity against the real cmdlet is proven by test; **any** argv outside
  that subset returns `false`, and then the *whole* chain declines back to phase-2a's
  delegate+batch fallback. Correctness always wins — the streaming lane is a pure
  speedup for the cases it covers, never a behavior fork.
- **Laziness buys SIGPIPE semantics for free.** When a downstream stage stops pulling
  (`head` after N lines), the upstream generators are abandoned mid-iteration and the
  producer stops producing — the early-exit a real pipe gets from SIGPIPE.
  **Exception:** `sort` is a *blocking* core (it must see every line), so nothing
  downstream of a `sort` can early-exit.
- **Relative file operands resolve through PowerShell's location**, not the process cwd:
  the fused cmdlet passes its `SessionState.Path` resolver into `TryCreate`, so a producer
  stage (`cat FILE`) and the cmdlet it stands in for can never name different files. This
  replaced a convention — "every writer of the working directory moves both halves" —
  that broke three times (`pushd`, the subshell `Pop-Location`, the module-mode `cd`
  alias), each time as a SILENT wrong-file read at exit 0. See `CatFileStage` remarks.
- **Serialization is identical by construction.** The executor renders each yielded
  string as `line + Environment.NewLine`, which is exactly how a `PsBash.TextOutput`
  object or a bare string renders on the unfused path
  (`InvokeBashFusedPipelineCommand.RenderItem`).
- **Exit codes.** Each stage exposes `ExitCode` valid after full enumeration (grep
  sets 1 on no-match); the executor propagates only the **last** stage's code to
  `$global:LASTEXITCODE`, matching an unfused pipe.
- **Guard** — the fused-vs-unfused parity tests
  (`src/PsBash.Cmdlets.Tests/LineStream*ParityTests.cs`, `LineStreamParityHarness`)
  are what keep the two lanes byte-identical. Any non-allowlisted / external stage, `|&`, per-stage redirect,
env-prefix, negation, or capture context keeps today's PowerShell-pipeline path.

**The fallback streams.** When a stage's core declines, the inner PowerShell pipeline
is piped into a sink instance of `Invoke-BashFusedPipeline` (hidden `-Sink` parameter
set) that renders each record into a ~32 KiB batch and writes the frame as soon as it
is full (`FusedFallbackStreamingTests`). It used to be `InvokeScript`, which returned the
whole result Collection only after the pipeline completed, so retained memory was the
whole output. Frame rendering is unchanged (`RenderItem`), so the bytes are identical.

**Unbounded-stage guard (`StageIsUnbounded`).** Kept after the fallback started streaming,
for a different reason than it was written: frames are cut by SIZE, not by idleness, so
a never-terminating stage (`tail -f log | grep x`) would hold its lines in a half-full
frame where the unfused lane prints each one live, and an idle flush needs a timer thread
while `WriteObject` is pipeline-thread-only. `tail -f` / `-F` / `--follow` (and,
conservatively, `tail` with a non-literal arg that could expand to `-f`) therefore keep
the unfused lane; plain `tail -n 5` still fuses. The check is keyed by command name so a
future allowlist addition with its own unbounded flag inherits the seam.
The kill switch `PSBASH_FUSED=0` (falsy tokens) disables detection; default ON.
`ls | grep .txt` and other typed-object boundaries are unaffected — `ls` (and
`find`/`awk`/`jq`) are deliberately **not** allowlisted.

---

## 6. Other Emitters

### `EmitWord`

Walks `CompoundWord` parts and concatenates their emitted text. Handles brace
expansion (e.g., `file{1,2,3}.txt` -> `@('file1.txt','file2.txt','file3.txt')`).
Calls `TransformWordPath` on the final result. A word with an unquoted glob part never reaches it as an
operand: the callers test `IsGlobWord` / `IsBraceGlobWord` first and emit the pattern through
`EmitGlobPatternExpr` instead (§4 "Pathname expansion"); `EmitWord` still renders such a word bare for the
positions that do not expand (assignment values, `case`, `[[ ]]`, redirect targets).

### Number-shaped words

A bash word is text. PowerShell's tokenizer reads a whole unquoted `1e5`, `33441e77`, `0x10`, `0b101`, `1kb`,
`10d`, `7u`, `08`, `.5`, `1.` as a NUMBER: a function's `$args`, an `[object]` parameter, an `@(…)` item or a
`foreach` item gets the converted value (`for f in 33441e77` printed `3.3441E+81`, `f 1e5` gave the function
100000, `{-0,1}` became the range `@(0..1)`). **Invariant: whether emitted text may stay bare is decided ONLY by
`PsNumberLiteral` (`Parser/PsNumberLiteral.cs`), through `PsBuild.QuoteArgumentNumber` (an argument: a word
PowerShell would read as a number is single-quoted, except a canonical integer) and `PsBuild.IsBareIntegerValue`
(a list item: only a canonical integer, `0` or `-?[1-9][0-9]{0,17}`, which keeps its text in argument AND
expression mode; `08`, `-0`, `+5` are quoted).** `EmitWord` applies the first to every word. Never a
`double.TryParse` / `long.TryParse` on emitted text. Guard `PsNumberLiteralGuardTests` (the predicate against
PowerShell's own tokenizer over a fuzzed corpus, and a source scan of `PsEmitter.cs`); emitter tests
`NumberShapedWordEmitterTests`; oracle `NumberShapedWordDifferentialTests` (every word-emitting construct).

### Quote marks (bash text PowerShell reads as syntax)

PowerShell's tokenizer ends a string at the Unicode quote marks as well as at the ASCII ones: U+2018-U+201B
close a single-quoted string, U+201C-U+201E a double-quoted one, and a token that starts with U+2013-U+2015
(en dash, em dash, horizontal bar) is a `-parameter`. To bash they are ordinary characters. Every emitted
PowerShell literal therefore goes through ONE `PsBuild` primitive that knows the whole set:

| Context | Primitive | Escape |
|---|---|---|
| `'…'` | `SingleQuote` / `EscapeForSingleQuote` | every single-quote mark doubled |
| `"…"` (no live `$`) | `DoubleQuote` / `EscapeForDoubleQuote` | backtick before `` ` ``, `$`, every double-quote mark |
| `"…"` with live `$` (a heredoc body) | `EscapeDoubleQuoteMarks` | backtick before every double-quote mark |
| unquoted argument text | `BarewordNeedsEscape` / `EscapeBareword` | `{` `}`, quote marks, a leading dash mark; a whole word becomes one `"…"` |

A pasted apostrophe (`echo it’s`) was a parse error that failed the whole script, `echo 'a‘’b'` silently
printed `a’b`, and `echo –n x` ran `echo -n`. An ad-hoc `.Replace("'", "''")` is forbidden outside `PsBuild`
(guard `PsQuoteMarkGuardTests`, which also round-trips every primitive through PowerShell's parser and
parses a corpus of every word context × every mark; oracle `PsQuotingDifferentialTests`).

Inside the `$( )` of a PowerShell expandable string, double-quote marks are rewritten even within a nested
literal (`"$('a""b')"` is `a"b`: a pair becomes one, a backtick before one is consumed). Code holding such a
mark never sits inside a string: a `${…}` whose argument or subscript holds one (`"${u:-a\"\"b}"`,
`"${m[k""]}"`) is emitted as a join (`BracedVarLeavesString`, the same `TryEmitJoinedWord` /
`TryEmitJoinedValue` path a command substitution takes).

Raw subscript text (an assoc compound key `["k\"j"]=v`) gets bash's quote removal through
`PsBash.Core.BashQuoting.RemoveQuotes`, shared with the cmdlets' run-time `BashGlob.RemoveQuotes`; stripping
only the outer quotes kept `k\"j`.

### `EmitDoubleQuoted`

Emits `"..."` with inner parts (literals, variable subs, command subs) rendered
inside the quotes.

### `EmitBracedVar`

Handles `${VAR...}` parameter expansions:

The `:`-prefixed forms act when the variable is unset **or empty**, so they use
the PowerShell ternary — which is exactly bash's test, since `""` and `$null` are
both falsy while `"0"` (set and non-empty in bash) is truthy in PowerShell.
They previously emitted `??`, which only tests `$null`, so a variable set to the
empty string skipped the operator entirely and `x=; echo ${x:-d}` printed nothing.

- `${VAR:-default}` -> `($env:VAR ? $env:VAR : "default")`
- `${VAR:=default}` -> `($env:VAR ? $env:VAR : ($env:VAR = "default"))`
- `${VAR:+alt}` -> `($env:VAR ? "alt" : "")`
- `${VAR:?msg}` -> `($env:VAR ? $env:VAR : $(throw "msg"))`
- `${#VAR}` -> `$env:VAR.Length`
- `${VAR%%pat}` / `${VAR%pat}` -> `-replace` suffix removal
- `${VAR##pat}` / `${VAR#pat}` -> `-replace` prefix removal
- `${VAR//find/rep}` / `${VAR/find/rep}` -> `-replace`
- `${VAR:offset:length}` -> `.Substring(offset, length)`
- `${VAR^^}` / `${VAR,,}` / `${VAR^}` / `${VAR,}` -> case conversion
- `${VAR-w}` / `${VAR=w}` / `${VAR+w}` / `${VAR?msg}` -> colon-less **unset-only** forms, which KEEP `??`. `$env:X` is `$null` when unset but `""` when set to empty, so `??` is an EXACT match for the unset-only test — which is precisely why it was wrong for the `:` forms above. The operator is never dropped.
- `${@:off}` / `${@:off:len}` / `${*:…}` -> a slice of the positional parameters, via the runtime-clamped array slicer. bash counts positionals from `$1` while the emitted array's index 0 already holds `$1`, so a POSITIVE offset shifts down by one and a NEGATIVE offset (counting from the end) passes through. `${@:0}` degrades to the whole list — bash prepends `$0`, which the array does not carry.
- `${VAR@Q}` -> single-quote the value for reuse (`"'" + ($env:VAR -replace "'","'\''") + "'"`); `${VAR@U}` / `${VAR@L}` / `${VAR@u}` / `${VAR@l}` -> case transforms. `@E` (escape-expand), `@P` (prompt-expand), `@A` (assignment form), `@a` (attribute flags) have no faithful PowerShell mapping and degrade to the bare value — the transform is dropped, the value is preserved (documented gap, NOT a silent total drop).
- `${arr[n]}` -> `$arr[n]`, `${arr[@]}` -> `$arr`, `${#arr[@]}` -> `$arr.Count`
- `${!arr[@]}` -> `$arr.Keys`

### One arithmetic evaluator

**Invariant.** Every bash arithmetic context is evaluated by ONE evaluator: `BashArithmeticParser` (parse) plus
`BashArith.Evaluate` (value), reached from emitted code as `Invoke-BashArith`. The contexts are `$(( ))`, `(( ))`,
`let`, the `for ((;;))` clauses, a `[[ a -eq b ]]` operand, `declare -i NAME=VALUE`, the offset and length of a
slice of a string, an array or the positionals (`${s:o:l}`, `${a[@]:o:l}`, `${@:o:l}`), and an indexed subscript
that is read, written, unset or named indirectly (`${a[i]}`, `${a[$k]}`, `a[i]=x`, `unset 'a[i]'`, `ref='a[i]'; ${!ref}`).

**The one shortcut.** `BashArithmeticParser.TryPlainDecimal` decides when the evaluator can be skipped: only for a
canonical decimal literal (`0`, `7`, `-12`; no leading `0`, no `+`, no blanks, within 64 bits). Every evaluator reads
such text as that number, so skipping it cannot change a result. Everything else goes to the evaluator: `010` is
octal 8, `08` is an error, `-010` is -8, and `1+1`, `i`, `$j` (j=`1+1`) are expressions. In emitted PowerShell, the
same test is `PsBuild.ArithPlainDecimal` (`[PsBash.Core.Parser.BashArithmeticParser]::PlainDecimal`, a number or
`$null`), and an evaluated operand used as a number is `PsBuild.ArithValue` (`[long]$(Invoke-BashArith …)`). It is a
WORD, so a failed operand abandons the command like `$(( 08 ))`. A slice bound goes through
`PsEmitter.EmitArithOperandValue`, which labels its error with the expansion (`s: 08: value too great for base
(error token is "08")`, `a[@]: …`, `@: …`).

**The bug class.** Each context had its own shortcut: `int.TryParse` (slices, literal subscripts), `long.TryParse`
(`declare -i`, `unset`), `[long]` after `-match '^-?[0-9]+$'` (`${a[$k]}`, the indirect element, `Set-BashElement`),
`value[0] != '0'` (element operands, which let `-010` through). They read `010` as 10 and `08` as 8. A variable
slice bound (`${a[@]:$j}`) was cast with `[int]` instead of being evaluated, and a bound such as `1+1` or `i`
degraded to the whole array. The evaluator itself also pasted a `$x` value with blanks around it, so `16#$h` became
`16# ff`. bash pastes the text verbatim: `h=ff; $((16#$h))` is 255 and `x=2; $((1$x))` is 12.

**Evaluator semantics (bash 5.2, expr.c).**

- A compound assignment reads its target BEFORE the right side runs: `x=1; $((x += (x=10)))` is 11.
- `**` is exponentiation by squaring with a wrapping multiply. A negative exponent is an error.
- `-2**63 / -1` wraps to -2**63, and `% -1` is 0. This also holds for `/=`, `%=`, `let` and `(( ))`.
- No .NET exception leaves the evaluator. A fault is a `BashArithException` of that expression.
- `++` / `--` is an increment only after a name or before one (blanks allowed). Anywhere else it is two signs:
  `++5` is 5, `1--1` is 2, and `5++` is "operand expected".
- `BASE#DIGITS` up to base 36 reads letters of either case as 10-35. Above base 36, a-z are 10-35, A-Z 36-61,
  `@` 62 and `_` 63.
- An assignment operator after anything but a name is "attempted assignment to non-variable". This covers `1=2`,
  and `0 ? 1 : x = 5`, because the false branch is a conditional. A missing operand after `?` or `:` is
  "expression expected".
- The recursion limit names the expression that went one level too deep: `r1=r2 r2=r1; $((r1+1))` is
  `r2: expression recursion level exceeded (error token is "r2")`.

**Guard.** `Core.Tests/ArithmeticOperandGuardTests`: no C# `int`/`long` `.Parse`/`.TryParse` of an arithmetic operand
in `PsEmitter*.cs`, `PsBuild.cs` or the cmdlets that evaluate arithmetic. No emitted `[int]`/`[long]` cast except of
the evaluator's value or of a non-arithmetic number (an fd, a status, the uid). No `[int]::TryParse` / `-match
'^-?[0-9]+$'` number test in the emitter or the module except an inventoried non-arithmetic one. Behaviour against
bash: `Differential.Tests/ArithOneEvaluatorDifferentialTests`, `Cmdlets.Tests/ArithOneEvaluatorTests`.

### Arithmetic operands the evaluator cannot resolve (`BuildArithFragments`)

`$(( … ))` / `(( … ))` / the `for ((;;))` clauses all funnel through
`EmitArithmeticInvocation`, which normally hands the expression to the runtime
evaluator as ONE single-quoted literal (`Invoke-BashArith '2**10'`). Two kinds of
span cannot survive that, because bash expands them to text *before* evaluating:

- **positional / special parameters** (`$1`, `$#`, `${?}`) — the evaluator's lexer
  would read `$1` as the literal `1`;
- **command substitutions** (`$(date +%s)`, `` `date +%s` ``) — the command has to
  actually run.

`BuildArithFragments` scans the expression once and splits it into PowerShell
concatenation fragments: each such span becomes a value-producing subexpression,
everything else stays a single-quoted literal for the evaluator to resolve itself.
`$(( $(echo 5) + 60 ))` emits

```powershell
Invoke-BashArith ('' + $((@(Invoke-BashEcho 5 | ConvertTo-BashCapture) -join [string][char]10).Trim()) + ' + 60')
```

The leading `'' +` forces string (not numeric) concatenation. The newline join uses
`[char]10` rather than a `"`n"` literal deliberately: the fragment can land inside an
arbitrarily nested `"$( … )"`, where a `"` would need escaping once per enclosing
string level. The scan is quote-aware, so a `)` inside a quoted argument
(`$(grep -c "a)b" f)`) does not end the substitution. Returns `null` when nothing
needs substituting, so the common case keeps the cheap one-literal form.

### Command-substitution capture (`ConvertTo-BashCapture`)

Every `$(…)` body ends in the binary cmdlet `ConvertTo-BashCapture` (`EmitCommandSub` / `EmitCommandSubString` / the arithmetic operand), not `ForEach-Object { Get-BashText $_ }`. It turns the records into the lines of their BYTE STREAM: a normal record is a line, a `NoTrailingNewline` (exact-bytes) record is glued to whatever follows it, and an exact record that already ends in `\n` loses it (the caller's join re-adds it once). So `$(printf a; printf b)` is `ab` (it was `a\nb`), `$(printf 'x\n'; echo y)` is `x\ny`, and a streaming producer such as `tr '\n' ,` may emit many exact records and still capture as one string — which is what let `tr` stop buffering. The pipe-TARGET conversion before a native program (`ForEach-Object { Get-BashText $_ } | native`, EmitPipeline) is separate and unchanged.

### `EmitRedirect`

Maps redirects to PowerShell: `>file`, `>>file`, `2>&1`, etc. Calls
`TransformRedirectTarget` on the target.

### `TransformWordPath` / `TransformRedirectTarget`

- `/tmp/file` -> `PsBuild.TempPath("file")` = `"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/file"` (both; double-quoted because a bare `$(…)/file` is two PowerShell arguments; a REST containing `"` keeps the legacy Windows-only `$env:TEMP\rest`).
  The rewrite is decided at RUNTIME, not transpile time: the emitted text may run on any OS.
  Windows maps `/tmp` to `$env:TEMP`; Linux/macOS keep the literal `/tmp` (`$env:TEMP` is usually
  unset there, and the old unconditional `$env:TEMP\file` collapsed to `\file` at the filesystem
  root). Build it only via `PsBuild.TempPath` / `TempDirExpr`; never hand-concatenate.
  **The rewrite is decided on the word's PARTS, not its emitted text** (`TryEmitTmpRootedWord` /
  `TryEmitTmpBraceWord` in `EmitWord`): the leading `/tmp/` of the first part (bare, `'…'`, or inside
  `"…"`) is removed and the rest is rendered through `AppendFlattenedParts` (the body of
  `FlattenPartsToDoubleQuotedString`) into one `"TempDirExpr/…"` string. Quotes are therefore
  consumed (`/tmp/psb_'s p'` → file `psb_s p`, not one with literal `'`), `$x` / `$(cmd)` stay live,
  `\$` / `\'` are literal, and a glob (`/tmp/*.log`) stays in the string for the cmdlet's own glob
  expansion. `/tmp/{a,b}` yields one rooted string per item. Brace-with-non-literal, tilde and
  process-sub words decline to their dedicated emitters; `$HOME/tmp`, `/var/tmp`, `./tmp` are never
  rewritten. Splicing emitted text into a string (the old `TempPath(emitted)`) is the bug class
  this replaced — the text still carried bash's quote characters.
  **Runtime twin — one policy for paths known only at run time** (`d=/tmp; … $d/f`):
  `PsBash.Core.RuntimePath.Map` (Windows: `/tmp[/…]` → `$env:TEMP[\…]`, then
  `WindowsPath.Normalize`'s unix-drive rules; elsewhere unchanged). Every resolver uses it, so a
  redirect, `cat`, `rm`, `cd` and `[ -e ]` agree on which file a spelling names:
  cmdlet operands via `FileSystemHelpers.NormalizeOperandPath` / `ProviderPath` (the chokepoint
  most cmdlets resolve through), psm1 helpers (`Get-BashItem`, readers) and emitted code via
  `BashRuntime.MapPath` — `cd` maps its target before `GetFullPath`; a non-literal `[ -e W ]` /
  `[[ -f W ]]` operand and a non-literal `< W` (read by native `Get-Content`) are wrapped with
  `PsBuild.RuntimeWindowsPath` (the word rides as a script-block argument; spliced into an
  expression its `/` would divide). A literal operand keeps its transpile-time rewrite and no
  runtime call. `source` uses `ProviderPath` too (its private `/tmp`→`GetTempPath` copy is gone).
- `/dev/null` -> `$null` — **redirect targets ONLY**. As a command *operand*, bash's
  `/dev/null` is an empty FILE (`grep x /dev/null` reads it and exits 1), not the `$null`
  discard sink — mapping it in `TransformWordPath` crashed cmdlets with "Value cannot be
  null". The literal flows to the runtime, where `BashFileSystem.OpenRead` (via
  `FileSystemHelpers.IsNullDevice`) serves it as empty.
- **Unix drive paths** (`/c/Users/...`, `/mnt/c/...`) -> `C:\Users\...`, via
  `TryTranslateMsysDrivePath` → `WindowsPath.TryMapUnixDrivePath`. Applied uniformly to
  redirect targets, command operands, and `cd` targets so an absolute drive path resolves
  the same way everywhere (otherwise `cat /c/x` resolves `\c\x` against the current drive
  and becomes `C:\c\x`). Paths with no drive component (`/home/x`) are left alone. A bare
  one-letter operand (`/c`, `/e`, `/f`) is a Windows SWITCH (`cmd /c`, `robocopy /e`,
  `taskkill /f`), not a drive root, and is never rewritten; only `cd /c` opts in to the bare root.

  This is **opt-in**: it fires only under `PSBASH_UNIX_PATHS=1`, set by the Shell layer
  from `--unix-paths`. Off by default so direct ps-bash users don't get surprise rewrites;
  **on for wrappers** (Claude Code, OpenCode) that emit unix-shaped paths assuming a POSIX
  filesystem. It is the first thing to check when an operand path "isn't there" — and note
  the rewrite targets the **Windows** filesystem, so a path meant to be consumed inside a
  WSL distro is broken by it.

### `EmitSimpleVar`

Maps bash special variables to PowerShell equivalents:

- `$?` -> `$LASTEXITCODE`
- `$@` / `$*` / `$#` / `$1`..`$9` / `${10}`.. -> the shell's positional list `$global:BashPositional` (set by a function
  call, `set --`, `shift`, `source` with arguments), or the script's `$args` when the shell has none. Invariant: the
  choice is `PsBuild.PositionalIsShells` (`$null -ne $global:BashPositional`), never truthiness: an EMPTY list is
  still the shell's (truthiness fell back to `$args` after a `shift` to empty, so a function's `while [ "$1" ]; do
  shift; done` never ended). Guard `PositionalFallbackGuardTests`.
- `$0` -> `$(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })` (the launcher's `-c CMD NAME` / script path as typed; it used to be `$MyInvocation.MyCommand.Name`, empty at top level)
- `$$` / `$!` -> `$PID`
- `$HOME`, `$LASTEXITCODE`, `$null`, `$true`, `$false` -> kept as-is
- Loop variables -> `$var` (not `$env:var`)
- Everything else -> `$env:NAME`

---

## 7. Special Runtime Handling

While the emitter's primary job is to forward arguments to runtime functions, certain commands require special mention due to bash-specific syntax that needs runtime-level translation:

### Process Substitution Classifier

The default `<(...)` and `>(...)` path emits `Invoke-ProcessSub`, which captures
producer output into an ephemeral temp file and passes that file path to the
consumer. This remains the safe fallback for seekable or multi-file consumers.
The producer is STREAMED into the file as it runs (`ProcessSubFileWriter`, 64 KB
buffered, exact bytes) rather than collected first; the consumer still starts only
after the producer finishes (a live feed needs a FIFO / named pipe, see the deferred
bullets below).

Some mapped commands can treat a single file operand exactly like stdin. For
those consumers, the emitter routes one pure process-substitution operand through
`Invoke-ProcessSubPipeline` so producer objects flow directly into the mapped
runtime command instead of being serialized to a temp file.

| Consumer cmdlet | Route for one `<(...)` operand | Reason |
|-----------------|--------------------------------|--------|
| `Invoke-BashHead` | `Invoke-ProcessSubPipeline` | stdin-substitutable stream consumer |
| `Invoke-BashTail` | `Invoke-ProcessSubPipeline` | stdin-substitutable stream consumer |
| `Invoke-BashSort` | `Invoke-ProcessSubPipeline` | stdin-substitutable stream consumer |
| `Invoke-BashUniq` | `Invoke-ProcessSubPipeline` | stdin-substitutable stream consumer |
| `Invoke-BashWc` | `Invoke-ProcessSub` | output format depends on filename presence — not truly stdin-substitutable (RC-8b) |
| `Invoke-BashDiff` | `Invoke-ProcessSub` | multi-argument, seekable file consumer |
| `Invoke-BashGrep` with `-f <(...)` | `Invoke-ProcessSub` | process substitution is a pattern file, not stdin |
| All other mapped commands | `Invoke-ProcessSub` | fallback until explicitly classified |

The pipeline-object route is intentionally narrow: it requires exactly one pure
process-substitution word for a classified consumer. Multiple process
substitutions stay on the temp-file path.

Current support is intentionally 80/20:

- Tier 1 is the default temp-file route. Unknown external commands, commands that
  need multiple file operands, and commands that may require seekable input stay
  on `Invoke-ProcessSub`. This is usually correct, but it is not streaming.
- Tier 2 is the pipeline-object route for known mapped commands where a single
  `<(...)` operand is equivalent to stdin. Today that allowlist is `head`,
  `tail`, `sort`, and `uniq`. `wc` was removed from this allowlist in RC-8b
  because `wc`'s output format echoes the operand filename when invoked with a
  file argument (`100 /dev/fd/63`) but emits count-only output for stdin
  (`100`). The two shapes are not byte-equivalent, so routing `wc` through the
  pipeline route would silently change its output relative to bash. `wc` stays
  on the Tier-1 temp-file route so a filename is present.
- `diff`, `comm`, `cmp`, `grep -f <(...)`, and similar file-oriented consumers
  stay on the temp-file route. Their operands are file paths or pattern files,
  not generic stdin streams.
- General external command streaming is deferred. The emitter must not guess that
  `external <(producer)` can become `producer | external` or `external -`; that
  requires an explicit allowlist and parity tests.
- A real stream-path bridge is deferred. There is no fake anonymous-pipe filename
  path. Windows would need a named-pipe path that the target process can open;
  POSIX would need FIFO or inherited-fd semantics. Both need cleanup, early-exit,
  and platform-specific tests before becoming a supported route.

### `sed` Backreferences

The `sed` command uses backreferences in the replacement string (`\1`, `\2`, ..., `\9` and `\&` for the entire match). PowerShell's regex engine uses `$1`, `$2`, ..., `$9` and `$0` instead.

The runtime `Invoke-BashSed` function handles this translation via `ConvertFrom-SedExpression`:

```powershell
# bash: sed 's/\([a-z]\+\)-\([0-9]\+\)/\2-\1/g' file.txt
# Runtime converts replacement \2-\1 to $2-$1 before passing to .NET regex
```

The conversion happens at:
- `\1` through `\9` -> `$1` through `$9` (capture groups)
- `\&` -> `$0` (entire match)

This allows sed expressions to work correctly with PowerShell's `[regex]::Replace()` while preserving bash syntax in user scripts.

---

## 7.9 Prefix assignments are temp binding frames

`v=t cmd` binds `v` for the command only. The binding is a FRAME over the variable, not a second assignment:
`x=42; x=tmp unset x` takes the frame and leaves 42, and so does `x=tmp eval 'unset x'`. Three ends exist, all in
`PsEmitter` and all through `PsBuild`:

| Form | Emitted by | End |
|---|---|---|
| `v=t cmd` (builtin, external, mapped) | `WrapEnvPairs` | `$__saved_v` put back |
| `v=t f` / `v=t eval` / `source` / `.` | `WrapEnvPairs` with a frame (`PushTempFrame`, `BindSameScope`) | put back unless `unset` took the frame |
| `set -o posix; v=t special-builtin` | `EmitPersistentPrefix` (`BindSameScope` ... `PersistSameScope`) | the value stays, unless `unset` took the frame |

Invariant: a prefix assignment is never emitted as a plain assignment followed by the command. The POSIX form
was (`$env:x = "tmp"; Invoke-BashUnset x`), so `x=tmp unset x` after `set -o posix` left `x` unset (oils
temp-binding "Temp bindings with unset"). The one exception is a declaration naming its own prefix
(`v=t readonly v`, `v=t export v`), which bash itself turns into the variable's value. Guards:
`Core.Tests/PersistentPrefixGuardTests` (every name of `s_posixSpecialBuiltins` emits the persisting frame, and
`PsBuild.PersistSameScope` has one caller) and `Differential.Tests/TempBindingDifferentialTests` (each form, at
top level, in a function called twice, over a `local`, with and without POSIX mode).

## 7a. PowerShell statement keywords are not commands

`exit`, `return`, `break`, `continue`, `throw` are PowerShell **statements**, not
commands. Three separate emitter paths have to know this, and each one broke a
different way before it did (`PsEmitter.PsStatementKeywordCommands` is the shared
set):

1. **Never splat into one.** `exit $code` emitted `exit @__bashsplat0`; `@var`
   after a statement keyword is a hard parse error that poisons the ENTIRE
   emitted file. `EmitExitWithUnquotedVar` keeps bash's word-splitting semantics
   by consuming the split array as a VALUE inside `exit $( … )` — so an empty
   operand still falls back to `$?`, the way bash elides the word.

2. **Never make one the right operand of `&&` / `||`.** PowerShell's chain
   operators require a command, so the keyword is parsed as a command NAME:
   `cmd && break` printed "The term 'break' is not recognized", and `cmd || exit 1`
   SILENTLY DID NOT EXIT. `TryEmitAndOrEndingInStatementKeyword` rewrites the
   chain to `if (<exit-code test of the whole prefix>) { KEYWORD }`.

3. **`exit` inside a subshell is scoped.** bash's `( … )` exits only the
   subshell and sets `$?` in the parent, so `(exit 7); echo $?` prints 7 and
   keeps running — a bare PowerShell `exit` killed the whole shell. At
   `_subshellDepth > 0`, `exit N` emits `$global:LASTEXITCODE = N; return`, and
   the subshell scope (below) supplies the `& { }` script block that `return`
   returns from. `$( … )` / `<( … )` bodies are subshells too (`EmitCaptured`
   raises the depth): `v=$(exit 4); echo $?` prints 4.

4. **A subshell changes nothing in its parent.** bash forks; ps-bash runs the body
   in the same runspace, so every `( … )` — and every `$( … )` whose body could
   change shell state — is `PsBuild.ShellStateScope`:
   `& { $__psbash_ss = [PsBash.Cmdlets.BashShellState]::Save(…); try { BODY } finally { Restore; … } }`.
   The child scope isolates PS-scoped state (arrays, functions, `set -e`'s
   `$ErrorActionPreference`, `set -u`'s StrictMode); `BashShellState` saves and restores
   what a scope does not: bash variables (through `BashVariableStore`), the `$global:`
   shell state (errexit, `$-`, positionals, `BASH_REMATCH`, traps, `$!`), the `shopt`
   table, and `set -x` (session-wide `Set-PSDebug`, tracked by `$global:__BashXtrace`).
   `$?` is not restored — the subshell's status is the parent's `$?`. The cwd pop stays
   last in the `finally`. A `$( … )` keeps the cheap unwrapped form only when every
   pipeline stage is a mapped, inert `Invoke-Bash*` command (`CannotChangeShellState`):
   an unmapped word may be a function. A `$( … )` body with its own `set -e` starts with
   errexit OFF (bash does not inherit it) and gets checks for its body.

## 8. Anti-patterns

Historical emitter implementations translated bash flags into PowerShell named
parameters. This caused persistent bugs because the emitter's translation logic
diverged from what the runtime `Invoke-Bash*` functions actually expected.

Examples of what went wrong:

| Command | Old (broken) emitter output | Problem |
|---------|---------------------------|---------|
| `head -n 5` | `Select-Object -First 5` | Bypassed the runtime entirely; `Invoke-BashHead` was never called. Broke when head had to handle `-c` or combined flags. |
| `cut -d, -f1` | `Invoke-BashCut -Delimiter , -Field 1` | Emitter parsed `-d` and `-f` and re-emitted them as named PS params. When the runtime changed its param names or parsing, emitter had to change too. Two parsers doing the same work. |
| `wc -l` | `Measure-Object -Line \| Select-Object -ExpandProperty Lines` | Completely native PS translation; no runtime function involved. Could not handle `wc -w`, `wc -c`, or bare `wc`. |
| `sort -r` | `Invoke-BashSort -r` | Partial extraction -- only `-r` was recognized. Other sort flags (`-n`, `-k`, `-t`) were silently dropped. |

The fix in every case was the same: **stop translating flags in the emitter,
forward everything to the runtime function, and let the runtime handle it.**

The passthrough pattern (`EmitPassthrough`) was introduced to enforce this
discipline. Adding a new mapped command requires only a switch case and a single
`EmitPassthrough` call -- no flag parsing logic in the emitter.

---

## 9. Hooks

The emitter translates certain bash constructs that affect prompt and directory-change
behavior into ps-bash hook registrations rather than direct PowerShell equivalents.

### 9.1 PROMPT_COMMAND

A bash assignment `PROMPT_COMMAND="cmd"` is emitted as:

```powershell
Register-BashPromptHook -Name 'main' -ScriptBlock { cmd }
```

Subsequent `PROMPT_COMMAND+="other"` appends produce additional hooks with
auto-generated names (`'main_2'`, `'main_3'`, etc.) to preserve additive semantics.

### 9.2 set -e / set -x / set -u

These are handled inside `EmitSimple` (Section 4) and are not hook-related, but they
interact with hook execution:

- `set -e` installs `$global:__BashErrexit = $true`. Hook scriptblocks are invoked
  outside the errexit guard; exceptions are caught by the hook runner (see Section 5.5
  of `on-cd-hooks.md`).
- `set -x` (`Set-PSDebug -Trace 1`) traces PowerShell statements, including hook
  invocations, to the debug stream.
- Unsupported options (including noexec and noglob), dynamic options
  and unsupported combined forms are rejected before execution. Use separate supported
  `set` commands and `set -- ...` for positional parameters. `pipefail` remains the
  documented no-op exclusion.

### 9.3 trap ERR

`trap '...' ERR` is emitted as `$global:__BashTrapERR = { ... }` and fired by
`InvokeBashEvalCommand` when a command returns non-zero. It is **not** managed by the
hook registry and does not interact with `Register-BashPromptHook` or
`Register-BashChpwdHook`.

### 9.5 trap EXIT

`trap '...' EXIT` is emitted as `$global:__BashTrapEXIT = { ... }`. `BashTranspiler`
wraps any script whose source registers an EXIT (or ERR) trap in a
`try { … } finally { … & $global:__BashTrapEXIT … }` epilogue, so the handler fires
on normal end, on `exit N`, and under errexit — PowerShell runs `finally` in all
three cases. Scripts with no such trap keep the historical bare emission.
`InvokeBashEvalCommand` no longer fires EXIT itself (the transpiled epilogue owns it)
and fires only ERR.

### 9.4 trap DEBUG

In a script that runs other commands, `trap '...' DEBUG` runs the action before each simple command
after it (`PsEmitter.DebugLead`, gated on `PsEmitter.SetsDebugTrapForOtherCommands`: any command besides the trap,
on any line). A lone `trap '...' DEBUG` (an interactive prompt hook) is mapped to `Register-BashChpwdHook`; see
`on-cd-hooks.md` Section 15.1.

For full hook semantics, firing model, coexistence with PSReadLine/oh-my-posh/Starship,
and known gaps, see [`on-cd-hooks.md`](./on-cd-hooks.md).
