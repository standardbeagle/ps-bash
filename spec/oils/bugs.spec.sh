## Imported from the Oils spec tests (bugs.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### echo keyword
echo done
## stdout: done
## status: 0
## timeout: 10

#### if/else
if false; then
  echo THEN
else
  echo ELSE
fi
## stdout: ELSE
## status: 0
## timeout: 10

#### Turn an array into an integer.
a=(1 2 3)
(( a = 42 )) 
echo $a
## stdout: 42
## status: 0
## timeout: 10

#### assign readonly -- one line
readonly x=1; x=2; echo hi
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### assign readonly -- multiple lines
readonly x=1
x=2
echo hi
## stdout: hi
## status: 0
## timeout: 10

#### assign readonly -- multiple lines -- set -o posix
set -o posix
readonly x=1
x=2
echo hi
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset readonly -- one line
readonly x=1; unset x; echo hi
## stdout: hi
## status: 0
## timeout: 10

#### unset readonly -- multiple lines
readonly x=1
unset x
echo hi
## stdout: hi
## status: 0
## timeout: 10

#### Function names
foo$x() {
  echo hi
}

foo $x() {
  echo hi
}

# Note: zsh should return 1 or 2
## stdout:
## status: 2
## timeout: 10

#### 'echo' and printf fail on writing to full disk

# Inspired by https://blog.sunfishcode.online/bugs-in-hello-world/

echo hi > /dev/full
echo status=$?

printf '%s\n' hi > /dev/full
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### other builtins fail on writing to full disk

type echo > /dev/full
echo status=$?

# other random builtin
ulimit -a > /dev/full
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### autoconf word split (#1449)

mysed() {
  for line in "$@"; do
    echo "[$line]"
  done
}

sedinputs="f1 f2"
sedscript='my sed command'

# Parsed and evaluated correctly: with word_part.EscapedLiteral \"

x=$(eval "mysed -n \"\$sedscript\" $sedinputs")
echo '--- $()'
echo "$x"

# With backticks, the \" gets lost somehow

x=`eval "mysed -n \"\$sedscript\" $sedinputs"`
echo '--- backticks'
echo "$x"


# Test it in a case statement

case `eval "mysed -n \"\$sedscript\" $sedinputs"` in 
  (*'[my sed command]'*)
    echo 'NOT SPLIT'
    ;;
esac
## STDOUT:
--- $()
[-n]
[my sed command]
[f1]
[f2]
--- backticks
[-n]
[my sed command]
[f1]
[f2]
NOT SPLIT
## END
## status: 0
## timeout: 10

#### autoconf arithmetic - relaxed eval_unsafe_arith (#1450)

as_fn_arith ()
{
    as_val=$(( $* ))
}
as_fn_arith 1 + 1
echo $as_val
## stdout: 2
## status: 0
## timeout: 10

#### command execution $(echo 42 | tee PWNED) not allowed

rm -f PWNED

x='a[$(echo 42 | tee PWNED)]=1'
echo $(( x ))

if test -f PWNED; then
  cat PWNED
else
  echo NOPE
fi
## STDOUT:
1
42
## END
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### process sub <(echo 42 | tee PWNED) not allowed

rm -f PWNED

x='a[<(echo 42 | tee PWNED)]=1'
echo $(( x ))

if test -f PWNED; then
  cat PWNED
else
  echo NOPE
fi



# bash keeps going
## stdout: NOPE
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset doesn't allow command execution

typeset -a a  # for mksh
a=(42)
echo len=${#a[@]}

unset -v 'a[$(echo 0 | tee PWNED)]'
echo len=${#a[@]}

if test -f PWNED; then
  echo PWNED
  cat PWNED
else
  echo NOPE
fi
## STDOUT:
len=1
len=0
PWNED
0
## END
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### printf integer size bug

# from Koiche on Zulip

printf '%x\n' 2147483648
printf '%u\n' 2147483648
## STDOUT:
80000000
2147483648
## END
## status: 0
## timeout: 10

#### autotools as_fn_arith bug in configure

# Causes 'grep -e' check to infinite loop.
# Reduced from a configure script.

as_fn_arith() {
  as_val=$(( $* ))
}

as_fn_arith 0 + 1
echo as_val=$as_val
## stdout: as_val=1
## status: 0
## timeout: 10

#### Crash in {1..10} - issue #2296

{1..10}
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### file with NUL byte
echo -e 'echo one \0 echo two' > tmp.sh
$SH tmp.sh
## stdout:
## status: 126
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### fastlex: PS1 format string that's incomplete / with NUL byte
case $SH in bash) exit ;; esac

x=$'\\D{%H:%M'  # leave off trailing }
echo x=${x@P}


# bash just ignores the missing }

# These shells don't understand @P
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### subshell while running a script (regression)
# Ensures that spawning a subshell doesn't cause a seek on the file input stream
# representing the current script (issue #1233).
cat >tmp.sh <<'EOF'
echo start
(:)
echo end
EOF
$SH tmp.sh
## STDOUT:
start
end
## END
## status: 0
## timeout: 10
## real-launcher

#### for loop (issue #1446)
case $SH in dash|mksh|ash) exit ;; esac

for (( n=0; n<(3-(1)); n++ )) ; do echo $n; done
## STDOUT:
0
1
## END
## status: 0
## timeout: 10
## real-launcher

#### for loop 2 (issue #1446)
case $SH in dash|mksh|ash) exit ;; esac


for (( n=0; n<(3- (1)); n++ )) ; do echo $n; done
## STDOUT:
0
1
## END
## status: 0
## timeout: 10
## real-launcher

#### (( status bug
case $SH in dash|ash) exit ;; esac

# from Koiche on Zulip

(( 1 << 32 ))
echo status=$?

(( 1 << 32 )) && echo yes
## STDOUT:
status=0
yes
## END
## status: 0
## timeout: 10
## real-launcher

#### OSH can use ARGV name
case $SH in dash|ash) exit ;; esac

foo() {
  if test -v ARGV; then
    echo 'BUG local'
  fi
  ARGV=( a b )
  echo len=${#ARGV[@]}
}

if test -v ARGV; then
  echo 'BUG global'
fi
foo
## stdout: len=2
## status: 0
## timeout: 10
## real-launcher

#### #2640 Hanging when reading from character devices
# Relies on timeout from GNU coreutils
timeout 0.5 $SH -c 'cat /dev/zero | cat | head -c 5 | tr \\0 0; echo'

# The exact cause of the issue is:
#
#  $ cat /dev/zero | cat | head -c 5
#
# A pipe through cat is necessary and the first cat MUST be reading from a
# character device.

# Other cases without the bug above
printf '\0\0\0\0\0\0\0' > tmp.txt
$SH -c 'cat </dev/zero | head -c 5 | tr \\0 0; echo'
$SH -c 'cat /dev/zero | head -c 5 | tr \\0 0; echo'
$SH -c 'cat tmp.txt | cat | head -c 5 | tr \\0 0; echo'
## STDOUT:
00000
00000
00000
00000
## END
## status: 0
## XFAIL ps-bash gap-oils-bugs bugs (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
