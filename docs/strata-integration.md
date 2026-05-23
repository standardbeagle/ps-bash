# Strata integration — `Format-Styled`

ps-bash consumes the [Strata](../../strata) selector engine to style object pipelines
with CSS and render them via Spectre.Console. This powers the `Format-Styled` cmdlet
(Phase 3 of the Strata plan: dogfood a real `Format-Styled` command).

## How the dependency is wired

Strata lives in the sibling repo `../strata` and is **not yet on nuget.org**. It is
consumed as local NuGet packages:

1. Strata is packed into `../strata/local-feed/` by `../strata/scripts/pack-local.sh`
   (default version `0.1.0-dev`).
2. `nuget.config` registers that folder as the `strata-local` package source.
3. `src/PsBash.Cmdlets/PsBash.Cmdlets.csproj` references the Strata packages:
   `Strata.Css`, `Strata.Adapters.PSObject`, `Strata.Properties.Styling`,
   `Strata.Render.Spectre` (`Spectre.Console` + `ExCSS` flow in transitively).

### Refreshing Strata after a change

```bash
( cd ../strata && ./scripts/pack-local.sh )   # repack 0.1.0-dev into local-feed
dotnet nuget locals global-packages --clear    # only if the version was reused
./scripts/test.sh src/PsBash.Cmdlets.Tests --filter FormatStyled
```

Reusing the same `0.1.0-dev` version means NuGet may serve the cached copy; bump the
version (`pack-local.sh 0.1.1-dev` + matching `PackageReference` versions) or clear the
global-packages cache when iterating on Strata internals.

## Using `Format-Styled`

```powershell
# Inline CSS
Get-Process | Format-Styled 'Process { color: cyan }' -Property Name,Id

# Stylesheet file + class projection
Get-Process |
  Select-Object *, @{ n='class'; e={ if ($_.CPU -gt 50) { 'busy' } } } |
  Format-Styled samples/Format-Styled/procs.css -Property Name,Id,CPU
```

Parameters:

- `-Css` (position 0, **optional**; aliases `-Style`, `-Stylesheet`): the stylesheet to
  apply. Accepts **inline CSS** (any value containing `{` or a newline), a **`.css` file
  path**, or the **name of a built-in / user stylesheet** (`default`, `ls`, `ps`, …).
  **Omitted entirely → the built-in `default` stylesheet.**
- `-InputObject` (pipeline): the objects to style.
- `-Property`: properties to render per row, in order. Omitted → row's `ToString()`.
- `-ClassProperty`: property supplying class labels (default `class`; space-separated
  string or string sequence).

Selectors match each row by `Kind` (type name, namespace stripped — e.g. `Process`),
`Id` (`Id` then `Name` property), and `.class` labels. `NO_COLOR` disables ANSI output.

The cmdlet emits a single rendered ANSI string; PowerShell prints the escapes in the host.

## Default and customizable stylesheets

`Format-Styled` ships built-in stylesheets, embedded in the cmdlet assembly:

| Name | For | Class hooks (project a `class`) |
|------|-----|---------------------------------|
| `default` | any pipeline (used when no stylesheet is given) | `.error .warn .ok .info .muted .bold .strike`; kinds `DirectoryInfo`, `FileInfo`, `Process` |
| `ls` | directory listings | `.dir .exe .symlink .archive .hidden` |
| `ps` | process listings | `.busy .stuck .idle` |

```powershell
Get-Process | Format-Styled              # built-in default
Get-Process | Format-Styled -Style ps    # built-in ps sheet
```

**Customize** by dropping a `.css` file with the **same base name** into a user style dir.
Your rules are appended *after* the built-in, so later declarations win (the CSS cascade) —
retune one selector without recopying the whole sheet. Search order:

1. `$PSBASH_STYLE_PATH` — a directory, or a `PATH`-separated list of directories.
2. `~/.config/ps-bash/styles/`
3. `~/.psbash/styles/`

```powershell
# Make the default sheet paint processes green+bold, everything else unchanged:
New-Item ~/.config/ps-bash/styles -ItemType Directory -Force | Out-Null
'Process { color: green; font-weight: bold }' | Set-Content ~/.config/ps-bash/styles/default.css
Get-Process | Format-Styled              # now green+bold

# Or keep a project-local style dir:
$env:PSBASH_STYLE_PATH = "$PWD/.styles"
```

**Colors**: the 16 ANSI names (`black red green yellow blue magenta cyan white` + `bright*`),
`transparent`, or `#rrggbb` hex (note: `silver`, `purple`, etc. are not recognized).
**Properties**: `color`, `font-weight: bold`, `text-decoration: strikethrough` / `underline`.
