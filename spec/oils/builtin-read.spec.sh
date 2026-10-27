## Imported from the Oils spec tests (builtin-read.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### read line from here doc

# NOTE: there are TABS below
read x <<EOF
A		B C D E
FG
EOF
echo "[$x]"
## stdout: [A		B C D E]
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read /dev/null
read -n 1 </dev/null
echo $?
## stdout: 1
## status: 0
## timeout: 10

#### read with zero args
echo | read
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### read builtin with no newline returns status 1

# This is odd because the variable is populated successfully.  OSH/YSH might
# need a separate put reading feature that doesn't use IFS.

echo -n ZZZ | { read x; echo status=$?; echo $x; }
## STDOUT:
status=1
ZZZ
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read builtin splits value across multiple vars
# NOTE: there are TABS below
read x y z <<EOF
A		B C D E 
FG
EOF
echo "[$x/$y/$z]"
## stdout: [A/B/C D E]
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read builtin with too few variables
set -o errexit
set -o nounset  # hm this doesn't change it
read x y z <<EOF
A B
EOF
echo /$x/$y/$z/
## stdout: /A/B//
## status: 0
## timeout: 10

#### read -n with invalid arg
read -n not_a_number
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### read without args uses $REPLY, no splitting occurs (without -n)

# mksh and zsh implement splitting with $REPLY, bash/ash don't

echo '  a b  ' | (read; echo "[$REPLY]")
echo '  a b  ' | (read myvar; echo "[$myvar]")

echo '  a b  \
  line2' | (read; echo "[$REPLY]")
echo '  a b  \
  line2' | (read myvar; echo "[$myvar]")

# Now test with -r
echo '  a b  \
  line2' | (read -r; echo "[$REPLY]")
echo '  a b  \
  line2' | (read -r myvar; echo "[$myvar]")
## STDOUT:
[  a b  ]
[a b]
[  a b    line2]
[a b    line2]
[  a b  \]
[a b  \]
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read multiple vars spanning many lines
read x y << 'EOF'
one-\
two three-\
four five-\
six
EOF
argv.py "$x" "$y" "$z"
## stdout:
## status: 127
## timeout: 10

#### read with IFS=$'\n'
# The leading spaces are stripped if they appear in IFS.
IFS=$(echo -e '\n')
read var <<EOF
  a b c
  d e f
EOF
echo "[$var]"
## stdout: [  a b c]
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read with IFS=''
IFS=''
read x y <<EOF
  a b c d
EOF
echo "[$x|$y]"
## stdout: [  a b c d|]
## status: 0
## timeout: 10

#### dynamic scope used to set vars
f() {
  read head << EOF
ref: refs/heads/dev/andy
EOF
}
f
echo $head
## stdout: ref: refs/heads/dev/andy
## status: 0
## timeout: 10

#### read -d : (colon-separated records)
printf a,b,c:d,e,f:g,h,i | {
  IFS=,
  read -d : v1
  echo "v1=$v1"
  read -d : v1 v2
  echo "v1=$v1 v2=$v2"
  read -d : v1 v2 v3
  echo "v1=$v1 v2=$v2 v3=$v3"
}
## STDOUT:
v1=a,b,c
v1=d v2=e,f
v1=g v2=h v3=i
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read -d '' (null-separated records)
printf 'a,b,c\0d,e,f\0g,h,i' | {
  IFS=,
  read -d '' v1
  echo "v1=$v1"
  read -d '' v1 v2
  echo "v1=$v1 v2=$v2"
  read -d '' v1 v2 v3
  echo "v1=$v1 v2=$v2 v3=$v3"
}
## STDOUT:
v1=a,b,c
v1=d v2=e,f
v1=g v2=h v3=i
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read -rd
read -rd '' var <<EOF
foo
bar
EOF
echo "$var"
## STDOUT:
foo
bar
## END
## status: 0
## timeout: 10

#### read -d when there's no delimiter
{ read -d : part
  echo $part $?
  read -d : part
  echo $part $?
} <<EOF
foo:bar
EOF
## STDOUT:
foo 0
bar 1
## END
## status: 0
## timeout: 10

#### read -t -0.5 is invalid
# bash appears to just take the absolute value?

read -t -0.5 < /dev/null
echo $?
## stdout: 1
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### read -u syntax error
read -u -3
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### read usage
read -n -1
echo status=$?
# zsh gives a fatal error?  seems inconsistent
## stdout: status=1
## status: 0
## timeout: 10

#### read with smooshed args
echo hi | { read -rn1 var; echo var=$var; }
## stdout: var=h
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "backslash + newline" should be swallowed regardless of "-d <delim>"

printf '%s\n' 'a b\' 'c d' | (read; argv.py "$REPLY")
printf '%s\n' 'a b\,c d'   | (read; argv.py "$REPLY")
printf '%s\n' 'a b\' 'c d' | (read -d ,; argv.py "$REPLY")
printf '%s\n' 'a b\,c d'   | (read -d ,; argv.py "$REPLY")

# mksh/zsh swallows "backslash + delim" instead.
['a bc d']
['a b,c d']
['a b\nc d']
['a bc d']
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### IFS='x ' read a b: trailing spaces (with max_split)
echo 'hello world  test   ' | (read a b; argv.py "$a" "$b")
echo '-- IFS=x --'
IFS='x '
echo 'a ax  x  '     | (read a b; argv.py "$a" "$b")
echo 'a ax  x  x'    | (read a b; argv.py "$a" "$b")
echo 'a ax  x  x  '  | (read a b; argv.py "$a" "$b")
echo 'a ax  x  x  a' | (read a b; argv.py "$a" "$b")
## stdout: -- IFS=x --
## status: 127
## timeout: 10

#### IFS='x ' incomplete backslash
echo ' a b \' | (read a; argv.py "$a")
echo ' a b \' | (read a b; argv.py "$a" "$b")
IFS='x '
echo $'a ax  x    \\\nhello' | (read a b; argv.py "$a" "$b")
## stdout:
## status: 127
## timeout: 10

#### IFS='\ ' and backslash escaping
IFS='\ '
echo "hello\ world  test" | (read a b; argv.py "$a" "$b")
IFS='\'
echo "hello\ world  test" | (read a b; argv.py "$a" "$b")
# In mksh/zsh, IFS='\' is stronger than backslash escaping
## stdout:
## status: 127
## timeout: 10

#### max_split and backslash escaping
echo 'Aa b \ a\ b' | (read a b; argv.py "$a" "$b")
echo 'Aa b \ a\ b' | (read a b c; argv.py "$a" "$b" "$c")
echo 'Aa b \ a\ b' | (read a b c d; argv.py "$a" "$b" "$c" "$d")
## stdout:
## status: 127
## timeout: 10

#### IFS=x read a b <<< xxxxxx
IFS='x '
echo x     | (read a b; argv.py "$a" "$b")
echo xx    | (read a b; argv.py "$a" "$b")
echo xxx   | (read a b; argv.py "$a" "$b")
echo xxxx  | (read a b; argv.py "$a" "$b")
echo xxxxx | (read a b; argv.py "$a" "$b")
echo '-- spaces --'
echo 'x    ' | (read a b; argv.py "$a" "$b")
echo 'xx   ' | (read a b; argv.py "$a" "$b")
echo 'xxx  ' | (read a b; argv.py "$a" "$b")
echo 'xxxx ' | (read a b; argv.py "$a" "$b")
echo 'xxxxx' | (read a b; argv.py "$a" "$b")
echo '-- with char --'
echo 'xa    ' | (read a b; argv.py "$a" "$b")
echo 'xax   ' | (read a b; argv.py "$a" "$b")
echo 'xaxx  ' | (read a b; argv.py "$a" "$b")
echo 'xaxxx ' | (read a b; argv.py "$a" "$b")
echo 'xaxxxx' | (read a b; argv.py "$a" "$b")
['', '']
['', 'x']
['', 'xx']
['', 'xxx']
['', 'xxxx']
-- spaces --
['', '']
['', 'x']
['', 'xx']
['', 'xxx']
['', 'xxxx']
-- with char --
['', 'a']
['', 'ax']
['', 'axx']
['', 'axxx']
['', 'axxxx']
## STDOUT:
-- spaces --
-- with char --
## END
## status: 127
## timeout: 10

#### read and "\ "

IFS='x '
check() { echo "$1" | (read a b; argv.py "$a" "$b"); }

echo '-- xs... --'
check 'x '
check 'x \ '
check 'x \ \ '
check 'x \ \ \ '
echo '-- xe... --'
check 'x\ '
check 'x\ \ '
check 'x\ \ \ '
check 'x\  '
check 'x\  '
check 'x\    '

# check 'xx\ '
# check 'xx\ '

-- xs... --
['', '']
['', '']
['', '']
['', '']
-- xe... --
['', '']
['', '']
['', '']
['', '']
['', '']
['', '']
## STDOUT:
-- xs... --
-- xe... --
## END
## status: 127
## timeout: 10

#### read bash bug
IFS='x '
echo 'x\  \ ' | (read a b; argv.py "$a" "$b")
['', '  ']
['', '']
['', ' ']
## stdout:
## status: 127
## timeout: 10

#### read -n doesn't strip whitespace (bug fix)
case $SH in dash|zsh) exit ;; esac

echo '  a b  ' | (read -n 4; echo "[$REPLY]")
echo '  a b  ' | (read -n 5; echo "[$REPLY]")
echo '  a b  ' | (read -n 6; echo "[$REPLY]")
echo

echo 'one var strips whitespace'
echo '  a b  ' | (read -n 4 myvar; echo "[$myvar]")
echo '  a b  ' | (read -n 5 myvar; echo "[$myvar]")
echo '  a b  ' | (read -n 6 myvar; echo "[$myvar]")
echo

echo 'three vars'
echo '  a b  ' | (read -n 4 x y z; echo "[$x] [$y] [$z]")
echo '  a b  ' | (read -n 5 x y z; echo "[$x] [$y] [$z]")
echo '  a b  ' | (read -n 6 x y z; echo "[$x] [$y] [$z]")
## STDOUT:
[  a ]
[  a b]
[  a b ]

one var strips whitespace
[a]
[a b]
[a b]

three vars
[a] [] []
[a] [b] []
[a] [b] []
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -d -n - respects delimiter and splits

case $SH in dash|zsh|ash) exit ;; esac

echo 'delim c'
echo '  a b c ' | (read -d 'c' -n 3; echo "[$REPLY]")
echo '  a b c ' | (read -d 'c' -n 4; echo "[$REPLY]")
echo '  a b c ' | (read -d 'c' -n 5; echo "[$REPLY]")
echo

echo 'one var'
echo '  a b c ' | (read -d 'c' -n 3 myvar; echo "[$myvar]")
echo '  a b c ' | (read -d 'c' -n 4 myvar; echo "[$myvar]")
echo '  a b c ' | (read -d 'c' -n 5 myvar; echo "[$myvar]")
echo

echo 'three vars'
echo '  a b c ' | (read -d 'c' -n 3 x y z; echo "[$x] [$y] [$z]")
echo '  a b c ' | (read -d 'c' -n 4 x y z; echo "[$x] [$y] [$z]")
echo '  a b c ' | (read -d 'c' -n 5 x y z; echo "[$x] [$y] [$z]")
## STDOUT:
delim c
[  a]
[  a ]
[  a b]

one var
[a]
[a]
[a b]

three vars
[a] [] []
[a] [] []
[a] [b] []
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -n from pipe
case $SH in dash|ash|zsh) exit ;; esac

echo abcxyz | { read -n 3; echo reply=$REPLY; }

# zsh appears to hang with -k
## stdout: reply=abc
## status: 0
## timeout: 10
## real-launcher

#### read -s from pipe, not a terminal
case $SH in dash|zsh) exit ;; esac

# It's hard to really test this because it requires a terminal.  We hit a
# different code path when reading through a pipe.  There can be bugs there
# too!

echo foo | { read -s; echo $REPLY; }
echo bar | { read -n 2 -s; echo $REPLY; }

# Hm no exit 1 here?  Weird
echo b | { read -n 2 -s; echo $?; echo $REPLY; }
## STDOUT:
foo
ba
0
b
## END
## status: 0
## timeout: 10
## real-launcher

#### read -a reads into array

# read -a is used in bash-completion
# none of these shells implement it
case $SH in
  *mksh|*dash|*zsh|*/ash)
    exit 2;
    ;;
esac

read -a myarray <<'EOF'
a b c\ d
EOF
argv.py "${myarray[@]}"

# arguments are ignored here
read -r -a array2 extra arguments <<'EOF'
a b c\ d
EOF
argv.py "${array2[@]}"
argv.py "${extra[@]}"
argv.py "${arguments[@]}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### read -t 0 tests if input is available
case $SH in dash|zsh|mksh) exit ;; esac

# is there input available?
read -t 0 < /dev/null
echo $?

# floating point
read -t 0.0 < /dev/null
echo $?

# floating point
echo foo | { read -t 0; echo reply=$REPLY; }
echo $?
## STDOUT:
0
0
reply=
0
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -t 0.5
case $SH in dash) exit ;; esac

read -t 0.5 < /dev/null
echo $?
## stdout: 1
## status: 0
## timeout: 10
## real-launcher

#### read -u
case $SH in dash|mksh) exit ;; esac

# file descriptor
read -u 3 3<<EOF
hi
EOF
echo reply=$REPLY
## stdout: reply=hi
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -u -s
case $SH in dash|mksh) exit ;; esac

# file descriptor
read -s -u 3 3<<EOF
hi
EOF
echo reply=$REPLY
## stdout: reply=hi
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -u 3 -d 5
case $SH in dash|mksh) exit ;; esac

# file descriptor
read -u 3 -d 5 3<<EOF
123456789
EOF
echo reply=$REPLY
## stdout: reply=1234
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -u 3 -d b -N 6
case $SH in ash|zsh) exit ;; esac

# file descriptor
read -u 3 -d b -N 4 3<<EOF
ababababa
EOF
echo reply=$REPLY
# test end on EOF
read -u 3 -d b -N 6 3<<EOF
ab
EOF
echo reply=$REPLY
## STDOUT:
reply=abab
reply=ab
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -N doesn't respect delimiter, while read -n does
case $SH in dash|zsh|ash) exit ;; esac

echo foobar | { read -n 5 -d b; echo $REPLY; }
echo foobar | { read -N 5 -d b; echo $REPLY; }
## STDOUT:
foo
fooba
## END
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -p (not fully tested)

# hm DISABLED if we're not going to the terminal
# so we're only testing that it accepts the flag here

case $SH in dash|mksh|zsh) exit ;; esac

echo hi | { read -p 'P'; echo $REPLY; }
echo hi | { read -p 'P' -n 1; echo $REPLY; }
## STDOUT:
hi
h
## END
## status: 0
## timeout: 10
## real-launcher

#### read -r -d '' for NUL strings, e.g. find -print0


case $SH in dash|zsh|mksh) exit ;; esac  # NOT IMPLEMENTED

mkdir -p read0
cd read0
rm -f *

touch a\\b\\c\\d  # -r is necessary!

find . -type f -a -print0 | { read -r -d ''; echo "[$REPLY]"; }
## stdout: [./a\b\c\d]
## status: 0
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -n from directory

case $SH in dash|ash) return ;; esac  # not implemented

# same hanging bug
case $SH in mksh) return ;; esac

mkdir -p dir
read -n 3 x < ./dir
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10
## real-launcher

#### read -n 0
case $SH in zsh) exit 99;; esac  # read -n not implemented

echo 'a\b\c\d\e\f' | (read -n 0; argv.py "$REPLY")

# ash appears to treat 0 as unspecified
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### read -n and backslash escape
case $SH in zsh) exit 99;; esac  # read -n not implemented

echo 'a\b\c\d\e\f' | (read -n 5; argv.py "$REPLY")
echo 'a\ \ \ \ \ ' | (read -n 5; argv.py "$REPLY")
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### read -n 4 with incomplete backslash
case $SH in zsh) exit 99;; esac  # read -n not implemented

echo 'abc\def\ghijklmn' | (read -n 4; argv.py "$REPLY")
echo '   \xxx\xxxxxxxx' | (read -n 4; argv.py "$REPLY")

# bash implements "-n NUM" as number of characters
# ash implements "-n NUM" as number of bytes
['abc']
['   ']
# mksh implements "-n NUM" as number of bytes, and also "read" (without
# variable names) in mksh is equivalent to "read REPLY, i.e., consideres IFS.
['abc']
['']
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### read -n 4 with backslash + delim
case $SH in zsh) exit 99;; esac  # read -n not implemented

echo $'abc\\\ndefg' | (read -n 4; argv.py "$REPLY")

# mksh and ash implements "-n NUM" as number of bytes.
['abc']
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-builtin-read builtin-read (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### empty input and splitting
case $SH in mksh|ash|dash|zsh) exit 99; esac
echo '' | (read -a a; argv.py "${a[@]}")
IFS=x
echo '' | (read -a a; argv.py "${a[@]}")
IFS=
echo '' | (read -a a; argv.py "${a[@]}")
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### IFS='x ' read -a: trailing spaces (unlimited split)
case $SH in mksh|ash|dash|zsh) exit 99; esac
IFS='x '
echo 'a b'     | (read -a a; argv.py "${a[@]}")
echo 'a b '    | (read -a a; argv.py "${a[@]}")
echo 'a bx'    | (read -a a; argv.py "${a[@]}")
echo 'a bx '   | (read -a a; argv.py "${a[@]}")
echo 'a b x'   | (read -a a; argv.py "${a[@]}")
echo 'a b x '  | (read -a a; argv.py "${a[@]}")
echo 'a b x x' | (read -a a; argv.py "${a[@]}")
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### IFS='x ' read -a: intermediate spaces (unlimited split)
case $SH in mksh|ash|dash|zsh) exit 99; esac
IFS='x '
echo 'a x b'   | (read -a a; argv.py "${a[@]}")
echo 'a xx b'  | (read -a a; argv.py "${a[@]}")
echo 'a xxx b' | (read -a a; argv.py "${a[@]}")
echo 'a x xb'  | (read -a a; argv.py "${a[@]}")
echo 'a x x b' | (read -a a; argv.py "${a[@]}")
echo 'ax b'    | (read -a a; argv.py "${a[@]}")
echo 'ax xb'   | (read -a a; argv.py "${a[@]}")
echo 'ax  xb'  | (read -a a; argv.py "${a[@]}")
echo 'ax x xb' | (read -a a; argv.py "${a[@]}")
## stdout:
## status: 127
## timeout: 10
## real-launcher
