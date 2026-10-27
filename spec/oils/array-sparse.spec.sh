## Imported from the Oils spec tests (array-sparse.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### test length
sp=(x y z)

sp[5]=z

echo len=${#sp[@]}

sp[10]=z

echo len=${#sp[@]}
## STDOUT:
len=4
len=5
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### +=
sp1[10]=a
sp1[20]=b
sp1[99]=c
typeset -p sp1 | sed 's/"//g'
sp1+=(1 2 3)
typeset -p sp1 | sed 's/"//g'
## STDOUT:
declare -a sp1=([10]=a [20]=b [99]=c)
declare -a sp1=([10]=a [20]=b [99]=c [100]=1 [101]=2 [102]=3)
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### a[i]=v
sp1[10]=a
sp1[20]=b
sp1[30]=c
typeset -p sp1 | sed 's/"//g'
sp1[10]=X
sp1[25]=Y
sp1[90]=Z
typeset -p sp1 | sed 's/"//g'
## STDOUT:
declare -a sp1=([10]=a [20]=b [30]=c)
declare -a sp1=([10]=X [20]=b [25]=Y [30]=c [90]=Z)
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### unset -v a[i]
a=(1 2 3 4 5 6 7 8 9)
typeset -p a
unset -v "a[1]"
typeset -p a
unset -v "a[9]"
typeset -p a
unset -v "a[0]"
typeset -p a
## STDOUT:
declare -a a=([0]="1" [1]="2" [2]="3" [3]="4" [4]="5" [5]="6" [6]="7" [7]="8" [8]="9")
declare -a a=([0]="1" [2]="3" [3]="4" [4]="5" [5]="6" [6]="7" [7]="8" [8]="9")
declare -a a=([0]="1" [2]="3" [3]="4" [4]="5" [5]="6" [6]="7" [7]="8" [8]="9")
declare -a a=([2]="3" [3]="4" [4]="5" [5]="6" [6]="7" [7]="8" [8]="9")
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ((sp[i])) and ((sp[i]++))
a=(1 2 3 4 5 6 7 8 9)
unset -v 'a[2]' 'a[3]' 'a[7]'

echo $((a[0]))
echo $((a[1]))
echo $((a[2]))
echo $((a[3]))
echo $((a[7]))

echo $((a[1]++))
echo $((a[2]++))
echo $((a[3]++))
echo $((a[7]++))

echo $((++a[1]))
echo $((++a[2]))
echo $((++a[3]))
echo $((++a[7]))

echo $((a[1] = 100, a[1]))
echo $((a[2] = 100, a[2]))
echo $((a[3] = 100, a[3]))
echo $((a[7] = 100, a[7]))
## STDOUT:
1
2
0
0
0
2
0
0
0
4
2
2
2
100
100
100
100
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${a[@]}
a=(v{0,1,2,3,4,5,6,7,8,9})
unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'

argv.py "${a[@]}"
argv.py "abc${a[@]}xyz"
## stdout:
## status: 127
## timeout: 10

#### ${a[@]-unset}, ${a[@]:-empty}, etc.
a1=()
a2=("")
a3=("" "")

echo "a1 unset: [${a1[@]-unset}]"
echo "a1 empty: [${a1[@]:-empty}]"
echo "a2 unset: [${a2[@]-unset}]"
echo "a2 empty: [${a2[@]:-empty}]"
echo "a3 unset: [${a3[@]-unset}]"
echo "a3 empty: [${a3[@]:-empty}]"
## STDOUT:
a1 unset: [unset]
a1 empty: [empty]
a2 unset: []
a2 empty: [empty]
a3 unset: [ ]
a3 empty: [ ]
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${a-}
a1=()
a2=("" "")
a3=(foo bar)

echo "$a1, ${a1-(unset)}, ${a1:-(empty)};"
echo "$a2, ${a2-(unset)}, ${a2:-(empty)};"
echo "$a3, ${a3-(unset)}, ${a3:-(empty)};"
## STDOUT:
, (unset), (empty);
, , (empty);
foo, foo, foo;
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "${a[*]}"
a=(v{0,1,2,3,4,5,6,7,8,9})
unset -v 'a[3]' 'a[4]' 'a[7]' 'a[9]'

echo "${a[*]}"
IFS=
echo "${a[*]}"
IFS=/
echo "${a[*]}"
## STDOUT:
v0 v1 v2 v5 v6 v8
v0v1v2v5v6v8
v0/v1/v2/v5/v6/v8
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### test "declare -p sp"
a0=()
a1=(1)
a2=(1 2)
a=(x y z w)
a[500]=100
a[1000]=100

case $SH in
bash|mksh)
  typeset -p a0 a1 a2 a
  exit ;;
esac

declare -p a0 a1 a2 a
## STDOUT:
declare -a a0=()
declare -a a1=([0]="1")
declare -a a2=([0]="1" [1]="2")
declare -a a=([0]="x" [1]="y" [2]="z" [3]="w" [500]="100" [1000]="100")
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Negative index with a[i]=v
case $SH in mksh) exit ;; esac

sp1[9]=x
typeset -p sp1 | sed 's/"//g'

sp1[-1]=A
sp1[-4]=B
sp1[-8]=C
sp1[-10]=D
typeset -p sp1 | sed 's/"//g'
## STDOUT:
declare -a sp1=([9]=x)
declare -a sp1=([0]=D [2]=C [6]=B [9]=A)
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### a[i]=v with BigInt
case $SH in mksh) exit ;; esac

sp1[1]=x
sp1[5]=y
sp1[9]=z

echo "${#sp1[@]}"
sp1[0x7FFFFFFFFFFFFFFF]=a
echo "${#sp1[@]}"
sp1[0x7FFFFFFFFFFFFFFE]=b
echo "${#sp1[@]}"
sp1[0x7FFFFFFFFFFFFFFD]=c
echo "${#sp1[@]}"
## STDOUT:
3
4
5
6
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Negative out-of-bound index with a[i]=v (1/2)
case $SH in mksh) exit ;; esac

sp1[9]=x
sp1[-11]=E
declare -p sp1
## stdout: declare -a sp1=([9]="x")
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Negative out-of-bound index with a[i]=v (2/2)
case $SH in mksh) exit ;; esac

sp1[9]=x

sp1[-21]=F
declare -p sp1
## stdout: declare -a sp1=([9]="x")
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### xtrace a+=()
#case $SH in mksh) exit ;; esac

sp1=(1)
set -x
sp1+=(2)
## stdout:
## status: 0
## timeout: 10
## real-launcher

#### unset -v a[i] with out-of-bound negative index
case $SH in mksh) exit ;; esac

a=(1)

unset -v "a[-2]"
unset -v "a[-3]"
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### unset -v a[i] for max index
case $SH in mksh) exit ;; esac

a=({1..9})
unset -v 'a[-1]'
a[-1]=x
declare -p a
unset -v 'a[-1]'
a[-1]=x
declare -p a
## STDOUT:
declare -a a=([0]="1" [1]="2" [2]="3" [3]="4" [4]="5" [5]="6" [6]="7" [7]="x")
declare -a a=([0]="1" [1]="2" [2]="3" [3]="4" [4]="5" [5]="6" [6]="x")
## END
## status: 0
## timeout: 10
## real-launcher

#### [[ -v a[i] ]]
case $SH in mksh) exit ;; esac

sp1=()
[[ -v sp1[0] ]]; echo "$? (expect 1)"
[[ -v sp1[9] ]]; echo "$? (expect 1)"

sp2=({1..9})
[[ -v sp2[0] ]]; echo "$? (expect 0)"
[[ -v sp2[8] ]]; echo "$? (expect 0)"
[[ -v sp2[9] ]]; echo "$? (expect 1)"
[[ -v sp2[-1] ]]; echo "$? (expect 0)"
[[ -v sp2[-2] ]]; echo "$? (expect 0)"
[[ -v sp2[-9] ]]; echo "$? (expect 0)"

sp3=({1..9})
unset -v 'sp3[4]'
[[ -v sp3[3] ]]; echo "$? (expect 0)"
[[ -v sp3[4] ]]; echo "$? (expect 1)"
[[ -v sp3[5] ]]; echo "$? (expect 0)"
[[ -v sp3[-1] ]]; echo "$? (expect 0)"
[[ -v sp3[-4] ]]; echo "$? (expect 0)"
[[ -v sp3[-5] ]]; echo "$? (expect 1)"
[[ -v sp3[-6] ]]; echo "$? (expect 0)"
[[ -v sp3[-9] ]]; echo "$? (expect 0)"
## STDOUT:
1 (expect 1)
1 (expect 1)
0 (expect 0)
0 (expect 0)
1 (expect 1)
0 (expect 0)
0 (expect 0)
0 (expect 0)
0 (expect 0)
1 (expect 1)
0 (expect 0)
0 (expect 0)
0 (expect 0)
1 (expect 1)
0 (expect 0)
0 (expect 0)
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### [[ -v a[i] ]] with invalid negative index
case $SH in mksh) exit ;; esac

sp1=()
([[ -v sp1[-1] ]]; echo "$? (expect 1)")
sp2=({1..9})
([[ -v sp2[-10] ]]; echo "$? (expect 1)")
sp3=({1..9})
unset -v 'sp3[4]'
([[ -v sp3[-10] ]]; echo "$? (expect 1)")
## STDOUT:
1 (expect 1)
1 (expect 1)
1 (expect 1)
## END
## status: 0
## timeout: 10
## real-launcher

#### ((sp[i])) and ((sp[i]++)) with invalid negative index
case $SH in mksh) exit ;; esac

a=({1..9})
unset -v 'a[2]' 'a[3]' 'a[7]'

echo $((a[-10]))
## stdout: 0
## status: 0
## timeout: 10
## real-launcher

#### ${sp[i]}
case $SH in mksh) exit ;; esac

sp=({1..9})
unset -v 'sp[2]'
unset -v 'sp[3]'
unset -v 'sp[7]'

echo "sp[0]: '${sp[0]}', ${sp[0]:-(empty)}, ${sp[0]+set}."
echo "sp[1]: '${sp[1]}', ${sp[1]:-(empty)}, ${sp[1]+set}."
echo "sp[8]: '${sp[8]}', ${sp[8]:-(empty)}, ${sp[8]+set}."
echo "sp[2]: '${sp[2]}', ${sp[2]:-(empty)}, ${sp[2]+set}."
echo "sp[3]: '${sp[3]}', ${sp[3]:-(empty)}, ${sp[3]+set}."
echo "sp[7]: '${sp[7]}', ${sp[7]:-(empty)}, ${sp[7]+set}."

echo "sp[-1]: '${sp[-1]}'."
echo "sp[-2]: '${sp[-2]}'."
echo "sp[-3]: '${sp[-3]}'."
echo "sp[-4]: '${sp[-4]}'."
echo "sp[-9]: '${sp[-9]}'."
## STDOUT:
sp[0]: '1', 1, set.
sp[1]: '2', 2, set.
sp[8]: '9', 9, set.
sp[2]: '', (empty), .
sp[3]: '', (empty), .
sp[7]: '', (empty), .
sp[-1]: '9'.
sp[-2]: ''.
sp[-3]: '7'.
sp[-4]: '6'.
sp[-9]: '1'.
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ${sp[i]} with negative invalid index
case $SH in mksh) exit ;; esac

sp=({1..9})
unset -v 'sp[2]'
unset -v 'sp[3]'
unset -v 'sp[7]'

echo "sp[-10]: '${sp[-10]}'."
echo "sp[-11]: '${sp[-11]}'."
echo "sp[-19]: '${sp[-19]}'."
## STDOUT:
sp[-10]: ''.
sp[-11]: ''.
sp[-19]: ''.
## END
## status: 0
## timeout: 10
## real-launcher

#### ${a[@]:offset:length}
case $SH in mksh) exit ;; esac

a=(v{0..9})
unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'

echo '==== ${a[@]:offset} ===='
echo "[${a[@]:0}][${a[*]:0}]"
echo "[${a[@]:2}][${a[*]:2}]"
echo "[${a[@]:3}][${a[*]:3}]"
echo "[${a[@]:5}][${a[*]:5}]"
echo "[${a[@]:9}][${a[*]:9}]"
echo "[${a[@]:10}][${a[*]:10}]"
echo "[${a[@]:11}][${a[*]:11}]"

echo '==== ${a[@]:negative} ===='
echo "[${a[@]: -1}][${a[*]: -1}]"
echo "[${a[@]: -2}][${a[*]: -2}]"
echo "[${a[@]: -5}][${a[*]: -5}]"
echo "[${a[@]: -9}][${a[*]: -9}]"
echo "[${a[@]: -10}][${a[*]: -10}]"
echo "[${a[@]: -11}][${a[*]: -11}]"
echo "[${a[@]: -21}][${a[*]: -21}]"

echo '==== ${a[@]:offset:length} ===='
echo "[${a[@]:0:0}][${a[*]:0:0}]"
echo "[${a[@]:0:1}][${a[*]:0:1}]"
echo "[${a[@]:0:3}][${a[*]:0:3}]"
echo "[${a[@]:2:1}][${a[*]:2:1}]"
echo "[${a[@]:2:4}][${a[*]:2:4}]"
echo "[${a[@]:3:4}][${a[*]:3:4}]"
echo "[${a[@]:5:4}][${a[*]:5:4}]"
echo "[${a[@]:5:0}][${a[*]:5:0}]"
echo "[${a[@]:9:1}][${a[*]:9:1}]"
echo "[${a[@]:9:2}][${a[*]:9:2}]"
echo "[${a[@]:10:1}][${a[*]:10:1}]"
## STDOUT:
==== ${a[@]:offset} ====
[v0 v1 v5 v6 v8 v9][v0 v1 v5 v6 v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v9][v9]
[][]
[][]
==== ${a[@]:negative} ====
[v9][v9]
[v8 v9][v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v1 v5 v6 v8 v9][v1 v5 v6 v8 v9]
[v0 v1 v5 v6 v8 v9][v0 v1 v5 v6 v8 v9]
[][]
[][]
==== ${a[@]:offset:length} ====
[][]
[v0][v0]
[v0 v1 v5][v0 v1 v5]
[v5][v5]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[v5 v6 v8 v9][v5 v6 v8 v9]
[][]
[v9][v9]
[v9][v9]
[][]
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ${@:offset:length}
case $SH in mksh) exit ;; esac

set -- v{1..9}

{
  echo '==== ${@:offset:length} ===='
  echo "[${*:0:3}][${*:0:3}]"
  echo "[${*:1:3}][${*:1:3}]"
  echo "[${*:3:3}][${*:3:3}]"
  echo "[${*:5:10}][${*:5:10}]"

  echo '==== ${@:negative} ===='
  echo "[${*: -1}][${*: -1}]"
  echo "[${*: -3}][${*: -3}]"
  echo "[${*: -9}][${*: -9}]"
  echo "[${*: -10}][${*: -10}]"
  echo "[${*: -11}][${*: -11}]"
  echo "[${*: -3:2}][${*: -3:2}]"
  echo "[${*: -9:4}][${*: -9:4}]"
  echo "[${*: -10:4}][${*: -10:4}]"
  echo "[${*: -11:4}][${*: -11:4}]"
} | sed "s:$SH:\$SH:g;s:${SH##*/}:\$SH:g"
## STDOUT:
==== ${@:offset:length} ====
[/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2][/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2]
[v1 v2 v3][v1 v2 v3]
[v3 v4 v5][v3 v4 v5]
[v5 v6 v7 v8 v9][v5 v6 v7 v8 v9]
==== ${@:negative} ====
[v9][v9]
[v7 v8 v9][v7 v8 v9]
[v1 v2 v3 v4 v5 v6 v7 v8 v9][v1 v2 v3 v4 v5 v6 v7 v8 v9]
[/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2 v3 v4 v5 v6 v7 v8 v9][/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2 v3 v4 v5 v6 v7 v8 v9]
[][]
[v7 v8][v7 v8]
[v1 v2 v3 v4][v1 v2 v3 v4]
[/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2 v3][/mnt/c/Users/andyb/AppData/Local/Temp/ps-$SH/spec-record/538bb986cbf847d0bfa71a0b8f312ead.sh v1 v2 v3]
[][]
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ${a[@]:BigInt}
case $SH in mksh) exit ;; esac

case $SH in
  bash)
    # disabled with soil-ovm-tarball image 2025-04-30b - the CI runs on Debian 12
    # now
    exit

    # Work around bash integer overflow bug that only happens on say Debian 10,
    # but NOT Debian 12.  The bug exists in bash 5.2.  It's unclear why it
    # depends on the OS version.
    v='/etc/debian_version'
    # debian version 10 / debian buster
    if test -f $v && grep -E 'buster/sid|^10' $v >/dev/null; then
      cat << 'EOF'
[x][x]
[y x][y x]
[z y x][z y x]
[z y x][z y x]
EOF
      exit
    fi
    # Actual STDOUT of buggy bash builds:
    # [][]
    # [][]
    # [][]
    # [][]
    ;;
esac

a=(1 2 3)
a[0x7FFFFFFFFFFFFFFF]=x
a[0x7FFFFFFFFFFFFFFE]=y
a[0x7FFFFFFFFFFFFFFD]=z

echo "[${a[@]: -1}][${a[*]: -1}]"
echo "[${a[@]: -2}][${a[*]: -2}]"
echo "[${a[@]: -3}][${a[*]: -3}]"
echo "[${a[@]: -4}][${a[*]: -4}]"
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ${a[@]#...}
case $SH in mksh) exit ;; esac

a=(v{0..9})
unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'

argv.py "${a[@]#v}"
argv.py "abc${a[@]#v}xyz"
argv.py "${a[@]%[0-5]}"
argv.py "abc${a[@]%[0-5]}xyz"
argv.py "${a[@]#v?}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### ${a[@]/pat/rep}

case $SH in mksh) exit ;; esac

a=(v{0..9})
unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'

argv.py "${a[@]/?}"
argv.py "${a[@]//?}"
argv.py "${a[@]/#?}"
argv.py "${a[@]/%?}"

argv.py "${a[@]/v/x}"
argv.py "${a[@]//v/x}"
argv.py "${a[@]/[0-5]/D}"
argv.py "${a[@]//[!0-5]/_}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### ${a[@]@P}, ${a[@]@Q}, and ${a[@]@a}
case $SH in mksh) exit ;; esac

a=(v{0..9})
unset -v 'a[2]' 'a[3]' 'a[4]' 'a[7]'

argv.py "${a[@]@P}"
argv.py "${a[*]@P}"
argv.py "${a[@]@Q}"
argv.py "${a[*]@Q}"
argv.py "${a[@]@a}"
argv.py "${a[*]@a}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### ${!a[0]}
case $SH in mksh) exit ;; esac

v1=hello v2=world
a=(v1 v2)

echo "${!a[0]}, ${!a[1]}"
## stdout: hello, world
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### ${!a[@]}
case $SH in mksh) exit ;; esac

a=(v{0..9})
unset -v 'a[3]' 'a[4]' 'a[7]' 'a[9]'

argv.py "${!a[@]}"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### compgen -F _set_COMPREPLY
case $SH in mksh) exit ;; esac

_set_COMPREPLY() {
  COMPREPLY=({0..9})
  unset -v 'COMPREPLY[2]' 'COMPREPLY[4]' 'COMPREPLY[6]'
}

compgen -F _set_COMPREPLY
## STDOUT:
0
1
3
5
7
8
9
## END
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### compadjust
case $SH in bash|mksh) exit ;; esac

COMP_ARGV=(echo 'Hello,' 'Bash' 'world!')
compadjust cur prev words cword
argv.py "$cur" "$prev" "$cword"
argv.py "${words[@]}"
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### (YSH) @[sp] and @sp
case $SH in bash|mksh) exit ;; esac

a=({0..5})
unset -v 'a[1]' 'a[2]' 'a[4]'

shopt -s parse_at
argv.py @[a]
argv.py @a
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### (YSH) append v1 v2... (a)
case $SH in bash|mksh) exit ;; esac

a=(1 2 3)
unset -v 'a[1]'
append 'x' 'y' 'z' (a)
= a
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-array-sparse array-sparse (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Regression: a[-1]=1
case $SH in mksh) exit 99 ;; esac

a[-1]=1
## stdout:
## status: 1
## timeout: 10
## real-launcher

#### Initializing indexed array with ([index]=value)
case $SH in mksh) exit 99 ;; esac
declare -a a=([xx]=1 [yy]=2 [zz]=3)
echo status=$?
argv.py "${a[@]}"
## stdout: status=0
## status: 127
## timeout: 10
## real-launcher
