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

## The TTY model (resolved) and the one remaining gap

`ps-bash-host` is `PublishAot=false` (JIT) so Terminal.Gui's reflection paths run there. The earlier
worry — "host stdout is IPC-piped, so Terminal.Gui sees redirected output and goes headless" — holds
only for the **non-interactive** modes (`-c`, stdin pipe, file). In the **interactive REPL (`ps-bash
-i`)** the launcher spawns the host **attached to the real PTY slave** as its stdio (the same path
`browse` and `vim` use), so the host's `Console` *is* the terminal. **No launcher↔host handshake is
needed.** Verified: `ShowStyledPtyTests` drives `seq 1 5 | Show-Styled` through `ps-bash -i` under a
real pseudo-terminal and the Terminal.Gui window draws and quits cleanly.

So the mode split is automatic and correct:

- **Interactive REPL** → host owns the PTY → `Show-Styled` enters the live Terminal.Gui loop.
- **`-c` / pipe / file / SDK** → `Console.IsOutputRedirected` → `Show-Styled` emits the headless
  summary; the styled *default* (`PSBASH_DEFAULT_FORMAT`, P3) renders the static Spectre string.

**The one remaining gap — post-TUI LineEditor re-arm.** After a Terminal.Gui session exits, the
shell prompt returns but the **next** input line is not processed: Terminal.Gui owns the terminal
directly and bypasses ps-bash's `LineEditor`, so on shutdown it does not re-arm the cooked-mode line
reader (`browse` avoids this by being hand-rolled *inside* that editor). The fix is to re-initialize
the `InteractiveShell` line reader after `Application.Shutdown()` returns control. Until then, the
viewer draws and quits cleanly, but a user must press Enter / re-focus to resume typing.
`ShowStyledPtyTests` asserts the verified contract (draw + clean quit-to-prompt) and documents this
omission per QA-rubric Directive 5.

Windows ConPTY runtime verification is CI-gated (the POSIX PTY tests `Skip.If(Windows)`).

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
- **P4** interactive `Show-Styled` viewer — **landed + verified over a real PTY**. The cmdlet, the
  mutable node model (`StyledNode`), and the shared resolver (`StyledStyles`, which `Format-Styled`
  delegates to) build the `Surface → Row* → Detail/Button` tree and project it via
  `TerminalGuiProjection`. Terminal.Gui v2 + `System.Reactive` + transitive deps are now **embedded
  in the host bundle** (Core `_StrataDep`) and extracted at runtime — confirmed end-to-end through
  the real `ps-bash` host (`Get-Process | Show-Styled` → procsvc sheet, views built). Headless path
  unit-tested (3/3); the **live loop drawing + clean quit** verified by `ShowStyledPtyTests` under a
  real pseudo-terminal. **Remaining:** the post-TUI LineEditor re-arm (above), routing the styled
  *default* to the interactive viewer in the REPL (vs the static string today), and the
  **ping/tracert** live sources.

### Why P4 is staged separately

P1–P3 satisfy the goal end-to-end on every platform and mode with automated tests. P4 adds *live*
interaction; its two un-headless-testable risks (Terminal.Gui native-driver load in the extracted
host bundle, and the launcher↔host TTY handshake) want a real-terminal verification loop, so they are
deliberately not bundled into the same automated-green increment.
