## Seed corpus: core shell semantics. Expectations are recorded from bash 5.2 (PSBASH_SPEC_RECORD=1), never hand-written.

#### echo and quoting
echo "a  b" 'c  d' e\ f
## stdout: a  b c  d e f
## status: 0

#### parameter default and alternate
unset x; echo "${x:-d}" "${x:+a}"; x=1; echo "${x:-d}" "${x:+a}"
## STDOUT:
d 
1 a
## END
## status: 0

#### quoted array expands one word per element
a=("x y" z); for i in "${a[@]}"; do echo "[$i]"; done
## STDOUT:
[x y]
[z]
## END
## status: 0

#### arithmetic
echo $((2+3*4)) $((7/2)) $((7%3))
## stdout: 14 3 1
## status: 0

#### case with variable pattern
p=b; case b in $p) echo hit;; *) echo miss;; esac
## stdout: hit
## status: 0

#### getopts cluster
set -- -ab -c val x; while getopts ab:c: o; do echo "$o=$OPTARG"; done; echo $OPTIND
## STDOUT:
a=
b=-c
3
## END
## status: 0

#### associative array
declare -A m=([k1]=v1 [k2]=v2); echo "${m[k1]}" "${#m[@]}"
## stdout: v1 2
## status: 0

#### printf %q
printf '%q\n' 'a b'
## stdout: a\ b
## status: 0

#### status of failure
false; echo $?
## stdout: 1
## status: 0

#### pipeline status of last command
echo hi | cat; echo $?
## STDOUT:
hi
0
## END
## status: 0

#### set -f disables globbing
set -f; echo *; set +f
## stdout: *
## status: 0

#### subshell param error ends only subshell
( : "${nope:?boom}" ) 2>/dev/null; echo after
## stdout: after
## status: 0

#### unset of a readonly variable fails and keeps the value
readonly v=1; unset v 2>/dev/null; echo $? $v
## stdout: 1 1
## status: 0

#### unset of a normal variable succeeds
w=1; unset w; echo $? "${w-gone}"
## stdout: 0 gone
## status: 0

#### declare -i evaluates later assignments
declare -i n=2+3; n=n+1; n=n*2; echo $n
## stdout: 12
## status: 0

#### plain variable keeps the text
m=2+3; m=m+1; echo $m
## stdout: m+1
## status: 0

#### trap ERR runs on a failing statement
trap 'echo err' ERR; false; echo done
## STDOUT:
err
done
## END
## status: 0

#### trap ERR is not run for a condition or a non-final && operand
trap 'echo err' ERR; if false; then :; fi; false && true; ! false; echo done
## stdout: done
## status: 0

#### trap ERR sees the failing status
trap 'echo "rc=$?"' ERR; (exit 3); echo after
## STDOUT:
rc=3
after
## END
## status: 0

#### trap ERR cleared
trap 'echo err' ERR; trap - ERR; false; echo done
## stdout: done
## status: 0

#### extglob alternation in case
shopt -s extglob
case abc in @(abc|x)) echo y;; *) echo n;; esac
case abd in @(abc|x)) echo y;; *) echo n;; esac
## STDOUT:
y
n
## END
## status: 0

#### extglob negation and repetition in [[ ]]
shopt -s extglob
[[ foo.c == !(*.h) ]] && echo notheader
[[ aaa == +(a) ]] && echo plus
[[ ab == ?(a)b ]] && echo opt
## STDOUT:
notheader
plus
opt
## END
## status: 0

#### nameref reads and writes its target
x=1; declare -n r=x; echo $r; r=7; echo $x "${r}"
## STDOUT:
1
7 7
## END
## status: 0

#### nameref to another script variable after the fact
a=A; b=B; declare -n p=a; echo $p; declare -n q=b; echo $q $a
## STDOUT:
A
B A
## END
## status: 0

#### flock runs the command and returns its status
d=$(mktemp -d); flock "$d/l" printf "held\n"; echo $?; rm -rf "$d"
## STDOUT:
held
0
## END
## status: 0

#### flock -c runs a command line
d=$(mktemp -d); flock "$d/l" -c "echo a; echo b"; echo $?; rm -rf "$d"
## STDOUT:
a
b
0
## END
## status: 0

#### flock -n fails while the lock is held
d=$(mktemp -d); flock "$d/l" sleep 2 & sleep 0.5; flock -n "$d/l" echo got; echo $?; wait; rm -rf "$d"
## stdout: 1
## status: 0

#### assignment to a readonly variable fails and keeps the value
readonly X=1
X=2
echo "rc=$? X=$X"
Y=3
echo Y=$Y
## STDOUT:
rc=1 X=1
Y=3
## END
## status: 0

#### umask reports and sets the mask
umask
umask -S
umask 077
umask
hash -r
echo rc=$?
## STDOUT:
0022
u=rwx,g=rx,o=rx
0077
rc=0
## END
## status: 0

#### globstar and nocaseglob
d=$(mktemp -d)
cd "$d"
mkdir -p a/b
touch x.txt a/y.txt a/b/z.txt
echo **/*.txt
shopt -s globstar
echo **/*.txt
echo **
echo a/**
echo **/z.txt
shopt -u globstar
shopt -s nocaseglob
echo X.TXT *.TXT
cd /
rm -rf "$d"
## STDOUT:
a/y.txt
a/b/z.txt a/y.txt x.txt
a a/b a/b/z.txt a/y.txt x.txt
a/ a/b a/b/z.txt a/y.txt
a/b/z.txt
X.TXT x.txt
## END
## status: 0

#### backslash before a letter is that letter (not a PowerShell escape)
echo a\bc a\nb a\ea a\tb
\echo hi
for i in 1 2 3; do echo $i; \break; done
echo end
## STDOUT:
abc anb aea atb
hi
1
end
## END
## status: 0

#### FUNCNAME is the function call stack
f() { echo "f: ${FUNCNAME[0]}"; g; }
g() { echo "g: ${FUNCNAME[0]} ${FUNCNAME[1]}"; }
f
echo "top n=${#FUNCNAME[@]}"
## STDOUT:
f: f
g: g f
top n=0
## END
## status: 0

#### PS4 defaults to "+ " and can be set
echo "[$PS4]"
PS4='>> '
echo "[$PS4]"
## STDOUT:
[+ ]
[>> ]
## END
## status: 0

#### compgen filters a word list by prefix
compgen -W "apple apricot banana" -- ap
echo rc=$?
compgen -W "x y" -- z
echo rc=$?
f1() { :; }
f2() { :; }
compgen -A function -- f
## STDOUT:
apple
apricot
rc=0
rc=1
f1
f2
## END
## status: 0

#### declare with several initializers
declare X=1 Y=2; echo "[$X] [$Y]"
declare -i P=1 Q=2+3; echo "[$P] [$Q]"
declare A="a b" B=$(echo c); echo "[$A] [$B]"
## STDOUT:
[1] [2]
[1] [5]
[a b] [c]
## END
## status: 0

#### quoted for-list items word-split when used unquoted
for c in "a b" 'c  d' e; do printf '[%s]' $c; echo; done
for c in "x y"; do set -- $c; echo "$# $1 $2"; done
for c in 1 2; do printf '<%s>' $c; done; echo
## STDOUT:
[a][b]
[c][d]
[e]
2 x y
<1><2>
## END
## status: 0

#### read variable word-splits when used unquoted
echo "p   q" | { read line; printf '[%s]' $line; echo; }
echo "p   q" | { read line; echo $line; }
## STDOUT:
[p][q]
p q
## END
## status: 0

#### command names that start with a dash are commands, not operators
--
echo rc=$?
-x foo
echo rc=$?
## STDOUT:
rc=127
rc=127
## END
## status: 0

#### indexed element assignment: variable subscript, append past the end, arithmetic subscript, +=
a=(1 2); a[2]=3; c=(); for i in 0 1 2; do c[$i]=v$i; done; j=1; e=(a b c); e[j+1]=Z; e[$j]+=Y
echo "${a[@]}" "${c[@]}" "${e[@]}"
## stdout: 1 2 3 v0 v1 v2 a bY Z
## status: 0

#### associative element assignment with a variable and a spaced key
declare -A m; k=k3; m[k1]=v1; m["k 2"]=v2; m[$k]=v3; for x in k1 "k 2" k3; do echo "$x=${m[$x]}"; done
## STDOUT:
k1=v1
k 2=v2
k3=v3
## END
## status: 0

#### array literal word-splits an unquoted command substitution and variable
a=($(printf '%s\n' c a b)); x="p q r"; g=($x); h=("$x"); i=("$(printf 'a\nb')"); u=(); u+=($(echo m n))
echo ${#a[@]} ${#g[@]} ${#h[@]} ${#i[@]} ${#u[@]}
## stdout: 3 3 1 1 2
## status: 0

#### subshell fires its own EXIT trap when it ends
( trap 'echo cleanup' EXIT; echo body )
( trap 'echo c2' EXIT; exit 3 ); echo "rc=$?"
## STDOUT:
body
cleanup
c2
rc=3
## END

#### $LINENO is the source line of the command
echo $LINENO
f() {
  echo "in f $LINENO"
}
f
echo '$LINENO' "at ${LINENO}"
## STDOUT:
1
in f 3
$LINENO at 6
## END

#### trap RETURN fires when the function returns, keeping its status
f() { trap 'echo ret' RETURN; echo in; return 4; }; f; echo "rc=$?"
g() { trap 'echo cleanup $x' RETURN; x=5; echo work; }; g; g
## STDOUT:
in
ret
rc=4
work
cleanup 5
work
cleanup 5
## END

#### an assigning word runs before a later unquoted read in the same command
n=3; echo $((n+=2)) $n; echo ${w=q} $w; echo ${z:=a} $z
## STDOUT:
5 5
q q
a a
## END

#### a for-in loop variable does not leak into later arithmetic
for i in 1 2 3; do :; done
i=0; until [ $i -ge 3 ]; do i=$((i+1)); done; echo $i
j=0; while [ $j -lt 3 ]; do j=$((j+1)); done; echo $j
## STDOUT:
3
3
## END

#### += on a declare -i variable adds; on a plain variable appends
declare -i num=5; num+=2; echo $num
declare -i k; k+=3; k+=4; echo $k
s=ab; s+=cd; echo $s
## STDOUT:
7
7
abcd
## END

#### kill -SIG $$ runs the shell's own trap and the script continues
trap 'echo got-usr1' USR1; kill -USR1 $$; echo after1
trap 'echo got-int' INT; kill -INT $$; echo after2
trap '' USR2; kill -USR2 $$; echo after3
## STDOUT:
got-usr1
after1
got-int
after2
after3
## END

#### "$*" and "${a[*]}" join with the first character of IFS, "$@" with a space
set -- a b c; arr=(p q r)
IFS=:; echo "$*"; echo "$@"; echo "${arr[*]}"; echo "${arr[@]}"
f() { local IFS=,; echo "$*"; }; f x y
unset IFS; echo "$*"
## STDOUT:
a:b:c
a b c
p:q:r
p q r
x,y
a b c
## END

#### ${x//$'\n'/,} and ${x//$nl/,} replace the quote-removed / variable operand
x=$'a\nb c'; nl=$'\n'; sep=-
echo "[${x//$'\n'/,}]"; echo "[${x//$nl/;}]"; echo "[${x// /$sep}]"; echo "[${x/'b'/B}]"; echo "[${x//b/'$1'}]"
## STDOUT:
[a,b c]
[a;b c]
[a
b-c]
[a
B c]
[a
$1 c]
## END

#### ${#@} and ${#*} are the positional count, also inside $(( ))
set -- a b c
echo ${#@} ${#*} "${#@}" $(( ${#@} + 1 ))
## stdout: 3 3 3 4

#### counting idiom: ${seen[$w]:-0} in $(( )), ${a[1]} in $(( )), bare RANDOM under set -u
declare -A seen; a=(5 6)
for w in x y x; do seen[$w]=$(( ${seen[$w]:-0} + 1 )); done
set -u
echo "${seen[x]} ${seen[y]} $(( ${a[1]} + 1 )) $(( RANDOM % 1 ))"
## stdout: 2 1 7 0
