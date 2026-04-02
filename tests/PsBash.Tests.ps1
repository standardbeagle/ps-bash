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

Describe 'Get-BashText' {
    It 'extracts BashText from BashObject' {
        $obj = New-BashObject -BashText 'hello'
        Get-BashText -InputObject $obj | Should -Be 'hello'
    }

    It 'returns string representation for plain strings' {
        Get-BashText -InputObject 'plain' | Should -Be 'plain'
    }

    It 'returns empty string for null' {
        Get-BashText -InputObject $null | Should -Be ''
    }

    It 'extracts BashText from LsEntry' {
        $entry = [PSCustomObject]@{
            PSTypeName = 'PsBash.LsEntry'
            Name       = 'test.txt'
            BashText   = '-rw-r--r-- 1 user group 1024 Jan  1 00:00 test.txt'
        }
        Get-BashText -InputObject $entry | Should -Be '-rw-r--r-- 1 user group 1024 Jan  1 00:00 test.txt'
    }
}

Describe 'Invoke-BashGrep — Standalone' {
    It 'echo hello | grep h returns object with BashText hello' {
        $result = @(Invoke-BashEcho -n 'hello' | Invoke-BashGrep 'h')
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Be 'hello'
    }

    It 'echo hello | grep x returns nothing' {
        $result = @(Invoke-BashEcho -n 'hello' | Invoke-BashGrep 'x')
        $result.Count | Should -Be 0
    }

    It 'exports grep alias pointing to Invoke-BashGrep' {
        $alias = Get-Alias -Name grep -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashGrep'
    }

    It 'throws with no arguments' {
        { Invoke-BashGrep } | Should -Throw
    }
}

Describe 'Invoke-BashGrep — File Mode' {
    BeforeAll {
        $grepDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-grep-test-$(Get-Random)"
        New-Item -Path $grepDir -ItemType Directory -Force | Out-Null

        $grepFile = Join-Path $grepDir 'sample.txt'
        [System.IO.File]::WriteAllText($grepFile, "hello world`nfoo bar`nHELLO UPPER`nskip this`nbaz foo`n", [System.Text.UTF8Encoding]::new($false))

        $grepFile2 = Join-Path $grepDir 'other.txt'
        [System.IO.File]::WriteAllText($grepFile2, "alpha`nbeta`ngamma`n", [System.Text.UTF8Encoding]::new($false))

        $contextFile = Join-Path $grepDir 'context.txt'
        [System.IO.File]::WriteAllText($contextFile, "line1`nline2`nmatch here`nline4`nline5`nline6`n", [System.Text.UTF8Encoding]::new($false))

        $subDir = Join-Path $grepDir 'sub'
        New-Item -Path $subDir -ItemType Directory -Force | Out-Null
        $nestedFile = Join-Path $subDir 'nested.txt'
        [System.IO.File]::WriteAllText($nestedFile, "nested match`nno match here wait yes match`n", [System.Text.UTF8Encoding]::new($false))
    }

    AfterAll {
        Remove-Item -Path $grepDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'grep pattern file returns GrepMatch objects' {
        $results = @(Invoke-BashGrep 'hello' $grepFile)
        $results.Count | Should -Be 1
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.GrepMatch'
        $results[0].Line | Should -Be 'hello world'
        $results[0].LineNumber | Should -Be 1
        $results[0].FileName | Should -Be $grepFile
    }

    It 'grep -i HELLO file matches case insensitively' {
        $results = @(Invoke-BashGrep -i 'HELLO' $grepFile)
        $results.Count | Should -Be 2
        $results[0].Line | Should -Be 'hello world'
        $results[1].Line | Should -Be 'HELLO UPPER'
    }

    It 'grep -v skip file returns inverted matches' {
        $results = @(Invoke-BashGrep -v 'skip' $grepFile)
        $results.Count | Should -Be 4
        $results | ForEach-Object { $_.Line | Should -Not -Match 'skip' }
    }

    It 'grep -n pattern file shows line numbers in BashText' {
        $results = @(Invoke-BashGrep -n 'foo' $grepFile)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be "2:foo bar"
        $results[1].BashText | Should -Be "5:baz foo"
    }

    It 'grep -c pattern file returns count only' {
        $results = @(Invoke-BashGrep -c 'foo' $grepFile)
        $results.Count | Should -Be 1
        $results[0].BashText | Should -Be '2'
    }

    It 'grep -r pattern dir searches recursively' {
        $results = @(Invoke-BashGrep -r 'match' $grepDir)
        $results.Count | Should -BeGreaterOrEqual 3
    }

    It 'grep -l pattern file returns filenames only' {
        $results = @(Invoke-BashGrep -l 'hello' $grepFile $grepFile2)
        $results.Count | Should -Be 1
        $results[0].BashText | Should -Be $grepFile
    }

    It 'grep -E extended regex with alternation' {
        $results = @(Invoke-BashGrep -E '(foo|alpha)' $grepFile)
        $results.Count | Should -Be 2
        $results[0].Line | Should -Be 'foo bar'
        $results[1].Line | Should -Be 'baz foo'
    }

    It 'grep -A2 -B1 pattern file returns context lines' {
        $results = @(Invoke-BashGrep -A2 -B1 'match' $contextFile)
        $lines = $results | ForEach-Object { $_.Line }
        $lines | Should -Contain 'line2'
        $lines | Should -Contain 'match here'
        $lines | Should -Contain 'line4'
        $lines | Should -Contain 'line5'
    }

    It 'grep nonexistent file writes error' {
        $result = Invoke-BashGrep 'pattern' '/nonexistent/xyz.txt' 2>&1
        $errors = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errors.Count | Should -BeGreaterThan 0
    }

    It 'GrepMatch ToString returns BashText' {
        $results = @(Invoke-BashGrep 'hello' $grepFile)
        "$($results[0])" | Should -Be $results[0].BashText
    }

    It 'grep -n with multiple files includes filename in BashText' {
        $results = @(Invoke-BashGrep -n 'alpha' $grepFile $grepFile2)
        $match = $results | Where-Object { $_.Line -eq 'alpha' }
        $match.BashText | Should -Match ':'
        $match.BashText | Should -Match 'alpha'
    }
}

Describe 'Invoke-BashGrep — Pipeline Bridge' {
    BeforeAll {
        $bridgeDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-bridge-test-$(Get-Random)"
        New-Item -Path $bridgeDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $bridgeDir 'file1.txt') -Value 'hello'
        Set-Content -Path (Join-Path $bridgeDir 'file2.log') -Value 'world'
        New-Item -Path (Join-Path $bridgeDir 'subdir') -ItemType Directory -Force | Out-Null

        $bridgeCatFile = Join-Path $bridgeDir 'data.txt'
        [System.IO.File]::WriteAllText($bridgeCatFile, "alpha line`nbeta line`ngamma line`n", [System.Text.UTF8Encoding]::new($false))
    }

    AfterAll {
        Remove-Item -Path $bridgeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'ls | grep .txt returns PsBash.LsEntry objects' {
        $results = @(Invoke-BashLs $bridgeDir | Invoke-BashGrep '.txt')
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
    }

    It '(ls | grep .txt)[0].Name works (object preserved)' {
        $results = @(Invoke-BashLs $bridgeDir | Invoke-BashGrep '.txt')
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Contain 'file1.txt'
    }

    It '(ls | grep .txt)[0].SizeBytes works (property preserved)' {
        $results = @(Invoke-BashLs $bridgeDir | Invoke-BashGrep '.txt')
        $results[0].SizeBytes | Should -BeGreaterOrEqual 0
    }

    It 'cat file | grep pattern returns PsBash.CatLine objects' {
        $results = @(Invoke-BashCat $bridgeCatFile | Invoke-BashGrep 'beta')
        $results.Count | Should -Be 1
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.CatLine'
        $results[0].Content | Should -Be 'beta line'
    }

    It 'plain string | grep pattern works' {
        $results = @('plain text' | Invoke-BashGrep 'plain')
        $results.Count | Should -Be 1
        "$($results[0])" | Should -Be 'plain text'
    }

    It 'pipeline grep -v inverts correctly' {
        $results = @(Invoke-BashLs $bridgeDir | Invoke-BashGrep -v '.txt')
        $names = $results | ForEach-Object { $_.Name }
        $names | Should -Not -Contain 'file1.txt'
    }

    It 'pipeline grep -c returns count' {
        $results = @(Invoke-BashCat $bridgeCatFile | Invoke-BashGrep -c 'line')
        $results.Count | Should -Be 1
        $results[0].BashText | Should -Be '3'
    }

    It 'pipeline grep -i is case insensitive' {
        $results = @(Invoke-BashEcho -n 'Hello World' | Invoke-BashGrep -i 'hello')
        $results.Count | Should -Be 1
    }

    It 'ls -la | grep .txt preserves IsDirectory property' {
        $results = @(Invoke-BashLs -la $bridgeDir | Invoke-BashGrep '.txt')
        $results[0].IsDirectory | Should -Be $false
    }

    It 'multiple pipeline objects filter correctly' {
        $results = @(
            @('apple', 'banana', 'cherry', 'avocado') | Invoke-BashGrep '^a'
        )
        $results.Count | Should -Be 2
    }
}

Describe 'Invoke-BashSort — Standalone' {
    It 'echo lines | sort returns alphabetical order' {
        $results = @(Invoke-BashEcho -ne "b\na\nc" | Invoke-BashSort)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'a'
        $texts[1] | Should -Be 'b'
        $texts[2] | Should -Be 'c'
    }

    It 'sort -r reverses order' {
        $results = @(Invoke-BashEcho -ne "a\nb\nc" | Invoke-BashSort -r)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'c'
        $texts[1] | Should -Be 'b'
        $texts[2] | Should -Be 'a'
    }

    It 'sort -n sorts numerically (1, 2, 10 not 1, 10, 2)' {
        $results = @(Invoke-BashEcho -ne "10\n1\n2" | Invoke-BashSort -n)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be '1'
        $texts[1] | Should -Be '2'
        $texts[2] | Should -Be '10'
    }

    It 'sort -u removes duplicates' {
        $results = @(Invoke-BashEcho -ne "a\nb\na\nc\nb" | Invoke-BashSort -u)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts.Count | Should -Be 3
        $texts[0] | Should -Be 'a'
        $texts[1] | Should -Be 'b'
        $texts[2] | Should -Be 'c'
    }

    It 'sort -f sorts case insensitively' {
        $results = @(Invoke-BashEcho -ne "Banana\napple\nCherry" | Invoke-BashSort -f)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'apple'
        $texts[1] | Should -Be 'Banana'
        $texts[2] | Should -Be 'Cherry'
    }

    It 'sort -k2 sorts by field 2' {
        $results = @(Invoke-BashEcho -ne "x 3\ny 1\nz 2" | Invoke-BashSort -k2)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'y 1'
        $texts[1] | Should -Be 'z 2'
        $texts[2] | Should -Be 'x 3'
    }

    It 'sort -t: -k3 sorts by field 3 with colon delimiter' {
        $results = @(Invoke-BashEcho -ne "a:b:3\nc:d:1\ne:f:2" | Invoke-BashSort -t: -k3)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'c:d:1'
        $texts[1] | Should -Be 'e:f:2'
        $texts[2] | Should -Be 'a:b:3'
    }

    It 'sort -h sorts human-readable sizes (1K < 1M < 1G)' {
        $results = @(Invoke-BashEcho -ne "1G\n1K\n1M" | Invoke-BashSort -h)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be '1K'
        $texts[1] | Should -Be '1M'
        $texts[2] | Should -Be '1G'
    }

    It 'sort -V sorts version numbers (1.2 < 1.10)' {
        $results = @(Invoke-BashEcho -ne "1.10\n1.2\n1.1" | Invoke-BashSort -V)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be '1.1'
        $texts[1] | Should -Be '1.2'
        $texts[2] | Should -Be '1.10'
    }

    It 'sort -M sorts month names (Jan < Feb < Mar)' {
        $results = @(Invoke-BashEcho -ne "Mar\nJan\nFeb" | Invoke-BashSort -M)
        $texts = $results | ForEach-Object { (Get-BashText -InputObject $_) -replace "`n$", '' }
        $texts[0] | Should -Be 'Jan'
        $texts[1] | Should -Be 'Feb'
        $texts[2] | Should -Be 'Mar'
    }

    It 'sort -c returns exit code 1 if unsorted' {
        Invoke-BashEcho -ne "b\na" | Invoke-BashSort -c
        $global:LASTEXITCODE | Should -Be 1
    }

    It 'sort -c returns exit code 0 if sorted' {
        Invoke-BashEcho -ne "a\nb" | Invoke-BashSort -c
        $global:LASTEXITCODE | Should -Be 0
    }

    It 'exports sort alias pointing to Invoke-BashSort' {
        $alias = Get-Alias -Name sort -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashSort'
    }
}

Describe 'Invoke-BashSort — Object-Aware' {
    BeforeAll {
        $sortDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-sort-test-$(Get-Random)"
        New-Item -Path $sortDir -ItemType Directory -Force | Out-Null
        # Create files with different sizes
        [System.IO.File]::WriteAllBytes((Join-Path $sortDir 'tiny.txt'), [byte[]]::new(100))
        [System.IO.File]::WriteAllBytes((Join-Path $sortDir 'medium.txt'), [byte[]]::new(5000))
        [System.IO.File]::WriteAllBytes((Join-Path $sortDir 'large.txt'), [byte[]]::new(50000))

        $sortCatFile = Join-Path $sortDir 'words.txt'
        [System.IO.File]::WriteAllText($sortCatFile, "cherry`napple`nbanana`n", [System.Text.UTF8Encoding]::new($false))
    }

    AfterAll {
        Remove-Item -Path $sortDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'ls -lh | sort -h returns LsEntry objects sorted by size' {
        $results = @(Invoke-BashLs -lh $sortDir | Invoke-BashSort -h)
        $results.Count | Should -BeGreaterOrEqual 3
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
        $sizes = $results | ForEach-Object { $_.SizeBytes }
        for ($i = 1; $i -lt $sizes.Count; $i++) {
            $sizes[$i] | Should -BeGreaterOrEqual $sizes[$i - 1]
        }
    }

    It '(ls -lh | sort -h)[0].SizeBytes returns smallest file size' {
        $results = @(Invoke-BashLs -lh $sortDir | Invoke-BashSort -h)
        $results[0].SizeBytes | Should -BeLessOrEqual $results[-1].SizeBytes
    }

    It 'ls -la | sort -k5 -n sorts by size field numerically' {
        $results = @(Invoke-BashLs -la $sortDir | Invoke-BashSort -k5 -n)
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
        # Field 5 in ls -l output is the size column
        $sizes = $results | ForEach-Object { $_.SizeBytes }
        for ($i = 1; $i -lt $sizes.Count; $i++) {
            $sizes[$i] | Should -BeGreaterOrEqual $sizes[$i - 1]
        }
    }

    It 'cat file.txt | sort sorts CatLine objects by BashText' {
        $results = @(Invoke-BashCat $sortCatFile | Invoke-BashSort)
        $results.Count | Should -Be 3
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.CatLine'
        $results[0].Content | Should -Be 'apple'
        $results[1].Content | Should -Be 'banana'
        $results[2].Content | Should -Be 'cherry'
    }

    It 'sort preserves original object types in output' {
        $lsResults = @(Invoke-BashLs -lh $sortDir | Invoke-BashSort -h)
        foreach ($r in $lsResults) {
            $r.PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
            $r.Name | Should -Not -BeNullOrEmpty
            $r.SizeBytes | Should -Not -BeNullOrEmpty
        }

        $catResults = @(Invoke-BashCat $sortCatFile | Invoke-BashSort)
        foreach ($r in $catResults) {
            $r.PSTypeNames[0] | Should -Be 'PsBash.CatLine'
            $r.LineNumber | Should -BeGreaterThan 0
        }
    }
}

Describe 'Invoke-BashHead — Pipeline' {
    BeforeAll {
        $headDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-head-test-$(Get-Random)"
        New-Item -Path $headDir -ItemType Directory -Force | Out-Null

        $headFile = Join-Path $headDir 'lines.txt'
        $content = (1..15 | ForEach-Object { "line$_" }) -join "`n"
        [System.IO.File]::WriteAllText($headFile, "$content`n", [System.Text.UTF8Encoding]::new($false))

        Set-Content -Path (Join-Path $headDir 'a.txt') -Value 'aaa'
        Set-Content -Path (Join-Path $headDir 'b.txt') -Value 'bbb'
        Set-Content -Path (Join-Path $headDir 'c.txt') -Value 'ccc'
    }

    AfterAll {
        Remove-Item -Path $headDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'cat file.txt | head returns first 10 lines (objects preserved)' {
        $results = @(Invoke-BashCat $headFile | Invoke-BashHead)
        $results.Count | Should -Be 10
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.CatLine'
        $results[0].Content | Should -Be 'line1'
        $results[9].Content | Should -Be 'line10'
    }

    It 'cat file.txt | head -n 5 returns first 5' {
        $results = @(Invoke-BashCat $headFile | Invoke-BashHead -n 5)
        $results.Count | Should -Be 5
        $results[0].Content | Should -Be 'line1'
        $results[4].Content | Should -Be 'line5'
    }

    It 'head -n 3 file.txt works in file mode' {
        $results = @(Invoke-BashHead -n 3 $headFile)
        $results.Count | Should -Be 3
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.CatLine'
        $results[0].Content | Should -Be 'line1'
        $results[2].Content | Should -Be 'line3'
    }

    It 'ls -la | head -n 5 returns LsEntry objects' {
        $results = @(Invoke-BashLs -la $headDir | Invoke-BashHead -n 5)
        $results.Count | Should -BeLessOrEqual 5
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
    }

    It '(ls -la | head -n 1).Name works' {
        $results = @(Invoke-BashLs $headDir | Invoke-BashHead -n 1)
        $results[0].Name | Should -Not -BeNullOrEmpty
    }

    It 'exports head alias pointing to Invoke-BashHead' {
        $alias = Get-Alias -Name head -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashHead'
    }

    It 'head nonexistent file writes error' {
        $result = Invoke-BashHead -n 1 '/nonexistent/xyz.txt' 2>&1
        $errors = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errors.Count | Should -BeGreaterThan 0
    }
}

Describe 'Invoke-BashTail — Pipeline' {
    BeforeAll {
        $tailDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-tail-test-$(Get-Random)"
        New-Item -Path $tailDir -ItemType Directory -Force | Out-Null

        $tailFile = Join-Path $tailDir 'lines.txt'
        $content = (1..15 | ForEach-Object { "line$_" }) -join "`n"
        [System.IO.File]::WriteAllText($tailFile, "$content`n", [System.Text.UTF8Encoding]::new($false))

        Set-Content -Path (Join-Path $tailDir 'a.txt') -Value 'aaa'
        Set-Content -Path (Join-Path $tailDir 'b.txt') -Value 'bbb'
        Set-Content -Path (Join-Path $tailDir 'c.txt') -Value 'ccc'
    }

    AfterAll {
        Remove-Item -Path $tailDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'cat file.txt | tail returns last 10 lines' {
        $results = @(Invoke-BashCat $tailFile | Invoke-BashTail)
        $results.Count | Should -Be 10
        $results[0].Content | Should -Be 'line6'
        $results[9].Content | Should -Be 'line15'
    }

    It 'cat file.txt | tail -n 5 returns last 5' {
        $results = @(Invoke-BashCat $tailFile | Invoke-BashTail -n 5)
        $results.Count | Should -Be 5
        $results[0].Content | Should -Be 'line11'
        $results[4].Content | Should -Be 'line15'
    }

    It 'tail -n +3 file.txt returns from line 3 onward' {
        $results = @(Invoke-BashTail -n +3 $tailFile)
        $results.Count | Should -Be 13
        $results[0].Content | Should -Be 'line3'
        $results[-1].Content | Should -Be 'line15'
    }

    It 'ls -la | tail -n 5 returns LsEntry objects' {
        $results = @(Invoke-BashLs $tailDir | Invoke-BashTail -n 5)
        $results.Count | Should -BeLessOrEqual 5
        foreach ($r in $results) {
            $r.PSTypeNames[0] | Should -Be 'PsBash.LsEntry'
        }
    }

    It 'exports tail alias pointing to Invoke-BashTail' {
        $alias = Get-Alias -Name tail -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashTail'
    }

    It 'tail nonexistent file writes error' {
        $result = Invoke-BashTail -n 1 '/nonexistent/xyz.txt' 2>&1
        $errors = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errors.Count | Should -BeGreaterThan 0
    }

    It 'tail -n +3 in pipeline mode returns from item 3 onward' {
        $results = @(Invoke-BashCat $tailFile | Invoke-BashTail -n +3)
        $results.Count | Should -Be 13
        $results[0].Content | Should -Be 'line3'
    }
}

Describe 'Invoke-BashWc — File Mode' {
    BeforeAll {
        $wcDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-wc-test-$(Get-Random)"
        New-Item -Path $wcDir -ItemType Directory -Force | Out-Null

        $wcFile1 = Join-Path $wcDir 'file1.txt'
        [System.IO.File]::WriteAllText($wcFile1, "hello world`nfoo bar baz`n", [System.Text.UTF8Encoding]::new($false))

        $wcFile2 = Join-Path $wcDir 'file2.txt'
        [System.IO.File]::WriteAllText($wcFile2, "one two`nthree`n", [System.Text.UTF8Encoding]::new($false))
    }

    AfterAll {
        Remove-Item -Path $wcDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'wc file.txt returns lines, words, bytes in BashText format' {
        $results = @(Invoke-BashWc $wcFile1)
        $results.Count | Should -Be 1
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.WcResult'
        $results[0].Lines | Should -Be 2
        $results[0].Words | Should -Be 5
        $results[0].Bytes | Should -Be 24
        $results[0].FileName | Should -Be $wcFile1
    }

    It 'wc -l file.txt returns line count only' {
        $results = @(Invoke-BashWc -l $wcFile1)
        $results.Count | Should -Be 1
        $results[0].Lines | Should -Be 2
        $results[0].BashText | Should -Match '2'
        $results[0].BashText | Should -Match ([regex]::Escape($wcFile1))
    }

    It 'wc -l file1.txt file2.txt returns per-file and total' {
        $results = @(Invoke-BashWc -l $wcFile1 $wcFile2)
        $results.Count | Should -Be 3
        $results[0].Lines | Should -Be 2
        $results[0].FileName | Should -Be $wcFile1
        $results[1].Lines | Should -Be 2
        $results[1].FileName | Should -Be $wcFile2
        $results[2].Lines | Should -Be 4
        $results[2].FileName | Should -Be 'total'
    }

    It 'wc nonexistent file writes error' {
        $result = Invoke-BashWc '/nonexistent/xyz.txt' 2>&1
        $errors = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errors.Count | Should -BeGreaterThan 0
    }

    It 'exports wc alias pointing to Invoke-BashWc' {
        $alias = Get-Alias -Name wc -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashWc'
    }
}

Describe 'Invoke-BashWc — Pipeline Mode' {
    BeforeAll {
        $wcPipeDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-wc-pipe-test-$(Get-Random)"
        New-Item -Path $wcPipeDir -ItemType Directory -Force | Out-Null

        $wcPipeFile = Join-Path $wcPipeDir 'data.txt'
        [System.IO.File]::WriteAllText($wcPipeFile, "hello world`nfoo bar baz`n", [System.Text.UTF8Encoding]::new($false))

        Set-Content -Path (Join-Path $wcPipeDir 'a.txt') -Value 'aaa'
        Set-Content -Path (Join-Path $wcPipeDir 'b.txt') -Value 'bbb'
    }

    AfterAll {
        Remove-Item -Path $wcPipeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'cat file.txt | wc -l counts BashText lines' {
        $results = @(Invoke-BashCat $wcPipeFile | Invoke-BashWc -l)
        $results.Count | Should -Be 1
        $results[0].Lines | Should -Be 2
    }

    It 'ls -la | wc -l counts objects' {
        $results = @(Invoke-BashLs $wcPipeDir | Invoke-BashWc -l)
        $results.Count | Should -Be 1
        $results[0].Lines | Should -BeGreaterOrEqual 2
    }

    It 'WcResult ToString returns BashText' {
        $results = @(Invoke-BashCat $wcPipeFile | Invoke-BashWc -l)
        "$($results[0])" | Should -Be $results[0].BashText
    }
}

Describe 'Invoke-BashFind' {
    BeforeAll {
        $findDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-find-test-$(Get-Random)"
        New-Item -Path $findDir -ItemType Directory -Force | Out-Null

        # Create test structure
        Set-Content -Path (Join-Path $findDir 'file1.txt') -Value 'hello'
        Set-Content -Path (Join-Path $findDir 'file2.txt') -Value 'world'
        Set-Content -Path (Join-Path $findDir 'test_data.txt') -Value 'test content here'
        Set-Content -Path (Join-Path $findDir 'script.ps1') -Value 'Write-Host hello'

        $subDir = Join-Path $findDir 'subdir'
        New-Item -Path $subDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $subDir 'nested.txt') -Value 'nested content'
        Set-Content -Path (Join-Path $subDir 'nested.ps1') -Value 'Write-Host nested'

        $deepDir = Join-Path $subDir 'deep'
        New-Item -Path $deepDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $deepDir 'deep.txt') -Value 'deep file'

        # Create a large file (>1k)
        $largeContent = 'x' * 2048
        Set-Content -Path (Join-Path $findDir 'large.txt') -Value $largeContent

        # Create an empty file
        [System.IO.File]::WriteAllBytes((Join-Path $findDir 'empty.txt'), [byte[]]@())

        # Create an empty directory
        New-Item -Path (Join-Path $findDir 'emptydir') -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $findDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'find . -name *.txt returns FindEntry objects with BashText paths' {
        $results = @(Invoke-BashFind $findDir -name '*.txt')
        $results.Count | Should -BeGreaterOrEqual 5
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.FindEntry'
        $results[0].BashText | Should -Not -BeNullOrEmpty
        $results[0].Path | Should -Not -BeNullOrEmpty
    }

    It 'find paths use forward slashes' {
        $results = @(Invoke-BashFind $findDir -name '*.txt')
        $nested = $results | Where-Object { $_.Name -eq 'nested.txt' }
        $nested | Should -Not -BeNullOrEmpty
        $nested.BashText | Should -Not -Match '\\'
        $nested.BashText | Should -Match '/'
    }

    It 'find . -type f returns files only' {
        $results = @(Invoke-BashFind $findDir -type f)
        $results | Should -Not -BeNullOrEmpty
        $dirs = @($results | Where-Object { $_.IsDirectory })
        $dirs.Count | Should -Be 0
    }

    It 'find . -type d returns directories only' {
        $results = @(Invoke-BashFind $findDir -type d)
        $results | Should -Not -BeNullOrEmpty
        $files = @($results | Where-Object { -not $_.IsDirectory })
        $files.Count | Should -Be 0
    }

    It 'find . -name *.txt -size +1k returns large files' {
        $results = @(Invoke-BashFind $findDir -name '*.txt' -size '+1k')
        $results.Count | Should -BeGreaterOrEqual 1
        $large = @($results | Where-Object { $_.Name -eq 'large.txt' })
        $large.Count | Should -Be 1
    }

    It 'find . -maxdepth 2 limits search depth' {
        # maxdepth 0 = root only, 1 = root + direct children, 2 = two levels
        $results = @(Invoke-BashFind $findDir -maxdepth 1)
        $deep = @($results | Where-Object { $_.Name -eq 'deep.txt' })
        $deep.Count | Should -Be 0

        $nested = @($results | Where-Object { $_.Name -eq 'nested.txt' })
        $nested.Count | Should -Be 0

        $direct = @($results | Where-Object { $_.Name -eq 'file1.txt' })
        $direct.Count | Should -Be 1
    }

    It 'find . -maxdepth 2 includes two levels deep' {
        $results = @(Invoke-BashFind $findDir -maxdepth 2)
        $nested = @($results | Where-Object { $_.Name -eq 'nested.txt' })
        $nested.Count | Should -Be 1

        $deep = @($results | Where-Object { $_.Name -eq 'deep.txt' })
        $deep.Count | Should -Be 0
    }

    It 'find . -mtime -7 finds recently modified files' {
        $results = @(Invoke-BashFind $findDir -mtime '-7')
        $results.Count | Should -BeGreaterOrEqual 1
        # All test files were just created, so all should match
        $txtFiles = @($results | Where-Object { $_.Name -eq 'file1.txt' })
        $txtFiles.Count | Should -Be 1
    }

    It 'find . -empty finds empty files and dirs' {
        $results = @(Invoke-BashFind $findDir -empty)
        $emptyFile = @($results | Where-Object { $_.Name -eq 'empty.txt' })
        $emptyFile.Count | Should -Be 1

        $emptyDir = @($results | Where-Object { $_.Name -eq 'emptydir' })
        $emptyDir.Count | Should -Be 1
    }

    It 'find . -name *.txt | grep test works with pipeline bridge' {
        $results = @(Invoke-BashFind $findDir -name '*.txt' | Invoke-BashGrep 'test')
        $results.Count | Should -BeGreaterOrEqual 1
        # Should find test_data.txt in path
        $testData = @($results | Where-Object { $_.BashText -match 'test' })
        $testData.Count | Should -BeGreaterOrEqual 1
    }

    It 'find outputs objects suitable for xargs-style consumption' {
        $results = @(Invoke-BashFind $findDir -name '*.txt' -type f)
        $results | Should -Not -BeNullOrEmpty
        # Each result has FullPath for xargs-style use
        foreach ($r in $results) {
            $r.FullPath | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $r.FullPath | Should -Be $true
        }
    }

    It 'FindEntry has metadata from Get-BashFileInfo' {
        $results = @(Invoke-BashFind $findDir -name 'file1.txt')
        $results.Count | Should -Be 1
        $results[0].Permissions | Should -Not -BeNullOrEmpty
        $results[0].SizeBytes | Should -BeGreaterOrEqual 0
        $results[0].LastModified | Should -Not -BeNullOrEmpty
    }

    It 'FindEntry ToString returns BashText' {
        $results = @(Invoke-BashFind $findDir -name 'file1.txt')
        "$($results[0])" | Should -Be $results[0].BashText
    }

    It 'find nonexistent path writes error' {
        $result = Invoke-BashFind '/nonexistent/path/xyz' 2>&1
        $errMsg = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsg.Count | Should -BeGreaterOrEqual 1
    }

    It 'find includes root directory itself' {
        $results = @(Invoke-BashFind $findDir)
        $root = @($results | Where-Object { $_.FullPath -eq (Resolve-Path $findDir).Path })
        $root.Count | Should -Be 1
    }
}

Describe 'Invoke-BashStat' {
    BeforeAll {
        $statDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-stat-test-$(Get-Random)"
        New-Item -Path $statDir -ItemType Directory -Force | Out-Null

        $statFile = Join-Path $statDir 'file.txt'
        [System.IO.File]::WriteAllText($statFile, 'hello world', [System.Text.UTF8Encoding]::new($false))

        $statSubDir = Join-Path $statDir 'subdir'
        New-Item -Path $statSubDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $statDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'stat file.txt returns StatEntry with all properties' {
        $result = @(Invoke-BashStat $statFile)
        $result.Count | Should -Be 1
        $entry = $result[0]
        $entry.PSObject.TypeNames[0] | Should -Be 'PsBash.StatEntry'
        $entry.Name | Should -Be 'file.txt'
        $entry.FullPath | Should -Be (Resolve-Path $statFile).Path
        $entry.SizeBytes | Should -Be 11
        $entry.Permissions | Should -Not -BeNullOrEmpty
        $entry.OctalPerms | Should -Match '^\d{4}$'
        $entry.LinkCount | Should -BeGreaterOrEqual 1
        $entry.Owner | Should -Not -BeNullOrEmpty
        $entry.Group | Should -Not -BeNullOrEmpty
        $entry.Inode | Should -BeOfType [long]
        $entry.Blocks | Should -BeOfType [long]
        $entry.Device | Should -BeOfType [long]
        $entry.LastModified | Should -BeOfType [datetime]
        $entry.MtimeEpoch | Should -BeGreaterThan 0
        $entry.BashText | Should -Not -BeNullOrEmpty
    }

    It 'stat -c %s file.txt returns size only' {
        $result = @(Invoke-BashStat -c '%s' $statFile)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Be "11`n"
    }

    It 'stat -c "%a %n" file.txt returns octal permissions + name' {
        $result = @(Invoke-BashStat -c '%a %n' $statFile)
        $result.Count | Should -Be 1
        $text = $result[0].BashText.TrimEnd("`n")
        $text | Should -Match '^\d{4} file\.txt$'
    }

    It 'stat --printf="%s\n" file.txt returns printf-style with no trailing newline' {
        $result = @(Invoke-BashStat '--printf=%s\n' $statFile)
        $result.Count | Should -Be 1
        # printf-style: escape sequences processed, no auto-trailing newline
        $result[0].BashText | Should -Be "11`n"
    }

    It 'stat --printf="%s" file.txt has no trailing newline' {
        $result = @(Invoke-BashStat '--printf=%s' $statFile)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Be '11'
    }

    It 'stat -t file.txt returns terse format' {
        $result = @(Invoke-BashStat -t $statFile)
        $result.Count | Should -Be 1
        $fields = $result[0].BashText.TrimEnd("`n") -split ' '
        $fields.Count | Should -Be 14
        $fields[0] | Should -Be 'file.txt'
        $fields[1] | Should -Be '11'
    }

    It 'stat nonexistent writes error and sets exit code 1' {
        $result = Invoke-BashStat '/nonexistent/path/xyz' 2>&1
        $errMsg = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsg.Count | Should -BeGreaterOrEqual 1
        $errMsg[0].Exception.Message | Should -BeLike "stat: cannot stat*"
        $global:LASTEXITCODE | Should -Be 1
    }

    It 'stat on directory returns correct type' {
        $result = @(Invoke-BashStat $statSubDir)
        $result.Count | Should -Be 1
        $result[0].IsDirectory | Should -Be $true
        $result[0].BashText | Should -Match 'directory'
    }

    It 'stat default output contains File, Size, Device, Inode, Access, Modify lines' {
        $result = @(Invoke-BashStat $statFile)
        $text = $result[0].BashText
        $text | Should -Match 'File:'
        $text | Should -Match 'Size:'
        $text | Should -Match 'Blocks:'
        $text | Should -Match 'Device:'
        $text | Should -Match 'Inode:'
        $text | Should -Match 'Access:'
        $text | Should -Match 'Modify:'
    }

    It 'stat -c "%U %G" returns owner and group' {
        $result = @(Invoke-BashStat -c '%U %G' $statFile)
        $text = $result[0].BashText.TrimEnd("`n")
        $parts = $text -split ' '
        $parts.Count | Should -Be 2
        $parts[0] | Should -Not -BeNullOrEmpty
        $parts[1] | Should -Not -BeNullOrEmpty
    }

    It 'stat -c "%i %b %d" returns inode blocks device' {
        $result = @(Invoke-BashStat -c '%i %b %d' $statFile)
        $text = $result[0].BashText.TrimEnd("`n")
        $parts = $text -split ' '
        $parts.Count | Should -Be 3
        [long]::TryParse($parts[0], [ref]$null) | Should -Be $true
        [long]::TryParse($parts[1], [ref]$null) | Should -Be $true
        [long]::TryParse($parts[2], [ref]$null) | Should -Be $true
    }

    It 'stat -c "%A" returns permission string like drwx or -rw-' {
        $result = @(Invoke-BashStat -c '%A' $statFile)
        $text = $result[0].BashText.TrimEnd("`n")
        $text | Should -Match '^[-dlrwx]{10}$'
    }

    It 'stat -c "%Y" returns mtime epoch as positive integer' {
        $result = @(Invoke-BashStat -c '%Y' $statFile)
        $text = $result[0].BashText.TrimEnd("`n")
        [long]$epoch = [long]$text
        $epoch | Should -BeGreaterThan 0
    }

    It 'stat multiple files returns one entry per file' {
        $result = @(Invoke-BashStat $statFile $statSubDir)
        $result.Count | Should -Be 2
        $result[0].Name | Should -Be 'file.txt'
        $result[1].Name | Should -Be 'subdir'
    }

    It 'stat -c "%%s" returns literal %s' {
        $result = @(Invoke-BashStat -c '%%s' $statFile)
        $text = $result[0].BashText.TrimEnd("`n")
        $text | Should -Be '%s'
    }

    It 'StatEntry ToString returns BashText' {
        $result = @(Invoke-BashStat $statFile)
        "$($result[0])" | Should -Be $result[0].BashText
    }

    It 'stat exports stat alias pointing to Invoke-BashStat' {
        $alias = Get-Alias -Name stat -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashStat'
    }

    It 'stat on Linux returns real inode and blocks' -Skip:($IsWindows -or $IsMacOS) {
        $result = @(Invoke-BashStat $statFile)
        $result[0].Inode | Should -BeGreaterThan 0
        $result[0].Blocks | Should -BeGreaterOrEqual 0
    }

    It 'stat on Windows synthesizes inode=0 and device from drive' -Skip:(-not $IsWindows) {
        $result = @(Invoke-BashStat $statFile)
        $result[0].Inode | Should -Be 0
        $result[0].Device | Should -BeGreaterOrEqual 0
    }
}

Describe 'Integration: Multi-Command Pipelines' {
    BeforeAll {
        $intDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-integration-test-$(Get-Random)"
        New-Item -Path $intDir -ItemType Directory -Force | Out-Null

        # Create files with varying sizes for sorting
        Set-Content -Path (Join-Path $intDir 'alpha.txt') -Value 'alpha content'
        Set-Content -Path (Join-Path $intDir 'beta.txt') -Value 'beta content here longer'
        Set-Content -Path (Join-Path $intDir 'gamma.log') -Value 'gamma log data'
        Set-Content -Path (Join-Path $intDir 'delta.txt') -Value 'x'

        # Create a file with specific patterns for grep
        $patternContent = "line one has pattern`nline two is plain`nline three has pattern again`nline four is plain"
        [System.IO.File]::WriteAllText((Join-Path $intDir 'patterns.txt'), $patternContent, [System.Text.UTF8Encoding]::new($false))

        # Create ps1 files for find tests
        Set-Content -Path (Join-Path $intDir 'a.ps1') -Value 'script a'
        Set-Content -Path (Join-Path $intDir 'b.ps1') -Value 'script b'
        Set-Content -Path (Join-Path $intDir 'c.ps1') -Value 'script c'

        $intSub = Join-Path $intDir 'sub'
        New-Item -Path $intSub -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $intSub 'd.ps1') -Value 'script d'
        Set-Content -Path (Join-Path $intSub 'e.ps1') -Value 'script e'
        Set-Content -Path (Join-Path $intSub 'f.ps1') -Value 'script f'
    }

    AfterAll {
        Remove-Item -Path $intDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'ls -la | grep .txt | sort -k5 -n preserves LsEntry objects through 3 stages' {
        $results = @(Invoke-BashLs -la $intDir | Invoke-BashGrep '\.txt' | Invoke-BashSort -k 5 -n)
        $results.Count | Should -BeGreaterOrEqual 1
        # Objects should still have LsEntry properties
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.LsEntry'
        $results[0].SizeBytes | Should -Not -BeNullOrEmpty
    }

    It 'ls -la | grep .txt | head -n 3 returns filtered limited LsEntry objects' {
        $results = @(Invoke-BashLs -la $intDir | Invoke-BashGrep '\.txt' | Invoke-BashHead -n 3)
        $results.Count | Should -BeLessOrEqual 3
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.LsEntry'
    }

    It 'cat file.txt | grep pattern | wc -l counts matching lines' {
        $patternsFile = Join-Path $intDir 'patterns.txt'
        $results = @(Invoke-BashCat $patternsFile | Invoke-BashGrep 'pattern' | Invoke-BashWc -l)
        $results.Count | Should -Be 1
        $results[0].Lines | Should -Be 2
    }

    It 'find . -name *.ps1 | head -n 5 returns limited FindEntry objects' {
        $results = @(Invoke-BashFind $intDir -name '*.ps1' | Invoke-BashHead -n 5)
        $results.Count | Should -BeLessOrEqual 5
        $results.Count | Should -BeGreaterOrEqual 1
        # Pipeline bridge: find emits FindEntry, head passes through
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.FindEntry'
    }

    It 'ls -lh | sort -h | tail -n 5 returns largest files as LsEntry objects' {
        $results = @(Invoke-BashLs -lh $intDir | Invoke-BashSort -h | Invoke-BashTail -n 5)
        $results.Count | Should -BeLessOrEqual 5
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.LsEntry'
    }
}
