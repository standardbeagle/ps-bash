## Absorbed from Differential.Tests/RedirectionDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Differential_Redirect_StdoutToFile_EchoCatRoundtrip
tmpf="/tmp/psbash_redir_$$"; echo hello > "$tmpf"; cat "$tmpf"; rm -f "$tmpf"
## stdout: hello
## status: 0

#### Differential_Redirect_AppendToFile_TwoLineOutput
tmpf="/tmp/psbash_append_$$"; echo a > "$tmpf"; echo b >> "$tmpf"; cat "$tmpf"; rm -f "$tmpf"
## STDOUT:
a
b
## END
## status: 0

#### Differential_Redirect_DevNull_DiscardsOutput
echo suppressed > /dev/null; echo done
## stdout: done
## status: 0

#### Differential_Redirect_Stderr_DevNull_SuppressesError
__psbash_no_such_cmd_xyz__ 2>/dev/null; echo $?
## stdout: 127
## status: 0

#### Differential_Redirect_HereString_CatHello
cat <<< "hello"
## stdout: hello
## status: 0

#### Differential_Redirect_Heredoc_Literal_NoVarExpansion
cat <<'EOF'
$USER
EOF
## stdout: $USER
## status: 0

#### Differential_Redirect_StderrToStdout_PipedToCat
echo err >&2 2>&1 | cat
## stdout:
## status: 0

#### Differential_Redirect_VariableTarget_WritesCorrectFile
logfile="/tmp/psbash_var_$$"; echo content > "$logfile"; cat "$logfile"; rm -f "$logfile"
## stdout: content
## status: 0

#### Differential_Redirect_StdinFromFile_CatReadsIt
tmpf="/tmp/psbash_stdin_$$"; echo world > "$tmpf"; cat < "$tmpf"; rm -f "$tmpf"
## stdout: world
## status: 0

#### Differential_Redirect_IoNumber_Adjacent_IsStderrRedirect
__psbash_no_such_xyz__ 2>/dev/null; echo after
## stdout: after
## status: 0

#### Differential_Redirect_BraceGroup_StdoutToFile
tmpf="/tmp/psbash_brace_$$"; { echo line1; echo line2; } > "$tmpf"; cat "$tmpf"; rm -f "$tmpf"
## STDOUT:
line1
line2
## END
## status: 0

#### Differential_Redirect_Heredoc_StripTabs
cat <<-EOF
	hello
	world
EOF
## STDOUT:
hello
world
## END
## status: 0

#### Differential_Redirect_HeredocFollowedByCommand
cat <<EOF
body line
EOF
echo SECOND
## STDOUT:
body line
SECOND
## END
## status: 0

#### Differential_Redirect_HeredocBodyUnbalancedQuote_ThenCommand
cat <<'EOF'
val="oops
more )( ` stuff
EOF
echo DONE
## STDOUT:
val="oops
more )( ` stuff
DONE
## END
## status: 0

#### Differential_Redirect_StackedHeredocs_ThenCommand
cat <<A <<B
alpha
A
beta
B
echo END
## STDOUT:
beta
END
## END
## status: 0

#### Differential_Redirect_Heredoc_Expanding_VarSubstitution
USER=someone; cat <<EOF
hello $USER
EOF
## stdout: hello someone
## status: 0

#### Differential_Redirect_StderrToFile_RawBytesNoCmdletPrefix
ls /nonexistent_psb 2> e; echo rc=$?; ls /nonexistent_psb2 2>> e; cat -A e
## STDOUT:
rc=2
ls: cannot access '/nonexistent_psb': No such file or directory$
ls: cannot access '/nonexistent_psb2': No such file or directory$
## END
## status: 0

#### Differential_Redirect_BothStreamsToFile_InOrder
{ echo out; ls /nonexistent_psb; echo out2; } &> e; cat -A e; ls /nonexistent_psb >&e2; cat -A e2
## STDOUT:
out$
ls: cannot access '/nonexistent_psb': No such file or directory$
out2$
ls: cannot access '/nonexistent_psb': No such file or directory$
## END
## status: 0

#### Differential_Redirect_SameFdTwice_DevNull_Parses
echo x >/dev/null >/dev/null; echo rc=$?; ls /nonexistent_psb 2>/dev/null 2>/dev/null; echo rc=$?; echo after
## STDOUT:
rc=0
rc=2
after
## END
## status: 0

#### Differential_Redirect_SameFdTwice_Files_LastWins
echo x >a >b; echo y >p >q >r; echo old > s; echo new >>s >u; d=.; echo z >$d/m >"$d/n"; ls /nonexistent_psb 2>f1 2>f2; for f in a b p q r s u m n; do printf '%s=[%s]\n' $f "$(cat $f)"; done; wc -c < f1; cat f2
## STDOUT:
a=[]
b=[x]
p=[]
q=[]
r=[y]
s=[old]
u=[new]
m=[]
n=[z]
0
ls: cannot access '/nonexistent_psb': No such file or directory
## END
## status: 0

#### Differential_Redirect_DupBeforeFile_StderrToPipe
ls /nonexistent_psb 2>&1 >o | tr a-z A-Z; echo "o=[$(cat o)]"
## STDOUT:
LS: CANNOT ACCESS '/NONEXISTENT_PSB': NO SUCH FILE OR DIRECTORY
o=[]
## END
## status: 0

#### Differential_Redirect_PipeStage_StderrToFile_StdoutFlows
{ echo a; ls /nonexistent_psb; } 2>e | tr a-z A-Z; cat -A e
## STDOUT:
A
ls: cannot access '/nonexistent_psb': No such file or directory$
## END
## status: 0

#### Differential_Redirect_ExpandedDevNullTarget_Discards
n=/dev/null; echo hi > $n; echo rc=$?; ls /nonexistent_psb 2> $n; echo rc=$?; ls
## STDOUT:
rc=0
rc=2
## END
## status: 0

#### InTempDir
t=$(mktemp -d); cd "$t" || exit 1; ls /nonexistent_psb 2>&1 >o | tr a-z A-Z; echo "o=[$(cat o)]"; cd /; rm -rf "$t"
## STDOUT:
LS: CANNOT ACCESS '/NONEXISTENT_PSB': NO SUCH FILE OR DIRECTORY
o=[]
## END
## status: 0

#### InTempDir row 2
t=$(mktemp -d); cd "$t" || exit 1; n=/dev/null; echo hi > $n; echo rc=$?; ls /nonexistent_psb 2> $n; echo rc=$?; ls; cd /; rm -rf "$t"
## STDOUT:
rc=0
rc=2
## END
## status: 0

#### InTempDir row 3
t=$(mktemp -d); cd "$t" || exit 1; ls /nonexistent_psb 2> e; echo rc=$?; ls /nonexistent_psb2 2>> e; cat -A e; cd /; rm -rf "$t"
## STDOUT:
rc=2
ls: cannot access '/nonexistent_psb': No such file or directory$
ls: cannot access '/nonexistent_psb2': No such file or directory$
## END
## status: 0

#### InTempDir row 4
t=$(mktemp -d); cd "$t" || exit 1; echo x >a >b; echo y >p >q >r; echo old > s; echo new >>s >u; d=.; echo z >$d/m >"$d/n"; ls /nonexistent_psb 2>f1 2>f2; for f in a b p q r s u m n; do printf '%s=[%s]\n' $f "$(cat $f)"; done; wc -c < f1; cat f2; cd /; rm -rf "$t"
## STDOUT:
a=[]
b=[x]
p=[]
q=[]
r=[y]
s=[old]
u=[new]
m=[]
n=[z]
0
ls: cannot access '/nonexistent_psb': No such file or directory
## END
## status: 0

#### InTempDir row 5
t=$(mktemp -d); cd "$t" || exit 1; echo x >/dev/null >/dev/null; echo rc=$?; ls /nonexistent_psb 2>/dev/null 2>/dev/null; echo rc=$?; echo after; cd /; rm -rf "$t"
## STDOUT:
rc=0
rc=2
after
## END
## status: 0

#### InTempDir row 6
t=$(mktemp -d); cd "$t" || exit 1; { echo a; ls /nonexistent_psb; } 2>e | tr a-z A-Z; cat -A e; cd /; rm -rf "$t"
## STDOUT:
A
ls: cannot access '/nonexistent_psb': No such file or directory$
## END
## status: 0

#### InTempDir row 7
t=$(mktemp -d); cd "$t" || exit 1; { echo out; ls /nonexistent_psb; echo out2; } &> e; cat -A e; ls /nonexistent_psb >&e2; cat -A e2; cd /; rm -rf "$t"
## STDOUT:
out$
ls: cannot access '/nonexistent_psb': No such file or directory$
out2$
ls: cannot access '/nonexistent_psb': No such file or directory$
## END
## status: 0
