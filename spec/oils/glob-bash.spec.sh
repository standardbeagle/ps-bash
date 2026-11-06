## Imported from the Oils spec tests (glob-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### shopt -s nullglob
argv.py _tmp/spec-tmp/*.nonexistent
shopt -s nullglob
argv.py _tmp/spec-tmp/*.nonexistent
## stdout:
## status: 127
## timeout: 10

#### shopt -s failglob in command context
argv.py *.ZZ
shopt -s failglob
argv.py *.ZZ  # nothing is printed, not []
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### shopt -s failglob in loop context
for x in *.ZZ; do echo $x; done
echo status=$?
shopt -s failglob
for x in *.ZZ; do echo $x; done
echo status=$?
## STDOUT:
*.ZZ
status=0
status=1
## END
## status: 0
## timeout: 10

#### shopt -s failglob in array literal context
myarr=(*.ZZ)
echo "${myarr[@]}"
shopt -s failglob
myarr=(*.ZZ)
echo status=$?
## STDOUT:
*.ZZ
status=1
## END
## status: 0
## timeout: 10

#### shopt -s failglob exits properly in command context with set -e
set -e
argv.py *.ZZ
shopt -s failglob
argv.py *.ZZ
echo status=$?
## stdout:
## status: 127
## timeout: 10

#### shopt -s failglob exits properly in loop context with set -e
set -e
for x in *.ZZ; do echo $x; done
echo status=$?

shopt -s failglob
for x in *.ZZ; do echo $x; done
echo status=$?
## STDOUT:
*.ZZ
status=0
## END
## status: 1
## timeout: 10

#### shopt -s failglob behavior on single line with semicolon
# bash behaves differently when commands are separated by a semicolon than when
# separated by a newline. This behavior doesn't make sense or seem to be
# intentional, so osh does not mimic it.

shopt -s failglob
echo *.ZZ; echo status=$? # bash doesn't execute the second part!
echo *.ZZ
echo status=$? # bash executes this
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-glob-bash glob-bash (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
