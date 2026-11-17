using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using PsBash.Core.Runtime;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// <see cref="RunspaceTemplate.Split"/> moves only statements it can prove are pure definitions; everything else
/// runs per runspace, in order.
/// Oracle note (Directive 1): host-internal, no bash equivalent; hand-written asserts.
/// </summary>
public class RunspaceTemplateSplitTests
{
    [Fact]
    public void Split_PlainFunctionAndConstantAlias_MoveToSessionState()
    {
        var parts = RunspaceTemplate.Split(
            "function Get-A { param($x) $x }\n$script:v = 1\nSet-Alias -Name 'a' -Value 'Get-A' -Force -Scope Global -Option AllScope\n")!;
        var fn = Assert.Single(parts.Functions);
        Assert.Equal("Get-A", fn.Name);
        Assert.Equal(" param($x) $x ", fn.Definition);
        var alias = Assert.Single(parts.Aliases);
        Assert.Equal(("a", "Get-A", ScopedItemOptions.AllScope), (alias.Name, alias.Value, alias.Options));
        Assert.Equal("$script:v = 1\n", parts.Residual);
    }

    [Fact]
    public void Split_ParametersOutsideTheBody_BecomeAParamBlock()
    {
        var fn = Assert.Single(RunspaceTemplate.Split("function F([string]$Text, $n = 2) { \"$Text$n\" }")!.Functions);
        var sb = ScriptBlock.Create(fn.Definition);
        var ast = (ScriptBlockAst)sb.Ast;
        Assert.Equal(new[] { "Text", "n" }, ast.ParamBlock!.Parameters.Select(p => p.Name.VariablePath.UserPath));
    }

    [Theory]
    [InlineData("filter F { $_ }")]                                   // a filter is not expressible as a function entry
    [InlineData("function global:F { 1 }")]                           // scope-qualified name
    [InlineData("function F { 1 }\nfunction F { 2 }")]               // redefined: order matters
    [InlineData("Set-Alias -Name a -Value $target")]                  // non-constant argument
    [InlineData("Set-Alias -Name a -Value b -Scope Local")]           // a scope other than the script's own
    [InlineData("Set-Alias -Name a -Value b -Option ReadOnly")]       // an option a session-state entry treats differently
    [InlineData("Set-Alias -Name a -Value b -PassThru")]              // an unknown parameter
    [InlineData("Set-Alias a b | Out-Null")]                          // more than one pipeline element
    [InlineData("& Set-Alias a b")]                                   // invocation operator
    public void Split_AnythingNotProvablyADefinition_StaysInTheResidual(string script)
    {
        var parts = RunspaceTemplate.Split(script)!;
        Assert.Empty(parts.Aliases);
        // A redefined function stays as both statements; a filter or scoped function stays as itself.
        Assert.Empty(parts.Functions);
        Assert.Equal(script.Replace("\r\n", "\n").Split('\n').Length, parts.Residual.TrimEnd('\n').Split('\n').Length);
    }

    [Theory]
    [InlineData("function F { ")]                                     // parse error
    [InlineData("param($x)\nfunction F { 1 }")]                       // a param block: the script is a command
    [InlineData("using namespace System.Text\nfunction F { 1 }")]
    [InlineData("begin { }\nprocess { }")]
    public void Split_ScriptThatIsNotAPlainStatementList_IsNotSplit(string script)
        => Assert.Null(RunspaceTemplate.Split(script));

    [Fact]
    public void Split_ResidualKeepsStatementOrder()
    {
        var parts = RunspaceTemplate.Split("$a = 1\nfunction F { }\n$b = 2\nSet-Alias x y\n$c = 3")!;
        Assert.Equal("$a = 1\n$b = 2\n$c = 3\n", parts.Residual);
    }
}

/// <summary>
/// A runspace opened from the <see cref="RunspaceTemplate"/> must end in the same state as one that ran the whole
/// psm1 as a script: same functions (same parameters and bodies), same aliases, same global variables. This is the
/// guard that keeps the template split from drifting as the psm1 changes; and the split must keep covering most
/// of the psm1, or every daemon command silently pays for the whole module again.
/// Oracle note (Directive 1): host-internal, no bash equivalent; hand-written asserts.
/// </summary>
[Collection("SdkHost")]
public class RunspaceTemplateEquivalenceTests
{
    private static (string Psm1, string CmdletsDll) ModulePaths()
    {
        var modulePath = ModuleExtractor.ExtractEmbedded();
        return (Path.Combine(Path.GetDirectoryName(modulePath)!, "PsBash.psm1"), ModuleExtractor.GetCmdletsDllPath());
    }

    [Fact]
    public void Template_SplitsMostOfThePsm1()
    {
        var (psm1, dll) = ModulePaths();
        var template = RunspaceTemplate.For(psm1, dll, SdkRunspace.CreateBaseSessionState);
        Assert.True(template.IsSplit, "the psm1 no longer splits: every runspace runs the whole module");
        long psm1Length = new FileInfo(psm1).Length;
        Assert.True(template.ResidualScript.Length * 4 < psm1Length,
            $"residual is {template.ResidualScript.Length} of {psm1Length} chars: a top-level statement shape the split "
            + "does not recognize is now carrying most of the module");
    }

    [Fact]
    public void TemplateRunspace_MatchesARunspaceThatRanTheWholePsm1()
    {
        var (psm1, dll) = ModulePaths();
        var template = RunspaceTemplate.For(psm1, dll, SdkRunspace.CreateBaseSessionState);

        using var reference = Open(RunspaceTemplateTestSupport.ReferenceSessionState(dll), psm1,
            SdkRunspace.ReadStartupModule(psm1), dropProxies: true);
        using var templated = Open(template.NewSessionState(), psm1, template.ResidualScript, dropProxies: false);

        var refFns = Functions(reference); var tplFns = Functions(templated);
        Assert.Equal(refFns.Keys.Order(), tplFns.Keys.Order());
        foreach (var (name, shape) in refFns)
            Assert.True(shape == tplFns[name], $"function {name} differs:\n--- whole psm1\n{shape}\n--- template\n{tplFns[name]}");

        Assert.Equal(Aliases(reference), Aliases(templated));

        var refVars = Variables(reference); var tplVars = Variables(templated);
        Assert.Equal(refVars.Keys.Order(), tplVars.Keys.Order());
        foreach (var (name, value) in refVars)
            Assert.True(value == tplVars[name], $"${name}: whole psm1 '{value}', template '{tplVars[name]}'");
    }

    private static Runspace Open(InitialSessionState iss, string psm1, string script, bool dropProxies)
    {
        var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();
        rs.SessionStateProxy.SetVariable("PsBashModulePsm1Path", psm1);
        rs.SessionStateProxy.SetVariable("__BashOperandsExpanded", true);
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(script).Invoke();
        // Not HadErrors: PowerShell sets it for a probe whose error was suppressed (`-ErrorAction Ignore`) too.
        Assert.True(ps.Streams.Error.Count == 0, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        // Loading the module records nothing in $Error either: a `-ErrorAction SilentlyContinue` probe still builds a
        // full ErrorRecord (message, position, stack) on every runspace the host builds. Probe with `Ignore`.
        ps.Commands.Clear();
        var recorded = ps.AddScript("$Error | ForEach-Object { $_.ToString() + ' @ ' + $_.InvocationInfo.PositionMessage }").Invoke();
        Assert.True(recorded.Count == 0, "loading the module recorded errors:\n" + string.Join("\n", recorded));
        if (dropProxies)
        {
            ps.Commands.Clear();
            foreach (var proxy in RunspaceTemplate.LiteralArgumentProxies)
                ps.AddScript($"Remove-Item -LiteralPath 'Function:\\{proxy}' -ErrorAction SilentlyContinue").Invoke();
        }
        return rs;
    }

    private static object? Eval(Runspace rs, string script)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        return ps.AddScript(script).Invoke().FirstOrDefault()?.BaseObject;
    }

    // name -> parameters + named-block bodies (whitespace-insensitive: a definition string and a psm1 function differ
    // only in where the text came from).
    private static Dictionary<string, string> Functions(Runspace rs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in rs.SessionStateProxy.InvokeCommand.GetCommands("*", CommandTypes.Function, nameIsPattern: true).Cast<FunctionInfo>())
        {
            // A psm1 function's Ast is its FunctionDefinitionAst (parameters may sit outside the body); a definition
            // string's is a ScriptBlockAst.
            var (ast, outside) = fn.ScriptBlock.Ast switch
            {
                FunctionDefinitionAst fd => (fd.Body, fd.Parameters),
                ScriptBlockAst sba => (sba, null),
                var other => throw new InvalidOperationException($"{fn.Name}: unexpected Ast {other.GetType().Name}"),
            };
            static string Squash(string t) => t.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            string Block(NamedBlockAst? b) => b is null ? "" : Squash(string.Concat(b.Statements.Select(s => s.Extent.Text)));
            IEnumerable<string> parms = ast.ParamBlock is { } pb ? pb.Parameters.Select(p => p.Extent.Text)
                : outside is { } o ? o.Select(p => p.Extent.Text) : Enumerable.Empty<string>();
            IEnumerable<string> attrs = ast.ParamBlock is { } pb2 ? pb2.Attributes.Select(a => a.Extent.Text) : Enumerable.Empty<string>();
            map[fn.Name] = "params: " + Squash(string.Join(",", parms))
                + "\nattrs: " + string.Join(",", attrs)
                + "\nbegin: " + Block(ast.BeginBlock) + "\nprocess: " + Block(ast.ProcessBlock) + "\nend: " + Block(ast.EndBlock);
        }
        return map;
    }

    private static string Aliases(Runspace rs) => (string)Eval(rs,
        "(Get-ChildItem Alias: | Sort-Object Name | ForEach-Object { \"$($_.Name)=$($_.Definition) [$($_.Options)]\" }) -join \"`n\"")!;

    // Global variables a module load could set, as text; automatic variables that differ only because the two
    // runspaces ran different amounts of script are left out.
    private static readonly HashSet<string> Automatic = new(StringComparer.OrdinalIgnoreCase)
    {
        "?", "^", "$", "_", "args", "input", "Error", "StackTrace", "MyInvocation", "LASTEXITCODE", "PSItem",
        "foreach", "switch", "Matches", "PSCmdlet", "this", "true", "false", "null", "PWD",
        // Wall-clock stamps set at load.
        "BashStartTime",
    };

    private static Dictionary<string, string> Variables(Runspace rs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vars = (System.Collections.IEnumerable)Eval(rs, "& { ,@(Get-Variable -Scope Global) }")!;
        foreach (var o in vars)
        {
            var v = (PSVariable)((o as PSObject)?.BaseObject ?? o);
            if (Automatic.Contains(v.Name)) continue;
            map[v.Name] = v.Value switch
            {
                null => "<null>",
                string or bool or int or long or double or Version => v.Value.ToString()!,
                System.Collections.ICollection c => $"{v.Value.GetType().Name}[{c.Count}]",
                _ => v.Value.GetType().FullName!,
            } + $" {v.Options}";
        }
        return map;
    }
}

internal static class RunspaceTemplateTestSupport
{
    /// <summary>The session state SdkRunspace used before the template: base + PsBash cmdlets, no psm1 entries.</summary>
    public static InitialSessionState ReferenceSessionState(string cmdletsDll)
    {
        var iss = SdkRunspace.CreateBaseSessionState();
        var asm = System.Reflection.Assembly.LoadFrom(cmdletsDll);
        foreach (var (type, attr) in CmdletTypeScanner.FindCmdletTypes(asm))
            iss.Commands.Add(new SessionStateCmdletEntry($"{attr.VerbName}-{attr.NounName}", type, null));
        return iss;
    }
}
