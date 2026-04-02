#Requires -Modules Pester

BeforeAll {
    $modulePath = Join-Path $PSScriptRoot '..' 'src' 'PsBash.psd1'
    Import-Module $modulePath -Force
}

Describe 'Module Loading' {
    It 'imports PsBash without error' {
        $mod = Get-Module PsBash
        $mod | Should -Not -BeNullOrEmpty
        $mod.Name | Should -Be 'PsBash'
    }

    It 'exports echo alias pointing to Invoke-BashEcho' {
        $alias = Get-Alias -Name echo -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashEcho'
    }

    It 'exports printf alias pointing to Invoke-BashPrintf' {
        $alias = Get-Alias printf
        $alias.Definition | Should -Be 'Invoke-BashPrintf'
    }
}

Describe 'Get-BashPlatform' {
    It 'returns a known platform string' {
        $platform = Get-BashPlatform
        $platform | Should -BeIn @('Windows', 'Linux', 'macOS')
    }
}

Describe 'New-BashObject' {
    It 'creates object with BashText property' {
        $obj = New-BashObject -BashText 'test'
        $obj.BashText | Should -Be 'test'
    }

    It 'has PsBash.TextOutput type name' {
        $obj = New-BashObject -BashText 'test'
        $obj.PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
    }

    It 'ToString returns BashText' {
        $obj = New-BashObject -BashText 'hello'
        $obj.ToString() | Should -Be 'hello'
    }
}

Describe 'Invoke-BashEcho' {
    It 'outputs BashObject with BashText containing hello plus newline' {
        $result = Invoke-BashEcho 'hello'
        $result.BashText | Should -Be "hello`n"
    }

    It 'with no args outputs just a newline' {
        $result = Invoke-BashEcho
        $result.BashText | Should -Be "`n"
    }

    It '-n outputs without trailing newline' {
        $result = Invoke-BashEcho -n 'hello'
        $result.BashText | Should -Be 'hello'
    }

    It '-e \t outputs tab character' {
        $result = Invoke-BashEcho -e '\t'
        $result.BashText | Should -Be "`t`n"
    }

    It '-e \n outputs newline in text plus trailing newline' {
        $result = Invoke-BashEcho -e '\n'
        $result.BashText | Should -Be "`n`n"
    }

    It '-e \\ outputs literal backslash' {
        $result = Invoke-BashEcho -e '\\'
        $result.BashText | Should -Be "\`n"
    }

    It '-E \t outputs literal \t (escapes disabled by default)' {
        $result = Invoke-BashEcho -E '\t'
        $result.BashText | Should -Be "\t`n"
    }

    It 'multiple words joins them with spaces' {
        $result = Invoke-BashEcho 'hello' 'world'
        $result.BashText | Should -Be "hello world`n"
    }

    It '-ne combines no-newline and escape processing' {
        $result = Invoke-BashEcho -ne 'hello\tworld'
        $result.BashText | Should -Be "hello`tworld"
    }

    It 'ToString works for string interpolation' {
        $result = Invoke-BashEcho -n 'hello'
        "$result" | Should -Be 'hello'
    }

    It 'output is pipeline-able via BashText property' {
        $text = Invoke-BashEcho -n 'hello' | ForEach-Object { $_.BashText }
        $text | Should -Be 'hello'
    }

    It '-- stops flag parsing' {
        $result = Invoke-BashEcho '--' '-n'
        $result.BashText | Should -Be "-n`n"
    }
}

Describe 'Invoke-BashPrintf' {
    It 'formats %s %d correctly' {
        $result = Invoke-BashPrintf '%s %d' 'count' '42'
        $result.BashText | Should -Be 'count 42'
    }

    It 'with no args throws' {
        { Invoke-BashPrintf } | Should -Throw
    }

    It 'with just a format string and no args' {
        $result = Invoke-BashPrintf 'hello'
        $result.BashText | Should -Be 'hello'
    }

    It 'handles %% as literal percent' {
        $result = Invoke-BashPrintf '100%%'
        $result.BashText | Should -Be '100%'
    }

    It 'handles escape sequences in format' {
        $result = Invoke-BashPrintf 'a\tb'
        $result.BashText | Should -Be "a`tb"
    }

    It '%s with multiple string args uses first only' {
        $result = Invoke-BashPrintf '%s' 'hello' 'world'
        $result.BashText | Should -Be 'hello'
    }
}

Describe 'ConvertFrom-BashArgs' {
    It 'parses combined short flags' {
        $defs = New-FlagDefs -Entries @('-n', 'n flag', '-e', 'e flag')
        $result = ConvertFrom-BashArgs -Arguments @('-ne', 'hello') -FlagDefs $defs
        $result.Flags['-n'] | Should -BeTrue
        $result.Flags['-e'] | Should -BeTrue
        $result.Operands | Should -Be @('hello')
    }

    It 'treats unknown flags as operands' {
        $defs = New-FlagDefs -Entries @('-n', 'n flag')
        $result = ConvertFrom-BashArgs -Arguments @('-x', 'hello') -FlagDefs $defs
        $result.Operands | Should -Contain '-x'
    }

    It 'handles -- separator' {
        $defs = New-FlagDefs -Entries @('-n', 'n flag')
        $result = ConvertFrom-BashArgs -Arguments @('--', '-n') -FlagDefs $defs
        $result.Flags['-n'] | Should -BeFalse
        $result.Operands | Should -Be @('-n')
    }

    It 'handles empty arguments' {
        $defs = New-FlagDefs -Entries @('-n', 'n flag')
        $result = ConvertFrom-BashArgs -Arguments @() -FlagDefs $defs
        $result.Flags['-n'] | Should -BeFalse
        $result.Operands.Count | Should -Be 0
    }
}
