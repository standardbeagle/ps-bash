# `browse` Object Workbench

`browse` is an object-aware workbench for PowerShell and ps-bash pipelines. It consumes pipeline objects, keeps the original objects attached to rows, and renders a compact table plus inspection/action commands. The MVP is intentionally line-mode so it works in ordinary terminals and test harnesses; a richer full-screen TUI can build on the same binding and action model.

## Command Syntax

```powershell
ps | browse
ls | browse
ls | browse -List
ls | browse -Inspect 0
ls | browse -Select 0,2 -PassThru
ls | browse -Select 0 -Action inspect
ls | browse -Select 0 -Action delete       # preview only
ls | browse -Select 0 -Action delete -Force
ps | browse -Select 0 -Exec '$1 | Select-Object ProcessName,Id'
ps | browse -Select 0 -Exec '$1 | Stop-Process'       # gated preview
ps | browse -Select 0 -Exec '$1 | Stop-Process' -Force
```

With no explicit mode, `browse` opens an interactive line-mode workbench when stdin is a terminal. In redirected or automated execution it emits browse row objects equivalent to `-List`.

## Object Bindings

Actions and exec commands receive:

- `${1}` and `$_`: the current object.
- `$items`: selected objects, or the current object as a one-item array when nothing is selected.
- `$__browse_current` and `$__browse_items`: stable aliases for scripts that prefer non-positional names.

`$items` is the primary selected-object binding. PowerShell's automatic `$args` is not used as a contract because it is owned by scriptblock invocation semantics.

## Action Registry

Adapters are PowerShell objects with this shape:

```powershell
@{
    Name = 'file'
    TypeNames = @('System.IO.FileInfo', 'PsBash.LsEntry')
    DisplayProperties = @('Name', 'Length', 'LastWriteTime', 'FullName')
    Actions = @(
        @{ Name = 'inspect'; Destructive = $false; Script = { param($Current, $Items) ... } },
        @{ Name = 'delete';  Destructive = $true;  Script = { param($Current, $Items) ... } }
    )
}
```

Resolution prefers the first adapter whose `TypeNames` match an object's `PSTypeNames` or .NET type name. The default adapter falls back to `DefaultDisplayPropertySet`, public properties, then `ToString()`.

## Safety Model

Actions declare `Destructive`. Destructive actions return a preview unless `-Force` is supplied.

Exec mode applies a conservative command classifier. Commands containing destructive verbs or aliases such as `Remove-Item`, `rm`, `del`, `Stop-Process`, `Restart-Service`, `Set-Service`, or `kill` require `-Force`. The preview includes resolved target strings for the current/selected objects and the command that would run.

## Non-Interactive Behavior

When stdin is redirected or a test harness supplies objects, `browse` must not block for input. It emits browse row objects with index, selection state, type, display text, and `OriginalObject`. `-PassThru` returns selected/current original objects for downstream commands.
