using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// uniq <c>-z</c> / <c>--group[=METHOD]</c>, oracle GNU coreutils 9.4 (`wsl bash`, `od -c`). Bytes are
/// rendered with NUL as '@' and a record's newline as '~' so a missing/extra terminator shows.
/// </summary>
public class UniqZeroGroupTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public UniqZeroGroupTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string Bytes(string pipeline) =>
        Run($"$r = @({pipeline}); [PsBash.Cmdlets.BashRuntime]::RecordStreamText($r).Replace([string][char]0,'@').Replace(\"`n\",'~')")
            .AssertSuccess().Stdout;

    [Fact]
    public void Z_NulSeparatedRecordsInAndOut()   // oracle: printf "a\0a\0b" | uniq -z -> a\0b\0
    {
        Assert.Equal("a@b@", Bytes(@"Invoke-BashPrintf 'a\0a\0b' | Invoke-BashUniq '-z'"));
    }

    [Fact]
    public void Z_CountFormat()
    {
        Assert.Equal("      2 a@      1 b@", Bytes(@"Invoke-BashPrintf 'a\0a\0b\0' | Invoke-BashUniq '-zc'"));
    }

    [Fact]
    public void Z_NewlineIsDataNotATerminator()
    {
        // records "a\na", "a\na", "b": the newline is part of the record.
        Assert.Equal("a~a@b@", Bytes(@"Invoke-BashPrintf 'a\na\0a\na\0b\0' | Invoke-BashUniq '-z'"));
        Assert.Equal("a~a@a~a@", Bytes(@"Invoke-BashPrintf 'a\na\0a\na\0b\0' | Invoke-BashUniq '-z' '-D' '--all-repeated=separate'"));
    }

    [Fact]
    public void Z_AllRepeatedPrependUsesNulSeparator()
    {
        Assert.Equal("@a@a@", Bytes(@"Invoke-BashPrintf 'a\0a\0b\0' | Invoke-BashUniq '-z' '-D' '--all-repeated=prepend'"));
    }

    [Fact]
    public void Z_FileInput_AndOutputOperand()
    {
        var d = Path.Combine(Path.GetTempPath(), "uz_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        try
        {
            File.WriteAllBytes(Path.Combine(d, "in"), new byte[] { (byte)'x', 0, (byte)'x', 0, (byte)'y', 0 });
            Run($"Invoke-BashUniq '-z' '{d}/in' '{d}/out'").AssertSuccess();
            Assert.Equal(new byte[] { (byte)'x', 0, (byte)'y', 0 }, File.ReadAllBytes(Path.Combine(d, "out")));
        }
        finally { Directory.Delete(d, true); }
    }

    [Theory]   // every row: oracle `uniq --group<m>` on a,a,b,c,c
    [InlineData("", "a~a~~b~~c~c~")]
    [InlineData("=prepend", "~a~a~~b~~c~c~")]
    [InlineData("=append", "a~a~~b~~c~c~~")]
    [InlineData("=separate", "a~a~~b~~c~c~")]
    [InlineData("=both", "~a~a~~b~~c~c~~")]
    [InlineData("=s", "a~a~~b~~c~c~")]
    public void Group_Methods(string method, string expected)
    {
        Assert.Equal(expected, Bytes($"Invoke-BashPrintf 'a\\na\\nb\\nc\\nc\\n' | Invoke-BashUniq '--group{method}'"));
    }

    [Fact]
    public void Group_SingleLine_Both_AndEmptyInput()
    {
        Assert.Equal("~a~~", Bytes(@"Invoke-BashPrintf 'a\n' | Invoke-BashUniq '--group=both'"));
        Assert.Equal("", Bytes(@"@() | Invoke-BashUniq '--group=both'"));
        Assert.Equal("a~~b~~", Bytes(@"Invoke-BashPrintf 'a\nb\n' | Invoke-BashUniq '--group=append'"));
        Assert.Equal("~a~~b~", Bytes(@"Invoke-BashPrintf 'a\nb\n' | Invoke-BashUniq '--group=prepend'"));
    }

    [Fact]
    public void Group_WithZero_SeparatesWithNul()
    {
        Assert.Equal("a@a@@b@", Bytes(@"Invoke-BashPrintf 'a\0a\0b\0' | Invoke-BashUniq '-z' '--group'"));
    }

    [Fact]
    public void Group_Errors_ExitOne()
    {
        Run("'a' | Invoke-BashUniq '--group' '-c'").AssertFailed(1, "--group is mutually exclusive with -c/-d/-D/-u");
        Run("'a' | Invoke-BashUniq '--group=bogus'").AssertFailed(1, "invalid argument 'bogus' for '--group'");
    }

    [Fact]
    public void Group_UsesTheCmdletNotTheFusedCore()
    {
        // The fused lane carries \n-terminated lines only; both lanes must agree via decline.
        Assert.False(LineStreamRegistry.TryCreate("uniq", new[] { "-z" }, out _));
        Assert.False(LineStreamRegistry.TryCreate("uniq", new[] { "--group" }, out _));
        Assert.True(LineStreamRegistry.TryCreate("uniq", new[] { "-c" }, out _));
    }
}
