#
# Experimental module manifest for binary module 'PsBash.Cmdlets'
# Includes Invoke-BashEval for local viability testing only.
#

@{

RootModule = 'PsBash.Cmdlets.dll'

NestedModules = @('PsBash.psd1')

ModuleVersion = '0.9.5'

GUID = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'

Author = 'Andy Brummer'

CompanyName = 'StandardBeagle'

Copyright = '(c) Andy Brummer. All rights reserved.'

Description = 'Experimental binary cmdlets for ps-bash, including Invoke-BashEval. JIT-only (PowerShell 7.4+); does not register host aliases.'

CompatiblePSEditions = 'Core'

PowerShellVersion = '7.4'

RequiredModules = @(@{ ModuleName = 'PsBash'; ModuleVersion = '0.9.5' })

FunctionsToExport = @('*')

CmdletsToExport = @(
    'Invoke-BashEval',
    'Invoke-BashSource',
    'ConvertTo-PowerShell',
    'Test-BashSyntax',
    'Register-BashChpwdHook',
    'Register-BashPromptHook',
    'Unregister-BashChpwdHook',
    'Unregister-BashPromptHook',
    'Get-BashHook',
    # REFACTOR-2 Phase 1 / 1b: leaf Invoke-Bash* functions migrated from PsBash.psm1.
    'Invoke-BashBasename',
    'Invoke-BashDirname',
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
    # REFACTOR-2 Phase 3: sed migrated from PsBash.psm1.
    'Invoke-BashSed',
    # RC-8a: Invoke-ProcessSubSource migrated from PsBash.psm1 to fix
    # source <(...) scope persistence — see InvokeProcessSubSourceCommand.cs.
    'Invoke-ProcessSubSource'
)

VariablesToExport = @()

AliasesToExport = @()

PrivateData = @{
    PSData = @{
        Tags = @('bash', 'powershell', 'transpiler', 'cmdlets', 'experimental', 'PSEdition_Core')
        LicenseUri = 'https://github.com/standardbeagle/ps-bash/blob/main/LICENSE'
        ProjectUri = 'https://github.com/standardbeagle/ps-bash'
        ReleaseNotes = 'Experimental build with Invoke-BashEval enabled. Not part of the supported default cmdlet surface.'
    }
}

}
