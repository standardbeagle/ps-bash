# On-CD Hook Cmdlet API Specification

This document specifies the hook registry and cmdlet API for directory-change
hooks (`chpwd`) and prompt hooks (`PROMPT_COMMAND`-equivalent) in the ps-bash
interactive shell.

**Status:** DESIGNED — Specification complete. Implementation pending.

Target implementation files:
- `src/PsBash.Cmdlets/HookRegistry.cs` -- hook storage and firing logic
- `src/PsBash.Cmdlets/RegisterBashChpwdHookCommand.cs` -- `Register-BashChpwdHook`
- `src/PsBash.Cmdlets/RegisterBashPromptHookCommand.cs` -- `Register-BashPromptHook`
- `src/PsBash.Cmdlets/UnregisterBashChpwdHookCommand.cs` -- `Unregister-BashChpwdHook`
- `src/PsBash.Cmdlets/UnregisterBashPromptHookCommand.cs` -- `Unregister-BashPromptHook`
- `src/PsBash.Cmdlets/GetBashHookCommand.cs` -- `Get-BashHook`

---

## 1. Overview

ps-bash supports two categories of hooks that tools like `fnm`, `direnv`, and
`starship` depend on:

| Hook Kind | bash/zsh Equivalent | Fires When |
|-----------|---------------------|------------|
| `ChpwdHook` | `chpwd` function (zsh) | Directory changes between prompt ticks |
| `PromptHook` | `PROMPT_COMMAND` (bash) | Every prompt tick, unconditionally |

Both hook kinds are name-keyed so that tools can register, replace, and
unregister their own hook without disturbing hooks registered by other tools.

---

## 2. Cmdlet API

### 2.1 Register-BashChpwdHook

Registers a scriptblock to run when the current directory changes.

```powershell
Register-BashChpwdHook -Name <string> -ScriptBlock <scriptblock>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `-Name` | `string` | Yes | Unique name for this hook. Re-registering an existing name replaces the hook atomically. |
| `-ScriptBlock` | `scriptblock` | Yes | Code to run on directory change. Receives `$OldPath` and `$NewPath` as named variables in the scriptblock's scope. |

**Behavior:**
- Adds or replaces the hook in the registry under `(ChpwdHook, Name)`.
- The scriptblock runs in the **caller's global process scope** (see Section 4).
- Idempotent: calling again with the same name replaces the previous scriptblock.

**Example:**

```powershell
# fnm: switch Node version on cd
Register-BashChpwdHook -Name 'fnm' -ScriptBlock {
    param($OldPath, $NewPath)
    fnm use --silent-if-unchanged
}

# direnv: load/unload .envrc on cd
Register-BashChpwdHook -Name 'direnv' -ScriptBlock {
    param($OldPath, $NewPath)
    direnv export pwsh | Invoke-Expression
}
```

---

### 2.2 Register-BashPromptHook

Registers a scriptblock to run on every prompt tick.

```powershell
Register-BashPromptHook -Name <string> -ScriptBlock <scriptblock>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `-Name` | `string` | Yes | Unique name for this hook. Re-registering an existing name replaces the hook atomically. |
| `-ScriptBlock` | `scriptblock` | Yes | Code to run on every prompt. No parameters are passed; hooks read state from globals or environment. |

**Behavior:**
- Adds or replaces the hook in the registry under `(PromptHook, Name)`.
- Runs on **every prompt tick** regardless of directory change.
- Runs in the **caller's global process scope** (see Section 4).
- Idempotent: calling again with the same name replaces the previous scriptblock.

**Example:**

```powershell
# starship: update prompt string before display
Register-BashPromptHook -Name 'starship' -ScriptBlock {
    $env:STARSHIP_SHELL = 'bash'
    $global:BashPromptString = starship prompt
}

# PROMPT_COMMAND equivalent: update terminal title
Register-BashPromptHook -Name 'title' -ScriptBlock {
    $host.UI.RawUI.WindowTitle = "ps-bash: $(Split-Path -Leaf $PWD)"
}
```

---

### 2.3 Unregister-BashChpwdHook

Removes a previously registered chpwd hook.

```powershell
Unregister-BashChpwdHook -Name <string>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `-Name` | `string` | Yes | Name of the hook to remove. No-op if the name is not registered. |

**Behavior:**
- Removes the hook from the registry atomically.
- If the name does not exist, the call succeeds silently (no error thrown).

**Example:**

```powershell
# deactivate: unregister fnm hook
Unregister-BashChpwdHook -Name 'fnm'
```

---

### 2.4 Unregister-BashPromptHook

Removes a previously registered prompt hook.

```powershell
Unregister-BashPromptHook -Name <string>
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `-Name` | `string` | Yes | Name of the hook to remove. No-op if the name is not registered. |

**Behavior:**
- Removes the hook from the registry atomically.
- If the name does not exist, the call succeeds silently (no error thrown).

---

### 2.5 Get-BashHook

Lists registered hooks.

```powershell
Get-BashHook [-Kind <HookKind>]
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `-Kind` | `HookKind` enum | No | Filter by kind: `ChpwdHook` or `PromptHook`. Omit to return all hooks. |

**Output type:** `BashHookInfo` objects with properties:

| Property | Type | Description |
|----------|------|-------------|
| `Kind` | `HookKind` | `ChpwdHook` or `PromptHook` |
| `Name` | `string` | Hook name |
| `ScriptBlock` | `scriptblock` | The registered scriptblock |

**Example:**

```powershell
Get-BashHook
# Kind         Name      ScriptBlock
# ----         ----      -----------
# ChpwdHook   fnm       { param($OldPath, $NewPath) fnm use --silent... }
# ChpwdHook   direnv    { param($OldPath, $NewPath) direnv export pwsh... }
# PromptHook  starship  { $env:STARSHIP_SHELL = 'bash'; $global:Bash... }

Get-BashHook -Kind ChpwdHook
# Returns only ChpwdHook entries.
```

---

## 3. Storage

### 3.1 HookRegistry

The hook registry is a singleton stored in `PsBash.Cmdlets` module session state
(not per-runspace). All five cmdlets access the same registry instance.

```csharp
internal sealed class HookRegistry
{
    // Key: (HookKind, Name). Value: ScriptBlock.
    private readonly ConcurrentDictionary<(HookKind Kind, string Name), ScriptBlock> _hooks = new();

    public static HookRegistry Instance { get; } = new();

    public void Register(HookKind kind, string name, ScriptBlock scriptBlock)
        => _hooks[(kind, name)] = scriptBlock;

    public bool Unregister(HookKind kind, string name)
        => _hooks.TryRemove((kind, name), out _);

    public IEnumerable<(HookKind Kind, string Name, ScriptBlock ScriptBlock)> GetAll()
        => _hooks.Select(kv => (kv.Key.Kind, kv.Key.Name, kv.Value));

    public IEnumerable<ScriptBlock> GetByKind(HookKind kind)
        => _hooks.Where(kv => kv.Key.Kind == kind).Select(kv => kv.Value);
}

public enum HookKind { ChpwdHook, PromptHook }
```

**Rationale for `ConcurrentDictionary`:** The prompt fires from the shell's main
thread, but `Register-BashChpwdHook` may be called from an interactive session
where the user types commands or from `$PROFILE` loading during startup. Concurrent
access must not corrupt the registry.

---

## 4. Execution Scope

**Decision: hooks run in process-global scope.**

Hooks are invoked via `ScriptBlock.InvokeWithContext` with a null variable
table, causing PowerShell to resolve variable reads and writes against the
**global scope** of the current runspace. This is the correct choice because:

- `$env:*` mutations are process-wide by definition and work in any scope.
- Tools like `direnv` and `fnm` pipe output to `Invoke-Expression`, which
  evaluates in the calling scope; making that global ensures `$env:` assignments
  land in the process environment table rather than a transient local scope.
- Variable assignments inside hooks (`$global:BashPromptString = ...`) need to
  survive across the hook call and be visible to the prompt renderer; global
  scope provides this.

**Implications for hook authors:**
- Use `$env:NAME` for environment variable mutations (always global).
- Use `$global:VarName` for shell-state variables.
- Avoid local variable declarations with `$local:` inside hooks — they evaporate
  after the hook returns and have no effect on the shell.

---

## 5. Firing Semantics

### 5.1 Prompt Tick Driver

The shell's prompt function (called on every `PSReadLine` prompt render) drives
both hook kinds. Pseudocode:

```powershell
function Invoke-BashPrompt {
    $currentPath = $PWD.Path

    # --- ChpwdHook firing ---
    if ($currentPath -ne $global:__BashLastCwd) {
        $oldPath = $global:__BashLastCwd
        $global:__BashLastCwd = $currentPath
        foreach ($sb in [HookRegistry]::Instance.GetByKind('ChpwdHook')) {
            try {
                & $sb -OldPath $oldPath -NewPath $currentPath
            } catch {
                $global:BashHookErrors += $_
            }
        }
    }

    # --- PromptHook firing ---
    foreach ($sb in [HookRegistry]::Instance.GetByKind('PromptHook')) {
        try {
            & $sb
        } catch {
            $global:BashHookErrors += $_
        }
    }
}
```

### 5.2 ChpwdHook Parameters

ChpwdHook scriptblocks receive two named parameters:

| Parameter | Type | Value |
|-----------|------|-------|
| `$OldPath` | `string` | `$PWD.Path` before the directory change |
| `$NewPath` | `string` | `$PWD.Path` after the directory change (current) |

The parameters are passed positionally (`& $sb $oldPath $newPath`). Hook authors
may use `param($OldPath, $NewPath)` to name them or `$args[0]`/`$args[1]` to
access them positionally.

### 5.3 PromptHook Parameters

PromptHook scriptblocks receive no parameters. They read state from globals,
`$env:`, or external tools. No arguments are passed.

### 5.4 Firing Order

Hooks fire in insertion order within each kind. Since `ConcurrentDictionary`
does not guarantee insertion order, the implementation enumerates keys sorted
by `Name` (lexicographic, ordinal). This makes firing order deterministic and
independent of registration timing.

Tools that need ordering guarantees should choose names that sort into the
desired order (e.g., `'01-fnm'`, `'02-direnv'`).

### 5.5 Error Handling

Exceptions thrown inside hooks are:

1. **Caught** — the exception does not propagate to the prompt or crash the shell.
2. **Appended** to `$global:BashHookErrors` (a `List[ErrorRecord]`). The list
   is initialized to empty on module import and grows unbounded unless cleared.
3. **Not displayed** at the prompt by default. Users can inspect errors with:

```powershell
$global:BashHookErrors          # All hook errors since last clear
$global:BashHookErrors.Clear()  # Clear the error log
```

This design matches zsh's behavior: a broken `chpwd` hook does not crash the
shell; it silently records the failure.

### 5.6 Self-Unregistering Hooks

A hook can remove itself by calling `Unregister-BashChpwdHook` or
`Unregister-BashPromptHook` from inside its own scriptblock. Since the firing
loop iterates a snapshot of the `ConcurrentDictionary`, a hook that unregisters
itself mid-iteration does not cause a collection-modified exception; the current
iteration completes normally and the hook is absent on the next prompt tick.

Example — one-shot hook that fires only once:

```powershell
Register-BashChpwdHook -Name 'one-shot-init' -ScriptBlock {
    param($OldPath, $NewPath)
    # Run initialization logic here...
    do-something
    # Remove self
    Unregister-BashChpwdHook -Name 'one-shot-init'
}
```

---

## 6. Bash/Zsh to PowerShell Mapping

### 6.1 PROMPT_COMMAND

`PROMPT_COMMAND` in bash is a string (or array in bash 5.1+) executed before
each primary prompt is displayed. The mapping is:

| bash | ps-bash |
|------|---------|
| `PROMPT_COMMAND="do_something"` | `Register-BashPromptHook -Name 'main' -ScriptBlock { do_something }` |
| `PROMPT_COMMAND+="other_thing"` | `Register-BashPromptHook -Name 'other' -ScriptBlock { other_thing }` |
| `unset PROMPT_COMMAND` | `Unregister-BashPromptHook -Name 'main'` |

The emitter translates `PROMPT_COMMAND=...` assignment to the
`Register-BashPromptHook` call during script transpilation. Multiple assignments
to `PROMPT_COMMAND` in the same script produce multiple hooks with distinct
auto-generated names (`'main'`, `'main_2'`, etc.) rather than replacing a single
hook; this matches the additive semantics of `PROMPT_COMMAND+=`.

### 6.2 zsh chpwd Hook

In zsh, `chpwd` is a special function that fires on directory change. The
mapping is:

| zsh | ps-bash |
|-----|---------|
| `chpwd() { do_something }` | `Register-BashChpwdHook -Name 'chpwd' -ScriptBlock { do_something }` |
| `add-zsh-hook chpwd fnm_auto_use` | `Register-BashChpwdHook -Name 'fnm_auto_use' -ScriptBlock { fnm_auto_use }` |

The emitter does not automatically translate `chpwd()` function definitions —
these appear only in zsh-specific init files, which ps-bash does not process.
Users who port zsh init scripts call the `Register-BashChpwdHook` cmdlet
directly in their `$PROFILE`.

### 6.3 trap '...' DEBUG

The bash `trap '...' DEBUG` mechanism fires before every command. It is **not
mapped** to a ps-bash hook. Reasons:

1. `trap DEBUG` fires before every single command execution, not just at prompt
   time. Emulating this in PowerShell would require wrapping every emitted
   statement in a scriptblock and invoking it via a trampoline — this is
   impractical and would degrade all execution performance.
2. The primary consumers of `trap DEBUG` are debuggers (e.g., `bashdb`) and
   per-command timing tools. PowerShell has `Set-PSDebug -Trace` for debugger
   use and `Measure-Command` for timing; these are the correct equivalents.
3. `$BASH_COMMAND` (set by the DEBUG trap) has no natural equivalent in a
   transpiled execution model where bash source lines and PowerShell execution
   are decoupled.

Tools that use `trap DEBUG` for env-loading (e.g., some `nvm` configurations)
should be ported to use `Register-BashChpwdHook` instead, which provides the
directory-change trigger those tools actually need.

### 6.4 direnv Integration

`direnv` exports environment via `direnv export pwsh`, which prints a block of
`$env:` assignment statements. The canonical registration:

```powershell
Register-BashChpwdHook -Name 'direnv' -ScriptBlock {
    param($OldPath, $NewPath)
    $export = direnv export pwsh 2>$null
    if ($export) { Invoke-Expression $export }
}
```

This mirrors the pattern in `direnv`'s own PowerShell hook documentation.

### 6.5 fnm Integration

`fnm` (Fast Node Manager) switches the active Node.js version based on
`.nvmrc` or `.node-version` files. The canonical registration:

```powershell
Register-BashChpwdHook -Name 'fnm' -ScriptBlock {
    param($OldPath, $NewPath)
    fnm use --silent-if-unchanged 2>$null
}
```

`fnm use` mutates `$env:PATH` and related variables; running in global scope
ensures those mutations persist past the hook call.

---

## 7. HookKind Enum

```csharp
namespace PsBash.Cmdlets;

public enum HookKind
{
    ChpwdHook,
    PromptHook,
}
```

The `-Kind` parameter on `Get-BashHook` accepts `HookKind` values. PowerShell
performs automatic string-to-enum coercion, so `Get-BashHook -Kind ChpwdHook`
works without explicit casting.

---

## 8. BashHookInfo Output Object

```csharp
namespace PsBash.Cmdlets;

public sealed class BashHookInfo
{
    public HookKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public ScriptBlock ScriptBlock { get; init; } = ScriptBlock.EmptyScriptBlock;

    public override string ToString() => $"{Kind}/{Name}";
}
```

`Get-BashHook` emits `BashHookInfo` objects. The default `ToString()` produces
`ChpwdHook/fnm`-style output for quick identification in the pipeline.

---

## 9. Global State Variables

| Variable | Type | Initial Value | Purpose |
|----------|------|---------------|---------|
| `$global:__BashLastCwd` | `string` | `$PWD.Path` at module import | Previous directory; compared each prompt tick to detect changes |
| `$global:BashHookErrors` | `List[ErrorRecord]` | `@()` | Error log for hook exceptions |

Both variables are initialized by the `PsBash.Cmdlets` module's `OnImport` handler.

---

## 10. Threading Model

The prompt function and the `Register-*` / `Unregister-*` cmdlets all execute on
the same PowerShell runspace thread (the interactive shell's main thread). The
`ConcurrentDictionary` provides safe concurrent access in case a background job
or `Start-ThreadJob` calls `Register-BashChpwdHook`, but under normal
interactive use there is no concurrency.

The firing loop takes a snapshot of hook entries before iterating
(`_hooks.ToArray()`) so that hooks that modify the registry during iteration do
not cause `InvalidOperationException`.

---

## 11. Implementation Notes

### 11.1 PSReadLine Integration

The prompt function (`Invoke-BashPrompt`) is wired into PSReadLine's prompt
callback. PSReadLine calls the prompt function before displaying each new input
line. The integration point is:

```powershell
Set-PSReadLineOption -PromptText { Invoke-BashPrompt }
```

or equivalently, defining the `prompt` function (which PSReadLine calls
automatically if defined).

### 11.2 Snapshot for Iteration Safety

The firing loop must not hold a lock while executing hooks (hooks may call
`Register-*`/`Unregister-*`). The safe pattern:

```csharp
// In HookRegistry
public ScriptBlock[] SnapshotByKind(HookKind kind)
    => _hooks
        .Where(kv => kv.Key.Kind == kind)
        .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal)
        .Select(kv => kv.Value)
        .ToArray();
```

The prompt function calls `SnapshotByKind` once per kind and iterates the
returned array, not the live dictionary.

### 11.3 First-Tick Initialization

On the first prompt tick, `$global:__BashLastCwd` is the directory at module
import time. If the user changes directory before the first prompt fires (rare
but possible in scripts), the chpwd hook fires correctly because `$PWD.Path`
will differ from the recorded initial value.

### 11.4 Compatibility with cd Aliases

The shell's `cd` alias calls `Set-Location`. `$PWD` is updated by
`Set-Location` before the next prompt fires; the hook detects the change at
prompt time, not at `Set-Location` time. This is the same semantic as zsh's
`chpwd` (which fires after `cd` completes, before the next prompt).

---

## 12. Acceptance Criteria

Implementation is complete when:

- [ ] `Register-BashChpwdHook -Name x -ScriptBlock { ... }` adds the hook
- [ ] Re-registering the same name replaces the hook (no duplicate entries)
- [ ] `Unregister-BashChpwdHook -Name x` removes the hook; no error if absent
- [ ] `Register-BashPromptHook` and `Unregister-BashPromptHook` behave identically for their kind
- [ ] `Get-BashHook` returns all hooks; `-Kind` filters correctly
- [ ] ChpwdHooks fire when `$PWD` changes between prompt ticks, not on every tick
- [ ] ChpwdHooks receive `$OldPath` and `$NewPath` as positional args
- [ ] PromptHooks fire on every prompt tick with no arguments
- [ ] Hook exceptions are caught; `$global:BashHookErrors` grows; shell does not crash
- [ ] A hook can unregister itself; no collection-modified exception
- [ ] `direnv export pwsh | Invoke-Expression` from inside a ChpwdHook lands env mutations in the process
- [ ] `fnm use` from inside a ChpwdHook updates `$env:PATH` persistently
- [ ] `$global:__BashLastCwd` is initialized on module import
- [ ] Hooks fire in deterministic name-sorted order

---

## 13. References

- Related specs:
  - [`shell-implementation-phases.md`](./shell-implementation-phases.md) — implementation roadmap
  - [`plugin-architecture.md`](./plugin-architecture.md) — module session state patterns
  - [`cwd-awareness.md`](./cwd-awareness.md) — CWD change detection in history
  - [`config-format.md`](./config-format.md) — shell configuration schema
