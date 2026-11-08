## Imported from the Oils spec tests (pipeline.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Brace group in pipeline
{ echo one; echo two; } | tac
## STDOUT:
two
one
## END
## status: 0
## timeout: 10

#### For loop starts pipeline
for w in one two; do
  echo $w
done | tac
## STDOUT:
two
one
## END
## status: 0
## timeout: 10

#### While Loop ends pipeline
seq 3 | while read i
do
  echo ".$i"
done
## STDOUT:
.1
.2
.3
## END
## status: 0
## timeout: 10

#### Redirect in Pipeline
echo hi 1>&2 | wc -l
## stdout: 0
## status: 0
## timeout: 10

#### Pipeline comments
echo abcd |    # input
               # blank line
tr a-z A-Z     # transform
## stdout: ABCD
## status: 0
## timeout: 10

#### Exit code is last status
echo a | egrep '[0-9]+'
## stdout:
## status: 1
## timeout: 10

#### PIPESTATUS
return3() {
  return 3
}
{ sleep 0.03; exit 1; } | { sleep 0.02; exit 2; } | { sleep 0.01; return3; }
echo ${PIPESTATUS[@]}
## stdout: 1 2 3
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### PIPESTATUS with shopt -s lastpipe
shopt -s lastpipe
return3() {
  return 3
}
{ sleep 0.03; exit 1; } | { sleep 0.02; exit 2; } | { sleep 0.01; return3; }
echo ${PIPESTATUS[@]}
## stdout: 1 2 3
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### |&
stdout_stderr.py |& cat
## stdout: /mnt/c/Users/andyb/AppData/Local/Temp/ps-bash/spec-record/b0820adfb1c84d0198db939c5309a11e.sh: line 1: stdout_stderr.py: command not found
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ! in if
if ! echo hi; then
  echo TRUE
else
  echo FALSE
fi
## STDOUT:
hi
FALSE
## END
## status: 0
## timeout: 10

#### ! with ||
! echo hi || echo FAILED
## STDOUT:
hi
FAILED
## END
## status: 0
## timeout: 10

#### ! with { }
! { echo 1; echo 2; } || echo FAILED
## STDOUT:
1
2
FAILED
## END
## status: 0
## timeout: 10

#### ! with ( )
! ( echo 1; echo 2 ) || echo FAILED
## STDOUT:
1
2
FAILED
## END
## status: 0
## timeout: 10

#### ! is not a command
v='!'
$v echo hi
## stdout:
## status: 127
## timeout: 10

#### Evaluation of argv[0] in pipeline occurs in child
${cmd=echo} hi | wc -l
echo "cmd=$cmd"
## STDOUT:
1
cmd=
## END
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### bash/dash/mksh run the last command is run in its own process
echo hi | read line
echo "line=$line"
## stdout: line=
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### shopt -s lastpipe (always on in OSH)
shopt -s lastpipe
echo hi | read line
echo "line=$line"
## stdout: line=hi
## status: 0
## timeout: 10

#### shopt -s lastpipe (always on in OSH)
shopt -s lastpipe
i=0
seq 3 | while read line; do
  (( i++ ))
done
echo i=$i
## stdout: i=3
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### SIGPIPE causes pipeline to die (regression for issue #295)
cat /dev/urandom | sleep 0.1
echo ${PIPESTATUS[@]}

# hm bash gives '1 0' which seems wrong
## stdout: 141 0
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Nested pipelines
{ sleep 0.1 | seq 3; } | cat
{ sleep 0.1 | seq 10; } | { cat | cat; } | wc -l
## STDOUT:
1
2
3
10
## END
## status: 0
## timeout: 10

#### Pipeline in eval
ls /dev/null | eval 'cat | cat' | wc -l
## stdout: 1
## status: 0
## timeout: 10

#### Initial value of PIPESTATUS is empty string
case $SH in dash|zsh) exit ;; esac

echo pipestatus ${PIPESTATUS[@]}
## stdout: pipestatus 0
## status: 0
## timeout: 10
## real-launcher

#### PIPESTATUS is set on simple commands
case $SH in dash|zsh) exit ;; esac

false
echo pipestatus ${PIPESTATUS[@]}

exit 55 | (exit 44)
echo pipestatus ${PIPESTATUS[@]}

true
echo pipestatus ${PIPESTATUS[@]}
## STDOUT:
pipestatus 1
pipestatus 55 44
pipestatus 0
## END
## status: 0
## XFAIL ps-bash gap-oils-pipeline pipeline (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ! turns non-zero into zero
! $SH -c 'exit 42'; echo $?
## stdout: 0
## status: 0
## timeout: 10
## real-launcher

#### ! turns zero into 1
! $SH -c 'exit 0'; echo $?
## stdout: 1
## status: 0
## timeout: 10
## real-launcher

#### shopt -s lastpipe and shopt -s no_last_fork interaction

case $SH in dash) exit ;; esac

$SH -c '
shopt -s lastpipe
set -o errexit
set -o pipefail

ls | false | wc -l'
echo status=$?

# Why does this give status 0?  It should fail

$SH -c '
shopt -s lastpipe
shopt -s no_fork_last  # OSH only
set -o errexit
set -o pipefail

ls | false | wc -l'
echo status=$?
## STDOUT:
0
status=1
0
status=1
## END
## status: 0
## timeout: 10
## real-launcher
