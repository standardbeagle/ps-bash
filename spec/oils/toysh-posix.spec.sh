## Imported from the Oils spec tests (toysh-posix.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Fatal error
# http://landley.net/notes.html#20-06-2020

abc=${a?bc} echo hello; echo blah
## stdout:
## status: 1
## timeout: 10

#### readonly with temp binding
# http://landley.net/notes.html#20-06-2020

# temp binding
readonly abc=123
abc=def echo one
echo status=$?

echo potato < /does/not/exist || echo hello
## STDOUT:
one
status=0
hello
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh-posix toysh-posix (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Failed redirect in assignment, vs. export

abc=def > /does/not/exist1
echo abc=$abc

export abc=def > /does/not/exist2
echo abc=$abc
## STDOUT:
abc=def
abc=def
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh-posix toysh-posix (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### IFS 5
cc() { for i in $*; do echo -$i-; done;}; cc "" "" "" "" ""
cc() { echo =$1$2=;}; cc "" ""
## stdout: ==
## status: 0
## timeout: 10

#### Command Sub Syntax Error
# http://landley.net/notes.html#28-01-2020

echo $(if true)
echo $?
echo $(false)
echo $?
## stdout:
## status: 2
## timeout: 10

#### Pipeline - http://landley.net/notes-2019.html#16-12-2019
echo hello | { read i; echo $i;} | { read i; echo $i;} | cat
echo hello | while read i; do echo -=$i=- | sed s/=/@/g ; done | cat
## STDOUT:
hello
-@hello@-
## END
## status: 0
## timeout: 10

#### setting readonly var (bash is only one where it's non-fatal)
# https://landley.net/notes-2020.html#20-06-2020

readonly abc=123
abc=def
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Function def in pipeline
# http://landley.net/notes.html#26-05-2020

echo hello | potato() { echo abc; } | echo ha
## stdout: ha
## status: 0
## timeout: 10

#### IFS 2 - copied into spec/word-split
# this one appears different between osh and bash
A="   abc   def   "; for i in ""x""$A""; do echo =$i=; done
## STDOUT:
=x=
=abc=
=def=
==
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh-posix toysh-posix (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### IFS 3
IFS=x; X="onextwoxxthree"; y=$X; echo $y
## stdout: one two  three
## status: 0
## timeout: 10

#### IFS 4

setopt SH_WORD_SPLIT

IFS=x

func1() {
  echo /$*/
  for i in $*; do echo -$i-; done
}
func1 "" ""

echo

func2() {
  echo /"$*"/
  for i in =$*=; do echo -$i-; done
}
func2 "" ""
## STDOUT:
/ /
--

/x/
-=-
-=-
## END
## status: 0
## timeout: 10

#### Can't parse extra }

$SH -c 'for i in a"$@"b;do echo =$i=;done;}' 123 456 789
## stdout:
## status: 2
## timeout: 10
## real-launcher
