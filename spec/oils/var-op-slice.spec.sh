## Imported from the Oils spec tests (var-op-slice.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### String slice
foo=abcdefg
echo ${foo:1:3}
## stdout: bcd
## status: 0
## timeout: 10

#### Cannot take length of substring slice
# These are runtime errors, but we could make them parse time errors.
v=abcde
echo ${#v:1:3}
# zsh actually implements this!
## stdout:
## status: 1
## timeout: 10

#### Out of range string slice: begin
# out of range begin doesn't raise error in bash, but in mksh it skips the
# whole thing!
foo=abcdefg
echo _${foo:100:3}
echo $?
## STDOUT:
_
0
## END
## status: 0
## timeout: 10

#### Out of range string slice: length
# OK in both bash and mksh
foo=abcdefg
echo _${foo:3:100}
echo $?
## STDOUT:
_defg
0
## END
## status: 0
## timeout: 10

#### Negative start index
foo=abcdefg
echo ${foo: -4:3}
## stdout: def
## status: 0
## timeout: 10

#### Negative start index respects unicode
foo=abcd-μ-
echo ${foo: -4:3}
## stdout: d-μ
## status: 0
## timeout: 10

#### Negative second arg is position, not length!
foo=abcdefg
echo ${foo:3:-1} ${foo: 3: -2} ${foo:3 :-3 }
## stdout: def de d
## status: 0
## timeout: 10

#### Negative start index respects unicode
foo=abcd-μ-
echo ${foo: -5: -3}
## stdout: cd
## status: 0
## timeout: 10

#### String slice with math
# I think this is the $(()) language inside?
i=1
foo=abcdefg
echo ${foo: i+4-2 : i + 2}
## stdout: def
## status: 0
## timeout: 10

#### Slice undefined
echo -${undef:1:2}-
set -o nounset
echo -${undef:1:2}-
echo -done-
# mksh doesn't respect nounset!
## stdout: --
## status: 1
## XFAIL ps-bash gap-oils-var-op-slice var-op-slice (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Slice UTF-8 String
# mksh slices by bytes.
foo='--μ--'
echo ${foo:1:3}
## stdout: -μ-
## status: 0
## timeout: 10

#### Slice string with invalid UTF-8 results in empty string and warning
s=$(echo -e "\xFF")bcdef
echo -${s:1:3}-
## stdout: -bcd-
## status: 0
## timeout: 10

#### Slice string with invalid UTF-8 with strict_word_eval
shopt -s strict_word_eval || true
echo slice
s=$(echo -e "\xFF")bcdef
echo -${s:1:3}-
## STDOUT:
slice
-bcd-
## END
## status: 0
## timeout: 10

#### Slice with an index that's an array -- silent a[0] decay
i=(3 4 5)
mystr=abcdefg
echo assigned
echo ${mystr:$i:2}
## STDOUT:
assigned
de
## END
## status: 0
## timeout: 10

#### Slice with an assoc array
declare -A A=(['5']=3 ['6']=4)
mystr=abcdefg
echo assigned
echo ${mystr:$A:2}
## STDOUT:
assigned
ab
## END
## status: 0
## timeout: 10

#### Simple ${@:offset}

set -- 4 5 6

result=$(argv.py ${@:0})
echo ${result//"$0"/'SHELL'}

argv.py ${@:1}
argv.py ${@:2}
## STDOUT:

## END
## status: 127
## timeout: 10

#### ${@:0:1}
set a b c
result=$(echo ${@:0:1})
echo ${result//"$0"/'SHELL'}
## stdout: SHELL
## status: 0
## XFAIL ps-bash gap-oils-var-op-slice var-op-slice (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Permutations of implicit begin and length
array=(1 2 3)

argv.py ${array[@]}

# *** implicit length of N **
argv.py ${array[@]:0}

# Why is this one not allowed
#argv.py ${array[@]:}

# ** implicit length of ZERO **
#argv.py ${array[@]::}
#argv.py ${array[@]:0:}

argv.py ${array[@]:0:0}
echo

# Same agreed upon permutations
set -- 1 2 3
argv.py ${@}
argv.py ${@:1}
argv.py ${@:1:0}
echo

s='123'
argv.py "${s}"
argv.py "${s:0}"
argv.py "${s:0:0}"
## STDOUT:


## END
## status: 127
## timeout: 10

#### ${array[@]::} has implicit length of zero - for ble.sh

# https://oilshell.zulipchat.com/#narrow/stream/121540-oil-discuss/topic/.24.7Barr.5B.40.5D.3A.3A.7D.20in.20bash.20-.20is.20it.20documented.3F

array=(1 2 3)
argv.py ${array[@]::}
argv.py ${array[@]:0:}

echo

set -- 1 2 3
argv.py ${@::}
argv.py ${@:0:}
## STDOUT:

## END
## status: 127
## timeout: 10

#### ${@:offset} and ${*:offset}
case $SH in zsh) return ;; esac  # zsh is very different

argv.shell-name-checked () {
  argv.py "${@//$0/SHELL}"
}
fun() {
  argv.shell-name-checked -${*:0}- # include $0
  argv.shell-name-checked -${*:1}- # from $1
  argv.shell-name-checked -${*:3}- # last parameter $3
  argv.shell-name-checked -${*:4}- # empty
  argv.shell-name-checked -${*:5}- # out of boundary
  argv.shell-name-checked -${@:0}-
  argv.shell-name-checked -${@:1}-
  argv.shell-name-checked -${@:3}-
  argv.shell-name-checked -${@:4}-
  argv.shell-name-checked -${@:5}-
  argv.shell-name-checked "-${*:0}-"
  argv.shell-name-checked "-${*:1}-"
  argv.shell-name-checked "-${*:3}-"
  argv.shell-name-checked "-${*:4}-"
  argv.shell-name-checked "-${*:5}-"
  argv.shell-name-checked "-${@:0}-"
  argv.shell-name-checked "-${@:1}-"
  argv.shell-name-checked "-${@:3}-"
  argv.shell-name-checked "-${@:4}-"
  argv.shell-name-checked "-${@:5}-"
}
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### ${@:offset:length} and ${*:offset:length}
case $SH in zsh) return ;; esac  # zsh is very different

argv.shell-name-checked () {
  argv.py "${@//$0/SHELL}"
}
fun() {
  argv.shell-name-checked -${*:0:2}- # include $0
  argv.shell-name-checked -${*:1:2}- # from $1
  argv.shell-name-checked -${*:3:2}- # last parameter $3
  argv.shell-name-checked -${*:4:2}- # empty
  argv.shell-name-checked -${*:5:2}- # out of boundary
  argv.shell-name-checked -${@:0:2}-
  argv.shell-name-checked -${@:1:2}-
  argv.shell-name-checked -${@:3:2}-
  argv.shell-name-checked -${@:4:2}-
  argv.shell-name-checked -${@:5:2}-
  argv.shell-name-checked "-${*:0:2}-"
  argv.shell-name-checked "-${*:1:2}-"
  argv.shell-name-checked "-${*:3:2}-"
  argv.shell-name-checked "-${*:4:2}-"
  argv.shell-name-checked "-${*:5:2}-"
  argv.shell-name-checked "-${@:0:2}-"
  argv.shell-name-checked "-${@:1:2}-"
  argv.shell-name-checked "-${@:3:2}-"
  argv.shell-name-checked "-${@:4:2}-"
  argv.shell-name-checked "-${@:5:2}-"
}
fun "a 1" "b 2" "c 3"
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### ${array[@]:} vs ${array[@]: }  - bash and zsh inconsistent

$SH -c 'array=(1 2 3); argv.py ${array[@]:}'
$SH -c 'array=(1 2 3); argv.py space ${array[@]: }'

$SH -c 's=123; argv.py ${s:}'
$SH -c 's=123; argv.py space ${s: }'
## stdout:
## status: 127
## timeout: 10
## real-launcher
