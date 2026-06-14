#
# Module manifest for binary module 'PsBash.Cmdlets'
#

@{

RootModule = 'PsBash.Cmdlets.dll'

NestedModules = @('PsBash.psd1')

ModuleVersion = '0.9.5'

GUID = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'

Author = 'Andy Brummer'

CompanyName = 'StandardBeagle'

Copyright = '(c) Andy Brummer. All rights reserved.'

Description = 'Binary cmdlets for ps-bash: Invoke-BashSource, ConvertTo-PowerShell, Test-BashSyntax. JIT-only (PowerShell 7.4+); does not register host aliases.'

CompatiblePSEditions = 'Core'

PowerShellVersion = '7.4'

# Modules that must be imported into the global environment prior to importing this module
RequiredModules = @(@{ ModuleName = 'PsBash'; ModuleVersion = '0.9.5' })

# Re-export all nested script-module functions so binary cmdlets that execute
# transpiled scriptblocks can resolve commands like Invoke-BashLs in the
# caller's scope. Aliases remain blocked (AliasesToExport = @()) so host
# aliases like ls are not hijacked.
FunctionsToExport = @('*')

# Cmdlets exported. Listed explicitly (no wildcards) for performance.
CmdletsToExport = @(
    'Invoke-BashSource',
    'ConvertTo-PowerShell',
    'Test-BashSyntax',
    'Register-BashChpwdHook',
    'Register-BashPromptHook',
    'Unregister-BashChpwdHook',
    'Unregister-BashPromptHook',
    'Get-BashHook',
    # REFACTOR-2 Phase 1 / 1b: leaf Invoke-Bash* functions migrated from
    # PsBash.psm1 to binary cmdlets. The psm1 no longer defines these
    # functions, so the cmdlet is the sole implementation; psm1's Set-Alias
    # lines resolve to it.
    'Invoke-BashBasename',
    'Invoke-BashDirname',
    'Invoke-BashEcho',
    'Invoke-BashKill',
    'Invoke-BashRedirect',
    'Invoke-BashPrintf',
    'Invoke-BashPwd',
    # REFACTOR-2 Phase 1c: cat / head / tail / wc migrated from PsBash.psm1.
    'Invoke-BashCat',
    'Invoke-BashHead',
    'Invoke-BashTail',
    'Invoke-BashWc',
    # REFACTOR-2 Phase 1d: ls migrated from PsBash.psm1 — the final leaf of
    # REFACTOR-2 Phase 1. Tier 1 / Tier 3 provider paths stay in psm1 behind
    # Get-BashLsProviderEntries; the cmdlet owns Tier 2 + sort + format.
    'Invoke-BashLs',
    # REFACTOR-2 Phase 3: sed migrated from PsBash.psm1 (with its
    # ConvertFrom-SedExpression / Test-SedAddress helpers reimplemented in C#).
    'Invoke-BashSed',
    # REFACTOR-2 Phase 3 follow-on: find migrated from PsBash.psm1. The
    # Get-BashFileInfo slice the cmdlet needs is duplicated in C# (psm1's
    # Get-BashFileInfo stays — Invoke-BashStat still depends on it).
    # -exec dispatches via InvokeCommand.InvokeScript with a parameterized
    # script body and $args splat — no string concatenation of user input
    # into the script body (Directive 12).
    'Invoke-BashFind',
    # REFACTOR-2 Phase F6: jq migrated from PsBash.psm1 (the *-Jq* helper
    # web reimplemented in C# inside JqEngine; ConvertTo-JqJson +
    # Invoke-JqFilter remain as psm1 shims for the still-psm1 Invoke-BashYq).
    'Invoke-BashJq',
    # RC-8a: Invoke-ProcessSubSource migrated from PsBash.psm1. The psm1
    # function introduced a script function scope, so source <(...) env vars
    # and function defs were discarded on return. The cmdlet has no script
    # scope, so InvokeScript(useNewScope:false) targets the eval pipeline.
    'Invoke-ProcessSubSource',
    # REFACTOR-2 follow-on: read migrated from PsBash.psm1. Pipeline-or-stdin
    # read into one or more variables in the caller's scope plus the process
    # environment block (so subsequent $env:NAME expansions in transpiled bash
    # see the value). -p / -a declared as explicit string parameters to
    # bypass common-parameter prefix collisions.
    'Invoke-BashRead',
    # Strata integration: styles a pipeline of objects with a CSS stylesheet and
    # renders them via the Strata selector engine + Spectre.Console projection.
    'Format-Styled',
    # Interactive counterpart: a full-screen, navigable list with per-row detail
    # expansion, driven by the same Strata stylesheets via a Console.ReadKey +
    # Spectre projection loop. Headless (redirected I/O) emits a summary instead.
    'Show-Styled',
    # Network probes as styled objects (managed ICMP): one object per reply / hop with
    # a native-style BashText line plus a latency `class`, so they read like the real
    # tools and become the interactive styled viewer when piped to Show-Styled.
    'Invoke-BashPing',
    'Invoke-BashTraceroute'
)

VariablesToExport = @()

# Explicitly empty so importing this module does not hijack host aliases like ls, cat, etc.
AliasesToExport = @()

PrivateData = @{
    PSData = @{
        Tags = @('bash', 'powershell', 'transpiler', 'cmdlets', 'PSEdition_Core')
        LicenseUri = 'https://github.com/standardbeagle/ps-bash/blob/main/LICENSE'
        ProjectUri = 'https://github.com/standardbeagle/ps-bash'
        ReleaseNotes = 'Published alongside PsBash module. Binary cmdlets: Invoke-BashSource, ConvertTo-PowerShell, Test-BashSyntax.'
    }
}

}
