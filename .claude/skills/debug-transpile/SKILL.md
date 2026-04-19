---
name: debug-transpile
description: Debug a bash command that produces wrong output or crashes in ps-bash
---

Debug transpilation issue for: $ARGUMENTS

## Diagnostic Steps

1. **Check transpiled output**: Run with `PSBASH_DEBUG=1` to see what the emitter produces:
   ```bash
   PSBASH_DEBUG=1 dotnet run --project src/PsBash.Shell -- -c 'your command here'
   ```
   Check stderr for `[ps-bash] transpiled:` line.

2. **Test with source module**: Bypass the embedded module cache to use current source:
   ```bash
   PSBASH_MODULE=./src/PsBash.Module/PsBash.psd1 dotnet run --project src/PsBash.Shell -- -c 'your command here'
   ```
   If this works but without PSBASH_MODULE it doesn't, the embedded module is stale.

3. **Clear module cache**: Remove the cached extraction:
   ```bash
   rm -rf "$TEMP/ps-bash/module-1.0.0.0"
   ```

4. **Test runtime directly**: Load the module in pwsh and test the function:
   ```powershell
   pwsh -NoProfile -Command "Import-Module ./src/PsBash.Module/PsBash.psd1 -DisableNameChecking; your-test-here"
   ```

5. **Check for quoting issues**: If PowerShell misinterprets args:
   - `{}` → scriptblock (need quoting in emitter via NeedsPassthroughQuoting)
   - `,` → array separator (need quoting)
   - `$` → variable expansion (check single vs double quotes)

6. **Check for multi-line BashText**: If a command gets wrong results from piped input with embedded newlines, the runtime function needs the multi-line split pattern.

7. **Write regression test** at the appropriate layer before fixing.
