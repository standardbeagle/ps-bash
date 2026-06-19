using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Curated weak-spot corpus: one row per construct that this parseability layer must keep
/// emitting valid PowerShell for. Organized by the construct categories in
/// docs/specs/transpile-fuzz-grammar.md §3. Every row either guards a real fixed bug
/// (named in a comment) or pins a construct the grammar fuzzer also covers — the corpus is
/// the human-readable, deterministic floor under the randomized fuzzers.
///
/// Contract: each input must transpile to PowerShell that parses with zero errors
/// (ParseabilityContract branch A). A construct that does NOT yet satisfy that is a KnownGap_*
/// Skip fact at the bottom, not a silent omission.
/// </summary>
public class TranspileParseabilityCorpusTests
{
    [Theory]
    // ── 3.2 parameter expansion ───────────────────────────────────────
    [InlineData("echo ${x:-default}")]
    [InlineData("echo ${x:=default}")]
    [InlineData("echo ${x:+alt}")]
    [InlineData("echo ${x:?msg}")]
    [InlineData("echo ${x-default}")]
    [InlineData("echo ${x=default}")]
    [InlineData("echo ${x+alt}")]
    [InlineData("echo ${x?msg}")]
    [InlineData("echo ${#x}")]
    [InlineData("echo ${x#pre}")]
    [InlineData("echo ${x##pre}")]
    [InlineData("echo ${x%suf}")]
    [InlineData("echo ${x%%suf}")]
    [InlineData("echo ${x/find/rep}")]
    [InlineData("echo ${x//find/rep}")]
    [InlineData("echo ${x/#pre/rep}")]
    [InlineData("echo ${x/%suf/rep}")]
    [InlineData("echo ${x:2}")]
    [InlineData("echo ${x:2:3}")]
    [InlineData("echo ${x: -2}")]
    [InlineData("echo ${x^^}")]
    [InlineData("echo ${x,,}")]
    [InlineData("echo ${x^}")]
    [InlineData("echo ${x,}")]
    [InlineData("echo ${x@Q}")]
    [InlineData("echo ${x@U}")]
    [InlineData("echo ${x@L}")]
    [InlineData("echo ${!x}")]
    [InlineData("echo ${x:-$(date)}")]
    [InlineData("echo ${x:-`date`}")]
    // nested default word — fixed: inner ($env:y ?? "z") carried raw " into the outer "…".
    [InlineData("echo ${x:-${y:-z}}")]
    [InlineData("echo ${a:-${b:+${c:-d}}}")]
    [InlineData("echo \"${a:-${b}}\"")]
    [InlineData("echo ${x:-a b c}")]
    [InlineData("echo ${x:-\"quoted spaces\"}")]
    // ── 3.3 arrays ─────────────────────────────────────────────────────
    [InlineData("arr=(a b c); echo ${arr[0]}")]
    [InlineData("arr=(a b c); echo ${arr[@]}")]
    [InlineData("arr=(a b c); echo ${arr[*]}")]
    [InlineData("arr=(a b c); echo ${#arr[@]}")]
    [InlineData("arr=(a b c); echo ${!arr[@]}")]
    [InlineData("declare -A m; m[k]=v; echo ${m[k]}")]
    [InlineData("arr+=(d e)")]
    [InlineData("echo ${arr[@]:1:2}")]
    [InlineData("for x in \"${arr[@]}\"; do echo $x; done")]
    // ── 3.4 command substitution + compound bodies ─────────────────────
    [InlineData("echo $(date)")]
    [InlineData("echo `date`")]
    [InlineData("echo $(echo $(echo nested))")]
    // compound bodies — fixed: switch/foreach/if statement can't head a pipe ("empty pipe element").
    [InlineData("echo $(case $x in a) echo A;; esac)")]
    [InlineData("echo $(for i in 1 2; do echo $i; done)")]
    [InlineData("echo $(if true; then echo y; fi)")]
    [InlineData("echo $(while false; do echo x; done)")]
    [InlineData("echo $(cat <<EOF\nbody\nEOF\n)")]
    [InlineData("y=$(grep foo bar | head -1)")]
    // ── 3.5 arithmetic ─────────────────────────────────────────────────
    [InlineData("echo $((1+2))")]
    [InlineData("echo $((2**10))")]
    [InlineData("echo $((a>b?a:b))")]
    [InlineData("(( i++ ))")]
    [InlineData("(( x = 5 ))")]
    [InlineData("for ((i=0;i<3;i++)); do echo $i; done")]
    [InlineData("echo $(( ${#arr[@]} + 1 ))")]
    // ── 3.6 test expressions ───────────────────────────────────────────
    [InlineData("[ -f /etc/passwd ]")]
    // quoted operand to a unary file test — fixed: emitted ""$env:file"" (double-wrapped).
    [InlineData("[ -f \"$file\" ]")]
    [InlineData("[ -s \"$dir/x\" ]")]
    [InlineData("[ -d \"$HOME\" ]")]
    // for-in list mixing $'…' + brace expansion — fixed: FormatForItem re-quoted PS values.
    [InlineData("for f in $'a\\tb' {x.txt,y.txt}; do echo $f; done")]
    [InlineData("[ -z \"$x\" ]")]
    [InlineData("[ \"$a\" = \"$b\" ]")]
    [InlineData("[ $n -eq 0 ]")]
    [InlineData("[[ -d /tmp ]]")]
    [InlineData("[[ $x == pat* ]]")]
    [InlineData("[[ $x =~ ^[0-9]+$ ]]")]
    [InlineData("[[ $a && $b ]]")]
    [InlineData("[[ ! -e foo ]]")]
    // POSIX combinators — fixed: multi-clause -a/-o fell through to bare-operand fallback.
    [InlineData("[ a = b -a c = d ]")]
    [InlineData("[ -f x -o -d y ]")]
    [InlineData("[ -f x -a -r x -a -w x ]")]
    // ── 3.7 brace expansion ────────────────────────────────────────────
    [InlineData("echo {a,b,c}")]
    [InlineData("echo {1..5}")]
    [InlineData("echo {01..10}")]
    [InlineData("echo {1..10..2}")]
    [InlineData("echo {a,b}{1,2}")]
    [InlineData("echo pre{a,b}post")]
    [InlineData("echo {a,{b,c},d}")]
    // ── 3.1 quoting / word seams ───────────────────────────────────────
    [InlineData("echo $'ansi\\nC'")]
    [InlineData("echo \"a\"'b'\"c\"")]
    [InlineData("echo \"$x\"y")]
    [InlineData("echo \"price: \\$5\"")]
    [InlineData("echo '#notcomment'")]
    [InlineData("echo foo#bar")]
    [InlineData("x='a b'; echo $x")]
    [InlineData("x='a b'; echo \"$x\"")]
    // ── 3.9 heredocs ───────────────────────────────────────────────────
    [InlineData("cat <<EOF\nhello $USER\nEOF")]
    [InlineData("cat <<'EOF'\nliteral $x\nEOF")]
    [InlineData("cat <<-EOF\n\tindented\n\tEOF")]
    [InlineData("cat <<<\"here string\"")]
    [InlineData("cat <<EOF\nline with \"quotes\" and 'ticks' and $(cmd)\nEOF")]
    [InlineData("cat <<EOF && echo after\nbody\nEOF")]
    // ── 3.8 redirections ───────────────────────────────────────────────
    [InlineData("cmd 2>&1")]
    [InlineData("cmd &> /tmp/f")]
    [InlineData("cmd >| /tmp/f")]
    [InlineData("cmd 1>&2")]
    [InlineData("diff <(sort a) <(sort b)")]
    [InlineData("cmd > /dev/null 2>&1")]
    // ── 3.10 control flow / lists ──────────────────────────────────────
    [InlineData("if true; then echo y; elif false; then echo m; else echo n; fi")]
    [InlineData("while read l; do echo $l; done")]
    [InlineData("until [ $i -gt 3 ]; do (( i++ )); done")]
    [InlineData("case $x in a|b) echo ab;; *) echo other;; esac")]
    [InlineData("f() { echo $1; }")]
    [InlineData("function g { echo hi; }")]
    [InlineData("( cd /tmp && ls )")]
    [InlineData("{ echo a; echo b; }")]
    [InlineData("a | b | c")]
    [InlineData("a |& b")]
    [InlineData("! grep foo file")]
    [InlineData("a && b || c")]
    [InlineData("true && { echo y; }")]
    // ── 3.11 special vars / env prefix / declare ───────────────────────
    [InlineData("echo $? $@ $# $1 $$ $! $- $_ $0 ${10}")]
    [InlineData("FOO=bar BAZ=qux cmd")]
    [InlineData("export PATH=/x:$PATH")]
    [InlineData("declare -i n=5")]   // fixed: emitted broken [int]$global:n=5 = 0
    [InlineData("declare -i count")]
    [InlineData("declare x=hello")]
    [InlineData("read -ra arr")]
    // ── Bash-tool wrapper fragments ────────────────────────────────────
    [InlineData("shopt -u extglob 2>/dev/null || true")]
    [InlineData("eval 'echo hi' < /dev/null")]
    [InlineData("pwd -P >| /tmp/cwd")]
    public void Corpus_EmitsValidPowerShell(string bashInput)
    {
        Assert.Equal(
            ParseabilityContract.Outcome.ValidPowerShell,
            ParseabilityContract.Assert(bashInput));
    }
}
