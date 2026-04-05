# ps-bash project instructions

## Architecture

```
bash input → BashLexer → BashParser → PsEmitter → PwshWorker → Invoke-Bash* runtime
```

- **Lexer/Parser**: tokenizes and parses bash into an AST modeled on Oils syntax.asdl
- **Emitter**: maps bash commands to `Invoke-Bash*` functions via **passthrough** — forwards all args, never translates flags
- **Runtime**: PowerShell module (`PsBash.psm1`) with full bash-compatible flag parsing in each function

## The Passthrough Principle

The emitter maps command names (e.g., `head` → `Invoke-BashHead`) and forwards all arguments unchanged. The runtime functions handle all flag parsing. Never translate bash flags to PowerShell parameters in the emitter.

## Running Tests

Always use `scripts/test.sh` instead of `dotnet test` directly.
It shuts down MSBuild server nodes and testhost processes on exit.

```bash
./scripts/test.sh                          # all tests
./scripts/test.sh --filter "MyTest"        # specific test
./scripts/test.sh src/PsBash.Core.Tests    # specific project
```

Do NOT use bare `dotnet test ...` — it leaks MSBuild worker nodes and testhost processes.

## Specs

- @docs/specs/parser-grammar.md — tokens, AST nodes, grammar productions, Oils gap analysis
- @docs/specs/emitter-strategy.md — passthrough principle, pipe mappings, anti-patterns
- @docs/specs/runtime-functions.md — BashObject model, command reference, temp files
