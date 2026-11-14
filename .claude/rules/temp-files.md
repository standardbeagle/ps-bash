---
paths:
  - "src/PsBash.Core/Runtime/**"
---

# TEMP FILES. Ref: `docs/specs/runtime-functions.md` (Temp File Strategy)

文言：皆置 ps-bash/ 之下，勿用GetTempFileName；按時間戳失效；共享用FileShare.ReadWrite。

## ALL temp under `ps-bash/`
Never `Path.GetTempPath()` directly, never `Path.GetTempFileName()`. Always under `ps-bash/`:
- module extraction → `ps-bash/module-{version}-{hash}/` (marker = hash; `.inuse` held shared, old dirs pruned)
- worker script → `ps-bash/module-{version}-{hash}/ps-bash-worker.ps1`
- process substitution → `ps-bash/proc-sub/psub-{random}` (reaper deletes only `psub-*`: mktemp shares the dir)

## INVALIDATION
Re-extract when assembly `LastWriteTimeUtc` > extracted file's. (Extractor compares; missing file = extract.)

## CONCURRENCY
Shared temp files: `FileShare.ReadWrite`. NEVER `FileShare.None` (parallel processes lock out).
