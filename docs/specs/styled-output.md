# Styled output — interactive Format-Styled

How ps-bash turns a pipeline of PowerShell objects into **styled, button-bearing,
expandable** output, and how a user opts that in as the default. Built on the Strata
selector/cascade engine (see [`strata-integration.md`](../strata-integration.md)).

## Goal

Three deliverables, in dependency order:

1. **Integrate latest Strata** — the `StandardBeagle.Strata.*` widgets release (Button /
   Dialog kinds, `z-index`, `:expanded` pseudo-class, the Terminal.Gui projection). *(done — P1)*
2. **Stylesheets that add buttons + detail expansion to common objects** — shared CSS for
   filesystem (`FileInfo`/`DirectoryInfo`), process/service, a generic PSObject fallback, and
   error records. One stylesheet drives **both** renderers (static Spectre, interactive
   Terminal.Gui), exactly as Strata's `procs.css` / `show-processes.css` pair demonstrates.
3. **Opt-in: Format-Styled as the default output** — a session/env switch that routes
   `Out-Default` through styled rendering. Off by default (no regression to golden/differential
   suites); on → interactive viewer when a real terminal is present, static ANSI otherwise.

## Two renderers, one cascade

```
objects ──► PsObject node tree (kind/class/:pseudo) ──► CSS cascade ──┬─► SpectreProjection   ─► ANSI string   (static, any sink)
                                          ▲                            └─► TerminalGuiProjection ─► live View tree (interactive, real TTY)
                                          │
                          shared built-in stylesheets (fs / procsvc / object / error)
```

- **Static** (`Format-Styled`, today): `SpectreProjection` → one ANSI string forwarded over IPC.
  Works in every mode (`-c`, pipe, file, non-TTY). Buttons render as `[ Label ]` chrome;
  `:expanded` rows render their detail block inline. No keyboard interaction.
- **Interactive** (`Show-Styled` / `Format-Styled -Interactive`, P4): `TerminalGuiProjection` →
  full-screen View tree; `command:` CSS bindings move focus, toggle `:expanded`, fire button
  actions (kill / open / restart). Headless fallback (redirected I/O) prints a one-line summary,
  mirroring the Strata demo.

## The TTY-handoff constraint (P4 integration seam)

`ps-bash-host` is `PublishAot=false` (JIT) so Terminal.Gui's reflection paths are fine there —
**but** the host's stdout is piped over IPC to the AOT launcher (`PsBash.Shell`), which owns the
real PTY. Terminal.Gui sees `Console.IsOutputRedirected == true` in the host and goes headless.

So the interactive viewer needs the **real terminal**, which the launcher holds. Options:

- **A. Host opens `/dev/tty` directly** for the Terminal.Gui session while the launcher yields raw
  mode (an "enter TUI" IPC handshake). Host is JIT → Terminal.Gui runs. *Preferred; the work is the
  launcher↔host handshake + Windows console-handle equivalent.*
- **B. Viewer in the launcher** — rejected: launcher is NativeAOT, Terminal.Gui is not AOT-safe.
- **C. Spawn a separate non-AOT helper process** bound to the tty — heavier, last resort.

Until the handshake lands, the interactive viewer is reachable where the host already owns a TTY
(module-mode `Import-Module PsBash` in a plain `pwsh`, and direct one-shot runs), with the static
renderer as the universal fallback. **This is the one piece deferred from a single session; the
stylesheets and the static path ship complete.**

## Stylesheets (built-in, embedded)

`src/PsBash.Cmdlets/styles/*.css`, logical name `PsBash.Cmdlets.styles.<name>.css`. User overrides
of the same base name append after the built-in (cascade — later wins); search order in
`FormatStyledCommand.ResolveStylesheet`.

| Name | Objects | Classes / kinds | Buttons (`command:`) | Expansion |
|------|---------|-----------------|----------------------|-----------|
| `fs` | `FileInfo`, `DirectoryInfo` | `.dir .exe .symlink .hidden .large` | `open`, `cd` | size/dates/attrs/acl |
| `procsvc` | `Process`, `Service` | `.busy .idle .stopped .running` | `stop`, `restart` | path/memory/threads/status |
| `object` | any `PSObject` (fallback) | — | `copy`, `inspect` | full property list |
| `error` | `ErrorRecord` | `.error .warn` | `dismiss` | stack / invocation / category |

Each sheet defines kind + `.class` colour rules, `:focused` (full-screen focus cursor), `:expanded`
detail styling, `Button` chrome, and the `command:` key bindings (`navigate-*`, `toggle-expand`,
the per-family actions). The colour/`.class`/`Button` rules also apply under the static Spectre
projection; `:focused` and `command:` are inert there (no input loop) but harmless.

## Opt-in default (P3)

`SdkRunspaceSetup.ps1` installs a proxy `Out-Default` that, when
`$env:PSBASH_DEFAULT_FORMAT -eq 'styled'`, routes non-string objects through `Format-Styled`
(interactive when a TTY is present, else static). Strings, already-formatted output, and the
styled cmdlet's own output pass straight through to avoid recursion. Default unset → stock
PowerShell formatting, so existing tests and external-command parity are untouched.

## Status

All on branch `feat/strata-interactive-styled`.

- **P1** Strata integration — **done**. `StandardBeagle.Strata.*` @ `0.1.0-alpha.1.2`, FormatStyled 11/11.
- **P2** stylesheets (`fs`/`procsvc`/`object`/`error`) + `command:` descriptor in the static path —
  **done**. 4 theory cases parse+render green (15/15).
- **P3** opt-in `PSBASH_DEFAULT_FORMAT=styled` at the SdkWorker flush point — **done**. Native
  PSObject output styled with ANSI when on, stock formatting when off; SdkWorker suite 18/18.
- **P4** interactive Terminal.Gui viewer + ping/tracert sources + launcher↔host TTY handshake —
  **pending**. The largest piece: needs the TerminalGui/Interaction/Reactive (+ Terminal.Gui native)
  assemblies embedded for the host runtime and the TTY handshake above. The interactive run loop
  cannot be unit-verified headlessly (needs a real terminal driver), so it lands behind a headless
  fallback and is verified manually in module-mode (`Import-Module PsBash` in a real `pwsh`).

### Why P4 is staged separately

P1–P3 satisfy the goal end-to-end on every platform and mode with automated tests. P4 adds *live*
interaction; its two un-headless-testable risks (Terminal.Gui native-driver load in the extracted
host bundle, and the launcher↔host TTY handshake) want a real-terminal verification loop, so they are
deliberately not bundled into the same automated-green increment.
