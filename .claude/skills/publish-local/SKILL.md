---
name: publish-local
description: Rebuild and publish the local ps-bash exe for testing in opencode
disable-model-invocation: true
---

Rebuild the local ps-bash binary for opencode testing.

## Steps

1. **Run tests**:
   ```bash
   ./scripts/test.sh
   ```
   Abort if any tests fail.

2. **Publish release build** (NativeAOT disabled due to missing vswhere on this machine):
   ```bash
   dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained
   ```
   Output: `src/PsBash.Shell/bin/Release/net10.0/win-x64/publish/ps-bash.exe`

3. **Clear module cache** so the fresh embedded module is extracted on next run:
   ```bash
   rm -rf "$TEMP/ps-bash/module-1.0.0.0"
   ```

4. **Verify**: The exe is referenced by `~/work/opencode/ps-code.ps1`.
   Test with: `dotnet run --project src/PsBash.Shell -- -c 'echo hello'`
