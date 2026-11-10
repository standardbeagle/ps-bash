using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A lowercase bash variable must not clobber the Windows system variable of the same name (<c>path=abc</c> made
/// <c>git</c> "command not found"; <c>tmp=zz</c> sent temp files to <c>./zz/</c>). <see cref="BashNameCase"/> diverts a
/// non-upper-case spelling of a reserved name to a mangled store name. Hand-written asserts: this is ps-bash's own
/// storage scheme, not bash-observable output (the Differential class <c>ReservedNameCaseTests</c> covers that).
/// The Windows behaviour is forced through the <see cref="BashNameCase.Override"/> seam so these run on any OS.
/// </summary>
public class BashNameCaseTests
{
    [Theory]
    [InlineData("path", "__psbash_cv_0_PATH")]
    [InlineData("Path", "__psbash_cv_1_PATH")]
    [InlineData("pATH", "__psbash_cv_e_PATH")]
    [InlineData("PaTh", "__psbash_cv_5_PATH")]
    [InlineData("tmp", "__psbash_cv_0_TMP")]
    [InlineData("Temp", "__psbash_cv_1_TEMP")]
    [InlineData("os", "__psbash_cv_0_OS")]
    [InlineData("home", "__psbash_cv_0_HOME")]
    [InlineData("ps1", "__psbash_cv_0_PS1")]
    [InlineData("bash_version", "__psbash_cv_0_BASH_VERSION")]
    [InlineData("path[0]", "__psbash_cv_0_PATH[0]")]
    [InlineData("path[$i+1]", "__psbash_cv_0_PATH[$i+1]")]
    public void ToStore_ReservedSpelling_IsMangled(string bash, string store)
    {
        Assert.Equal(store, BashNameCase.ToStore(bash, windows: true));
        Assert.Equal(bash, BashNameCase.ToBash(store));
        Assert.True(BashNameCase.IsMangled(store));
    }

    [Theory]
    [InlineData("PATH")]          // the real variable: never diverted
    [InlineData("TMP")]
    [InlineData("OS")]
    [InlineData("PS1")]
    [InlineData("x")]             // an ordinary name (x vs X stays a documented limitation)
    [InlineData("http_proxy")]    // lowercase by convention and must reach children
    [InlineData("mypath")]        // merely contains a reserved name
    [InlineData("path2")]
    [InlineData("_path")]
    [InlineData("pathext_x")]
    [InlineData("PATH[0]")]
    [InlineData("?")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("pa-th")]
    [InlineData("__psbash_cv_0_PATH")]   // idempotent: a store name maps to itself
    [InlineData("__psbash_cv_0_PATH[2]")]
    public void ToStore_NotAReservedSpelling_IsUnchanged(string name)
        => Assert.Equal(name, BashNameCase.ToStore(name, windows: true));

    [Theory]
    [InlineData("path")]
    [InlineData("tmp")]
    [InlineData("Home")]
    public void ToStore_NotWindows_IsUnchanged(string name)
        => Assert.Equal(name, BashNameCase.ToStore(name, windows: false));

    [Theory]
    [InlineData("__psbash_cv_0_NOTRESERVED")]   // looks mangled, but the name is not on the list
    [InlineData("__psbash_cv_3_OS")]            // mask says all upper-case: ToStore never produces it
    [InlineData("__psbash_cv_zz_PATH")]          // mask is not hex
    [InlineData("__psbash_cv_10_OS")]           // mask wider than the name
    [InlineData("__psbash_cv__PATH")]
    [InlineData("__psbash_ea_path")]
    [InlineData("PATH")]
    [InlineData("path")]
    public void ToBash_NotAMangledName_IsUnchanged(string name)
    {
        Assert.Equal(name, BashNameCase.ToBash(name));
        Assert.False(BashNameCase.IsMangled(name));
    }

    [Fact]
    public void ToBash_StoreNameInAnyCase_Demangles()
        // The environment block may hand a name back in another case; the mask, not the stored case, decides.
        => Assert.Equal("Path", BashNameCase.ToBash("__PSBASH_CV_1_path"));

    [Fact]
    public void Override_IsScopedToTheFlow()
    {
        using (BashNameCase.Override(windows: true))
        {
            Assert.True(BashNameCase.Enabled);
            using (BashNameCase.Override(windows: false))
                Assert.Equal("path", BashNameCase.ToStore("path"));
            Assert.Equal("__psbash_cv_0_PATH", BashNameCase.ToStore("path"));
        }
    }

    // ── AST: every node that carries a variable name stores the mapped name ──────────────────────────────

    /// <summary>Each AST record with a variable-name parameter, built with the bash name <c>path</c>.</summary>
    private static readonly (Type Type, Func<object> Build, Func<object, string> Read)[] VariableNodes =
    [
        (typeof(WordPart.SimpleVarSub), () => new WordPart.SimpleVarSub("path"), o => ((WordPart.SimpleVarSub)o).Name),
        (typeof(WordPart.BracedVarSub), () => new WordPart.BracedVarSub("path", null), o => ((WordPart.BracedVarSub)o).Name),
        (typeof(Assignment), () => new Assignment("path", AssignOp.Equal, null), o => ((Assignment)o).Name),
        (typeof(EnvPair), () => new EnvPair("path", null), o => ((EnvPair)o).Name),
        (typeof(Command.ForIn), () => new Command.ForIn("path", ImmutableArray<CompoundWord>.Empty, new Command.Simple(ImmutableArray<CompoundWord>.Empty, ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty)), o => ((Command.ForIn)o).Var),
        (typeof(Command.Select), () => new Command.Select("path", ImmutableArray<CompoundWord>.Empty, new Command.Simple(ImmutableArray<CompoundWord>.Empty, ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty)), o => ((Command.Select)o).Var),
        (typeof(ArithmeticExpr.Identifier), () => new ArithmeticExpr.Identifier("path"), o => ((ArithmeticExpr.Identifier)o).Name),
        (typeof(ArithmeticExpr.Assignment), () => new ArithmeticExpr.Assignment("path", ArithmeticAssignmentOp.Assign, new ArithmeticExpr.Number(1)), o => ((ArithmeticExpr.Assignment)o).Name),
        (typeof(ArithmeticExpr.Increment), () => new ArithmeticExpr.Increment("path", 1, true), o => ((ArithmeticExpr.Increment)o).Name),
        (typeof(ArithmeticExpr.Parameter), () => new ArithmeticExpr.Parameter("$path"), o => ((ArithmeticExpr.Parameter)o).LookupKey),
    ];

    [Fact]
    public void AstVariableNodes_StoreTheMappedName()
    {
        using var _ = BashNameCase.Override(windows: true);
        foreach (var (type, build, read) in VariableNodes)
            Assert.True(read(build()) == "__psbash_cv_0_PATH", $"{type.Name} kept the bash name");
    }

    [Fact]
    public void AstVariableNodes_SeamOff_KeepTheBashName()
    {
        using var _ = BashNameCase.Override(windows: false);
        foreach (var (type, build, read) in VariableNodes)
            Assert.True(read(build()) == "path", $"{type.Name} changed the name with the seam off");
    }

    /// <summary>
    /// A NEW record with a variable-name parameter must join <see cref="VariableNodes"/> (and normalise its name),
    /// or a reserved spelling in it would silently write the system variable again. Function names and here-doc
    /// fd variables are not bash variables of a reserved spelling in practice and are listed as exempt.
    /// </summary>
    [Fact]
    public void EveryAstRecordWithAVariableName_IsCovered()
    {
        var exempt = new[] { typeof(Command.ShFunction) };
        var covered = VariableNodes.Select(n => n.Type).ToHashSet();
        var nameParams = new[] { "Name", "Var", "VarName", "LookupKey" };
        var missing = typeof(BashNode).Assembly.GetTypes()
            .Where(t => typeof(BashNode).IsAssignableFrom(t) && !t.IsAbstract && !exempt.Contains(t))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(string) && nameParams.Contains(p.Name))))
            .Where(t => !covered.Contains(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(missing.Count == 0, "AST records with a variable name not covered by BashNameCase: " + string.Join(", ", missing));
    }

    // ── Transpiler: the emitted text uses the store name ─────────────────────────────────────────────────

    [Theory]
    [InlineData("path=abc", "$env:__psbash_cv_0_PATH = ")]
    [InlineData("echo \"$path\"", "$env:__psbash_cv_0_PATH")]
    [InlineData("echo ${path:-x}", "$env:__psbash_cv_0_PATH")]
    [InlineData("for path in a b; do :; done", "foreach ($__psbash_cv_0_PATH in")]
    [InlineData("path[0]=1", "$__psbash_cv_0_PATH = Set-BashElement")]
    [InlineData("path=x env", "$env:__psbash_cv_0_PATH = \"x\"")]
    [InlineData("export tmp=x", "$env:__psbash_cv_0_TMP = ")]
    [InlineData("f() { local path=x; echo $path; }", "$env:__psbash_cv_0_PATH = \"x\"")]
    [InlineData("Temp=1", "$env:__psbash_cv_1_TEMP = ")]
    public void Transpile_ReservedLowercaseName_UsesStoreName(string bash, string expected)
    {
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("$env:path", ps);
        Assert.DoesNotContain("$env:tmp ", ps);
    }

    [Theory]
    [InlineData("PATH=/x:$PATH", "$env:PATH = ")]
    [InlineData("echo $TMP", "$env:TMP")]
    [InlineData("for PATH in a; do :; done", "foreach ($PATH in")]
    public void Transpile_UpperCaseReservedName_IsTheRealVariable(string bash, string expected)
    {
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(expected, ps);
        Assert.DoesNotContain(BashNameCase.Prefix, ps);
    }

    [Theory]
    [InlineData("path=abc; echo \"$path\" ${path:-x}")]
    [InlineData("for path in a b; do echo $path; done")]
    [InlineData("path=x env; f() { local tmp=1; }")]
    public void Transpile_SeamOff_HasNoStoreNames(string bash)
    {
        // Linux/macOS: the environment is case-sensitive, nothing is diverted and the output is as before.
        using var _ = BashNameCase.Override(windows: false);
        var ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain(BashNameCase.Prefix, ps);
    }

    [Fact]
    public void Transpile_NounsetMessage_NamesTheBashVariable()
    {
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("set -u; echo $path");
        Assert.Contains("'path: '", ps);
        Assert.DoesNotContain("'__psbash_cv_0_PATH: '", ps);
    }

    [Fact]
    public void Transpile_ParamErrorMessage_NamesTheBashVariable()
    {
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("echo ${tmp:?gone}");
        Assert.Contains("'tmp: '", ps);
    }
}
