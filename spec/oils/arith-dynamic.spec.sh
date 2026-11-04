## Imported from the Oils spec tests (arith-dynamic.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Double quotes
echo $(( "1 + 2" * 3 ))
echo $(( "1+2" * 3 ))
## STDOUT:
7
7
## END
## status: 0
## timeout: 10

#### Single quotes
echo $(( '1' + '2' * 3 ))
echo status=$?

echo $(( '1 + 2' * 3 ))
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### Substitutions
x='1 + 2'
echo $(( $x * 3 ))
echo $(( "$x" * 3 ))
## STDOUT:
7
7
## END
## status: 0
## XFAIL ps-bash gap-oils-arith-dynamic arith-dynamic (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Variable references
x='1'
echo $(( x + 2 * 3 ))
echo status=$?

# Expression like values are evaluated first (this is unlike double quotes)
x='1 + 2'
echo $(( x * 3 ))
echo status=$?
## STDOUT:
7
status=0
9
status=0
## END
## status: 0
## timeout: 10
