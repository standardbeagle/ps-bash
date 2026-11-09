## Imported from the Oils spec tests (builtin-umask.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### usage: too many args

# most shells don't verify this
umask 1 2
if test $? -ne 0; then
  echo fail
fi
## stdout:
## status: 0
## timeout: 10

#### usage: empty input, space input
umask ''
case $? in
  1) echo error ;;
  2) echo error ;;
  *) echo status=$? ;;
esac

umask ' '
case $? in
  1) echo error too ;;
  2) echo error too ;;
  *) echo status=$? ;;
esac
## STDOUT:
error
error too
## END
## status: 0
## timeout: 10

#### symbolic syntax error: b=rwx
umask 0124

umask b=rwx
case $? in
  1) echo error ;;
  2) echo error ;;
  *) echo status=$? ;;
esac

umask  # make sure it hasn't changed
## STDOUT:
error
0124
## END
## status: 0
## timeout: 10

#### symbolic syntax error: start with -
umask 0124
umask -rwx
if test $? -ne 0; then echo 'error'; fi
umask | tail -c 4

umask 0124
umask -wx
if test $? -ne 0; then echo 'error'; fi
umask | tail -c 4

umask 0124
umask -=+
if test $? -ne 0; then echo 'error'; fi
umask | tail -c 4
## STDOUT:
error
124
error
124
error
124
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-umask builtin-umask (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### symbolic syntax error: empty clause u-r,,u-r
umask 0124
umask 'u-r,u-r'
echo status=$?
umask

# syntax error
umask 'u+r,,u-r'
if test $? -ne 0; then echo 'error'; fi
umask
## STDOUT:
status=0
0524
error
0524
## END
## status: 0
## timeout: 10

#### usage: invalid octal digits
umask 089
case $? in
  1) echo error ;;
  2) echo error ;;
esac
## stdout: error
## status: 0
## timeout: 10

#### usage: large octal number
umask 0022

# osh and other shells treat truncate 0o1234567 as 0o0567
umask 1234567
echo status=$?

umask
## STDOUT:
status=1
0022
## END
## status: 0
## timeout: 10

#### umask permcopy
umask 0124 
umask a=u
umask | tail -c 4

umask 0365
umask a=g
umask | tail -c 4

umask 0124
umask a=o
umask | tail -c 4
## STDOUT:
124
365
124
## END
## status: 0
## timeout: 10

#### umask permcopy running value
umask 0124
umask a=,a=u
umask | tail -c 4

umask 0124
umask a=
umask a=u
umask | tail -c 4
## STDOUT:
124
777
## END
## status: 0
## timeout: 10

#### umask sequential actions
umask 0124
umask u+r+w+x
umask | tail -c 4

umask 0124
umask a+r+w+x,o-w
umask | tail -c 4

umask 0124
umask a+x+wr-r
umask | tail -c 4
## STDOUT:
124
124
124
## END
## status: 0
## timeout: 10

#### 'umask -p' prints a form that can be eval'd
umask -p
echo status=$?
## STDOUT:
umask 0022
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-umask builtin-umask (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### set umask with symbolic mode: u-rw  ...  u=,g+,o-  ...

umask 0000
umask u-rw
echo status0=$?
umask | tail -c 4

umask 0700
umask u=r
echo status1=$?
umask | tail -c 4

umask 0000
umask u=r,g=w,o=x
echo status2=$?
umask | tail -c 4

umask 0777
umask u+r,g+w,o+x
echo status3=$?
umask | tail -c 4

umask 0000
umask u-r,g-w,o-x
echo status4=$?
umask | tail -c 4

umask 0137
umask u=,g+,o-
echo status5=$?
umask | tail -c 4
## STDOUT:
status0=0
600
status1=0
300
status2=0
356
status3=0
356
status4=0
421
status5=0
737
## END
## status: 0
## timeout: 10

#### umask with too many arguments (i.e. extra spaces)
umask 0111
# spaces are an error in bash
# dash & mksh only interpret the first one
umask u=, g+, o-
if test $? -ne 0; then
  echo ok
fi
umask | tail -c 4
## STDOUT:
ok
111
## END
## status: 0
## timeout: 10

#### umask allow overwriting and duplicates
umask 0111
umask u=rwx,u=rw,u=r,u=,g=rwx
umask | tail -c 4
## stdout: 701
## status: 0
## timeout: 10

#### umask a is valid who
umask 0732
umask a=rwx
umask | tail -c 4

umask 0124
umask a+r
umask | tail -c 4

umask 0124
umask a-r
umask | tail -c 4
## STDOUT:
000
120
564
## END
## status: 0
## timeout: 10

#### umask X perm
umask 0124
umask a=X
echo ret0 = $?
umask | tail -c 4

umask 0246
umask a=X
echo ret1 = $?
umask | tail -c 4

umask 0246
umask a-X
echo ret2 = $?
umask | tail -c 4
## STDOUT:
ret0 = 1
124
ret1 = 1
246
ret2 = 1
246
## END
## status: 0
## timeout: 10

#### umask s perm
umask 0124
umask a-s
echo ret0 = $?
umask | tail -c 4

umask 0124
umask a+s
echo ret1 = $?
umask | tail -c 4

umask 0124
umask a=s
echo ret2 = $?
umask | tail -c 4
## STDOUT:
ret0 = 1
124
ret1 = 1
124
ret2 = 1
124
## END
## status: 0
## timeout: 10

#### umask t perm
umask 0124
umask a-t
echo ret0 = $?
umask | tail -c 4

umask 0124
umask a+t
echo ret1 = $?
umask | tail -c 4

umask 0124
umask a=t
echo ret2 = $?
umask | tail -c 4
## STDOUT:
ret0 = 1
124
ret1 = 1
124
ret2 = 1
124
## END
## status: 0
## timeout: 10

#### umask default who
umask 0124
umask =
umask | tail -c 4

umask 0124
umask =rx
echo ret = $?
umask | tail -c 4

umask 0124
umask +
umask | tail -c 4

umask 0124
# zsh ALSO treats this as just `umask`
umask - >/dev/null
umask | tail -c 4
## STDOUT:
777
ret = 0
222
124
124
## END
## status: 0
## timeout: 10

#### umask bare op
umask 0124
umask =+=
umask | tail -c 4

umask 0124
umask +=
umask | tail -c 4

umask 0124
umask =+rwx+rx
umask | tail -c 4
## STDOUT:
124
124
124
## END
## status: 0
## timeout: 10
