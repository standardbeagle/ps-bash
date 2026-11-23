using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The file each shell function was defined in: its entry in <c>BASH_SOURCE</c> while it runs, and the name a
/// diagnostic from its body starts with (<c>./lib.sh: line 2: cd: ...</c>). bash 5.2 (oracle in
/// <c>ShellLanguageGapDifferentialTests.FunctionSource_*</c>):
/// <list type="bullet">
/// <item>defined in a script file or a sourced file: that file, as it was named (also from an <c>eval</c> there, and
/// from a function body that file defined);</item>
/// <item>defined under <c>bash -c</c>: <c>environment</c>; read from stdin (<c>bash -s</c>, <c>cat s | bash</c>,
/// interactive): <c>main</c>;</item>
/// <item>a function the shell did not define itself (imported with <c>export -f</c>): <c>environment</c>.</item>
/// </list>
/// The definition records it at run time (<c>PsBuild.FunctionDefined</c>, emitted after every definition); a call puts
/// it on the frame stack (<see cref="VarFrameStack.EnterFunction(SessionState, string)"/>), which composes what the
/// script sees (<see cref="VarFrameStack.BashSource"/>). The table is shell state (<c>BashShellState</c>): a subshell's
/// definitions end with it, and a background job inherits a copy.
/// <para>The bug class: <c>$global:BASH_SOURCE</c> holds only the files being read, so a function saw its caller's
/// file (or nothing under <c>-c</c>) and its diagnostics named that file. Guard: the definition and call sites are the
/// only writers (<c>Core.Tests/ShellLanguageGapParserTests</c> asserts both emitted forms).</para>
/// </summary>
public static class FunctionSources
{
    /// <summary>The <c>$global:</c> variable holding the table (name to file), listed in <c>BashShellState</c>.</summary>
    public const string TableVariable = "__BashFunctionSources";

    /// <summary>bash's name for a function it did not read from a file: one defined under <c>-c</c> or imported.</summary>
    internal const string Environment = "environment";

    /// <summary>bash's name for a function read from stdin (a piped script, <c>bash -s</c>, an interactive shell).</summary>
    internal const string Main = "main";

    /// <summary>
    /// The definition of <paramref name="name"/> just ran: record where. A definition is a command whose status is 0
    /// (<c>false; g() { :; }; echo $?</c> prints 0, and a function whose last command is a definition returns 0), so it
    /// sets that status here, the one place every definition runs.
    /// </summary>
    public static void Defined(SessionState session, string name)
    {
        session.PSVariable.Set("global:LASTEXITCODE", 0);
        var files = VarFrameStack.BashSource(session.PSVariable.GetValue("global:BASH_SOURCE"));
        if (Unwrap(session.PSVariable.GetValue("global:" + TableVariable)) is not Hashtable table)
        {
            // Function names are PowerShell's, which ignores case: `F` redefines `f`.
            table = new Hashtable(StringComparer.OrdinalIgnoreCase);
            session.PSVariable.Set("global:" + TableVariable, table);
        }
        table[name] = DefinitionSite(files, ShellReader.DashC);
    }

    /// <summary>The file <paramref name="name"/> was defined in (<see cref="Environment"/> when the shell has no record).</summary>
    public static string Of(SessionState session, string? name)
    {
        if (name is not null && Unwrap(session.PSVariable.GetValue("global:" + TableVariable)) is Hashtable table
            && table[name] is string where)
            return where;
        return Environment;
    }

    /// <summary>
    /// The pure rule: the file being read where the definition ran (<paramref name="visibleSource"/>'s first entry), or,
    /// reading no file, <see cref="Environment"/> under <c>-c</c> and <see cref="Main"/> for stdin.
    /// </summary>
    internal static string DefinitionSite(IReadOnlyList<string> visibleSource, bool dashC)
        => visibleSource.Count > 0 && visibleSource[0].Length > 0 ? visibleSource[0] : dashC ? Environment : Main;

    private static object? Unwrap(object? o) => o is PSObject p ? p.BaseObject : o;
}
