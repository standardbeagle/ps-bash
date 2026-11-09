## Imported from the Oils spec tests (builtin-getopts.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### getopts empty
set -- 
getopts 'a:' opt
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=1 opt=? OPTARG=
## status: 0
## timeout: 10

#### getopts sees unknown arg
set -- -Z
getopts 'a:' opt
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=0 opt=? OPTARG=
## status: 0
## timeout: 10

#### getopts three invocations
set -- -h -c foo
getopts 'hc:' opt
echo status=$? opt=$opt
getopts 'hc:' opt
echo status=$? opt=$opt
getopts 'hc:' opt
echo status=$? opt=$opt
## STDOUT:
status=0 opt=h
status=0 opt=c
status=1 opt=?
## END
## status: 0
## timeout: 10

#### getopts resets OPTARG
set -- -c foo -h
getopts 'hc:' opt
echo status=$? opt=$opt OPTARG=$OPTARG
getopts 'hc:' opt
echo status=$? opt=$opt OPTARG=$OPTARG
## STDOUT:
status=0 opt=c OPTARG=foo
status=0 opt=h OPTARG=
## END
## status: 0
## timeout: 10

#### OPTARG is empty (not unset) after parsing a flag doesn't take an arg

set -u
getopts 'ab' name '-a'
echo name=$name
echo OPTARG=$OPTARG
## stdout: name=a
## status: 1
## timeout: 10

#### Basic getopts invocation
set -- -h -c foo x y z
FLAG_h=0
FLAG_c=''
while getopts "hc:" opt; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
  esac
done
shift $(( OPTIND - 1 ))
echo h=$FLAG_h c=$FLAG_c optind=$OPTIND argv=$@
## stdout: h=1 c=foo optind=4 argv=x y z
## status: 0
## timeout: 10

#### getopts with invalid variable name
set -- -c foo -h
getopts 'hc:' opt-
echo status=$? opt=$opt OPTARG=$OPTARG OPTIND=$OPTIND
## stdout: status=1 opt= OPTARG=foo OPTIND=3
## status: 0
## timeout: 10

#### getopts with invalid flag
set -- -h -x
while getopts "hc:" opt; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
    '?') echo ERROR $OPTIND; exit 2; ;;
  esac
done
echo status=$?
## stdout: ERROR 3
## status: 2
## timeout: 10

#### getopts with with -
set -- -h -
echo "$@"
while getopts "hc:" opt; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
    '?') echo ERROR $OPTIND; exit 2; ;;
  esac
done
echo status=$?
## STDOUT:
-h -
status=0
## END
## status: 0
## timeout: 10

#### getopts missing required argument
set -- -h -c
while getopts "hc:" opt; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
    '?') echo ERROR $OPTIND; exit 2; ;;
  esac
done
echo status=$?
## stdout: ERROR 3
## status: 2
## timeout: 10

#### getopts doesn't look for flags after args
set -- x -h -c y
FLAG_h=0
FLAG_c=''
while getopts "hc:" opt; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
  esac
done
shift $(( OPTIND - 1 ))
echo h=$FLAG_h c=$FLAG_c optind=$OPTIND argv=$@
## stdout: h=0 c= optind=1 argv=x -h -c y
## status: 0
## timeout: 10

#### getopts with explicit args
# NOTE: Alpine doesn't appear to use this, but bash-completion does.
FLAG_h=0
FLAG_c=''
arg=''
set -- A B C
while getopts "hc:" opt -h -c foo x y z; do
  case $opt in
    h) FLAG_h=1 ;;
    c) FLAG_c="$OPTARG" ;;
  esac
done
echo h=$FLAG_h c=$FLAG_c optind=$OPTIND argv=$@
## stdout: h=1 c=foo optind=4 argv=A B C
## status: 0
## timeout: 10

#### OPTIND
echo $OPTIND
## stdout: 1
## status: 0
## timeout: 10

#### OPTIND after multiple getopts with same spec
while getopts "hc:" opt; do
  echo '-'
done
echo OPTIND=$OPTIND

set -- -h -c foo x y z
while getopts "hc:" opt; do
  echo '-'
done
echo OPTIND=$OPTIND

set --
while getopts "hc:" opt; do
  echo '-'
done
echo OPTIND=$OPTIND
## STDOUT:
OPTIND=1
-
-
OPTIND=4
OPTIND=1
## END
## status: 0
## timeout: 10

#### OPTIND after multiple getopts with different spec
# Wow this is poorly specified!  A fundamental design problem with the global
# variable OPTIND.
set -- -a
while getopts "ab:" opt; do
  echo '.'
done
echo OPTIND=$OPTIND

set -- -c -d -e foo
while getopts "cde:" opt; do
  echo '-'
done
echo OPTIND=$OPTIND

set -- -f
while getopts "f:" opt; do
  echo '_'
done
echo OPTIND=$OPTIND
## STDOUT:
.
OPTIND=2
-
-
OPTIND=5
OPTIND=2
## END
## status: 0
## timeout: 10

#### OPTIND narrowed down
FLAG_a=
FLAG_b=
FLAG_c=
FLAG_d=
FLAG_e=
set -- -a
while getopts "ab:" opt; do
  case $opt in
    a) FLAG_a=1 ;;
    b) FLAG_b="$OPTARG" ;;
  esac
done
# Bash doesn't reset OPTIND!  It skips over c!  mksh at least warns about this!
# You have to reset OPTIND yourself.

set -- -c -d -e E
while getopts "cde:" opt; do
  case $opt in
    c) FLAG_c=1 ;;
    d) FLAG_d=1 ;;
    e) FLAG_e="$OPTARG" ;;
  esac
done

echo a=$FLAG_a b=$FLAG_b c=$FLAG_c d=$FLAG_d e=$FLAG_e
## stdout: a=1 b= c= d=1 e=E
## status: 0
## timeout: 10

#### Getopts parses the function's arguments
FLAG_h=0
FLAG_c=''
myfunc() {
  while getopts "hc:" opt; do
    case $opt in
      h) FLAG_h=1 ;;
      c) FLAG_c="$OPTARG" ;;
    esac
  done
}
set -- -h -c foo x y z
myfunc -c bar
echo h=$FLAG_h c=$FLAG_c opt=$opt optind=$OPTIND argv=$@
## stdout: h=0 c=bar opt=? optind=3 argv=-h -c foo x y z
## status: 0
## timeout: 10

#### Local OPTIND
# minimal test case extracted from bash-completion
min() {
  local OPTIND=1

  while getopts "n:e:o:i:s" flag "$@"; do
    echo "loop $OPTIND";
  done
}
min -s
## stdout: loop 2
## status: 0
## timeout: 10

#### two flags: -ab
getopts "ab" opt -ab
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "ab" opt -ab
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
## STDOUT:
OPTIND=1 opt=a OPTARG=
OPTIND=2 opt=b OPTARG=
## END
## status: 0
## timeout: 10

#### flag and arg: -c10
getopts "c:" opt -c10
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "c:" opt -c10
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
## STDOUT:
OPTIND=2 opt=c OPTARG=10
OPTIND=2 opt=? OPTARG=
## END
## status: 0
## timeout: 10

#### More Smooshing 1
getopts "ab:c:" opt -ab hi -c hello
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "ab:c:" opt -ab hi -c hello
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "ab:c:" opt -ab hi -c hello
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
## STDOUT:
OPTIND=1 opt=a OPTARG=
OPTIND=3 opt=b OPTARG=hi
OPTIND=5 opt=c OPTARG=hello
## END
## status: 0
## timeout: 10

#### More Smooshing 2
getopts "abc:" opt -abc10
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "abc:" opt -abc10
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
getopts "abc:" opt -abc10
echo OPTIND=$OPTIND opt=$opt OPTARG=$OPTARG
## STDOUT:
OPTIND=1 opt=a OPTARG=
OPTIND=1 opt=b OPTARG=
OPTIND=2 opt=c OPTARG=10
## END
## status: 0
## timeout: 10

#### OPTIND should be >= 1 (regression)
OPTIND=-1
getopts a: foo
echo status=$?

OPTIND=0
getopts a: foo
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### getopts silent error reporting - invalid option
# Leading : in optspec enables silent mode: OPTARG=option char, no error msg
set -- -Z
getopts ':a:' opt 2>&1
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=0 opt=? OPTARG=Z
## status: 0
## timeout: 10

#### getopts silent error reporting - missing required argument
# Silent mode returns ':' and sets OPTARG to option char
set -- -a
getopts ':a:' opt 2>&1
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=0 opt=: OPTARG=a
## status: 0
## timeout: 10

#### getopts normal mode - invalid option (compare with silent)
# Normal mode: OPTARG is empty, prints error message
set -- -Z
getopts 'a:' opt 2>/dev/null
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=0 opt=? OPTARG=
## status: 0
## timeout: 10

#### getopts normal mode - missing required argument (compare with silent)
# Normal mode returns '?', OPTARG is empty
set -- -a
getopts 'a:' opt 2>/dev/null
echo "status=$? opt=$opt OPTARG=$OPTARG"
## stdout: status=0 opt=? OPTARG=
## status: 0
## timeout: 10

#### getopts handles '--' #2579
set -- "-a" "--"
while getopts "a" name; do
        case "$name" in
                a)
                        echo "a"
                        ;;
                ?)
                        echo "?"
                        ;;
        esac
done
echo "name=$name"
echo "$OPTIND"
## STDOUT:
a
name=?
3
## END
## status: 0
## timeout: 10

#### getopts leaves all args after '--' as operands #2579
set -- "-a" "--" "-c" "operand"
while getopts "a" name; do
    case "$name" in
        a)
            echo "a"
            ;;
        c)
            echo "c"
            ;;
        ?)
            echo "?"
            ;;
    esac
done
shift $((OPTIND - 1))
echo "$#"
echo "$@"
## STDOUT:
a
2
-c operand
## END
## status: 0
## timeout: 10
