## Absorbed from Differential.Tests/SedDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Sed_TrailingNewline_SingleLineInput_NextCommandNotGlued
printf 'x.y\n' | sed 's/\./-/'; echo Z
## STDOUT:
x-y
Z
## END
## status: 0

#### Sed_NativePipe_OdSeesBashTextNotTable
printf 'a\n' | sed s/a/b/ | od -c
## STDOUT:
0000000   b  \n
0000002
## END
## status: 0

#### Sed_NextJoin_MultiCommandExpression
printf 'a\nb\nc\n' | sed 'N;s/\n/+/'
## STDOUT:
a+b
c
## END
## status: 0

#### Sed_Next_LastLine_NoExtraPrint
printf 'a\nb\nc\n' | sed -n 'N;p'
## STDOUT:
a
b
## END
## status: 0

#### Sed_Change_Range_EmitsOnce
printf '1\n2\n3\n' | sed '1,2c\
REPL'
## STDOUT:
REPL
3
## END
## status: 0

#### Sed_DollarAddress_AppendAfterLastLine
printf '1\n2\n3\n' | sed '$a\
APP'
## STDOUT:
1
2
3
APP
## END
## status: 0

#### Sed_AddressRegex_Semicolon_DeletesMatchingLines
printf 'a;b\nc\n' | sed '/;/d'
## stdout: c
## status: 0

#### Sed_AddressRegex_EscapedSemicolon_DeletesMatchingLines
printf 'a;b\nc\n' | sed '/\;/d'
## stdout: c
## status: 0

#### Sed_RangeAddress_EndRegexSemicolonAnchor
printf 'a;b\nc;d\ne\n' | sed '1,/;$/d'
## stdout:
## status: 0

#### Sed_Bre_PunctClass_Compiles
printf 'a!b\n' | sed 's/[[:punct:]]/_/'
## stdout: a_b
## status: 0
