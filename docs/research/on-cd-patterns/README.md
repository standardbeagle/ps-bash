# On-CD Hook Patterns Research

Research into how real-world env-setup tools emit bash init hooks, catalogued to
inform the design of a ps-bash hook API that covers actual usage.

## Tools Surveyed

- fnm (Fast Node Manager)
- direnv
- nodenv
- rbenv
- pyenv
- zoxide
- starship

## Summary Table

| Tool | Hook mechanism | Fire condition | Needs prev-cmd state | Unregister |
|------|---------------|----------------|---------------------|------------|
| fnm | PROMPT_COMMAND | Every prompt; PWD dedup inside `fnm use` | No | Unset function, remove from PROMPT_COMMAND |
| direnv | PROMPT_COMMAND (modern); PROMPT_COMMAND + trap DEBUG (old <2.20) | Every prompt; PWD dedup inside `direnv export` | Saves/restores `$?` but does not inspect it for logic | `direnv deny`; hook stays registered |
| nodenv | PROMPT_COMMAND (rehash only) | Every prompt | No | Remove shims from $PATH |
| rbenv | PROMPT_COMMAND (rehash only) | Every prompt | Saves/restores `$?` but does not inspect it | Remove shims from $PATH |
| pyenv | Shell function only (base); PROMPT_COMMAND (virtualenv plugin) | Base: per-invocation via shims; virtualenv: every prompt | Saves/restores `$?` in virtualenv hook | `unset PYENV_ROOT`, remove from $PATH |
| zoxide | PROMPT_COMMAND (default) OR trap DEBUG (_ZO_HOOK=pwd) | Precmd: every prompt; DEBUG: every command with PWD diff check | Reads `$PWD` only; not `$?` or `$BASH_COMMAND` | `unset -f`, `trap - DEBUG`, remove from PROMPT_COMMAND |
| starship | trap DEBUG (preexec timing) + PROMPT_COMMAND (prompt render) | Both; DEBUG on every command; PROMPT_COMMAND on every prompt | YES — reads `$?` (exit status) and records command start time via DEBUG | `trap - DEBUG`, remove from PROMPT_COMMAND, restore PS1 |

## Pattern Taxonomy

### Pattern A: PROMPT_COMMAND only (most common)
Used by: fnm, direnv (modern), nodenv, rbenv, pyenv-virtualenv

- Fires after every command (at the next prompt).
- Enough for cd-driven behavior because by the time PROMPT_COMMAND fires,
  `$PWD` has already changed.
- Tools doing PWD-change detection (fnm, direnv) do it internally.
- Required pwsh hook shape: a `$global:__BashPromptCommand` registry plus
  a `prompt` function that invokes all registered callbacks before drawing PS1.

### Pattern B: trap DEBUG only
Not used by any surveyed tool as the primary mechanism. Would fire on every
command (before execution), giving access to `$BASH_COMMAND` and the old `$PWD`.

### Pattern C: trap DEBUG (preexec) + PROMPT_COMMAND (precmd)
Used by: starship, zoxide (_ZO_HOOK=pwd mode)

- DEBUG trap fires *before* each command: used to record start time or capture
  old `$PWD`.
- PROMPT_COMMAND fires *after* each command: used to render prompt or update
  directory database.
- Required pwsh hook shape: BOTH a `$global:__BashDebugTrap` invoked from the
  trap and `$global:__BashPromptCommand` invoked from the prompt function.

### Pattern D: Shell function + shims (version managers)
Used by: nodenv, rbenv, pyenv (base)

- No PROMPT_COMMAND or DEBUG trap for the core version-switching.
- Version resolution is per-invocation inside shim executables that read
  `.node-version` / `.python-version` / `.ruby-version` in the current dir.
- Shell function intercepts subcommands (`shell`, `rehash`) that need to modify
  the caller's environment via `eval`.
- Required pwsh hook shape: shims directory prepended to `$env:PATH`; a
  PowerShell function wrapping the binary to `Invoke-Expression` the output of
  `sh-shell` / `sh-rehash` subcommands.

## Minimum pwsh Hook API Required

Based on the above, a ps-bash on-cd / prompt hook API needs three primitives:

### 1. PROMPT_COMMAND registry (`$global:__BashPromptCommand`)
A list of scriptblocks or function names invoked after every command, before the
prompt is redrawn. This maps directly to bash's `PROMPT_COMMAND`.

```powershell
# Register a callback
$global:__BashPromptCommand = [System.Collections.Generic.List[object]]::new()
$global:__BashPromptCommand.Add({ fnm use --silent-if-unchanged 2>$null })

# Invocation point (inside the prompt function or Set-PSReadLineKeyHandler):
foreach ($cb in $global:__BashPromptCommand) { & $cb }
```

### 2. DEBUG trap registry (`$global:__BashDebugTrap`)
A list of scriptblocks invoked before each command executes. This maps to
bash's `trap '...' DEBUG`. Required by starship (timing) and zoxide (PWD mode).

```powershell
# PSReadLine CommandValidationHandler or a custom pre-execution hook:
Set-PSReadLineOption -CommandValidationHandler {
    foreach ($cb in $global:__BashDebugTrap) { & $cb }
}
```

Note: PowerShell has no perfect equivalent of DEBUG trap. The closest
approximation is PSReadLine's `CommandValidationHandler` (fires before each
command) or `$PSConsoleHostReadLine` override. Neither fires inside non-
interactive script execution. For script execution, the DEBUG-trap equivalent
would require a custom `ICommandRuntime` wrapper or AST rewriting.

### 3. Shell function `eval` bridge
For tools like nodenv/rbenv/pyenv that emit PowerShell-evaluable output from
subcommands, we need an `Invoke-Expression` bridge.

```powershell
function nodenv {
    switch ($args[0]) {
        'rehash' { Invoke-Expression (& nodenv.exe sh-rehash @($args | Select-Object -Skip 1)) }
        'shell'  { Invoke-Expression (& nodenv.exe sh-shell  @($args | Select-Object -Skip 1)) }
        default  { & nodenv.exe @args }
    }
}
```

## Key Findings

1. **PROMPT_COMMAND is universal**: every tool that needs a per-command hook uses
   PROMPT_COMMAND. It is the minimum viable hook surface.

2. **trap DEBUG is optional but important**: only tools needing preexec timing
   (starship) or before-cd PWD capture (zoxide pwd mode) use it. It is harder
   to emulate in PowerShell.

3. **$? preservation is a near-universal concern**: tools that use PROMPT_COMMAND
   save `$?` at the top of their hook and restore it at the end. The pwsh
   equivalent is saving `$LASTEXITCODE` at the start of the prompt callback
   and restoring it before returning.

4. **PWD-change detection is done tool-side**: no tool relies on the shell to
   report "cd happened". They either check $PWD themselves or receive it from
   the shim at invocation time. ps-bash does not need to synthesize a cd-event.

5. **None of the tools use `$BASH_COMMAND`** in their stable modern hook for
   logic (only for re-entrance guards). We do not need to emulate `$BASH_COMMAND`
   to satisfy these patterns.

6. **direnv has native pwsh support**: `direnv hook pwsh` and `direnv export pwsh`
   exist and produce `$env:` assignments directly. No translation layer needed
   for direnv specifically.
