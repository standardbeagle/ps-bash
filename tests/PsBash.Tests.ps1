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

Describe 'Invoke-BashCat' {
    BeforeAll {
        $catDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-cat-test-$(Get-Random)"
        New-Item -Path $catDir -ItemType Directory -Force | Out-Null

        $file1 = Join-Path $catDir 'file1.txt'
        $file2 = Join-Path $catDir 'file2.txt'
        $tabFile = Join-Path $catDir 'tabs.txt'
        $blankFile = Join-Path $catDir 'blanks.txt'

        # Write files with explicit LF line endings for cross-platform consistency
        [System.IO.File]::WriteAllText($file1, "hello`nworld`n", [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($file2, "foo`nbar`n", [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($tabFile, "col1`tcol2`nval1`tval2`n", [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($blankFile, "line1`n`n`nline2`n`nline3`n", [System.Text.UTF8Encoding]::new($false))
    }

    AfterAll {
        Remove-Item -Path $catDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'cat file.txt returns one BashObject per line' {
        $results = @(Invoke-BashCat $file1)
        $results.Count | Should -Be 2
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.CatLine'
        $results[0].Content | Should -Be 'hello'
        $results[1].Content | Should -Be 'world'
    }

    It 'cat file.txt has LineNumber, Content, FileName, BashText' {
        $results = @(Invoke-BashCat $file1)
        $results[0].LineNumber | Should -Be 1
        $results[0].Content | Should -Be 'hello'
        $results[0].FileName | Should -Be $file1
        $results[0].BashText | Should -Be 'hello'
    }

    It 'cat -n numbers all lines' {
        $results = @(Invoke-BashCat -n $file1)
        $results[0].BashText | Should -Be "     1`thello"
        $results[1].BashText | Should -Be "     2`tworld"
    }

    It 'cat -b numbers only non-blank lines' {
        $results = @(Invoke-BashCat -b $blankFile)
        $numbered = $results | Where-Object { $_.BashText -match '^\s+\d' }
        $blank = $results | Where-Object { $_.Content -eq '' }
        $numbered.Count | Should -Be 3
        $blank | ForEach-Object { $_.BashText | Should -Be '' }
    }

    It 'cat -s squeezes consecutive blank lines' {
        $results = @(Invoke-BashCat -s $blankFile)
        # Original: line1, '', '', line2, '', line3 => line1, '', line2, '', line3
        $contents = $results | ForEach-Object { $_.Content }
        # No two consecutive blank lines
        for ($i = 0; $i -lt $contents.Count - 1; $i++) {
            if ($contents[$i] -eq '') {
                $contents[$i + 1] | Should -Not -Be ''
            }
        }
    }

    It 'cat -E shows $ at end of each line' {
        $results = @(Invoke-BashCat -E $file1)
        $results[0].BashText | Should -Be 'hello$'
        $results[1].BashText | Should -Be 'world$'
    }

    It 'cat -T shows ^I for tabs' {
        $results = @(Invoke-BashCat -T $tabFile)
        $results[0].BashText | Should -Be 'col1^Icol2'
        $results[1].BashText | Should -Be 'val1^Ival2'
    }

    It 'cat nonexistent.txt writes to stderr with exit code 1' {
        $result = Invoke-BashCat '/nonexistent/path/xyz.txt' 2>&1
        $errors = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errors.Count | Should -BeGreaterThan 0
        $errors[0].ToString() | Should -Match 'cat:.*No such file or directory'
        $global:LASTEXITCODE | Should -Be 1
    }

    It 'cat file1.txt file2.txt concatenates' {
        $results = @(Invoke-BashCat $file1 $file2)
        $results.Count | Should -Be 4
        $results[0].Content | Should -Be 'hello'
        $results[1].Content | Should -Be 'world'
        $results[2].Content | Should -Be 'foo'
        $results[3].Content | Should -Be 'bar'
        $results[2].FileName | Should -Be $file2
    }

    It 'pipeline input passes through (stdin mode)' {
        $results = @('hello', 'world' | ForEach-Object { New-BashObject -BashText $_ } | Invoke-BashCat)
        $results.Count | Should -Be 2
        $results[0].Content | Should -Be 'hello'
        $results[1].Content | Should -Be 'world'
    }

    It 'cat - reads from pipeline' {
        $results = @('alpha', 'beta' | ForEach-Object { New-BashObject -BashText $_ } | Invoke-BashCat '-')
        $results.Count | Should -Be 2
        $results[0].Content | Should -Be 'alpha'
        $results[1].Content | Should -Be 'beta'
    }

    It 'exports cat alias pointing to Invoke-BashCat' {
        $alias = Get-Alias -Name cat -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashCat'
    }

    It 'handles CRLF line endings transparently' {
        $crlfFile = Join-Path $catDir 'crlf.txt'
        [System.IO.File]::WriteAllText($crlfFile, "line1`r`nline2`r`n", [System.Text.UTF8Encoding]::new($false))
        $results = @(Invoke-BashCat $crlfFile)
        $results[0].Content | Should -Be 'line1'
        $results[1].Content | Should -Be 'line2'
    }

    It 'handles UTF-8 BOM transparently' {
        $bomFile = Join-Path $catDir 'bom.txt'
        [System.IO.File]::WriteAllText($bomFile, "bomtest`n", [System.Text.UTF8Encoding]::new($true))
        $results = @(Invoke-BashCat $bomFile)
        $results[0].Content | Should -Be 'bomtest'
    }

    It 'cat -n with blanks file numbers all lines including blank' {
        $results = @(Invoke-BashCat -n $blankFile)
        # All lines should be numbered sequentially
        for ($i = 0; $i -lt $results.Count; $i++) {
            $results[$i].LineNumber | Should -Be ($i + 1)
            $results[$i].BashText | Should -Match "^\s+$($i + 1)`t"
        }
    }

    It 'ToString returns BashText' {
        $results = @(Invoke-BashCat $file1)
        "$($results[0])" | Should -Be $results[0].BashText
    }
}
