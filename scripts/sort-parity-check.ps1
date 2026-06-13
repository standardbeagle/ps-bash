# Differential parity: real bash `sort` (via WSL) vs freshly built Invoke-BashSort.
# Canonicalize to LF + strip trailing whitespace per line, then byte-compare.
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot '..\src\PsBash.Cmdlets\bin\Debug\net10.0\PsBash.Cmdlets.dll'
Import-Module $dll -ErrorAction Stop

# Deterministic dataset exercising the decorated modes.
$lines = @(
  "banana`t30`tEpsilon"
  "apple`t5`tzeta"
  "Apple`t5`talpha"
  "cherry`t100`tBeta"
  "date`t2`tgamma"
  "banana`t30`tdelta"
  "1.10.0`t12`tMar"
  "1.2.0`t12`tJan"
  "1.9.0`t3`tDec"
  "10K`t7`tFeb"
  "1.5M`t7`tNov"
  "2G`t1`tjan"
  ""
  "  leading`t9`tspace"
  "ZZZ`t999`taaa"
)
$input = ($lines -join "`n")
$tmp = [IO.Path]::GetTempFileName()
# Write with LF endings, no trailing newline beyond the joined content.
[IO.File]::WriteAllText($tmp, $input, [Text.UTF8Encoding]::new($false))
$wslPath = (wsl wslpath ($tmp -replace '\\','/')).Trim()

function Canon($text) {
    ($text -replace "`r","") -split "`n" | ForEach-Object { $_.TrimEnd() }
}

$cases = @(
  @{ Name='default';        Bash='sort';                  Ps={ $lines | Invoke-BashSort } }
  @{ Name='-r reverse';     Bash='sort -r';               Ps={ $lines | Invoke-BashSort -r } }
  @{ Name='-f fold';        Bash='sort -f';               Ps={ $lines | Invoke-BashSort -f } }
  @{ Name='-n -k2';         Bash='sort -n -k2';           Ps={ $lines | Invoke-BashSort -n '-k2' } }
  @{ Name='-rn -k2';        Bash='sort -rn -k2';          Ps={ $lines | Invoke-BashSort -rn '-k2' } }
  @{ Name='-k3 -k2n multi'; Bash='sort -k3,3 -k2,2n';     Ps={ $lines | Invoke-BashSort '-k3,3' '-k2,2n' } }
  @{ Name='-u unique';      Bash='sort -u';               Ps={ $lines | Invoke-BashSort -u } }
  @{ Name='-d dict';        Bash='sort -d';               Ps={ $lines | Invoke-BashSort -d } }
  @{ Name='-V version -k1'; Bash='sort -V -k1,1';         Ps={ $lines | Invoke-BashSort -V '-k1,1' } }
)

$fail = 0
foreach ($c in $cases) {
    $bashOut = wsl bash -c "$($c.Bash) '$wslPath'"
    $psOut = (& $c.Ps | ForEach-Object { $_.BashText ?? "$_" })
    $b = Canon($bashOut -join "`n")
    $p = Canon($psOut -join "`n")
    if (($b -join "|") -eq ($p -join "|")) {
        "PASS  $($c.Name)"
    } else {
        $fail++
        "FAIL  $($c.Name)"
        "  bash: $($b -join ' / ')"
        "  ps  : $($p -join ' / ')"
    }
}
Remove-Item $tmp -ErrorAction SilentlyContinue
""
if ($fail -eq 0) { "ALL PARITY CHECKS PASSED" } else { "$fail PARITY FAILURE(S)" }
