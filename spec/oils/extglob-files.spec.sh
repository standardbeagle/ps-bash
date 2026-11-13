## Imported from the Oils spec tests (extglob-files.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### @() matches exactly one of the patterns
shopt -s extglob
mkdir -p 0
cd 0
touch {foo,bar}.cc {foo,bar,baz}.h
echo @(*.cc|*.h)
## stdout: bar.cc bar.h baz.h foo.cc foo.h
## status: 0
## timeout: 10

#### ?() matches 0 or 1
shopt -s extglob
mkdir -p 1
cd 1
touch {foo,bar}.cc {foo,bar,baz}.h foo. foo.hh
ext=cc
echo foo.?($ext|h)
## stdout: foo. foo.cc foo.h
## status: 0
## XFAIL ps-bash gap-oils-extglob-files extglob-files (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### *() matches 0 or more
shopt -s extglob
mkdir -p eg1
touch eg1/_ eg1/_One eg1/_OneOne eg1/_TwoTwo eg1/_OneTwo
echo eg1/_*(One|Two)
## stdout: eg1/_ eg1/_One eg1/_OneOne eg1/_OneTwo eg1/_TwoTwo
## status: 0
## timeout: 10

#### +() matches 1 or more
shopt -s extglob
mkdir -p eg2
touch eg2/_ eg2/_One eg2/_OneOne eg2/_TwoTwo eg2/_OneTwo
echo eg2/_+(One|$(echo Two))
## stdout: eg2/_One eg2/_OneOne eg2/_OneTwo eg2/_TwoTwo
## status: 0
## timeout: 10

#### !(*.h|*.cc) to match everything except C++
shopt -s extglob
mkdir -p extglob2
touch extglob2/{foo,bar}.cc extglob2/{foo,bar,baz}.h \
      extglob2/{foo,bar,baz}.py
echo extglob2/!(*.h|*.cc)
## stdout: extglob2/bar.py extglob2/baz.py extglob2/foo.py
## status: 0
## timeout: 10

#### Two adjacent alternations
shopt -s extglob
mkdir -p 2
touch 2/{aa,ab,ac,ba,bb,bc,ca,cb,cc}
echo 2/!(b)@(b|c)
echo 2/!(b)?@(b|c)  # wildcard in between
echo 2/!(b)a@(b|c)  # constant in between
## STDOUT:
2/ab 2/ac 2/cb 2/cc
2/ab 2/ac 2/bb 2/bc 2/cb 2/cc
2/ab 2/ac
## END
## status: 0
## timeout: 10

#### Nested extended glob pattern
shopt -s extglob
mkdir -p eg6
touch eg6/{ab,ac,ad,az,bc,bd}
echo eg6/a@(!(c|d))
echo eg6/a!(@(ab|b*))
## STDOUT:
eg6/ab eg6/az
eg6/ac eg6/ad eg6/az
## END
## status: 0
## timeout: 10

#### Extended glob patterns with spaces
shopt -s extglob
mkdir -p eg4
touch eg4/a 'eg4/a b' eg4/foo
argv.py eg4/@(a b|foo)
## stdout:
## status: 127
## timeout: 10

#### Filenames with spaces
shopt -s extglob
mkdir -p eg5
touch eg5/'a b'{cd,de,ef}
argv.py eg5/'a '@(bcd|bde|zzz)
## stdout:
## status: 127
## timeout: 10

#### nullglob with extended glob
shopt -s extglob
mkdir eg6
argv.py eg6/@(no|matches)  # no matches
shopt -s nullglob  # test this too
argv.py eg6/@(no|matches)  # no matches
## stdout:
## status: 127
## timeout: 10

#### Glob other punctuation chars (lexer mode)
shopt -s extglob
mkdir -p eg5
cd eg5
touch __{aa,'<>','{}','#','&&'}
argv.py @(__aa|'__<>'|__{}|__#|__&&|)

# mksh sorts them differently
## stdout:
## status: 127
## XFAIL ps-bash gap-oils-extglob-files extglob-files (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### More glob escaping
shopt -s extglob
mkdir -p eg7
cd eg7
touch '_[:]' '_*' '_?'
argv.py @('_[:]'|'_*'|'_?')
argv.py @(nested|'_?'|@('_[:]'|'_*'))

# mksh sorts them differently
## stdout:
## status: 127
## timeout: 10

#### Escaping of pipe (glibc bug, see demo/glibc_fnmatch.c)
shopt -s extglob

mkdir -p extpipe
cd extpipe

touch '__|' foo
argv.py @('foo'|__\||bar)
argv.py @('foo'|'__|'|bar)
## stdout:
## status: 127
## timeout: 10

#### Extended glob as argument to ${undef:-} (dynamic globbing)

# This case popped into my mind after inspecting osh/word_eval.py for calls to
# _EvalWordToParts()

shopt -s extglob

mkdir -p eg8
cd eg8
touch {foo,bar,spam}.py

# regular glob
echo ${undef:-*.py}

# extended glob
echo ${undef:-@(foo|bar).py}
## STDOUT:
bar.py foo.py spam.py
bar.py foo.py
## END
## status: 0
## XFAIL ps-bash gap-oils-extglob-files extglob-files (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Extended glob in assignment builtin

# Another invocation of _EvalWordToParts() that OSH should handle

shopt -s extglob
mkdir -p eg9
cd eg9
touch {foo,bar}.py
typeset -@(*.py) myvar
echo status=$?
## stdout: status=2
## status: 0
## timeout: 10

#### Extended glob in same word as array
shopt -s extglob
mkdir -p eg10
cd eg10

touch {'a b c',bee,cee}.{py,cc}
set -- 'a b' 'c'

argv.py "$@"

# This works!
argv.py star glob "$*"*.py
argv.py star extglob "$*"*@(.py|cc)

# Hm this actually still works!  the first two parts are literal.  And then
# there's something like the simple_word_eval algorithm on the rest.  Gah.
argv.py at extglob "$@"*@(.py|cc)
## stdout:
## status: 127
## timeout: 10

#### Extended glob with word splitting
shopt -s extglob
mkdir -p 3
cd 3

x='a b'
touch bar.{cc,h}

# OSH may disallow splitting when there's an extended glob
argv.py $x*.@(cc|h)
## stdout:
## status: 127
## timeout: 10

#### In Array Literal and for loop
shopt -s extglob
mkdir -p eg11
cd eg11
touch {foo,bar,spam}.py
for x in @(fo*|bar).py; do
  echo $x
done

echo ---
declare -a A
A=(zzz @(fo*|bar).py)
echo "${A[@]}"
## STDOUT:
bar.py
foo.py
---
zzz bar.py foo.py
## END
## status: 0
## timeout: 10

#### no_dash_glob
shopt -s extglob
mkdir -p opts
cd opts

touch -- foo bar -dash
echo @(*)

shopt --set no_dash_glob
echo @(*)
## STDOUT:
-dash bar foo
-dash bar foo
## END
## status: 0
## timeout: 10

#### noglob
shopt -s extglob
mkdir -p _noglob
cd _noglob

set -o noglob
echo @(*)
echo @(__nope__*|__nope__?|'*'|'?'|'[:alpha:]'|'|')
## STDOUT:
@(*)
@(__nope__*|__nope__?|*|?|[:alpha:]||)
## END
## status: 0
## XFAIL ps-bash gap-oils-extglob-files extglob-files (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### failglob
shopt -s extglob

rm -f _failglob/*
mkdir -p _failglob
cd _failglob

shopt -s failglob
echo @(*)
echo status=$?

touch foo
echo @(*)
echo status=$?
## STDOUT:
status=1
foo
status=0
## END
## status: 0
## timeout: 10

#### no match
shopt -s extglob
echo @(__nope__)

# OSH has glob quoting here
echo @(__nope__*|__nope__?|'*'|'?'|'[:alpha:]'|'|')

if test $SH != osh; then
  exit
fi

# OSH has this alias for @()
echo ,(osh|style)
## STDOUT:
@(__nope__)
@(__nope__*|__nope__?|*|?|[:alpha:]||)
## END
## status: 0
## XFAIL ps-bash gap-oils-extglob-files extglob-files (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
