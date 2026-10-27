## Imported from the Oils spec tests (var-op-test.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Lazy Evaluation of Alternative
i=0
x=x
echo ${x:-$((i++))}
echo $i
echo ${undefined:-$((i++))}
echo $i  # i is one because the alternative was only evaluated once
## STDOUT:
x
0
0
1
## END
## status: 0
## timeout: 10

#### Default value when empty
empty=''
echo ${empty:-is empty}
## stdout: is empty
## status: 0
## timeout: 10

#### Default value when unset
echo ${unset-is unset}
## stdout: is unset
## status: 0
## timeout: 10

#### Unquoted with array as default value
set -- '1 2' '3 4'
argv.py X${unset=x"$@"x}X
argv.py X${unset=x$@x}X  # If you want OSH to split, write this
# osh
## stdout:
## status: 127
## timeout: 10

#### Quoted with array as default value
set -- '1 2' '3 4'
argv.py "X${unset=x"$@"x}X"
argv.py "X${unset=x$@x}X"  # OSH is the same here

# Bash 4.2..4.4 had a bug. This was fixed in Bash 5.0.
#
# ## BUG bash STDOUT:
# ['Xx1', '2', '3', '4xX']
# ['Xx1 2 3 4xX']
# ## END
## stdout:
## status: 127
## timeout: 10

#### Assign default with array
set -- '1 2' '3 4'
argv.py X${unset=x"$@"x}X
argv.py "$unset"
## stdout:
## status: 127
## timeout: 10

#### Assign default value when empty
empty=''
${empty:=is empty}
echo $empty
## stdout: is empty
## status: 0
## timeout: 10

#### Assign default value when unset
${unset=is unset}
echo $unset
## stdout: is unset
## status: 0
## timeout: 10

#### ${v:+foo} Alternative value when empty
v=foo
empty=''
echo ${v:+v is not empty} ${empty:+is not empty}
## stdout: v is not empty
## status: 0
## timeout: 10

#### ${v+foo} Alternative value when unset
v=foo
echo ${v+v is not unset} ${unset:+is not unset}
## stdout: v is not unset
## status: 0
## timeout: 10

#### "${x+foo}" quoted (regression)
# Python's configure caught this
argv.py "${with_icc+set}" = set
## stdout:
## status: 127
## timeout: 10

#### ${s+foo} and ${s:+foo} when set -u
set -u
v=v
echo v=${v:+foo}
echo v=${v+foo}
unset v
echo v=${v:+foo}
echo v=${v+foo}
## STDOUT:
v=foo
v=foo
v=
v=
## END
## status: 0
## timeout: 10

#### ${v-foo} and ${v:-foo} when set -u
set -u
v=v
echo v=${v:-foo}
echo v=${v-foo}
unset v
echo v=${v:-foo}
echo v=${v-foo}
## STDOUT:
v=v
v=v
v=foo
v=foo
## END
## status: 0
## timeout: 10

#### $@ (empty) and - and +
echo argv=${@-minus}
echo argv=${@+plus}
echo argv=${@:-minus}
echo argv=${@:+plus}
## STDOUT:
argv=minus
argv=
argv=minus
argv=
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### $@ ("") and - and +
set -- ""
echo argv=${@-minus}
echo argv=${@+plus}
echo argv=${@:-minus}
echo argv=${@:+plus}

# Zsh treats $@ as an array unlike Bash converting it to a string by joining it
# with a space.
## STDOUT:
argv=
argv=plus
argv=minus
argv=
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### $@ ("" "") and - and +
set -- "" ""
echo argv=${@-minus}
echo argv=${@+plus}
echo argv=${@:-minus}
echo argv=${@:+plus}
## STDOUT:
argv=
argv=plus
argv=
argv=plus
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### $* ("" "") and - and + (IFS=)
set -- "" ""
IFS=
echo argv=${*-minus}
echo argv=${*+plus}
echo argv=${*:-minus}
echo argv=${*:+plus}
## STDOUT:
argv=
argv=plus
argv=
argv=plus
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "$*" ("" "") and - and + (IFS=)
set -- "" ""
IFS=
echo "argv=${*-minus}"
echo "argv=${*+plus}"
echo "argv=${*:-minus}"
echo "argv=${*:+plus}"
## STDOUT:
argv=
argv=plus
argv=minus
argv=
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Error when empty
empty=''
echo ${empty:?'is em'pty}  # test eval of error
echo should not get here
## stdout:
## status: 1
## timeout: 10

#### Error when unset
echo ${unset?is empty}
echo should not get here
## stdout:
## status: 1
## timeout: 10

#### Error when unset
v=foo
echo ${v+v is not unset} ${unset:+is not unset}
## stdout: v is not unset
## status: 0
## timeout: 10

#### ${var=x} dynamic scope
f() { : "${hello:=x}"; echo $hello; }
f
echo hello=$hello

f() { hello=x; }
f
echo hello=$hello
## STDOUT:
x
hello=x
hello=x
## END
## status: 0
## timeout: 10

#### array ${arr[0]=x}
arr=()
echo ${#arr[@]}
: ${arr[0]=x}
echo ${#arr[@]}
## STDOUT:
0
1
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### assoc array ${arr["k"]=x}
# note: this also works in zsh

declare -A arr=()
echo ${#arr[@]}
: ${arr['k']=x}
echo ${#arr[@]}
## STDOUT:
0
1
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "\z" as arg
echo "${undef-\$}"
echo "${undef-\(}"
echo "${undef-\z}"
echo "${undef-\"}"
echo "${undef-\`}"
echo "${undef-\\}"
# Note: this line terminates the quoting by ` not to confuse the text editor.
## STDOUT:
$
\(
\z
"
`
\
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "\e" as arg
echo "${undef-\e}"
## stdout: \e
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### "${array[@]} with set -u (bash is outlier)
case $SH in dash) exit ;; esac

set -u

typeset -a empty
empty=()

echo empty /"${empty[@]}"/
echo undefined /"${undefined[@]}"/



# empty array is unset in mksh
## STDOUT:
empty //
undefined //
## END
## status: 0
## timeout: 10
## real-launcher

#### "${undefined[@]+foo}" and "${undefined[@]:+foo}", with set -u
case $SH in dash) exit ;; esac

set -u

echo plus /"${array[@]+foo}"/
echo plus colon /"${array[@]:+foo}"/
## STDOUT:
plus //
plus colon //
## END
## status: 0
## timeout: 10
## real-launcher

#### "${a[@]+foo}" and "${a[@]:+foo}" - operators are equivalent on arrays?

case $SH in dash) exit ;; esac

echo '+ ' /"${array[@]+foo}"/
echo '+:' /"${array[@]:+foo}"/
echo

typeset -a array
array=()

echo '+ ' /"${array[@]+foo}"/
echo '+:' /"${array[@]:+foo}"/
echo

array=('')

echo '+ ' /"${array[@]+foo}"/
echo '+:' /"${array[@]:+foo}"/
echo

array=(spam eggs)

echo '+ ' /"${array[@]+foo}"/
echo '+:' /"${array[@]:+foo}"/
echo



# Bash 2.0..4.4 has a bug that "${a[@]:-xxx}" produces an empty string.  It
# seemed to consider a[@] and a[*] are non-empty when there is at least one
# element even if the element is empty.  This was fixed in Bash 5.0.
#
# ## BUG bash STDOUT:
# +  //
# +: //
#
# +  //
# +: //
#
# +  /foo/
# +: /foo/
#
# +  /foo/
# +: /foo/
#
# ## END
## STDOUT:
+  //
+: //

+  //
+: //

+  /foo/
+: //

+  /foo/
+: /foo/

## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Nix idiom ${!hooksSlice+"${!hooksSlice}"} - was workaround for obsolete bash 4.3 bug

case $SH in dash|mksh|zsh) exit ;; esac

# https://oilshell.zulipchat.com/#narrow/stream/307442-nix/topic/Replacing.20bash.20with.20osh.20in.20Nixpkgs.20stdenv

(argv.py ${!hooksSlice+"${!hooksSlice}"})

hooksSlice=x

argv.py ${!hooksSlice+"${!hooksSlice}"}

declare -a hookSlice=()

argv.py ${!hooksSlice+"${!hooksSlice}"}

foo=42
bar=43

declare -a hooksSlice=(foo bar spam eggs)

argv.py ${!hooksSlice+"${!hooksSlice}"}


# Bash 4.4 has a bug that ${!undef-} successfully generates an empty word.
#
# ## BUG bash STDOUT:
# []
# []
# []
# ['42']
# ## END
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### array and - and +
case $SH in dash) exit ;; esac

shopt -s compat_array  # to refer to array as scalar

empty=()
a1=('')
a2=('' x)
a3=(3 4)
echo empty=${empty[@]-minus}
echo a1=${a1[@]-minus}
echo a1[0]=${a1[0]-minus}
echo a2=${a2[@]-minus}
echo a3=${a3[@]-minus}
echo ---

echo empty=${empty[@]+plus}
echo a1=${a1[@]+plus}
echo a1[0]=${a1[0]+plus}
echo a2=${a2[@]+plus}
echo a3=${a3[@]+plus}
echo ---

echo empty=${empty+plus}
echo a1=${a1+plus}
echo a2=${a2+plus}
echo a3=${a3+plus}
echo ---

# Test quoted arrays too
argv.py "${empty[@]-minus}"
argv.py "${empty[@]+plus}"
argv.py "${a1[@]-minus}"
argv.py "${a1[@]+plus}"
argv.py "${a1[0]-minus}"
argv.py "${a1[0]+plus}"
argv.py "${a2[@]-minus}"
argv.py "${a2[@]+plus}"
argv.py "${a3[@]-minus}"
argv.py "${a3[@]+plus}"
## STDOUT:
empty=minus
a1=
a1[0]=
a2= x
a3=3 4
---
empty=
a1=plus
a1[0]=plus
a2=plus
a3=plus
---
empty=
a1=plus
a2=plus
a3=plus
---
## END
## status: 127
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### assoc array and - and +
case $SH in dash|mksh) exit ;; esac

declare -A empty=()
declare -A assoc=(['k']=v)

echo empty=${empty[@]-minus}
echo empty=${empty[@]+plus}
echo assoc=${assoc[@]-minus}
echo assoc=${assoc[@]+plus}

echo ---
echo empty=${empty[@]:-minus}
echo empty=${empty[@]:+plus}
echo assoc=${assoc[@]:-minus}
echo assoc=${assoc[@]:+plus}
## STDOUT:
empty=minus
empty=
assoc=v
assoc=plus
---
empty=minus
empty=
assoc=v
assoc=plus
## END
## status: 0
## timeout: 10
## real-launcher

#### op-test for ${a} and ${a[0]}
case $SH in dash) exit ;; esac

test-hyphen() {
  echo "a   : '${a-no-colon}' '${a:-with-colon}'"
  echo "a[0]: '${a[0]-no-colon}' '${a[0]:-with-colon}'"
}

a=()
test-hyphen
a=("")
test-hyphen
a=("" "")
test-hyphen
IFS=
test-hyphen


# Zsh's ${a} and ${a[@]} implement something different from the other shells'.



# Bash 2.0..4.4 has a bug that "${a[@]:-xxx}" produces an empty string.  It
# seemed to consider a[@] and a[*] are non-empty when there is at least one
# element even if the element is empty.  This was fixed in Bash 5.0.
#
# ## BUG bash STDOUT:
# a[@]: 'no-colon' 'with-colon'
# a[*]: 'no-colon' 'with-colon'
# a[@]: '' ''
# a[*]: '' ''
# a[@]: ' ' ' '
# a[*]: ' ' ' '
# a[@]: ' ' ' '
# a[*]: '' ''
# ## END

# Zsh's ${a} and ${a[@]} implement something different from the other shells'.
## STDOUT:
a   : 'no-colon' 'with-colon'
a[0]: 'no-colon' 'with-colon'
a   : '' 'with-colon'
a[0]: '' 'with-colon'
a   : '' 'with-colon'
a[0]: '' 'with-colon'
a   : '' 'with-colon'
a[0]: '' 'with-colon'
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-test var-op-test (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
