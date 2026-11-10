## Absorbed from Differential.Tests/BashParityGapDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Local_IsVisibleToCallee
f(){ local v=1; g; }; g(){ echo "v=$v"; }; f
## stdout: v=1
## status: 0

#### Local_DoesNotLeakAfterReturn
f(){ local v=1; }; f; echo "[${v-unset}]"
## stdout: [unset]
## status: 0

#### Declare_InFunction_IsLocal
f(){ declare y=5; }; f; echo "[${y-unset}]"
## stdout: [unset]
## status: 0

#### Readonly_RejectsReassignment
readonly r=1; r=2; echo "r=$r"
## stdout:
## status: 1

#### VariableNames_AreCaseSensitive
x=1; X=2; echo "$x $X"
## stdout: 1 2
## status: 0
## XFAIL ps-bash gap-differential-bashparitygap bashparitygap (differential): differs from bash 5.2; see docs/specs/spec-gaps.md

#### LargeValue_SurvivesAssignment
big=$(head -c 40000 /dev/zero | tr '\0' a); echo ${#big}
## stdout: 40000
## status: 0
## XFAIL ps-bash gap-differential-bashparitygap bashparitygap (differential): differs from bash 5.2; see docs/specs/spec-gaps.md

#### DeclareInteger_EvaluatesArithmetic
declare -i n=3; n+=2; echo $n
## stdout: 5
## status: 0

#### DeclareNameref_WritesThrough
v=a; declare -n ref=v; ref=b; echo $v
## stdout: b
## status: 0

#### LocalIfs_AffectsStarJoin
f(){ local IFS=,; echo "$*"; }; f a b c
## stdout: a,b,c
## status: 0

#### UnexportedVariable_IsNotInChildEnvironment
x=1; env | grep -c '^x='
## stdout: 0
## status: 1
## XFAIL ps-bash gap-differential-bashparitygap bashparitygap (differential): differs from bash 5.2; see docs/specs/spec-gaps.md

#### ArithPostIncrement_ThenPlainExpansion
x=5; echo $((x++)) $x
## stdout: 5 6
## status: 0

#### ArithAssign_ThenPlainExpansion
x=1; echo $((x+=2)) $x
## stdout: 3 3
## status: 0

#### DefaultAssign_ThenPlainExpansion
unset y; echo ${y:=d} $y
## stdout: d d
## status: 0

#### ArraySliceNextToLength
a=(1 2 3); echo ${#a[@]} ${a[@]:1}
## stdout: 3 2 3
## status: 0

#### Printf_PercentQ
printf '%q\n' 'a b'
## stdout: a\ b
## status: 0

#### Printf_TimeFormat
printf '%(%Y)T\n' 0
## stdout: 1969
## status: 0

#### SetU_UnboundVariableFails
set -u; echo ${undefined_var}; echo after
## stdout:
## status: 1

#### SetF_DisablesGlob
cd "$(mktemp -d)"; touch a b; set -f; echo *
## stdout: *
## status: 0

#### ErrTrap_Fires
trap 'echo trapped' ERR; false; echo ok
## STDOUT:
trapped
ok
## END
## status: 0

#### SignalTrap_Fires
trap 'echo U' USR1; kill -USR1 $$; echo ok
## STDOUT:
U
ok
## END
## status: 0

#### Funcname_InFunction
f(){ echo "$FUNCNAME"; }; f
## stdout: f
## status: 0

#### Lineno_IsSet
echo $LINENO
## stdout: 1
## status: 0

#### Exec_RedirectOnly_PrintsNothing
exec 5<&0; echo ok
## stdout: ok
## status: 0

#### Getopts_Loop
set -- -a -b X; while getopts 'ab:' o; do echo "$o $OPTARG"; done
## STDOUT:
a 
b X
## END
## status: 0

#### Getopts_ExplicitArgs
getopts 'ab:' o -a; echo "o=$o"
## stdout: o=a
## status: 0

#### Pipestatus_AfterPipeline
true | false | true; echo "${PIPESTATUS[@]}"
## stdout: 0 1 0
## status: 0

#### Pipefail_ReportsFailure
set -o pipefail; false | true; echo $?
## stdout: 1
## status: 0
