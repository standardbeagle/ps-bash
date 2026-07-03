---
paths:
  - "src/PsBash.Cmdlets/**"
---

# OS INTERFACE. Platform behavior lives in shared helpers, not per-cmdlet branches.

文言：毀滅性檔操與子程序皆經共用助手，勿各自為政。刪除用Delete*Force(清唯讀)，生成用RunChildProcess(限時殺樹)；同患勿散落。

## DESTRUCTIVE FILESYSTEM — `FileSystemHelpers` (one source)
A bare `Directory.Delete`/`File.Delete` THROWS on a Windows read-only descendant (.git packs,
node_modules) — `rm -rf`/`cp -rf`/`mv`/`find -delete` must not. Route EVERY force-delete through:
- `FileSystemHelpers.DeleteDirectoryForce(dir)` — native recursive delete, read-only-clearing fallback.
- `FileSystemHelpers.DeleteFileForce(path)` — file delete, clear-and-retry on UnauthorizedAccess.
- `FileSystemHelpers.DeleteEntryForce(path, isDir)` — picks the right one for a mixed list.
- `FileSystemHelpers.ClearReadOnly(path)` — best-effort attribute clear (for non-recursive cases, e.g. `find`'s dir delete which must stay non-recursive for bash parity).
NEVER re-derive the read-only fallback in a cmdlet. A new destructive op → use these.

## CHILD PROCESSES — `BashRuntime.RunChildProcess` (timeout + kill-tree)
Buffered shell-out (id, checksum external, etc.) → `BashRuntime.RunChildProcess(psi[, timeout])`:
bounded wait, concurrent stdout/stderr drain, `Kill(entireProcessTree:true)` on timeout. A hung
child can never wedge the single-threaded host runspace. Raw `Process.Start` is allowed ONLY for
streaming/interactive consumers (traceroute hop-by-hop, less paging) that handle `Stopping`
+ kill-tree themselves. Never raw-spawn a buffered command.

## PATHS / IDENTITY (already centralized — reuse, don't re-add)
- operand path normalization → `FileSystemHelpers.NormalizeOperandPath` → `WindowsPath` (unix→drive).
- `--version` → `FileSystemHelpers.TryHandleVersion`; exit code → `FileSystemHelpers.SetLastExitCode`.
- file reads → `BashFileSystem` (streaming, BOM/CRLF, binary policy) — NEVER raw `File.ReadAllText`.

## RAW-LINE FLAG RECOVERY (binder-swallowed short flags)
A flag the case-insensitive binder ate (grep/sed `-E`, cut `-d:`, uniq `-D`, echo `-e`), recovered
by regex-scanning the invocation line, MUST scope to `BashRuntime.CurrentPipelineSegment(MyInvocation)`,
NEVER the whole `MyInvocation.Line` — the full line leaks OTHER pipeline commands' flags (a `-E` in a
downstream `sed` flipped `grep` into extended-regex). Value clamps use `BashRuntime.ParseCountClamped`.

## BashFlagSpecs.json = collision-guard input (see `CommonParameterCollisionGuardTests`)
The single flag-spec source doubles as the input to the common-param collision guard. A corrupt/edited
spec HIDES real collisions — repairing it exposed `which -a` (collides with `-Arguments`) needing a
single-letter decoy. Never round-trip it through a PS pipeline that unwraps single-element arrays (that
char-corrupts every single-flag command). Colliding letters: `a c d e i o p v w`.
