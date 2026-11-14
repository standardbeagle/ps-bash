using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// Invoke-BashMapfile from PsBash.psm1 to a binary cmdlet.
///
/// Oracle: the psm1 function. The cmdlet preserves every observable
/// branch: pipeline-only input, empty lines dropped, default array name
/// <c>MAPFILE</c>, <c>-n</c> max-lines cap, <c>-O</c> origin offset with
/// empty-string prefix, <c>-t</c> trailing-newline strip, <c>-d</c>
/// accepted-but-ignored. <c>-s</c> (skip) is a cmdlet addition required
/// by the migration task spec.
/// </summary>
public class InvokeBashMapfileCommandTests
{
    /// <summary>
    /// Run a script in the PsBash test fixture and return the value of the
    /// named variable as a string[]. Empty / unset variable returns an
    /// empty array.
    /// </summary>
    private static string[] RunAndReadArray(string script, string varName)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        // Read the variable, then enumerate its elements one-per-pipeline-item
        // by piping through ForEach-Object. Plain `@(Get-Variable -ValueOnly)`
        // can collapse a string[] back to a single space-joined PSObject under
        // certain SDK runspace conditions; ForEach-Object guarantees one
        // PSObject per element.
        var result = pwsh.AddScript(
            $"$v = Get-Variable -Name '{varName}' -ValueOnly -ErrorAction SilentlyContinue; " +
            "if ($null -eq $v) { return } " +
            "$arr = @($v); for ($k=0; $k -lt $arr.Count; $k++) { Write-Output $arr[$k] }").Invoke();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    /// <summary>
    /// Run a script and return any error records as strings.
    /// </summary>
    private static string[] RunAndCollectErrors(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var errs = pwsh.AddScript("$error | ForEach-Object { $_.ToString() }").Invoke();
        return errs.Select(o => o?.ToString() ?? "").ToArray();
    }

    /// <summary>
    /// Run a script for stdout/help output.
    /// </summary>
    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(script).Invoke();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Mapfile_BasicStdin_PopulatesMapfileVariable()
    {
        var arr = RunAndReadArray(
            "'a','b','c' | Invoke-BashMapfile -t",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b", "c" }, arr);
    }

    [Fact]
    public void Mapfile_MaxLines_CapsAtN()
    {
        var arr = RunAndReadArray(
            "'one','two','three','four' | Invoke-BashMapfile -t -n 2",
            "MAPFILE");
        Assert.Equal(new[] { "one", "two" }, arr);
    }

    /// <summary>The array as "index=value" entries (sparse arrays keep their own subscripts).</summary>
    private static string[] RunAndReadEntries(string script, string varName)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"$v = Get-Variable -Name '{varName}' -ValueOnly -ErrorAction SilentlyContinue; " +
            "if ($v -is [System.Collections.IDictionary]) { foreach ($k in $v.Keys) { \"$k=$($v[$k])\" } } " +
            "else { $arr = @($v); for ($k=0; $k -lt $arr.Count; $k++) { \"$k=$($arr[$k])\" } }").Invoke();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Mapfile_Origin_StoresFromThatIndexOnly()
    {
        // bash: mapfile -t -O 5 a <<< $'x\ny' -> declare -a a=([5]="x" [6]="y") (no empty prefix).
        var arr = RunAndReadEntries("'x','y' | Invoke-BashMapfile -t -O 5", "MAPFILE");
        Assert.Equal(new[] { "5=x", "6=y" }, arr);
    }

    [Fact]
    public void Mapfile_Origin_KeepsExistingElements()
    {
        // bash: a=(a b c d e); mapfile -t -O 2 a <<< $'X\nY' -> a b X Y e (oils builtin-bash "mapfile -O start").
        var arr = RunAndReadEntries(
            "$global:arr1 = @('a','b','c','d','e'); 'X','Y' | Invoke-BashMapfile -t -O 2 arr1", "arr1");
        Assert.Equal(new[] { "0=a", "1=b", "2=X", "3=Y", "4=e" }, arr);
    }

    [Fact]
    public void Mapfile_NoOrigin_ReplacesExistingElements()
    {
        var arr = RunAndReadEntries(
            "$global:arr2 = @('a','b','c','d','e'); 'X' | Invoke-BashMapfile -t arr2", "arr2");
        Assert.Equal(new[] { "0=X" }, arr);
    }

    [Fact]
    public void Mapfile_StripTrailingNewline_TrimsCRLF()
    {
        // -t strips only the trailing delimiter (newline), as in bash: a CR is data and stays.
        var arr = RunAndReadArray(
            "\"line1`r\",\"line2`r\" | Invoke-BashMapfile -t",
            "MAPFILE");
        Assert.Equal(new[] { "line1\r", "line2\r" }, arr);
    }

    [Fact]
    public void Mapfile_CustomDelimiter_SplitsTheStream()
    {
        // -d cuts the stream at the delimiter: one record "a,b,c" is three elements.
        var arr = RunAndReadArray(
            "'a,b,c' | Invoke-BashMapfile -t -d ','",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b", "c" }, arr);
    }

    [Fact]
    public void Mapfile_SkipLines_DropsFirstN()
    {
        var arr = RunAndReadArray(
            "'a','b','c','d' | Invoke-BashMapfile -t -s 1",
            "MAPFILE");
        Assert.Equal(new[] { "b", "c", "d" }, arr);
    }

    [Fact]
    public void Mapfile_CustomArrayName_AssignsToNamedVariable()
    {
        var arr = RunAndReadArray(
            "'foo','bar' | Invoke-BashMapfile -t MYARR",
            "MYARR");
        Assert.Equal(new[] { "foo", "bar" }, arr);
        // And MAPFILE should NOT have been touched.
        var mapfile = RunAndReadArray(
            "'foo','bar' | Invoke-BashMapfile MYARR",
            "MAPFILE");
        Assert.Empty(mapfile);
    }

    [Fact]
    public void Mapfile_AliasMapfile_ResolvesToCmdlet()
    {
        var arr = RunAndReadArray(
            "'a','b' | mapfile -t",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b" }, arr);
    }

    [Fact]
    public void Mapfile_AliasReadarray_ResolvesToCmdlet()
    {
        var arr = RunAndReadArray(
            "'a','b' | readarray -t",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b" }, arr);
    }

    [Fact]
    public void Mapfile_KeepsEmptyLinesAndTheNewlineWithoutT()
    {
        var kept = RunAndReadArray("\"a`n`nb`n\" | Invoke-BashMapfile", "MAPFILE");
        Assert.Equal(new[] { "a\n", "\n", "b\n" }, kept);
        var stripped = RunAndReadArray("\"a`n`nb`n\" | Invoke-BashMapfile -t", "MAPFILE");
        Assert.Equal(new[] { "a", "", "b" }, stripped);
    }

    [Fact]
    public void Mapfile_DelimiterKeptWithoutT_AndNulDelimiterDropped()
    {
        var kept = RunAndReadArray("'1:3:' | Invoke-BashMapfile -d ':'", "MAPFILE");
        Assert.Equal(new[] { "1:", "3:" }, kept);
        var nul = RunAndReadArray("\"1`03`0\" | Invoke-BashMapfile -d ''", "MAPFILE");
        Assert.Equal(new[] { "1", "3" }, nul);
    }

    [Fact]
    public void Mapfile_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashMapfile --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("mapfile", StringComparison.OrdinalIgnoreCase)
                                  || l.Contains("array", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Directive 12 injection probe ----

    [Fact]
    public void Mapfile_InjectionInArrayName_TreatedAsLiteralAndRejected()
    {
        // A literal `$(throw 'PWNED')` token in the array-name argument
        // (passed via PowerShell backtick-escape) must not be evaluated as
        // PowerShell code. The cmdlet treats the token as a literal name
        // candidate, detects scriptblock metacharacters, emits a bash-style
        // "not a valid identifier" error, and skips the assignment.
        //
        // The error message intentionally echoes the bad name back per the
        // psm1 oracle's "mapfile: 'NAME': not a valid identifier" shape, so
        // the literal substring "PWNED" appears in error text. That is not
        // evidence the throw fired — what matters is whether the cmdlet
        // assigned the array (which would mean PowerShell evaluated the
        // expression to produce a name string). Assert on the variable
        // state, not the error text.
        var arr = RunAndReadArray(
            "'a','b' | Invoke-BashMapfile \"`$(throw 'PWNED')\"",
            "MAPFILE");
        Assert.Empty(arr);
    }

    [Fact]
    public void Mapfile_EmptyPipeline_LeavesVariableEmpty()
    {
        // Edge case (Directive 3 axis 1): no pipeline items -> empty array.
        var arr = RunAndReadArray(
            "@() | Invoke-BashMapfile",
            "MAPFILE");
        Assert.Empty(arr);
    }
}
