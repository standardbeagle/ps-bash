# PsBash.Transpiler

The bash → PowerShell transpiler that powers [PsBash](https://github.com/standardbeagle/ps-bash):
a hand-written lexer + recursive-descent parser (AST modeled on
[Oils/OSH](https://github.com/oilshell/oil)) and a PowerShell emitter. It is a
**leaf library with no PsBash dependencies** (only [Parlot](https://www.nuget.org/packages/Parlot)),
so you can embed bash-to-PowerShell translation in your own app — for example to
build your own shell on top of a PowerShell runspace.

```
dotnet add package PsBash.Transpiler
```

## Transpile bash to PowerShell

```csharp
using PsBash.Core.Transpiler; // assembly: PsBash.Transpiler

string ps = BashTranspiler.Transpile("ls -la | grep .log | sort -k5 -rn | head -5");
// ps == "Invoke-BashLs -la | Invoke-BashGrep .log | Invoke-BashSort -k5 -rn | Invoke-BashHead -5"
```

`Transpile` accepts an optional `TranspileContext` (`Default`, or `Eval` for
`eval`-style in-process re-transpilation). Use `TranspileWithMap` when you need
to map a PowerShell runtime error back to the originating bash line:

```csharp
TranspileResult result = BashTranspiler.TranspileWithMap("for x in a b c; do echo $x; done");
string ps = result.PowerShell;       // emitted PowerShell
// result.LineMap: emitted line -> source bash line
```

## Build your own shell

The emitted PowerShell calls `Invoke-Bash*` commands (e.g. `Invoke-BashGrep`).
To run it with full fidelity, load the ps-bash **runtime module** into your
runspace, then execute the transpiled text:

```csharp
using System.Management.Automation;
using System.Management.Automation.Runspaces;

using var rs = RunspaceFactory.CreateRunspace();
rs.Open();
using var ps = PowerShell.Create();
ps.Runspace = rs;

// Load the runtime: the PsBash module (Invoke-Bash* commands) ships embedded in
// the PsBash.Core package and is extracted at runtime — add a reference to
// `PsBash.Core` and import the extracted module, or `Install-Module PsBash` /
// `PsBash.Cmdlets` from the PowerShell Gallery into the runspace.
ps.AddScript("Import-Module PsBash").Invoke();
ps.Commands.Clear();

string transpiled = BashTranspiler.Transpile(userBashLine);
foreach (var item in ps.AddScript(transpiled).Invoke())
    Console.WriteLine(item);
```

A REPL is then just: read a line → `BashTranspiler.Transpile` → run in the
runspace → print. Add history, completion, and prompt handling to taste. See the
ps-bash `PsBash.Shell` project for a complete launcher + host implementation.

## Scope

- **In scope:** bash syntax → PowerShell text. Lexer, parser, AST (`PsBash.Core.Parser.Ast`), emitter.
- **Out of scope (use `PsBash.Core`):** the runtime `Invoke-Bash*` command implementations, the IPC host, and process orchestration.

## License

MIT — see the [repository](https://github.com/standardbeagle/ps-bash).
