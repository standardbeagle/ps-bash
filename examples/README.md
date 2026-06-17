# PsBash examples

Runnable, commented scripts demonstrating every PsBash feature. Each folder targets one
area of the product; the docs site links here.

| # | Folder | Feature | How to run |
|---|--------|---------|-----------|
| 01 | [`01-bash-commands/`](01-bash-commands/) | Bash commands + the typed pipeline | `ps-bash 01-bash-commands/pipelines.sh` · `Import-Module PsBash; ./01-bash-commands/typed-objects.ps1` |
| 02 | [`02-interactive-shell/`](02-interactive-shell/) | Ctrl-R, `!!`, aliases, completion, `z`/`zi` | Source `psbashrc.example`, then read the walkthroughs |
| 03 | [`03-styled-output/`](03-styled-output/) | `Format-Styled`, custom CSS themes, auto-theming | `Import-Module PsBash; ./03-styled-output/format-styled.ps1` |
| 04 | [`04-interactive-tui/`](04-interactive-tui/) | `browse` adapters from a `.psm1`, `Show-Styled` CSS TUI | `Import-Module PsBash; Import-Module ./04-interactive-tui/PrWorkbench.psm1` |
| 05 | [`05-hooks/`](05-hooks/) | Prompt + on-`cd` hooks (direnv/fnm style) | `Import-Module PsBash; . ./05-hooks/chpwd-hooks.ps1` |

## Two ways to run these

PsBash ships **two** surfaces, and the examples use both:

- **The module** — `Import-Module PsBash` in any PowerShell session gives you the
  `Invoke-Bash*` cmdlets, the `ls`/`grep`/… aliases, `Format-Styled`, `Show-Styled`,
  `browse`, and the `Register-Bash*Hook` functions. The `.ps1` / `.psm1` examples use this.
- **The interactive shell** — `ps-bash` (no args) is a full REPL that reads bash. The `.sh`
  examples run with `ps-bash <file>` or `ps-bash -c '<command>'`. The line editor, Ctrl-R,
  `z`/`zi`, `!!`, and tab completion live here (see `02-interactive-shell/`).

> Scripts that need a real terminal (the `Show-Styled` / `browse` viewers, Ctrl-R, `z`) say
> so at the top. The rest run non-interactively and are safe in CI.
