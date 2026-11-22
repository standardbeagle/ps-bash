using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
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
    [InlineData("windir", "__psbash_cv_0_WINDIR")]          // Windows' own spelling: Git Bash leaves $windir unset too
    [InlineData("programdata", "__psbash_cv_0_PROGRAMDATA")] // only the exact native spelling is exempt
    [InlineData("PsModulePath", "__psbash_cv_105_PSMODULEPATH")]
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
    [InlineData("ProgramData")]          // exempt: Git Bash keeps these native spellings set (the system value)
    [InlineData("PSModulePath")]
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

    [Theory]
    [InlineData("windir", "WINDIR")]                 // Windows' native entry IS the upper-case variable (Git Bash lists WINDIR=)
    [InlineData("Path", "PATH")]
    [InlineData("SystemRoot", "SystemRoot")]         // not on the reserved list: unchanged
    [InlineData("ProgramData", "ProgramData")]       // exempt spelling: listed as is, as Git Bash does
    [InlineData("PSModulePath", "PSModulePath")]
    [InlineData("PATH", "PATH")]
    [InlineData("__psbash_cv_0_PATH", "__psbash_cv_0_PATH")]
    [InlineData("http_proxy", "http_proxy")]
    public void FromEnvironment_NativeSpelling_IsTheUpperCaseVariable(string envKey, string store)
    {
        using var _ = BashNameCase.Override(windows: true);
        Assert.Equal(store, BashNameCase.FromEnvironment(envKey));
    }

    [Theory]
    [InlineData("__psbash_cv_0_PATH", "path")]
    [InlineData("windir", "WINDIR")]
    [InlineData("TEMP", "TEMP")]
    public void ToListed_EnvironmentKey_IsWhatBashLists(string envKey, string listed)
    {
        using var _ = BashNameCase.Override(windows: true);
        Assert.Equal(listed, BashNameCase.ToListed(envKey));
    }

    [Fact]
    public void FromEnvironment_NotWindows_IsUnchanged()
    {
        using var _ = BashNameCase.Override(windows: false);
        Assert.Equal("windir", BashNameCase.FromEnvironment("windir"));
    }

    [Fact]
    public void StoreNameComparer_BashNameAndStoreName_AreOneKey()
    {
        using var _ = BashNameCase.Override(windows: true);
        var cmp = BashNameCase.StoreNameComparer;
        Assert.True(cmp.Equals("path", "__psbash_cv_0_PATH"));
        Assert.Equal(cmp.GetHashCode("path"), cmp.GetHashCode("__psbash_cv_0_PATH"));
        Assert.False(cmp.Equals("path", "PATH"));
        Assert.False(cmp.Equals("path", "Path"));
        Assert.False(cmp.Equals("x", "X"));
        var set = new HashSet<string>(cmp) { "__psbash_cv_0_TMP" };
        Assert.Contains("tmp", set);
        Assert.DoesNotContain("TMP", set);
    }

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

    // ── The emitter's name-keyed tables compare by store name ─────────────────────────────────────────────

    /// <summary>
    /// Every emitter table keyed by variable name holds names from AST records (store names) AND from word text
    /// (<c>unset tmp</c>, <c>local -p path</c>, a nameref target): one built with an ordinal comparer misses the
    /// other kind (<c>declare -n tmp=x; unset tmp</c> unset <c>tmp</c>, not <c>x</c>). Each must be built with
    /// <c>PsEmitter.VarNameKeys</c> (<see cref="BashNameCase.StoreNameComparer"/>). A source scan: a table built, or
    /// copied, with any other comparer fails here. A NEW name-keyed table joins <see cref="EmitterNameTables"/>.
    /// </summary>
    private static readonly string[] EmitterNameTables =
    [
        "_readonlyVars", "_namerefs", "_litAssigned", "_pendingNamerefs", "_functionLocalNames", "_intVars", "_caseVars",
        "_localAttrNames", "_envPrefixShadow", "_lexicalLocals", "_dynLocals",
        // Locals that become one of the tables.
        "var dynLocals", "var set = prior",
    ];

    [Fact]
    public void EmitterVariableNameTables_CompareByStoreName()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"), Encoding.Latin1);
        Assert.Contains("IEqualityComparer<string> VarNameKeys = BashNameCase.StoreNameComparer;", src);
        foreach (var field in EmitterNameTables.Where(n => n.StartsWith('_')))
            Assert.True(Regex.IsMatch(src, @"private static [\w<>, ]+\? " + field + ";"), field + " is no longer declared: update EmitterNameTables");

        var bad = new List<string>();
        int built = 0;
        foreach (var stmt in src.Split(';'))
        {
            if (!Regex.IsMatch(stmt, @"new (HashSet<string>|Dictionary<string, \w+>)\(")) continue;
            if (!EmitterNameTables.Any(n => n.StartsWith('_') ? Regex.IsMatch(stmt, @"\b" + n + @"\b") : stmt.Contains(n))) continue;
            built++;
            foreach (Match m in Regex.Matches(stmt, @"new (HashSet<string>|Dictionary<string, \w+>)\(([^()]*(\([^()]*\))?[^()]*)\)"))
                if (!m.Groups[2].Value.Contains("VarNameKeys")) bad.Add(m.Value.Trim());
        }
        Assert.True(built >= 15, $"only {built} name-table constructions found: the scan no longer matches the source");
        Assert.True(bad.Count == 0, "name-keyed emitter tables built without VarNameKeys: " + string.Join(" | ", bad));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Transpile_UnsetOfNamerefWithReservedSpelling_UnsetsTheTarget()
    {
        // The nameref table holds the store name (__psbash_cv_0_TMP); `unset tmp` looked up the word `tmp` and missed.
        using var _ = BashNameCase.Override(windows: true);
        Assert.EndsWith("Invoke-BashUnset x", BashTranspiler.Transpile("x=1; declare -n tmp=x; unset tmp").TrimEnd());
    }

    [Fact]
    public void Transpile_UnsetNOfReservedSpelling_DropsTheReference()
    {
        // bash: y=1 lines=5. The reference stayed, so `lines=5` wrote y.
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("y=1; declare -n lines=y; unset -n lines; lines=5");
        Assert.Contains("$env:__psbash_cv_0_LINES = \"5\"", ps);
        Assert.DoesNotContain("$env:y = \"5\"", ps);
    }

    [Fact]
    public void Transpile_LocalPOfReservedSpelling_ListsTheLocal()
    {
        // The function's locals are store names; `local -p path` compared the word and said "not found".
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("f(){ local path=q; local -p path; }; f");
        Assert.Contains("Invoke-BashDeclare '-p' path", ps);
        Assert.DoesNotContain("not found", ps);
    }

    [Fact]
    public void Transpile_NamerefToItselfWithReservedSpelling_IsRefused()
    {
        // The name was mapped, the target was not: they never compared equal.
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("declare -n path=path");
        Assert.Contains("'declare: path: nameref variable self references not allowed'", ps);
        Assert.DoesNotContain("SetNameref", ps);
    }

    [Fact]
    public void Transpile_RuntimeExportAppend_MapsTheNameAfterTheOperator()
    {
        // `export "path+=:b"` mapped `path+` (no reserved spelling) and stripped the `+` after: Env:path is PATH.
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("n=path; export \"$n+=:b\"");
        int strip = ps.IndexOf("EndsWith('+')", StringComparison.Ordinal);
        int map = ps.IndexOf("$__psbN = [PsBash.Core.BashNameCase]::ToStore($__psbN); Set-Item", StringComparison.Ordinal);
        Assert.True(strip > 0 && map > strip, ps);
        Assert.DoesNotContain("ToStore($__psbD", ps);
    }

    [Fact]
    public void Transpile_NamePrefixListing_ListsEnvironmentKeysAsBashDoes()
    {
        // ${!wi*}: Windows' own `windir` lists as WINDIR (Git Bash), a store name as its bash name.
        using var _ = BashNameCase.Override(windows: true);
        Assert.Contains("[PsBash.Core.BashNameCase]::ToListed($_.Name)", BashTranspiler.Transpile("echo ${!wi*}"));
    }

    // ── Transpiler: the emitted text uses the store name ─────────────────────────────────────────────────

    [Theory]
    [InlineData("path=abc", "$env:__psbash_cv_0_PATH = ")]
    [InlineData("echo \"$path\"", "$env:__psbash_cv_0_PATH")]
    [InlineData("echo ${path:-x}", "$env:__psbash_cv_0_PATH")]
    [InlineData("for path in a b; do :; done", "BashVariableStore]::Set('__psbash_cv_0_PATH'")]
    [InlineData("path[0]=1", "__psbash_cv_0_PATH = Set-BashElement")]
    [InlineData("declare path=x", "$env:__psbash_cv_0_PATH = ")]
    [InlineData("typeset -i lines=3", "$env:__psbash_cv_0_LINES = ")]
    [InlineData("readonly tmp=1", "$env:__psbash_cv_0_TMP = ")]
    [InlineData("n=path; export \"$n=1\"", "[PsBash.Core.BashNameCase]::ToStore(")]
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
    [InlineData("for PATH in a; do :; done", "BashVariableStore]::Set('PATH'")]
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
    public void Transpile_ReadonlyRefusal_NamesTheBashVariable()
    {
        // The static refusal is emitted text (PsBuild.ShellDiagnosticLine demangles a literal message).
        using var _ = BashNameCase.Override(windows: true);
        var ps = BashTranspiler.Transpile("readonly path=1; path=2");
        Assert.Contains("'path: readonly variable'", ps);
        Assert.DoesNotContain("$env:path", ps);
    }

    // ── Messages: a store name in diagnostic text reads as the bash name ─────────────────────────────────

    [Theory]
    [InlineData("__psbash_cv_0_PATH: unbound variable", "path: unbound variable")]
    [InlineData("local: __psbash_cv_1_TEMP: readonly variable", "local: Temp: readonly variable")]
    [InlineData("__psbash_cv_0_PATH[2]: bad array subscript", "path[2]: bad array subscript")]
    [InlineData("a __psbash_cv_0_TMP b __psbash_cv_0_OS", "a tmp b os")]
    [InlineData("x__psbash_cv_0_PATH: y", "x__psbash_cv_0_PATH: y")]           // part of a longer identifier
    [InlineData("__psbash_cv_0_NOTRESERVED: z", "__psbash_cv_0_NOTRESERVED: z")] // not a store name
    [InlineData("PATH: unbound variable", "PATH: unbound variable")]
    [InlineData("+ __psbash_cv_0_PATH=abc", "+ path=abc")]                      // a set -x line (psm1 Write-BashXtrace)
    [InlineData("", "")]
    public void ToBashText_StoreNamesInAMessage_ReadAsBashNames(string text, string expected)
        => Assert.Equal(expected, BashNameCase.ToBashText(text));

    [Fact]
    public void ShellDiagnosticLine_DemanglesALiteral_LeavesAnExpressionAlone()
    {
        Assert.Contains("'path: readonly variable'", PsBuild.ShellDiagnosticLine(PsBuild.SingleQuote("__psbash_cv_0_PATH: readonly variable")));
        // An expression is code: the store name there is the variable's real name.
        Assert.Contains("$env:__psbash_cv_0_PATH", PsBuild.ShellDiagnosticLine("($env:__psbash_cv_0_PATH + ': x')"));
    }

    // ── The by-name primitives map a bash name themselves (environment-model.md section 12) ─────────────

    // ShellExports by name: ChildEnvironmentGuardTests.ReservedSpelling_ExportedByItsBashName_IsTheStoreName (it needs the
    // shell start, ShellExports.Reset, that class's tests share and run one at a time).


    [Fact]
    public void ScalarOverlayForCommand_BashNames_AreStoreNames()
    {
        // `env path=x cmd`: the command's store holds the store name; PATH is not replaced.
        using var _ = BashNameCase.Override(windows: true);
        var overlay = ScalarOverlay.ForCommand(true, [], [new("path", "x")]);
        Assert.Equal("x", overlay.Get("__psbash_cv_0_PATH"));
        Assert.Null(overlay.Get("PATH"));
    }
}
