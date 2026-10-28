## Imported from the Oils spec tests (builtin-bracket.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### zero args: [ ]
[ ] || echo false
## stdout: false
## status: 0
## timeout: 10

#### one arg: [ x ] where x is one of '=' '!' '(' ']'
[ = ]
echo status=$?
[ ] ]
echo status=$?
[ '!' ]
echo status=$?
[ '(' ]
echo status=$?
## STDOUT:
status=0
status=0
status=0
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### one arg: empty string is false.  Equivalent to -n.
test 'a'  && echo true
test ''   || echo false
## STDOUT:
true
false
## END
## status: 0
## timeout: 10

#### -a as unary operator (alias of -e)
# NOT IMPLEMENTED FOR OSH, but could be later.  See comment in core/id_kind.py.
[ -a / ]
echo status=$?
[ -a /nonexistent ]
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## timeout: 10

#### two args: -z with = ! ( ]
[ -z = ]
echo status=$?
[ -z ] ]
echo status=$?
[ -z '!' ]
echo status=$?
[ -z '(' ]
echo status=$?
## STDOUT:
status=1
status=1
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### three args
[ foo = '' ]
echo status=$?
[ foo -a '' ]
echo status=$?
[ foo -o '' ]
echo status=$?
[ ! -z foo ]
echo status=$?
[ \( foo \) ]
echo status=$?
## STDOUT:
status=1
status=1
status=0
status=0
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### four args
[ ! foo = foo ]
echo status=$?
[ \( -z foo \) ]
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### test with extra args is syntax error
test -n x ]
echo status=$?
test -n x y
echo status=$?
## STDOUT:
status=2
status=2
## END
## status: 0
## timeout: 10

#### ] syntax errors
[
echo status=$?
test  # not a syntax error
echo status=$?
[ -n x  # missing ]
echo status=$?
[ -n x ] y  # extra arg after ]
echo status=$?
[ -n x y  # extra arg
echo status=$?
## STDOUT:
status=2
status=1
status=2
status=2
status=2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### -n
test -n 'a'  && echo true
test -n ''   || echo false
## STDOUT:
true
false
## END
## status: 0
## timeout: 10

#### ! -a
[ -z '' -a ! -z x ]
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### -o
[ -z x -o ! -z x ]
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### -t 1 for stdout
# There is no way to get a terminal in the test environment?
[ -t 1 ]
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### [ -t invalid ]
[ -t invalid ]
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### [ a -eq b ]
[ a -eq a ]
echo status=$?
## stdout: status=2
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### test -b -c -S (block, character, socket)
# NOTE: we do not have the "true" case

echo -b
test -b nonexistent
echo status=$?
test -b testdata
echo status=$?
test -b /
echo status=$?

echo -c
test -c nonexistent
echo status=$?
test -c testdata
echo status=$?

echo -S
test -S nonexistent
echo status=$?
test -S testdata
echo status=$?
## STDOUT:
-b
status=1
status=1
status=1
-c
status=1
status=1
-S
status=1
status=1
## END
## status: 0
## timeout: 10

#### -v to test variable (bash)
test -v nonexistent
echo global=$?

g=1
test -v g
echo global=$?

f() {
  local f_var=0
  g
}

g() {
  test -v f_var
  echo dynamic=$?
  test -v g
  echo dynamic=$?
  test -v nonexistent
  echo dynamic=$?
}
f
## STDOUT:
global=1
global=0
dynamic=0
dynamic=0
dynamic=1
## END
## status: 0
## timeout: 10

#### test -o for options
# note: it's lame that the 'false' case is confused with the 'typo' case.
# but checking for error code 2 is unlikely anyway.
test -o nounset
echo status=$?

set -o nounset
test -o nounset
echo status=$?

test -o _bad_name_
echo status=$?
## STDOUT:
status=1
status=0
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### -nt -ot
[ present -nt absent ] || exit 1
[ absent -ot present ] || exit 2
## stdout:
## status: 1
## timeout: 10

#### Overflow error
test -t 12345678910
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Bug regression
test "$ipv6" = "yes" -a "$ipv6lib" != "none"
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### test -c
test -c /dev/zero
echo status=$?
## stdout: status=0
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### test -S
test -S /dev/zero
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### bug from pnut: negative number $((-1))

# https://lobste.rs/s/lplim1/design_self_compiling_c_transpiler#c_km2ywc

[ $((-42)) -le 0 ]
echo status=$?

[ $((-1)) -le 0 ]
echo status=$?

echo

[ -1 -le 0 ]
echo status=$?

[ -42 -le 0 ]
echo status=$?

echo

test -1 -le 0
echo status=$?

test -42 -le 0
echo status=$?
## STDOUT:
status=0
status=0

status=0
status=0

status=0
status=0
## END
## status: 0
## timeout: 10

#### negative octal numbers, etc.

# zero
[ -0 -eq 0 ]
echo zero=$?

# octal numbers can be negative
[ -0123 -eq -83 ]
echo octal=$?

# hex doesn't have negative numbers?
[ -0xff -eq -255 ]
echo hex=$?

# base N doesn't either
[ -64#a -eq -10 ]
echo baseN=$?
## STDOUT:
zero=0
octal=1
hex=2
baseN=2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### No octal, hex, base N conversion - leading 0 is a regular decimal

# arithmetic has octal conversion
echo $(( 073 ))
echo $(( -073 ))

echo

# Bracket does NOT have octal conversion!  That is annoying.
[ 073 -eq 73 ]
echo status=$?

[ -073 -eq -73 ]
echo status=$?

echo

[ 0xff -eq 255 ]
echo hex=$?
[ 64#a -eq 10 ]
echo baseN=$?
## STDOUT:
59
-59

status=0
status=0

hex=2
baseN=2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Looks like octal, but digit is too big

# arithmetic has octal conversion
echo $(( 083 ))
echo status=$?

echo $(( -083 ))
echo status=$?

echo

# Bracket does NOT have octal conversion!  That is annoying.
[ 083 -eq 83 ]
echo status=$?

[ -083 -eq -83 ]
echo status=$?
## STDOUT:
status=1
status=1

status=0
status=0
## END
## status: 0
## timeout: 10

#### no recursive arith [ 1+2 -eq 3 ]

[ 1+2 -eq 3 ]
echo status=$?

s='1+2'
[ "$s" -eq 3 ]
echo status=$?
## STDOUT:
status=2
status=2
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ( )
[ -z '' -a '(' ! -z x ')' ]
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### ( ) ! -a -o with system version of [
command [ --version
command [ -z '' -a '(' ! -z x ')' ] && echo true
## stdout: true
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### == is alias for =
[ a = a ] && echo true
[ a == a ] && echo true
## STDOUT:
true
true
## END
## status: 0
## timeout: 10

#### == and = does not do glob
[ abc = 'a*' ]
echo status=$?
[ abc == 'a*' ]
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### [ with op variable
# OK -- parsed AFTER evaluation of vars
op='='
[ a $op a ] && echo true
[ a $op b ] || echo false
## STDOUT:
true
false
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [ with unquoted empty var
empty=''
[ $empty = '' ] && echo true
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [ compare with literal -f
# Hm this is the same
var=-f
[ $var = -f ] && echo true
[ '-f' = $var ] && echo true
## STDOUT:
true
true
## END
## status: 0
## timeout: 10

#### [ '(' foo ] is runtime syntax error
[ '(' foo ]
echo status=$?
## stdout: status=2
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### -z '>' implies two token lookahead
[ -z ] && echo true  # -z is operand
[ -z '>' ] || echo false  # -z is operator
[ -z '>' -- ] && echo true  # -z is operand
## STDOUT:
true
false
true
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### operator/operand ambiguity with ]
# bash parses this as '-z' AND ']', which is true.  It's a syntax error in
# dash/mksh.
[ -z -a ] ]
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### operator/operand ambiguity with -a
# bash parses it as '-z' AND '-a'.  It's a syntax error in mksh but somehow a
# runtime error in dash.
[ -z -a -a ]
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### -k for sticky bit
# not isolated: /tmp usually has sticky bit on
# https://en.wikipedia.org/wiki/Sticky_bit

test -k /tmp
echo status=$?

test -k /bin
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-bracket builtin-bracket (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### More negative numbers
case $SH in dash) exit ;; esac

[[ -1 -le 0 ]]
echo status=$?

[[ $((-1)) -le 0 ]]
echo status=$?
## STDOUT:
status=0
status=0
## END
## status: 0
## timeout: 10
## real-launcher
