## Imported from the Oils spec tests (builtin-process.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### exec builtin
exec echo hi
## stdout: hi
## status: 0
## timeout: 10

#### exec builtin with redirects
exec 1>&2
echo 'to stderr'
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### exec builtin accepts --
exec -- echo hi
## stdout: hi
## status: 0
## timeout: 10

#### exec -- 2>&1
exec -- 3>&1
echo stdout 1>&3
## stdout: stdout
## status: 0
## timeout: 10

#### exec -a sets argv[0]
exec -a FOOPROC sh -c 'echo $0'
## stdout: FOOPROC
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Exit out of function
f() { exit 3; }
f
exit 4
## stdout:
## status: 3
## timeout: 10

#### Exit builtin with invalid arg
exit invalid
# Rationale: runtime errors are 1
## stdout:
## status: 2
## timeout: 10

#### Exit builtin with too many args
# This is a parse error in OSH.
exit 7 8 9
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### time with brace group argument

err=time-$(basename $SH).txt
{
  time {
    sleep 0.01
    sleep 0.02
  }
} 2> $err

grep --only-matching user $err
echo result=$?

# Regression: check fractional seconds
gawk '
BEGIN { ok = 0 }
match( $0, /\.([0-9]+)/, m) {
  if (m[1] > 0) {  # check fractional seconds
    ok = 1
  }
}
END { if (ok) { print "non-zero" } }
' $err


# time doesn't accept a block?

# dash doesn't have time keyword
## STDOUT:
user
result=0
non-zero
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ulimit with no flags is like -f

ulimit > no-flags.txt
echo status=$?

ulimit -f > f.txt
echo status=$?

diff -u no-flags.txt f.txt
echo diff=$?

# Print everything
# ulimit -a
## STDOUT:
status=0
status=0
diff=0
## END
## status: 0
## timeout: 10

#### ulimit too many args

ulimit 1 2
if test $? -ne 0; then
  echo pass
else
  echo fail
fi

#ulimit -f
## stdout: fail
## status: 0
## timeout: 10

#### ulimit negative arg

ulimit -f

# an arg
ulimit -f -- -42
if test $? -ne 0; then
  echo pass
else
  echo fail
fi
## STDOUT:
unlimited
pass
## END
## status: 0
## timeout: 10

#### ulimit -a doesn't take arg
case $SH in bash) exit ;; esac

ulimit -a 42
if test $? -ne 0; then
  echo 'failure that was expected'
fi
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### ulimit doesn't accept multiple flags - reduce confusion between shells

# - bash, zsh, busybox ash accept multiple "commands", which requires custom
#   flag parsing, like

#   ulimit -f 999 -n
#   ulimit -f 999 -n 888
#
# - dash and mksh accept a single ARG
#
# we want to make it clear we're like the latter

# can't print all and -f
ulimit -f -a >/dev/null
echo status=$?

ulimit -f -n >/dev/null
echo status=$?

ulimit -f -n 999 >/dev/null
echo status=$?



# zsh is better - it checks that -a and -f are exclusive
## STDOUT:
status=0
status=0
status=0
## END
## status: 0
## timeout: 10

#### YSH readability: ulimit --all the same as ulimit -a

case $SH in bash|dash|mksh|zsh) exit ;; esac

ulimit -a > short.txt
ulimit --all > long.txt

wc -l short.txt long.txt

diff -u short.txt long.txt
echo status=$?
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### arg that would overflow 64 bits is detected

# no 64-bit integers
case $SH in mksh) exit ;; esac

echo -n 'before '; ulimit -f

# 1 << 63 overflows signed int

lim=$(( (1 << 62) + 1 ))
#echo lim=$lim

# bash detects that this is out of range
# so does osh-cpp, but not osh-cpython

ulimit -f $lim
echo -n 'after '; ulimit -f
## STDOUT:
before unlimited
after unlimited
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ulimit -f 1 prevents files larger 512 bytes
trap - XFSZ  # don't handle this

rm -f err.txt
touch err.txt

bytes() {
  local n=$1
  local st=0
  for i in $(seq $n); do
    echo -n x
    st=$?
    if test $st -ne 0; then
      echo "ERROR: echo failed with status $st" >> err.txt
    fi
  done
}

ulimit -f 1

bytes 512 > ok.txt
echo 512 status=$?

bytes 513 > too-big.txt
echo 513 status=$?
echo

wc --bytes ok.txt too-big.txt
echo

cat err.txt
## STDOUT:
512 status=0
513 status=0

 512 ok.txt
 513 too-big.txt
1025 total

## END
## status: 0
## timeout: 10

#### ulimit -S for soft limit (default), -H for hard limit
case $SH in dash|zsh) exit ;; esac

# Note: ulimit -n -S 1111 is OK in osh/dash/mksh, but not bash/zsh
# Mus be ulimit -S -n 1111

show_state() {
  local msg=$1
  echo "$msg"
  echo -n '  '; ulimit -S -t
  echo -n '  '; ulimit -H -t
  echo
}

show_state 'init'

ulimit -S -t 123456
show_state '-S'

ulimit -H -t 123457
show_state '-H'

ulimit -t 123455
show_state 'no flag'

echo 'GET'

ulimit -S -t 123454
echo -n '  '; ulimit -t
echo -n '  '; ulimit -S -t
echo -n '  '; ulimit -H -t
## STDOUT:
init
  unlimited
  unlimited

-S
  123456
  unlimited

-H
  123456
  123457

no flag
  123455
  123455

GET
  123454
  123454
  123455
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Changing resource limit is denied

# Not sure why these don't work
case $SH in dash|mksh) exit ;; esac


flag=-t

ulimit -S -H $flag 100
echo both=$?

ulimit -S $flag 90
echo soft=$?

ulimit -S $flag 95
echo soft=$?

ulimit -S $flag 105
if test $? -ne 0; then
  echo soft OK
else
  echo soft fail
fi

ulimit -H $flag 200
if test $? -ne 0; then
  echo hard OK
else
  echo hard fail
fi
## STDOUT:
both=0
soft=0
soft=0
soft OK
hard OK
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ulimit -n limits file descriptors

# OSH bug
# https://oilshell.zulipchat.com/#narrow/channel/502349-osh/topic/alpine.20build.20failures.20-.20make.20-.20ulimit.20-n.2064/with/519691301

$SH -c 'ulimit -n 64; echo hi >out'
echo status=$?

$SH -c 'ulimit -n 0; echo hi >out'
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-process builtin-process (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
