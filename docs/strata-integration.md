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

- `-Css` (position 0, required): a `.css` file path **or** inline CSS text. A value
  containing `{` or a newline is always treated as inline CSS.
- `-InputObject` (pipeline): the objects to style.
- `-Property`: properties to render per row, in order. Omitted → row's `ToString()`.
- `-ClassProperty`: property supplying class labels (default `class`; space-separated
  string or string sequence).

Selectors match each row by `Kind` (type name, namespace stripped — e.g. `Process`),
`Id` (`Id` then `Name` property), and `.class` labels. `NO_COLOR` disables ANSI output.

The cmdlet emits a single rendered ANSI string; PowerShell prints the escapes in the host.
