# docs/specs index

Per-subsystem deep references. Browse here; the few auto-loaded ones are `@`-linked in CLAUDE.md.
Nav starts at /CODE_MAP.md. Keep this index complete — a guard test fails on any unlisted spec.

- [architecture-overview.md](architecture-overview.md) — Architecture Overview
- [atuin-adapter.md](atuin-adapter.md) — Atuin Plugin Adapter Design
- [autosuggestions.md](autosuggestions.md) — Fish-Style Autosuggestions
- [browse.md](browse.md) — `browse` Object Workbench
- [command-assist.md](command-assist.md) — Command Assist Specification (AI prompt → reviewed command)
- [compact-output.md](compact-output.md) — Compact Output Specification (opt-in bounded digest)
- [compiled-plan-lane.md](compiled-plan-lane.md) — Compiled Plan Lane Specification (plan IR + decline contract)
- [completion-providers.md](completion-providers.md) — Tab Completion Providers Specification
- [config-format.md](config-format.md) — Config File Format Specification
- [ctrlr-ui.md](ctrlr-ui.md) — Ctrl-R Full-Screen Search UI
- [cwd-awareness.md](cwd-awareness.md) — CWD-Aware History Design
- [design-decisions.md](design-decisions.md) — Design Decisions and Rationale
- [emitter-strategy.md](emitter-strategy.md) — Emitter Strategy Specification
- [environment-model.md](environment-model.md) — shell variables, the export attribute (`ShellExports`), how a child's environment is built, and one storage per variable (`BashVariableLookup`, §8)
- [error-handling.md](error-handling.md) — Error Handling Specification
- [flagspec-extraction.md](flagspec-extraction.md) — FlagSpecs Extraction Plan
- [frecency-jump.md](frecency-jump.md) — Frecency Directory Jump (zoxide-style `z` / `zi`)
- [history-store-interface.md](history-store-interface.md) — IHistoryStore Interface Specification
- [host-lifecycle-contract.md](host-lifecycle-contract.md) — Host Lifecycle Metadata and Ownership Contract
- [spec-suite.md](spec-suite.md) — fast bash-conformance corpus + runner (DESIGN; in-host execution, executor-slot parallelism, absorbs overlapping tests)
- [spec-gaps.md](spec-gaps.md) — known bash divergences (XFAIL cases) in the spec corpus, grouped by area
- [xfail-triage.md](xfail-triage.md) — XFAIL backlog by agent-relevance tier (agent-facing scripting first, interactive-only last)
- [bash-feature-coverage.md](bash-feature-coverage.md) — measured bash-feature coverage (344-snippet probe vs bash 5.2): what matches, what is fixed, what is open
- [executor-slots.md](executor-slots.md) — one command per host + launcher-side executor slots (replaces the global exec gate and nested hosts)
- [shell-fds-and-bash-source.md](shell-fds-and-bash-source.md) — `BASH_SOURCE`, `exec N<file` / `read -u` / `>&N` descriptors, `exec cmd`
- [background-jobs.md](background-jobs.md) — process-isolated `&` design (DESIGN; fork semantics, cross-platform process management); §12 live job output
- [stdio-messaging.md](stdio-messaging.md) — output channels, fds and per-channel credit (in-host mux implemented; wire channels designed)
- [interactive-completion.md](interactive-completion.md) — Interactive Completion Specification
- [intentional-differences.md](intentional-differences.md) — Intentional Differences from bash / GNU (platform, architecture, deliberate choices, refused options)
- [keybindings.md](keybindings.md) — Interactive Shell Keybindings
- [lineeditor-vt100-design.md](lineeditor-vt100-design.md) — LineEditor VT100 Design
- [on-cd-hooks.md](on-cd-hooks.md) — On-CD Hook Cmdlet API Specification
- [parser-grammar.md](parser-grammar.md) — Parser Grammar Specification (v2 AST)
- [paths.md](paths.md) — Paths: forward-slash `C:/x` out (`BashPath.ToShell`), one inbound mapper (`RuntimePath`/`WindowsPath`); producer inventory, PATH policy
- [program-files.md](program-files.md) — how a resolved program file starts (`ProgramFile`: `#!` dispatch, PATHEXT, execute bit) at every site
- [plugin-architecture.md](plugin-architecture.md) — Plugin Architecture Specification
- [pty.md](pty.md) — PTY Architecture Specification
- [runtime-command-reference.md](runtime-command-reference.md) — Command Reference
- [runtime-functions.md](runtime-functions.md) — Runtime Functions Specification
- [runtime-migrated-cmdlets.md](runtime-migrated-cmdlets.md) — Migrated Binary Cmdlets (REFACTOR-2)
- [shell-implementation-phases.md](shell-implementation-phases.md) — Shell Implementation Phases
- [sqlite-history-schema.md](sqlite-history-schema.md) — SQLite History Store Schema
- [styled-output.md](styled-output.md) — Styled Output (interactive Format-Styled, buttons + detail expansion, opt-in default)
- [transpile-fuzz-grammar.md](transpile-fuzz-grammar.md) — Transpile Fuzz Grammar & Parseability Contract (never emit broken PowerShell)

## Platform dependencies

ps-bash ships win-x64, linux-x64/arm64 (glibc + musl) and osx-arm64. Every OS/arch-dependent constant, struct
layout and system tool path lives in ONE place: `src/PsBash.Transpiler/Platform/` (namespace `PsBash.Core.Platform`;
the Transpiler assembly is referenced by Cmdlets, Core, Host and Shell).

- `PosixStat` — libc `stat`/`lstat`/`fstat` and the `struct stat` layout per target (Linux x86_64, Linux arm64 =
  `asm-generic`, Darwin 64-bit-inode incl. `$INODE64` symbols on x86_64; glibc < 2.33 via `__xstat`). The kernel
  fills a byte buffer that the pure `TryDecode(span, layout)` reads, so each layout is unit-tested on any OS.
- `SystemTools` — the OS's own `stat`/`id`/`ps`/`sysctl` (`Resolve(name)`: `/usr/bin`, `/bin` (Alpine/busybox),
  `/usr/sbin`, `/sbin`; never PATH-first, since callers pick GNU vs BSD flags by OS), `PosixShell` (`/bin/sh`), the
  host-bash candidates. psm1: `[PsBash.Cmdlets.BashRuntime]::SystemToolPath('stat')`.
- Runtime facts come from .NET, never a literal: page size = `Environment.SystemPageSize` (16K/64K arm64 kernels).

Rule: a new platform fact goes into `Platform/`; a call site asks it. Guards (`Core.Tests/PlatformDependencyGuardTests`):
no `struct stat` field / stat-family P/Invoke outside `Platform/`; no `"/usr/bin/…"`, `"/bin/…"`, `"/usr/sbin/…"`,
`"/usr/local/bin/…"` literal in product C# or psm1 outside `Platform/` (allowlist, each entry with its reason).
Layouts: `Core.Tests/PosixStatTests` (hand-built buffers per layout).
