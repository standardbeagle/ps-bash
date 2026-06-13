# Ad-hoc profiling harness for the hot-path cmdlets. Imports the freshly built
# binary cmdlets and pushes a large in-memory line set through each common
# command, reporting wall-clock + throughput. Not a committed test — a probe.
param(
    [int]$Lines = 200000,
    [int]$Repeat = 3
)

$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot '..\src\PsBash.Cmdlets\bin\Debug\net10.0\PsBash.Cmdlets.dll'
Import-Module $dll -ErrorAction Stop

# Build a realistic-ish data set: mixed-case words, numbers, whitespace fields.
$rand = [Random]::new(1234)
$words = 'alpha','beta','gamma','delta','Epsilon','ZETA','eta','theta'
$data = [string[]]::new($Lines)
for ($i = 0; $i -lt $Lines; $i++) {
    $w1 = $words[$rand.Next($words.Length)]
    $w2 = $words[$rand.Next($words.Length)]
    $n  = $rand.Next(0, 100000)
    $data[$i] = "$w1`t$n`t$w2-value_$($i % 997)"
}

function Bench($name, [scriptblock]$work) {
    $best = [double]::MaxValue
    for ($r = 0; $r -lt $Repeat; $r++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $count = (& $work | Measure-Object).Count
        $sw.Stop()
        if ($sw.Elapsed.TotalMilliseconds -lt $best) { $best = $sw.Elapsed.TotalMilliseconds }
    }
    $lps = [int]($Lines / ($best / 1000.0))
    "{0,-28} {1,8:N1} ms  {2,12:N0} lines/s  (out={3})" -f $name, $best, $lps, $count
}

"Profiling $Lines lines, best of $Repeat runs"
"=" * 78
Bench "tr a-z -> A-Z (translate)"  { $data | Invoke-BashTr 'a-z' 'A-Z' }
Bench "tr -d (delete digits)"       { $data | Invoke-BashTr -d '0-9' }
Bench "tr -s (squeeze)"             { $data | Invoke-BashTr -s 'a-z' }
Bench "tr -c (complement xlate)"    { $data | Invoke-BashTr -c 'a-zA-Z' '#' }
Bench "cut -f1 -d TAB"              { $data | Invoke-BashCut '-f1' "-d`t" }
Bench "cut -f1,3 -d TAB"           { $data | Invoke-BashCut '-f1,3' "-d`t" }
Bench "sort (default)"             { $data | Invoke-BashSort }
Bench "sort -n -k2"                { $data | Invoke-BashSort -n -k2 }
Bench "sort -u"                    { $data | Invoke-BashSort -u }
Bench "sort -V"                    { $data | Invoke-BashSort -V }
Bench "uniq"                       { $data | Invoke-BashUniq }
Bench "uniq -f1 (skip field)"      { $data | Invoke-BashUniq -f1 }
Bench "grep alpha"                 { $data | Invoke-BashGrep 'alpha' }
Bench "grep -c ZETA"               { $data | Invoke-BashGrep -c 'ZETA' }
