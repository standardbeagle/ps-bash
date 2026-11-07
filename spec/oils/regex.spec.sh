## Imported from the Oils spec tests (regex.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### BASH_REMATCH
[[ foo123 =~ ([a-z]+)([0-9]+) ]]
echo status=$?
argv.py "${BASH_REMATCH[@]}"

[[ failed =~ ([a-z]+)([0-9]+) ]]
echo status=$?
argv.py "${BASH_REMATCH[@]}"  # not cleared!
## STDOUT:
status=0
status=1
## END
## status: 127
## timeout: 10

#### Match is unanchored at both ends
[[ 'bar' =~ a ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Failed match
[[ 'bar' =~ X ]] && echo true
## stdout:
## status: 1
## timeout: 10

#### Regex quoted with \ -- preferred in bash
[[ 'a b' =~ ^(a\ b)$ ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Regex quoted with single quotes
# bash doesn't like the quotes
[[ 'a b' =~ '^(a b)$' ]] && echo true
## stdout:
## status: 1
## timeout: 10

#### Regex quoted with double quotes
# bash doesn't like the quotes
[[ 'a b' =~ "^(a b)$" ]] && echo true
## stdout:
## status: 1
## timeout: 10

#### Fix single quotes by storing in variable
pat='^(a b)$'
[[ 'a b' =~ $pat ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Fix single quotes by storing in variable
pat="^(a b)$"
[[ 'a b' =~ $pat ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Double quoting pat variable -- again bash doesn't like it.
pat="^(a b)$"
[[ 'a b' =~ "$pat" ]] && echo true
## stdout:
## status: 1
## timeout: 10

#### Mixing quoted and unquoted parts
[[ 'a b' =~ 'a 'b ]] && echo true
[[ "a b" =~ "a "'b' ]] && echo true
## STDOUT:
true
true
## END
## status: 0
## timeout: 10

#### Regex with == and not =~ is parse error, different lexer mode required
# They both give a syntax error.  This is lame.
[[ '^(a b)$' == ^(a\ b)$ ]] && echo true
## stdout:
## status: 2
## timeout: 10

#### Omitting ( )
[[ '^a b$' == ^a\ b$ ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Malformed regex
# Are they trying to PARSE the regex?  Do they feed the buffer directly to
# regcomp()?
[[ 'a b' =~ ^)a\ b($ ]] && echo true
## stdout:
## status: 2
## timeout: 10

#### Regex with |
[[ 'bar' =~ foo|bar ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### Regex to match literal brackets []

# bash-completion relies on this, so we're making it match bash.
# zsh understandably differs.
[[ '[]' =~ \[\] ]] && echo true

# Another way to write this.
pat='\[\]'
[[ '[]' =~ $pat ]] && echo true
## STDOUT:
true
true
## END
## status: 0
## timeout: 10

#### Regex to match literals . ^ $ etc.
[[ 'x' =~ \. ]] || echo false
[[ '.' =~ \. ]] && echo true

[[ 'xx' =~ \^\$ ]] || echo false
[[ '^$' =~ \^\$ ]] && echo true

[[ 'xxx' =~ \+\*\? ]] || echo false
[[ '*+?' =~ \*\+\? ]] && echo true

[[ 'xx' =~ \{\} ]] || echo false
[[ '{}' =~ \{\} ]] && echo true
## STDOUT:
false
true
false
true
false
true
false
true
## END
## status: 0
## timeout: 10

#### Unquoted { is a regex parse error
[[ { =~ { ]] && echo true
echo status=$?
## stdout: status=2
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Fatal error inside [[ =~ ]]

# zsh and osh are stricter than bash.  bash treats [[ like a command.

[[ a =~ $(( 1 / 0 )) ]]
echo status=$?
## stdout: status=1
## status: 0
## timeout: 10

#### Quoted { and +
[[ { =~ "{" ]] && echo 'yes {'
[[ + =~ "+" ]] && echo 'yes +'
[[ * =~ "*" ]] && echo 'yes *'
[[ ? =~ "?" ]] && echo 'yes ?'
[[ ^ =~ "^" ]] && echo 'yes ^'
[[ $ =~ "$" ]] && echo 'yes $'
[[ '(' =~ '(' ]] && echo 'yes ('
[[ ')' =~ ')' ]] && echo 'yes )'
[[ '|' =~ '|' ]] && echo 'yes |'
[[ '\' =~ '\' ]] && echo 'yes \'
echo ---

[[ . =~ "." ]] && echo 'yes .'
[[ z =~ "." ]] || echo 'no .'
echo ---

# This rule is weird but all shells agree.  I would expect that the - gets
# escaped?  It's an operator?  but it behaves like a-z.
[[ a =~ ["a-z"] ]]; echo "a $?"
[[ - =~ ["a-z"] ]]; echo "- $?"
[[ b =~ ['a-z'] ]]; echo "b $?"
[[ z =~ ['a-z'] ]]; echo "z $?"

echo status=$?
## STDOUT:
yes {
yes +
yes *
yes ?
yes ^
yes $
yes (
yes )
yes |
yes \
---
yes .
no .
---
a 0
- 1
b 0
z 0
status=0
## END
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Escaped {
# from bash-completion
[[ '$PA' =~ ^(\$\{?)([A-Za-z0-9_]*)$ ]] && argv.py "${BASH_REMATCH[@]}"
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Escaped { stored in variable first
# from bash-completion
pat='^(\$\{?)([A-Za-z0-9_]*)$'
[[ '$PA' =~ $pat ]] && argv.py "${BASH_REMATCH[@]}"
## stdout:
## status: 127
## timeout: 10

#### regex with ?
[[ 'c' =~ c? ]] && echo true
[[ '' =~ c? ]] && echo true
## STDOUT:
true
true
## END
## status: 0
## timeout: 10

#### regex with unprintable characters
# can't have nul byte

# This pattern has literal characters
pat=$'^[\x01\x02]+$'

[[ $'\x01\x02\x01' =~ $pat ]]; echo status=$?
[[ $'a\x01' =~ $pat ]]; echo status=$?

# NOTE: There doesn't appear to be any way to escape these!
pat2='^[\x01\x02]+$'
## STDOUT:
status=0
status=1
## END
## status: 0
## timeout: 10

#### pattern $f(x)  -- regression
f=fff
[[ fffx =~ $f(x) ]]
echo status=$?
[[ ffx =~ $f(x) ]]
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## timeout: 10

#### pattern a=(1)
[[ a=x =~ a=(x) ]]
echo status=$?
[[ =x =~ a=(x) ]]
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## timeout: 10

#### pattern @f(x)
shopt -s parse_at
[[ @fx =~ @f(x) ]]
echo status=$?
[[ fx =~ @f(x) ]]
echo status=$?
## STDOUT:
status=0
status=1
## END
## status: 0
## timeout: 10

#### Bug: Nix idiom with closing ) next to pattern

if [[ ! (" ${params[*]} " =~ " -shared " || " ${params[*]} " =~ " -static ") ]]; then
  echo one
fi

# Reduced idiom
if [[ (foo =~ foo) ]]; then
  echo two
fi
## STDOUT:
one
two
## END
## status: 0
## timeout: 10

#### unquoted (a  b) as pattern, (a  b|c)

if [[ 'a  b' =~ (a  b) ]]; then
  echo one
fi

if [[ 'a b' =~ (a  b) ]]; then
  echo BAD
fi

if [[ 'a b' =~ (a b|c) ]]; then
  echo two
fi

# I think spaces are only allowed within ()

if [[ '  c' =~ (a|  c) ]]; then
  echo three
fi
## STDOUT:
one
two
three
## END
## status: 0
## timeout: 10

#### Multiple adjacent () groups

if [[ 'a-b-c-d' =~ a-(b|  >>)-c-( ;|[de])|ff|gg ]]; then
  argv.py "${BASH_REMATCH[@]}"
fi

if [[ ff =~ a-(b|  >>)-c-( ;|[de])|ff|gg ]]; then
  argv.py "${BASH_REMATCH[@]}"
fi

# empty group ()

if [[ zz =~ ([a-z]+)() ]]; then
  argv.py "${BASH_REMATCH[@]}"
fi

# nested empty group
if [[ zz =~ ([a-z]+)(()z) ]]; then
  argv.py "${BASH_REMATCH[@]}"
fi
## stdout:
## status: 127
## timeout: 10

#### c|a unquoted

if [[ a =~ c|a ]]; then
  echo one
fi
## stdout: one
## status: 0
## timeout: 10

#### Parse error with 2 words

if [[ a =~ c a ]]; then
  echo one
fi
## stdout:
## status: 2
## timeout: 10

#### make a lisp example

str='(hi)'
[[ "${str}" =~ ^^([][{}\(\)^@])|^(~@)|(\"(\\.|[^\\\"])*\")|^(;[^$'\n']*)|^([~\'\`])|^([^][ ~\`\'\";{}\(\)^@\,]+)|^[,]|^[[:space:]]+ ]]
echo status=$?

m=${BASH_REMATCH[0]}
echo m=$m
## STDOUT:
status=0
m=(
## END
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Operators and space lose meaning inside ()
[[ '< >' =~ (< >) ]] && echo true
## stdout: true
## status: 0
## timeout: 10

#### unquoted [a  b] as pattern, [a  b|c]

$SH <<'EOF'
[[ a =~ [ab] ]] && echo yes
EOF
echo "[ab]=$?"

$SH <<'EOF'
[[ a =~ [a b] ]] && echo yes
EOF
echo "[a b]=$?"

$SH <<'EOF'
[[ a =~ ([a b]) ]] && echo yes
EOF
echo "[a b]=$?"
## STDOUT:
yes
[ab]=0
[a b]=2
yes
[a b]=0
## END
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Operator chars ; & but not |

# Hm semicolon is still an operator in bash
$SH <<'EOF'
[[ ';' =~ ; ]] && echo semi
EOF
echo semi=$?

$SH <<'EOF'
[[ ';' =~ (;) ]] && echo semi paren
EOF
echo semi paren=$?

echo

$SH <<'EOF'
[[ '&' =~ & ]] && echo amp
EOF
echo amp=$?

# Oh I guess this is not a bug?  regcomp doesn't reject this trivial regex?
$SH <<'EOF'
[[ '|' =~ | ]] && echo pipe1
[[ 'a' =~ | ]] && echo pipe2
EOF
echo pipe=$?

$SH <<'EOF'
[[ '|' =~ a| ]] && echo four
EOF
echo pipe=$?

# This is probably special because > operator is inside foo [[ a > b ]]
$SH <<'EOF'
[[ '<>' =~ <> ]] && echo angle
EOF
echo angle=$?

# Bug: OSH allowed this!
$SH <<'EOF'
[[ $'a\nb' =~ a
b ]] && echo newline
EOF
echo newline=$?
## STDOUT:
semi=2
semi paren
semi paren=0

amp=2
pipe1
pipe2
pipe=0
four
pipe=0
angle=2
newline=2
## END
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Quotes '' "" $'' $"" in pattern

$SH <<'EOF'
[[ '|' =~ '|' ]] && echo sq
EOF
echo sq=$?

$SH <<'EOF'
[[ '|' =~ "|" ]] && echo dq
EOF
echo dq=$?

$SH <<'EOF'
[[ '|' =~ $'|' ]] && echo dollar-sq
EOF
echo dollar-sq=$?

$SH <<'EOF'
[[ '|' =~ $"|" ]] && echo dollar-dq
EOF
echo dollar-dq=$?
## STDOUT:
sq
sq=0
dq
dq=0
dollar-sq
dollar-sq=0
dollar-dq
dollar-dq=0
## END
## status: 0
## XFAIL ps-bash gap-oils-regex regex (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Unicode in pattern

$SH <<'EOF'
[[ μ =~ μ ]] && echo mu
EOF
echo mu=$?
## STDOUT:
mu
mu=0
## END
## status: 0
## timeout: 10
## real-launcher
