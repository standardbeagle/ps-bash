#
# Module manifest for binary module 'PsBash.Cmdlets'
#

@{

RootModule = 'PsBash.Cmdlets.dll'

NestedModules = @('PsBash.psd1')

ModuleVersion = '0.1.0'

GUID = 'b2c3d4e5-f6a7-8901-bcde-f23456789012'

Author = 'Andy Brummer'

CompanyName = 'StandardBeagle'

Copyright = '(c) Andy Brummer. All rights reserved.'

Description = 'Binary cmdlets for ps-bash: Invoke-BashEval, Invoke-BashSource, ConvertTo-PowerShell, Test-BashSyntax. JIT-only (PowerShell 7.4+); does not register host aliases.'

CompatiblePSEditions = 'Core'

PowerShellVersion = '7.4'

# Re-export all nested script-module functions so Invoke-BashEval transpiled
# scriptblocks can resolve commands like Invoke-BashLs in the caller's scope.
# Aliases remain blocked (AliasesToExport = @()) so host aliases like ls are
# not hijacked. This is the fallback after proving private nested-module scope
# binding does not work for ScriptBlock.Create from a binary cmdlet.
FunctionsToExport = @('*')

# Cmdlets exported. Listed explicitly (no wildcards) for performance.
CmdletsToExport = @('Invoke-BashEval', 'Invoke-BashSource', 'ConvertTo-PowerShell', 'Test-BashSyntax')

VariablesToExport = @()

# Explicitly empty so importing this module does not hijack host aliases like ls, cat, etc.
AliasesToExport = @()

PrivateData = @{
    PSData = @{
        Tags = @('bash', 'powershell', 'transpiler', 'cmdlets', 'PSEdition_Core')
        LicenseUri = 'https://github.com/standardbeagle/ps-bash/blob/main/LICENSE'
        ProjectUri = 'https://github.com/standardbeagle/ps-bash'
    }
}

}
