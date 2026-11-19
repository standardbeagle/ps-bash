# Known Issues

Open bugs and gaps in ps-bash v0.15.0, each checked against the code, the specs or bash 5.2.
Deliberate differences from bash (platform limits, refused options) are listed separately in
[Intentional Differences](specs/intentional-differences.md); the ones scripts hit most often are
repeated here, under their area. Check both pages before you report a bug.

## Pipelines

- **Output of a streaming child waits for an idle upstream stage.**
  `sleep 5 | bash -c 'echo hi'` prints `hi` after 5 s; bash prints it at once. A nested shell's
  output is emitted when the pipeline thread runs its stage, which happens when an input record
  arrives or the upstream ends. The output is complete and in order, only late.
  Spec: `stdio-messaging.md` §4.6.
- **No SIGPIPE.** A producer whose reader exits early is stopped, but it is not killed by a
  signal and its status is not 141: `yes | head -n 1; echo ${PIPESTATUS[*]}` prints `141 0` in
  bash. `PIPESTATUS` and `pipefail` are otherwise implemented. Two spec cases stay XFAIL for
  this (`wontfix-sigpipe`).
- **A stage stopped by `head` reports 0 when `head` writes to the script's stdout.**
  `nosuchcmd 2>&1 | head -1; echo ${PIPESTATUS[0]}` prints `0`; bash prints `127`. With
  `head`'s output redirected (`| head -1 >/dev/null`), the status is 127.
- **A bare `exit` in a pipe stage exits 0.** `false; { exit; } | cat; echo ${PIPESTATUS[*]}`
  prints `0 0`; bash prints `1 0` (the status before the pipeline). `exit N` is correct.
- **`f 2>&1` where the function `f` runs a program.** The program keeps separate stdout and
  stderr pipes, so lines it writes to both can change order. A program run directly under
  `2>&1`, `|&` or `&>` keeps its order.
- **`NAME=value cmd` as a pipe stage collects its input first.** The stage starts after its
  upstream completes, so an unbounded producer (`yes | FOO=1 head -n1`) does not stream into it.

## Redirection

- **`exec` on stdin, stdout or stderr is partly supported.** `exec > log; rest` redirects the
  statements that follow it in the same list. At the top level of a script that is the rest
  of the script, as in bash. Inside a compound command or a function it differs:
  - As the last statement of a compound command or function body, it is refused. The shell
    prints `exec: N: redirecting the shell's own stdin/stdout/stderr with exec is not supported`,
    applies no redirection and goes on. Example: `if $quiet; then exec 2>/dev/null; fi`.
    Put the commands to redirect in a group instead: `{ cmds; } 2>/dev/null`.
  - Followed by other statements in a compound command or function body, it covers only
    those statements. Bash keeps the redirection after the block or function ends.
  - An interactive line that holds only the `exec`, and `exec 1>&-`, are refused the same way.

  `exec` on descriptors 3 and up (`exec 3>f`, `exec {fd}>f`, `exec 3>&1`, `exec 3>&-`, and
  the read-write `exec 5<>f`) works.
- **`/dev/stdin` as a file operand.** Only `cat` and awk's `getline < "/dev/stdin"` read it.
  `head`, `tail`, `sort`, `grep`, `sed`, `cut`, `wc` and the other line tools report that it
  cannot be opened; use `-` or a pipe. `cp`, `touch`, `stat` and `source` do not accept a
  device operand (`cp f /dev/null`).
- **A native program can miss the tail of a `> file` burst.** Output written faster than one
  record per millisecond is buffered for up to 50 ms. A ps-bash command that reads the file
  sees all of it; a native program or a size check (`test -s`, `stat`, `ls -l`) run right
  after the burst, inside the same redirected group, may not.
- **A native program's stdout is line-decoded.** PowerShell drops a CR before an LF and adds
  a missing final newline, so `ssh host cat f > f` is not byte-exact for such a file. Its
  stdin is exact.

## Processes and environment

- **A program started as a plain `prog args` inherits unexported variables.** Nested shells,
  programs under `2>&1` / `|&` / `&>`, and the programs `timeout`, `nohup` and awk `system()`
  start get exported variables only; `env`, `printenv` and `compgen -e` list only exported
  names. A plain `x=1; prog` with no stderr merge, in any position (also `env prog`,
  `command prog`, `xargs prog`, `time prog`), is started by PowerShell, which hands it every
  shell variable. The same program is the one process killed on a stop; its children are not.
  Spec: `environment-model.md` §7.
- **`.cmd` and `.bat` in a background job** get the host process environment in place of the
  job's copy.
- **Foreground `nohup CMD` returns 0 at once.** GNU nohup waits and returns CMD's status.
  `nohup CMD &` is unaffected.
- **A nested shell is slow to start.** Each `bash -c`, `sh -c` and `#!` shell script runs in a
  ps-bash of its own: about 1 s on a warm host, more for the first one on a cold host. A loop
  that starts hundreds takes minutes. Spec: `executor-slots.md` §5.

## Signals and background jobs

- **A trapped signal runs at the next top-level statement.** A signal that arrives during one
  long command, or inside a `( )` or `$( )` body, waits for it to finish. With no trap, STOP,
  TSTP, TTIN, TTOU and CONT are ignored: a host command cannot be suspended.
- **A job starts about 13 ms after its `&`** (median on Windows; 10-53 ms under load); bash
  forks at once. A `sleep` in the job is shortened by that lag, so longer sleeps keep bash's
  order. Jobs that differ by less than the lag can finish in another order than in bash. Two
  spec cases stay XFAIL for this (`arch-bg-job-start-latency`), one of them `wait -n`.
- **Some job output is still printed at `wait`.** A job streams its output as it runs when
  its stdout is the shell's own. A job started inside a function, a redirected group, a
  pipeline, `$( )`, `<( )`, `eval`, `source`, a trap, or after `exec > file` is collected and
  printed in one piece at `wait` or at exit.
- **`-c` and jobs still running at the end.** After the script ends, jobs get 1 s; then they
  are stopped and output they had not delivered is dropped. Use `wait`, or `nohup cmd &` for a
  process that must outlive the shell.
- **Ctrl+C ends a program's whole process tree,** including a child its script started with
  `&`. Bash leaves that child running. A `setsid` child survives in both. A plain `prog args`
  is the exception: only that process is stopped (see above).

## Shell language

- **`select` and `coproc` are not supported.** Each prints `bash: select: not supported by
  ps-bash` (or `coproc`) with status 2, and the rest of the script runs. This is by design.
- **`BASH_ARGV`, `BASH_ARGC`, `BASH_COMMAND` and `HISTCMD` are unset.**
- **`declare -A m=([a]b]=3)` sets the key `a]b` to `3`.** Bash takes the whole word
  `[a]b]=3` as a key with an empty value.
- **`caller` prints nothing and returns 1, inside a function too.** Bash prints the calling
  line and file (`0 NULL` at the top level of a script file).
- **A sourced file that turns on `pipefail` does not turn it on for the caller,** unless the
  calling script has a `set` or `shopt` command of its own that names `pipefail`. The word in
  a comment or a quoted string does not count.
- **`help NAME` prints the synopsis and the one-line description only,** for 61 builtins. Bash
  goes on with the long text.
- **The report for a killed job is shorter.** After `sleep 30 & kill -9 $!; wait $!`, ps-bash
  prints `bash: 1000 Killed`. Bash prints `bash: line 1: <pid> Killed  sleep 30`. The
  status, 137, is the same.
- **`< empty-file wc` pads its counts as for a pipe** (`      0       0       0`). Bash's wc
  sees a regular file and prints `0 0 0`.
- **`nice` does not exist.** `nice cmd` is "command not found" (127).

## Commands

- **`date` ignores `$TZ`.** `TZ=UTC date` prints local time. Use `date -u`.
- **`gzip -l` does not list piped input.** `gzip -l < f.gz` compresses the stream. GNU lists
  it under the name `stdout`. Name the file: `gzip -l f.gz`.
- **`mktemp` with no TEMPLATE, or with `-t` or `--tmpdir` and no DIR, uses ps-bash's temp
  folder** (`…/Temp/ps-bash/proc-sub` on Windows), not `/tmp`, when `TMPDIR` is not exported.
- **`install -v` reports a replaced destination as `install: swapped 'DEST' -> 'DEST.old'`**
  with full paths. GNU prints `removed 'DEST'`. The other `-v` lines match GNU.
- **`tput setaf N` on Windows prints the 256-color form (`ESC[38;5;Nm`) for colors 0-15.**
  ncurses prints `ESC[3Nm` and `ESC[9Nm` there. Both select the same color.
- **`timeout -s SIG` and `-k DUR` are checked but have no effect.** The command is stopped the
  same way for every signal, and the status is 124 where GNU's `timeout -s KILL` gives 137.
- **Some single-letter flags are taken by PowerShell.** `ping -W SECS`, `uname -o`, `uname -p`,
  `uname -i` and `shuf -o FILE` print `Parameter cannot be processed because the parameter
  name 'W' is ambiguous` (naming the letter), do nothing, and leave status 0. `uname -v` is
  ignored.
- **`env -C DIR pwd` prints the shell's directory, not DIR.** Programs and `ls` run in DIR
  (`env -C DIR sh -c pwd` prints DIR).

## macOS

- **An outside SIGUSR1 does not reach `trap … USR1`.** The .NET runtime uses SIGUSR1 to
  suspend its own threads on macOS, so ps-bash does not catch it. `kill -USR1` sent from a
  ps-bash shell is delivered, and SIGUSR2 works from anywhere.
- **`PsBash.Cmdlets.Tests` is advisory in the macOS release gate.** Under the full suite's
  load, an awk output pipe (`print | "wc -l"`) lost its child twice (broken pipe, once exit
  138) and a job-clock sleep bound failed. Each test passes when run alone on macOS. Windows
  and Linux runs of the suite, and every other macOS suite, block a release.
- **A raw byte in a variable** (`x=$(printf '\xe9')`) becomes U+FFFD on Linux and macOS;
  Windows keeps it.

## Windows

- **Variable names are case-insensitive.** `x=1; X=2` is one variable.
- **`/` is `C:/` and `/tmp` is `%TEMP%`.** An absolute path a script prints starts with a
  drive letter. `PATH` is `;`-separated: a `:` written between entries becomes `;`.
- **No mode bits.** `chmod +x` succeeds and changes nothing; `ls -l` shows synthesized modes.
- **`<(cmd)` is a temp file** (`C:/…/psub-…`) where bash gives `/dev/fd/63`, and it holds the whole
  output of cmd before the reader starts.

## Interactive shell

`bind` changes nothing (it warns `line editing not enabled`), there is no `set -m` job
control, and the prompt escapes `\j`, `\!` and `\#` are constants.
See [Using ps-bash](shell-guide.md#limitations).

## Fixed in v0.15.0

Entries this page or earlier releases listed as open:

- `PIPESTATUS` is set after every pipeline and reset by a simple command or an assignment.
- `set -e` ends a failing pipe stage; it used to run to its end.
- `exit` in a function called from a pipe stage, `( )` or `$( )` ended the whole script.
- `kill -USR1 $$` from a nested shell was refused; it now runs the trap.
- A nested shell, or a program started with `2>&1`, saw every shell variable.
- `--rcfile` was parsed and dropped.
- Background job output was held until `wait`.
- `kill -FOO PID` sent TERM; an unknown signal is now an error.
- `expr 1 + 2 + 3` printed `3`; `expr` now chains and follows GNU's precedence.
- `date -I`, `-R` and `--rfc-3339` were ignored and the default format was printed.
- `tee`, `ping`, `tput`, `shuf` and `id` exited 0 after a failure.
- `uname` printed `MINGW64_NT-…` on Linux and macOS.
- `mktemp` ignored `$TMPDIR` and a template's directory.
- `rmdir -p a/b` went on to remove empty directories above `a`.
- `rg` printed `\` in the paths it found on Windows.
- `c=eval; $c 'echo x'`, `"$@"` and `command NAME` could not run a builtin (127).
- The quoted word `pipefail` in a script (`grep -c pipefail f && echo y`) broke the parse.
- `PPID`, `SHLVL`, `GROUPS`, `DIRSTACK`, `BASH_ALIASES`, `SRANDOM` and `BASH_SUBSHELL` were
  unset, and `$IFS` read empty before it was assigned.
- `exec 5<>file` was a parse error.
- `( trap 'echo err' ERR; false )` did not run the trap, and neither did
  `trap 'echo D' DEBUG; echo a` on one line.
- `nosuchcmd 2>&1 | head -1 >/dev/null` left 0 in `PIPESTATUS[0]`; it is 127. One case is
  still open (see Pipelines).

## Production review status

The [2026-10-18](production-readiness-review-2026-10-18.md) and
[2026-10-20](production-readiness-review-2026-10-20.md) reviews record evidence and closure
criteria. Status of their findings:

- **Fixed: archive extraction (B01).** `tar -x` refuses to write through an existing symlink
  or junction ancestor or final component (nonzero exit, outside content untouched).
  Residual: a link swapped in between the check and the open by a concurrent attacker is
  narrowed, not eliminated. Extract untrusted archives into a fresh directory with trusted
  ancestors.
- **Fixed: installers (B02).** `install.sh` and `install.ps1` install the complete package
  into a versioned directory, run a real command before switching, verify the published
  digest, and keep the previous version. Unsupported platforms fail with a named error.
- **Fixed: release integrity (B03-B05).** Binaries are staged as workflow artifacts and
  promoted only after every gate passes; every job builds the tag's single resolved SHA.
- **Partly fixed: history privacy (B08).** A line starting with a space, or any line while
  `PSBASH_HISTORY_PRIVATE=1`, is never recorded; an unopenable history DB degrades to
  in-memory with a warning. `history -d ID` and `history -c` delete (`-c` runs
  `secure_delete`, a WAL truncate and VACUUM). Still open: a retention policy.
- **Partly fixed: durable sessions (B06, B07).** `ps-bash session start|list|status|stop NAME`,
  `PSBASH_SESSION=NAME` and `session attach` give a host and a terminal that outlive their
  launchers ([Named sessions](sessions.md)). Commands inside one host still run one at a time.
- **Gzip and tar** reject a stream with a missing footer in every mode. Gzip keeps an existing
  output file unless forced.
- **`set -u`** covers `$x`, `${x}`, `$1`..`$N` and names read inside `$(( ))`. **`set -C`**
  covers `>` onto an existing file; the check runs when the redirect opens the file, so the
  command itself has already run.
