# PsBash.Core

Bash-to-PowerShell transpiler library. Parses bash commands into an AST and emits equivalent PowerShell.

## Quick Start

```csharp
using PsBash.Core.Transpiler;
using PsBash.Core.Runtime;

// Transpile a bash command to PowerShell
string ps = BashTranspiler.Transpile("echo hello | grep -i 'world' | head -n 5");
// Result: "echo hello | Invoke-BashGrep -i 'world' | Invoke-BashHead -n 5"

// Extract the runtime module (needed for Invoke-Bash* functions)
string modulePath = ModuleExtractor.ExtractEmbedded();
// Load into your pwsh session: Import-Module $modulePath
```

## Architecture

```
bash input --> BashLexer --> BashParser --> PsEmitter --> PowerShell string
```

- **BashLexer**: Tokenizes bash input into typed tokens
- **BashParser**: Recursive-descent parser producing an AST (based on Oils/OSH syntax.asdl)
- **PsEmitter**: Walks the AST and emits PowerShell, mapping bash commands to `Invoke-Bash*` runtime functions

## API Reference

### BashTranspiler (recommended entry point)

```csharp
// Transpile bash to PowerShell. Throws ParseException on invalid input.
string ps = BashTranspiler.Transpile("ls -la | grep '.txt'");
```

### PsEmitter (lower-level access)

```csharp
// Transpile with null return on parse failure (no exception)
string? ps = PsEmitter.Transpile("echo hello");

// Parse then emit separately (for AST inspection)
Command? ast = BashParser.Parse("echo hello");
string ps = PsEmitter.Emit(ast);
```

### ModuleExtractor (runtime setup)

```csharp
// Extract the embedded PsBash PowerShell module to a temp directory.
// Returns path to PsBash.psd1. Thread-safe, cached by assembly version.
string psd1Path = ModuleExtractor.ExtractEmbedded();
```

### BashParser + AST (for custom processing)

```csharp
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

Command? ast = BashParser.Parse("echo hello | wc -l");
// ast is Command.Pipeline with two Command.Simple children
```

## Runtime Module

The transpiler emits calls to `Invoke-Bash*` functions (e.g., `Invoke-BashGrep`, `Invoke-BashHead`).
These are provided by the embedded PowerShell module. Extract it once per session:

```csharp
string modulePath = ModuleExtractor.ExtractEmbedded();
// Then in PowerShell: Import-Module $modulePath
```

## Thread Safety

- `BashTranspiler.Transpile()` and `PsEmitter.Transpile()` are thread-safe
- `ModuleExtractor.ExtractEmbedded()` is thread-safe (uses file locking)
- The parser uses `[ThreadStatic]` for loop variable tracking
