## Imported from the Oils spec tests (explore-parsing.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Length of length of ARGS!
fun() { echo ${##}; }
fun 0 1 2 3 4 5 6 7 8 
## stdout: 1
## status: 0
## timeout: 10

#### Length of length of ARGS!  2 digit
fun() { echo ${##}; }
fun 0 1 2 3 4 5 6 7 8 9
## stdout: 2
## status: 0
## timeout: 10

#### readonly +

# dash and bash validate this!  But not set +

readonly + >/dev/null
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-explore-parsing explore-parsing (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set +
set + >/dev/null
echo status=$?
## stdout: status=0
## status: 0
## XFAIL ps-bash gap-oils-explore-parsing explore-parsing (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Is \r considered whitespace?
echo -e 'echo\rTEST' > myscript
$SH myscript
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-explore-parsing explore-parsing (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
