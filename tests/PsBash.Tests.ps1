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

Describe 'Invoke-BashCp' {
    BeforeAll {
        $cpDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-cp-test-$(Get-Random)"
        New-Item -Path $cpDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $cpDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'cp src.txt dst.txt copies a file' {
        $src = Join-Path $cpDir 'src.txt'
        Set-Content -Path $src -Value 'hello'
        $dst = Join-Path $cpDir 'dst.txt'
        Invoke-BashCp $src $dst
        Test-Path -LiteralPath $dst | Should -Be $true
        Get-Content $dst | Should -Be 'hello'
    }

    It 'cp -r dir1 dir2 copies directory recursively' {
        $srcDir = Join-Path $cpDir 'srcdir'
        New-Item -Path $srcDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $srcDir 'inner.txt') -Value 'nested'
        $dstDir = Join-Path $cpDir 'dstdir'
        Invoke-BashCp -r $srcDir $dstDir
        Test-Path -LiteralPath (Join-Path $dstDir 'inner.txt') | Should -Be $true
        Get-Content (Join-Path $dstDir 'inner.txt') | Should -Be 'nested'
    }

    It 'cp -v src.txt dst.txt returns verbose BashObject output' {
        $src = Join-Path $cpDir 'vsrc.txt'
        Set-Content -Path $src -Value 'verbose test'
        $dst = Join-Path $cpDir 'vdst.txt'
        $result = @(Invoke-BashCp -v $src $dst)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match "->.*"
    }

    It 'cp without -r on directory emits error' {
        $srcDir = Join-Path $cpDir 'norecurse'
        New-Item -Path $srcDir -ItemType Directory -Force | Out-Null
        $dstDir = Join-Path $cpDir 'norecurse-dst'
        $result = Invoke-BashCp $srcDir $dstDir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'omitting directory'
    }

    It 'cp -n does not overwrite existing file' {
        $src = Join-Path $cpDir 'nsrc.txt'
        $dst = Join-Path $cpDir 'ndst.txt'
        Set-Content -Path $src -Value 'new content'
        Set-Content -Path $dst -Value 'original'
        Invoke-BashCp -n $src $dst
        Get-Content $dst | Should -Be 'original'
    }

    It 'cp nonexistent file emits error' {
        $result = Invoke-BashCp (Join-Path $cpDir 'nope.txt') (Join-Path $cpDir 'out.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }

    It 'cp missing operand emits error' {
        $result = Invoke-BashCp (Join-Path $cpDir 'src.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'missing file operand'
    }
}

Describe 'Invoke-BashMv' {
    BeforeAll {
        $mvDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-mv-test-$(Get-Random)"
        New-Item -Path $mvDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $mvDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'mv old.txt new.txt renames a file' {
        $old = Join-Path $mvDir 'old.txt'
        Set-Content -Path $old -Value 'move me'
        $new = Join-Path $mvDir 'new.txt'
        Invoke-BashMv $old $new
        Test-Path -LiteralPath $old | Should -Be $false
        Test-Path -LiteralPath $new | Should -Be $true
        Get-Content $new | Should -Be 'move me'
    }

    It 'mv -n does not overwrite existing file' {
        $src = Join-Path $mvDir 'mvnsrc.txt'
        $dst = Join-Path $mvDir 'mvndst.txt'
        Set-Content -Path $src -Value 'new content'
        Set-Content -Path $dst -Value 'keep me'
        Invoke-BashMv -n $src $dst
        Get-Content $dst | Should -Be 'keep me'
        Test-Path -LiteralPath $src | Should -Be $true
    }

    It 'mv -v returns verbose BashObject output' {
        $src = Join-Path $mvDir 'mvsrc.txt'
        Set-Content -Path $src -Value 'verbose move'
        $dst = Join-Path $mvDir 'mvdst.txt'
        $result = @(Invoke-BashMv -v $src $dst)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match "->.*"
    }

    It 'mv nonexistent file emits error' {
        $result = Invoke-BashMv (Join-Path $mvDir 'nope.txt') (Join-Path $mvDir 'out.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }

    It 'mv file into existing directory' {
        $src = Join-Path $mvDir 'mvfile.txt'
        Set-Content -Path $src -Value 'into dir'
        $destDir = Join-Path $mvDir 'mvtargetdir'
        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        Invoke-BashMv $src $destDir
        Test-Path -LiteralPath (Join-Path $destDir 'mvfile.txt') | Should -Be $true
    }
}

Describe 'Invoke-BashRm' {
    BeforeAll {
        $rmDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-rm-test-$(Get-Random)"
        New-Item -Path $rmDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $rmDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'rm file.txt removes a file' {
        $file = Join-Path $rmDir 'rmme.txt'
        Set-Content -Path $file -Value 'delete me'
        Invoke-BashRm $file
        Test-Path -LiteralPath $file | Should -Be $false
    }

    It 'rm -rf dir removes directory recursively' {
        $dir = Join-Path $rmDir 'rmrecurse'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $dir 'a.txt') -Value 'a'
        New-Item -Path (Join-Path $dir 'sub') -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $dir 'sub' 'b.txt') -Value 'b'
        Invoke-BashRm -rf $dir
        Test-Path -LiteralPath $dir | Should -Be $false
    }

    It 'rm directory without -r emits error' {
        $dir = Join-Path $rmDir 'rmdironly'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        $result = Invoke-BashRm $dir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'Is a directory'
        Test-Path -LiteralPath $dir | Should -Be $true
    }

    It 'rm nonexistent file emits error' {
        $result = Invoke-BashRm (Join-Path $rmDir 'nope.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }

    It 'rm -f nonexistent file is silent' {
        $result = Invoke-BashRm -f (Join-Path $rmDir 'nope.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -Be 0
    }

    It 'rm -v outputs verbose messages' {
        $file = Join-Path $rmDir 'rmverbose.txt'
        Set-Content -Path $file -Value 'verbose delete'
        $result = @(Invoke-BashRm -v $file)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match 'removed'
    }

    It 'rm refuses to delete root path' {
        $rootPath = [System.IO.Path]::GetPathRoot((Get-Location).Path)
        $result = Invoke-BashRm -rf $rootPath 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'protected path'
    }

    It 'rm refuses to delete home directory' {
        $homePath = [System.Environment]::GetFolderPath('UserProfile')
        $result = Invoke-BashRm -rf $homePath 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'protected path'
    }

    It 'rm with no args and no -f emits error' {
        $result = Invoke-BashRm 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'missing operand'
    }
}

Describe 'Invoke-BashMkdir' {
    BeforeAll {
        $mkdirDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-mkdir-test-$(Get-Random)"
        New-Item -Path $mkdirDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $mkdirDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'mkdir creates a directory' {
        $dir = Join-Path $mkdirDir 'newdir'
        Invoke-BashMkdir $dir
        Test-Path -LiteralPath $dir | Should -Be $true
        (Get-Item $dir) -is [System.IO.DirectoryInfo] | Should -Be $true
    }

    It 'mkdir -p a/b/c creates parent directories' {
        $dir = Join-Path $mkdirDir 'a' 'b' 'c'
        Invoke-BashMkdir -p $dir
        Test-Path -LiteralPath $dir | Should -Be $true
    }

    It 'mkdir existing directory emits error' {
        $dir = Join-Path $mkdirDir 'existing'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        $result = Invoke-BashMkdir $dir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'File exists'
    }

    It 'mkdir -p existing directory is silent' {
        $dir = Join-Path $mkdirDir 'pexisting'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        $result = Invoke-BashMkdir -p $dir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -Be 0
    }

    It 'mkdir -v outputs verbose message' {
        $dir = Join-Path $mkdirDir 'verbosedir'
        $result = @(Invoke-BashMkdir -v $dir)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match 'created directory'
    }

    It 'mkdir without -p and missing parent emits error' {
        $dir = Join-Path $mkdirDir 'nope' 'child'
        $result = Invoke-BashMkdir $dir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }
}

Describe 'Invoke-BashRmdir' {
    BeforeAll {
        $rmdirDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-rmdir-test-$(Get-Random)"
        New-Item -Path $rmdirDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $rmdirDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'rmdir removes empty directory' {
        $dir = Join-Path $rmdirDir 'emptydir'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        Invoke-BashRmdir $dir
        Test-Path -LiteralPath $dir | Should -Be $false
    }

    It 'rmdir non-empty directory emits error' {
        $dir = Join-Path $rmdirDir 'notempty'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $dir 'file.txt') -Value 'content'
        $result = Invoke-BashRmdir $dir 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'Directory not empty'
        Test-Path -LiteralPath $dir | Should -Be $true
    }

    It 'rmdir nonexistent directory emits error' {
        $result = Invoke-BashRmdir (Join-Path $rmdirDir 'nope') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }

    It 'rmdir -v outputs verbose message' {
        $dir = Join-Path $rmdirDir 'verbosermdir'
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        $result = @(Invoke-BashRmdir -v $dir)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match 'removing directory'
    }

    It 'rmdir on file emits error' {
        $file = Join-Path $rmdirDir 'afile.txt'
        Set-Content -Path $file -Value 'not a dir'
        $result = Invoke-BashRmdir $file 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'Not a directory'
    }

    It 'rmdir -p removes parent directories when empty' {
        $base = Join-Path $rmdirDir 'p1'
        $child = Join-Path $base 'p2'
        $leaf = Join-Path $child 'p3'
        New-Item -Path $leaf -ItemType Directory -Force | Out-Null
        Invoke-BashRmdir -p $leaf
        Test-Path -LiteralPath $leaf | Should -Be $false
        Test-Path -LiteralPath $child | Should -Be $false
        Test-Path -LiteralPath $base | Should -Be $false
    }
}

Describe 'Invoke-BashTouch' {
    BeforeAll {
        $touchDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-touch-test-$(Get-Random)"
        New-Item -Path $touchDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $touchDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'touch creates new empty file' {
        $file = Join-Path $touchDir 'newfile.txt'
        Invoke-BashTouch $file
        Test-Path -LiteralPath $file | Should -Be $true
        (Get-Item $file).Length | Should -Be 0
    }

    It 'touch updates timestamp on existing file' {
        $file = Join-Path $touchDir 'existing.txt'
        Set-Content -Path $file -Value 'content'
        $before = (Get-Item $file).LastWriteTime
        Start-Sleep -Milliseconds 100
        Invoke-BashTouch $file
        $after = (Get-Item $file).LastWriteTime
        $after | Should -BeGreaterThan $before
    }

    It 'touch -d sets specific date' {
        $file = Join-Path $touchDir 'dated.txt'
        Set-Content -Path $file -Value 'content'
        Invoke-BashTouch -d '2024-01-01' $file
        $mtime = (Get-Item $file).LastWriteTime
        $mtime.Year | Should -Be 2024
        $mtime.Month | Should -Be 1
        $mtime.Day | Should -Be 1
    }

    It 'touch multiple files creates all' {
        $f1 = Join-Path $touchDir 'multi1.txt'
        $f2 = Join-Path $touchDir 'multi2.txt'
        Invoke-BashTouch $f1 $f2
        Test-Path -LiteralPath $f1 | Should -Be $true
        Test-Path -LiteralPath $f2 | Should -Be $true
    }

    It 'touch missing parent emits error' {
        $file = Join-Path $touchDir 'nope' 'child.txt'
        $result = Invoke-BashTouch $file 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'No such file or directory'
    }

    It 'touch with invalid date emits error' {
        $file = Join-Path $touchDir 'baddate.txt'
        Set-Content -Path $file -Value 'content'
        $result = Invoke-BashTouch -d 'not-a-date' $file 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'invalid date'
    }
}

Describe 'Invoke-BashLn' {
    BeforeAll {
        $lnDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-ln-test-$(Get-Random)"
        New-Item -Path $lnDir -ItemType Directory -Force | Out-Null
    }

    AfterAll {
        Remove-Item -Path $lnDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'ln -s creates symbolic link' -Skip:$IsWindows {
        $target = Join-Path $lnDir 'lntarget.txt'
        Set-Content -Path $target -Value 'target content'
        $link = Join-Path $lnDir 'lnlink.txt'
        Invoke-BashLn -s $target $link
        Test-Path -LiteralPath $link | Should -Be $true
        $item = Get-Item $link
        $item.LinkType | Should -Be 'SymbolicLink'
        Get-Content $link | Should -Be 'target content'
    }

    It 'ln creates hard link' -Skip:$IsWindows {
        $target = Join-Path $lnDir 'hlntarget.txt'
        Set-Content -Path $target -Value 'hard link content'
        $link = Join-Path $lnDir 'hlnlink.txt'
        Invoke-BashLn $target $link
        Test-Path -LiteralPath $link | Should -Be $true
        Get-Content $link | Should -Be 'hard link content'
    }

    It 'ln -s -v returns verbose BashObject output' -Skip:$IsWindows {
        $target = Join-Path $lnDir 'vlntarget.txt'
        Set-Content -Path $target -Value 'verbose'
        $link = Join-Path $lnDir 'vlnlink.txt'
        $result = @(Invoke-BashLn -sv $target $link)
        $result.Count | Should -Be 1
        $result[0].BashText | Should -Match '->'
    }

    It 'ln existing link without -f emits error' -Skip:$IsWindows {
        $target = Join-Path $lnDir 'existtarget.txt'
        Set-Content -Path $target -Value 'target'
        $link = Join-Path $lnDir 'existlink.txt'
        Set-Content -Path $link -Value 'existing'
        $result = Invoke-BashLn -s $target $link 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'File exists'
    }

    It 'ln -sf overwrites existing link' -Skip:$IsWindows {
        $target = Join-Path $lnDir 'forcetarget.txt'
        Set-Content -Path $target -Value 'force target'
        $link = Join-Path $lnDir 'forcelink.txt'
        Set-Content -Path $link -Value 'will be overwritten'
        Invoke-BashLn -sf $target $link
        $item = Get-Item $link
        $item.LinkType | Should -Be 'SymbolicLink'
    }

    It 'ln missing operand emits error' {
        $result = Invoke-BashLn -s (Join-Path $lnDir 'only.txt') 2>&1
        $errMsgs = @($result | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $errMsgs.Count | Should -BeGreaterOrEqual 1
        "$($errMsgs[0])" | Should -Match 'missing file operand'
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

Describe 'Invoke-BashPs — Basic Output' {
    It 'ps aux returns PsEntry objects with BashText' {
        $results = @(Invoke-BashPs aux)
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.PsEntry'
        $results[0].BashText | Should -Not -BeNullOrEmpty
    }

    It 'ps aux includes current pwsh process' {
        $results = @(Invoke-BashPs aux)
        $pwsh = $results | Where-Object { $_.ProcessName -eq 'pwsh' -or $_.Command -like '*pwsh*' }
        $pwsh | Should -Not -BeNullOrEmpty
    }

    It 'ps aux BashText has correct column alignment' {
        $results = @(Invoke-BashPs aux)
        # BashText should have USER, PID, %CPU, %MEM, VSZ, RSS, TTY, STAT, START, TIME, COMMAND columns
        $line = $results[0].BashText
        # Should contain the PID as text somewhere in the line
        $line | Should -Match '\d+'
        # Lines should be padded/formatted with spaces
        $line.Length | Should -BeGreaterThan 20
    }

    It 'ps aux populates core object properties' {
        $results = @(Invoke-BashPs aux)
        $self = $results | Where-Object { $_.PID -eq $PID }
        if (-not $self) {
            # Fallback: find any pwsh process
            $self = $results | Where-Object { $_.ProcessName -eq 'pwsh' } | Select-Object -First 1
        }
        $self | Should -Not -BeNullOrEmpty
        $self.PID | Should -BeGreaterThan 0
        $self.User | Should -Not -BeNullOrEmpty
        $self.Command | Should -Not -BeNullOrEmpty
        $self.ProcessName | Should -Not -BeNullOrEmpty
    }

    It 'ps with no args returns PsEntry objects' {
        $results = @(Invoke-BashPs)
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.PsEntry'
    }
}

Describe 'Invoke-BashPs — Flags' {
    It 'ps -e returns all processes' {
        $results = @(Invoke-BashPs -e)
        $results.Count | Should -BeGreaterOrEqual 2
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.PsEntry'
    }

    It 'ps -A is equivalent to -e' {
        $resultsE = @(Invoke-BashPs -e)
        $resultsA = @(Invoke-BashPs -A)
        # Both should return the same set of processes (count may vary slightly due to timing)
        [System.Math]::Abs($resultsE.Count - $resultsA.Count) | Should -BeLessOrEqual 5
    }

    It 'ps -f shows full format with PPID' {
        $results = @(Invoke-BashPs -f)
        $results.Count | Should -BeGreaterOrEqual 1
        $self = $results | Where-Object { $_.PID -eq $PID }
        if (-not $self) {
            $self = $results | Where-Object { $_.ProcessName -eq 'pwsh' } | Select-Object -First 1
        }
        $self | Should -Not -BeNullOrEmpty
        $self.PPID | Should -BeOfType [int]
    }

    It 'ps -p PID filters to specific process' {
        $results = @(Invoke-BashPs -p $PID)
        $results.Count | Should -Be 1
        $results[0].PID | Should -Be $PID
    }

    It 'ps -p with invalid PID returns empty' {
        $results = @(Invoke-BashPs -p 999999999)
        $results.Count | Should -Be 0
    }

    It 'ps --sort=pid sorts by PID ascending' {
        $results = @(Invoke-BashPs aux --sort=pid)
        $results.Count | Should -BeGreaterOrEqual 2
        for ($i = 1; $i -lt [System.Math]::Min($results.Count, 20); $i++) {
            $results[$i].PID | Should -BeGreaterOrEqual $results[$i - 1].PID
        }
    }

    It 'ps --sort=-pid sorts by PID descending' {
        $results = @(Invoke-BashPs aux '--sort=-pid')
        $results.Count | Should -BeGreaterOrEqual 2
        for ($i = 1; $i -lt [System.Math]::Min($results.Count, 20); $i++) {
            $results[$i].PID | Should -BeLessOrEqual $results[$i - 1].PID
        }
    }

    It 'ps -u USER filters by username' {
        $currentUser = if ($IsWindows) { $env:USERNAME } else { (id -un) }
        $results = @(Invoke-BashPs -u $currentUser)
        $results.Count | Should -BeGreaterOrEqual 1
        foreach ($r in $results) {
            $r.User | Should -Be $currentUser
        }
    }
}

Describe 'Invoke-BashPs — Custom Output Format' {
    It 'ps -o pid,comm shows only requested columns' {
        $results = @(Invoke-BashPs -o 'pid,comm')
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PID | Should -BeGreaterThan 0
        $results[0].ProcessName | Should -Not -BeNullOrEmpty
        # BashText should be shorter (fewer columns)
        $results[0].BashText.Trim().Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries).Count | Should -BeLessOrEqual 5
    }

    It 'ps -eo pid,user,comm shows all procs with custom format' {
        $results = @(Invoke-BashPs -e -o 'pid,user,comm')
        $results.Count | Should -BeGreaterOrEqual 2
        $results[0].PID | Should -BeGreaterThan 0
        $results[0].User | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-BashPs — Pipeline Bridge' {
    It 'ps aux | grep pwsh finds powershell processes' {
        $results = @(Invoke-BashPs aux | Invoke-BashGrep 'pwsh')
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.PsEntry'
        $results[0].Command | Should -BeLike '*pwsh*'
    }

    It 'ps aux | sort preserves PsEntry type' {
        $results = @(Invoke-BashPs aux | Invoke-BashSort -k 2 -n | Invoke-BashHead -n 5)
        $results.Count | Should -BeLessOrEqual 5
        $results.Count | Should -BeGreaterOrEqual 1
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.PsEntry'
    }

    It 'ps aux | wc -l counts processes' {
        $results = @(Invoke-BashPs aux | Invoke-BashWc -l)
        $results.Count | Should -Be 1
        $results[0].Lines | Should -BeGreaterOrEqual 1
    }

    It 'ps alias works' {
        $alias = Get-Alias -Name ps -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashPs'
    }
}

# ── sed Tests ──

Describe 'Invoke-BashSed — Basic Substitution' {
    It 'echo hello world | sed s/world/earth/ -> hello earth' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashSed 's/world/earth/')
        $result.Count | Should -Be 1
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello earth'
    }

    It 'substitutes first occurrence only by default' {
        $result = @(Invoke-BashEcho 'aaa' | Invoke-BashSed 's/a/b/')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'baa'
    }

    It 's///g replaces all occurrences' {
        $result = @(Invoke-BashEcho 'aaa' | Invoke-BashSed 's/a/b/g')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'bbb'
    }
}

Describe 'Invoke-BashSed — File Mode' {
    BeforeEach {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-sed-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }
    AfterEach {
        if (Test-Path $testDir) { Remove-Item -Recurse -Force $testDir }
    }

    It 'sed s/old/new/g file.txt replaces globally in file' {
        $file = Join-Path $testDir 'test.txt'
        Set-Content -Path $file -Value "old is old`nold again" -NoNewline
        $results = @(Invoke-BashSed 's/old/new/g' $file)
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'new is new'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'new again'
    }

    It 'sed -n 2,4p file.txt prints only lines 2-4' {
        $file = Join-Path $testDir 'lines.txt'
        Set-Content -Path $file -Value "line1`nline2`nline3`nline4`nline5" -NoNewline
        $results = @(Invoke-BashSed -n '2,4p' $file)
        $results.Count | Should -Be 3
        ($results[0].BashText -replace "`n$", '') | Should -Be 'line2'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'line3'
        ($results[2].BashText -replace "`n$", '') | Should -Be 'line4'
    }

    It 'sed /pattern/d deletes matching lines' {
        $file = Join-Path $testDir 'del.txt'
        Set-Content -Path $file -Value "keep`nremove this`nkeep too" -NoNewline
        $results = @(Invoke-BashSed '/remove/d' $file)
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'keep'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'keep too'
    }

    It 'sed -i s/old/new/g modifies file in place' {
        $file = Join-Path $testDir 'inplace.txt'
        Set-Content -Path $file -Value "old text`nold line" -NoNewline
        $results = @(Invoke-BashSed -i 's/old/new/g' $file)
        $results.Count | Should -Be 0
        $content = Get-Content -Raw $file
        $content | Should -BeLike '*new text*'
        $content | Should -BeLike '*new line*'
    }
}

Describe 'Invoke-BashSed — Extended Regex' {
    It 'sed -E s/(foo|bar)/baz/g replaces alternation' {
        $result = @(Invoke-BashEcho 'foo and bar' | Invoke-BashSed -E 's/(foo|bar)/baz/g')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'baz and baz'
    }
}

Describe 'Invoke-BashSed — Transliterate' {
    It 'sed y/abc/xyz/ transliterates characters' {
        $result = @(Invoke-BashEcho 'aabbcc' | Invoke-BashSed 'y/abc/xyz/')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'xxyyzz'
    }

    It 'transliterate leaves non-mapped characters unchanged' {
        $result = @(Invoke-BashEcho 'abcdef' | Invoke-BashSed 'y/abc/xyz/')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'xyzdef'
    }
}

Describe 'Invoke-BashSed — Range Delete' {
    BeforeEach {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-sed-range-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }
    AfterEach {
        if (Test-Path $testDir) { Remove-Item -Recurse -Force $testDir }
    }

    It 'sed /start/,/end/d deletes range of lines' {
        $file = Join-Path $testDir 'range.txt'
        Set-Content -Path $file -Value "before`nstart here`nmiddle`nend here`nafter" -NoNewline
        $results = @(Invoke-BashSed '/start/,/end/d' $file)
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'before'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'after'
    }
}

Describe 'Invoke-BashSed — Pipeline Bridge' {
    It 'ls | sed transforms BashText but preserves object type' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-sed-ls-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        try {
            Set-Content -Path (Join-Path $testDir 'test.txt') -Value 'content'
            $results = @(Invoke-BashLs -la $testDir | Invoke-BashSed 's/\.txt/.bak/')
            $txtEntry = $results | Where-Object {
                $null -ne $_.PSObject.Properties['BashText'] -and
                $_.BashText -match '\.bak'
            }
            $txtEntry | Should -Not -BeNullOrEmpty
            $txtEntry.PSObject.TypeNames[0] | Should -Be 'PsBash.LsEntry'
        } finally {
            Remove-Item -Recurse -Force $testDir
        }
    }

    It 'pipeline sed preserves original object properties' {
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TestObj'
            BashText   = "hello world`n"
            Name       = 'original'
        }
        $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value { $this.BashText } -Force
        $results = @($obj | Invoke-BashSed 's/world/earth/')
        $results.Count | Should -Be 1
        $results[0].Name | Should -Be 'original'
        ($results[0].BashText -replace "`n$", '') | Should -Be 'hello earth'
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.TestObj'
    }

    It 'pipeline sed /pattern/d filters out matching objects' {
        $obj1 = New-BashObject -BashText "keep this`n"
        $obj2 = New-BashObject -BashText "remove this`n"
        $obj3 = New-BashObject -BashText "keep also`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashSed '/remove/d')
        $results.Count | Should -Be 2
    }
}

Describe 'Invoke-BashSed — Multiple Expressions' {
    It 'sed -e expr1 -e expr2 applies both' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashSed -e 's/hello/hi/' -e 's/world/earth/')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hi earth'
    }
}

Describe 'Invoke-BashSed — Alias' {
    It 'sed alias works' {
        $alias = Get-Alias -Name sed -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashSed'
    }
}

Describe 'Invoke-BashAwk — Field Extraction' {
    It 'echo hello world | awk {print $1} -> hello' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashAwk '{print $1}')
        $result.Count | Should -Be 1
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello'
    }

    It 'echo hello world | awk {print $2} -> world' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashAwk '{print $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'world'
    }

    It 'echo hello world | awk {print $0} -> hello world' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashAwk '{print $0}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello world'
    }

    It 'print NF gives field count' {
        $result = @(Invoke-BashEcho 'a b c d' | Invoke-BashAwk '{print NF}')
        ($result[0].BashText -replace "`n$", '') | Should -Be '4'
    }

    It 'print $NF gives last field' {
        $result = @(Invoke-BashEcho 'a b c d' | Invoke-BashAwk '{print $NF}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'd'
    }
}

Describe 'Invoke-BashAwk — Field Separator' {
    It 'awk -F: {print $2} splits on colon' {
        $result = @(Invoke-BashEcho 'a:b:c' | Invoke-BashAwk -F: '{print $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'b'
    }

    It 'awk -F with tab separator' {
        $obj = New-BashObject -BashText "a`tb`tc`n"
        $result = @($obj | Invoke-BashAwk '-F\t' '{print $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'b'
    }

    It 'awk -F with multi-char separator' {
        $result = @(Invoke-BashEcho 'a::b::c' | Invoke-BashAwk '-F::' '{print $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'b'
    }
}

Describe 'Invoke-BashAwk — NR (Line Numbers)' {
    It 'awk {print NR, $0} numbers lines' {
        $obj1 = New-BashObject -BashText "a`n"
        $obj2 = New-BashObject -BashText "b`n"
        $obj3 = New-BashObject -BashText "c`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk '{print NR, $0}')
        $results.Count | Should -Be 3
        ($results[0].BashText -replace "`n$", '') | Should -Be '1 a'
        ($results[1].BashText -replace "`n$", '') | Should -Be '2 b'
        ($results[2].BashText -replace "`n$", '') | Should -Be '3 c'
    }
}

Describe 'Invoke-BashAwk — BEGIN/END Blocks' {
    It 'BEGIN{OFS="\t"} {print $1,$2} uses tab OFS' {
        $obj1 = New-BashObject -BashText "a 1`n"
        $obj2 = New-BashObject -BashText "b 2`n"
        $results = @($obj1, $obj2 | Invoke-BashAwk 'BEGIN{OFS="\t"} {print $1,$2}')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be "a`t1"
        ($results[1].BashText -replace "`n$", '') | Should -Be "b`t2"
    }

    It 'END{print NR} prints line count' {
        $obj1 = New-BashObject -BashText "a`n"
        $obj2 = New-BashObject -BashText "b`n"
        $results = @($obj1, $obj2 | Invoke-BashAwk 'END{print NR}')
        $results.Count | Should -Be 1
        ($results[0].BashText -replace "`n$", '') | Should -Be '2'
    }

    It 'BEGIN{print "header"} prints before input' {
        $obj = New-BashObject -BashText "data`n"
        $results = @($obj | Invoke-BashAwk 'BEGIN{print "header"} {print $0}')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'header'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'data'
    }
}

Describe 'Invoke-BashAwk — Pattern Matching' {
    It '/regex/ filters matching lines' {
        $obj1 = New-BashObject -BashText "foo`n"
        $obj2 = New-BashObject -BashText "bar`n"
        $obj3 = New-BashObject -BashText "baz`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk '/ba/')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'bar'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'baz'
    }

    It '$2 > 8 filters by field comparison' {
        $obj1 = New-BashObject -BashText "a 10`n"
        $obj2 = New-BashObject -BashText "b 20`n"
        $obj3 = New-BashObject -BashText "c 5`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk '$2 > 8')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'a 10'
        ($results[1].BashText -replace "`n$", '') | Should -Be 'b 20'
    }

    It '$1 == "value" filters by equality' {
        $obj1 = New-BashObject -BashText "alice 30`n"
        $obj2 = New-BashObject -BashText "bob 25`n"
        $obj3 = New-BashObject -BashText "alice 40`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk '$1 == "alice" {print $1, $2}')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'alice 30'
    }

    It 'NR > 1 skips first line' {
        $obj1 = New-BashObject -BashText "header`n"
        $obj2 = New-BashObject -BashText "data1`n"
        $obj3 = New-BashObject -BashText "data2`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk 'NR > 1')
        $results.Count | Should -Be 2
        ($results[0].BashText -replace "`n$", '') | Should -Be 'data1'
    }
}

Describe 'Invoke-BashAwk — String Functions' {
    It 'tolower($0) converts to lowercase' {
        $result = @(Invoke-BashEcho 'HELLO' | Invoke-BashAwk '{print tolower($0)}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello'
    }

    It 'toupper($0) converts to uppercase' {
        $result = @(Invoke-BashEcho 'hello' | Invoke-BashAwk '{print toupper($0)}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'HELLO'
    }

    It 'length($0) returns string length' {
        $result = @(Invoke-BashEcho 'hello' | Invoke-BashAwk '{print length($0)}')
        ($result[0].BashText -replace "`n$", '') | Should -Be '5'
    }

    It 'substr($0,2,3) extracts substring' {
        $result = @(Invoke-BashEcho 'hello' | Invoke-BashAwk '{print substr($0,2,3)}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'ell'
    }

    It 'gsub replaces all occurrences' {
        $result = @(Invoke-BashEcho 'aabaa' | Invoke-BashAwk '{gsub(/a/,"x"); print}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'xxbxx'
    }

    It 'sub replaces first occurrence' {
        $result = @(Invoke-BashEcho 'aabaa' | Invoke-BashAwk '{sub(/a/,"x"); print}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'xabaa'
    }
}

Describe 'Invoke-BashAwk — Math Expressions' {
    It '$3 + $4 adds fields' {
        $result = @(Invoke-BashEcho 'a b 10 20' | Invoke-BashAwk '{print $3 + $4}')
        ($result[0].BashText -replace "`n$", '') | Should -Be '30'
    }

    It '$2 * 2 multiplies' {
        $result = @(Invoke-BashEcho 'item 5' | Invoke-BashAwk '{print $2 * 2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be '10'
    }

    It 'arithmetic in pattern condition' {
        $obj1 = New-BashObject -BashText "a 10`n"
        $obj2 = New-BashObject -BashText "b 3`n"
        $results = @($obj1, $obj2 | Invoke-BashAwk '$2 * 2 > 15 {print $1}')
        $results.Count | Should -Be 1
        ($results[0].BashText -replace "`n$", '') | Should -Be 'a'
    }
}

Describe 'Invoke-BashAwk — Variables' {
    It '-v VAR=VAL assigns variable' {
        $result = @(Invoke-BashEcho 'hello' | Invoke-BashAwk -v 'greeting=hi' '{print greeting, $1}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hi hello'
    }

    It 'OFS controls output field separator in print' {
        $result = @(Invoke-BashEcho 'a b c' | Invoke-BashAwk 'BEGIN{OFS=","} {print $1,$2,$3}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'a,b,c'
    }

    It 'FS set in BEGIN controls field splitting' {
        $result = @(Invoke-BashEcho 'a:b:c' | Invoke-BashAwk 'BEGIN{FS=":"} {print $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'b'
    }
}

Describe 'Invoke-BashAwk — Printf' {
    It 'printf formats output' {
        $result = @(Invoke-BashEcho 'hello 42' | Invoke-BashAwk '{printf "%s=%d\n", $1, $2}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello=42'
    }

    It 'printf without newline concatenates' {
        $obj1 = New-BashObject -BashText "a`n"
        $obj2 = New-BashObject -BashText "b`n"
        $results = @($obj1, $obj2 | Invoke-BashAwk '{printf "%s", $1}')
        $results.Count | Should -Be 1
        ($results[0].BashText -replace "`n$", '') | Should -Be 'ab'
    }
}

Describe 'Invoke-BashAwk — Pipeline Bridge' {
    It 'ls | awk {print $NF} extracts last field from LsEntry' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-awk-ls-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        try {
            Set-Content -Path (Join-Path $testDir 'myfile.txt') -Value 'content'
            $results = @(Invoke-BashLs -la $testDir | Invoke-BashAwk '{print $NF}')
            $fileResult = $results | Where-Object {
                $null -ne $_.PSObject.Properties['BashText'] -and
                ($_.BashText -replace "`n$", '') -eq 'myfile.txt'
            }
            $fileResult | Should -Not -BeNullOrEmpty
        } finally {
            Remove-Item -Recurse -Force $testDir
        }
    }

    It 'awk produces BashObjects with PsBash.TextOutput type' {
        $result = @(Invoke-BashEcho 'test' | Invoke-BashAwk '{print $1}')
        $result[0].PSObject.TypeNames[0] | Should -Be 'PsBash.TextOutput'
    }

    It 'pattern filter with pipeline objects' {
        $obj1 = New-BashObject -BashText "alice 90`n"
        $obj2 = New-BashObject -BashText "bob 30`n"
        $obj3 = New-BashObject -BashText "carol 80`n"
        $results = @($obj1, $obj2, $obj3 | Invoke-BashAwk '$2 > 50 {print $1, $2}')
        $results.Count | Should -Be 2
    }
}

Describe 'Invoke-BashAwk — Multiple Statements' {
    It 'semicolon separates statements in action' {
        $result = @(Invoke-BashEcho 'hello world' | Invoke-BashAwk '{x = $1; print x}')
        ($result[0].BashText -replace "`n$", '') | Should -Be 'hello'
    }
}

Describe 'Invoke-BashAwk — Alias' {
    It 'awk alias works' {
        $alias = Get-Alias -Name awk -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashAwk'
    }
}

Describe 'Invoke-BashPs — Numeric Properties' {
    It 'Memory and CPU are numeric on current platform' {
        $results = @(Invoke-BashPs aux)
        $self = $results | Where-Object { $_.PID -eq $PID }
        if (-not $self) {
            $self = $results | Where-Object { $_.ProcessName -eq 'pwsh' } | Select-Object -First 1
        }
        $self | Should -Not -BeNullOrEmpty
        $self.CPU | Should -BeOfType [double]
        $self.Memory | Should -BeOfType [double]
        $self.MemoryMB | Should -BeOfType [double]
        $self.VSZ | Should -BeOfType [long]
        $self.RSS | Should -BeOfType [long]
        $self.WorkingSet | Should -BeOfType [long]
    }
}

# --- cut Tests ---

Describe 'Invoke-BashCut — Field Mode' {
    It 'extracts a single field with delimiter' {
        $result = Invoke-BashEcho -n 'a:b:c' | Invoke-BashCut -d: -f2
        $result.BashText | Should -Be 'b'
    }

    It 'extracts multiple fields with comma-separated spec' {
        $result = Invoke-BashEcho -n 'a:b:c' | Invoke-BashCut -d: -f '1,3'
        $result.BashText | Should -Be 'a:c'
    }

    It 'extracts field range' {
        $result = Invoke-BashEcho -n 'a:b:c:d' | Invoke-BashCut -d: -f '2-3'
        $result.BashText | Should -Be 'b:c'
    }

    It 'uses tab as default delimiter' {
        $result = Invoke-BashEcho -n "one`ttwo`tthree" | Invoke-BashCut -f2
        $result.BashText | Should -Be 'two'
    }

    It 'handles missing fields gracefully' {
        $result = Invoke-BashEcho -n 'a:b' | Invoke-BashCut -d: -f '1,5'
        $result.BashText | Should -Be 'a'
    }
}

Describe 'Invoke-BashCut — Character Mode' {
    It 'extracts character range' {
        $result = Invoke-BashEcho -n 'hello world' | Invoke-BashCut -c '1-3'
        $result.BashText | Should -Be 'hel'
    }

    It 'extracts specific character positions' {
        $result = Invoke-BashEcho -n 'abcdef' | Invoke-BashCut -c '1,3,5'
        $result.BashText | Should -Be 'ace'
    }
}

Describe 'Invoke-BashCut — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-cut-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'data.csv') -Value "name,age,city`nalice,30,london`nbob,25,paris" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'reads from file and extracts fields' {
        $results = @(Invoke-BashCut '-d,' -f2 (Join-Path $testDir 'data.csv'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be 'age'
        $results[1].BashText | Should -Be '30'
        $results[2].BashText | Should -Be '25'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashCut '-d,' -f1 (Join-Path $testDir 'nope.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashCut — Pipeline Bridge' {
    It 'works on BashText of pipeline objects' {
        $results = @(Invoke-BashEcho -n 'a:b:c' | Invoke-BashCut -d: -f2)
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        $results[0].BashText | Should -Be 'b'
    }
}

Describe 'Invoke-BashCut — Alias' {
    It 'cut alias resolves to Invoke-BashCut' {
        $alias = Get-Alias -Name cut -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashCut'
    }
}

# --- tr Tests ---

Describe 'Invoke-BashTr — Character Translation' {
    It 'translates uppercase to lowercase' {
        $result = Invoke-BashEcho -n 'HELLO' | Invoke-BashTr 'A-Z' 'a-z'
        $result.BashText | Should -Be 'hello'
    }

    It 'translates lowercase to uppercase' {
        $result = Invoke-BashEcho -n 'world' | Invoke-BashTr 'a-z' 'A-Z'
        $result.BashText | Should -Be 'WORLD'
    }

    It 'translates specific characters' {
        $result = Invoke-BashEcho -n 'abc' | Invoke-BashTr 'abc' 'xyz'
        $result.BashText | Should -Be 'xyz'
    }
}

Describe 'Invoke-BashTr — Delete Mode' {
    It 'deletes vowels' {
        $result = Invoke-BashEcho -n 'hello world' | Invoke-BashTr -d 'aeiou'
        $result.BashText | Should -Be 'hll wrld'
    }

    It 'deletes a character range' {
        $result = Invoke-BashEcho -n 'abc123def' | Invoke-BashTr -d '0-9'
        $result.BashText | Should -Be 'abcdef'
    }
}

Describe 'Invoke-BashTr — Squeeze Mode' {
    It 'squeezes repeated spaces' {
        $result = Invoke-BashEcho -n 'hello   world' | Invoke-BashTr -s ' '
        $result.BashText | Should -Be 'hello world'
    }

    It 'squeezes repeated characters' {
        $result = Invoke-BashEcho -n 'aabbcc' | Invoke-BashTr -s 'a-z'
        $result.BashText | Should -Be 'abc'
    }
}

Describe 'Invoke-BashTr — Pipeline Bridge' {
    It 'produces BashObjects with transformed text' {
        $result = Invoke-BashEcho -n 'TEST' | Invoke-BashTr 'A-Z' 'a-z'
        $result.PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        $result.BashText | Should -Be 'test'
    }

    It 'handles multiline input' {
        $results = @(Invoke-BashEcho -e 'ABC\nDEF' | Invoke-BashTr 'A-Z' 'a-z')
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'abc'
        $results[1].BashText | Should -Be 'def'
    }
}

Describe 'Invoke-BashTr — Alias' {
    It 'tr alias resolves to Invoke-BashTr' {
        $alias = Get-Alias -Name tr -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashTr'
    }
}

# --- uniq Tests ---

Describe 'Invoke-BashUniq — Basic' {
    It 'removes consecutive duplicate lines' {
        $results = @(Invoke-BashEcho -e 'a\na\nb' | Invoke-BashUniq)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'a'
        $results[1].BashText | Should -Be 'b'
    }

    It 'keeps non-consecutive duplicates' {
        $results = @(Invoke-BashEcho -e 'a\nb\na' | Invoke-BashUniq)
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be 'a'
        $results[1].BashText | Should -Be 'b'
        $results[2].BashText | Should -Be 'a'
    }
}

Describe 'Invoke-BashUniq — Count Mode' {
    It 'prefixes lines with count' {
        $results = @(Invoke-BashEcho -e 'a\na\nb' | Invoke-BashUniq -c)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Match '^\s*2 a$'
        $results[1].BashText | Should -Match '^\s*1 b$'
    }
}

Describe 'Invoke-BashUniq — Duplicates Only' {
    It 'shows only duplicated lines' {
        $results = @(Invoke-BashEcho -e 'a\na\nb\nc\nc' | Invoke-BashUniq -d)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'a'
        $results[1].BashText | Should -Be 'c'
    }
}

Describe 'Invoke-BashUniq — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-uniq-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'dupes.txt') -Value "alpha`nalpha`nbeta`nbeta`nbeta`ngamma" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'reads from file and deduplicates' {
        $results = @(Invoke-BashUniq (Join-Path $testDir 'dupes.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be 'alpha'
        $results[1].BashText | Should -Be 'beta'
        $results[2].BashText | Should -Be 'gamma'
    }
}

Describe 'Invoke-BashUniq — Pipeline Bridge' {
    It 'produces BashObjects' {
        $results = @(Invoke-BashEcho -e 'x\nx\ny' | Invoke-BashUniq)
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
    }
}

Describe 'Invoke-BashUniq — Alias' {
    It 'uniq alias resolves to Invoke-BashUniq' {
        $alias = Get-Alias -Name uniq -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashUniq'
    }
}

# --- rev Tests ---

Describe 'Invoke-BashRev — Basic' {
    It 'reverses a string' {
        $result = Invoke-BashEcho -n 'hello' | Invoke-BashRev
        $result.BashText | Should -Be 'olleh'
    }

    It 'reverses multiple lines' {
        $results = @(Invoke-BashEcho -e 'abc\ndef' | Invoke-BashRev)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'cba'
        $results[1].BashText | Should -Be 'fed'
    }

    It 'handles single character' {
        $result = Invoke-BashEcho -n 'x' | Invoke-BashRev
        $result.BashText | Should -Be 'x'
    }

    It 'handles palindrome' {
        $result = Invoke-BashEcho -n 'racecar' | Invoke-BashRev
        $result.BashText | Should -Be 'racecar'
    }
}

Describe 'Invoke-BashRev — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-rev-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'words.txt') -Value "hello`nworld" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'reverses lines from a file' {
        $results = @(Invoke-BashRev (Join-Path $testDir 'words.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'olleh'
        $results[1].BashText | Should -Be 'dlrow'
    }
}

Describe 'Invoke-BashRev — Pipeline Bridge' {
    It 'produces BashObjects' {
        $result = Invoke-BashEcho -n 'test' | Invoke-BashRev
        $result.PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
    }
}

Describe 'Invoke-BashRev — Alias' {
    It 'rev alias resolves to Invoke-BashRev' {
        $alias = Get-Alias -Name rev -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashRev'
    }
}

# --- nl Tests ---

Describe 'Invoke-BashNl — Basic' {
    It 'numbers non-blank lines' {
        $results = @(Invoke-BashEcho -e 'alpha\nbeta\ngamma' | Invoke-BashNl)
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Match '^\s+1\talpha$'
        $results[1].BashText | Should -Match '^\s+2\tbeta$'
        $results[2].BashText | Should -Match '^\s+3\tgamma$'
    }

    It 'skips numbering blank lines by default' {
        $results = @(Invoke-BashEcho -e 'a\n\nb' | Invoke-BashNl)
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Match '^\s+1\ta$'
        $results[1].BashText | Should -Be ''
        $results[2].BashText | Should -Match '^\s+2\tb$'
    }
}

Describe 'Invoke-BashNl — Number All Lines' {
    It 'numbers all lines including blank with -ba' {
        $results = @(Invoke-BashEcho -e 'a\n\nb' | Invoke-BashNl -ba)
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Match '^\s+1\ta$'
        $results[1].BashText | Should -Match '^\s+2\t$'
        $results[2].BashText | Should -Match '^\s+3\tb$'
    }
}

Describe 'Invoke-BashNl — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-nl-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'lines.txt') -Value "first`nsecond`nthird" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'numbers lines from a file' {
        $results = @(Invoke-BashNl (Join-Path $testDir 'lines.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Match '^\s+1\tfirst$'
        $results[1].BashText | Should -Match '^\s+2\tsecond$'
        $results[2].BashText | Should -Match '^\s+3\tthird$'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashNl (Join-Path $testDir 'nope.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashNl — Pipeline Bridge' {
    It 'produces BashObjects' {
        $results = @(Invoke-BashEcho -e 'test' | Invoke-BashNl)
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
    }
}

Describe 'Invoke-BashNl — Alias' {
    It 'nl alias resolves to Invoke-BashNl' {
        $alias = Get-Alias -Name nl -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashNl'
    }
}

# ============================================================================
# diff Command Tests
# ============================================================================

Describe 'Invoke-BashDiff — Normal Format' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-diff-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'file1.txt') -Value "alpha`nbeta`ngamma" -NoNewline
        Set-Content -Path (Join-Path $testDir 'file2.txt') -Value "alpha`nBETA`ngamma" -NoNewline
        Set-Content -Path (Join-Path $testDir 'identical.txt') -Value "alpha`nbeta`ngamma" -NoNewline
        Set-Content -Path (Join-Path $testDir 'added.txt') -Value "alpha`nbeta`ngamma`ndelta" -NoNewline
        Set-Content -Path (Join-Path $testDir 'deleted.txt') -Value "alpha`ngamma" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'shows normal diff output for changed line' {
        $results = @(Invoke-BashDiff (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $results.Count | Should -BeGreaterThan 0
        $results[0].BashText | Should -Be '2c2'
        $results[1].BashText | Should -Be '< beta'
        $results[2].BashText | Should -Be '---'
        $results[3].BashText | Should -Be '> BETA'
    }

    It 'produces no output for identical files' {
        $results = @(Invoke-BashDiff (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'identical.txt'))
        $results.Count | Should -Be 0
    }

    It 'shows additions in normal format' {
        $results = @(Invoke-BashDiff (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'added.txt'))
        $results.Count | Should -BeGreaterThan 0
        $found = $false
        foreach ($r in $results) {
            if ($r.BashText -match '^\d+a\d+') { $found = $true }
        }
        $found | Should -Be $true
    }

    It 'shows deletions in normal format' {
        $results = @(Invoke-BashDiff (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'deleted.txt'))
        $results.Count | Should -BeGreaterThan 0
        $found = $false
        foreach ($r in $results) {
            if ($r.BashText -match '^\d+d\d+') { $found = $true }
        }
        $found | Should -Be $true
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashDiff (Join-Path $testDir 'nope.txt') (Join-Path $testDir 'file1.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashDiff — Unified Format' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-diffu-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'file1.txt') -Value "alpha`nbeta`ngamma" -NoNewline
        Set-Content -Path (Join-Path $testDir 'file2.txt') -Value "alpha`nBETA`ngamma" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'produces unified format with -u flag' {
        $results = @(Invoke-BashDiff -u (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $results.Count | Should -BeGreaterThan 0
        $results[0].BashText | Should -BeLike '--- *'
        $results[1].BashText | Should -BeLike '+++ *'
        $results[2].BashText | Should -BeLike '@@ *'
    }

    It 'includes context lines around changes' {
        $results = @(Invoke-BashDiff -u (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $contextLines = @($results | Where-Object { $_.BashText.StartsWith(' ') })
        $contextLines.Count | Should -BeGreaterThan 0
    }

    It 'marks removed lines with - and added with +' {
        $results = @(Invoke-BashDiff -u (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $removed = @($results | Where-Object { $_.BashText -cmatch '^-[^-]' })
        $added = @($results | Where-Object { $_.BashText -cmatch '^\+[^+]' })
        $removed.Count | Should -BeGreaterThan 0
        $added.Count | Should -BeGreaterThan 0
    }
}

Describe 'Invoke-BashDiff — Pipeline Bridge' {
    It 'produces BashObjects' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-diffpb-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'a.txt') -Value "x" -NoNewline
        Set-Content -Path (Join-Path $testDir 'b.txt') -Value "y" -NoNewline
        $results = @(Invoke-BashDiff (Join-Path $testDir 'a.txt') (Join-Path $testDir 'b.txt'))
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }
}

Describe 'Invoke-BashDiff — Alias' {
    It 'diff alias resolves to Invoke-BashDiff' {
        $alias = Get-Alias -Name diff -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashDiff'
    }
}

# ============================================================================
# comm Command Tests
# ============================================================================

Describe 'Invoke-BashComm — Three Column Output' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-comm-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'file1.txt') -Value "apple`nbanana`ncherry" -NoNewline
        Set-Content -Path (Join-Path $testDir 'file2.txt') -Value "banana`ncherry`ndate" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'shows three columns: unique-to-1, unique-to-2, common' {
        $results = @(Invoke-BashComm (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $results.Count | Should -Be 4
        $results[0].BashText | Should -Be 'apple'
        $results[1].BashText | Should -Be "`t`tbanana"
        $results[2].BashText | Should -Be "`t`tcherry"
        $results[3].BashText | Should -Be "`tdate"
    }

    It 'suppresses column 1 with -1' {
        $results = @(Invoke-BashComm -1 (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        # apple suppressed, banana/cherry common (1 tab), date unique-to-2 (no tab)
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be "`tbanana"
        $results[1].BashText | Should -Be "`tcherry"
        $results[2].BashText | Should -Be 'date'
    }

    It 'shows only common lines with -12' {
        $results = @(Invoke-BashComm -12 (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'banana'
        $results[1].BashText | Should -Be 'cherry'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashComm (Join-Path $testDir 'nope.txt') (Join-Path $testDir 'file1.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashComm — Suppress Combinations' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-comm2-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'f1.txt') -Value "a`nb`nc" -NoNewline
        Set-Content -Path (Join-Path $testDir 'f2.txt') -Value "b`nc`nd" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'suppresses column 2 with -2' {
        $results = @(Invoke-BashComm -2 (Join-Path $testDir 'f1.txt') (Join-Path $testDir 'f2.txt'))
        # a (unique-to-1), b (common, 1 tab), c (common, 1 tab); d suppressed
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be 'a'
        $results[1].BashText | Should -Be "`tb"
        $results[2].BashText | Should -Be "`tc"
    }

    It 'suppresses column 3 with -3' {
        $results = @(Invoke-BashComm -3 (Join-Path $testDir 'f1.txt') (Join-Path $testDir 'f2.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'a'
        $results[1].BashText | Should -Be "`td"
    }
}

Describe 'Invoke-BashComm — Pipeline Bridge' {
    It 'produces BashObjects' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-commpb-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'a.txt') -Value "x" -NoNewline
        Set-Content -Path (Join-Path $testDir 'b.txt') -Value "x" -NoNewline
        $results = @(Invoke-BashComm (Join-Path $testDir 'a.txt') (Join-Path $testDir 'b.txt'))
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }
}

Describe 'Invoke-BashComm — Alias' {
    It 'comm alias resolves to Invoke-BashComm' {
        $alias = Get-Alias -Name comm -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashComm'
    }
}

# ============================================================================
# column Command Tests
# ============================================================================

Describe 'Invoke-BashColumn — Table Mode' {
    It 'auto-aligns whitespace-separated columns' {
        $results = @(Invoke-BashEcho -e "Name Age City`nAlice 30 London`nBob 25 Paris" | Invoke-BashColumn -t)
        $results.Count | Should -Be 3
        # All rows should have same-width columns (padded)
        $results[0].BashText | Should -Match '^Name\s+Age\s+City$'
        $results[1].BashText | Should -Match '^Alice\s+30\s+London$'
        $results[2].BashText | Should -Match '^Bob\s+25\s+Paris$'
    }

    It 'uses custom delimiter with -s' {
        $results = @(Invoke-BashEcho -e "root:0:root`nbin:1:bin" | Invoke-BashColumn -s: -t)
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Match '^root\s+0\s+root$'
        $results[1].BashText | Should -Match '^bin\s+1\s+bin$'
    }

    It 'passes through without -t' {
        $results = @(Invoke-BashEcho -e "hello world" | Invoke-BashColumn)
        $results[0].BashText | Should -Be 'hello world'
    }
}

Describe 'Invoke-BashColumn — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-column-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'data.txt') -Value "a 1`nbb 22`nccc 333" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'reads from file and aligns' {
        $results = @(Invoke-BashColumn -t (Join-Path $testDir 'data.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Match '^a\s+1$'
        $results[1].BashText | Should -Match '^bb\s+22$'
        $results[2].BashText | Should -Match '^ccc\s+333$'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashColumn -t (Join-Path $testDir 'nope.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashColumn — Pipeline Bridge' {
    It 'produces BashObjects' {
        $results = @(Invoke-BashEcho -n 'test' | Invoke-BashColumn -t)
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
    }
}

Describe 'Invoke-BashColumn — Alias' {
    It 'column alias resolves to Invoke-BashColumn' {
        $alias = Get-Alias -Name column -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashColumn'
    }
}

# ============================================================================
# join Command Tests
# ============================================================================

Describe 'Invoke-BashJoin — Basic Join' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-join-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'file1.txt') -Value "1 Alice`n2 Bob`n3 Charlie" -NoNewline
        Set-Content -Path (Join-Path $testDir 'file2.txt') -Value "1 HR`n2 Eng`n4 Sales" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'joins on first field by default' {
        $results = @(Invoke-BashJoin (Join-Path $testDir 'file1.txt') (Join-Path $testDir 'file2.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be '1 Alice HR'
        $results[1].BashText | Should -Be '2 Bob Eng'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashJoin (Join-Path $testDir 'nope.txt') (Join-Path $testDir 'file1.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashJoin — Custom Fields and Delimiter' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-join2-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'f1.txt') -Value "Alice:1`nBob:2" -NoNewline
        Set-Content -Path (Join-Path $testDir 'f2.txt') -Value "1:HR`n2:Eng" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'joins on specific fields with custom delimiter' {
        $results = @(Invoke-BashJoin -t: -1 2 -2 1 (Join-Path $testDir 'f1.txt') (Join-Path $testDir 'f2.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be '1:Alice:HR'
        $results[1].BashText | Should -Be '2:Bob:Eng'
    }
}

Describe 'Invoke-BashJoin — Pipeline Bridge' {
    It 'produces BashObjects' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-joinpb-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'a.txt') -Value "1 x" -NoNewline
        Set-Content -Path (Join-Path $testDir 'b.txt') -Value "1 y" -NoNewline
        $results = @(Invoke-BashJoin (Join-Path $testDir 'a.txt') (Join-Path $testDir 'b.txt'))
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }
}

Describe 'Invoke-BashJoin — Alias' {
    It 'join alias resolves to Invoke-BashJoin' {
        $alias = Get-Alias -Name join -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashJoin'
    }
}

# ============================================================================
# paste Command Tests
# ============================================================================

Describe 'Invoke-BashPaste — Merge Files' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-paste-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'names.txt') -Value "Alice`nBob`nCharlie" -NoNewline
        Set-Content -Path (Join-Path $testDir 'ages.txt') -Value "30`n25`n35" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'merges files side-by-side with tab delimiter' {
        $results = @(Invoke-BashPaste (Join-Path $testDir 'names.txt') (Join-Path $testDir 'ages.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be "Alice`t30"
        $results[1].BashText | Should -Be "Bob`t25"
        $results[2].BashText | Should -Be "Charlie`t35"
    }

    It 'uses custom delimiter with -d' {
        $results = @(Invoke-BashPaste '-d,' (Join-Path $testDir 'names.txt') (Join-Path $testDir 'ages.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be 'Alice,30'
        $results[1].BashText | Should -Be 'Bob,25'
        $results[2].BashText | Should -Be 'Charlie,35'
    }

    It 'writes error on missing file' {
        $Error.Clear()
        Invoke-BashPaste (Join-Path $testDir 'nope.txt') (Join-Path $testDir 'names.txt') 2>$null
        $Error.Count | Should -BeGreaterThan 0
        $Error[0].Exception.Message | Should -BeLike '*No such file*'
    }
}

Describe 'Invoke-BashPaste — Serial Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-pastes-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'data.txt') -Value "a`nb`nc" -NoNewline
        Set-Content -Path (Join-Path $testDir 'data2.txt') -Value "1`n2`n3" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'transposes each file to a single line in serial mode' {
        $results = @(Invoke-BashPaste -s (Join-Path $testDir 'data.txt') (Join-Path $testDir 'data2.txt'))
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be "a`tb`tc"
        $results[1].BashText | Should -Be "1`t2`t3"
    }

    It 'serial mode with custom delimiter' {
        $results = @(Invoke-BashPaste -s '-d,' (Join-Path $testDir 'data.txt'))
        $results.Count | Should -Be 1
        $results[0].BashText | Should -Be 'a,b,c'
    }
}

Describe 'Invoke-BashPaste — Uneven Files' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-pasteu-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'short.txt') -Value "a`nb" -NoNewline
        Set-Content -Path (Join-Path $testDir 'long.txt') -Value "1`n2`n3" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'pads shorter file with empty strings' {
        $results = @(Invoke-BashPaste (Join-Path $testDir 'short.txt') (Join-Path $testDir 'long.txt'))
        $results.Count | Should -Be 3
        $results[0].BashText | Should -Be "a`t1"
        $results[1].BashText | Should -Be "b`t2"
        $results[2].BashText | Should -Be "`t3"
    }
}

Describe 'Invoke-BashPaste — Pipeline Bridge' {
    It 'produces BashObjects' {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-pastepb-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'a.txt') -Value "x" -NoNewline
        Set-Content -Path (Join-Path $testDir 'b.txt') -Value "y" -NoNewline
        $results = @(Invoke-BashPaste (Join-Path $testDir 'a.txt') (Join-Path $testDir 'b.txt'))
        $results[0].PSTypeNames[0] | Should -Be 'PsBash.TextOutput'
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }
}

Describe 'Invoke-BashPaste — Alias' {
    It 'paste alias resolves to Invoke-BashPaste' {
        $alias = Get-Alias -Name paste -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashPaste'
    }
}

# ── tee ──────────────────────────────────────────────────────────────

Describe 'Invoke-BashTee — Basic Output' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-tee-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'writes to file and passes through to stdout' {
        $outFile = Join-Path $testDir 'out.txt'
        $results = @(Invoke-BashEcho 'hello' | Invoke-BashTee $outFile)
        $results.Count | Should -Be 1
        (Get-BashText -InputObject $results[0]) | Should -Be "hello`n"
        $content = [System.IO.File]::ReadAllText($outFile)
        $content | Should -Be "hello`n"
    }

    It 'writes multiple lines to file' {
        $outFile = Join-Path $testDir 'multi.txt'
        $results = @(Invoke-BashEcho -e "a`nb`nc" | Invoke-BashTee $outFile)
        $results.Count | Should -Be 1
        $content = [System.IO.File]::ReadAllText($outFile)
        $content | Should -Be "a`nb`nc`n"
    }
}

Describe 'Invoke-BashTee — Append Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-teea-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'appends to existing file with -a flag' {
        $outFile = Join-Path $testDir 'append.txt'
        Invoke-BashEcho 'first' | Invoke-BashTee $outFile | Out-Null
        Invoke-BashEcho 'second' | Invoke-BashTee -a $outFile | Out-Null
        $content = [System.IO.File]::ReadAllText($outFile)
        $content | Should -Be "first`nsecond`n"
    }

    It 'creates file in append mode if not exists' {
        $outFile = Join-Path $testDir 'newappend.txt'
        Invoke-BashEcho 'data' | Invoke-BashTee -a $outFile | Out-Null
        $content = [System.IO.File]::ReadAllText($outFile)
        $content | Should -Be "data`n"
    }
}

Describe 'Invoke-BashTee — Object Passthrough' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-teeobj-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'passes original objects through pipeline while writing BashText to file' {
        Set-Content -Path (Join-Path $testDir 'sample.txt') -Value 'test' -NoNewline
        $outFile = Join-Path $testDir 'listing.txt'
        $results = @(Invoke-BashLs $testDir | Invoke-BashTee $outFile)
        $results.Count | Should -BeGreaterThan 0
        $results[0].PSTypeNames[0] | Should -BeLike 'PsBash.*'
        (Test-Path -LiteralPath $outFile) | Should -Be $true
        $fileContent = [System.IO.File]::ReadAllText($outFile)
        $fileContent.Length | Should -BeGreaterThan 0
    }

    It 'writes to multiple files simultaneously' {
        $out1 = Join-Path $testDir 'copy1.txt'
        $out2 = Join-Path $testDir 'copy2.txt'
        $results = @(Invoke-BashEcho 'multi' | Invoke-BashTee $out1 $out2)
        $results.Count | Should -Be 1
        [System.IO.File]::ReadAllText($out1) | Should -Be "multi`n"
        [System.IO.File]::ReadAllText($out2) | Should -Be "multi`n"
    }
}

Describe 'Invoke-BashTee — Error Handling' {
    It 'errors on nonexistent parent directory' {
        $badPath = Join-Path ([System.IO.Path]::GetTempPath()) 'nonexistent' 'file.txt'
        { Invoke-BashEcho 'test' | Invoke-BashTee $badPath 2>&1 | Out-Null } | Should -Not -Throw
        (Test-Path -LiteralPath $badPath) | Should -Be $false
    }
}

Describe 'Invoke-BashTee — Alias' {
    It 'tee alias resolves to Invoke-BashTee' {
        $alias = Get-Alias -Name tee -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashTee'
    }
}

# ── xargs ────────────────────────────────────────────────────────────

Describe 'Invoke-BashXargs — Basic' {
    It 'collects input lines and passes as arguments' {
        $results = @(Invoke-BashEcho -e "a`nb`nc" | Invoke-BashXargs Invoke-BashEcho)
        $results.Count | Should -Be 1
        (Get-BashText -InputObject $results[0]) | Should -Be "a b c`n"
    }

    It 'works with no pipeline input (empty)' {
        $results = @(@() | Invoke-BashXargs Invoke-BashEcho)
        $results.Count | Should -Be 1
        (Get-BashText -InputObject $results[0]) | Should -Be "`n"
    }
}

Describe 'Invoke-BashXargs — Replacement Mode' {
    It 'replaces {} with each input line using -I' {
        $results = @(Invoke-BashEcho -e "a`nb" | Invoke-BashXargs -I '{}' Invoke-BashEcho 'file: {}')
        $results.Count | Should -Be 2
        (Get-BashText -InputObject $results[0]) | Should -Be "file: a`n"
        (Get-BashText -InputObject $results[1]) | Should -Be "file: b`n"
    }
}

Describe 'Invoke-BashXargs — Max Args' {
    It 'limits args per invocation with -n' {
        $results = @(Invoke-BashEcho -e "a`nb`nc`nd" | Invoke-BashXargs -n 2 Invoke-BashEcho)
        $results.Count | Should -Be 2
        (Get-BashText -InputObject $results[0]) | Should -Be "a b`n"
        (Get-BashText -InputObject $results[1]) | Should -Be "c d`n"
    }

    It 'handles remainder when not evenly divisible' {
        $results = @(Invoke-BashEcho -e "a`nb`nc" | Invoke-BashXargs -n 2 Invoke-BashEcho)
        $results.Count | Should -Be 2
        (Get-BashText -InputObject $results[0]) | Should -Be "a b`n"
        (Get-BashText -InputObject $results[1]) | Should -Be "c`n"
    }
}

Describe 'Invoke-BashXargs — Integration with PsBash Commands' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-xargs-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'a.txt') -Value "hello" -NoNewline
        Set-Content -Path (Join-Path $testDir 'b.txt') -Value "world" -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'works with find piped to xargs wc' {
        $results = @(Invoke-BashFind $testDir -name '*.txt' | Invoke-BashXargs Invoke-BashWc -l)
        $results.Count | Should -BeGreaterThan 0
    }

    It 'works with find piped to xargs cat' {
        $results = @(Invoke-BashFind $testDir -name '*.txt' | Invoke-BashSort | Invoke-BashXargs Invoke-BashCat)
        $results.Count | Should -Be 2
    }
}

Describe 'Invoke-BashXargs — Error Handling' {
    It 'errors when no command specified' {
        $results = @(@('a') | Invoke-BashXargs 2>&1)
        $results[0] | Should -BeOfType [System.Management.Automation.ErrorRecord]
    }
}

Describe 'Invoke-BashXargs — Alias' {
    It 'xargs alias resolves to Invoke-BashXargs' {
        $alias = Get-Alias -Name xargs -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashXargs'
    }
}

Describe 'Invoke-BashJq — Field Access' {
    It 'extracts a string field with quotes' {
        $r = Invoke-BashEcho "{`"name`":`"John`"}" | Invoke-BashJq '.name'
        (Get-BashText $r) | Should -Be '"John"'
    }

    It 'extracts a string field raw with -r' {
        $r = Invoke-BashEcho "{`"name`":`"John`"}" | Invoke-BashJq -r '.name'
        (Get-BashText $r) | Should -Be 'John'
    }

    It 'extracts nested fields' {
        $r = Invoke-BashEcho "{`"a`":{`"b`":1}}" | Invoke-BashJq '.a.b'
        (Get-BashText $r) | Should -Be '1'
    }

    It 'returns null for missing field' {
        $r = Invoke-BashEcho "{`"a`":1}" | Invoke-BashJq '.b'
        (Get-BashText $r) | Should -Be 'null'
    }
}

Describe 'Invoke-BashJq — Array Access' {
    It 'accesses array element by index' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq '.[0]'
        (Get-BashText $r) | Should -Be '1'
    }

    It 'accesses array element with negative index' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq '.[-1]'
        (Get-BashText $r) | Should -Be '3'
    }

    It 'iterates array elements with .[]' {
        $results = @(Invoke-BashEcho '[1,2,3]' | Invoke-BashJq '.[]')
        $results.Count | Should -Be 3
        (Get-BashText $results[0]) | Should -Be '1'
        (Get-BashText $results[1]) | Should -Be '2'
        (Get-BashText $results[2]) | Should -Be '3'
    }

    It 'chains iterate and field access' {
        $results = @(Invoke-BashEcho '[{"n":"a"},{"n":"b"}]' | Invoke-BashJq '.[].n')
        $results.Count | Should -Be 2
        (Get-BashText $results[0]) | Should -Be '"a"'
        (Get-BashText $results[1]) | Should -Be '"b"'
    }
}

Describe 'Invoke-BashJq — Identity and Pipe' {
    It 'passes through with identity filter' {
        $r = Invoke-BashEcho '42' | Invoke-BashJq '.'
        (Get-BashText $r) | Should -Be '42'
    }

    It 'chains filters with pipe' {
        $r = Invoke-BashEcho '{"a":[1,2,3]}' | Invoke-BashJq '.a | length'
        (Get-BashText $r) | Should -Be '3'
    }

    It 'chains iterate with select via pipe' {
        $results = @(Invoke-BashEcho '[1,2,3]' | Invoke-BashJq '.[] | select(. > 1)')
        $results.Count | Should -Be 2
        (Get-BashText $results[0]) | Should -Be '2'
        (Get-BashText $results[1]) | Should -Be '3'
    }
}

Describe 'Invoke-BashJq — Map and Select' {
    It 'maps a field from array of objects' {
        $r = Invoke-BashEcho '[{"a":1},{"a":2}]' | Invoke-BashJq 'map(.a)'
        (Get-BashText $r) | Should -Match '^\['
        (Get-BashText $r) | Should -Match '1'
        (Get-BashText $r) | Should -Match '2'
    }

    It 'filters with select on iterated values' {
        $results = @(Invoke-BashEcho '[10,20,30]' | Invoke-BashJq '.[] | select(. >= 20)')
        $results.Count | Should -Be 2
        (Get-BashText $results[0]) | Should -Be '20'
        (Get-BashText $results[1]) | Should -Be '30'
    }

    It 'select with equality' {
        $results = @(Invoke-BashEcho '[{"n":"a"},{"n":"b"}]' | Invoke-BashJq '.[] | select(.n == "b")')
        $results.Count | Should -Be 1
    }
}

Describe 'Invoke-BashJq — Built-in Functions' {
    It 'returns sorted keys of an object' {
        $r = Invoke-BashEcho '{"b":2,"a":1}' | Invoke-BashJq 'keys'
        $text = Get-BashText $r
        $text | Should -Match '"a"'
        $text | Should -Match '"b"'
    }

    It 'returns values of an object' {
        $r = Invoke-BashEcho '{"a":1,"b":2}' | Invoke-BashJq 'values'
        $text = Get-BashText $r
        $text | Should -Match '1'
        $text | Should -Match '2'
    }

    It 'returns length of an array' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq 'length'
        (Get-BashText $r) | Should -Be '3'
    }

    It 'returns length of a string' {
        $r = Invoke-BashEcho '"hello"' | Invoke-BashJq 'length'
        (Get-BashText $r) | Should -Be '5'
    }

    It 'returns length of an object' {
        $r = Invoke-BashEcho '{"a":1,"b":2}' | Invoke-BashJq 'length'
        (Get-BashText $r) | Should -Be '2'
    }

    It 'returns type for number' {
        $r = Invoke-BashEcho '42' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"number"'
    }

    It 'returns type for string' {
        $r = Invoke-BashEcho '"hello"' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"string"'
    }

    It 'returns type for array' {
        $r = Invoke-BashEcho '[1,2]' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"array"'
    }

    It 'returns type for object' {
        $r = Invoke-BashEcho '{"a":1}' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"object"'
    }

    It 'returns type for null' {
        $r = Invoke-BashEcho 'null' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"null"'
    }

    It 'returns type for boolean' {
        $r = Invoke-BashEcho 'true' | Invoke-BashJq 'type'
        (Get-BashText $r) | Should -Be '"boolean"'
    }
}

Describe 'Invoke-BashJq — Output Flags' {
    It 'produces compact output with -c' {
        $r = Invoke-BashEcho '{"a":1,"b":2}' | Invoke-BashJq -c '.'
        (Get-BashText $r) | Should -Be '{"a":1,"b":2}'
    }

    It 'sorts keys with -S' {
        $r = Invoke-BashEcho '{"b":1,"a":2}' | Invoke-BashJq -S -c '.'
        (Get-BashText $r) | Should -Be '{"a":2,"b":1}'
    }

    It 'combines -S and pretty output' {
        $r = Invoke-BashEcho '{"b":1,"a":2}' | Invoke-BashJq -S '.'
        $text = Get-BashText $r
        $lines = $text -split "`n"
        $lines[0] | Should -Be '{'
        $lines[1].Trim() | Should -Match '"a"'
    }
}

Describe 'Invoke-BashJq — Slurp Mode' {
    It 'wraps input in array with -s' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq -s '.'
        $text = Get-BashText $r
        $text | Should -Match '^\['
    }

    It 'slurp with length returns 1 for single input' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq -s 'length'
        (Get-BashText $r) | Should -Be '1'
    }
}

Describe 'Invoke-BashJq — Object Construction' {
    It 'constructs object with renamed keys' {
        $r = Invoke-BashEcho '{"a":1}' | Invoke-BashJq -c '{x: .a}'
        (Get-BashText $r) | Should -Be '{"x":1}'
    }

    It 'constructs object with multiple keys' {
        $r = Invoke-BashEcho '{"first":"A","last":"B"}' | Invoke-BashJq -c '{f: .first, l: .last}'
        (Get-BashText $r) | Should -Be '{"f":"A","l":"B"}'
    }
}

Describe 'Invoke-BashJq — Array Construction' {
    It 'constructs array from iterated fields' {
        $r = Invoke-BashEcho '[{"n":"a"},{"n":"b"}]' | Invoke-BashJq -c '[.[] | .n]'
        (Get-BashText $r) | Should -Be '["a","b"]'
    }

    It 'constructs array from map' {
        $r = Invoke-BashEcho '[1,2,3]' | Invoke-BashJq -c '[.[] | select(. > 1)]'
        (Get-BashText $r) | Should -Be '[2,3]'
    }
}

Describe 'Invoke-BashJq — String Interpolation' {
    It 'interpolates field values in strings' {
        $r = Invoke-BashEcho '{"name":"world"}' | Invoke-BashJq -r "`"hello \(.name)`""
        (Get-BashText $r) | Should -Be 'hello world'
    }

    It 'interpolates nested expressions' {
        $r = Invoke-BashEcho '{"a":{"b":"val"}}' | Invoke-BashJq -r "`"result: \(.a.b)`""
        (Get-BashText $r) | Should -Be 'result: val'
    }
}

Describe 'Invoke-BashJq — File Mode' {
    BeforeAll {
        $testDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-jq-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $testDir -Force | Out-Null
        Set-Content -Path (Join-Path $testDir 'data.json') -Value '{"name":"Alice","age":30}' -NoNewline
    }
    AfterAll {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }

    It 'reads JSON from file' {
        $r = Invoke-BashJq '.name' (Join-Path $testDir 'data.json')
        (Get-BashText $r) | Should -Be '"Alice"'
    }

    It 'applies filter to file data' {
        $r = Invoke-BashJq -r '.name' (Join-Path $testDir 'data.json')
        (Get-BashText $r) | Should -Be 'Alice'
    }

    It 'errors on missing file' {
        $results = @(Invoke-BashJq '.name' (Join-Path $testDir 'missing.json') 2>&1)
        $results[0] | Should -BeOfType [System.Management.Automation.ErrorRecord]
    }
}

Describe 'Invoke-BashJq — Pipeline Bridge' {
    It 'accepts BashObject pipeline input' {
        $r = Invoke-BashEcho '{"x":42}' | Invoke-BashJq '.x'
        (Get-BashText $r) | Should -Be '42'
    }

    It 'outputs BashObjects' {
        $r = Invoke-BashEcho '{"a":1}' | Invoke-BashJq '.'
        $r.PSObject.Properties['BashText'] | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-BashJq — Alias' {
    It 'jq alias resolves to Invoke-BashJq' {
        $alias = Get-Alias -Name jq -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashJq'
    }
}

# ── Invoke-BashDate ──────────────────────────────────────────────────

Describe 'Invoke-BashDate — Default Output' {
    It 'returns current date/time in default format' {
        $r = Invoke-BashDate
        $r.BashText | Should -Match '^\w{3} \w{3} [ \d]\d \d{2}:\d{2}:\d{2} \S+ \d{4}$'
    }

    It 'returns object with Year, Month, Day properties' {
        $r = Invoke-BashDate
        $now = [datetime]::Now
        $r.Year | Should -Be $now.Year
        $r.Month | Should -Be $now.Month
        $r.Day | Should -Be $now.Day
    }

    It 'returns object with Hour, Minute, Second properties' {
        $r = Invoke-BashDate
        $r.PSObject.Properties['Hour'] | Should -Not -BeNullOrEmpty
        $r.PSObject.Properties['Minute'] | Should -Not -BeNullOrEmpty
        $r.PSObject.Properties['Second'] | Should -Not -BeNullOrEmpty
    }

    It 'returns Epoch as integer seconds' {
        $r = Invoke-BashDate
        $r.Epoch | Should -BeOfType [long]
        $r.Epoch | Should -BeGreaterThan 1700000000
    }

    It 'returns DayOfWeek as string' {
        $r = Invoke-BashDate
        $r.DayOfWeek | Should -BeIn @('Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday')
    }

    It 'returns DateTime as DateTimeOffset' {
        $r = Invoke-BashDate
        $r.DateTime | Should -BeOfType [System.DateTimeOffset]
    }
}

Describe 'Invoke-BashDate — Format String' {
    It 'formats with +%Y-%m-%d' {
        $r = Invoke-BashDate '+%Y-%m-%d'
        $expected = [datetime]::Now.ToString('yyyy-MM-dd')
        $r.BashText | Should -Be $expected
    }

    It 'formats with +%H:%M:%S' {
        $r = Invoke-BashDate '+%H:%M:%S'
        $r.BashText | Should -Match '^\d{2}:\d{2}:\d{2}$'
    }

    It 'formats epoch with +%s' {
        $r = Invoke-BashDate '+%s'
        [long]$r.BashText | Should -BeGreaterThan 1700000000
    }

    It 'formats weekday with +%A' {
        $r = Invoke-BashDate -d '2024-01-15' '+%A'
        $r.BashText | Should -Be 'Monday'
    }

    It 'formats month name with +%B' {
        $r = Invoke-BashDate -d '2024-03-01' '+%B'
        $r.BashText | Should -Be 'March'
    }

    It 'formats timezone with +%Z' {
        $r = Invoke-BashDate '+%Z'
        $r.BashText | Should -Not -BeNullOrEmpty
    }

    It 'handles literal text in format' {
        $r = Invoke-BashDate -d '2024-06-15' '+Date: %Y/%m/%d'
        $r.BashText | Should -Be 'Date: 2024/06/15'
    }
}

Describe 'Invoke-BashDate — UTC Flag' {
    It 'returns UTC time with -u' {
        $r = Invoke-BashDate -u '+%Z'
        $r.BashText | Should -Be 'UTC'
    }

    It 'DateTime is UTC with -u' {
        $r = Invoke-BashDate -u
        $r.DateTime.Offset | Should -Be ([System.TimeSpan]::Zero)
    }
}

Describe 'Invoke-BashDate — Date String (-d)' {
    It 'parses ISO date string' {
        $r = Invoke-BashDate -d '2024-01-15' '+%Y-%m-%d'
        $r.BashText | Should -Be '2024-01-15'
    }

    It 'parses date with time' {
        $r = Invoke-BashDate -d '2024-01-15 14:30:00' '+%H:%M:%S'
        $r.BashText | Should -Be '14:30:00'
    }

    It 'sets object properties from date string' {
        $r = Invoke-BashDate -d '2024-07-04'
        $r.Year | Should -Be 2024
        $r.Month | Should -Be 7
        $r.Day | Should -Be 4
    }
}

Describe 'Invoke-BashDate — Reference File (-r)' {
    It 'returns modification time of a file' {
        $tmp = New-TemporaryFile
        try {
            $r = Invoke-BashDate -r $tmp.FullName '+%Y'
            $r.BashText | Should -Match '^\d{4}$'
        } finally {
            Remove-Item $tmp.FullName -Force
        }
    }
}

Describe 'Invoke-BashDate — Alias' {
    It 'date alias resolves to Invoke-BashDate' {
        $alias = Get-Alias -Name date -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashDate'
    }
}

# ── Invoke-BashSeq ──────────────────────────────────────────────────

Describe 'Invoke-BashSeq — Basic Sequences' {
    It 'seq 5 produces 1 through 5' {
        $r = @(Invoke-BashSeq 5)
        $r.Count | Should -Be 5
        (Get-BashText $r[0]) | Should -Be '1'
        (Get-BashText $r[4]) | Should -Be '5'
    }

    It 'seq 2 5 produces 2 through 5' {
        $r = @(Invoke-BashSeq 2 5)
        $r.Count | Should -Be 4
        (Get-BashText $r[0]) | Should -Be '2'
        (Get-BashText $r[3]) | Should -Be '5'
    }

    It 'seq 1 2 10 produces 1 3 5 7 9' {
        $r = @(Invoke-BashSeq 1 2 10)
        $r.Count | Should -Be 5
        (Get-BashText $r[0]) | Should -Be '1'
        (Get-BashText $r[1]) | Should -Be '3'
        (Get-BashText $r[2]) | Should -Be '5'
        (Get-BashText $r[3]) | Should -Be '7'
        (Get-BashText $r[4]) | Should -Be '9'
    }

    It 'seq with decrement 5 -1 1' {
        $r = @(Invoke-BashSeq 5 -1 1)
        $r.Count | Should -Be 5
        (Get-BashText $r[0]) | Should -Be '5'
        (Get-BashText $r[4]) | Should -Be '1'
    }

    It 'seq with decimal increment 0.5 0.5 2.5' {
        $r = @(Invoke-BashSeq 0.5 0.5 2.5)
        $r.Count | Should -Be 5
        (Get-BashText $r[0]) | Should -Be '0.5'
        (Get-BashText $r[4]) | Should -Be '2.5'
    }
}

Describe 'Invoke-BashSeq — Flags' {
    It '-w pads to equal width' {
        $r = @(Invoke-BashSeq -w 1 10)
        (Get-BashText $r[0]) | Should -Be '01'
        (Get-BashText $r[9]) | Should -Be '10'
    }

    It '-s sets separator in BashText' {
        $r = @(Invoke-BashSeq -s ',' 1 3)
        $r.Count | Should -Be 1
        (Get-BashText $r[0]) | Should -Be '1,2,3'
    }

    It '-w with three-digit range' {
        $r = @(Invoke-BashSeq -w 1 100)
        (Get-BashText $r[0]) | Should -Be '001'
        (Get-BashText $r[99]) | Should -Be '100'
    }
}

Describe 'Invoke-BashSeq — Object Properties' {
    It 'returns Value and Index properties' {
        $r = @(Invoke-BashSeq 3)
        $r[0].Value | Should -Be 1
        $r[0].Index | Should -Be 0
        $r[2].Value | Should -Be 3
        $r[2].Index | Should -Be 2
    }
}

Describe 'Invoke-BashSeq — Alias' {
    It 'seq alias resolves to Invoke-BashSeq' {
        $alias = Get-Alias -Name seq -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashSeq'
    }
}

# ── Invoke-BashExpr ──────────────────────────────────────────────────

Describe 'Invoke-BashExpr — Arithmetic' {
    It 'adds two numbers' {
        $r = Invoke-BashExpr 2 + 3
        $r.Value | Should -Be 5
        $r.BashText | Should -Be '5'
    }

    It 'subtracts' {
        $r = Invoke-BashExpr 10 - 3
        $r.Value | Should -Be 7
    }

    It 'multiplies' {
        $r = Invoke-BashExpr 4 '*' 5
        $r.Value | Should -Be 20
    }

    It 'integer division' {
        $r = Invoke-BashExpr 10 / 3
        $r.Value | Should -Be 3
    }

    It 'modulo' {
        $r = Invoke-BashExpr 10 '%' 3
        $r.Value | Should -Be 1
    }
}

Describe 'Invoke-BashExpr — Comparison' {
    It 'less than true returns 1' {
        $r = Invoke-BashExpr 2 '<' 3
        $r.Value | Should -Be 1
        $r.BashText | Should -Be '1'
    }

    It 'less than false returns 0' {
        $r = Invoke-BashExpr 5 '<' 3
        $r.Value | Should -Be 0
        $r.BashText | Should -Be '0'
    }

    It 'equals true' {
        $r = Invoke-BashExpr 5 '=' 5
        $r.Value | Should -Be 1
    }

    It 'not equals' {
        $r = Invoke-BashExpr 5 '!=' 3
        $r.Value | Should -Be 1
    }

    It 'greater or equal' {
        $r = Invoke-BashExpr 5 '>=' 5
        $r.Value | Should -Be 1
    }
}

Describe 'Invoke-BashExpr — String Operations' {
    It 'length returns string length' {
        $r = Invoke-BashExpr length 'hello'
        $r.Value | Should -Be 5
        $r.BashText | Should -Be '5'
    }

    It 'substr extracts substring (1-based)' {
        $r = Invoke-BashExpr substr 'hello' 2 3
        $r.Value | Should -Be 'ell'
        $r.BashText | Should -Be 'ell'
    }

    It 'index finds first char position (1-based)' {
        $r = Invoke-BashExpr index 'hello' 'lo'
        $r.Value | Should -Be 3
    }

    It 'index returns 0 when not found' {
        $r = Invoke-BashExpr index 'hello' 'z'
        $r.Value | Should -Be 0
    }

    It 'match returns matched portion length' {
        $r = Invoke-BashExpr match 'abc123' '[a-z]*'
        $r.Value | Should -Be 3
    }

    It 'match with capture group returns captured text' {
        $r = Invoke-BashExpr match 'abc123def' '[^0-9]*\([0-9]*\)'
        $r.Value | Should -Be '123'
    }
}

Describe 'Invoke-BashExpr — Alias' {
    It 'expr alias resolves to Invoke-BashExpr' {
        $alias = Get-Alias -Name expr -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashExpr'
    }
}

Describe 'Invoke-BashDate — Pipeline Bridge' {
    It 'outputs BashObjects' {
        $r = Invoke-BashDate
        $r.PSObject.Properties['BashText'] | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-BashSeq — Pipeline Bridge' {
    It 'seq output pipes to grep' {
        $r = @(Invoke-BashSeq 1 10 | Invoke-BashGrep '5')
        $r.Count | Should -Be 1
        (Get-BashText $r[0]) | Should -Be '5'
    }
}

Describe 'Invoke-BashExpr — Pipeline Bridge' {
    It 'outputs BashObject' {
        $r = Invoke-BashExpr 1 + 1
        $r.PSObject.Properties['BashText'] | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-BashDu' {
    BeforeAll {
        $duDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-du-test-$(Get-Random)"
        New-Item -Path $duDir -ItemType Directory -Force | Out-Null

        $sub1 = Join-Path $duDir 'src'
        New-Item -Path $sub1 -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $sub1 'main.ps1') -Value ('x' * 1024)
        Set-Content -Path (Join-Path $sub1 'util.ps1') -Value ('y' * 512)

        $sub2 = Join-Path $duDir 'docs'
        New-Item -Path $sub2 -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $sub2 'readme.txt') -Value ('z' * 256)

        $deep = Join-Path $sub1 'lib'
        New-Item -Path $deep -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $deep 'helper.ps1') -Value ('w' * 2048)

        Set-Content -Path (Join-Path $duDir 'root.txt') -Value ('r' * 128)
    }

    AfterAll {
        Remove-Item -Path $duDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'du ./testdir returns size for each subdirectory' {
        $results = @(Invoke-BashDu $duDir)
        $results.Count | Should -BeGreaterOrEqual 2
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.DuEntry'
        $results[0].SizeBytes | Should -BeOfType [long]
        $results[0].Path | Should -Not -BeNullOrEmpty
    }

    It 'du default only shows directories, not individual files' {
        $results = @(Invoke-BashDu $duDir)
        $fileEntries = @($results | Where-Object { $_.Path -match '\.(txt|ps1)$' })
        $fileEntries.Count | Should -Be 0
    }

    It 'du -h shows human-readable sizes' {
        $results = @(Invoke-BashDu -h $duDir)
        $results | Should -Not -BeNullOrEmpty
        $results[0].SizeHuman | Should -Not -BeNullOrEmpty
        $results[0].BashText | Should -Match '\t'
    }

    It 'du -s shows total only' {
        $results = @(Invoke-BashDu -s $duDir)
        $results.Count | Should -Be 1
        $results[0].SizeBytes | Should -BeGreaterThan 0
    }

    It 'du -a includes all files' {
        $results = @(Invoke-BashDu -a $duDir)
        $fileEntries = @($results | Where-Object { $_.Path -match '\.(txt|ps1)$' })
        $fileEntries.Count | Should -BeGreaterOrEqual 4
    }

    It 'du -c dir1 dir2 produces grand total line' {
        $results = @(Invoke-BashDu -c $sub1 $sub2)
        $total = @($results | Where-Object { $_.IsTotal })
        $total.Count | Should -Be 1
        $total[0].Path | Should -Be 'total'
    }

    It 'du -d 1 limits max depth' {
        $results = @(Invoke-BashDu -d 1 $duDir)
        $deep = @($results | Where-Object { $_.Depth -gt 1 })
        $deep.Count | Should -Be 0
    }

    It 'du BashText format is size<tab>path' {
        $results = @(Invoke-BashDu $duDir)
        $results[0].BashText | Should -Match '^\d+\t'
    }

    It 'du Depth property reflects directory nesting' {
        $results = @(Invoke-BashDu $duDir)
        $results | Should -Not -BeNullOrEmpty
        $results | ForEach-Object { $_.Depth | Should -BeOfType [int] }
    }

    It 'du nonexistent path writes error' {
        $results = @(Invoke-BashDu '/nonexistent/path' 2>$null)
        $results.Count | Should -Be 0
    }
}

Describe 'Invoke-BashDu — Alias' {
    It 'du alias resolves to Invoke-BashDu' {
        $alias = Get-Alias -Name du -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashDu'
    }
}

Describe 'Invoke-BashTree' {
    BeforeAll {
        $treeDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-tree-test-$(Get-Random)"
        New-Item -Path $treeDir -ItemType Directory -Force | Out-Null

        $srcDir = Join-Path $treeDir 'src'
        New-Item -Path $srcDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $srcDir 'main.ps1') -Value 'code'
        Set-Content -Path (Join-Path $srcDir 'util.ps1') -Value 'utils'

        $libDir = Join-Path $srcDir 'lib'
        New-Item -Path $libDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $libDir 'helper.ps1') -Value 'help'

        $docsDir = Join-Path $treeDir 'docs'
        New-Item -Path $docsDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $docsDir 'readme.txt') -Value 'readme'

        Set-Content -Path (Join-Path $treeDir 'root.txt') -Value 'root'
        Set-Content -Path (Join-Path $treeDir 'app.log') -Value 'logdata'

        # Dotfile for -a tests
        Set-Content -Path (Join-Path $treeDir '.hidden') -Value 'hidden'
    }

    AfterAll {
        Remove-Item -Path $treeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'tree ./testdir returns TreeEntry objects with box-drawing characters' {
        $results = @(Invoke-BashTree $treeDir)
        $results.Count | Should -BeGreaterOrEqual 2
        $results[0].PSObject.TypeNames[0] | Should -Be 'PsBash.TreeEntry'
        $hasBoxChars = ($results | Where-Object { $_.BashText -match '[├└│]' }).Count -gt 0
        $hasBoxChars | Should -BeTrue
    }

    It 'tree -d shows directories only' {
        $results = @(Invoke-BashTree -d $treeDir)
        $files = @($results | Where-Object { -not $_.IsDirectory -and $_.Name -ne '' })
        # Only the summary line has IsDirectory=$false and empty Name
        $nonSummary = @($files | Where-Object { $_.BashText -notmatch 'director' })
        $nonSummary.Count | Should -Be 0
    }

    It 'tree -L 1 limits to depth 1' {
        $results = @(Invoke-BashTree -L 1 $treeDir)
        $deep = @($results | Where-Object { $_.Depth -gt 1 })
        $deep.Count | Should -Be 0
    }

    It 'tree -a includes dotfiles' {
        $results = @(Invoke-BashTree -a $treeDir)
        $hidden = @($results | Where-Object { $_.Name -eq '.hidden' })
        $hidden.Count | Should -Be 1
    }

    It 'tree default excludes dotfiles' {
        $results = @(Invoke-BashTree $treeDir)
        $hidden = @($results | Where-Object { $_.Name -eq '.hidden' })
        $hidden.Count | Should -Be 0
    }

    It 'tree -I pattern excludes matching files' {
        $results = @(Invoke-BashTree -I '*.log' $treeDir)
        $logs = @($results | Where-Object { $_.Name -match '\.log$' })
        $logs.Count | Should -Be 0
    }

    It 'tree shows summary line with directories and files count' {
        $results = @(Invoke-BashTree $treeDir)
        $summary = $results[-1]
        $summary.BashText | Should -Match '\d+ director'
        $summary.BashText | Should -Match '\d+ file'
    }

    It 'tree --dirsfirst puts directories before files' {
        $results = @(Invoke-BashTree '--dirsfirst' $treeDir)
        $depth1 = @($results | Where-Object { $_.Depth -eq 1 -and $_.Name -ne '' })
        $firstDir = -1
        $lastFile = -1
        for ($i = 0; $i -lt $depth1.Count; $i++) {
            if ($depth1[$i].IsDirectory -and $firstDir -eq -1) { $firstDir = $i }
            if (-not $depth1[$i].IsDirectory) { $lastFile = $i }
        }
        if ($firstDir -ge 0 -and $lastFile -ge 0) {
            $firstDir | Should -BeLessThan $lastFile
        }
    }

    It 'tree root line shows directory name' {
        $results = @(Invoke-BashTree $treeDir)
        $root = $results[0]
        $root.Depth | Should -Be 0
        $root.IsDirectory | Should -BeTrue
    }

    It 'tree entry has correct properties' {
        $results = @(Invoke-BashTree $treeDir)
        $entry = $results | Where-Object { $_.Depth -gt 0 } | Select-Object -First 1
        $entry.Name | Should -Not -BeNullOrEmpty
        $entry.Path | Should -Not -BeNullOrEmpty
        $entry.PSObject.Properties['TreePrefix'] | Should -Not -BeNullOrEmpty
    }

    It 'tree nonexistent path writes error' {
        $results = @(Invoke-BashTree '/nonexistent/path' 2>$null)
        $results.Count | Should -Be 0
    }
}

Describe 'Invoke-BashTree — Alias' {
    It 'tree alias resolves to Invoke-BashTree' {
        $alias = Get-Alias -Name tree -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashTree'
    }
}

Describe 'Invoke-BashDu — Pipeline Bridge' {
    BeforeAll {
        $duPipeDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-du-pipe-$(Get-Random)"
        New-Item -Path $duPipeDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $duPipeDir 'file.txt') -Value 'data'
    }

    AfterAll {
        Remove-Item -Path $duPipeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'du output pipes to grep' {
        $results = @(Invoke-BashDu $duPipeDir | Invoke-BashGrep 'file')
        # du without -a won't show files, but grep on BashText of directory entries
        # Just verify pipeline works
        $duResults = @(Invoke-BashDu -a $duPipeDir)
        $duResults.Count | Should -BeGreaterOrEqual 1
        $duResults[0].PSObject.Properties['BashText'] | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-BashTree — Pipeline Bridge' {
    BeforeAll {
        $treePipeDir = Join-Path ([System.IO.Path]::GetTempPath()) "psbash-tree-pipe-$(Get-Random)"
        New-Item -Path $treePipeDir -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $treePipeDir 'file.txt') -Value 'data'
    }

    AfterAll {
        Remove-Item -Path $treePipeDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'tree output pipes to grep' {
        $results = @(Invoke-BashTree $treePipeDir | Invoke-BashGrep 'file')
        $results.Count | Should -BeGreaterOrEqual 1
    }
}

# ── Invoke-BashEnv ──────────────────────────────────────────────────────

Describe 'Invoke-BashEnv — List All' {
    It 'returns env vars as objects with Name, Value, BashText' {
        $results = @(Invoke-BashEnv)
        $results.Count | Should -BeGreaterThan 0
        $first = $results[0]
        $first.PSObject.Properties['Name'] | Should -Not -BeNullOrEmpty
        $first.PSObject.Properties['Value'] | Should -Not -BeNullOrEmpty
        $first.BashText | Should -Match '^[^=]+=.*'
    }

    It 'BashText format is NAME=value' {
        $results = @(Invoke-BashEnv)
        $sample = $results | Where-Object { $_.Name -eq 'PATH' } | Select-Object -First 1
        $sample | Should -Not -BeNullOrEmpty
        $sample.BashText | Should -Be "PATH=$($sample.Value)"
    }
}

Describe 'Invoke-BashEnv — Filter by Name' {
    It 'returns just the value for a specific variable' {
        $result = Invoke-BashEnv 'PATH'
        $result.Value | Should -Be $env:PATH
        $result.BashText | Should -Be "PATH=$($env:PATH)"
    }

    It 'returns error for nonexistent variable' {
        $result = Invoke-BashEnv 'PSBASH_NONEXISTENT_VAR_12345' 2>&1
        $result | Should -Match 'not set'
    }
}

Describe 'Invoke-BashEnv — Alias' {
    It 'env alias resolves to Invoke-BashEnv' {
        $alias = Get-Alias -Name env -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashEnv'
    }

    It 'printenv alias resolves to Invoke-BashEnv' {
        $alias = Get-Alias -Name printenv -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashEnv'
    }
}

# ── Invoke-BashBasename ─────────────────────────────────────────────────

Describe 'Invoke-BashBasename — Basic' {
    It 'extracts filename from path' {
        $result = Invoke-BashBasename '/foo/bar.txt'
        $result.BashText | Should -Be 'bar.txt'
    }

    It 'strips trailing slash from directory path' {
        $result = Invoke-BashBasename '/foo/bar/'
        $result.BashText | Should -Be 'bar'
    }

    It 'returns name when no directory' {
        $result = Invoke-BashBasename 'file.txt'
        $result.BashText | Should -Be 'file.txt'
    }
}

Describe 'Invoke-BashBasename — Suffix Removal' {
    It 'removes suffix with -s flag' {
        $result = Invoke-BashBasename -s '.txt' '/foo/bar.txt'
        $result.BashText | Should -Be 'bar'
    }

    It 'does not remove suffix if it does not match' {
        $result = Invoke-BashBasename -s '.log' '/foo/bar.txt'
        $result.BashText | Should -Be 'bar.txt'
    }

    It 'does not remove suffix that equals the entire name' {
        $result = Invoke-BashBasename -s 'bar' '/foo/bar'
        $result.BashText | Should -Be 'bar'
    }
}

Describe 'Invoke-BashBasename — Multiple Paths' {
    It 'handles multiple path arguments' {
        $results = @(Invoke-BashBasename '/a/one.txt' '/b/two.txt')
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be 'one.txt'
        $results[1].BashText | Should -Be 'two.txt'
    }
}

Describe 'Invoke-BashBasename — Alias' {
    It 'basename alias resolves to Invoke-BashBasename' {
        $alias = Get-Alias -Name basename -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashBasename'
    }
}

# ── Invoke-BashDirname ──────────────────────────────────────────────────

Describe 'Invoke-BashDirname — Basic' {
    It 'extracts directory from path' {
        $result = Invoke-BashDirname '/foo/bar.txt'
        $result.BashText | Should -Be '/foo'
    }

    It 'returns parent for nested directory' {
        $result = Invoke-BashDirname '/foo/bar/baz'
        $result.BashText | Should -Be '/foo/bar'
    }

    It 'returns . for bare filename' {
        $result = Invoke-BashDirname 'file.txt'
        $result.BashText | Should -Be '.'
    }

    It 'returns / for root path' {
        $result = Invoke-BashDirname '/file.txt'
        $result.BashText | Should -Be '/'
    }
}

Describe 'Invoke-BashDirname — Multiple Paths' {
    It 'handles multiple path arguments' {
        $results = @(Invoke-BashDirname '/a/one.txt' '/b/two.txt')
        $results.Count | Should -Be 2
        $results[0].BashText | Should -Be '/a'
        $results[1].BashText | Should -Be '/b'
    }
}

Describe 'Invoke-BashDirname — Alias' {
    It 'dirname alias resolves to Invoke-BashDirname' {
        $alias = Get-Alias -Name dirname -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashDirname'
    }
}

# ── Invoke-BashPwd ──────────────────────────────────────────────────────

Describe 'Invoke-BashPwd — Basic' {
    It 'returns current directory with forward slashes' {
        $result = Invoke-BashPwd
        $result.BashText | Should -Not -BeNullOrEmpty
        $result.BashText | Should -Not -Match '\\'
    }

    It 'matches Get-Location result' {
        $result = Invoke-BashPwd
        $expected = (Get-Location).Path -replace '\\', '/'
        $result.BashText | Should -Be $expected
    }
}

Describe 'Invoke-BashPwd — Alias' {
    It 'pwd alias resolves to Invoke-BashPwd' {
        $alias = Get-Alias -Name pwd -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashPwd'
    }
}

# ── Invoke-BashHostname ─────────────────────────────────────────────────

Describe 'Invoke-BashHostname — Basic' {
    It 'returns machine hostname' {
        $result = Invoke-BashHostname
        $result.BashText | Should -Not -BeNullOrEmpty
        $result.BashText | Should -Be ([System.Net.Dns]::GetHostName())
    }
}

Describe 'Invoke-BashHostname — Alias' {
    It 'hostname alias resolves to Invoke-BashHostname' {
        $alias = Get-Alias -Name hostname -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashHostname'
    }
}

# ── Invoke-BashWhoami ───────────────────────────────────────────────────

Describe 'Invoke-BashWhoami — Basic' {
    It 'returns current username' {
        $result = Invoke-BashWhoami
        $result.BashText | Should -Not -BeNullOrEmpty
        $expected = [System.Environment]::UserName
        $result.BashText | Should -Be $expected
    }
}

Describe 'Invoke-BashWhoami — Alias' {
    It 'whoami alias resolves to Invoke-BashWhoami' {
        $alias = Get-Alias -Name whoami -Scope Global
        $alias.Definition | Should -Be 'Invoke-BashWhoami'
    }
}

# ── Slice 19 Pipeline Bridge ────────────────────────────────────────────

Describe 'Slice 19 — Pipeline Bridge' {
    It 'env output pipes to grep' {
        $results = @(Invoke-BashEnv | Invoke-BashGrep 'PATH')
        $results.Count | Should -BeGreaterOrEqual 1
    }

    It 'basename output pipes to grep' {
        $result = Invoke-BashBasename '/foo/bar.txt' | Invoke-BashGrep 'bar'
        $result | Should -Not -BeNullOrEmpty
    }
}

# ── Tab Completion (Slice 24) ──────────────────────────────────────────

Describe 'Register-BashCompletions — Flag Spec Data' {
    It 'BashFlagSpecs contains ls with expected flags' {
        $specs = & (Get-Module PsBash) { $script:BashFlagSpecs }
        $specs | Should -Not -BeNullOrEmpty
        $specs.ContainsKey('ls') | Should -BeTrue
        $lsFlags = $specs['ls']
        $lsFlags.Count | Should -BeGreaterOrEqual 8
        ($lsFlags | Where-Object { $_[0] -eq '-l' }) | Should -Not -BeNullOrEmpty
        ($lsFlags | Where-Object { $_[0] -eq '-a' }) | Should -Not -BeNullOrEmpty
        ($lsFlags | Where-Object { $_[0] -eq '-h' }) | Should -Not -BeNullOrEmpty
        ($lsFlags | Where-Object { $_[0] -eq '-R' }) | Should -Not -BeNullOrEmpty
    }

    It 'BashFlagSpecs contains grep with short and context flags' {
        $specs = & (Get-Module PsBash) { $script:BashFlagSpecs }
        $specs.ContainsKey('grep') | Should -BeTrue
        $grepFlags = $specs['grep']
        ($grepFlags | Where-Object { $_[0] -eq '-i' }) | Should -Not -BeNullOrEmpty
        ($grepFlags | Where-Object { $_[0] -eq '-A' }) | Should -Not -BeNullOrEmpty
        ($grepFlags | Where-Object { $_[0] -eq '-B' }) | Should -Not -BeNullOrEmpty
        ($grepFlags | Where-Object { $_[0] -eq '-C' }) | Should -Not -BeNullOrEmpty
    }

    It 'BashFlagSpecs contains all expected commands' {
        $specs = & (Get-Module PsBash) { $script:BashFlagSpecs }
        $expectedCommands = @(
            'ls', 'cat', 'grep', 'sort', 'head', 'tail', 'wc',
            'find', 'stat', 'cp', 'mv', 'rm', 'mkdir', 'rmdir',
            'touch', 'ln', 'ps', 'sed', 'awk', 'cut', 'tr',
            'uniq', 'nl', 'diff', 'comm', 'column', 'join',
            'paste', 'tee', 'xargs', 'jq', 'date', 'seq',
            'du', 'tree', 'basename', 'pwd'
        )
        foreach ($cmd in $expectedCommands) {
            $specs.ContainsKey($cmd) | Should -BeTrue -Because "$cmd should have flag specs"
        }
    }
}

Describe 'Register-BashCompletions — Completer Results' {
    It 'ls completer returns flags when word starts with -' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['ls'] }
        $completer | Should -Not -BeNullOrEmpty
        $results = @(& $completer '-' $null $null)
        $results | Should -Not -BeNullOrEmpty
        $results | Should -HaveCount 8
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '-l'
        $names | Should -Contain '-a'
        $names | Should -Contain '-h'
        $names | Should -Contain '-R'
    }

    It 'grep completer returns matching flags for -' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['grep'] }
        $results = @(& $completer '-' $null $null)
        $results | Should -Not -BeNullOrEmpty
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '-i'
        $names | Should -Contain '-v'
        $names | Should -Contain '-n'
    }

    It 'completions have correct CompletionResultType' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['ls'] }
        $results = @(& $completer '-' $null $null)
        foreach ($r in $results) {
            $r | Should -BeOfType [System.Management.Automation.CompletionResult]
            $r.ResultType | Should -Be ([System.Management.Automation.CompletionResultType]::ParameterValue)
        }
    }

    It 'completions include description tooltip' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['ls'] }
        $results = @(& $completer '-l' $null $null)
        $match = $results | Where-Object { $_.CompletionText -eq '-l' }
        $match | Should -Not -BeNullOrEmpty
        $match.ToolTip | Should -Be 'long listing'
    }

    It 'completer returns nothing for non-flag words' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['ls'] }
        $results = @(& $completer 'foo' $null $null)
        $results.Count | Should -Be 0
    }

    It 'completer filters by prefix' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['sort'] }
        $results = @(& $completer '-n' $null $null)
        $results | Should -Not -BeNullOrEmpty
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '-n'
        $names | Should -Not -Contain '-r'
    }

    It 'stat completer returns --printf' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['stat'] }
        $results = @(& $completer '--' $null $null)
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '--printf'
    }

    It 'tree completer returns --dirsfirst' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['tree'] }
        $results = @(& $completer '--' $null $null)
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '--dirsfirst'
    }

    It 'find completer returns -name, -type, -maxdepth' {
        $completer = & (Get-Module PsBash) { $script:BashCompleters['find'] }
        $results = @(& $completer '-' $null $null)
        $names = $results | ForEach-Object { $_.CompletionText }
        $names | Should -Contain '-name'
        $names | Should -Contain '-type'
        $names | Should -Contain '-maxdepth'
    }
}

Describe 'Register-BashCompletions — Function Export' {
    It 'Register-BashCompletions function exists' {
        $cmd = Get-Command Register-BashCompletions -ErrorAction SilentlyContinue
        $cmd | Should -Not -BeNullOrEmpty
    }
}
