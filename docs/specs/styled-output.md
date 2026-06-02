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
objects ──► StyledNode tree (kind/class/:pseudo) ──► CSS cascade ──► SpectreProjection ─► ANSI ──┬─► one string   (Format-Styled, static)
                              ▲                                                                   └─► repaint loop (Show-Styled, interactive)
                              │
              shared built-in stylesheets (fs / procsvc / object / error)
```

Both renderers use the **same** AOT-clean `SpectreProjection`; only the driver differs.

- **Static** (`Format-Styled`): one ANSI string forwarded over IPC. Works in every mode (`-c`, pipe,
  file, non-TTY). `:expanded` rows render their detail inline; no keyboard interaction.
- **Interactive** (`Show-Styled`): a `Console.ReadKey` loop (the proven-clean `browse` pattern)
  mutates the focused row's `:focused` / `:expanded` pseudo-state, re-runs the cascade, and repaints
  the Spectre frame on the alternate screen. ↑↓/jk move, Enter expands a row's detail block, q quits.
  Headless fallback (redirected I/O) prints a one-line summary.

**Why not Strata's Terminal.Gui projection:** it was built and verified to draw over a PTY, but
Terminal.Gui v2 (prealpha) drives the tty through its own input loop + native termios and leaves the
host's stdin dead on exit — no termios reset, subprocess isolation, or job-control reclaim recovered
it. The `Console.ReadKey` + Spectre loop shares the exact terminal path the line editor uses
(`browse`/`vim`), so it exits cleanly; it also drops the Terminal.Gui / System.Reactive native-dep
embedding entirely. (`StandardBeagle.Strata.Interaction` is still referenced — only for the
`command:` property descriptor so the stylesheets parse.)

## The TTY model

In the **interactive REPL (`ps-bash -i`)** the launcher spawns the host **attached to the real PTY
slave** as its stdio (the same path `browse` and `vim` use), so the host's `Console` *is* the
terminal — `Show-Styled` reads keys with `Console.ReadKey` directly, no launcher↔host handshake. In
the **non-interactive** modes (`-c`, stdin pipe, file, SDK) the host's stdio is IPC-piped, so
`Console.IsOutputRedirected` is true and `Show-Styled` emits the headless summary instead.

So the mode split is automatic:

- **Interactive REPL** → host owns the PTY → `Show-Styled` runs the `ReadKey` repaint loop and
  exits cleanly (the shell stays responsive — verified by `ShowStyledPtyTests`, which navigates,
  quits, and round-trips an `echo` afterward).
- **`-c` / pipe / file / SDK** → redirected → `Show-Styled` prints the summary; the styled *default*
  (`PSBASH_DEFAULT_FORMAT`, P3) renders the static Spectre string.

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
- **P4** interactive `Show-Styled` viewer — **landed + verified over a real PTY, exits clean**. The
  cmdlet, the mutable node model (`StyledNode`), the shared resolver (`StyledStyles`, which
  `Format-Styled` delegates to), and the `Console.ReadKey` + Spectre repaint loop
  (`StyledInteractiveSession`) build the `Surface → (Row | Detail)*` tree, cascade it, and repaint on
  each keystroke. `ShowStyledPtyTests` drives `seq 1 5 | Show-Styled` through `ps-bash -i` under a real
  pseudo-terminal: the viewer renders, navigates, quits, and the shell round-trips an `echo`
  afterward (clean exit). Headless path unit-tested (3/3). The Terminal.Gui projection was built,
  verified to draw over a PTY, then **removed** (post-exit stdin death — see above), along with its
  native-dep embedding and the subprocess-isolation experiment.
- **P5** route the styled *default* (`PSBASH_DEFAULT_FORMAT`) to the interactive viewer in the REPL
  (today it renders the static Spectre string), and add the **ping/tracert** live sources — pending.
