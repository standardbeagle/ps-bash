using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash's variable contexts (the oils <c>[bash_unset]</c> cases): what <c>unset</c> and a value-less <c>local</c> do
/// depends on which function made the name local and which temp binding (<c>v=t f</c>, <c>v=t eval</c>) is in force.
/// <list type="bullet">
/// <item>Unsetting a local of the CALLER (<c>unlocal() { unset -v "$1"; }</c>) revealed nothing: v stayed unset.
/// Unsetting a temp binding showed nothing either. bash reveals the value the frame hid.</item>
/// <item><c>local v</c> under <c>v=tempenv f</c> left v unset (bash: keeps tempenv).</item>
/// <item>A second <c>local v</c> in the same call unset the first one's value.</item>
/// <item><c>v=t0 eval 'v=t1 eval ...'</c> left v=t0 afterwards: both evals saved into one <c>$__saved_v</c>.</item>
/// <item><c>v=t eval 'echo $v'</c> printed the old v: the inlined body read the pre-assignment copy meant for eval's
/// own words.</item>
/// <item>A local or binding whose frame <c>unset</c> took was still put back when its function or binding ended,
/// overwriting a later write (<c>unlocal v; v=new</c> left the caller's old value). A subshell's <c>unset</c> pops a
/// frame for the subshell only.</item>
/// </list>
/// </summary>
public class VariableContextDifferentialTests
{
    [Fact]
    public Task UnsetAndLocal_FollowBashsFrames() => AssertOracle.EqualAsync(
        "unlocal() { unset -v \"$1\"; }\nshow() { echo \"$1: [${v-(unset)}]\"; }\nv=global\n"
        + "f1() { local v=local; unset v; show local-unset; }; f1\n"
        + "f2() { local v=local; unlocal v; show dyn-unset; }; f2\n"
        + "f3() { local v=local; unset v; show tempenv-local-unset; }; v=tempenv f3\n"
        + "f4() { unset v; show tempenv-unset; }; v=tempenv f4\n"
        + "f5() { unlocal v; show tempenv-unlocal; }; v=tempenv f5\n"
        + "f6() { local v; show local-novalue; }; f6; v=tempenv f6\n"
        + "f7() { local v; unlocal v; show local-unlocal; }; v=tempenv f7; v=tempenv eval f7\n"
        + "f8() { local v=a; local v; show relocal; }; f8\nshow after\n"
        + "v=t0 eval 'v=t1 eval \"unset v; show peel1; unset v; show peel2\"'\nshow end\n"
        + "v=t eval 'echo \"eval-body: $v\"'\nv=t eval \"echo eval-arg: $v\"\n"
        + "g() { local v=l; v=t eval 'echo \"fn-eval-body: $v\"'; echo \"fn-after: $v\"; }; g\n"
        // A frame `unset` took is not put back when its function or binding ends: writes since went to the outer v.
        + "v=g; p1() { local v=l; unlocal v; v=new; }; p1; echo \"local-popped: $v\"\n"
        + "v=g; p2() { unset v; v=new; }; v=t p2; echo \"temp-fn-popped: $v\"\n"
        + "v=g; v=t eval 'unset v; v=new'; echo \"temp-eval-popped: $v\"\n"
        + "v=g; p3() { local v=l; v=new; }; p3; echo \"local-kept: $v\"\n"
        + "v=g; v=t eval 'v=new'; echo \"temp-eval-kept: $v\"\n"
        // A subshell's unset pops the frame for the subshell only.
        + "v=g; s1() { local v=l; ( unlocal v; echo \"in-subshell: $v\" ); echo \"s1: $v\"; }; s1; echo \"subshell-pop: $v\"");
}
