## Imported from the Oils spec tests (var-op-len.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### String length
v=foo
echo ${#v}
## stdout: 3
## status: 0
## timeout: 10

#### Unicode string length (UTF-8)
v=$'_\u03bc_'
echo ${#v}
## stdout: 3
## status: 0
## timeout: 10

#### Length of undefined variable
echo ${#undef}
## stdout: 0
## status: 0
## timeout: 10

#### Length of undefined variable with nounset
set -o nounset
echo ${#undef}
## stdout:
## status: 1
## timeout: 10

#### Length operator can't be followed by test operator
echo ${#x-default}

x=''
echo ${#x-default}

x='foo'
echo ${#x-default}
## stdout:
## status: 1
## timeout: 10

#### ${#s} respects LC_ALL - length in bytes or code points
case $SH in dash) exit ;; esac

# This test case is sorta "infected" because spec-common.sh sets LC_ALL=C.UTF-8
#
# For some reason mksh behaves differently
#
# See demo/04-unicode.sh

#echo $LC_ALL
unset LC_ALL 

# note: this may depend on the CI machine config
LANG=en_US.UTF-8

#LC_ALL=en_US.UTF-8

for s in $'\u03bc' $'\U00010000'; do
  LC_ALL=
  echo "len=${#s}"

  LC_ALL=C
  echo "len=${#s}"

  echo
done
## STDOUT:
len=1
len=2

len=4
len=4

## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-len var-op-len (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
