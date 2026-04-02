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

Describe 'Format-BashSize' {
    It 'returns bytes for small values' {
        Format-BashSize -Bytes 512 | Should -Be '512'
    }

    It 'returns K for kilobytes' {
        Format-BashSize -Bytes 1024 | Should -Be '1.0K'
    }

    It 'returns M for megabytes' {
        Format-BashSize -Bytes (1024 * 1024) | Should -Be '1.0M'
    }

    It 'returns G for gigabytes' {
        Format-BashSize -Bytes (1024 * 1024 * 1024) | Should -Be '1.0G'
    }

    It 'rounds up to one decimal for values under 10 (ceiling like GNU ls)' {
        Format-BashSize -Bytes 1025 | Should -Be '1.1K'
    }

    It 'rounds up to integer for values 10 and above' {
        Format-BashSize -Bytes (240 * 1024 * 1024) | Should -Be '240M'
    }

    It 'returns 0 for zero bytes' {
        Format-BashSize -Bytes 0 | Should -Be '0'
    }
}

Describe 'Format-BashDate' {
    It 'shows HH:mm for recent files' {
        $recent = [datetime]::Now.AddDays(-10)
        $result = Format-BashDate -Date $recent
        $result | Should -Match '^\w{3}\s+\d{1,2}\s\d{2}:\d{2}$'
    }

    It 'shows year for old files' {
        $old = [datetime]::Now.AddMonths(-8)
        $result = Format-BashDate -Date $old
        $result | Should -Match '^\w{3}\s+\d{1,2}\s+\d{4}$'
    }
}

Describe 'ConvertTo-PermissionString' {
    It 'converts 0644 to rw-r--r--' {
        ConvertTo-PermissionString -Mode 0x1A4 | Should -Be 'rw-r--r--'
    }

    It 'converts 0755 to rwxr-xr-x' {
        ConvertTo-PermissionString -Mode 0x1ED | Should -Be 'rwxr-xr-x'
    }

    It 'converts 0700 to rwx------' {
        ConvertTo-PermissionString -Mode 0x1C0 | Should -Be 'rwx------'
    }
}

Describe 'Invoke-BashLs' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-ls-test-$(Get-Random)"
        New-Item -Path $testDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'file1.txt') -Value 'hello'
        Set-Content -Path (Join-Path $testDir 'file2.txt') -Value 'hello world, this is a longer file'
        New-Item -Path (Join-Path $testDir 'subdir') -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $testDir '.hidden') -Value 'secret'
        Set-Content -Path (Join-Path $testDir 'subdir' 'nested.txt') -Value 'nested'
    }

    AfterAll {
        Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns BashObject array with Name property' {
        $results = Invoke-BashLs $testDir
        $results | Should -Not -BeNullOrEmpty
        $results[0].Name | Should -Not -BeNullOrEmpty
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
    }

    It 'has SizeBytes and Permissions properties populated' {
        $results = Invoke-BashLs $testDir
        $first = $results | Where-Object { -not $_.IsDirectory } | Select-Object -First 1
        $first.SizeBytes | Should -BeGreaterThan 0
        $first.Permissions | Should -Match '^-[rwx-]{9}$'
    }

    It 'ls -l has BashText matching permission format' {
        $results = Invoke-BashLs -l $testDir
        $first = $results[0]
        $first.BashText | Should -Match '^[dl-][rwx-]{9}\s+'
    }

    It 'ls -lh shows human-readable sizes' {
        $results = Invoke-BashLs -lh $testDir
        $results | Should -Not -BeNullOrEmpty
        $first = $results[0]
        $first.BashText | Should -Not -BeNullOrEmpty
    }

    It 'ls without -a excludes dotfiles' {
        $results = Invoke-BashLs $testDir
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Not -Contain '.hidden'
    }

    It 'ls -a includes dotfiles' {
        $results = Invoke-BashLs -a $testDir
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Contain '.hidden'
    }

    It 'ls -la combined flags work' {
        $results = Invoke-BashLs -la $testDir
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Contain '.hidden'
        $results[0].BashText | Should -Match '^[dl-][rwx-]{9}\s+'
    }

    It 'ls -R recurses into subdirectories' {
        $results = Invoke-BashLs -R $testDir
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Contain 'nested.txt'
    }

    It 'ls -S sorts by size descending' {
        $results = Invoke-BashLs -S $testDir
        $sizes = $results | Where-Object { -not $_.IsDirectory } | ForEach-Object { $_.SizeBytes }
        if ($sizes.Count -ge 2) {
            $sizes[0] | Should -BeGreaterOrEqual $sizes[1]
        }
    }

    It 'ls -t sorts by time descending' {
        $oldest = Join-Path $testDir 'oldest.txt'
        Set-Content -Path $oldest -Value 'old'
        (Get-Item $oldest).LastWriteTime = [datetime]::Now.AddDays(-30)
        $results = Invoke-BashLs -t $testDir
        $times = $results | ForEach-Object { $_.LastModified }
        if ($times.Count -ge 2) {
            $times[0] | Should -BeGreaterOrEqual $times[1]
        }
        Remove-Item $oldest -Force
    }

    It 'ls nonexistent shows error' {
        $result = Invoke-BashLs '/nonexistent/path/xyz' 2>&1
        $errors = $result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }
        $errors | Should -Not -BeNullOrEmpty
    }

    It 'ls -la | ForEach-Object {$_.IsDirectory} works' {
        $results = Invoke-BashLs -la $testDir
        $dirs = $results | Where-Object { $_.IsDirectory }
        $dirs | Should -Not -BeNullOrEmpty
    }

    It 'IsDirectory is false for regular files' {
        $results = Invoke-BashLs $testDir
        $files = $results | Where-Object { -not $_.IsDirectory }
        $files | Should -Not -BeNullOrEmpty
    }

    It 'exports ls alias pointing to Invoke-BashLs' {
        $alias = Get-Alias -Name ls -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashLs'
    }

    It 'directory entry has d prefix in permissions' {
        $results = Invoke-BashLs -l $testDir
        $dir = $results | Where-Object { $_.IsDirectory } | Select-Object -First 1
        $dir.Permissions | Should -Match '^d'
    }

    It 'file entry has - prefix in permissions' {
        $results = Invoke-BashLs -l $testDir
        $file = $results | Where-Object { -not $_.IsDirectory } | Select-Object -First 1
        $file.Permissions | Should -Match '^-'
    }

    It 'ToString returns BashText' {
        $results = Invoke-BashLs -l $testDir
        "$($results[0])" | Should -Be $results[0].BashText
    }

    It 'ls -r reverses sort order' {
        $normal = Invoke-BashLs -S $testDir
        $reversed = Invoke-BashLs -Sr $testDir
        if ($normal.Count -ge 2) {
            $normal[0].Name | Should -Be $reversed[-1].Name
        }
    }
}
