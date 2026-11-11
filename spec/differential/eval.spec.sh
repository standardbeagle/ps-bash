## Absorbed from Differential.Tests/EvalDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Differential_Eval_BasicEcho_OutputsHello
eval "echo hello"
## stdout: hello
## status: 0

#### Differential_Eval_VarAssignment_VisibleAfterEval
eval 'x=hello'; echo $x
## stdout: hello
## status: 0

#### Differential_Eval_MultiArgJoined_ExecutesJoinedString
eval echo hello
## stdout: hello
## status: 0

#### Differential_Eval_NestedEval_InnerCommandExecuted
eval "eval 'echo nested'"
## stdout: nested
## status: 0

#### Differential_Eval_CmdSubMultilineExports_DoesNotHang
eval $(printf 'export A=1\nexport B=2\n'); echo $A $B
## stdout: 1 2
## status: 0

#### Differential_Eval_QuotedCmdSubMultiline_DoesNotHang
eval "$(printf 'export A=1\nexport B=2\n')"; echo $A $B
## stdout: 1 2
## status: 0

#### Differential_Eval_InFunction_StaticAssignVisible
f() { eval 'x=5'; echo "$x"; }; f
## stdout: 5
## status: 0

#### Differential_Eval_InFunction_DynamicAssignVisible
f() { v='y=7'; eval "$v"; echo "$y"; }; f
## stdout: 7
## status: 0

#### Differential_Eval_InFunction_ExecutesCommand
f() { eval 'echo inside'; }; f
## stdout: inside
## status: 0

#### Differential_Eval_ExitCodeFromFalse_PropagatedToShell
eval false; echo $?
## stdout: 1
## status: 0

#### Differential_Eval_FnmShapedPayload_DoesNotHang
export PATH=/usr/bin
eval $(printf 'export FNM_MULTISHELL_PATH="/tmp/fnm_msh"\nexport PATH="/tmp/fnm_msh":"$PATH"\n')
echo $FNM_MULTISHELL_PATH
echo "${PATH%%[:;]*}"  # the first entry: Windows separates PATH with ;
## STDOUT:
/tmp/fnm_msh
/tmp/fnm_msh
## END
## status: 0
