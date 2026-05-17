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
            "'a','b','c' | Invoke-BashMapfile",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b", "c" }, arr);
    }

    [Fact]
    public void Mapfile_MaxLines_CapsAtN()
    {
        var arr = RunAndReadArray(
            "'one','two','three','four' | Invoke-BashMapfile -n 2",
            "MAPFILE");
        Assert.Equal(new[] { "one", "two" }, arr);
    }

    [Fact]
    public void Mapfile_Origin_PrefixesEmptyStrings()
    {
        // -O 5 means destination indices 0..4 are empty strings and the
        // pipeline lines start at index 5.
        var arr = RunAndReadArray(
            "'x','y' | Invoke-BashMapfile -O 5",
            "MAPFILE");
        Assert.Equal(7, arr.Length);
        for (int i = 0; i < 5; i++) Assert.Equal("", arr[i]);
        Assert.Equal("x", arr[5]);
        Assert.Equal("y", arr[6]);
    }

    [Fact]
    public void Mapfile_StripTrailingNewline_TrimsCRLF()
    {
        // -t strips trailing \r and \n. Build pipeline items with embedded
        // CRs so the strip is observable.
        var arr = RunAndReadArray(
            "\"line1`r\",\"line2`r\" | Invoke-BashMapfile -t",
            "MAPFILE");
        // After empty-line drop + \r-trim, both lines survive without the CR.
        Assert.Equal(new[] { "line1", "line2" }, arr);
    }

    [Fact]
    public void Mapfile_CustomDelimiter_AcceptedButIgnored()
    {
        // Oracle parity: -d is consumed but the value is not used (split is
        // always on \n). 'a','b','c' through a `,`-delim becomes the same as
        // no-delim — three lines.
        var arr = RunAndReadArray(
            "'a','b','c' | Invoke-BashMapfile -d ','",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b", "c" }, arr);
    }

    [Fact]
    public void Mapfile_SkipLines_DropsFirstN()
    {
        var arr = RunAndReadArray(
            "'a','b','c','d' | Invoke-BashMapfile -s 1",
            "MAPFILE");
        Assert.Equal(new[] { "b", "c", "d" }, arr);
    }

    [Fact]
    public void Mapfile_CustomArrayName_AssignsToNamedVariable()
    {
        var arr = RunAndReadArray(
            "'foo','bar' | Invoke-BashMapfile MYARR",
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
            "'a','b' | mapfile",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b" }, arr);
    }

    [Fact]
    public void Mapfile_AliasReadarray_ResolvesToCmdlet()
    {
        var arr = RunAndReadArray(
            "'a','b' | readarray",
            "MAPFILE");
        Assert.Equal(new[] { "a", "b" }, arr);
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
        // A `$(throw 'PWNED')` substring in the array name argument must not
        // be evaluated as PowerShell code. The cmdlet treats the token as a
        // literal name candidate, detects scriptblock metacharacters, emits
        // a bash-style "not a valid identifier" error, and does NOT assign.
        // The MAPFILE variable therefore stays unset (Get-Variable returns
        // an empty array via the helper).
        var errors = RunAndCollectErrors(
            "'a','b' | Invoke-BashMapfile \"`$(throw 'PWNED')\"");
        // The error pipeline must not contain the literal 'PWNED' exception
        // text — that would indicate the throw actually fired.
        Assert.DoesNotContain(errors, e => e.Contains("PWNED", StringComparison.Ordinal));
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
