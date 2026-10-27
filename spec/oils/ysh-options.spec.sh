## Imported from the Oils spec tests (ysh-options.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### simple_word_eval doesn't split, glob, or elide empty
mkdir mydir
touch foo.z bar.z spam.z
spaces='a b'
dir=mydir
glob=*.z
prefix=sp
set -- 'x y' z

for i in 1 2; do
  local empty=
  argv.py $spaces $glob $empty $prefix*.z

  # arrays still work too, with this weird rule
  argv.py -"$@"-

  shopt -s simple_word_eval
done
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### simple_word_eval and strict_array conflict over globs
touch foo.txt bar.txt
set -- f

argv.py "$@"*.txt
shopt -s simple_word_eval
argv.py "$@"*.txt
shopt -s strict_array
argv.py "$@"*.txt
## stdout:
## status: 127
## timeout: 10

#### simple_word_eval and glob
shopt -s simple_word_eval

# rm -v -f *.ff
touch 1.ff 2.ff

for i in *.ff; do
  echo $i
done

array=(*.ff)
echo "${array[@]}"

echo *.ff
## STDOUT:
1.ff
2.ff
1.ff 2.ff
1.ff 2.ff
## END
## status: 0
## timeout: 10

#### parse_at
words=(a 'b c')
argv.py @words

shopt -s parse_at
argv.py @words
## stdout:
## status: 127
## timeout: 10

#### DISABLED: parse_at can't be used outside top level

# shopt -u expand_aliases conflicted with ble.sh, and it was also broken for
# proc/func

f() {
  shopt -s parse_at
  echo status=$?
}
f
echo 'should not get here'
## STDOUT:
status=1
should not get here
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### sourcing a file that sets parse_at
cat >lib.sh <<EOF
shopt -s parse_at
echo lib.sh
EOF

words=(a 'b c')
argv.py @words

# This has a side effect, which is a bit weird, but not sure how to avoid it.
# Maybe we should say that libraries aren't allowed to change it?

source lib.sh
echo 'main.sh'

argv.py @words
## STDOUT:
lib.sh
main.sh
## END
## status: 127
## timeout: 10

#### @a splices into $0
shopt -s simple_word_eval parse_at
a=(echo hi)
"${a[@]}"
@a

# Bug fix
shopt -s strict_array

"${a[@]}"
@a
## STDOUT:
hi
hi
## END
## status: 127
## timeout: 10

#### shopt -s strict:all
shopt -s strict:all
# normal option names
shopt -o -p | grep -- ' -o ' | grep -v hashall
shopt -p strict:all
## STDOUT:
set -o braceexpand
set -o interactive-comments
## END
## status: 1
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### oil:upgrade includes inherit_errexit
shopt -s oil:upgrade
echo $(echo one; false; echo two)
## stdout: one two
## status: 0
## timeout: 10

#### parse_brace: bad block to assignment builtin
shopt -s oil:upgrade
# This is a fatal programming error.  It's unlike passing an extra arg?
local x=y { echo 'bad block' }
echo status=$?
## stdout: status=1
## status: 0
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### parse_brace: bad block to external program
shopt -s oil:upgrade
# This is a fatal programming error.  It's unlike passing an extra arg?
ls { echo 'bad block' }
echo status=$?
## stdout: status=2
## status: 0
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### parse_brace: cd { } in pipeline
shopt -s oil:upgrade
cd /tmp {
  pwd
  pwd
} | tr a-z A-Z
## STDOUT:
/tmp/tmp.GoDHdeOrYP
/tmp/tmp.GoDHdeOrYP
## END
## status: 2
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### parse_brace: if accepts blocks
shopt -s oil:upgrade
shopt -u errexit  # don't need strict_errexit check!

if test -n foo {
  echo one
}
# harder
if test -n foo; test -n bar {
  echo two
}

# just like POSIX shell!
if test -n foo;

   test -n bar {
  echo three
}

if test -z foo {
  echo if
} else {
  echo else
}

if test -z foo {
  echo if
} elif test -z '' {
  echo elif
} else {
  echo else
}

echo 'one line'
if test -z foo { echo if } elif test -z '' { echo 1 }; if test -n foo { echo 2 };

echo 'sh syntax'
if test -z foo; then echo if; elif test -z ''; then echo 1; fi; if test -n foo { echo 2 };

# NOTE: This is not allowed because it's like a brace group!
# if test -n foo; { 
## stdout:
## status: 2
## timeout: 10

#### parse_brace: brace group in if condition

# strict_errexit would make this a RUNTIME error
shopt -s parse_brace
if { echo one; echo two } {
  echo three
}
## stdout:
## status: 2
## timeout: 10

#### parse_brace: while/until
shopt -s oil:upgrade
while true {
  echo one
  break
}
while true { echo two; break }

echo 'sh syntax'
while true; do echo three; break; done
## stdout:
## status: 2
## timeout: 10

#### parse_brace: for-in loop
shopt -s oil:upgrade
for x in one two {
  echo $x
}
for x in three { echo $x }

echo 'sh syntax'
for x in four; do echo $x; done
## stdout:
## status: 2
## timeout: 10

#### parse_paren: if statement
shopt -s oil:upgrade
var x = 1
if (x < 42) {
  echo less
}

if (x < 0) {
  echo negative
} elif (x < 42) {
  echo less
}

if (x < 0) {
  echo negative
} elif (x < 1) {
  echo less
} else {
  echo other
}
## stdout:
## status: 2
## timeout: 10

#### parse_paren: while statement
shopt -s oil:upgrade

# ksh style
var x = 1
while (( x < 3 )) {
  echo $x
  setvar x += 1
}
echo 'done ksh'

# sh style
var y = 1
while test $y -lt 3 {
  echo $y
  setvar y += 1
}
echo 'done sh'

# oil
var z = 1
while (z < 3) {
  echo $z
  setvar z += 1
}
echo 'done oil'
## stdout:
## status: 2
## timeout: 10

#### while subshell without parse_paren
while ( echo one ); do
  echo two
  break
done
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### nullglob is on with oil:upgrade
write one *.zzz two
shopt -s oil:upgrade
write __
write one *.zzz two
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### nullglob is on with oil:all
write  one *.zzz two
shopt -s oil:all
write __
write one *.zzz two
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### shopt -s simple_echo
foo='one   two'
echo $foo   # bad split then join
shopt -s simple_echo
echo
echo "$foo"  # good
echo $foo

echo -e "$foo"  # -e isn't special!
echo -n "$foo"  # -n isn't special!
## stdout-json: "one two\n\none   two\none two\none   two\none   two"
## status: 0
## timeout: 10

#### sigpipe_status_ok

status_141() {
  return 141
}

yes | head -n 1
echo ${PIPESTATUS[@]}

# DUMMY
yes | status_141
echo ${PIPESTATUS[@]}

shopt --set oil:upgrade  # sigpipe_status_ok
shopt --unset errexit

yes | head -n 1
echo ${PIPESTATUS[@]}

# Conveniently, the last 141 isn't changed to 0, because it's run in the
# CURRENT process.

yes | status_141
echo ${PIPESTATUS[@]}

echo background
false | status_141 &
wait
echo status=$? pipestatus=${PIPESTATUS[@]}
## STDOUT:
y
141 0
141 141
y
141 0
141 141
background
status=0 pipestatus=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### parse_at can be specified through sh -O
$SH +O parse_at -c 'words=(a "b c"); argv.py @words'
$SH -O parse_at -c 'words=(a "b c"); argv.py @words'
## stdout:
## status: 2
## timeout: 10
## real-launcher

#### osh -O errexit: use -O everywhere, even for Bourne options
$SH -O errexit -c 'shopt -p -o errexit'
#$SH -O errexit -c 'shopt -p errexit'  # bash doesn't allow this, but Oils does
## stdout:
## status: 2
## timeout: 10
## real-launcher

#### osh -O invalid
$SH -O errexit -c 'echo hi'
echo status=$?
$SH -O invalid -c 'echo hi'
echo status=$?
## STDOUT:
status=2
status=2
## END
## status: 0
## timeout: 10
## real-launcher

#### osh -o new_option is also accepted

$SH -o nullglob -c 'echo nullglob'
echo $? flag nullglob

$SH -o oil:upgrade -c 'proc p { echo upgrade }; p'
echo $? flag oil:upgrade

# Should disallow these

set -o nullglob
echo $? set builtin nullglob
set -o oil:upgrade
echo $? set builtin oil:upgrade
## STDOUT:
2 flag nullglob
2 flag oil:upgrade
2 set builtin nullglob
2 set builtin oil:upgrade
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-options ysh-options (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
