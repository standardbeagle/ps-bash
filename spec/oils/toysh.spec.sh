## Imported from the Oils spec tests (toysh.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### extglob $IFS 1
# http://landley.net/notes.html#12-06-2020
shopt -s extglob

touch abc\)d
echo ab+(c?d)

IFS=c ABC="c?d"
echo ab+($ABC)

ABC='*'
echo $ABC
## STDOUT:
abc)d
ab+( ?d)
abc)d
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### extglob $IFS 2
# http://landley.net/notes.html#17-05-2020

shopt -s extglob  # required for bash, not osh
IFS=x; ABC=cxd; for i in +($ABC); do echo =$i=; done
## STDOUT:
=+(c=
=d)=
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### char class / extglob
# http://landley.net/notes.html#14-05-2020
shopt -s extglob

touch l; echo [hello"]"

touch b
echo [$(echo abc)]

touch +
echo [+()]
echo [+(])
## STDOUT:
[hello]
b
+
[+(])
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### patsub of $* - http://landley.net/notes.html#23-04-2020
chicken() { echo ${*/b c/ghi}; }; chicken a b c d
## stdout: a b c d
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Brace Expansion
# http://landley.net/notes.html#04-01-2020

HOME=/home/foo

echo {~,~root}/pwd
echo \{~,~root}/pwd
echo ""{~,~root}/pwd
## STDOUT:
/home/foo/pwd /root/pwd
{~,~root}/pwd
~/pwd ~root/pwd
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### {abc}<<< - http://landley.net/notes-2019.html#09-12-2019
{ echo; } {abc}<<< walrus
cat <&$abc
## STDOUT:

walrus
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### slice of @ and @ - http://landley.net/notes.html#23-04-2020
IFS=x; X=x; eval abc=a${X}b

chicken() { for i in "${@:3:5}"; do echo =$i=; done; } ; chicken ab cd ef gh ij kl mn op qr

chicken() { for i in "${*:3:5}"; do echo =$i=; done; } ; chicken ab cd ef gh ij kl mn op qr
## STDOUT:
=ef=
=gh=
=ij=
=kl=
=mn=
=ef gh ij kl mn=
## END
## status: 0
## XFAIL ps-bash gap-oils-toysh toysh (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### @Q
# http://landley.net/notes.html#24-06-2020

# Fix these
case $SH in dash|mksh|zsh) exit ;; esac

xx() { echo "${*@Q}";}; xx a b c d
xx() { echo "${@@Q}";}; xx a b c d
## STDOUT:
'a' 'b' 'c' 'd'
'a' 'b' 'c' 'd'
## END
## status: 0
## timeout: 10
## real-launcher
