param([string]$File, [string[]]$Cases, [string]$Tag, [string]$Reason)
# Replace the `## XFAIL ps-bash ...` line of each named case of a spec file with `## XFAIL ps-bash <Tag> <Reason>`
# (A7: a case that differs only by a platform or a deliberate choice keeps its mark under an honest reason).
# Bytes outside those lines are untouched.
if (-not $Tag -or -not $Reason) { Write-Output 'usage: retag.ps1 -File f -Cases a,b -Tag tag -Reason "text"'; exit 2 }
$enc = [Text.Encoding]::GetEncoding('iso-8859-1')   # byte-transparent: some spec files hold raw bytes
$t = [IO.File]::ReadAllText($File, $enc)
if ($t.Contains("`r`n")) { Write-Output "CRLF working copy (spec files are eol=lf): run 'git checkout -- $File' or convert it, then retry"; exit 1 }
$nameEnc = [Text.Encoding]::UTF8
$line = $enc.GetString($nameEnc.GetBytes("`n## XFAIL ps-bash $Tag $Reason"))
$done = 0
foreach ($c in $Cases) {
    $needle = $enc.GetString($nameEnc.GetBytes("#### $c`n"))
    $at = $t.IndexOf($needle, [StringComparison]::Ordinal)
    if ($at -lt 0) { Write-Output "NOT FOUND: $c"; continue }
    $next = $t.IndexOf("`n#### ", $at + 1, [StringComparison]::Ordinal); if ($next -lt 0) { $next = $t.Length }
    $x = $t.IndexOf("`n## XFAIL ps-bash", $at, [StringComparison]::Ordinal)
    if ($x -lt 0 -or $x -ge $next) { Write-Output "NO MARK: $c"; continue }
    $eol = $t.IndexOf("`n", $x + 1); if ($eol -lt 0) { $eol = $t.Length }
    $t = $t.Remove($x, $eol - $x).Insert($x, $line)
    $done++
}
[IO.File]::WriteAllText($File, $t, $enc)
Write-Output "$([IO.Path]::GetFileName($File)): retagged $done of $($Cases.Count)"
