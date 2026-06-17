# ---------------------------------------------------------------------------
# Prompt + on-cd hooks (these work in BOTH module mode and the interactive shell).
# Run:  Import-Module PsBash; . ./examples/05-hooks/chpwd-hooks.ps1
#
# ps-bash bridges bash's prompt model onto PowerShell's `prompt` function as a
# WRAPPER, so it coexists with oh-my-posh / Starship / PSReadLine: it stores any
# existing prompt in $global:__BashOriginalPrompt, runs the hooks, then calls it.
# ---------------------------------------------------------------------------

Import-Module PsBash -ErrorAction Stop

# Run on every prompt.
Register-BashPromptHook -Name 'clock' -ScriptBlock {
    $Host.UI.RawUI.WindowTitle = "ps-bash — " + (Get-Date -Format 'HH:mm:ss')
}

# Run when the directory changes — the hook receives $OldPath and $NewPath.
# This is the integration point for direnv / fnm / nodenv / pyenv / zoxide.
Register-BashChpwdHook -Name 'announce' -ScriptBlock {
    param($OldPath, $NewPath)
    Write-Host "→ entered $NewPath" -ForegroundColor DarkGray
}

# Real-world examples (uncomment if you have the tools):
# Register-BashChpwdHook -Name 'direnv' -ScriptBlock { direnv export pwsh | Invoke-Expression }
# Register-BashChpwdHook -Name 'fnm'    -ScriptBlock { fnm use --silent-if-unchanged }

Write-Host "Registered hooks:" -ForegroundColor Cyan
Get-BashHook

# chpwd hooks fire between prompts when $PWD changes, run in the global scope (so
# $env:/$global: mutations persist), fire in name-sorted order, and a throwing
# hook is caught (error appended to $global:BashHookErrors, never crashes the shell).
Write-Host "`nNow `cd` somewhere to see the chpwd hook fire." -ForegroundColor DarkGray
# Unregister-BashChpwdHook -Name 'announce'   # remove one
