# FlagSpecs Extraction Plan

This document describes the plan to extract `$script:BashFlagSpecs` from the PowerShell runtime module into a C# resource file for AOT compilation.

## Current State

The FlagSpecs data is embedded in the PowerShell runtime module at `src/PsBash.Module/PsBash.psm1` (lines 12926-13103). This data structure maps command names to arrays of flag-description pairs:

```powershell
$script:BashFlagSpecs = @{
    'ls' = @(
        @('-l', 'long listing'),
        @('-a', 'show hidden'),
        @('-h', 'human readable sizes'),
        @('-R', 'recursive'),
        @('-S', 'sort by size'),
        @('-t', 'sort by time'),
        @('-r', 'reverse sort'),
        @('-1', 'one per line')
    )
    'grep' = @(
        @('-i', 'ignore case'),
        @('-v', 'invert match'),
        @('-n', 'line numbers'),
        # ... more flags
    )
    # ... 60 commands total
}
```

**Total commands**: 60 commands with flag specifications

**Used for**: Tab completion in PowerShell module via `Register-BashCompletions`

## Problem Statement

The AOT-compiled `ps-bash` shell binary (`src/PsBash.Shell/Program.cs`) has no access to the PowerShell runtime module. The shell process:

1. Is compiled as a native AOT binary
2. Spawns a `pwsh` worker process for executing transpiled bash
3. Cannot query the PSM1 module's `$script:BashFlagSpecs` for completion data

Without embedded completion data, tab completion for command flags will not work in the AOT shell.

## Target Format

### JSON Resource File

Location: `src/PsBash.Shell/Resources/FlagSpecs.json`

```json
{
  "ls": [
    {"flag": "-l", "desc": "long listing"},
    {"flag": "-a", "desc": "show hidden"},
    {"flag": "-h", "desc": "human readable sizes"},
    {"flag": "-R", "desc": "recursive"},
    {"flag": "-S", "desc": "sort by size"},
    {"flag": "-t", "desc": "sort by time"},
    {"flag": "-r", "desc": "reverse sort"},
    {"flag": "-1", "desc": "one per line"}
  ],
  "grep": [
    {"flag": "-i", "desc": "ignore case"},
    {"flag": "-v", "desc": "invert match"},
    {"flag": "-n", "desc": "line numbers"},
    {"flag": "-c", "desc": "count only"},
    {"flag": "-r", "desc": "recursive"},
    {"flag": "-l", "desc": "files with matches"},
    {"flag": "-E", "desc": "extended regex"},
    {"flag": "-A", "desc": "after context"},
    {"flag": "-B", "desc": "before context"},
    {"flag": "-C", "desc": "context"},
    {"flag": "-F", "desc": "fixed strings"},
    {"flag": "-w", "desc": "word regexp"},
    {"flag": "-o", "desc": "only matching"},
    {"flag": "-H", "desc": "with filename"},
    {"flag": "-h", "desc": "no filename"},
    {"flag": "-e", "desc": "pattern"},
    {"flag": "-m", "desc": "max count"}
  ],
  // ... remaining commands
}
```

### Alternative: Embedded as C# Dictionary

For AOT compilation, the JSON can be embedded as an embedded resource and deserialized at startup, or compiled directly into a `Dictionary<string, FlagSpec[]>` using source generation or codegen.

**Preferred approach**: Embedded resource + deserialization at startup

```csharp
public sealed record FlagSpec(string Flag, string Desc);

public static class FlagSpecs
{
    private static readonly Dictionary<string, FlagSpec[]> Data = Load();

    private static Dictionary<string, FlagSpec[]> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("PsBash.Shell.Resources.FlagSpecs.json")!;
        var json = new StreamReader(stream).ReadToEnd();
        return JsonSerializer.Deserialize<Dictionary<string, FlagSpec[]>>(json)!;
    }

    public static IReadOnlyList<FlagSpec>? GetFlags(string command) =>
        Data.TryGetValue(command, out var specs) ? specs : null;
}
```

## Generation Script

A one-time PowerShell script will parse the PSM1 hashtable and generate the JSON file:

```powershell
# scripts/generate-flagspecs.ps1

$psm1Path = "src/PsBash.Module/PsBash.psm1"
$outputPath = "src/PsBash.Shell/Resources/FlagSpecs.json"

# Extract the BashFlagSpecs hashtable definition
$content = Get-Content $psm1Path -Raw

# Parse the hashtable (simplified: assumes format is stable)
# In production, use AST parsing for robustness
$ast = [System.Management.Automation.Language.Parser]::ParseFile($psm1Path, [ref]$null, [ref]$null)

# Find the assignment to $script:BashFlagSpecs
$hashtableAst = $ast.FindAll({
    $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $args[0].Left.VariablePath.UserPath -eq 'script:BashFlagSpecs'
}, $true) | Select-Object -First 1

if ($null -eq $hashtableAst) {
    Write-Error "Could not find $script:BashFlagSpecs in $psm1Path"
    exit 1
}

# Convert PowerShell hashtable to JSON
$flagSpecs = @{}
# ... extract and convert pairs ...

$json = $flagSpecs | ConvertTo-Json -Depth 3
$json | Set-Content $outputPath -Encoding UTF8

Write-Host "Generated $outputPath"
```

## Integration Points

### 1. TabCompleter.cs

Modify `TabCompleter.Complete()` to look up flag completions:

```csharp
private static IReadOnlyList<string> CompleteFlags(
    string command,
    string partial,
    IReadOnlyDictionary<string, string> aliases)
{
    // Resolve alias to base command
    while (aliases.TryGetValue(command, out var expansion))
    {
        var parts = expansion.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0) break;
        command = parts[0];
    }

    var specs = FlagSpecs.GetFlags(command);
    if (specs is null) return [];

    var results = new List<string>();
    foreach (var spec in specs)
    {
        if (spec.Flag.StartsWith(partial, StringComparison.Ordinal))
            results.Add(spec.Flag);
    }
    return results;
}
```

### 2. Build Integration

Add to `src/PsBash.Shell/PsBash.Shell.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\FlagSpecs.json" />
</ItemGroup>
```

Add a pre-build target to regenerate JSON if PSM1 changes:

```xml
<Target Name="GenerateFlagSpecs" BeforeTargets="BeforeBuild">
  <Exec Command="pwsh scripts/generate-flagspecs.ps1" Condition="Exists('scripts/generate-flagspecs.ps1')" />
</Target>
```

## Future Enhancements

### Auto-Generation from Man Pages

The current FlagSpecs are manually maintained. Future work could auto-generate from:

1. **Linux man pages**: Parse `man ls`, `man grep` output for flag descriptions
2. **GNU `--help` output**: Parse `ls --help`, `grep --help` for structured help
3. **Completion scripts**: Parse bash completion files in `/usr/share/bash-completion/completions/`

Example pipeline:

```bash
# Extract flag specs from --help
ls --help | grep -E '^\s*-\w' | awk '{print $1, $2}' > ls-flags.txt

# Or use man page
man ls | col -b | grep -E '^\s*-\w' | awk '{print $1, $2}' > ls-flags.txt
```

Then convert to JSON via a script.

### Community Contributions

Once extracted, the FlagSpecs JSON file can be:

1. Stored in a separate repository for community contributions
2. Validated via CI (check JSON schema)
3. Versioned independently from ps-bash releases

## Acceptance Criteria

- [ ] JSON file generated at `src/PsBash.Shell/Resources/FlagSpecs.json`
- [ ] All 60 commands from PSM1 represented in JSON
- [ ] C# `FlagSpecs` class loads embedded JSON at startup
- [ ] `TabCompleter` uses `FlagSpecs.GetFlags()` for completion
- [ ] Completion tests verify flag suggestions match expected flags
- [ ] Build process regenerates JSON when PSM1 changes

## Dependencies

This task is part of **P5: Tab Completion** (see `shell-implementation-phases.md`). Flag completion is a prerequisite for:

- Full tab completion support in AOT shell
- Context-aware completion (future)
- Custom completion plugins (future)

## Related Documents

- `completion-providers.md` - Full completion provider specification
- `shell-implementation-phases.md` - P5 implementation details
- `runtime-functions.md` - BashFlagSpecs usage in PSM1
