---
name: add-command
description: Add a new bash command to the ps-bash transpiler and runtime
disable-model-invocation: true
---

Add bash command support for: $ARGUMENTS

## Steps

1. **Check if already implemented**: Search `TryEmitMappedCommand` in `src/PsBash.Core/Parser/PsEmitter.cs` and `Invoke-Bash*` in `src/PsBash.Module/PsBash.psm1`

2. **Add emitter mapping**: In `PsEmitter.cs` `TryEmitMappedCommand`, add:
   ```csharp
   case "commandname":
       result = EmitPassthrough("Invoke-BashCommandname", args);
       return true;
   ```
   Do NOT create a custom emit method — use `EmitPassthrough`.

3. **Implement runtime function**: In `PsBash.psm1`, add `Invoke-BashCommandname` following the arg parsing pattern:
   ```powershell
   function Invoke-BashCommandname {
       $Arguments = [string[]]$args
       $pipelineInput = @($input)
       # Manual flag parsing loop
       # Handle pipeline mode (split multi-line BashText)
       # Handle file mode
       # Output via New-BashObject -BashText
   }
   ```

4. **Add transpile test**: In `PsEmitterTests.cs`:
   ```csharp
   [Fact]
   public void Transpile_CommandnameInPipeline_EmitsPassthrough()
   {
       var result = PsEmitter.Transpile("echo test | commandname -flag");
       Assert.Contains("Invoke-BashCommandname", result);
   }
   ```

5. **Add e2e test**: Test actual output via `dotnet run --project src/PsBash.Shell -- -c '...'`

6. **Run tests**: `./scripts/test.sh`

7. **Rebuild local exe**: `dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained`

8. **Clear module cache**: `rm -rf "$TEMP/ps-bash/module-1.0.0.0"`
