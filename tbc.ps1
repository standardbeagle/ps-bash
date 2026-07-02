
$cases = @(
  @{ a=@('1','-eq','2','-a','3','-eq','3'); want=$false; d='1=2 AND 3=3' },
  @{ a=@('3','-eq','3','-a','2','-eq','2'); want=$true;  d='3=3 AND 2=2' },
  @{ a=@('-f','/nonexist','-o','-d','/');  want=$true;  d='-f miss OR -d /' },
  @{ a=@('1','-eq','1','-o','1','-eq','2','-a','1','-eq','2'); want=$true; d='T OR (F AND F)' },
  @{ a=@('1','-eq','2','-o','1','-eq','2','-a','1','-eq','1'); want=$false; d='F OR (F AND T)' },
  @{ a=@('!','-f','/nonexist'); want=$true; d='! -f miss' }
)
foreach ($c in $cases) {
  $got = Test-BashCondition @($c.a)
  $ok = ([bool]$got -eq $c.want)
  "{0}  {1,-20} got={2} want={3}" -f $(if($ok){'PASS'}else{'FAIL'}), $c.d, [bool]$got, $c.want
}
