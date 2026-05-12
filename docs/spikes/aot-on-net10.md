# PTY-0 Spike: Does PowerShell SDK still block AOT on .NET 10?

**Branch:** `pty-aot-probe` (do not merge to main)
**Loop task:** AEACP6UqDUBr
**Dart task:** 4V0QRPY3bk4N
**Date:** 2026-05-12
**Tooling:**
- Linux: .NET 10 SDK `10.0.100-rc.2.25502.107` (installed for the probe — Linux side previously had 8.0/9.0 only)
- Windows: .NET 10 SDK `10.0.100-rc.2.25502.107` (installed via `dotnet-install.ps1` for the probe — Windows side previously had runtimes only, no SDK)
- PowerShell SDK pinned in csproj: `Microsoft.PowerShell.SDK 7.4.*` (resolved 7.4.15)

## Configuration probed

`src/PsBash.Host/PsBash.Host.csproj` flipped:

```diff
- <PublishAot>false</PublishAot>
+ <PublishAot>true</PublishAot>
+ <InvariantGlobalization>true</InvariantGlobalization>
-     <NoWarn>$(NoWarn);IL2026</NoWarn>
+     <!-- IL2026 NoWarn intentionally removed for AOT probe to capture all warnings -->
```

The `IL2026` suppression was removed so the probe counts every warning.

## Publish results per RID

| RID | Command | Exit | Outcome |
|---|---|---|---|
| `linux-x64` | `dotnet publish src/PsBash.Host -r linux-x64 -c Release /p:PublishAot=true /p:InvariantGlobalization=true` | 0 | "Succeeded" — produced `ps-bash-host` (1.18 MB), but ILC emitted a hard signal that the entry method **will always throw** |
| `win-x64` (Linux host) | same with `-r win-x64` | non-zero | `error : Cross-OS native compilation is not supported.` — expected; AOT requires running ILC on the target OS |
| `win-x64` (Windows host) | same, run via `powershell.exe` interop | non-zero | `error : Platform linker not found. Ensure you have ... Desktop Development for C++ workload in Visual Studio.` — tooling prereq (MSVC linker) missing on this Windows host. Could not complete native compilation on this machine. |

`linux-x64`'s exit-0 is misleading. ILC printed this verbatim line during native code generation:

```
ILC: Method '[ps-bash-host]PsBash.Host.Program+<Main>d__0.MoveNext()' will always throw because: Failed to load assembly 'System.Management.Automation'
```

This is the canary that confirms the assumption.

## Warning counts (linux-x64)

Counted from `artifacts/aot-probe/publish-linux-x64.log` after a clean publish:

| Category | Count | Top occurrence |
|---|---|---|
| IL2026 | **1** | `src/PsBash.Host/Runtime/SdkRunspace.cs(129,27)` — `Assembly.GetTypes()` is RUC-attributed |
| IL3050 | **0** | none |
| IL3053 | **0** | none |
| `ILC: ... will always throw` | **1** | `Program.<Main>d__0.MoveNext()` — failed to load `System.Management.Automation` |

**The low warning count is misleading.** The Microsoft.PowerShell.SDK 7.4.15 package only ships ref assemblies under `ref/net8.0/`; there is no `ref/net10.0/` slice. ILC could not resolve the SMA reference under `net10.0` and bailed out by stamping the entry method with `IL3000`-style "will always throw" — so SMA's internals (cmdlet binder, scriptblock compiler, `Reflection.Emit` paths) were never analyzed at all. We did **not** get a flood of IL3050/IL3053 because the SMA graph was never walked, not because it would be clean.

## Runtime behavior (linux-x64)

The published `ps-bash-host` binary aborts immediately on launch — before any argument parsing, before any pipeline executes. All three required smoke tests produced **identical** output:

| Test | Result |
|---|---|
| `./ps-bash-host -c "Invoke-BashEcho hi"` | abort (exit 134, SIGABRT) |
| `./ps-bash-host -c "1..3 \| Invoke-BashSort"` | abort (exit 134, SIGABRT) |
| `./ps-bash-host -c "1..3 \| %{ \$_ * 2 }"` | abort (exit 134, SIGABRT) |

Verbatim stderr (identical across all three):

```
Unhandled exception. System.IO.FileNotFoundException: Could not find file 'System.Management.Automation'.
File name: 'System.Management.Automation'
   at Internal.Runtime.TypeLoaderExceptionHelper.CreateFileNotFoundException(ExceptionStringID, String) + 0x4a
   at PsBash.Host.Program.<Main>d__0.MoveNext() + 0x12
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine&) + 0x57
   at PsBash.Host.Program.Main(String[]) + 0x68
   at PsBash.Host.Program.<Main>(String[] args) + 0xd
```

Confirmation that the SMA assembly was simply not statically linked into the AOT image: no `*System.Management.Automation*` file exists under `src/PsBash.Host/bin/Release/net10.0/linux-x64/publish/`.

## Why this fails

Two compounding blockers, in order of independence:

1. **SMA has no `ref/net10.0/` ref assembly.** `Microsoft.PowerShell.SDK 7.4.15` ships only `ref/net8.0/`. ILC cannot consume that under a `net10.0` target the way the runtime JIT can, so it short-circuits and stamps `Program.Main` as always-throwing. This is the proximate failure on this branch.
2. **SMA depends on `System.Reflection.Emit`.** Cmdlet binder + scriptblock compiler dynamically emit IL at runtime. Even if PS shipped a net10 ref assembly tomorrow, ILC's static analysis cannot fold `Reflection.Emit`-based code generation into a native image — these paths would surface as IL3050 ("RequiresDynamicCode") on every emit site. This is the deeper structural blocker the EPIC's decision rests on.

The probe could not falsify (2) directly because (1) blocks ILC from reaching that analysis stage. Both confirm the EPIC's assumption holds.

## Recommendation

**Keep the launcher/host split.** The EPIC stands as written.

One-line reasoning: SMA 7.4.x has no net10 ref assembly, ILC bails out before analyzing the binder, and even past that bail-out `Reflection.Emit` in SMA's cmdlet binder + scriptblock compiler still forces dynamic code paths AOT cannot fold — so `PsBash.Host` cannot collapse into the AOT'd launcher without abandoning PowerShell SDK entirely.

**Re-spike triggers:**

- Microsoft.PowerShell.SDK ships a `ref/net10.0/` slice (would unblock the proximate failure and let ILC actually analyze SMA — at which point we'd learn the real IL3050 count).
- PowerShell 7.5+ replaces `Reflection.Emit` usage in the cmdlet binder / scriptblock compiler with AOT-friendly equivalents (e.g. source-generated dispatch).
- Either signal warrants reopening this probe before PTY-2/PTY-4 commits to the split as permanent.

## Artifacts

- `artifacts/aot-probe/publish-linux-x64.log` — full linux-x64 publish output
- `artifacts/aot-probe/publish-win-x64.log` — full win-x64 publish output (from Windows host)
- `artifacts/aot-probe/smoke-tests-linux-x64.txt` — verbatim smoke-test transcript
- `src/PsBash.Host/PsBash.Host.csproj` — flipped to `PublishAot=true` on `pty-aot-probe` branch (do not merge)
