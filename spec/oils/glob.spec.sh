## Imported from the Oils spec tests (glob.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### glob double quote escape
echo "*.sh"
## stdout: *.sh
## status: 0
## timeout: 10

#### glob single quote escape
echo "*.sh"
## stdout: *.sh
## status: 0
## timeout: 10

#### glob with char class expression
# note: mksh doesn't support [[:punct:]] ?
touch _tmp/e.E _tmp/foo.-
echo _tmp/*.[[:punct:]E]
## stdout: _tmp/e.E _tmp/foo.-
## status: 0
## timeout: 10

#### glob double quotes
# note: mksh doesn't support [[:punct:]] ?
touch _tmp/\"quoted.py\"
echo _tmp/\"*.py\"
## stdout: _tmp/"quoted.py"
## status: 0
## XFAIL ps-bash gap-oils-glob glob (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### glob escaped
# - mksh doesn't support [[:punct:]] ?
# - python shell fails because \[ not supported!
touch _tmp/\[abc\] _tmp/\?
echo _tmp/\[???\] _tmp/\?
## stdout: _tmp/[abc] _tmp/?
## status: 0
## timeout: 10

#### : escaped

touch _tmp/foo.-
echo _tmp/*.[[:punct:]] _tmp/*.[[:punct\:]]
## stdout: _tmp/foo.- _tmp/foo.-
## status: 0
## XFAIL ps-bash gap-oils-glob glob (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Glob after var manipulation
touch _tmp/foo.zzz _tmp/bar.zzz
g='_tmp/*.zzzZ'
echo $g ${g%Z}
## stdout: _tmp/*.zzzZ _tmp/bar.zzz _tmp/foo.zzz
## status: 0
## timeout: 10

#### Glob after part joining
touch _tmp/foo.yyy _tmp/bar.yyy
g='_tmp/*.yy'
echo $g ${g}y
## stdout: _tmp/*.yy _tmp/bar.yyy _tmp/foo.yyy
## status: 0
## XFAIL ps-bash gap-oils-glob glob (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Glob flags on file system
touch _tmp/-n _tmp/zzzzz
cd _tmp
echo -* hello zzzz?
## stdout-json: "hello zzzzz"
## status: 0
## timeout: 10

#### set -o noglob (bug #698)
var='\z'
set -f
echo $var
## stdout: \z
## status: 0
## timeout: 10

#### PatSub of unescaped [[] and []]
x='[foo]'
echo ${x//[\[z]/<}  # the right way to do it
echo ${x//[\]z]/>}
echo ${x//[[z]/<}  # also accepted
echo ${x//[]z]/>}
## STDOUT:
<foo]
[foo>
<foo]
[foo>
## END
## status: 0
## timeout: 10

#### PatSub of negated unescaped [[] and []]
x='[foo]'
echo ${x//[^\[z]/<}  # the right way to do it
echo ${x//[^\]z]/>}
echo ${x//[^[z]/<}  # also accepted
#echo ${x//[^]z]/>}  # only busybox ash interprets as ^\]
# mksh is doing something very odd, ignoring ^ altogether?
## STDOUT:
[<<<<
>>>>]
[<<<<
## END
## status: 0
## timeout: 10

#### \ in unquoted substitutions does not match a backslash
mkdir x
touch \
  x/test.ifs.\\.txt \
  x/test.ifs.\'.txt \
  x/test.ifs.a.txt \
  x/test.ifs.\\b.txt

v="*\\*.txt"
argv.py x/$v

v="*\'.txt"
argv.py x/$v

v='*\a.txt'
argv.py x/$v

v='*\b.txt'
argv.py x/$v


# 3 shells treat \ in unquoted substitution $v as literal \
## stdout:
## status: 127
## timeout: 10

#### \ in unquoted substitutions is preserved
v='\*\*.txt'
echo $v
echo "$v"
## STDOUT:
\*\*.txt
\*\*.txt
## END
## status: 0
## timeout: 10

#### \ in unquoted substitutions is preserved with set -o noglob
set -f
v='*\*.txt'
echo $v
## stdout: *\*.txt
## status: 0
## timeout: 10

#### \ in unquoted substitutions is preserved without glob matching
mkdir x
touch \
  'x/test.ifs.\.txt' \
  'x/test.ifs.*.txt'
v='*\*.txt'
argv.py x/unmatching.$v
## stdout:
## status: 127
## timeout: 10

#### \ in unquoted substitutions escapes globchars
mkdir x
touch \
  'x/test.ifs.\.txt' \
  'x/test.ifs.*.txt'

v='*\*.txt'
argv.py x/$v

v="\\" u='*.txt'
argv.py x/*$v$u

v="\\" u="*.txt"
argv.py x/*$v*.txt
## stdout:
## status: 127
## timeout: 10

#### pattern starting with . does not return . and ..

echo hi .*
## stdout: hi .*
## status: 0
## timeout: 10

#### glob backslash escape
echo \*.sh
## stdout: *.sh
## status: 0
## timeout: 10

#### 0 char glob -- does NOT work
echo []bin
## stdout: []bin
## status: 0
## timeout: 10

#### looks like glob at the start, but isn't
echo [bin
## stdout: [bin
## status: 0
## timeout: 10

#### looks like glob plus negation at the start, but isn't
echo [!bin
## stdout: [!bin
## status: 0
## timeout: 10

#### glob after var expansion
touch _tmp/a.A _tmp/aa.A _tmp/b.B
f="_tmp/*.A"
g="$f _tmp/*.B"
echo $g
## stdout: _tmp/a.A _tmp/aa.A _tmp/b.B
## status: 0
## timeout: 10

#### quoted var expansion with glob meta characters
touch _tmp/a.A _tmp/aa.A _tmp/b.B
f="_tmp/*.A"
echo "[ $f ]"
## stdout: [ _tmp/*.A ]
## status: 0
## timeout: 10

#### glob after "$@" expansion
fun() {
  echo "$@"
}
fun '_tmp/*.B'
## stdout: _tmp/*.B
## status: 0
## timeout: 10

#### glob after $@ expansion
touch _tmp/b.B
fun() {
  echo $@
}
fun '_tmp/*.B'
## stdout: _tmp/b.B
## status: 0
## timeout: 10

#### no glob after ~ expansion
HOME=*
echo ~/*.py
## stdout: */*.py
## status: 0
## timeout: 10

#### store literal globs in array then expand
touch _tmp/a.A _tmp/aa.A _tmp/b.B
g=("_tmp/*.A" "_tmp/*.B")
echo ${g[@]}
## stdout: _tmp/a.A _tmp/aa.A _tmp/b.B
## status: 0
## XFAIL ps-bash gap-oils-glob glob (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### glob inside array
touch _tmp/a.A _tmp/aa.A _tmp/b.B
g=(_tmp/*.A _tmp/*.B)
echo "${g[@]}"
## stdout: _tmp/a.A _tmp/aa.A _tmp/b.B
## status: 0
## timeout: 10

#### glob with escaped - in char class
touch _tmp/foo.-
touch _tmp/c.C
echo _tmp/*.[C-D] _tmp/*.[C\-D]
## stdout: _tmp/c.C _tmp/c.C _tmp/foo.-
## status: 0
## timeout: 10

#### shopt -u globskipdots shows . and ..
case $SH in dash|ash|mksh) exit ;; esac

shopt -u globskipdots
echo hi .*
## stdout: hi . ..
## status: 0
## XFAIL ps-bash gap-oils-glob glob (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
