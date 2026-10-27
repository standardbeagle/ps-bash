## Imported from the Oils spec tests (ysh-builtin-private.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### type and command builtin don't find private sleep

remove-path() { sed 's;/.*/;;'; }

type -t sleep
type sleep | remove-path
echo

# this is meant to find the "first word"
type -a sleep | remove-path | uniq
echo

command -v sleep | remove-path
## STDOUT:
file
sleep is sleep

sleep is sleep

sleep
## END
## status: 0
## timeout: 10

#### sleep without prefix is still external

# should not work
builtin sleep --version
if test "$?" != '0'; then
  echo ok
fi

sleep --version | head -n 1 >& 2
echo status=$?
## STDOUT:
ok
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### builtin rm -f - still fails when file can't be removed

mkdir read-only
touch read-only/stuck
chmod -w read-only
ls -l stuck

touch foo bar
$prefix rm -- read-only/stuck foo bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi

ls read-only/stuck

touch foo bar
$prefix rm -- read-only/stuck foo bar
echo status=$?

# Clean up for real
chmod +w read-only
$prefix rm -- read-only/stuck
echo status=$?

if test -f read-only/stuck; then echo fail; fi
## STDOUT:
status=1
read-only/stuck
status=1
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### compgen -A builtin doesn't find private builtins

compgen -A builtin slee
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### invoke usage
case $SH in bash) exit ;; esac

invoke
echo status=$?

invoke --
echo status=$?

echo

invoke sleep 0
echo status=$?

invoke -- sleep 0
echo status=$?

invoke --builtin -- sleep 0
echo status=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### invoke nonexistent name
case $SH in bash) exit ;; esac

invoke zz
echo status=$?

invoke --builtin zz
echo status=$?

invoke --builtin -- zz
echo status=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### invoke fails to find tools
case $SH in bash) exit ;; esac

invoke --builtin ls
echo status=$?

invoke --sh-func ls
echo status=$?

invoke --proc ls
echo status=$?

invoke --extern zzz
echo status=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### invoke --proc doesn't run shell functions
case $SH in bash) exit ;; esac

true() {
  echo sh-func true
}

invoke --proc true
echo status=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### but invoke --show finds the private builtin
case $SH in bash) exit ;; esac

invoke --show sleep | grep builtin
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### invoke --show does proper quoting
case $SH in bash) exit ;; esac

alias $'bad-alias=echo \xff\necho z'

bad-alias | od -A n -t x1

# sed hack for test framework bug
invoke --show bad-alias | sed 's/#.qtt8/%.qtt8/'

#alias $'bad=hi\xffname=echo hi'
#$'bad\xffname'
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin sleep behaves like external sleep
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

$prefix sleep
if test "$?" != 0; then
  echo ok
fi

# This is different!  OSH is stricter
if false; then
$prefix sleep --
if test "$?" != 0; then
  echo ok
fi
fi

$prefix sleep -2
if test "$?" != 0; then
  echo ok
fi

$prefix sleep -- -2
if test "$?" != 0; then
  echo ok
fi

$prefix sleep zz
if test "$?" != 0; then
  echo ok
fi

$prefix sleep 0
echo status=$?

$prefix sleep -- 0
echo status=$?

$prefix sleep '0.0005'
echo status=$?

$prefix sleep '+0.0005'
echo status=$?
## STDOUT:
ok
ok
ok
ok
status=0
status=0
status=0
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin sleep usage errors
case $SH in bash) exit ;; esac

builtin sleep 0.5s
echo status=$?

builtin sleep 0.1 extra
echo status=$?
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin cat

case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

seq 2 3 | $prefix cat
echo ---

# from file
#echo FOO > foo
#$prefix cat foo foo
## STDOUT:
2
3
---
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin cat usage
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

$prefix cat --bad >/dev/null
if test "$?" != 0; then
  echo ok
fi

$prefix cat -z
if test "$?" != 0; then
  echo ok
fi

seq 3 4 | $prefix cat --
echo status=$?
## STDOUT:
ok
ok
3
4
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin cat non nonexistent file
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

echo FOO > foo

$prefix cat foo nonexistent foo
echo status=$?
## STDOUT:
FOO
FOO
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin cat accept - for stdin
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

echo FOO > foo
seq 3 4 | $prefix cat foo - foo foo

echo ---

# second - is a no-op
seq 5 6 | $prefix cat - -
## STDOUT:
FOO
3
4
FOO
FOO
---
5
6
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin rm: usage, removing files
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

$prefix rm 
if test "$?" != 0; then echo ok; fi

$prefix rm --
if test "$?" != 0; then echo ok; fi

$prefix rm -- nonexistent
echo status=$?

touch foo bar
$prefix rm -- foo bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi
## STDOUT:
ok
ok
status=1
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin rm -f - ignores arguments that don't exist
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

touch foo bar
$prefix rm -- foo OOPS bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi

touch foo bar
$prefix rm -f -- foo OOPS bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi
## STDOUT:
status=1
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin rm -f allows empty arg list
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

$prefix rm -f
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10
## real-launcher

#### builtin rm always fails on directories (regardless of -f)
case $SH in
  *osh) prefix='builtin' ;;
  *) prefix='' ;;
esac

touch foo bar
mkdir -p tmp
$prefix rm -- foo tmp bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi

touch foo bar
mkdir -p tmp
$prefix rm -f -- foo tmp bar
echo status=$?

if test -f foo; then echo fail; fi
if test -f bar; then echo fail; fi
## STDOUT:
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### builtin readlink
case $SH in bash) exit ;; esac

echo TODO

# turn this into a builtin
# does that mean any builtin can be externalized?
# - [ aka test is a good candiate
# - we have stubs from true/false
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-ysh-builtin-private ysh-builtin-private (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
