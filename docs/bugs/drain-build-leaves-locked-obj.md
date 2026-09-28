# Bug: an aborted drain build leaves MSBuild nodes or the compiler server holding obj/bin

**Found:** 2026-09-27 (beagle-ab2 Windows, while draining ps-bash tasks)
**Severity:** High — a locked build output stalls the drain; the lock probe cannot name the holder
**Fixed:** `.tman.kdl` — every alias runs with `-nodeReuse:false` and `-p:UseSharedCompilation=false`

## Symptom

The ps-bash drain stopped three times in one day with:

```
step 'tman-build' is blocked by a locked build output ... holder facts: none could be named
```

A manual `dotnet build` then failed on files it writes **itself**:

```
Error writing to source link file 'obj\Debug\net10.0\PsBash.Host.sourcelink.json':
  The process cannot access the file
CS2012: Cannot open 'src\PsBash.Shell\obj\Debug\net10.0\ps-bash.dll' for writing
```

Seven `MSBuild.dll /nodemode:1 /nodeReuse:true` worker nodes from a build the drain
had aborted were still alive holding handles. No process had a ps-bash module loaded,
so the drain's lock probe — which names a holder by loading module facts — could not
identify them, and the task was set aside as blocked. After `dotnet build-server
shutdown` and a build with `-nodeReuse:false -p:UseSharedCompilation=false` the build
succeeded.

## Mechanism

Two long-lived processes outlive the `dotnet` that started them:

- **MSBuild worker nodes** (`/nodeReuse:true`, the default). An aborted build leaves
  them parked for the next build; they hold handles into `obj/`.
- **VBCSCompiler**, the Roslyn shared-compiler server. It is started on the first
  compile and persists by design; it holds `obj` intermediates.

Neither carries the ps-bash module, so a probe that names holders by module cannot
name them. The result is the exact "locked output, holder unknown" report.

Reproduced on beagle-ab2 with `dotnet build src/PsBash.Core -t:Rebuild
-nodeReuse:false` (shared compilation left ON): after exit, one `VBCSCompiler`
process remained, holding `PsBash.Core`'s obj intermediates. Adding
`-p:UseSharedCompilation=false` left zero compile-server processes.

## Fix

`.tman.kdl` — every alias the drain runs passes `-nodeReuse:false` **and**
`-p:UseSharedCompilation=false`, so an aborted run leaves no process holding
obj/bin:

| alias | before | after |
|---|---|---|
| `build` | `-nodeReuse:false` (from an earlier pass) | `+ -p:UseSharedCompilation=false` |
| `test` | `-nodeReuse:false` | `+ -p:UseSharedCompilation=false` |
| `test-proj` | `-nodeReuse:false` | `+ -p:UseSharedCompilation=false` |

`-nodeReuse:false` alone is not enough: it stops worker-node reuse but does **not**
stop the Roslyn compiler server, which is the process the repro left behind.

### Why the flag form, not an alias `env`

The task allowed either flags or `MSBUILDDISABLENODEREUSE=1` /
`DOTNET_CLI_USE_MSBUILD_SERVER=0` in an alias `env`. tman 0.5.0 has **no alias
`env` node** — its own help says to use `env sh -c "tman run --alias …"` to inject
environment variables into a run. The flag form is therefore the only mechanism
available, and it is also what the existing `build` alias already used.

## Cost (measured on beagle-ab2, `dotnet build -c Debug -f net10.0 -m:1`)

| build | shared compilation ON | OFF (alias) |
|---|---|---|
| full solution rebuild (`-t:Rebuild`) | 78 s, leaves 1 `VBCSCompiler` | 134 s, leaves none |
| warm incremental | 12 s | 10 s |

The cost is real but only on **cold/full rebuilds** (~56 s, +72%); warm
incremental builds — the common drain case after the first build — are unchanged
within noise. The three-times-a-day drain stall and the manual
`dotnet build-server shutdown` it forced cost more than a minute of cold-rebuild
time, so the flag form is the right trade.

## Verification

- `tman run --alias build` then process list: 0 `MSBuild`/`VBCSCompiler`.
- `tman run --alias test-proj -- src/PsBash.Core.Tests`: 1531 passed, 3 skipped,
  0 failed; 0 `MSBuild`/`VBCSCompiler` after exit.
- Killed build (`tman kill build` mid-run) → 0 leftover processes → fresh
  `tman run --alias build` succeeds, no locked obj file.

## Regression tests

None new. The behaviour under test is process lifetime of `dotnet`-spawned
servers, not repository code; there is no xunit seam for it. The acceptance
evidence is the process-list checks recorded above and the alias arguments
pinned in `.tman.kdl`, which any future edit to the file can be diffed against.
