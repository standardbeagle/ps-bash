## Imported from the Oils spec tests (var-op-strip.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Remove const suffix
v=abcd
echo ${v%d} ${v%%cd}
## stdout: abc ab
## status: 0
## timeout: 10

#### Remove const prefix
v=abcd
echo ${v#a} ${v##ab}
## stdout: bcd cd
## status: 0
## timeout: 10

#### Remove const suffix is vectorized on user array
a=(1a 2a 3a)
argv.py ${a[@]%a}
## stdout:
## status: 127
## timeout: 10

#### Remove const suffix is vectorized on $@ array
set -- 1a 2a 3a
argv.py ${@%a}
## stdout:
## status: 127
## timeout: 10

#### Remove const suffix from undefined
echo ${undef%suffix}
## STDOUT:

## END
## status: 0
## timeout: 10

#### Remove shortest glob suffix
v=aabbccdd
echo ${v%c*}
## stdout: aabbc
## status: 0
## timeout: 10

#### Remove longest glob suffix
v=aabbccdd
echo ${v%%c*}
## stdout: aabb
## status: 0
## timeout: 10

#### Remove shortest glob prefix
v=aabbccdd
echo ${v#*b}
## stdout: bccdd
## status: 0
## timeout: 10

#### Remove longest glob prefix
v=aabbccdd
echo ${v##*b}
## stdout: ccdd
## status: 0
## timeout: 10

#### Strip char class
v=abc
echo ${v%[[:alpha:]]}
## stdout: ab
## status: 0
## timeout: 10

#### Strip unicode prefix

show_hex() { od -A n -t c -t x1; }

# NOTE: LANG is set to utf-8.
# ? is a glob that stands for one character

v='μ-'
echo ${v#?} | show_hex
echo
echo ${v##?} | show_hex
echo

v='-μ'
echo ${v%?} | show_hex
echo
echo ${v%%?} | show_hex
  \n
  0a

  \n
  0a

  \n
  0a

  \n
  0a
## STDOUT:
   -  \n
  2d  0a

   -  \n
  2d  0a

   -  \n
  2d  0a

   -  \n
  2d  0a
## END
## status: 127
## XFAIL ps-bash gap-oils-var-op-strip var-op-strip (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Bug fix: Test that you can remove everything with glob
s='--x--'
argv.py "${s%%-*}" "${s%-*}" "${s#*-}" "${s##*-}"
## stdout:
## status: 127
## timeout: 10

#### Test that you can remove everything with const
s='abcd'
argv.py "${s%%abcd}" "${s%abcd}" "${s#abcd}" "${s##abcd}"
# failure case:
argv.py "${s%%abcde}" "${s%abcde}" "${s#abcde}" "${s##abcde}"
## stdout:
## status: 127
## timeout: 10

#### Prepend using replacement of #
# This case was found in Kubernetes and others
array=(aa bb '')
argv.py ${array[@]/#/prefix-}
## stdout:
## status: 127
## timeout: 10

#### Append using replacement of %
array=(aa bb '')
argv.py ${array[@]/%/-suffix}
## stdout:
## status: 127
## timeout: 10

#### strip unquoted and quoted [
# I guess dash and mksh treat unquoted [ as an invalid glob?
var='[foo]'
echo ${var#[}
echo ${var#"["}
echo "${var#[}"
echo "${var#"["}"
## STDOUT:
foo]
foo]
foo]
foo]
## END
## status: 0
## timeout: 10

#### strip unquoted and quoted []
# LooksLikeGlob('[]') is true
# I guess dash, mksh, and zsh treat unquoted [ as an invalid glob?
var='[]foo[]'
echo ${var#[]}
echo ${var#"[]"}
echo "${var#[]}"
echo "${var#"[]"}"
## STDOUT:
foo[]
foo[]
foo[]
foo[]
## END
## status: 0
## timeout: 10

#### strip unquoted and quoted ?
var='[foo]'
echo ${var#?}
echo ${var#"?"}
echo "${var#?}"
echo "${var#"?"}"
## STDOUT:
foo]
[foo]
foo]
[foo]
## END
## status: 0
## timeout: 10

#### strip unquoted and quoted [a]
var='[a]foo[]'
echo ${var#[a]}
echo ${var#"[a]"}
echo "${var#[a]}"
echo "${var#"[a]"}"
## STDOUT:
[a]foo[]
foo[]
[a]foo[]
foo[]
## END
## status: 0
## timeout: 10

#### Nested % and # operators (bug reported by Crestwave)
var=$'\n'
argv.py "${var#?}"
argv.py "${var%''}"
argv.py "${var%"${var#?}"}"
var='a'
argv.py "${var#?}"
argv.py "${var%''}"
argv.py "${var%"${var#?}"}"
## stdout:
## status: 127
## timeout: 10

#### strip * (bug regression)
x=abc
argv.py "${x#*}"
argv.py "${x##*}"
argv.py "${x%*}"
argv.py "${x%%*}"
## stdout:
## status: 127
## timeout: 10

#### strip ?
x=abc
argv.py "${x#?}"
argv.py "${x##?}"
argv.py "${x%?}"
argv.py "${x%%?}"
## stdout:
## status: 127
## timeout: 10

#### strip all
x=abc
argv.py "${x#abc}"
argv.py "${x##abc}"
argv.py "${x%abc}"
argv.py "${x%%abc}"
## stdout:
## status: 127
## timeout: 10

#### strip none
x=abc
argv.py "${x#}"
argv.py "${x##}"
argv.py "${x%}"
argv.py "${x%%}"
## stdout:
## status: 127
## timeout: 10

#### strip all unicode
x=μabcμ
echo "${x#?abc?}"
echo "${x##?abc?}"
echo "${x%?abc?}"
echo "${x%%?abc?}"
## STDOUT:




## END
## status: 0
## timeout: 10

#### strip none unicode
x=μabcμ
argv.py "${x#}"
argv.py "${x##}"
argv.py "${x%}"
argv.py "${x%%}"
## stdout:
## status: 127
## timeout: 10

#### Strip Right Brace (#702)
var='$foo'
echo 1 "${var#$foo}"
echo 2 "${var#\$foo}"

var='}'
echo 10 "${var#}}"
echo 11 "${var#\}}"
echo 12 "${var#'}'}"
echo 13 "${var#"}"}"
## STDOUT:
1 $foo
2 
10 }}
11 
12 
13 
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-strip var-op-strip (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### \(\) in pattern (regression)
x='foo()' 
echo 1 ${x%*\(\)}
echo 2 ${x%%*\(\)}
echo 3 ${x#*\(\)}
echo 4 ${x##*\(\)}
## STDOUT:
1 foo
2
3
4
## END
## status: 0
## timeout: 10

#### extglob in pattern
case $SH in dash|zsh|ash) exit ;; esac

shopt -s extglob

x='foo()' 
echo 1 ${x%*(foo|bar)'()'}
echo 2 ${x%%*(foo|bar)'()'}
echo 3 ${x#*(foo|bar)'()'}
echo 4 ${x##*(foo|bar)'()'}
## STDOUT:
1 foo
2
3
4
## END
## status: 0
## timeout: 10
## real-launcher
