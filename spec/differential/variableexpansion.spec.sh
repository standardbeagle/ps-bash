## Absorbed from Differential.Tests/VariableExpansionDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Differential_BasicVar_Expands
x=hello; echo $x
## stdout: hello
## status: 0

#### Differential_BracedVar_Expands
x=hello; echo ${x}
## stdout: hello
## status: 0

#### Differential_UndefinedVar_ExpandsEmpty
unset PSBASH_UNDEF_TEST_VAR; echo ">${PSBASH_UNDEF_TEST_VAR}<"
## stdout: ><
## status: 0

#### Differential_DefaultOperator_UnsetVar
unset PSBASH_UNDEF_TEST_VAR; echo ${PSBASH_UNDEF_TEST_VAR:-fallback}
## stdout: fallback
## status: 0

#### Differential_DefaultOperator_SetVar_ReturnsVar
x=hello; echo ${x:-fallback}
## stdout: hello
## status: 0

#### Differential_AssignDefault_UnsetVar_AssignsAndReturns
unset PSBASH_ASSIGN_TEST; echo ${PSBASH_ASSIGN_TEST:=assigned}; echo $PSBASH_ASSIGN_TEST
## STDOUT:
assigned
assigned
## END
## status: 0

#### Differential_AlternativeOperator_SetVar
x=hello; echo ${x:+yes}
## stdout: yes
## status: 0

#### Differential_AlternativeOperator_UnsetVar
unset PSBASH_UNDEF_TEST_VAR; echo ">${PSBASH_UNDEF_TEST_VAR:+yes}<"
## stdout: ><
## status: 0

#### Differential_Substring_OffsetAndLength
x=hello; echo ${x:1:3}
## stdout: ell
## status: 0

#### Differential_Substring_OffsetOnly
x=hello; echo ${x:2}
## stdout: llo
## status: 0

#### Differential_SuffixRemove_Shortest
x=hello.txt; echo ${x%.txt}
## stdout: hello
## status: 0

#### Differential_SuffixRemove_Longest
x=a.b.c; echo ${x%%.*}
## stdout: a
## status: 0

#### Differential_PrefixRemove_Shortest
x=hello.txt; echo ${x#hello}
## stdout: .txt
## status: 0

#### Differential_PrefixRemove_Longest
x=a.b.c; echo ${x##*.}
## stdout: c
## status: 0

#### Differential_SubstituteFirst
x=hello; echo ${x/l/L}
## stdout: heLlo
## status: 0

#### Differential_SubstituteFirst_MultipleMatchReplacesOnce
x="aabbcc"; echo "${x/a/X}"
## stdout: Xabbcc
## status: 0

#### Differential_SubstituteAll
x=hello; echo ${x//l/L}
## stdout: heLLo
## status: 0

#### Differential_CaseUpperAll
x=hello; echo ${x^^}
## stdout: HELLO
## status: 0

#### Differential_CaseLowerAll
x=HELLO; echo ${x,,}
## stdout: hello
## status: 0

#### Differential_CaseUpperFirst
x=hello; echo ${x^}
## stdout: Hello
## status: 0

#### Differential_CaseLowerFirst
x=HELLO; echo ${x,}
## stdout: hELLO
## status: 0

#### Differential_Length_StringVar
x=hello; echo ${#x}
## stdout: 5
## status: 0

#### Differential_SpecialVar_ExitCode_True
true; echo $?
## stdout: 0
## status: 0

#### Differential_SpecialVar_ExitCode_False
false; echo $?
## stdout: 1
## status: 0

#### Differential_SpecialVar_At_PositionalParams
set -- a b c; for x in "$@"; do echo $x; done
## STDOUT:
a
b
c
## END
## status: 0

#### Differential_SpecialVar_Hash_Count
set -- a b c; echo $#
## stdout: 3
## status: 0

#### Differential_SetDashDash_SimpleArgs_PositionalAccessible
set -- hello world; echo "$1 $2"
## stdout: hello world
## status: 0

#### Differential_QuotedAt_PreservesSpacesInArgs
set -- "hello world" foo; for x in "$@"; do echo "[$x]"; done
## STDOUT:
[hello world]
[foo]
## END
## status: 0

#### Differential_ForIn_BareAtStar_IteratesPerPositional
set -- a b c; for x in "$*"; do echo "<$x>"; done; for y in $@; do echo "-$y"; done
## STDOUT:
<a b c>
-a
-b
-c
## END
## status: 0

#### Differential_QuotedVar_SpacesPreserved
x="hello world"; echo "[$x]"
## stdout: [hello world]
## status: 0

#### Differential_Injection_VarContainsDollarParen_NoExec
x='$(echo injected)'; echo "$x"
## stdout: $(echo injected)
## status: 0

#### Differential_Injection_VarContainsSemicolon_NoSplit
x='a;b'; echo "$x"
## stdout: a;b
## status: 0

#### Differential_SpecialVar_Positional_1to3
set -- alpha beta gamma; echo $1; echo $2; echo $3
## STDOUT:
alpha
beta
gamma
## END
## status: 0

#### Differential_SpecialVar_Pid_IsNonEmptyInteger
pid=$$; if [ -n "$pid" ] && [ "$pid" -gt 0 ] 2>/dev/null; then echo pid_ok; else echo pid_bad; fi
## stdout: pid_ok
## status: 0
