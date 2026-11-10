param([string]$File, [string[]]$Cases)
# Remove the `## XFAIL ps-bash` line from each named case of a spec file. Bytes outside those lines are untouched.
$enc = [Text.Encoding]::GetEncoding('iso-8859-1')   # byte-transparent: some spec files hold raw bytes
$t = [IO.File]::ReadAllText($File, $enc)
$nameEnc = [Text.Encoding]::UTF8
$done = 0
foreach ($c in $Cases) {
    $needle = $enc.GetString($nameEnc.GetBytes("#### $c`n"))
    $at = $t.IndexOf($needle, [StringComparison]::Ordinal)
    if ($at -lt 0) { Write-Output "NOT FOUND: $c"; continue }
    $next = $t.IndexOf("`n#### ", $at + 1, [StringComparison]::Ordinal); if ($next -lt 0) { $next = $t.Length }
    $x = $t.IndexOf("`n## XFAIL ps-bash", $at, [StringComparison]::Ordinal)
    if ($x -lt 0 -or $x -ge $next) { Write-Output "NO MARK: $c"; continue }
    $eol = $t.IndexOf("`n", $x + 1); if ($eol -lt 0) { $eol = $t.Length }
    $t = $t.Remove($x, $eol - $x)
    $done++
}
[IO.File]::WriteAllText($File, $t, $enc)
Write-Output "$([IO.Path]::GetFileName($File)): unmarked $done of $($Cases.Count)"
