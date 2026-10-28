## Imported from the Oils spec tests (word-split.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### IFS is scoped
IFS=b
word=abcd
f() { local IFS=c; argv.py $word; }
f
argv.py $word
## stdout:
## status: 127
## timeout: 10

#### Tilde sub is not split, but var sub is
HOME="foo bar"
argv.py ~
argv.py $HOME
## stdout:
## status: 127
## timeout: 10

#### Word splitting
a="1 2"
b="3 4"
argv.py $a"$b"
## stdout:
## status: 127
## timeout: 10

#### Word splitting 2
a="1 2"
b="3 4"
c="5 6"
d="7 8"
argv.py $a"$b"$c"$d"

# Has tests on differences between  $*  "$*"  $@  "$@"
# http://stackoverflow.com/questions/448407/bash-script-to-receive-and-repass-quoted-parameters
## stdout:
## status: 127
## timeout: 10

#### $*
fun() { argv.py -$*-; }
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10

#### "$*"
fun() { argv.py "-$*-"; }
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10

#### $@
# How does this differ from $* ?  I don't think it does.
fun() { argv.py -$@-; }
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10

#### "$@"
fun() { argv.py "-$@-"; }
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10

#### empty argv
argv.py 1 "$@" 2 $@ 3 "$*" 4 $* 5
## stdout:
## status: 127
## timeout: 10

#### $* with empty IFS
set -- "1 2" "3  4"

IFS=
argv.py $*
argv.py "$*"
## stdout:
## status: 127
## timeout: 10

#### Word elision with space
s1=' '
argv.py $s1
## stdout:
## status: 127
## timeout: 10

#### Word elision with non-whitespace IFS
# Treated differently than the default IFS.  What is the rule here?
IFS='_'
char='_'
space=' '
empty=''
argv.py $char
argv.py $space
argv.py $empty
## stdout:
## status: 127
## timeout: 10

#### Leading/trailing word elision with non-whitespace IFS
# This behavior is weird.
IFS=_
s1='_a_b_'
argv.py $s1
## stdout:
## status: 127
## timeout: 10

#### Leading ' ' vs leading ' _ '
# This behavior is weird, but all shells agree.
IFS='_ '
s1='_ a  b _ '
s2='  a  b _ '
argv.py $s1
argv.py $s2
## stdout:
## status: 127
## timeout: 10

#### Multiple non-whitespace IFS chars.
IFS=_-
s1='a__b---c_d'
argv.py $s1
## stdout:
## status: 127
## timeout: 10

#### IFS with whitespace and non-whitepace.
# NOTE: Three delimiters means two empty words in the middle.  No elision.
IFS='_ '
s1='a_b _ _ _ c  _d e'
argv.py $s1
## stdout:
## status: 127
## timeout: 10

#### empty $@ and $* is elided
fun() { argv.py 1 $@ $* 2; }
fun
## stdout:
## status: 127
## timeout: 10

#### unquoted empty arg is elided
empty=""
argv.py 1 $empty 2
## stdout:
## status: 127
## timeout: 10

#### unquoted whitespace arg is elided
space=" "
argv.py 1 $space 2
## stdout:
## status: 127
## timeout: 10

#### empty literals are not elided
space=" "
argv.py 1 $space"" 2
## stdout:
## status: 127
## timeout: 10

#### no splitting when IFS is empty
IFS=""
foo="a b"
argv.py $foo
## stdout:
## status: 127
## timeout: 10

#### default value can yield multiple words
argv.py 1 ${undefined:-"2 3" "4 5"} 6
## stdout:
## status: 127
## timeout: 10

#### default value can yield multiple words with part joining
argv.py 1${undefined:-"2 3" "4 5"}6
## stdout:
## status: 127
## timeout: 10

#### default value with unquoted IFS char
IFS=_
argv.py 1${undefined:-"2_3"x_x"4_5"}6
## stdout:
## status: 127
## timeout: 10

#### IFS empty doesn't do splitting
IFS=''
x=$(python2 -c 'print(" a b\tc\n")')
argv.py $x
## stdout:
## status: 127
## timeout: 10

#### IFS unset behaves like $' \t\n'
unset IFS
x=$(python2 -c 'print(" a b\tc\n")')
argv.py $x
## stdout:
## status: 127
## timeout: 10

#### IFS='\'
# NOTE: OSH fails this because of double backslash escaping issue!
IFS='\'
s='a\b'
argv.py $s
## stdout:
## status: 127
## timeout: 10

#### IFS='\ '
# NOTE: OSH fails this because of double backslash escaping issue!
# When IFS is \, then you're no longer using backslash escaping.
IFS='\ '
s='a\b \\ c d\'
argv.py $s
## stdout:
## status: 127
## timeout: 10

#### IFS characters are glob metacharacters
IFS='* '
s='a*b c'
argv.py $s

IFS='?'
s='?x?y?z?'
argv.py $s

IFS='['
s='[x[y[z['
argv.py $s
## stdout:
## status: 127
## timeout: 10

#### Trailing space
argv.py 'Xec  ho '
argv.py X'ec  ho '
argv.py X"ec  ho "
## stdout:
## status: 127
## timeout: 10

#### Empty IFS (regression for bug)
IFS=
echo ["$*"]
set a b c
echo ["$*"]
## STDOUT:
[]
[abc]
## END
## status: 0
## timeout: 10

#### Unset IFS (regression for bug)
set a b c
unset IFS
echo ["$*"]
## stdout: [a b c]
## status: 0
## timeout: 10

#### IFS=o (regression for bug)
IFS=o
echo hi
## stdout: hi
## status: 0
## timeout: 10

#### IFS and joining arrays
IFS=:
set -- x 'y z'
argv.py "$@"
argv.py $@
argv.py "$*"
argv.py $*
## stdout:
## status: 127
## timeout: 10

#### IFS and joining arrays by assignments
IFS=:
set -- x 'y z'

s="$@"
argv.py "$s"

s=$@
argv.py "$s"

s="$*"
argv.py "$s"

s=$*
argv.py "$s"

# bash and mksh agree, but this doesn't really make sense to me.
# In OSH, "$@" is the only real array, so that's why it behaves differently.



# TODO:
# - unquoted args of whitespace are not elided (when IFS = null)
# - empty quoted args are kept
#
# - $* $@ with empty IFS
# - $* $@ with custom IFS
#
# - no splitting when IFS is empty
# - word splitting removes leading and trailing whitespace

# TODO: test framework needs common setup

# Test IFS and $@ $* on all these
## stdout:
## status: 127
## timeout: 10

#### TODO
empty=""
space=" "
AB="A B"
X="X"
Yspaces=" Y "
## stdout:
## status: 0
## timeout: 10

#### IFS='' with $@ and $* (bug #627)
set -- a 'b c'
IFS=''
argv.py at $@
argv.py star $*

# zsh agrees
## stdout:
## status: 127
## timeout: 10

#### IFS='' with $@ and $* and printf (bug #627)
set -- a 'b c'
IFS=''
printf '[%s]\n' $@
printf '[%s]\n' $*
## STDOUT:
[a]
[b c]
[a]
[b c]
## END
## status: 0
## timeout: 10

#### IFS='' with ${a[@]} and ${a[*]} (bug #627)
case $SH in dash | ash) exit 0 ;; esac

myarray=(a 'b c')
IFS=''
argv.py at ${myarray[@]}
argv.py star ${myarray[*]}
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### IFS='' with ${!prefix@} and ${!prefix*} (bug #627)
case $SH in dash | mksh | ash | yash) exit 0 ;; esac

gLwbmGzS_var1=1
gLwbmGzS_var2=2
IFS=''
argv.py at ${!gLwbmGzS_@}
argv.py star ${!gLwbmGzS_*}
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### IFS='' with ${!a[@]} and ${!a[*]} (bug #627)
case $SH in dash | mksh | ash | yash) exit 0 ;; esac

IFS=''
a=(v1 v2 v3)
argv.py at ${!a[@]}
argv.py star ${!a[*]}
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### Bug #628 split on : with : in literal word

# 2025-03: What's the cause of this bug?
#
# OSH is very wrong here
#   ['a', '\\', 'b']
# Is this a fundamental problem with the IFS state machine?
# It definitely relates to the use of backslashes.
# So we have at least 4 backslash bugs

IFS=':'
word='a:'
argv.py ${word}:b
argv.py ${word}:

echo ---

# Same thing happens for 'z'
IFS='z'
word='az'
argv.py ${word}zb
argv.py ${word}z
## stdout: ---
## status: 127
## timeout: 10

#### Bug #698, similar crash
var='\'
set -f
echo $var
## stdout: \
## status: 0
## timeout: 10

#### Bug #1664, \\ with noglob

# Note that we're not changing IFS

argv.py [\\]_
argv.py "[\\]_"

# TODO: no difference observed here, go back to original bug

#argv.py [\\_
#argv.py "[\\_"

echo noglob

# repeat cases with -f, noglob
set -f

argv.py [\\]_
argv.py "[\\]_"

#argv.py [\\_
#argv.py "[\\_"
## stdout: noglob
## status: 127
## timeout: 10

#### Empty IFS bug #2141 (from pnut)

res=0
sum() {
  # implement callee-save calling convention using `set`
  # here, we save the value of $res after the function parameters
  set $@ $res           # $1 $2 $3 are now set
  res=$(($1 + $2))
  echo "$1 + $2 = $res"
  res=$3                # restore the value of $res
}

unset IFS
sum 12 30 # outputs "12 + 30 = 42"

IFS=' '
sum 12 30 # outputs "12 + 30 = 42"

IFS=
sum 12 30 # outputs "1230 + 0 = 1230"

# I added this
IFS=''
sum 12 30

set -u
IFS=
sum 12 30 # fails with "fatal: Undefined variable '2'" on res=$(($1 + $2))
## STDOUT:
12 + 30 = 42
12 + 30 = 42
12 + 30 = 42
12 + 30 = 42
12 + 30 = 42
## END
## status: 0
## timeout: 10

#### Unicode in IFS

# bash, zsh, and yash support unicode in IFS, but dash/mksh/ash don't.

# for zsh, though we're not testing it here
setopt SH_WORD_SPLIT

x=çx IFS=ç
printf "<%s>\n" $x
## STDOUT:
<>
<x>
## END
## status: 0
## timeout: 10

#### 4 x 3 table: (default IFS, IFS='', IFS=zx) x ( $* "$*" $@ "$@" )

setopt SH_WORD_SPLIT  # for zsh

set -- 'a b' c ''

# default IFS
argv.py '  $*  '  $*
argv.py ' "$*" ' "$*"
argv.py '  $@  '  $@
argv.py ' "$@" ' "$@"
echo

IFS=''
argv.py '  $*  '  $*
argv.py ' "$*" ' "$*"
argv.py '  $@  '  $@
argv.py ' "$@" ' "$@"
echo

IFS=zx
argv.py '  $*  '  $*
argv.py ' "$*" ' "$*"
argv.py '  $@  '  $@
argv.py ' "$@" ' "$@"


# zsh disagrees on
# - $@ with default IFS an
# - $@ with IFS=zx
## STDOUT:


## END
## status: 127
## timeout: 10

#### 4 x 3 table - with for loop
case $SH in yash) exit ;; esac  # no echo -n

setopt SH_WORD_SPLIT  # for zsh

set -- 'a b' c ''

# default IFS
echo -n '  $*  ';  for i in  $*;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$*" ';  for i in "$*"; do echo -n ' '; echo -n -$i-; done; echo
echo -n '  $@  ';  for i in  $@;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$@" ';  for i in "$@"; do echo -n ' '; echo -n -$i-; done; echo
echo

IFS=''
echo -n '  $*  ';  for i in  $*;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$*" ';  for i in "$*"; do echo -n ' '; echo -n -$i-; done; echo
echo -n '  $@  ';  for i in  $@;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$@" ';  for i in "$@"; do echo -n ' '; echo -n -$i-; done; echo
echo

IFS=zx
echo -n '  $*  ';  for i in  $*;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$*" ';  for i in "$*"; do echo -n ' '; echo -n -$i-; done; echo
echo -n '  $@  ';  for i in  $@;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$@" ';  for i in "$@"; do echo -n ' '; echo -n -$i-; done; echo
## STDOUT:
  $*   -a- -b- -c-
 "$*"  -a b c -
  $@   -a- -b- -c-
 "$@"  -a b- -c- --

  $*   -a b- -c-
 "$*"  -a bc-
  $@   -a b- -c-
 "$@"  -a b- -c- --

  $*   -a b- -c-
 "$*"  -a b c -
  $@   -a b- -c-
 "$@"  -a b- -c- --
## END
## status: 0
## XFAIL ps-bash gap-oils-word-split word-split (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### IFS=x and '' and $@ - same bug as spec/toysh-posix case #12
case $SH in yash) exit ;; esac  # no echo -n

setopt SH_WORD_SPLIT  # for zsh

set -- one '' two

IFS=zx
echo -n '  $*  ';  for i in  $*;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$*" ';  for i in "$*"; do echo -n ' '; echo -n -$i-; done; echo
echo -n '  $@  ';  for i in  $@;  do echo -n ' '; echo -n -$i-; done; echo
echo -n ' "$@" ';  for i in "$@"; do echo -n ' '; echo -n -$i-; done; echo

argv.py '  $*  '  $*
argv.py ' "$*" ' "$*"
argv.py '  $@  '  $@
argv.py ' "$@" ' "$@"
## STDOUT:
  $*   -one- -- -two-
 "$*"  -one  two-
  $@   -one- -- -two-
 "$@"  -one- -- -two-
## END
## status: 127
## XFAIL ps-bash gap-oils-word-split an unquoted word MIXING literal text and a variable (-$i-) is not split on IFS; only a whole-word expansion is
## timeout: 10
## real-launcher

#### IFS=x and '' and $@ (#2)
setopt SH_WORD_SPLIT  # for zsh

set -- "" "" "" "" ""
argv.py =$@=
argv.py =$*=
echo

IFS=
argv.py =$@=
argv.py =$*=
echo

IFS=x
argv.py =$@=
argv.py =$*=



# yash-2.49 seems to behave in a strange way, but this behavior seems to have
# been fixed at least in yash-2.57.
## STDOUT:


## END
## status: 127
## timeout: 10

#### IFS=x and '' and $@ (#3)
setopt SH_WORD_SPLIT  # for zsh

IFS=x
set -- "" "" "" "" ""

argv.py $*
set -- $*
argv.py $*
set -- $*
argv.py $*
set -- $*
argv.py $*
set -- $*
argv.py $*



['', '', '']
['']
[]
[]
[]

['', '', '', '', '']
['', '', '', '', '']
['', '', '', '', '']
['', '', '', '', '']
['', '', '', '', '']
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-word-split word-split (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ""$A"" - empty string on both sides - derived from spec/toysh-posix #15

A="   abc   def   "

argv.py $A
argv.py ""$A""

unset IFS

argv.py $A
argv.py ""$A""

echo

# Do the same thing in a for loop - this is IDENTICAL behavior

for i in $A; do echo =$i=; done
echo

for i in ""$A""; do echo =$i=; done
echo

unset IFS

for i in $A; do echo =$i=; done
echo

for i in ""$A""; do echo =$i=; done
## STDOUT:

=abc=
=def=

==
=abc=
=def=
==

=abc=
=def=

==
=abc=
=def=
==
## END
## status: 0
## XFAIL ps-bash gap-oils-word-split word-split (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Regression: "${!v*}"x should not be split
case $SH in dash|mksh|ash|yash) exit 99;; esac
IFS=x
axb=1
echo "${!axb*}"
echo "${!axb*}"x
## STDOUT:
axb
axbx
## END
## status: 0
## timeout: 10
## real-launcher

#### Regression: ${!v} should be split
v=hello
IFS=5
echo ${#v}
echo "${#v}"
## STDOUT:

5
## END
## status: 0
## XFAIL ps-bash gap-oils-word-split word-split (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Regression: "${v:-AxBxC}"x should not be split
IFS=x
v=
echo "${v:-AxBxC}"
echo "${v:-AxBxC}"x  # <-- osh failed this
echo ${v:-AxBxC}
echo ${v:-AxBxC}x
echo ${v:-"AxBxC"}
echo ${v:-"AxBxC"}x
echo "${v:-"AxBxC"}"
echo "${v:-"AxBxC"}"x
## STDOUT:
AxBxC
AxBxCx
A B C
A B Cx
AxBxC
AxBxCx
AxBxC
AxBxCx
## END
## status: 0
## XFAIL ps-bash gap-oils-word-split word-split (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
