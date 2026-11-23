# Intentional Differences from bash / GNU

ps-bash aims for byte-exact bash 5.2 + GNU coreutils 9.4 behaviour, checked by the bash-oracle
differential suite. This page lists every place it deliberately does **not** match, and why, so a
divergence found in the wild can be checked here before it is reported as a bug.

Three kinds of entry:

- **Platform** — Windows has no equivalent of a Unix concept; ps-bash maps it as closely as it can.
- **Architecture** — follows from running on a PowerShell host with an object pipeline.
- **Not implemented** — a real option ps-bash refuses with **exit 2** (`option '-x' is recognized
  but not supported by ps-bash`), so a script fails loudly instead of getting silently wrong output.
  Exit 2 is reserved for this; a usage error uses the tool's own status (usually 1).

Per-command detail lives in [runtime-command-reference.md](runtime-command-reference.md); the raw-byte
model in [runtime-functions.md](runtime-functions.md) "Raw bytes".

## Platform (Windows)

| Area | ps-bash on Windows | Why |
|---|---|---|
| Permission bits (`ls -l`, `stat`, `find -perm`, `%m`/`%M`) | Synthesised: 0644 files, 0755 directories and executable extensions, read-only clears the write bits, 0777 links | NTFS has ACLs, not mode bits |
| `chmod`, `mkdir -m`, `cp` mode preservation | Only the owner-write bit maps (to the read-only attribute); `chmod +x` and every other bit succeed and change nothing; `chmod -v` reports the stand-in modes 0644 / 0755 (0444 / 0555 read-only) | Same |
| Owner / group (`ls -l`, `find -user`, `%u`/`%g`, `ls -n`) | Owner and primary-group SID; name without domain; numeric id = the SID's last sub-authority; `-nouser` = untranslatable SID | No uid/gid |
| Inode, link count, ctime | NTFS file index, `nNumberOfLinks` (a directory shows 1, Linux shows 2 + subdirs), NTFS ChangeTime | Closest NTFS equivalents |
| `ls -s`, `find %k`/`%b` | Whole 4 KiB allocation units | Real figure is filesystem-specific |
| `-readable` / `-writable` / `-executable` | Read-only attribute + executable-extension heuristic, not `access(2)` | No `access(2)` |
| `find -newerB*` | Works (birth time exists on NTFS) | GNU on ext4 refuses it |
| `tail -f` (descriptor mode) | Follows by path, not by open handle | A Windows file cannot be renamed while open |
| `tail --pid` | Liveness via `Process.GetProcessById` | No `kill(pid, 0)` |
| Symlink tests | Need the Windows symlink privilege; tests skip without it | OS policy |
| `mkdir -Z`, `cp -Z`/`--context`, `find -context` | Refused | No SELinux |
| `find -used` | Refused | No reliable access-vs-change data |
| `date +%N`, `date -d @S.FFFFFFFFF` | Sub-second resolution is 100 ns: `%N`'s last two digits are always `0`, and an input fraction past 7 digits is truncated | .NET time is 100 ns ticks |
| `mv` / `cp` onto a file in use | Replaced like Linux: a read-only or in-use destination (a running `.exe`) is renamed aside and the new file takes its name. An old program that is still running cannot be deleted, so it stays as a hidden `.NAME.psbash-replaced-XXXXXXXX` beside it until the next replace in that directory after it exits. A file a process holds open **without delete sharing** cannot be renamed or replaced at all: `Device or resource busy (open in NAME.exe, pid N)`, reported at once with nothing changed. A lock with no process to blame (antivirus, indexer) is retried for about a second (`FileTransfer`) | Windows refuses to overwrite or rename open files; Linux unlinks and keeps the old inode alive |
| Absolute paths (`$PWD`, `$OLDPWD`, `~`/`$HOME`, `pwd`, `dirs`, `mktemp`, `realpath`, `readlink`, `type`/`which`/`command -v`, `$0`, globs, file names in output) | **Windows:** forward slashes with the drive kept: `C:/Users/x`, UNC `//server/share/x`, never `/c/Users/x`. `/` is `C:/` and `/tmp` the Windows temp directory (`%TEMP%`). Input accepts `C:/x`, `C:\x`, `/c/x`, `/mnt/c/x` (`PSBASH_UNIX_PATHS=1` maps other `/x` forms). `PATH` keeps the Windows `;` form. A native program's own output is not rewritten. See `paths.md` | The filesystem has drive letters. Spec XFAILs caused only by this carry the id `platform-windows-paths` |
| `uname` on Windows | The MSYS identity: `MINGW64_NT-<version>` for `-s`, `-a` ending `MINGW64` (Linux and macOS report the host kernel, `uname(2)`) | Windows has no `uname(2)`, and scripts written for Git Bash branch on `MINGW*` to find Windows |
| Variable names differing only in case (`x=1; X=2`) | **Windows:** one variable; `$x` and `$X` read the same value (Linux/macOS: two). **Arrays, every OS:** one (`ta=(1); tA=(2)` leaves one array) | Bash scalars are process environment variables (`BashVariableStore`), and the Windows environment is case-insensitive and inherited by children as such. Case-sensitive names would need a store off the process environment. Arrays are PowerShell variables (`environment-model.md` §8), whose names are case-insensitive everywhere. Exception: a non-upper-case spelling of a reserved name (next row). Spec XFAILs caused only by this carry the id `platform-windows-env-case` |
| A reserved name spelled in lower or mixed case (`path`, `tmp`, `Temp`, `home`, `os`, `lines`, ...) | **Windows:** its own variable, as in bash: `path=abc` leaves `PATH` and command lookup alone, `tmp=x` leaves temp files where they were. Exported, a child sees it under its store name `__psbash_cv_<mask>_<NAME>` (a nested ps-bash reads it back as `path`; another program does not see `path`) | The Windows environment cannot hold `path` beside `PATH`. The name is stored mangled (`BashNameCase`, `environment-model.md` §12); a fixed list, so the emitted text does not depend on the machine. Windows' own spellings follow Git Bash (MSYS2): `$windir`/`$Path` are unset and listings show `WINDIR=`/`PATH=`; `ProgramData` and `PSModulePath` keep their native spelling and the system value |
| File names | **Windows:** a name cannot contain `" * ? \| < > :` (`touch '"q"'` fails), a trailing dot or space is stripped (`touch foo.` makes `foo`), and names are case-insensitive (`touch a A` makes one file; `[!ab]*` against `A` / `a`) | NTFS / Win32 naming rules. Spec XFAILs caused only by this carry the id `platform-windows-filenames` |
| `~user` | **Windows:** `~root` (any user other than the current one) stays literal. **Linux/macOS:** resolved from `/etc/passwd` only, so an account known only to a network directory (LDAP, sssd, macOS Directory Services) stays literal | No passwd database to look a home directory up in (`Platform/UserHome.cs`). Spec XFAILs caused only by this carry the id `platform-windows-tilde-user` |
| Unexported variable and a program started without a stderr merge (`x=1; prog`) | The program's environment has `x`. Nested shells (`sh -c`, `bash`), programs started with `2>&1` / `&>` / `\|&`, `env`, `printenv`, `compgen -e`, `declare -p` and every other child see exported variables only, as in bash | PowerShell's native-command processor hands a program the whole process environment, where every shell variable lives. See `environment-model.md` §7 |
| `PATHEXT` and other Windows variables | Present in the environment, so a prefix listing (`compgen -v P`, `compgen -e P`) includes them | Windows defines them |
| Raw byte in a variable on **Linux/macOS** | `x=$(printf '\xe9')` loses the byte (becomes U+FFFD) | Variables live in the process environment, and .NET re-encodes it on Unix; Windows keeps it |
| `trap … USR1` and a SIGUSR1 from outside ps-bash on **macOS** | Not delivered to the trap (`kill -USR1` from a ps-bash shell is); SIGUSR2 is | The .NET runtime uses SIGUSR1 to suspend its own threads for every GC on macOS (no real-time signals there); catching it turned each GC into a trap delivery (exit 138). `docs/specs/platform-dependencies.md` §Signals |

## Architecture

| Area | Behaviour | Why |
|---|---|---|
| Raw bytes as text | Invalid UTF-8 travels as markers U+DC80..U+DCFF; regex `.`, `${#x}`, `printf '%5s'` count a marker as one character (GNU in a UTF-8 locale would not match it) | The pipeline is .NET strings; see "Raw bytes" |
| Native child argv / env, file names | Cannot carry an invalid byte | The OS passes UTF-16 (Windows) or re-encodes (Unix) |
| `{ head -n1; cat; } < file` | Prints `1`; bash prints `1 2 3` | GNU `head` seeks a regular file back; ps-bash models a pipe, where the two agree |
| `cmd \| { head -n1; }` | The compound reads lazily only for a literal `yes`/`seq` producer; other producers finish before the compound reads | PowerShell runs a script block after its upstream completes |
| Shared stdin and natives | A native program gets the shared stdin (forwarded launcher stdin or a compound's) as its real process stdin, an OS pipe a pump fills; what it did not read is put back, so `prog; cat` behaves like bash. A function or alias of the same name gets nothing | Natives run under PowerShell, whose `feed \| prog` cannot end before its feed does |
| Native program's stdout | The OUTPUT of a native program is line-decoded by PowerShell before ps-bash sees it: a CR before an LF is dropped and a missing final newline is added, in a pipe, `>` / `>>` / `tee` and `$(…)` (`ssh host cat f > f` of a file with CRLFs or no final newline is not byte-exact; invalid UTF-8 and NUL survive). Its STDIN is exact: a pipe, `< file`, heredoc and here-string reach a program as the bytes bash would write (`ConvertTo-BashNativeStdin`, `Invoke-BashCat -RawStdinBytes`) | Byte-exact stdout needs ps-bash to spawn the program itself instead of PowerShell's native pipeline; tracked by the skipped cases of `NativeStdioBytesTests` |
| `cat FILE` text files | A file OPERAND of `cat` and the line tools is read CRLF- and BOM-transparently (`cat crlf.txt \| wc -c` is one byte short per CR), and non-UTF-8 text decodes lossily unless the file looks binary. A `< file` redirect is exact | Long-standing line-tool policy (`BashFileSystem`); only stdin is a byte stream |
| Stdin forwarding | Forwarded into `-c` commands; not into piped scripts, script files or the interactive shell | Those already read stdin as the script / keyboard |
| When `> f` output reaches the file | A record that comes 1 ms or more after the previous one (or is the first) is written at once; records in a faster burst (`seq 1000000 > f`) are buffered and written within 50 ms of going idle, and before any ps-bash command opens a file to read (`{ echo a; echo b; cat f; } > f` sees both lines, as in bash). A native program or a size check (`test -s`, `stat`, `ls -l`) run right after a burst in the same redirected group can miss up to 50 ms of its tail; bash has written every finished command's output | PowerShell runs the producer on as soon as a record is written, so the redirect cannot see where one command ends; a write per record would slow big outputs (`RedirectFileWriter`) |
| Process substitution word | `<(cmd)` / `>(cmd)` expand to a temp file (`C:/Users/x/AppData/Local/Temp/ps-bash/proc-sub/psub-…`, a shell path like every other: `paths.md`), not `/dev/fd/63`; `<(cmd)` holds cmd's whole output before the reader starts | PowerShell cannot hand a child an inherited pipe descriptor by number; the file is written as cmd streams (`ProcessSubFileWriter`). Spec case `ProcessSub_TempFile_WcSingleLargeInput` stays XFAIL for the name |
| `tail -f` in a fused pipeline | Never fuses | Fused frames are cut by size; flushing on idle needs a timer thread |
| `grep -r`, `rg` (internal engine) | Prune `.git`, `node_modules`, `bin`, `obj`, … before descending, and skip a WALKED binary file silently — no `binary file matches`, no `-c`/`-l` line (`PSBASH_SEARCH_NO_IGNORE=1` or `-a` disables both; a binary file named as an operand is reported as GNU does) | Keeps big trees under the host idle timeout; `rg` approximates gitignore this way |
| Direct PowerShell calls | A bare multi-letter bundle that prefixes a common parameter (`Invoke-BashMkdir -pv d`) or a repeated bare `-e` must be quoted | The PowerShell binder sees it first; transpiled bash is unaffected |
| `--sort` tuning (`sort -S -T --parallel …`) | Accepted and ignored | They change how GNU sorts, not what it prints; nothing spills |
| `cp --sparse`, `--reflink=auto` | Accepted; a normal copy | No sparse/reflink control from .NET |
| Exit status 2 | Always "ps-bash refuses this real option" | Lets scripts tell "unsupported" from "you typed it wrong" |
| Syntax error message | Same status (2), and the same lines run before it, but one line in the parser's words: `ps-bash: parse error: Unexpected token 'then' (Word) at line 2, col 4`. In a backtick body (parsed when it runs, as bash does) the prefix is bash's, `t.sh: command substitution: line N:`, with the parser's words after it. bash writes ``t.sh: line 2: syntax error near unexpected token `then'`` and then the source line | The parser is a recursive-descent transpiler front end, not bash's yacc grammar, so its error states don't map one to one onto bash's messages |
| Nesting depth | More than 1000 levels of `$( )`, `${x:-…}`, compound commands (`if`/`while`/`( )`/`{ }`), or ~333 of arithmetic / `test` parentheses is a parse error (`nesting too deep`); bash recurses until it runs out of stack | The transpiler and evaluators run inside the shared host; a .NET stack overflow cannot be caught and killed every session on it (`NestingGuard`) |
| Brace expansion size | A word expanding to more than 1,000,000 words (`{a,b}`×30, `{1..2000000}`) is a parse error | Expansion happens at transpile time in the launcher or host; the full product exhausted memory |
| Regex match time | One regex match running longer than 15 s fails the command (`PSBASH_REGEX_TIMEOUT_SECS`, `0` = off); GNU tools never backtrack | .NET's engine backtracks (`(a+)+$` is exponential) and a running match cannot be stopped, so it held the host's execution lease forever |
| `[[ s =~ re ]]` size limits | A regex nested more than 1000 groups deep, one whose expanded tree passes 400,000 nodes (`(a{1000}){1000}`), or a match past the engine's work budget (back-references on a long subject) is status 2, the regcomp-error status; bash would recurse, allocate or search on. Escaped non-ASCII under `nocasematch` folds approximately | The POSIX ERE engine (`BashEre`, regex-ere.md) runs in the shared host; every cost is bounded so no regex can hang or crash it |
| A command that cannot be stopped | If a command whose launcher left has not stopped 20 s later (`PSBASH_STUCK_GRACE_SECS`), the shared host logs why and exits; launchers then start a fresh one | A host runs one command at a time (its execution lease); a stuck one would otherwise hang every later command on the host |
| Signal to a ps-bash shell (`sh -c "kill -USR1 $$"`) | Delivered as a message to the script that host runs, not as an OS signal (background-jobs.md §13). Its trap runs or it ends with 128+N, as in bash. A trapped signal runs at the next top-level statement boundary: a signal that arrives during a `( )`/`$( )` body or one long command waits for it to finish. The stop family (STOP, TSTP, TTIN, TTOU) and CONT are ignored instead of suspending the script. A host that cannot be reached (an older host, no sidecar) gets `kill: (PID) - signal not delivered` and is not killed | `$$` is a shared host process serving every script of its session, so an OS kill ended all of them. A host command cannot be suspended |
| `SIGPIPE`: a writer whose reader left (`yes \| head -1`) | The pipeline ends and prints the same output, but the writer's status is 0: `${PIPESTATUS[@]}` is `0 0` where bash has `141 0`. No stage is ever "killed by SIGPIPE" | A stage is a PowerShell pipeline stage in the host, not a process with a pipe: when the reader finishes, the upstream is stopped by the pipeline, and there is no signal to report. 141 is deliberately not synthesized (`compiled-plan-lane.md` §4); spec cases are marked `wontfix-sigpipe` |
| Signal numbers on macOS / BSD | A script sees bash's Linux numbering on every OS: `kill -l`, `trap -l`, `kill -10`, a `128+N` status. `USR1` is 10 and a child killed by it reports 138 on macOS too, where macOS bash says 30 and 158 (and 10 is `SIGBUS`) | One numbering for every script; numbers are converted only at the OS boundary (`kill(2)`, a wait status) in `Platform/OsSignals.cs`. See `platform-dependencies.md` §Signals |
| `$BASHPID` in a subshell | `( )`, `$( )` and a compound pipe stage get their own `$BASHPID`, as in bash, but it is a synthetic id (odd, above every pid_max), not a process: `kill $BASHPID` inside the subshell signals the shell itself (as `kill $$` does), and `ps`/`/proc` know no such pid | Subshells run in the shared host process, not a fork |
| In-process PowerShell | PowerShell code reached from a command (`Invoke-Expression '[Environment]::Exit(3)'`) runs IN the host and can end it; the host log records the exit as `reason=none recorded` | It is code execution in the host process; only a process per command could isolate it |

## Deliberate choices

| Command | Behaviour | Why |
|---|---|---|
| `rm --no-preserve-root /` | Still refused | ps-bash's protected-path guard is unconditional |
| `split --hex-suffixes=FROM` with an `f` digit, `split -n l/N` with N > input size | Plain, correct output | GNU 9.4 produces garbage names / misbehaves there |
| `jq repeat(f)` | Matches the jq 1.7 binary (`1\|[limit(3;repeat(.*2))]` = `[2,2,2]`), not the manual | Oracle wins |
| `jq` `stderr` / `debug` | Line ends with a newline | The host terminates each record |
| awk recursion | Capped near depth 1000 with a clean exit-2 error | Protects the host's stack |
| Ctrl+C on a program whose script left `x &` running (`sh -c 'x & wait'`) | `x` is killed with the rest of the program's process tree; bash leaves it (an async child of a non-interactive shell ignores SIGINT). A `setsid` child (Windows: `CREATE_BREAKAWAY_FROM_JOB`) survives in both, and `timeout` kills `x` in both | A stop ends the whole tree by construction (Job Object / process group, `stdio-messaging.md` §4.6); Windows has no per-process signal disposition to copy |
| awk `close()` of a command with unread output | Waits up to 3 s, then reports gawk's 141 | Avoids hanging on a child that never exits |
| awk warnings | No `cmd. line:N:` prefix | Location tracking not kept |
| `rg -P`, `grep -P` | Translated to .NET regex; constructs it cannot express (`\K`, `\X`, recursion, `(?\|`, verbs) are an error, never a silent mismatch | No PCRE2 engine |
| `tree` | Follows tree(1) 2.1; checked against tree 2.1.1 | Not part of coreutils |
| Pathname expansion: collation | Matches sort in ordinal (byte / UTF-16) order, like bash in the C / C.UTF-8 locale; no `en_US` collation | One locale-independent order, the one the oracle runs |
| Pathname expansion: letter case | The matcher is case-sensitive on every OS, Windows included, unless `shopt -s nocaseglob` (`globstar` `**` and extglob words `@(…)` / `!(…)` expand as in bash) | bash's rule; the file system's case-insensitivity is not applied to a pattern |
| Extglob without `shopt -s extglob` | `@(…)`, `!(…)`, … are recognized in case patterns and `${v#pat}` even when `extglob` is off (bash: a syntax error there; inside `[[ ]]` bash accepts them too) | The transpiler parses before any `shopt` runs; a superset that only differs for scripts bash rejects |
| `${v/pat/rep}`: a negated bracket whose first member is `]` | Matches like in every other pattern context (`v=x; ${v/[!]]/X}` is `X`); bash 5.2 never matches it in the `/` operators only (`x`), while `${v#[!]]}` and `[[ x == [!]] ]]` do match | One pattern compiler for every context (`emitter-strategy.md` "Bash patterns: one compiler"); the bash behaviour looks like a parsing quirk of the `/` operand |
| `shopt -s failglob` | The failing COMMAND is aborted with `bash: no match: PAT` and status 1, and the script continues with the next statement; bash discards the whole line | Statement-level abort in a PowerShell host |
| Pathname expansion inside redirect targets, `case` words, `[[ ]]` | Not expanded (a redirect target is the literal word; bash expands it and fails `ambiguous redirect` on several matches) | Rare; not modelled |
| Command substitution glued into a longer word (`x$(cmd)y`) | Not word-split | Only a bare `$(…)` / `` `…` `` word goes through `ConvertTo-BashWords` |
| Redirect failure message (`echo > nodir/f`) | `bash: nodir/f: No such file or directory`; bash prints `script: line N: nodir/f: …` | The `NAME: line N:` prefix (`ShellDiagnostic.Prefix`) is wired for "command not found" only; the other shell diagnostics still print a fixed `bash: ` / `ps-bash: ` (`error-handling.md` §2.6). Status and filesystem state match |
| Env prefix on a pipe stage (`yes \| FOO=1 head -n1`) | The stage collects its input before running, so an unbounded producer does not stream into it | The save/set/restore wrapper is a script block, which starts after its upstream completes |
| `readonly x=v`, then `x=w` | Reports `x: readonly variable`, status 1, and the script goes on; bash (non-interactive) exits the script | The emitter cannot tell a script from the REPL, and a typo must not close an interactive shell |
| `declare -f fn`, `type fn` | Prints bash's layout (`fn () `, `{ `, body, `}`) with the body **as written** (re-indented), not re-printed from the syntax tree: `f() { a; b; }` prints `a; b` on one line where bash prints two. The text is valid bash and runs again (`eval "$(declare -f f)"`, `ssh host "$(declare -f f); f"`) | The definition carries its source text (`PsBuild.FunctionSourceMark`); a bash pretty-printer would be needed to reproduce bash's canonical layout |
| `command -v ls`, `type ls`, `which ls` (any ps-bash command) | The "path" is the name: `ls`, `ls is ls`, `type -t` = `file`; a real program prints its path | The command is the runtime's own implementation, with no file; the name still runs it |
| `PATH` on Windows | The value is `;`-separated; a `:` a script writes between entries (`PATH=_tmp:$PATH`, `export PATH="$HOME/bin:$PATH"`) becomes `;` after the assignment, so `echo "$PATH"` shows `_tmp;C:\…`. A drive colon (`C:\x`, `C:/x`) is kept | Windows command lookup reads `;`; with the `:` kept, `_tmp:C:\Program Files\x` is one entry and hides both directories |
| `type -P`/`-a` (Windows) | A PATH file runs and is listed when Windows can run it: a PATHEXT extension, or a `#!` script (run through its interpreter: `sh`/`bash` by ps-bash, others by name); any other file is "Permission denied" (126) | There is no execute bit; an extensionless file would otherwise go to the shell's "open with" dialog |
| `set -e` set by a caller of `source`/`eval`d code, or turned on only inside a sourced file | The sourced/eval'd code (or the caller's remaining lines) runs without per-statement checks; a failing function call there still counts at the caller's statement | Checks are emitted only for a script that itself contains `set -e`/`-o errexit` (`PsEmitter.EnablesErrexit`), so scripts without it emit exactly what they always did |
| `set -v` / `set -o verbose` | The option is tracked (`$-` has `v`, `$SHELLOPTS` lists `verbose`, `test -o verbose`), but input lines are not echoed to stderr | The whole script is transpiled before it runs; there is no line-at-a-time reader to echo |
| `select` menu loop, `coproc` | `bash: select: not supported by ps-bash` / `bash: coproc: ...`, status 2; the rest of the script still runs (`PsBuild.NotSupported`) | The menu loop needs a prompt-and-read loop over the shell's own stdin and `coproc` two live pipes to a background job; neither has a runtime yet. Silently dropping the loop (as before) hid it |
| `help NAME` | The synopsis and the one-line description (bash's first two lines; `-s`, `-d`, `-m` forms; a word matches as a prefix or a pattern, a pattern with bash's "Shell commands matching keyword" header); bash goes on with the long text. `help` alone lists the synopses without bash's header. Builtins only (`help if` is "no help topics") | The long help texts are bash's documentation; ps-bash carries the synopsis table (`InvokeBashHelpCommand.Topics`) |
| Windows drive paths in a glob word | A pattern spelled `/c/Users/*` (with `PSBASH_UNIX_PATHS=1`) or `C:/Users/*` is expanded with forward slashes (`C:/Users/x`), and a drive-letter prefix keeps the form it was typed | `/` is the pattern's only separator (`\` escapes) |

## Interactive shell

Spec XFAILs caused only by one of these carry the id `wontfix-interactive-<topic>` shown in the first column.

| Area | ps-bash | Why |
|---|---|---|
| Piped `bash -i` (`echo cmds \| bash -i`) | Replays the input one command per prompt: `PROMPT_COMMAND`, the `PS1`/`PS2` prompt and the echoed line on stderr, history and `!!` expansion, `${PS1}exit` at end of input. `$HISTFILE` is read and written only when the caller set it (never the launcher's default `~/.bash_history`), and an explicit `exit` ends the session without saving it | A piped session must not read or rewrite the user's own history file unless asked; the line editor (REPL) still serves a terminal, or `PSBASH_INTERACTIVE_REPL=1` |
| `bind` (`wontfix-interactive-bind`) | Every form prints `bind: warning: line editing not enabled` and returns 0, as in a non-interactive bash; `-p -P -v -V -q -s -S -x -X -r -m` list or change nothing | The line editor is ps-bash's own, not GNU readline: there are no readline functions, keymaps or variables to list or rebind |
| Prompt `\j`, `\!`, `\#` in `${v@P}` | As in bash: `\j` counts the job table, `\#` the top-level input lines read (`bash -c`: 0), `\!` the history entries under `set -o history` (environment-model.md §10). A finished job leaves the table when a builtin reports it (`jobs`, `wait`, `fg`), not at the next command boundary, so `\j` (and `jobs`) can count it until then. A script that neither expands a prompt (`@P`) nor turns history on counts nothing. An interactive shell does not count its lines: `\#` stays 1 in a piped `bash -i` prompt and in the REPL's own prompt (`InteractiveShell.ExpandPS1`, where `\!` is 1 too) | The count is one call per input line, paid only by a script that can observe it; bash reaps a finished job between commands, ps-bash when a builtin reports it |
| `fc` with an editor (`fc`, `fc -e vi [first [last]]`) | Refused after the history lookup: `fc: opening an editor is not supported`, status 1. `fc -l` and the re-run forms `fc -s [pat=rep] [cmd]` / `fc -e - ...` work as in bash. A re-run that re-runs itself (`history -s 'fc -s'; fc -s`) stops at eval's nesting bound (`fc: nesting depth limit (5) exceeded`) where bash recurses until it crashes | ps-bash runs commands in a host process with no terminal of its own to hand an editor; re-running needs none |
| `fg` | Waits for the job and prints its command as written (`fg %1`, `fg %%`, a job `kill` just signalled), as bash does, also without `set -m`; there is no terminal to hand the job | No process groups or controlling terminal (next row) |
| Process groups, sessions (`wontfix-interactive-job-control`) | No `set -m` job control; `ps -o pgid,sid,tpgid` has no Windows equivalent | Windows has Job Objects, not POSIX process groups or controlling terminals |

## Not implemented (refused, exit 2)

Real options that ps-bash rejects. Most are rarely used, need a facility .NET lacks, or need a large
subsystem; each fails loudly.

| Command | Refused |
|---|---|
| `ls` | quoting styles (`-Q -b -N -q --quoting-style --literal --show-control-chars`), `--time-style=+FORMAT`, `--sort=version\|width`, `-D -f -k -L -H -Z --author --block-size --si --dired --hyperlink --indicator-style --file-type --zero --dereference*` |
| `ls` output | never treated as a terminal (default width 80); `--full-time` shows .NET's 100 ns ticks |
| `sort` | `--debug` |
| `head -z`, `tail -z`, `uniq -z`, `sort -z`, `comm`, `join` | implemented, but stdin is buffered rather than streamed |
| `tac`, `nl`, `paste`, `fold`, `expand`, `unexpand`, `strings` | `tac -b/-r`, `nl -b pREGEX -d -f -h -l -p`, and the remaining refused options of these tools |
| `split` | `-C -t -e -u --filter --verbose` |
| `du` | `-B -D -H -L -P -S -t -X -0 --si --time --time-style --inodes --files0-from` |
| `stat` | `-L`, `-f` |
| `column` | `-n -N -O -C -E -m -H -R -T -W -J -r -i -p` |
| `file` | `-z -Z -s -k -r -c -C -d -E -l -m -e -P --extension`; no zip-subtype (EPUB/ODF/OOXML/APK), Exif, or source-code (C/JSON/XML/HTML) detection |
| `rg` | gitignore, preprocessor and tuning options: `--pre`, `--pre-glob`, `--ignore-file`, `--no-ignore-*`, `--max-filesize`, `--null-data`, `--crlf`, `--engine`, `--one-file-system`, `--context-separator`, `--path-separator`, `--max-columns`, `-M`, `--debug`, `--trace`; `-z` decodes `.gz` only |
| `mv` | `--no-copy`, `--strip-trailing-slashes`, `--debug` |
| `ln` | — (no "same file" refusal when the name already links to the target) |
| `tar` | bzip2 / xz / lzma / zstd / compress (no managed codec), `--transform`, `-r -u -A --delete`, ownership/mtime/sort overrides; absolute operands are stored by basename; no 10240-byte record padding |
| `gzip` | `-Z`/`--lzw`, `-b` (no LZW codec) |
| `diff` | side-by-side `-y`, `-e/-n`, `-p/-F`, `-x/-X`, `-S`, `-I`, `--color`, `-t/-T`; ambiguous long options list candidates alphabetically |
| `jq` | `-C`, `--seq` input, modules, date builtins (`now`, `strftime`, `strptime`, `todate`, …), `$__prog_args`; syntax errors omit bison's "expecting …" list |
| `awk` | `@name()` calls user functions only |
| `find` | `-used`, `-context` (see Platform) |
| `cut`, `head -c`, … | `cut -c` counts BYTES like GNU 9.4 (a multi-byte character cut by a range leaves raw-byte markers) |

## `${x:?msg}` at the top level

An unset `${x:?msg}` outside a subshell ends the script like bash, but with exit status 1 where bash 5.2 gives 127
for a non-interactive shell, and the message is `ps-bash: x: msg` rather than `bash: line N: x: msg`. Inside a
subshell it ends only the subshell (status 1), as bash does.

## `xargs -P`, background-job isolation, `sed -i` links, literal operands

`xargs -P N` is accepted but runs serially (one single-threaded runspace), so output order equals input order. `xargs -p` runs nothing when there is no `/dev/tty` to confirm on, as GNU does.

A background job (`cmd &`) runs in-process unless it can change shell state (assignments, `cd`, state builtins, functions, loops). Those get `Invoke-BashBackground -Isolated`, which uses a private host process so the job cannot alter the parent's variables or cwd. `PSBASH_BG_MODE=inproc|process` overrides the choice.

A job's output streams live when the job's stdout is the shell's own. In these cases it is still collected and printed at `wait` (or at exit), in one piece:

- a job started inside a function body;
- a job inside a compound command with an output redirect (`{ cmd & wait; } > f`);
- a job in a multi-stage pipeline, `$( )` or `<( )`;
- a job started by `eval`, `source` or a trap;
- a job started after `exec > file`.

The emitter cannot know where such a job's output goes at run time, and a live write would bypass that redirect. In `-c` mode a job that is still running when the script ends prints live for a 1 s grace and is then stopped, as before. Later output is dropped (background-jobs.md §12).

`sed -i` replaces a symlink with a regular file unless `--follow-symlinks` is given, as GNU does. `rm`/`cp`/`mv` operands are literal: only the shell expands wildcards, so a quoted `'*.txt'` names a file called `*.txt`.
