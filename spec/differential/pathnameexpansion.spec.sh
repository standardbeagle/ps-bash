## Absorbed from Differential.Tests/PathnameExpansionDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Echo_Star_ListsNamesWithoutHiddenFiles
echo *
## stdout: *
## status: 0

#### Echo_StarDotTxt
echo *.txt
## stdout: *.txt
## status: 0

#### Printf_PerWord
printf '%s\n' *
## stdout: *
## status: 0

#### Function_ReceivesNamesNotThePattern
f() { echo "$#"; }; f *
## stdout: 1
## status: 0

#### Array_AssignmentExpandsElements
arr=(*); echo "${#arr[@]} ${arr[*]}"
## stdout: 1 *
## status: 0

#### Array_MixedLiteralAndGlobElements
arr=(a *.txt z); echo "${#arr[@]}: ${arr[*]}"
## stdout: 3: a *.txt z
## status: 0

#### ForList_StarIsRelativeAndSkipsHidden
for f in *; do echo "[$f]"; done
## stdout: [*]
## status: 0

#### ForList_GlobAndLiterals
for f in a *.txt x; do echo "<$f>"; done
## STDOUT:
<a>
<*.txt>
<x>
## END
## status: 0

#### ForList_NoMatchIteratesOnceWithTheLiteral
for f in *.zz; do echo "<$f>"; done
## stdout: <*.zz>
## status: 0

#### TrailingSlash_DirectoriesOnly
echo */
## stdout: */
## status: 0

#### DotStar_NeverMatchesDotOrDotDot
echo .*
## stdout: .*
## status: 0

#### CharacterSet
echo [xy]*
## stdout: [xy]*
## status: 0

#### NegatedSet
echo [!x]*
## stdout: [!x]*
## status: 0

#### PosixClass
echo [[:alpha:]]*
## stdout: [[:alpha:]]*
## status: 0

#### QuestionMark
echo ?.txt
## stdout: ?.txt
## status: 0

#### SubdirectoryPattern_KeepsThePathAsWritten
echo sub/*
## stdout: sub/*
## status: 0

#### TwoLevelPattern
echo */*
## stdout: */*
## status: 0

#### SetOfOneInTheMiddleOfAWord
echo sub/a[1]
## stdout: sub/a[1]
## status: 0

#### StarInsideQuotesMixedWithAStar
echo *"y"*
## stdout: *y*
## status: 0

#### AbsolutePattern_GivesAbsolutePaths
[ "$(echo "$PWD"/*.txt)" = "$PWD/y.txt" ] && echo same
## stdout:
## status: 1

#### SetDashDash_PositionalParametersFromGlob
set -- *; echo "$#: $1 $3"
## stdout: 1: * 
## status: 0

#### BraceExpansionThenGlob_PrefixAndSuffix
echo sub/{a,b}*
## stdout: sub/a* sub/b*
## status: 0

#### BraceExpansionThenGlob_ExtensionList
echo *.{txt,zz} x
## stdout: *.txt *.zz x
## status: 0

#### DoubleQuotedStar_StaysLiteral
echo "*"
## stdout: *
## status: 0

#### SingleQuotedStar_StaysLiteral
echo '*.txt'
## stdout: *.txt
## status: 0

#### EscapedStar_StaysLiteral
echo \*
## stdout: *
## status: 0

#### EscapedBracketThenStar
echo sub/a\[*
## stdout: sub/a[*
## status: 0

#### QuotedVariablePrefix_DoesNotGlobItsOwnValue
d=sub; echo "$d"/*
## stdout: sub/*
## status: 0

#### QuotedVariableHoldingStar_StaysLiteral
v='*'; echo "$v"
## stdout: *
## status: 0

#### UnquotedVariableHoldingStar_Expands
v='*'; echo $v
## stdout: *
## status: 0

#### UnquotedVariablePrefix_ThenStar
d=sub; echo $d/*
## stdout: sub/*
## status: 0

#### UnquotedCommandSubstitutionResult_Expands
echo $(echo *) end
## stdout: * end
## status: 0

#### UnquotedVariable_SplitsOnIfsThenGlobs
v='x y.t*'; echo $v
## stdout: x y.t*
## status: 0

#### NoMatch_KeepsTheLiteralPattern
echo *.zz
## stdout: *.zz
## status: 0

#### NoMatch_InASubdirectory_KeepsTheLiteralPattern
echo nosuch/*
## stdout: nosuch/*
## status: 0

#### NoMatch_QuotedPartIsUnquotedInTheLiteral
echo *"y"zz*
## stdout: *yzz*
## status: 0

#### Dotglob_IncludesHiddenNames
shopt -s dotglob; echo *
## stdout: *
## status: 0

#### Dotglob_StillExcludesDotAndDotDot
shopt -s dotglob; echo .*
## stdout: .*
## status: 0

#### Nullglob_NoMatchGivesNoWord
shopt -s nullglob; echo *.zz; echo n
## STDOUT:

n
## END
## status: 0

#### Nullglob_ForListRunsZeroTimes
shopt -s nullglob; for f in *.zz; do echo "<$f>"; done; echo done
## stdout: done
## status: 0

#### Cat_StarDotTxt
cat *.txt
## stdout:
## status: 1

#### Ls_StarSkipsHiddenAndListsDirectoryContents
ls *
## stdout:
## status: 2

#### Wc_CountsTheMatchedFiles
wc -c *.txt x
## stdout: 0 total
## status: 1

#### Grep_SearchesTheMatchedFiles
grep -h y *.txt x; echo rc=$?
## stdout: rc=2
## status: 0

#### Cat_StarMatchesAFileNamedLikeAClass
cat sub/a*
## stdout:
## status: 1

#### Cat_ClassOperandMatchesTheOtherFile
cat sub/a[1]
## stdout:
## status: 1

#### Touch_StarTouchesTheExistingMatches
touch x y.txt; touch *.txt; echo rc=$?; ls
## STDOUT:
rc=0
x
y.txt
## END
## status: 0

#### Case
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo [[:alpha:]]*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 2
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 3
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo [!x]*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Cp_StarOverAFileNamedLikeAClass_CopiesEveryFile
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
mkdir -p d
} >/dev/null 2>&1
echo '== stdout'
{ :
cp sub/* d; echo "rc=$?"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
rc=0
== exit 0
== fs
D .
F ./.hid aAo=
D ./d
F ./d/a1 YQo=
F ./d/a[1] Ywo=
F ./d/b1 Ygo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 4
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
printf '%s\n' *
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub
x
y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 5
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
cat sub/a[1]
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
a
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 6
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
cat *.txt
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 7
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo [xy]*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 8
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo sub/{a,b}*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1 sub/a[1] sub/b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 9
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
touch *.txt; echo rc=$?
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
rc=0
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 10
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo .*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
.hid
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 11
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
ls *
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
x
y.txt

sub:
a1
a[1]
b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 12
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
d=sub; echo "$d"/*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1 sub/a[1] sub/b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 13
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
set -- *; echo "$#: $1 $3"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
3: sub y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 14
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
shopt -s nullglob; echo *.zz; echo n
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout

n
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 15
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
arr=(*); echo "${#arr[@]} ${arr[*]}"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
3 sub x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 16
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *"y"*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 17
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
f() { echo "$#"; }; f *
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
3
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Rm_StarRemovesOnlyNonHiddenFiles
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
rm -f *; echo rc=$?
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
rc=1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
## END
## status: 0

#### Case row 18
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo \*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 19
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
shopt -s nullglob; for f in *.zz; do echo "<$f>"; done; echo done
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
done
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 20
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *.txt
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 21
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo $(echo *) end
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub x y.txt end
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 22
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
shopt -s dotglob; echo *
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
.hid sub x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 23
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
for f in *.zz; do echo "<$f>"; done
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
<*.zz>
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 24
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
grep -h y *.txt x; echo rc=$?
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y
rc=0
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 25
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
d=sub; echo $d/*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1 sub/a[1] sub/b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 26
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo '*.txt'
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 27
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo sub/a[1]
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 28
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
shopt -s dotglob; echo .*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
.hid
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 29
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *.{txt,zz} x
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y.txt *.zz x
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 30
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *"y"zz*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*yzz*
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 31
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo sub/a\[*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a[1]
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 32
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo "*"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 33
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
v='x y.t*'; echo $v
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 34
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo *.zz
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*.zz
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 35
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
wc -c *.txt x
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
2 y.txt
2 x
4 total
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 36
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo sub/*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1 sub/a[1] sub/b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 37
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo ?.txt
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 38
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
for f in *; do echo "[$f]"; done
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
[sub]
[x]
[y.txt]
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 39
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
arr=(a *.txt z); echo "${#arr[@]}: ${arr[*]}"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
3: a y.txt z
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 40
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
[ "$(echo "$PWD"/*.txt)" = "$PWD/y.txt" ] && echo same
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
same
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 41
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo */*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/a1 sub/a[1] sub/b1
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 42
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
v='*'; echo "$v"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
*
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 43
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
cat sub/a*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
a
c
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Cp_StarIntoDirectory_MatchesAndNothingElse
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
mkdir -p d
} >/dev/null 2>&1
echo '== stdout'
{ :
cp *.txt x d; echo "rc=$?"
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
rc=0
== exit 0
== fs
D .
F ./.hid aAo=
D ./d
F ./d/x eAo=
F ./d/y.txt eQo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 44
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
v='*'; echo $v
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub x y.txt
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 45
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo nosuch/*
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
nosuch/*
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 46
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
echo */
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
sub/
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0

#### Case row 47
__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do if [ -d "$p" ]; then echo "D $p"; else echo "F $p $(base64 -w0 "$p")"; fi; done; }
d=$(mktemp -d) && cd "$d" && { :
printf '%s\n' 'x' > 'x'
printf '%s\n' 'y' > 'y.txt'
printf '%s\n' 'h' > '.hid'
mkdir -p 'sub'
printf '%s\n' 'a' > 'sub/a1'
mkdir -p 'sub'
printf '%s\n' 'b' > 'sub/b1'
mkdir -p 'sub'
printf '%s\n' 'c' > 'sub/a[1]'
} >/dev/null 2>&1
echo '== stdout'
{ :
for f in a *.txt x; do echo "<$f>"; done
} 2>/dev/null
echo "== exit $?"
echo '== fs'
__fs_snap
cd /; rm -rf "$d"
## STDOUT:
== stdout
<a>
<y.txt>
<x>
== exit 0
== fs
D .
F ./.hid aAo=
D ./sub
F ./sub/a1 YQo=
F ./sub/a[1] Ywo=
F ./sub/b1 Ygo=
F ./x eAo=
F ./y.txt eQo=
## END
## status: 0
