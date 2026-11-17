## Absorbed from Differential.Tests/GlobBraceExpansionDifferentialTests.cs (scripts only; expectations recorded from bash 5.2).

#### Differential_BraceTuple_ExpandsToWords
echo {a,b,c}
## stdout: a b c
## status: 0

#### Differential_BraceRange_IntegerSequence
echo {1..5}
## stdout: 1 2 3 4 5
## status: 0

#### Differential_BraceRange_ZeroPadded
echo {01..03}
## stdout: 01 02 03
## status: 0

#### Differential_BraceRange_WithStep
echo {0..10..3}
## stdout: 0 3 6 9
## status: 0

#### Differential_BraceTuple_WithPrefixAndSuffix
echo file{1,2,3}.txt
## stdout: file1.txt file2.txt file3.txt
## status: 0

#### Differential_BraceTuple_InsideDoubleQuotes_NoExpansion
echo "{a,b}"
## stdout: {a,b}
## status: 0

#### Differential_BraceTuple_TrailingComma_EmptyItem
echo {a,}
## stdout: a
## status: 0

#### Differential_BraceRange_LetterRange_KnownGap
echo {a..e}
## stdout: a b c d e
## status: 0

#### Differential_BraceTuple_NestedBraces_KnownGap
echo {a,b{1,2},c}
## stdout: a b1 b2 c
## status: 0

#### Differential_Glob_NoMatchPassthrough
echo /nonexistent_psbash_test_path/*.txt
## stdout: /nonexistent_psbash_test_path/*.txt
## status: 0

#### Differential_Glob_NegatedCharClass_NoMatch_Passthrough
echo /nonexistent_psbash_test_path/[!abc]*
## stdout: /nonexistent_psbash_test_path/[!abc]*
## status: 0

#### Differential_BraceRange_Descending
echo {5..1}
## stdout: 5 4 3 2 1
## status: 0
