## Imported from the Oils spec tests (append.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Append string to string
s='abc'
s+=d
echo $s
## stdout: abcd
## status: 0
## timeout: 10

#### Append to array to undefined variable

y+=(c d)
argv.py "${y[@]}"
## stdout:
## status: 127
## timeout: 10

#### Append array to array
a=(x y )
a+=(t 'u v')
argv.py "${a[@]}"
## stdout:
## status: 127
## timeout: 10

#### Append string to undefined variable

s+=foo
echo s=$s

# bash and mksh agree that this does NOT respect set -u.
# I think that's a mistake, but += is a legacy construct, so let's copy it.

set -u

t+=foo
echo t=$t
t+=foo
echo t=$t
## STDOUT:
s=foo
t=foo
t=foofoo
## END
## status: 0
## timeout: 10

#### error: s+=(my array)
s='abc'
s+=(d e f)
argv.py "${s[@]}"
## stdout:
## status: 127
## timeout: 10

#### export readonly +=

export e+=foo
echo e=$e

readonly r+=bar
echo r=$r

set -u

export e+=foo
echo e=$e

#readonly r+=foo
#echo r=$e
## STDOUT:
e=foo
r=bar
e=foofoo
## END
## status: 0
## timeout: 10

#### local +=

f() {
  local s+=foo
  echo s=$s

  set -u
  local s+=foo
  echo s=$s
}

f
## STDOUT:
s=foo
s=foofoo
## END
## status: 0
## timeout: 10

#### assign builtin appending array: declare d+=(d e)

declare d+=(d e)
echo "${d[@]}"
declare d+=(c l)
echo "${d[@]}"

readonly r+=(r e)
echo "${r[@]}"
# can't do this again

f() {
  local l+=(l o)
  echo "${l[@]}"

  local l+=(c a)
  echo "${l[@]}"
}

f
## STDOUT:
d e
d e c l
r e
l o
l o c a
## END
## status: 0
## timeout: 10

#### export+=array disallowed (strict_array)
shopt -s strict_array

export e+=(e x)
echo "${e[@]}"
## stdout: e x
## status: 0
## timeout: 10

#### Type mismatching of lhs+=rhs should not cause a crash
case $SH in mksh|zsh) exit ;; esac
s=
a=()
declare -A d=([lemon]=yellow)

s+=(1)
s+=([melon]=green)

a+=lime
a+=([1]=banana)

d+=orange
d+=(0)

true
## stdout:
## status: 0
## timeout: 10
## real-launcher
