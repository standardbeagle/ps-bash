## Absorbed from Differential.Tests/ProcessSubFixturesTests.cs (scripts only; expectations recorded from bash 5.2).

#### ProcessSub_TempFile_DiffIdentical_ExitsZero
diff <(seq 1 5) <(seq 1 5)
## stdout:
## status: 0

#### ProcessSub_TempFile_CatMultiple
cat <(echo one) <(echo two)
## STDOUT:
one
two
## END
## status: 0

#### ProcessSub_Source_ExportsVariable
source <(echo 'PSBASH_T10_VAR=hello'); echo $PSBASH_T10_VAR
## stdout: hello
## status: 0

#### ProcessSub_Source_MultiLineOutput
source <(printf 'A=first\nB=second\n'); echo $A $B
## stdout: first second
## status: 0

#### ProcessSub_TempFile_NegatedDiff
! diff <(echo a) <(echo a); echo $?
## stdout: 1
## status: 0

#### ProcessSub_TempFile_PasteTwoSubs
paste <(seq 1 5) <(seq 6 10)
## STDOUT:
1	6
2	7
3	8
4	9
5	10
## END
## status: 0

#### ProcessSub_TempFile_WcSingleLargeInput
wc -l <(seq 1 100) | cut -d' ' -f1
## stdout: 100
## status: 0

#### ProcessSub_Source_FunctionDefinition
source <(echo 'greet() { echo "hi $1"; }'); greet world
## stdout: hi world
## status: 0

#### ProcessSub_TempFile_CatMultiLineEach
cat <(echo -e "line1\nline2") <(echo -e "line3\nline4")
## STDOUT:
line1
line2
line3
line4
## END
## status: 0
