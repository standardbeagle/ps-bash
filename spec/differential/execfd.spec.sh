## Absorbed from Differential.Tests/ExecFdTests.cs (scripts only; expectations recorded from bash 5.2).

#### RunAsync
W="$(mktemp -d)"; cd "$W"; exec 8> out.txt; echo via8 >&8; echo more >&8; exec 8>&-; cat out.txt; exec 9>> out.txt; echo appended >&9; exec 9>&-; cat out.txt; cd /; rm -rf "$W"
## STDOUT:
via8
more
via8
more
appended
## END
## status: 0

#### RunAsync row 2
W="$(mktemp -d)"; cd "$W"; printf 'x\n' > d; exec 6< d; read -u 6 l; read -u 6 l2; echo "rc=$? l=$l l2=[$l2]"; exec 6<&-; cd /; rm -rf "$W"
## stdout: rc=1 l=x l2=[]
## status: 0

#### RunAsync row 3
exec 4>&2; echo to-err >&4; echo out; exec 4>&-
## stdout: out
## status: 0

#### RunAsync row 4
W="$(mktemp -d)"; cd "$W"; printf 'one\ntwo\nthree\n' > data.txt; exec 7< data.txt; read -u 7 a; read -u 7 b; echo "a=$a b=$b"; cat <&7; exec 7<&-; cd /; rm -rf "$W"
## STDOUT:
a=one b=two
three
## END
## status: 0

#### RunAsync row 5
exec false; echo notreached
## stdout:
## status: 1

#### RunAsync row 6
W="$(mktemp -d)"; cd "$W"; printf 'x\n' > d; exec 6< d; exec 6<&-; read -u 6 l 2>/dev/null; echo "rc=$?"; cd /; rm -rf "$W"
## stdout: rc=1
## status: 0

#### RunAsync row 7
exec 3>&1; echo to-three >&3; echo plain; exec 3>&-
## STDOUT:
to-three
plain
## END
## status: 0

#### RunAsync row 8
exec 3< /definitely/not/here; echo "rc=$?"
## stdout: rc=1
## status: 0

#### RunAsync row 9
W="$(mktemp -d)"; cd "$W"; printf 'FROM-FILE\n' > d; echo stdin-text | cat 7< d; cd /; rm -rf "$W"
## stdout: stdin-text
## status: 0

#### RunAsync row 10
exec printf 'bye\n'; echo notreached
## stdout: bye
## status: 0

#### RunAsync row 11
echo lost >&5; echo "rc=$?"
## stdout: rc=1
## status: 0
