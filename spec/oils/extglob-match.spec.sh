## Imported from the Oils spec tests (extglob-match.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### @ matches exactly one
[[ --verbose == --@(help|verbose) ]] && echo TRUE
[[ --oops == --@(help|verbose) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### @() with variable arms
choice1='help'
choice2='verbose'
[[ --verbose == --@($choice1|$choice2) ]] && echo TRUE
[[ --oops == --@($choice1|$choice2) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### extglob in variable
shopt -s extglob

# this syntax requires extglob in bash!!
# OSH never allows it
g=--@(help|verbose)

quoted='--@(help|verbose)'

[[ --help == $g ]] && echo TRUE
[[ --verbose == $g ]] && echo TRUE
[[ -- == $g ]] || echo FALSE
[[ --help == $q ]] || echo FALSE
[[ -- == $q ]] || echo FALSE
## STDOUT:
TRUE
TRUE
FALSE
FALSE
FALSE
## END
## status: 0
## timeout: 10

#### Matching literal '@(cc)'

# extglob is OFF.  Doesn't affect bash or mksh!
[[ cc == @(cc) ]] 
echo status=$?
[[ cc == '@(cc)' ]]
echo status=$?

shopt -s extglob

[[ cc == @(cc) ]]
echo status=$?
[[ cc == '@(cc)' ]]
echo status=$?
## STDOUT:
status=0
status=1
status=0
status=1
## END
## status: 0
## timeout: 10

#### nested @()
shopt -s extglob
pat='--@(help|verbose|no-@(long|short)-option)'
[[ --no-long-option == $pat ]] && echo TRUE
[[ --no-short-option == $pat ]] && echo TRUE
[[ --help == $pat ]] && echo TRUE
[[ --oops == $pat ]] || echo FALSE
## STDOUT:
TRUE
TRUE
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### nested @() with quotes and vars
shopt -s extglob
prefix=no
[[ --no-long-option == --@(help|verbose|$prefix-@(long|short)-'option') ]] &&
  echo TRUE
## stdout: TRUE
## status: 0
## timeout: 10

#### ? matches 0 or 1
[[ -- == --?(help|verbose) ]] && echo TRUE
[[ --oops == --?(help|verbose) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### + matches 1 or more
[[ --helphelp == --+(help|verbose) ]] && echo TRUE
[[ -- == --+(help|verbose) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### * matches 0 or more
[[ -- == --*(help|verbose) ]] && echo TRUE
[[ --oops == --*(help|verbose) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### simple repetition with *(foo) and +(Foo)
[[ foofoo == *(foo) ]] && echo TRUE
[[ foofoo == +(foo) ]] && echo TRUE
## STDOUT:
TRUE
TRUE
## END
## status: 0
## timeout: 10

#### ! matches none
[[ --oops == --!(help|verbose) ]] && echo TRUE
[[ --help == --!(help|verbose) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### match is anchored
[[ foo_ == @(foo) ]] || echo FALSE
[[ _foo == @(foo) ]] || echo FALSE
[[ foo == @(foo) ]] && echo TRUE
## STDOUT:
FALSE
FALSE
TRUE
## END
## status: 0
## timeout: 10

#### repeated match is anchored
[[ foofoo_ == +(foo) ]] || echo FALSE
[[ _foofoo == +(foo) ]] || echo FALSE
[[ foofoo == +(foo) ]] && echo TRUE
## STDOUT:
FALSE
FALSE
TRUE
## END
## status: 0
## timeout: 10

#### repetition with glob
# NOTE that * means two different things here
[[ foofoo_foo__foo___ == *(foo*) ]] && echo TRUE
[[ Xoofoo_foo__foo___ == *(foo*) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### No brace expansion in ==
[[ --X{a,b}X == --@(help|X{a,b}X) ]] && echo TRUE
[[ --oops == --@(help|X{a,b}X) ]] || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### adjacent extglob
[[ --help == @(--|++)@(help|verbose) ]] && echo TRUE
[[ ++verbose == @(--|++)@(help|verbose) ]] && echo TRUE
## STDOUT:
TRUE
TRUE
## END
## status: 0
## timeout: 10

#### nested extglob
[[ --help == --@(help|verbose=@(1|2)) ]] && echo TRUE
[[ --verbose=1 == --@(help|verbose=@(1|2)) ]] && echo TRUE
[[ --verbose=2 == --@(help|verbose=@(1|2)) ]] && echo TRUE
[[ --verbose == --@(help|verbose=@(1|2)) ]] || echo FALSE
## STDOUT:
TRUE
TRUE
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### extglob empty string
shopt -s extglob
[[ '' == @(foo|bar) ]] || echo FALSE
[[ '' == @(foo||bar) ]] && echo TRUE
## STDOUT:
FALSE
TRUE
## END
## status: 0
## timeout: 10

#### extglob empty pattern
shopt -s extglob
[[ '' == @() ]] && echo TRUE
[[ '' == @(||) ]] && echo TRUE
[[ X == @() ]] || echo FALSE
[[ '|' == @(||) ]] || echo FALSE
## STDOUT:
TRUE
TRUE
FALSE
FALSE
## END
## status: 0
## timeout: 10

#### case with extglob
shopt -s extglob
for word in --help --verbose --unmatched -- -zxzx -; do
  case $word in
    --@(help|verbose) )
      echo A
      continue
      ;;
    ( --?(b|c) )
      echo B
      continue
      ;;
    ( -+(x|z) )
      echo C
      continue
      ;;
    ( -*(x|z) )
      echo D
      continue
      ;;
    *)
      echo U
      continue
      ;;
  esac
done
## STDOUT:
A
A
U
B
C
D
## END
## status: 0
## timeout: 10

#### [[ $x == !($str) ]]
shopt -s extglob
empty=''
str='x'
[[ $empty == !($str) ]] && echo TRUE  # test glob match
[[ $str == !($str) ]]   || echo FALSE
## STDOUT:
TRUE
FALSE
## END
## status: 0
## timeout: 10

#### Turning extglob on changes the meaning of [[ !(str) ]] in bash
empty=''
str='x'
[[ !($empty) ]]  && echo TRUE   # test if $empty is empty
[[ !($str) ]]    || echo FALSE  # test if $str is empty
shopt -s extglob  # mksh doesn't have this
[[ !($empty) ]]  && echo TRUE   # negated glob
[[ !($str) ]]    && echo TRUE   # negated glob
## STDOUT:
TRUE
FALSE
TRUE
TRUE
## END
## status: 0
## XFAIL ps-bash gap-oils-extglob-match extglob-match (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### With extglob on, !($str) on the left or right of == has different meanings
shopt -s extglob
str='x'
[[ 1 == !($str) ]]  && echo TRUE   # glob match
## stdout: TRUE
## status: 0
## timeout: 10

#### extglob inside arg word
shopt -s extglob
[[ foo == @(foo|bar) ]] && echo rhs
[[ foo == ${unset:-@(foo|bar)} ]] && echo 'rhs arg'
[[ fo == ${unset:-@(foo|bar)} ]] || echo nope
## STDOUT:
rhs
rhs arg
nope
## END
## status: 0
## timeout: 10

#### extglob is not detected in regex!
shopt -s extglob
[[ foo =~ ^@(foo|bar)$ ]] || echo FALSE
## stdout: FALSE
## status: 0
## timeout: 10

#### regular glob of single unicode char
shopt -s extglob
[[ __a__ == __?__ ]]
echo $?
[[ __μ__ == __?__ ]]
echo $?
## STDOUT:
0
0
## END
## status: 0
## timeout: 10

#### extended glob of single unicode char
shopt -s extglob
[[ __a__ == @(__?__) ]]
echo $?
[[ __μ__ == @(__?__) ]]
echo $?
## STDOUT:
0
0
## END
## status: 0
## timeout: 10

#### Extended glob in ${x//pat/replace}
# not supported in OSH due to GlobToERE() strategy for positional info

shopt -s extglob
x=foo.py
echo ${x//@(?.py)/Z}
## stdout: foZ
## status: 0
## timeout: 10

#### Extended glob in ${x%PATTERN}

shopt -s extglob
x=foo.py
echo 'strip % ' ${x%.@(py|cc)}
echo 'strip %%' ${x%%.@(py|cc)}
echo 'strip # ' ${x#@(foo)}
echo 'strip ##' ${x##@(foo)}
## STDOUT:
strip %  foo
strip %% foo
strip #  .py
strip ## .py
## END
## status: 0
## timeout: 10
