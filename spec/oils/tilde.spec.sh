## Imported from the Oils spec tests (tilde.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### ~ expansion in assignment
HOME=/home/bob
a=~/src
echo $a
## stdout: /home/bob/src
## status: 0
## timeout: 10

#### ~ expansion in readonly assignment
# dash fails here!
# http://stackoverflow.com/questions/8441473/tilde-expansion-doesnt-work-when-i-logged-into-gui
HOME=/home/bob
readonly const=~/src
echo $const
## stdout: /home/bob/src
## status: 0
## timeout: 10

#### No ~ expansion in dynamic assignment
HOME=/home/bob
binding='const=~/src'
readonly "$binding"
echo $const
## stdout: ~/src
## status: 0
## XFAIL ps-bash gap-oils-tilde tilde (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### No tilde expansion in word that looks like assignment but isn't
# bash and mksh mistakenly expand here!
# bash fixes this in POSIX mode (gah).
# http://lists.gnu.org/archive/html/bug-bash/2016-06/msg00001.html
HOME=/home/bob
echo x=~
## stdout: x=/home/bob
## status: 0
## timeout: 10

#### other user
echo ~nonexistent
# zsh doesn't like nonexistent
## stdout: ~nonexistent
## status: 0
## timeout: 10

#### ${undef:-~}
HOME=/home/bar
echo ${undef:-~}
echo ${HOME:+~/z}
echo "${undef:-~}"
echo ${undef:-"~"}
## STDOUT:
/home/bar
/home/bar/z
~
~
## END
## status: 0
## timeout: 10
## XFAIL ps-bash triage differs from bash 5.2 (auto-marked; investigate)

#### ${x//~/~root}
HOME=/home/bar
x=~
echo ${x//~/~root}

# gah there is some expansion, what the hell
echo ${HOME//~/~root}

x=[$HOME]
echo ${x//~/~root}
## STDOUT:
/root
/root
[/root]
## END
## status: 0
## XFAIL ps-bash gap-oils-tilde tilde (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### x=foo:~ has tilde expansion
HOME=/home/bar
x=foo:~
echo $x
echo "$x"  # quotes don't matter, the expansion happens on assignment?
x='foo:~'
echo $x

x=foo:~,  # comma ruins it, must be /
echo $x

x=~:foo
echo $x

# no tilde expansion here
echo foo:~
## STDOUT:
foo:/home/bar
foo:/home/bar
foo:~
foo:~,
/home/bar:foo
foo:~
## END
## status: 0
## timeout: 10

#### tilde expansion an assignment keyword
HOME=/home/bar
f() {
  local x=foo:~
  echo $x
}
f
## stdout: foo:/home/bar
## status: 0
## timeout: 10

#### x=${undef-~:~}
HOME=/home/bar

x=~:${undef-~:~}
echo $x

# Most shells agree on a different behavior, but with the OSH parsing model,
# it's easier to agree with yash.  bash disagrees in a different way
## stdout: /home/bar:/home/bar:/home/bar
## status: 0
## XFAIL ps-bash gap-oils-tilde tilde (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### strict tilde
echo ~nonexistent

shopt -s strict_tilde
echo ~nonexistent

echo status=$?
## STDOUT:
~nonexistent
~nonexistent
status=0
## END
## status: 0
## timeout: 10

#### temp assignment x=~ env

HOME=/home/bar

xx=~ env | grep xx=

# Does it respect the colon rule too?
xx=~root:~:~ env | grep xx=
## STDOUT:
xx=/home/bar
xx=/root:/home/bar:/home/bar
## END
## status: 0
## XFAIL ps-bash gap-oils-tilde tilde (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### a[x]=foo:~ has tilde expansion
case $SH in dash|zsh) exit ;; esac

HOME=/home/bar
declare -a a
a[0]=foo:~
echo ${a[0]}

declare -A A
A['x']=foo:~
echo ${A['x']}
## STDOUT:
foo:/home/bar
foo:/home/bar
## END
## status: 0
## timeout: 10
## real-launcher
