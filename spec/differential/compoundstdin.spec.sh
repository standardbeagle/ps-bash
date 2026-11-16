## Absorbed from Differential.Tests/CompoundStdinDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 1
printf 'b\na\n' | { sort; }
## STDOUT:
a
b
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 2
printf 'x\n' | ( cat )
## stdout: x
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 3
printf 'b\na\nc\n' | { sort -r; }
## STDOUT:
c
b
a
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 4
printf '1\n2\n3\n' | { head -n1; cat; }
## stdout: 1
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 5
printf '1\n2\n3\n' | { read x; sort -r; echo got $x; }
## STDOUT:
3
2
got 1
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 6
printf '1\n2\n3\n' | { read x; read y; echo "$x$y"; cat; }
## STDOUT:
12
3
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 7
printf 'a\nb\n' | { cat; cat; echo end; }
## STDOUT:
a
b
end
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 8
printf 'a\nb\n' | while read l; do echo "<$l>"; done
## STDOUT:
<a>
<b>
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 9
printf 'a\nb\n' | { echo head; cat; echo tail; }
## STDOUT:
head
a
b
tail
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 10
printf 'b\na\nb\n' | { grep -v a | sort; }
## STDOUT:
b
b
## END
## status: 0

#### BraceGroupAndSubshellAsPipeStage_ShareStdin row 11
printf 'b\na\nb\n' | { sort | uniq -c; }
## STDOUT:
      1 a
      2 b
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 1
printf '1\n2\n3\n' | if true; then sort -r; fi
## STDOUT:
3
2
1
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 2
printf '1\n2\n3\n' | if true; then cat; else echo no; fi
## STDOUT:
1
2
3
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 3
printf '1\n2\n3\n' | for i in 1; do cat; done
## STDOUT:
1
2
3
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 4
printf '1\n2\n3\n' | for i in 1 2; do cat; echo "[$i]"; done
## STDOUT:
1
2
3
[1]
[2]
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 5
printf '1\n2\n3\n' | case x in x) wc -l;; esac
## stdout: 3
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 6
printf 'b\na\n' | case x in y) echo no;; *) sort;; esac
## STDOUT:
a
b
## END
## status: 0

#### OtherCompoundsAsPipeStage_ShareStdin row 7
printf 'a\nb\n' | while true; do cat; break; done
## STDOUT:
a
b
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 1
printf '3\n1\n2\n' > /tmp/psb_cs_a.txt; { sort; } < /tmp/psb_cs_a.txt; rm -f /tmp/psb_cs_a.txt
## STDOUT:
1
2
3
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 2
printf '3\n1\n2\n' > /tmp/psb_cs_b.txt; ( sort ) < /tmp/psb_cs_b.txt; rm -f /tmp/psb_cs_b.txt
## STDOUT:
1
2
3
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 3
printf '3\n1\n2\n' > /tmp/psb_cs_c.txt; { read x; sort; echo $x; } < /tmp/psb_cs_c.txt; rm -f /tmp/psb_cs_c.txt
## STDOUT:
1
2
3
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 4
printf '3\n1\n2\n' > /tmp/psb_cs_d.txt; if true; then sort; fi < /tmp/psb_cs_d.txt; rm -f /tmp/psb_cs_d.txt
## STDOUT:
1
2
3
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 5
printf '3\n1\n2\n' > /tmp/psb_cs_e.txt; for i in 1; do cat; done < /tmp/psb_cs_e.txt; rm -f /tmp/psb_cs_e.txt
## STDOUT:
3
1
2
## END
## status: 0

#### RedirectedInputFile_FeedsEveryCommandInside row 6
printf 'a\nb\n' > /tmp/psb_cs_f.txt; { while read l; do echo "<$l>"; done; } < /tmp/psb_cs_f.txt; rm -f /tmp/psb_cs_f.txt
## STDOUT:
<a>
<b>
## END
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 1
{ cat; } <<< hi
## stdout: hi
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 2
{ cat; } <<< 'a b'
## stdout: a b
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 3
( cat ) <<< hi
## stdout: hi
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 4
{ read x; echo "[$x]"; } <<< hi
## stdout: [hi]
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 5
for i in 1; do cat; done <<< hi
## stdout: hi
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 6
{ sort; } <<EOF
b
a
EOF
## STDOUT:
a
b
## END
## status: 0

#### HereStringAndHeredocIntoCompound_FeedsEveryCommandInside row 7
{ cat; echo mid; cat; } <<< hi
## STDOUT:
hi
mid
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 1
printf 'a\nb\n' | { echo one; echo two; }
## STDOUT:
one
two
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 2
printf 'a\nb\n' | { echo one; cat; }
## STDOUT:
one
a
b
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 3
printf 'a\nb\n' | { cat /dev/null; cat; }
## STDOUT:
a
b
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 4
printf 'a\nb\n' | { true; sort -r; }
## STDOUT:
b
a
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 5
printf 'a\nb\n' | { printf 'x\n'; cat; }
## STDOUT:
x
a
b
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 6
printf 'a\nb\n' | { { cat; } | sort -r; }
## STDOUT:
b
a
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 7
printf 'a\nb\n' | { cat | { sort -r; }; }
## STDOUT:
b
a
## END
## status: 0

#### NonReadingCommandsAndNestedCompounds_DoNotStealStdin row 8
printf 'q\n' | { { read z; echo "z=$z"; } | cat; }
## stdout: z=q
## status: 0
