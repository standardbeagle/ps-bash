---
paths:
  - "src/PsBash.Core/Runtime/**"
---

# Temp File Conventions

Reference: @docs/specs/runtime-functions.md (section 7)

## All temp files under `ps-bash/` subdirectory

Never write directly to `Path.GetTempPath()` or use `Path.GetTempFileName()`. All temp files go under `ps-bash/` subdirectory:

| Component | Path | Strategy |
|---|---|---|
| Module extraction | `ps-bash/module-{version}/` | Marker file + timestamp invalidation |
| Worker script | `ps-bash/module-{version}/ps-bash-worker.ps1` | Timestamp invalidation |
| Process substitution | `ps-bash/proc-sub/{random}` | Ephemeral, cleaned on error |

## Cache Invalidation

The module extractor and worker use assembly-timestamp-based invalidation:
- Check if extracted file exists
- Compare assembly LastWriteTimeUtc vs extracted file LastWriteTimeUtc
- Re-extract if assembly is newer

## Concurrency

Use `FileShare.ReadWrite` when writing extracted files so parallel processes don't lock each other out. Never use `FileShare.None` for shared temp files.
