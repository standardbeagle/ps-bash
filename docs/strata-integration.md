# Strata integration — `Format-Styled`

ps-bash consumes the [Strata](../../strata) selector engine to style object pipelines
with CSS and render them via Spectre.Console. This powers the `Format-Styled` cmdlet
(Phase 3 of the Strata plan: dogfood a real `Format-Styled` command).

## How the dependency is wired

Strata lives in the sibling repo `../strata` and is **not yet on nuget.org**. It is
consumed as local NuGet packages:

1. Strata is packed into `../strata/local-feed/` by `../strata/scripts/pack-local.sh`.
   The packages publish under the **`StandardBeagle.Strata.*`** ID prefix (the C#
   namespaces stay `Strata.*`; only the package IDs carry the prefix). The version is
   derived from git tags via MinVer, so the version arg to `pack-local.sh` is ignored —
   the feed is stamped with the MinVer value (currently `0.1.0-alpha.1.2`).
2. The csproj adds that folder as a per-project restore source (`RestoreAdditionalProjectSources`)
   only when `UseStrata=true`; it is deliberately NOT in `nuget.config` (a missing local
   source is a hard NU1301 error that would break CI).
3. `src/PsBash.Cmdlets/PsBash.Cmdlets.csproj` references the Strata packages — pinned to a
   single `$(StrataVersion)` property: `StandardBeagle.Strata.Css`,
   `StandardBeagle.Strata.Adapters.PSObject`, `StandardBeagle.Strata.Properties.Styling`,
   `StandardBeagle.Strata.Render.Spectre`, `StandardBeagle.Strata.Layout.Yoga`
   (`Spectre.Console` + `ExCSS` + `Yoga.Net` flow in transitively).

### Refreshing Strata after a change

```bash
( cd ../strata && ./scripts/pack-local.sh )   # repack into local-feed (MinVer-stamped)
dotnet nuget locals global-packages --clear    # only if the version was reused
./scripts/test.sh src/PsBash.Cmdlets.Tests --filter FormatStyled
```

MinVer stamps the same prerelease version until a new `v*` git tag lands in `../strata`,
so iterating on Strata internals reuses the version and NuGet may serve the cached copy.
Clear the global-packages cache (above) after a repack, or move the `$(StrataVersion)`
property in `PsBash.Cmdlets.csproj` to a freshly-tagged version.

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
