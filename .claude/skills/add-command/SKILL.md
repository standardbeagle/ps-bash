---
name: add-command
description: Add a new bash command to the ps-bash transpiler and runtime
disable-model-invocation: true
---

文言：先查重；PsEmitter加case用EmitPassthrough；psm1實作Invoke-Bash*；加transpile+e2e測試；test.sh；重建exe；清module快取。

Add bash command support for: $ARGUMENTS

## STEPS
1. **Dup check** — `TryEmitMappedCommand` in `src/PsBash.Transpiler/Parser/PsEmitter.cs` + `Invoke-Bash*` in `src/PsBash.Module/PsBash.psm1`.
2. **Emitter map** (`PsEmitter.cs` `TryEmitMappedCommand`): `case "cmd": result = EmitPassthrough("Invoke-BashCmd", args); return true;`. NO custom emit method — `EmitPassthrough` only.
3. **Runtime** (`PsBash.psm1` `Invoke-BashCmd`): `$Arguments=[string[]]$args; $pipelineInput=@($input)`; manual flag loop; pipeline mode passes ORIGINAL objects (defensive split for multi-line — see @.claude/rules/runtime.md); file mode; text → `Emit-BashLine`, typed → `New-BashObject`.
4. **Transpile test** (`PsEmitterTests.cs`): assert `PsEmitter.Transpile("… | cmd -flag")` contains `Invoke-BashCmd`.
5. **e2e test**: `dotnet run --project src/PsBash.Shell -- -c '…'`.
6. **Run**: `./scripts/test.sh`. 7. **Rebuild**: `dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained`. 8. **Clear cache**: `rm -rf "$TEMP/ps-bash/module-*"`.
