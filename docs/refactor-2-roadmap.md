# REFACTOR-2 Roadmap — Remaining psm1 → C# Cmdlet Migrations

**Live tracking artifact.** Maintained as functions get migrated. Reference companion: [`docs/refactor-2-migration-playbook.md`](./refactor-2-migration-playbook.md) (the per-function procedure).

## Status as of 2026-05-16

- **psm1 size:** 11,939 lines (started session at 12,691)
- **Remaining `Invoke-Bash*` functions in psm1:** 70
- **Total LOC in remaining functions:** 11,338
- **Compiled cmdlets in `PsBash.Cmdlets.dll`:** 28 (+ shared engines: `JqEngine`, `ChecksumEngine`, `FileSystemHelpers`)

## Migration tiers

Each function is rated by **size** (line count), **isolation** (no shared psm1 helper that would have to migrate with it), and **risk** (semantic edge cases / cross-platform branches). Tier numbering reflects recommended migration order.

### Tier 1 — Parallel-subagent ready (24 functions)

Self-contained or near-self-contained. No shared mutable state. Each one fits the established cmdlet pattern (`Arguments` catch-all, `-v` declared if present, optional pipeline input, error-out via `FileSystemHelpers.WriteBashError`). Safe to dispatch as parallel subagent work.

| Function | Lines | Pattern | Notes |
|---|---|---|---|
| Rev | 40 | file + pipeline transform | Per-line string reverse. Pure. |
| Strings | 46 | file scan | Find printable byte runs ≥ N chars. |
| Readlink | 45 | path resolver | Symlink target (use `FileSystemInfo.LinkTarget`). |
| Tput | 40 | system query | Native passthrough + fallback to `Host.UI.RawUI`. |
| Uname | 55 | system query | `Environment.OSVersion` / `RuntimeInformation`. |
| Mktemp | 58 | filesystem create | `Path.GetTempFileName()` + `-d` for dir mode. |
| Time | 68 | process timing | Stopwatch wrap + child process exec. |
| Env | 66 | env-var enumeration | `Environment.GetEnvironmentVariables()`. |
| File | 99 | file type detection | Read magic bytes, return MIME-ish type. |
| Tac | 57 | reverse lines | File + pipeline, uses `Read-BashFileLines` (small psm1 dep — inline it). |
| Nl | 85 | line numbering | Numeric prefix per line. |
| Fold | 77 | line wrapping | `-w` width, `-s` break on space. |
| Expand | 62 | tabs → spaces | `-t` tab stops. |
| Unexpand | 90 | spaces → tabs | inverse of expand. |
| Split | 97 | file partition | `-l N` lines per output, `-a N` suffix length. |
| Paste | 91 | column merge | Multi-file zip with separator. |
| Base64 | 86 | encode/decode | `Convert.ToBase64String` / `FromBase64String`. |
| Shuf | 89 | random shuffle | In-memory `Random.Shuffle` then emit. |
| Comm | 105 | common lines | Compare two sorted files, 3-column output. |
| Join | 120 | relational join | Two-file join on key column. |
| Seq | 120 | number sequence | Optional `-s` separator, `-w` zero-pad. |
| Expr | 121 | arithmetic eval | Custom integer expression parser (already isolated). |
| Column | 124 | tabular format | `-t` table mode, `-s` field separator. |
| Cut | 157 | column extract | `-d` delim, `-f` fields, `-c` byte range. |

**Total Tier 1 LOC: 1,996** — about a 17% additional reduction in psm1 size.

### Tier 2 — Medium complexity, sequential (15 functions)

Larger, more flag-rich, or has a real algorithmic core. Migrate one at a time with focused review.

| Function | Lines | Notes |
|---|---|---|
| Stat | 508 | Depends on `Get-BashFileInfo` (psm1, also used by `find` cmdlet — already C++-duplicated there) and `Format-StatString` (psm1, would migrate alongside). Cross-platform branches (Linux/Mac shells out to `/usr/bin/stat`). |
| Echo | 418 | `-e` / `-n` / `-E` flag-binding collision with `-ErrorAction` / `-Verbose`. **Per `runtime-migrated-cmdlets.md` this is documented as permanently psm1.** Revisit if PSCmdlet parameter-binding gains a "no common parameters" mode. |
| Date | 174 | Date arithmetic + custom format strings (GNU-style `+%Y-%m-%d`). |
| Tree | 178 | Directory tree rendering. `-L` depth, `-I` ignore pattern. |
| Du | 213 | Disk usage. Recursive size summation, `-h` human-readable. |
| Tr | 231 | Character translation. Uses `Expand-EscapeSequences` (already in BashRuntime). |
| Tar | 372 | Archive ops. Use `System.IO.Compression.TarFile`. |
| Diff | 393 | Unified-diff output. Reimplement diff algorithm or shell out (oracle stays psm1 currently). |
| Sort | 445 | Many flags: `-k`, `-t`, `-n`, `-V`, `-M`, `-r`, `-u`, `-f`, `-c`. Stable Sort. |
| Grep | 556 | Regex over file/pipeline. Already isolated. Big but mechanical. |
| Tar | 372 | Tar archive ops. |
| Browse | 167 | Interactive object browser — may need PTY awareness. |
| Less / More | 94 / 100 | Pagers — non-interactive mode trivial; interactive PTY-aware mode is harder. **Less hang bug filed (Task #F via earlier triage).** |
| Tee | 76 | Pipeline + file write. |
| Shopt | 44 | Shell options table (script-scope hashtable in psm1; bring it into C# or keep state in psm1 hashtable behind a shim). |

### Tier 3 — Shared-state group migrations (13 functions)

Best migrated as **coherent batches** because they share mutable state. Each batch needs a one-time state-ownership decision (e.g. "the `BashJobTable` is owned by `BashRuntime.JobRegistry`, all four job cmdlets read/write it").

#### Tier 3a — Job control (5 functions, 211 lines)
Shared: `$global:BashJobTable` (PS hashtable keyed by job ID).
- Bg (14), Fg (48), Jobs (21), Wait (31), Background (33), Kill (95)

Decision needed: keep the table in psm1 module scope and access it from cmdlets via `SessionState.PSVariable.GetValue`, or migrate the table itself into a C# static `JobRegistry`. The psm1-scope path is cheaper; the C# path is cleaner.

#### Tier 3b — Directory stack (3 functions, 74 lines)
Shared: `$global:BashDirStack` (PS array of paths).
- Pushd (21), Popd (13), Dirs (40)

Same decision shape as 3a.

#### Tier 3c — Positional / variables (4 functions, 211 lines)
- Shift (26) — modifies `$global:BashPositional`
- Unset (28) — `Remove-Variable` / `Remove-Item Env:`
- Let (25) — arithmetic assignment with PS-side variable mutation
- Read (132) — interactive line read, sets caller-scope variable

These touch the runspace variable space directly. Already idiomatic for `PSCmdlet.SessionState.PSVariable`.

#### Tier 3d — Aliases / hooks (2 functions, 168 lines)
- Alias (86), Trap (82)

Both register handlers in psm1 module scope. The script-mode alias path is what's complex; shell-mode alias is already in C# (`InteractiveShell`).

### Tier 4 — Large mechanical migrations (5 functions, 1,808 lines)

Big enough to warrant their own dedicated session, but no novel design problems.

- Xargs (826) — pipeline + exec orchestration
- Install (591) — file install, attribute preservation, Windows binary-swap path (memory entry exists)
- Stat (508) — cross-platform, helper-web (see Tier 2 — duplicated here for visibility)
- Rg (312) — ripgrep wrapper, native passthrough + fallback
- Type / Command (118 / 446) — command resolution, paired

### Tier 5 — Permanently psm1 (1 function, 1,106 lines)

- **Awk** — full AWK language interpreter (~12 psm1 helpers, ~950 LOC). Per `runtime-migrated-cmdlets.md` section "REFACTOR-2 Phase 3 — awk / jq / find decisions": *"awk stays psm1 permanently."* Migration cost vastly outweighs benefit; the differential suite passes against the psm1 oracle.

---

## Suggested migration order

1. **Tier 1 (24 functions, ~2,000 LOC)** — Parallel-subagent dispatch. See playbook. Once these land, psm1 drops to ~9,900 lines (~22% smaller than session start).
2. **Tier 2 (15 functions, ~3,800 LOC)** — Sequential, focused review.
3. **Tier 3 batches (4 batches, ~664 LOC)** — Each batch in one PR with the shared-state decision documented.
4. **Tier 4 (5 functions, ~1,808 LOC)** — Each its own task.
5. **Defer**: Awk + Echo (per spec).

After all of the above except Tier 5, psm1 will be roughly:
- Aliases (`Set-Alias` lines for every cmdlet)
- The remaining psm1 helpers (Show-BashHelp, Write-BashError, Resolve-BashGlob, Read-BashFileLines, BashErrorMode switch, prompt/cd-hook registries)
- Awk's interpreter
- Module-init code

Estimated final size: **~2,500 psm1 lines** (down from 12,691). That should bring single-invocation startup well within 2× pwsh's ~600ms floor.

## Coordination

- The Dart loop driver also picks REFACTOR-2 tasks. Before claiming a function, check `.dartai-locks.json` for the active claim and `git log origin/main --grep="REFACTOR-2"` for recent migrations.
- Worktree isolation (`Agent` tool with `isolation: "worktree"`) is the recommended mode for subagent migrations — each subagent gets its own branch, the orchestrator integrates one at a time, no conflicts on the central psm1.
