## Imported from the Oils spec tests (builtin-eval-source.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Eval
eval "a=3"
echo $a
## stdout: 3
## status: 0
## timeout: 10

#### eval accepts/ignores --
eval -- echo hi
## stdout: hi
## status: 0
## timeout: 10

#### eval usage
eval -
echo $?
eval -z
echo $?
127
0
127
## STDOUT:
127
2
## END
## status: 127
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### eval string with 'break continue return error'

set -e

sh_func_that_evals() {
  local code_str=$1
  for i in 1 2; do
    echo $i
    eval "$code_str"
  done
  echo 'end func'
}

for code_str in break continue return false; do
  echo "--- $code_str"
  sh_func_that_evals "$code_str"
done
echo status=$?
## STDOUT:
--- break
1
end func
--- continue
1
2
end func
--- return
1
--- false
1
## END
## status: 1
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### exit within eval (regression)
eval 'exit 42'
echo 'should not get here'
## stdout:
## status: 42
## timeout: 10

#### Source nonexistent
source /nonexistent/path
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Source with no arguments
source
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### Eval with syntax error
eval 'echo >'
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### Eval in does tilde expansion

x="~"
eval y="$x"  # scalar
test "$x" = "$y" || echo FALSE
[[ $x == /* ]] || echo FALSE  # doesn't start with /
[[ $y == /* ]] && echo TRUE

#argv "$x" "$y"
## STDOUT:
FALSE
FALSE
TRUE
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Eval in bash does tilde expansion in array

# the "make" plugin in bash-completion relies on this?  wtf?
x="~"

# UPSTREAM CODE

#eval array=( "$x" )

# FIXED CODE -- proper quoting.

eval 'array=(' "$x" ')'  # array

test "$x" = "${array[0]}" || echo FALSE
[[ $x == /* ]] || echo FALSE  # doesn't start with /
[[ "${array[0]}" == /* ]] && echo TRUE
## STDOUT:
FALSE
FALSE
TRUE
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### source looks in PATH for files
mkdir -p dir
echo "echo hi" > dir/cmd
PATH="dir:$PATH"
. cmd
rm dir/cmd
## stdout: hi
## status: 0
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### source works for files in subdirectory
mkdir -p dir
echo "echo path" > dir/cmd
. dir/cmd
rm dir/cmd
## stdout: path
## status: 0
## timeout: 10

#### sourcing along PATH should ignore directories

mkdir -p _tmp/shell
mkdir -p _tmp/dir/hello.sh
printf 'echo hi' >_tmp/shell/hello.sh

DIR=$PWD/_tmp/dir
SHELL=$PWD/_tmp/shell

# Should find the file hello.sh right away and source it
PATH="$SHELL:$PATH" . hello.sh
echo status=$?

# Should fail because hello.sh cannot be found
PATH="$DIR:$SHELL:$PATH" . hello.sh
echo status=$?
## STDOUT:
hi
status=0
hi
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-eval-source builtin-eval-source (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
