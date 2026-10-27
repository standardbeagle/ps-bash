## Imported from the Oils spec tests (var-op-patsub.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Pattern replacement
v=abcde
echo ${v/c*/XX}
## stdout: abXX
## status: 0
## timeout: 10

#### Pattern replacement on unset variable
echo -${v/x/y}-
echo status=$?
set -o nounset  # make sure this fails
echo -${v/x/y}-
## STDOUT:
--
status=0
## END
## status: 1
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Replace is longest match
# If it were shortest, then you would just replace the first <html>
s='begin <html></html> end'
echo ${s/<*>/[]}
## stdout: begin [] end
## status: 0
## timeout: 10

#### When LC_ALL=C, pattern ? doesn't match multibyte character
export LC_ALL='C'

s='_μ_ and _μ_'

# ? should match one char

echo ${s//_?_/foo}  # all
echo ${s/#_?_/foo}  # left
echo ${s/%_?_/foo}  # right
echo

a='_x_ and _y_'

echo ${a//_?_/foo}  # all
echo ${a/#_?_/foo}  # left
echo ${a/%_?_/foo}  # right
## STDOUT:
_μ_ and _μ_
_μ_ and _μ_
_μ_ and _μ_

foo and foo
foo and _y_
_x_ and foo
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${x/^} regression
x=abc
echo ${x/^}
echo ${x/!}

y=^^^
echo ${y/^}
echo ${y/!}

z=!!!
echo ${z/^}
echo ${z/!}

s=a^b!c
echo ${s/a^}
echo ${s/b!}
## STDOUT:
abc
abc
^^
^^^
!!!
!!
b!c
a^c
## END
## status: 0
## timeout: 10

#### \(\) in pattern (regression)

# Not extended globs
x='foo()' 
echo 1 ${x//*\(\)/z}
echo 2 ${x//*\(\)/z}
echo 3 ${x//\(\)/z}
echo 4 ${x//*\(\)/z}
## STDOUT:
1 z
2 z
3 fooz
4 z
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### patsub with single quotes and hyphen in character class (regression)

# from Crestwave's bf.bash

program='^++--hello.,world<>[]'
program=${program//[^'><+-.,[]']} 
echo $program
## stdout: ++--.,<>[]
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### patsub with [^]]

# This is a PARSING divergence.  In OSH we match [], rather than using POSIX
# rules!

pat='[^]]'
s='ab^cd^'
echo ${s//$pat/z}
## stdout: ab^cd^
## status: 0
## timeout: 10

#### [a-z] Invalid range end is syntax error
x=fooz
pat='[z-a]'  # Invalid range.  Other shells don't catch it!
#pat='[a-y]'
echo ${x//$pat}
echo status=$?
## STDOUT:
fooz
status=0
## END
## status: 0
## timeout: 10

#### Pattern is empty $foo$bar -- regression for infinite loop

x=-foo-

echo ${x//$foo$bar/bar}


# feels like memory unsafety in ZSH
## stdout: -foo-
## status: 0
## timeout: 10

#### ${x//~homedir/}

path=~/git/oilshell

# ~ expansion occurs
#echo path=$path

echo ${path//~/z}

echo ${path/~/z}
## STDOUT:
z/git/oilshell
z/git/oilshell
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Replace fixed strings
s=xx_xx
echo ${s/xx/yy} ${s//xx/yy} ${s/#xx/yy} ${s/%xx/yy}
## stdout: yy_xx yy_yy yy_xx xx_yy
## status: 0
## timeout: 10

#### Replace char class
s=xx_xx_xx
echo ${s//[[:alpha:]]/y} ${s//[^[:alpha:]]/-}
## stdout: yy_yy_yy xx-xx-xx
## status: 0
## timeout: 10

#### Replace hard glob
s='aa*bb+cc'
echo ${s//\**+/__}  # Literal *, then any sequence of characters, then literal +
## stdout: aa__cc
## status: 0
## timeout: 10

#### ${v/} is empty search and replacement
v=abcde
echo -${v/}-
echo status=$?
## STDOUT:
-abcde-
status=0
## END
## status: 0
## timeout: 10

#### ${v//} is empty search and replacement
v='a/b/c'
echo -${v//}-
echo status=$?
## STDOUT:
-a/b/c-
status=0
## END
## status: 0
## timeout: 10

#### Confusing unquoted slash matches bash (and ash)
x='/_/'
echo ${x////c}

echo ${x//'/'/c}
## STDOUT:
c_c
c_c
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Synthesized ${x///} bug (similar to above)

# found via test/parse-errors.sh

x='slash / brace } hi'
echo 'ambiguous:' ${x///}

echo 'quoted:   ' ${x//'/'}

# Wow we have all combination here -- TERRIBLE
## STDOUT:
ambiguous: slash brace } hi
quoted:    slash brace } hi
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${v/a} is the same as ${v/a/}  -- no replacement string
v='aabb'
echo ${v/a}
echo status=$?
## STDOUT:
abb
status=0
## END
## status: 0
## timeout: 10

#### Replacement with special chars (bug fix)
v=xx
echo ${v/x/"?"}
## stdout: ?x
## status: 0
## timeout: 10

#### Replace backslash
v='[\f]'
x='\f'
echo ${v/"$x"/_}

# mksh and zsh differ on this case, but this is consistent with the fact that
# \f as a glob means 'f', not '\f'.  TODO: Warn that it's a bad glob?
# The canonical form is 'f'.
echo ${v/$x/_}

echo ${v/\f/_}
echo ${v/\\f/_}
## STDOUT:
[_]
[\_]
[\_]
[_]
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Replace right ]
v='--]--'
x=']'
echo ${v/"$x"/_}
echo ${v/$x/_}
## STDOUT:
--_--
--_--
## END
## status: 0
## timeout: 10

#### Substitute glob characters in pattern, quoted and unquoted

# INFINITE LOOP in ash!
case $SH in ash) exit ;; esac

g='*'
v='a*b'
echo ${v//"$g"/-}
echo ${v//$g/-}
## STDOUT:
a-b
-
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Substitute one unicode character (UTF-8)
export LANG='en_US.UTF-8'

s='_μ_ and _μ_'

# ? should match one char

echo ${s//_?_/foo}  # all
echo ${s/#_?_/foo}  # left
echo ${s/%_?_/foo}  # right
## STDOUT:
foo and foo
foo and _μ_
_μ_ and foo
## END
## status: 0
## timeout: 10

#### Chromium from http://www.oilshell.org/blog/2016/11/07.html

case $SH in zsh) exit ;; esac

HOST_PATH=/foo/bar/baz
echo ${HOST_PATH////\\/}

# The way bash parses it
echo ${HOST_PATH//'/'/\\/}


# zsh has crazy bugs
## STDOUT:
\/foo\/bar\/baz
\/foo\/bar\/baz
## END
## status: 0
## XFAIL ps-bash gap-oils-var-op-patsub var-op-patsub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
