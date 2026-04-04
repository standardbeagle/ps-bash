# ps-bash Shell Binary — Technical Specification

**Project:** ps-bash  
**Component:** `ps-bash.exe` — Native shell binary for AI coding agent integration  
**Status:** Specification / Greenfield  
**Author:** StandardBeagle  

---

## Overview

`ps-bash.exe` is a POSIX-compatible shell binary that acts as a drop-in replacement for `bash` on Windows (and optionally Linux/macOS). It enables AI coding agents (Claude Code, opencode, Gemini CLI, Codex CLI, Copilot CLI) to use Unix-style bash commands on Windows without requiring Git Bash, Cygwin, or WSL.

The binary is built with .NET 10 Native AOT — a single self-contained executable with no .NET runtime dependency. It always shells out to `pwsh` (PowerShell 7+) for execution, using the existing `ps-bash` PowerShell module as the runtime for Unix command emulation.

---

## Goals

- **Drop-in shell replacement** — honor the POSIX shell interface contract (`-c`, `--login`, `-i` flags) so agents can set `SHELL=ps-bash.exe` with zero other changes
- **No embedded PowerShell** — always spawn an external `pwsh` process; never link against `Microsoft.PowerShell.SDK`
- **Persistent worker model** — one `pwsh` process per session, not per command; ps-bash module loads once and stays warm
- **Configurable pwsh location** — system pwsh, PATH discovery, side-by-side bundled pwsh, or `PSBASH_PWSH` env var override
- **Two distribution flavors** — slim (requires system pwsh) and full (bundles pwsh)
- **Cross-platform build** — same codebase compiles for `win-x64`, `linux-x64`, `osx-arm64`

---

## Non-Goals

- Full bash compatibility (only agent-emitted bash patterns are required)
- Embedding or linking PowerShell SDK assemblies
- Supporting WSL or Cygwin paths
- Replacing the existing ps-bash PowerShell module (this binary is additive)

---

## Repository Structure

Add to the existing `ps-bash` repository:

```
ps-bash/
├── src/
│   ├── PsBash.Module/           # existing PowerShell module (unchanged)
│   ├── PsBash.Core/             # NEW: shared transpiler + locator logic
│   │   ├── Transpiler/
│   │   │   ├── BashTranspiler.cs
│   │   │   ├── TranspileContext.cs
│   │   │   └── Transforms/
│   │   │       ├── ITransform.cs
│   │   │       ├── DevNullTransform.cs
bash │   │       ├── TmpPathTransform.cs
│   │   │       ├── ExportTransform.cs
│   │   │       ├── FileTestTransform.cs
│   │   │       ├── PipeTransform.cs
│   │   │       └── EnvVarTransform.cs
│   │   ├── Runtime/
│   │   │   ├── PwshLocator.cs
│   │   │   ├── PwshWorker.cs
│   │   │   └── PwshWorkerProtocol.cs
│   │   └── PsBash.Core.csproj
│   └── PsBash.Shell/            # NEW: AOT binary entry point
│       ├── Program.cs
│       ├── Args.cs
│       ├── InteractiveShell.cs
│       └── PsBash.Shell.csproj
├── scripts/
│   └── ps-bash-worker.ps1       # NEW: long-lived pwsh worker script
├── dist/
│   ├── slim/                    # build output: binary + module only
│   └── full/                    # build output: binary + module + bundled pwsh
└── ps-bash.sln
```

---

## Project Files

### PsBash.Core.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

### PsBash.Shell.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PsBash.Core\PsBash.Core.csproj" />
  </ItemGroup>
</Project>
```

---

## Shell Interface Contract

`ps-bash.exe` must honor the following invocation patterns used by AI agents:

| Invocation | Description |
|---|---|
| `ps-bash.exe -c "command"` | Primary agent usage — execute command string |
| `ps-bash.exe --login -c "command"` | Some agents add `--login` |
| `ps-bash.exe -i` | Interactive REPL mode |
| `ps-bash.exe -s` | Read from stdin |
| `ps-bash.exe -c "cmd" 2>/dev/null` | Exit code passthrough |
| `SHELL=ps-bash.exe <agent>` | Environment variable override |

Exit codes must pass through exactly from the underlying pwsh execution.

---

## pwsh Locator

Resolves the `pwsh` executable in priority order. Implemented in `PwshLocator.cs`.

```
Priority 1: PSBASH_PWSH environment variable
Priority 2: PSBASH_WORKER environment variable (full worker script override)
Priority 3: pwsh on PATH
Priority 4: pwsh.exe in <binary-dir>/pwsh/ (side-by-side bundled)
Priority 5: Fail with actionable error message
```

```csharp
public static class PwshLocator
{
    public static string Locate() =>
        FromEnvironment()
        ?? FromPath()
        ?? FromSideBySide()
        ?? throw new PwshNotFoundException(
            "ps-bash requires PowerShell 7+.\n" +
            "Install: https://aka.ms/powershell\n" +
            "Or set PSBASH_PWSH=/path/to/pwsh");

    private static string? FromEnvironment() =>
        Environment.GetEnvironmentVariable("PSBASH_PWSH") is { Length: > 0 } v
            ? v : null;

    private static string? FromPath()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator) ?? [];
        return paths
            .Select(p => Path.Combine(p, PwshExeName))
            .FirstOrDefault(File.Exists);
    }

    private static string? FromSideBySide()
    {
        var sxs = Path.Combine(AppContext.BaseDirectory, "pwsh", PwshExeName);
        return File.Exists(sxs) ? sxs : null;
    }

    private static string PwshExeName =>
        OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
}
```

---

## Persistent Worker Model

A single `pwsh` process is started at session open and reused for all commands. This means:

- The ps-bash module loads **once** per session, not per command
- `cd` and other state-mutating commands persist correctly across calls
- AV scanning overhead (Windows Defender etc.) occurs once, not per command
- Startup latency (~300ms for pwsh init) is paid once per agent session

### Worker Lifecycle

```
ps-bash.exe starts
    → PwshLocator.Locate() finds pwsh
    → PwshWorker.StartAsync() spawns pwsh with worker script
    → waits for "READY" signal on stdout
    → session open, commands can be dispatched

Per command:
    → BashTranspiler.Transpile(bashCommand) → pwshCommand
    → PwshWorker.ExecuteAsync(pwshCommand)
    → write command + "<<<END>>>" to worker stdin
    → stream stdout/stderr to our stdout/stderr
    → read "<<<EXIT:N>>>" sentinel
    → return exit code N

ps-bash.exe exits
    → PwshWorker.DisposeAsync() closes stdin
    → worker process exits naturally
```

### PwshWorker.cs

```csharp
public sealed class PwshWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly string _pwshPath;

    private PwshWorker(Process process, string pwshPath)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _pwshPath = pwshPath;
    }

    public static async Task<PwshWorker> StartAsync(
        string pwshPath,
        string? workerScriptPath = null,
        CancellationToken ct = default)
    {
        var scriptPath = workerScriptPath
            ?? Path.Combine(AppContext.BaseDirectory, "ps-bash-worker.ps1");

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", scriptPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // stderr passthrough
            UseShellExecute = false,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh worker");

        var worker = new PwshWorker(process, pwshPath);

        // Wait for READY signal
        var ready = await worker._stdout.ReadLineAsync(ct);
        if (ready != "READY")
            throw new InvalidOperationException(
                $"Worker failed to start. Got: {ready}");

        return worker;
    }

    public async Task<int> ExecuteAsync(
        string pwshCommand,
        CancellationToken ct = default)
    {
        await _stdin.WriteLineAsync(pwshCommand.AsMemory(), ct);
        await _stdin.WriteLineAsync("<<<END>>>".AsMemory(), ct);
        await _stdin.FlushAsync(ct);

        string? line;
        while ((line = await _stdout.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("<<<EXIT:", StringComparison.Ordinal))
            {
                var code = line["<<<EXIT:".Length..^3];
                return int.TryParse(code, out var n) ? n : 1;
            }
            Console.WriteLine(line);
        }

        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _stdin.DisposeAsync();
        await _process.WaitForExitAsync();
        _process.Dispose();
    }
}
```

### ps-bash-worker.ps1

```powershell
#Requires -Version 7.0
param()

# Load ps-bash module
$modulePath = Join-Path $PSScriptRoot "Modules" "ps-bash"
if (Test-Path $modulePath) {
    Import-Module $modulePath -ErrorAction Stop
} else {
    Import-Module ps-bash -ErrorAction Stop
}

# Signal ready
[Console]::Out.WriteLine("READY")
[Console]::Out.Flush()

# Command loop
while ($true) {
    $lines = [System.Collections.Generic.List[string]]::new()

    while ($true) {
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { exit 0 }      # stdin closed
        if ($line -eq '<<<END>>>') { break }
        $lines.Add($line)
    }

    $command = $lines -join "`n"

    try {
        $result = Invoke-Expression $command
        if ($null -ne $result) {
            $result | Out-String -Stream | ForEach-Object {
                [Console]::Out.WriteLine($_)
            }
        }
        [Console]::Out.WriteLine("<<<EXIT:0>>>")
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        [Console]::Out.WriteLine("<<<EXIT:1>>>")
    } finally {
        [Console]::Out.Flush()
    }
}
```

---

## Bash Transpiler

Transforms agent-emitted bash syntax to PowerShell. Implemented as an ordered pipeline of transforms. Uses source-generated regex for AOT compatibility — no runtime reflection.

### Design Principles

- **Agent-bash only** — target the 90% of patterns AI agents actually emit, not full POSIX bash
- **Ordered pipeline** — transforms apply in sequence; earlier transforms win
- **Delegate to ps-bash** — pipe targets route to ps-bash cmdlets, not reimplemented inline
- **Span-based where possible** — minimize allocations for common short commands
- **Fail loudly on unknowns** — unsupported constructs return a descriptive error command rather than silently broken output

### Transform Pipeline Order

```
1. DevNullTransform       — /dev/null → $null, NUL
2. TmpPathTransform       — /tmp/ → $env:TEMP\
3. HomePathTransform      — ~/ → $HOME\
4. ExportTransform        — export VAR=val → $env:VAR = "val"
5. EnvVarTransform        — $VAR → $env:VAR (outside pwsh-native contexts)
6. FileTestTransform      — [ -f x ], [ -d x ], [ -z x ], [ -n x ]
7. PipeTransform          — | grep → | Invoke-Grep, etc.
8. RedirectTransform      — 2>&1, >> passthrough validation
```

### Pipe Command Mappings

| Bash | PowerShell (ps-bash cmdlet) |
|---|---|
| `\| grep "pat"` | `\| Invoke-Grep "pat"` |
| `\| grep -v "pat"` | `\| Invoke-Grep -NotMatch "pat"` |
| `\| grep -i "pat"` | `\| Invoke-Grep -CaseInsensitive "pat"` |
| `\| grep -r "pat" dir` | `\| Invoke-Grep -Recurse "pat" dir` |
| `\| head -n N` | `\| Select-Object -First N` |
| `\| tail -n N` | `\| Select-Object -Last N` |
| `\| wc -l` | `\| Measure-Object -Line \| Select-Object -Expand Lines` |
| `\| sort` | `\| Sort-Object` |
| `\| sort -r` | `\| Sort-Object -Descending` |
| `\| uniq` | `\| Get-Unique` |
| `\| sed 's/x/y/'` | `\| Invoke-Sed 's/x/y/'` |
| `\| awk '{print $N}'` | `\| Invoke-Awk '{print $N}'` |
| `\| find . -name` | `\| Invoke-Find . -Name` |
| `\| cut -d: -f1` | `\| Invoke-Cut -Delimiter : -Field 1` |
| `\| xargs` | `\| Invoke-Xargs` |
| `\| tr 'a' 'b'` | `\| Invoke-Tr 'a' 'b'` |
| `\| tee file` | `\| Tee-Object file` |

### Common Substitutions

| Bash | PowerShell |
|---|---|
| `2> /dev/null` | `2>$null` |
| `> /dev/null` | `>$null` |
| `> /dev/null 2>&1` | `>$null 2>&1` |
| `/tmp/` | `$env:TEMP\` |
| `~/` | `$HOME\` |
| `export FOO=bar` | `$env:FOO = "bar"` |
| `export FOO="bar baz"` | `$env:FOO = "bar baz"` |
| `[ -f path ]` | `Test-Path "path"` |
| `[ -d path ]` | `Test-Path "path" -PathType Container` |
| `[ -z "$VAR" ]` | `[string]::IsNullOrEmpty($env:VAR)` |
| `[ -n "$VAR" ]` | `![string]::IsNullOrEmpty($env:VAR)` |
| `echo $FOO` | `Write-Output $env:FOO` |
| `$?` | `$LASTEXITCODE` |
| `&&` | `;` (pwsh 7 native `&&` also supported) |
| `\|\|` | pwsh 7 native `\|\|` supported |

### BashTranspiler.cs

```csharp
public static class BashTranspiler
{
    private static readonly ITransform[] Pipeline =
    [
        new DevNullTransform(),
        new TmpPathTransform(),
        new HomePathTransform(),
        new ExportTransform(),
        new FileTestTransform(),
        new PipeTransform(),
        new EnvVarTransform(),
        new RedirectTransform(),
    ];

    public static string Transpile(string bashCommand)
    {
        var context = new TranspileContext(bashCommand);
        foreach (var transform in Pipeline)
            transform.Apply(ref context);
        return context.Result;
    }
}

public interface ITransform
{
    void Apply(ref TranspileContext context);
}

public ref struct TranspileContext
{
    public string Result;
    public bool Modified;

    public TranspileContext(string input)
    {
        Result = input;
        Modified = false;
    }
}
```

### Source-Generated Regex (AOT compatible)

```csharp
// In each transform — use GeneratedRegex attribute, not new Regex()
[GeneratedRegex(@"2>\s*/dev/null", RegexOptions.Compiled)]
private static partial Regex DevNullStderr();

[GeneratedRegex(@"(?<!2)>\s*/dev/null", RegexOptions.Compiled)]
private static partial Regex DevNullStdout();

[GeneratedRegex(@"\|\s*grep\s+(-[a-z]+\s+)?""?([^""]+)""?",
    RegexOptions.Compiled)]
private static partial Regex PipeGrep();
```

---

## Entry Point

### Program.cs

```csharp
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;

// Parse arguments
var args = ShellArgs.Parse(Environment.GetCommandLineArgs()[1..]);

// Locate pwsh
string pwshPath;
try
{
    pwshPath = PwshLocator.Locate();
}
catch (PwshNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Interactive mode
if (args.Interactive)
{
    return await InteractiveShell.RunAsync(pwshPath);
}

// Stdin mode
if (args.ReadFromStdin)
{
    var command = await Console.In.ReadToEndAsync();
    args = args with { Command = command };
}

if (args.Command is null)
{
    Console.Error.WriteLine("ps-bash: no command specified");
    return 1;
}

// Transpile
var pwshCommand = BashTranspiler.Transpile(args.Command);

// Execute via persistent worker
await using var worker = await PwshWorker.StartAsync(
    pwshPath,
    workerScriptPath: Environment.GetEnvironmentVariable("PSBASH_WORKER"));

return await worker.ExecuteAsync(pwshCommand);
```

### ShellArgs.cs

```csharp
public record ShellArgs(
    string? Command,
    bool Interactive,
    bool Login,
    bool ReadFromStdin)
{
    public static ShellArgs Parse(string[] args)
    {
        string? command = null;
        bool interactive = false;
        bool login = false;
        bool stdin = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-c" when i + 1 < args.Length:
                    command = args[++i];
                    break;
                case "-i":
                    interactive = true;
                    break;
                case "--login":
                case "-l":
                    login = true;
                    break;
                case "-s":
                    stdin = true;
                    break;
                case "--":
                    break;
            }
        }

        return new ShellArgs(command, interactive, login, stdin);
    }
}
```

---

## Environment Variables

| Variable | Description | Default |
|---|---|---|
| `PSBASH_PWSH` | Full path to `pwsh` executable | Auto-detected |
| `PSBASH_WORKER` | Full path to worker `.ps1` script | `<binary-dir>/ps-bash-worker.ps1` |
| `PSBASH_MODULE` | Full path to ps-bash module dir | `<binary-dir>/Modules/ps-bash` |
| `PSBASH_DEBUG` | Set to `1` to log transpiled commands to stderr | Off |
| `SHELL` | Standard — set to full path of `ps-bash.exe` | N/A |

---

## Distribution

### Slim Package (~6MB)
```
ps-bash-slim/
├── ps-bash.exe              # AOT binary
├── ps-bash                  # AOT binary (Linux/Mac)
├── ps-bash-worker.ps1
└── Modules/
    └── ps-bash/             # PowerShell module
```

Requires: PowerShell 7+ installed on system  
Install via: `winget`, `scoop`, `choco`, `brew`, system package manager

### Full Package (~80MB)
```
ps-bash-full/
├── ps-bash.exe
├── ps-bash-worker.ps1
├── Modules/
│   └── ps-bash/
└── pwsh/                    # bundled PowerShell 7
    ├── pwsh.exe
    └── ...
```

Requires: Nothing  
Use for: Windows containers, air-gapped environments, Dockerfile `COPY`

---

## Build

### Publish Commands

```bash
# Windows x64
dotnet publish src/PsBash.Shell -r win-x64 -c Release -o dist/win-x64

# Linux x64
dotnet publish src/PsBash.Shell -r linux-x64 -c Release -o dist/linux-x64

# macOS ARM
dotnet publish src/PsBash.Shell -r osx-arm64 -c Release -o dist/osx-arm64
```

### CI Matrix (GitHub Actions)

```yaml
strategy:
  matrix:
    include:
      - os: windows-latest
        rid: win-x64
      - os: ubuntu-latest
        rid: linux-x64
      - os: macos-latest
        rid: osx-arm64
```

---

## Testing

### Unit Tests — Transpiler

Each transform has isolated unit tests. No pwsh required.

```csharp
[Theory]
[InlineData("ls 2> /dev/null",         "ls 2>$null")]
[InlineData("ls > /dev/null",          "ls >$null")]
[InlineData("ls > /dev/null 2>&1",     "ls >$null 2>&1")]
[InlineData("cat /tmp/foo",            @"cat $env:TEMP\foo")]
[InlineData("export FOO=bar",          @"$env:FOO = ""bar""")]
[InlineData("[ -f ./file ]",           @"Test-Path ""./file""")]
[InlineData("ls | grep pattern",       "ls | Invoke-Grep \"pattern\"")]
[InlineData("ls | head -n 10",         "ls | Select-Object -First 10")]
[InlineData("ls | wc -l",             "ls | Measure-Object -Line | Select-Object -Expand Lines")]
public void Transpile_CommonPatterns(string bash, string expected)
{
    Assert.Equal(expected, BashTranspiler.Transpile(bash));
}
```

### Integration Tests — Worker

Require pwsh to be installed. Skipped in CI if pwsh not found.

```csharp
[SkippableFact]
public async Task Worker_ExecutesSimpleCommand()
{
    Skip.IfNot(PwshLocator.TryLocate(out var pwsh));
    await using var worker = await PwshWorker.StartAsync(pwsh);
    var exit = await worker.ExecuteAsync("Write-Output 'hello'");
    Assert.Equal(0, exit);
}
```

### End-to-End Tests — Binary

Invoke `ps-bash.exe` as a subprocess, validate stdout/stderr/exit codes.

```csharp
[SkippableFact]
public async Task Binary_DevNullRedirect()
{
    var result = await RunBinary("-c", "Write-Error 'err' 2> /dev/null");
    Assert.Equal(0, result.ExitCode);
    Assert.Empty(result.Stderr);
}
```

---

## Agent Integration

### Claude Code (Windows)
```bash
# In settings or environment
SHELL=C:\tools\ps-bash\ps-bash.exe
```

### opencode
```json
{
  "shell": "C:\\tools\\ps-bash\\ps-bash.exe"
}
```

### Gemini CLI
```bash
export SHELL=/usr/local/bin/ps-bash
```

### Dockerfile (Windows container)
```dockerfile
FROM mcr.microsoft.com/windows/nanoserver:ltsc2022
COPY ps-bash-full/ C:/ps-bash/
ENV SHELL=C:/ps-bash/ps-bash.exe
RUN C:/ps-bash/ps-bash.exe -c "Get-Command Invoke-Grep"
```

### Dockerfile (Linux container, slim)
```dockerfile
FROM mcr.microsoft.com/powershell:latest
COPY ps-bash-slim/ /usr/local/ps-bash/
ENV SHELL=/usr/local/ps-bash/ps-bash
```

---

## Debug Mode

Set `PSBASH_DEBUG=1` to log transpiled commands to stderr before execution. Useful for diagnosing translation issues.

```
[ps-bash] input:      ls -la | grep ".cs" 2> /dev/null
[ps-bash] transpiled: Get-ChildItem -Force | Invoke-Grep "\.cs" 2>$null
[ps-bash] pwsh:       /usr/bin/pwsh
[ps-bash] worker:     /usr/local/ps-bash/ps-bash-worker.ps1
[ps-bash] exit:       0
```

---

## Open Questions / Future Work

- **Interactive mode** — full REPL with readline-style editing; v1 can stub this as `pwsh` passthrough
- **pwsh version check** — warn if pwsh < 7.0 (some features like native `&&`/`||` require 7+)
- **Transpiler coverage tracking** — log untranslated patterns in debug mode to inform future transform additions
- **opencode PR** — once binary is stable, contribute Windows shell detection to opencode that auto-detects ps-bash.exe
- **Gemini CLI PR** — address open P1 issue #3126 with ps-bash as the recommended Windows shell
- **winget/scoop/choco packages** — coordinate with package maintainers post-stable release

---

## Relationship to Existing ps-bash Module

This specification adds `PsBash.Core` and `PsBash.Shell` as new projects in the existing solution. The `PsBash.Module` project is **unchanged**. The shell binary is a consumer of the module at runtime (via pwsh), not a compile-time dependency.

The module continues to work standalone via `Install-Module ps-bash` for users who only want PowerShell cmdlets. The binary extends that to make it usable as a system shell by AI agents and CI pipelines.
