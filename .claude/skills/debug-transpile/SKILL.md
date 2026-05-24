---
name: debug-transpile
description: Debug a bash command that produces wrong output or crashes in ps-bash
---

文言：觀PSBASH_DEBUG之transpiled；以源module試；清快取；pwsh直測函數；查引號(花括/逗號/$)；查多行BashText；改前先寫回歸測試。

Debug transpile issue for: $ARGUMENTS

## STEPS
1. **See transpile**: `PSBASH_DEBUG=1 dotnet run --project src/PsBash.Shell -- -c '<cmd>'` → stderr `[ps-bash] transpiled:`.
2. **Source module** (bypass embedded cache): `PSBASH_MODULE=./src/PsBash.Module/PsBash.psd1 dotnet run … -c '<cmd>'`. Works only with it → embedded module stale.
3. **Clear cache**: `rm -rf "$TEMP/ps-bash/module-*"`.
4. **Test runtime directly**: `pwsh -NoProfile -Command "Import-Module ./src/PsBash.Module/PsBash.psd1 -DisableNameChecking; <test>"`.
5. **Quoting** (PS misreads args): `{}` → scriptblock; `,` → array sep (both need `NeedsPassthroughQuoting`); `$` → var expansion (single vs double quotes).
6. **Multi-line BashText**: wrong results from piped input with newlines → runtime needs the multi-line split pattern.
7. **Regression test** at the right layer BEFORE fixing.
