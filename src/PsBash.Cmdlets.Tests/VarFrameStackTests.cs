using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The oils <c>[bash_unset]</c> cases (ble-features / ble-unset, expectations from bash 5.2) replayed against the frame
/// stack: <c>v</c> is the shell variable, <c>unlocal() { unset -v "$1"; }</c> runs one function deeper.
/// </summary>
public class VarFrameStackTests
{
    private sealed class Shell
    {
        public readonly VarFrameStack S = new();
        public string? V = "global";

        public void Local(string? value = null)
        {
            var kept = S.Local("v", V);
            V = value ?? kept;
        }

        public void Unset() { if (S.Unset("v", out var r)) V = r; else V = null; }

        public void Unlocal() { S.Enter(); try { Unset(); } finally { S.Exit(); } }

        /// <summary><c>v=value f</c> / <c>v=value eval ...</c>: bind, run, end (restoring what the binding hid).</summary>
        public void Temp(string value, bool functionCall, Action body)
        {
            var saved = V;
            var id = S.PushTemp("v", saved, functionCall);
            V = value;
            try { if (functionCall) Call(body); else body(); }
            finally { V = saved; S.PopTemp("v", id); }
        }

        /// <summary>A function call (restoring its locals' values is the emitted code's job, not modelled here).</summary>
        public void Call(Action body)
        {
            S.Enter();
            try { body(); }
            finally { S.Exit(); }
        }
    }

    [Fact]
    public void LocalUnset_StaysLocalAndUnset_DynamicUnset_RevealsTheGlobal()
    {
        var sh = new Shell();
        sh.Call(() => { sh.Local("local"); sh.Unset(); Assert.Null(sh.V); });
        sh.V = "global";
        sh.Call(() => { sh.Local("local"); sh.Unlocal(); Assert.Equal("global", sh.V); });
    }

    [Fact]
    public void LocalOverAFunctionTempBinding_TakesItsPlace()
    {
        var sh = new Shell();
        sh.Temp("tempenv", functionCall: true, () => { sh.Local("local"); sh.Unset(); Assert.Null(sh.V); });
        Assert.Equal("global", sh.V);
        sh.Temp("tempenv", functionCall: true, () => { sh.Local("local"); sh.Unlocal(); Assert.Equal("global", sh.V); });
    }

    [Fact]
    public void UnsetOfATempBinding_RevealsWhatItHid()
    {
        var sh = new Shell();
        sh.Temp("tempenv", functionCall: true, () => { sh.Unset(); Assert.Equal("global", sh.V); });
        sh.Temp("tempenv", functionCall: true, () => { sh.Unlocal(); Assert.Equal("global", sh.V); });
    }

    [Fact]
    public void ValuelessLocal_InheritsOnlyFromATempBinding()
    {
        var sh = new Shell();
        sh.Call(() => { sh.Local(); Assert.Null(sh.V); });
        sh.V = "global";
        sh.Temp("tempenv", functionCall: true, () => { sh.Local(); Assert.Equal("tempenv", sh.V); });
        // v=tempenv f2, f2 calls f1, f1: local v -> tempenv (the binding is further out).
        sh.Temp("tempenv", functionCall: true, () => sh.Call(() => { sh.Local(); Assert.Equal("tempenv", sh.V); }));
        // f3: local v=local; f1: local v -> unset (a local further out is not inherited).
        sh.Call(() => { sh.Local("local"); sh.Call(() => { sh.Local(); Assert.Null(sh.V); }); });
    }

    [Fact]
    public void EvalTempBinding_IsANestedContext()
    {
        var sh = new Shell();
        // v=tempenv f5 vs v=tempenv eval 'f5': local+unlocal reveals global vs tempenv.
        sh.Temp("tempenv", functionCall: true, () => { sh.Local(); sh.Unlocal(); Assert.Equal("global", sh.V); });
        sh.Temp("tempenv", functionCall: false, () => sh.Call(() =>
        {
            sh.Local();
            Assert.Equal("tempenv", sh.V);
            sh.Unlocal();
            Assert.Equal("tempenv", sh.V);
        }));
    }

    [Fact]
    public void NestedLocalsUnderEvalBindings_UnlocalPeelsOneAtATime()
    {
        var sh = new Shell();
        var seen = new List<string?>();
        sh.Temp("tempenv1", functionCall: true, () =>
        {
            sh.Local("local1");
            sh.Temp("tempenv2", functionCall: false, () =>
            {
                sh.Local("local2");
                sh.Temp("tempenv3", functionCall: false, () =>
                {
                    sh.Local("local3");
                    for (int i = 0; i < 4; i++) { sh.Unlocal(); seen.Add(sh.V); }
                });
            });
        });
        Assert.Equal(new string?[] { "local2", "local1", "global", null }, seen);
    }

    [Fact]
    public void NestedEvalBindings_UnsetPeelsOneAtATime()
    {
        var sh = new Shell();
        var seen = new List<string?>();
        sh.Temp("tempenv1", functionCall: true, () =>
            sh.Temp("tempenv2", functionCall: false, () =>
                sh.Temp("tempenv3", functionCall: false, () =>
                {
                    for (int i = 0; i < 4; i++) { sh.Unset(); seen.Add(sh.V); }
                })));
        Assert.Equal(new string?[] { "tempenv2", "tempenv1", "global", null }, seen);
    }

    [Fact]
    public void LocalInsideAnEvalBinding_EndsWithTheEval()
    {
        var sh = new Shell();
        sh.Temp("tempenv1", functionCall: true, () =>
        {
            sh.Local("local1");
            sh.Temp("tempenv2", functionCall: false, () =>
            {
                sh.Local("local2");
                // ( unset v ) at the eval's own depth is a local-unset: no frame goes.
                Assert.False(sh.S.Unset("v", out _));
            });
            Assert.Equal("local1", sh.V);
        });
    }

    [Fact]
    public void NestedSameScopeBindings_RestoreInnermostFirst_AndUnsetPeelsThroughThem()
    {
        // v=global; v=t0 eval 'v=t1 eval "unset v; ...; unset v; ..."'; echo $v  -> peels t0 then global; ends global.
        var s = new VarFrameStack();
        s.Bind("v", "global");
        s.Bind("v", "t0");
        Assert.True(s.Unset("v", out var r1));
        Assert.Equal("t0", r1);
        Assert.True(s.Unset("v", out var r2));
        Assert.Equal("global", r2);
        // Both frames went to unset: the bindings' ends leave the current value (the unset ones), they restore nothing.
        Assert.Equal("cur", s.Unbind("v", "cur"));
        Assert.Equal("cur", s.Unbind("v", "cur"));
        Assert.Equal("cur", s.Unbind("v", "cur"));
        Assert.False(s.Unset("v", out _));
    }

    [Fact]
    public void Bindings_NotUnset_RestoreTheirSavedValues_InnermostFirst()
    {
        var s = new VarFrameStack();
        s.Bind("v", "global");
        s.Bind("v", "t0");
        Assert.Equal("t0", s.Unbind("v", "t1"));
        Assert.Equal("global", s.Unbind("v", "t0"));
    }

    [Fact]
    public void Bindings_OfDifferentNames_UnbindIndependently()
    {
        var s = new VarFrameStack();
        s.Bind("a", "1");
        s.Bind("b", "2");
        Assert.Equal("1", s.Unbind("a", "x"));
        Assert.Equal("2", s.Unbind("b", "y"));
    }

    [Fact]
    public void FunctionTempBinding_UnsetInside_IsNotRestored_DroppedByExit_IsRestored()
    {
        // v=t f2, f2: unset v; v=new -> new.   v=t f, f returns -> the binding's hidden value comes back.
        var s = new VarFrameStack();
        var id = s.PushTemp("v", "g", functionCall: true);
        s.Enter();
        Assert.True(s.Unset("v", out var revealed));
        Assert.Equal("g", revealed);
        s.Exit();
        Assert.False(s.EndTemp("v", id));

        var id2 = s.PushTemp("v", "g", functionCall: true);
        s.Enter();
        s.Exit();
        Assert.True(s.EndTemp("v", id2));
    }

    [Fact]
    public void OwnsLocal_IsFalseOnceAHelperUnsetTheLocal()
    {
        // f() { local v=l; unlocal v; v=new; }: f's return leaves new, not the caller's value.
        var s = new VarFrameStack();
        s.Enter();
        s.Local("v", "g");
        Assert.True(s.OwnsLocal("v"));
        s.Enter();
        Assert.True(s.Unset("v", out _));
        s.Exit();
        Assert.False(s.OwnsLocal("v"));
    }

    [Fact]
    public void Clear_DropsFramesBindingsAndDepth()
    {
        var s = new VarFrameStack();
        s.Bind("v", "x");
        s.Enter();
        s.Local("w", "y");
        s.Clear();
        Assert.Equal(0, s.Depth);
        Assert.Equal("cur", s.Unbind("v", "cur"));
        Assert.False(s.Unset("w", out _));
    }

    [Fact]
    public void Restore_UndoesASubshellsUnset()
    {
        // f5() { local v; ( unlocal v ); }: the pop belongs to the subshell; f's return still finds its local.
        var s = new VarFrameStack();
        s.Enter();
        s.Local("v", "global");
        var saved = s.Save();
        s.Enter();
        Assert.True(s.Unset("v", out _));
        s.Exit();
        Assert.False(s.OwnsLocal("v"));
        s.Restore(saved);
        Assert.True(s.OwnsLocal("v"));
        Assert.Equal(1, s.Depth);
        // The restored frame is a copy: changing the live stack leaves the snapshot alone.
        s.Enter();
        Assert.True(s.Unset("v", out var revealed));
        Assert.Equal("global", revealed);
        s.Exit();
        s.Restore(saved);
        Assert.True(s.OwnsLocal("v"));
    }

    [Fact]
    public void Exit_DropsTheFramesOfTheReturningFunction()
    {
        var s = new VarFrameStack();
        s.Enter();
        s.Local("v", "outer");
        s.Exit();
        Assert.False(s.Unset("v", out _));
        Assert.Equal(0, s.Depth);
        s.Exit();
        Assert.Equal(0, s.Depth);
    }
    /// <summary>
    /// BASH_LINENO: each Enter records the line of the statement that called; Exit puts the caller back on that line,
    /// so a second call in the same statement (<c>f $(g)</c>) still reports it. A script file adds 0 for main.
    /// </summary>
    [Fact]
    public void CallLines_AreTheCallSites_InnermostFirst()
    {
        var s = new VarFrameStack();
        Assert.Equal(new[] { "0" }, s.CallLines(inScriptFile: true));
        Assert.Empty(s.CallLines(inScriptFile: false));
        s.Line = 9; s.Enter();           // h called on line 9
        s.Line = 4; s.Enter();           // g called on line 4 inside h
        Assert.Equal(new[] { "4", "9", "0" }, s.CallLines(inScriptFile: true));
        s.Line = 1; s.Exit();            // g ran its own line 1, then returned
        Assert.Equal(4, s.Line);
        Assert.Equal(new[] { "9" }, s.CallLines(inScriptFile: false));
        s.Exit();
        Assert.Equal(9, s.Line);
        Assert.Empty(s.CallLines(inScriptFile: false));
    }

    /// <summary>
    /// The line a shell diagnostic shows (ShellDiagnostic). bash 5.2 (WSL): a run-time <c>eval</c> on line 7 whose text
    /// fails on its own line 2 reports line 8; a function called from inside it reports the function's own script lines;
    /// a nested eval counts from the inner eval's line; a file sourced inside an eval has its own lines; after an eval or
    /// a sourced file the caller is back on its own line (`eval "$three_lines"; x` on line 1 reports line 1).
    /// </summary>
    [Fact]
    public void RecordLine_InsideARunTimeEval_CountsFromTheEvalsLine()
    {
        var s = new VarFrameStack();
        s.RecordLine(7);
        Assert.Equal(7, s.Line);
        s.EnterLines(s.Line - 1);            // eval on line 7 (BeginEvalLines): its text's line 1 is line 7
        s.RecordLine(2);
        Assert.Equal(8, s.Line);
        s.Enter();                           // a function called from the eval's text: its body has script lines
        s.RecordLine(3);
        Assert.Equal(3, s.Line);
        s.Exit();
        Assert.Equal(8, s.Line);
        s.EnterLines(s.Line - 1);            // a nested eval on line 8
        s.RecordLine(1);
        Assert.Equal(8, s.Line);
        s.RecordLine(3);
        Assert.Equal(10, s.Line);
        s.ExitLines();
        Assert.Equal(8, s.Line);             // back on the inner eval's line
        s.EnterLines(0);                     // a file sourced from the eval (BeginSourceLines): its own lines
        s.RecordLine(2);
        Assert.Equal(2, s.Line);
        s.ExitLines();
        Assert.Equal(8, s.Line);
        s.RecordLine(2);
        Assert.Equal(8, s.Line);
        s.ExitLines();
        Assert.Equal(7, s.Line);             // back on the eval's line
        s.RecordLine(9);
        Assert.Equal(9, s.Line);
        s.ExitLines();                       // unbalanced end: a no-op
        Assert.Equal(9, s.Line);
        s.RecordLine(4);
        Assert.Equal(4, s.Line);
    }
}