## Imported from the Oils spec tests (posix.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Empty for loop is allowed
set -- a b
for x in; do
  echo hi
  echo $x
done
## stdout:
## status: 0
## timeout: 10

#### Empty for loop without in.  Do can be on the same line I guess.
set -- a b
for x do
  echo hi
  echo $x
done
## STDOUT:
hi
a
hi
b
## END
## status: 0
## timeout: 10

#### Empty case statement
case foo in
esac
## stdout:
## status: 0
## timeout: 10

#### Last case without ;;
foo=a
case $foo in
  a) echo A ;;
  b) echo B  
esac
## stdout: A
## status: 0
## timeout: 10

#### Only case without ;;
foo=a
case $foo in
  a) echo A
esac
## stdout: A
## status: 0
## timeout: 10

#### Case with optional (
foo=a
case $foo in
  (a) echo A ;;
  (b) echo B  
esac
## stdout: A
## status: 0
## timeout: 10

#### Empty action for case is syntax error
# POSIX grammar seems to allow this, but bash and dash don't.  Need ;;
foo=a
case $foo in
  a)
  b)
    echo A ;;
  d)
esac
## stdout:
## status: 2
## timeout: 10

#### Empty action is allowed for last case
foo=b
case $foo in
  a) echo A ;;
  b)
esac
## stdout:
## status: 0
## timeout: 10

#### Case with | pattern
foo=a
case $foo in
  a|b) echo A ;;
  c)
esac
## stdout: A
## status: 0
## timeout: 10

#### Bare semi-colon not allowed
# This is disallowed by the grammar; bash and dash don't accept it.
;



#
# Explicit tests
#
## stdout:
## status: 2
## timeout: 10

#### Command substitution in default
echo ${x:-$(ls -d /bin)}
## stdout: /bin
## status: 0
## XFAIL ps-bash gap-oils-posix posix (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Arithmetic expansion
x=3
while [ $x -gt 0 ]
do
  echo $x
  x=$(($x-1))
done
## STDOUT:
3
2
1
## END
## status: 0
## timeout: 10

#### Newlines in compound lists
x=3
while
  # a couple of <newline>s

  # a list
  date && ls -d /bin || echo failed; cat tests/hello.txt
  # a couple of <newline>s

  # another list
  wc tests/hello.txt > _tmp/posix-compound.txt & true

do
  # 2 lists
  ls -d /bin
  cat tests/hello.txt
  x=$(($x-1))
  [ $x -eq 0 ] && break
done
# Not testing anything but the status since output is complicated
## STDOUT:
Sun Oct 25 11:59:44 CDT 2026
/bin
/bin
Sun Oct 25 11:59:44 CDT 2026
/bin
/bin
Sun Oct 25 11:59:44 CDT 2026
/bin
/bin
## END
## status: 0
## XFAIL ps-bash gap-oils-posix posix (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Multiple here docs on one line
cat <<EOF1; cat <<EOF2
one
EOF1
two
EOF2
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### cat here doc; echo; cat here doc
cat <<EOF1; echo two; cat <<EOF2
one
EOF1
three
EOF2
## STDOUT:
one
two
three
## END
## status: 0
## timeout: 10
