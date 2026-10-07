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
| `mkdir -m`, `cp` mode preservation | Only the owner-write bit maps (to the read-only attribute) | Same |
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
| Raw byte in a variable on **Linux/macOS** | `x=$(printf '\xe9')` loses the byte (becomes U+FFFD) | Variables live in the process environment, and .NET re-encodes it on Unix; Windows keeps it |

## Architecture

| Area | Behaviour | Why |
|---|---|---|
| Raw bytes as text | Invalid UTF-8 travels as markers U+DC80..U+DCFF; regex `.`, `${#x}`, `printf '%5s'` count a marker as one character (GNU in a UTF-8 locale would not match it) | The pipeline is .NET strings; see "Raw bytes" |
| Native child argv / env, file names | Cannot carry an invalid byte | The OS passes UTF-16 (Windows) or re-encodes (Unix) |
| `{ head -n1; cat; } < file` | Prints `1`; bash prints `1 2 3` | GNU `head` seeks a regular file back; ps-bash models a pipe, where the two agree |
| `cmd \| { head -n1; }` | The compound reads lazily only for a literal `yes`/`seq` producer; other producers finish before the compound reads | PowerShell runs a script block after its upstream completes |
| Shared stdin and natives | A native program gets the shared stdin (forwarded launcher stdin or a compound's) as its real process stdin, an OS pipe a pump fills; what it did not read is put back, so `prog; cat` behaves like bash. A function or alias of the same name gets nothing | Natives run under PowerShell, whose `feed \| prog` cannot end before its feed does |
| Stdin forwarding | Forwarded into `-c` commands; not into piped scripts, script files or the interactive shell | Those already read stdin as the script / keyboard |
| `tail -f` in a fused pipeline | Never fuses | Fused frames are cut by size; flushing on idle needs a timer thread |
| `grep -r`, `rg` (internal engine) | Prune `.git`, `node_modules`, `bin`, `obj`, … before descending (`PSBASH_SEARCH_NO_IGNORE=1` disables) | Keeps big trees under the host idle timeout; `rg` approximates gitignore this way |
| Direct PowerShell calls | A bare multi-letter bundle that prefixes a common parameter (`Invoke-BashMkdir -pv d`) or a repeated bare `-e` must be quoted | The PowerShell binder sees it first; transpiled bash is unaffected |
| `--sort` tuning (`sort -S -T --parallel …`) | Accepted and ignored | They change how GNU sorts, not what it prints; nothing spills |
| `cp --sparse`, `--reflink=auto` | Accepted; a normal copy | No sparse/reflink control from .NET |
| Exit status 2 | Always "ps-bash refuses this real option" | Lets scripts tell "unsupported" from "you typed it wrong" |

## Deliberate choices

| Command | Behaviour | Why |
|---|---|---|
| `rm --no-preserve-root /` | Still refused | ps-bash's protected-path guard is unconditional |
| `split --hex-suffixes=FROM` with an `f` digit, `split -n l/N` with N > input size | Plain, correct output | GNU 9.4 produces garbage names / misbehaves there |
| `jq repeat(f)` | Matches the jq 1.7 binary (`1\|[limit(3;repeat(.*2))]` = `[2,2,2]`), not the manual | Oracle wins |
| `jq` `stderr` / `debug` | Line ends with a newline | The host terminates each record |
| awk recursion | Capped near depth 1000 with a clean exit-2 error | Protects the host's stack |
| awk `close()` of a command with unread output | Waits up to 3 s, then reports gawk's 141 | Avoids hanging on a child that never exits |
| awk warnings | No `cmd. line:N:` prefix | Location tracking not kept |
| `rg -P`, `grep -P` | Translated to .NET regex; constructs it cannot express (`\K`, `\X`, recursion, `(?\|`, verbs) are an error, never a silent mismatch | No PCRE2 engine |
| `tree` | Follows tree(1) 2.1; checked against tree 2.1.1 | Not part of coreutils |
| Pathname expansion: collation | Matches sort in ordinal (byte / UTF-16) order, like bash in the C / C.UTF-8 locale; no `en_US` collation | One locale-independent order, the one the oracle runs |
| Pathname expansion: `**`, extglob, `nocaseglob` | `**` acts as `*`; `+(a\|b)` / `@(…)` words keep the pre-glob emission (the literal word); `shopt -s nocaseglob` is accepted and ignored. The matcher is case-sensitive on every OS, Windows included | Not implemented; `globstar` is listed by `shopt` but does nothing |
| `shopt -s failglob` | The failing COMMAND is aborted with `bash: no match: PAT` and status 1, and the script continues with the next statement; bash discards the whole line | Statement-level abort in a PowerShell host |
| Pathname expansion of a name that contains `* ? [` | Mapped cmdlets that expand their own operands (`cp`, `mv`, `ls`, …) receive the already-expanded NAMES; a name that literally exists is used as is, but a cmdlet that only looks at `*`/`?` may re-glob one that does not (a missing `a[1]` next to `a1`) | The cmdlets keep their own globbing for direct PowerShell calls; their dialects differ |
| Pathname expansion inside redirect targets, `case` words, `[[ ]]` | Not expanded (a redirect target is the literal word; bash expands it and fails `ambiguous redirect` on several matches) | Rare; not modelled |
| Command substitution glued into a longer word (`x$(cmd)y`) | Not word-split | Only a bare `$(…)` / `` `…` `` word goes through `ConvertTo-BashWords` |
| Redirect failure message (`echo > nodir/f`) | `bash: nodir/f: No such file or directory`; bash prints `script: line N: nodir/f: …` | No line tracking at runtime; status and filesystem state match |
| Command whose `> f` / `>> f` cannot be opened | The command still runs; its stdout is discarded, status is 1 and the script continues (bash never runs it). Under `set -e` the script stops, as in bash | `Invoke-BashRedirect` is the pipeline's last stage; stopping upstream would need a terminating error, which aborts every later statement inside an emitted `try { }` |
| Env prefix on a pipe stage (`yes \| FOO=1 head -n1`) | The stage collects its input before running, so an unbounded producer does not stream into it | The save/set/restore wrapper is a script block, which starts after its upstream completes |
| `declare -f fn` | Prints bash's layout (`fn () `, `{ `, body, `}`) but the body is the function's emitted PowerShell | The bash source is not kept once a function is defined |
| `command -v ls`, `type ls`, `which ls` (any ps-bash command) | The "path" is the name: `ls`, `ls is ls`, `type -t` = `file`; a real program prints its path | The command is the runtime's own implementation, with no file; the name still runs it |
| Windows drive paths in a glob word | A pattern spelled `/c/Users/*` (with `PSBASH_UNIX_PATHS=1`) or `C:/Users/*` is expanded with forward slashes (`C:/Users/x`), and a drive-letter prefix keeps the form it was typed | `/` is the pattern's only separator (`\` escapes) |

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
