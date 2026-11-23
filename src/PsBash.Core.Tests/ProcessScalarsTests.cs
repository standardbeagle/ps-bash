using System;
using System.Linq;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// The foreground scalar store gives back the exact value written, an escaped-byte marker included
/// (<see cref="ProcessScalars"/>). REGRESSION: on Linux the process environment turned the marker of an invalid byte into
/// U+FFFD, so every scalar (<c>s=$'\xff'</c>, <c>$( )</c>, <c>read</c>, an export) lost it. Behavior vs bash:
/// Differential <c>RawBytesDifferentialTests.Scalars_KeepTheInvalidByte</c>. The store itself has no bash equivalent
/// (qa-rubric Directive 1), so these assert it directly; each test owns a unique variable name.
/// </summary>
public class ProcessScalarsTests
{
    private static readonly string Ff = "a" + RawBytes.ToMarker(0xFF) + "b";

    private static string Name() => "PSBASH_T_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Set_AValueWithAMarker_ReadsBackExactly()
    {
        var name = Name();
        try
        {
            ProcessScalars.Set(name, Ff);
            Assert.Equal(Ff, ProcessScalars.Get(name));
            Assert.Contains(ProcessScalars.Entries(), kv => kv.Key == name && kv.Value == Ff);
        }
        finally { ProcessScalars.Set(name, null); }
    }

    [Fact]
    public void Set_Null_Unsets()
    {
        var name = Name();
        ProcessScalars.Set(name, Ff);
        ProcessScalars.Set(name, null);
        Assert.Null(ProcessScalars.Get(name));
        Assert.Null(Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Set_AValueWithoutAMarker_IsTheEnvironmentsValue()
    {
        var name = Name();
        try
        {
            ProcessScalars.Set(name, Ff);
            ProcessScalars.Set(name, "plain");
            Assert.Equal("plain", ProcessScalars.Get(name));
            Assert.Equal("plain", Environment.GetEnvironmentVariable(name));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // A direct write of the environment (the host's per-command reset, PowerShell code) wins over a kept value.
    [Fact]
    public void ADirectEnvironmentWrite_MakesTheKeptValueStale()
    {
        var name = Name();
        try
        {
            ProcessScalars.Set(name, Ff);
            Environment.SetEnvironmentVariable(name, "direct");
            Assert.Equal("direct", ProcessScalars.Get(name));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // An entry copied from the environment into a child's gets the exact value; the caller's own value is its own.
    [Fact]
    public void ExactIfStored_TheCopiedValue_IsTheExactOne_AnotherValueIsUnchanged()
    {
        var name = Name();
        try
        {
            ProcessScalars.Set(name, Ff);
            var copied = Environment.GetEnvironmentVariable(name)!;
            Assert.Equal(Ff, ProcessScalars.ExactIfStored(name, copied));
            Assert.Equal("mine", ProcessScalars.ExactIfStored(name, "mine"));
            Assert.Equal(Ff, ProcessScalars.Get(name));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // A program is started by ps-bash with exact bytes only where PowerShell's launcher would lose one (NativeRunner).
    [Fact]
    public void ChildNeedsExactBytes_OnlyWhereTheEnvironmentLosesAByte()
    {
        var name = Name();
        try
        {
            if (ProcessScalars.EnvironmentHoldsEveryString)
            {
                Assert.False(ProcessScalars.ChildNeedsExactBytes([Ff]));
                return;
            }
            Assert.True(ProcessScalars.ChildNeedsExactBytes([Ff]));
            ProcessScalars.Set(name, Ff);
            Assert.True(ProcessScalars.KeepsAny);
            Assert.True(ProcessScalars.ChildNeedsExactBytes(["plain"]));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // Measured, not assumed: where the environment holds the marker nothing is kept, and the store is the environment.
    [Fact]
    public void EnvironmentHoldsEveryString_MatchesWhatTheEnvironmentGivesBack()
    {
        var name = Name();
        try
        {
            Environment.SetEnvironmentVariable(name, Ff);
            Assert.Equal(Environment.GetEnvironmentVariable(name) == Ff, ProcessScalars.EnvironmentHoldsEveryString);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
