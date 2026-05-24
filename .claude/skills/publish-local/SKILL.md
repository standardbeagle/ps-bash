---
name: publish-local
description: Rebuild and publish the local ps-bash exe for testing in opencode
disable-model-invocation: true
---

文言：測試→publish release(AOT關)→清 module-* 快取→驗證。或逕用 install-local.ps1。

Rebuild the local ps-bash binary. (Or just `pwsh install-local.ps1` — it does build + test + deploy.)

## STEPS
1. `./scripts/test.sh` — abort on failure.
2. `dotnet publish src/PsBash.Shell -c Release -r win-x64 -p:PublishAot=false --self-contained` (AOT off: no vswhere here). → `src/PsBash.Shell/bin/Release/net10.0/win-x64/publish/ps-bash.exe`.
3. Clear cache so the fresh embedded module re-extracts: `rm -rf "$TEMP/ps-bash/module-*"`.
4. Verify: `dotnet run --project src/PsBash.Shell -- -c 'echo hello'`. (exe used by `~/work/opencode/ps-code.ps1`.)
