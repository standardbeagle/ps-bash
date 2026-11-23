using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// GUARD: a command name known only at run time (<c>c=mkdir; $c d</c>, <c>"$@"</c>) runs the command the static emitter
/// maps the literal name to (<c>PsEmitter.TryEmitMappedCommand</c>, one table), whatever PowerShell function has the name.
/// <para>
/// REGRESSIONS: Windows' PowerShell defines <c>mkdir</c> and <c>more</c> functions, so Resolve-BashCommand returned the
/// bare name, the run-time body took it for a function (which reads stdin like a program) and piped the record into
/// Invoke-BashMkdir: <c>c=mkdir; echo x | $c d</c> failed "The input object cannot be bound" with status 1 (fleet s24u,
/// RuntimeCommandParityDifferentialTests.DynamicCommandName_Piped_RunsACommandThatDoesNotReadStdin). And a name mapped
/// under another command's name (<c>egrep</c>, <c>fgrep</c>, <c>gawk</c>, <c>sh</c>, <c>tracert</c>) was looked up only as
/// <c>Invoke-Bash&lt;Name&gt;</c>, so it ran a program of that name or was "command not found". Linux's PowerShell has no
/// such functions, so this test defines one for EVERY mapped name, which is Windows' case and worse.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): the PowerShell resolution seam has no bash equivalent; the bash behavior is
/// Differential RuntimeCommandParityDifferentialTests.</para>
/// </summary>
[Collection("SdkHost")]
public class DynamicCommandTableGuardTests
{
    private static readonly Regex CaseLabel = new("^\\s*case \"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex InvokeBash = new(@"\bInvoke-Bash\w+", RegexOptions.CultureInvariant);

    /// <summary>Every name the static table maps (the case labels of TryEmitMappedCommand), less the builtins whose run-time
    /// form is their own (BashBuiltins.RunAsSource / CallStackBuiltins, BuiltinDispatchGuardTests).</summary>
    private static List<string> MappedNames()
    {
        string file = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        bool inTable = false;
        foreach (var line in File.ReadLines(file, Encoding.Latin1))
        {
            if (line.Contains("private static bool TryEmitMappedCommand(", StringComparison.Ordinal)) { inTable = true; continue; }
            if (!inTable) continue;
            if (line.StartsWith("            default:", StringComparison.Ordinal)) break;
            if (CaseLabel.Match(line) is { Success: true } m) names.Add(m.Groups[1].Value);
        }
        names.ExceptWith(BashBuiltins.RunAsSource);
        names.ExceptWith(BashBuiltins.CallStackBuiltins);
        return [.. names];
    }

    /// <summary>The cmdlet the literal name runs (the first Invoke-Bash* of its emission). <c>time</c> first in a command
    /// is bash's keyword, which times the pipeline after it, so no literal emission reaches the table's <c>case "time"</c>;
    /// its command is read from that case's body.</summary>
    private static string StaticCommand(string name)
    {
        if (name != "time") return InvokeBash.Match(BashTranspiler.Transpile(name + " x")!).Value;
        string table = TableSource();
        return InvokeBash.Match(table, table.IndexOf("case \"time\":", StringComparison.Ordinal)).Value;
    }

    private static string TableSource()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"), Encoding.Latin1);
        return text[text.IndexOf("private static bool TryEmitMappedCommand(", StringComparison.Ordinal)..];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunTimeName_ResolvesToTheStaticTablesCommand(bool shadowedByPowerShellFunctions)
    {
        var names = MappedNames();
        Assert.Contains("mkdir", names);
        Assert.Contains("egrep", names);
        var resolved = Resolve(names, shadowedByPowerShellFunctions);
        var wrong = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            string expected = StaticCommand(names[i]);
            if (expected.Length == 0) { wrong.Add($"{names[i]}: its static form names no Invoke-Bash* command"); continue; }
            string r = resolved[i];
            bool ok = r.StartsWith("S:", StringComparison.Ordinal)
                ? string.Equals(r[2..], expected, StringComparison.OrdinalIgnoreCase)
                : r.StartsWith("B:", StringComparison.Ordinal) && Regex.IsMatch(r, @"\b" + Regex.Escape(expected) + @"\b");
            if (!ok) wrong.Add($"{names[i]}: static {expected}, run time {r}");
        }
        Assert.True(wrong.Count == 0, "A run-time name must run the static table's command:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The behavior: a run-time name piped a record it does not read drains it and runs; a mapped name under another
    /// command's name reads the pipe or the group's stdin. Both with Windows-like PowerShell functions defined.
    /// </summary>
    [Fact]
    public async Task RunTimeName_ShadowedByAPowerShellFunction_RunsTheBashCommand()
    {
        const string shadows =
            "function global:mkdir { [CmdletBinding()] param([Parameter(Mandatory)][string[]]$Path) New-Item -ItemType Directory -Path $Path }\n"
            + "function global:more { 'foreign-more' }\n"
            + "function global:egrep { 'foreign-egrep' }\n";
        const string bash =
            "T=\"$(mktemp -d)\"; c=mkdir; echo x | $c \"$T/d\"; echo \"rc=$?\"; test -d \"$T/d\" && echo made; "
            + "c=more; echo m | $c; c=egrep; echo ab | $c 'a|z'; c=gawk; echo 'p q' | $c '{print $2}'; "
            + "c=fgrep; { $c b; } <<< $'a\\nb'; rm -rf \"$T\"";
        string script = shadows + "& {\n" + BashTranspiler.Transpile(bash) + "\n} | ForEach-Object { Get-BashText $_ }";

        var sdk = SdkRunspace.Create();
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = sdk.Runspace;
            ps.AddScript(script);
            var output = ps.Invoke();
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            // A record is a line with or without its newline: join on one and drop the empties.
            var lines = string.Join("\n", output.Select(o => o?.ToString())).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(new[] { "rc=0", "made", "m", "ab", "q", "b" }.SequenceEqual(lines),
                "output: [" + string.Join("|", lines) + "] errors: " + errors);
            Assert.DoesNotContain("cannot be bound", errors, StringComparison.OrdinalIgnoreCase);
        }
        finally { await sdk.DisposeAsync(); }
    }

    /// <summary>Resolve-BashCommand's answer for each name: S:string, B:script block text, F:function name.</summary>
    private static List<string> Resolve(List<string> names, bool shadow)
    {
        var sdk = SdkRunspace.Create();
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = sdk.Runspace;
            ps.AddScript("""
                param([string[]]$Names, [bool]$Shadow)
                if ($Shadow) { foreach ($n in $Names) { Set-Item -LiteralPath ('Function:global:' + $n) -Value { 'foreign' } } }
                foreach ($n in $Names) {
                    $r = Resolve-BashCommand -Name $n
                    if ($r -is [System.Management.Automation.FunctionInfo]) { 'F:' + $r.Name }
                    elseif ($r -is [scriptblock]) { 'B:' + $r.ToString() }
                    else { 'S:' + [string]$r }
                }
                """).AddParameter("Names", names.ToArray()).AddParameter("Shadow", shadow);
            var output = ps.Invoke().Select(o => o?.ToString() ?? "").ToList();
            Assert.True(output.Count == names.Count,
                $"{output.Count} answers for {names.Count} names; errors: " + string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
            return output;
        }
        finally { sdk.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
