using System;
using System.Linq;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// The foreground scalar store gives back the exact value written, an escaped-byte marker included
/// (<see cref="ProcessScalars"/>). REGRESSION: on Linux the process environment turned the marker of an invalid byte into
/// U+FFFD, so every scalar (<c>s=$'\xff'</c>, <c>$( )</c>, <c>read</c>, an export) lost it. Behavior vs bash:
/// Differential <c>RawBytesDifferentialTests.Scalars_KeepTheInvalidByte</c>. The store itself has no bash equivalent
/// (qa-rubric Directive 1), so these assert it directly; each test owns a unique variable name.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // three tests write the environment directly, as the host's reset does
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
            using var direct = ProcessEnv.Set(name, "direct");
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
            ShellExports.Export(name);
            Assert.True(ProcessScalars.KeepsAnyExported);
            Assert.True(ProcessScalars.ChildNeedsExactBytes(["plain"]));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // A kept value that is not exported still turns the shell's stand-in on (KeepsAny): `prog "$s"` carries it in argv,
    // which only the stand-in sees. REGRESSION: keyed on exported values only, `s=$'\xff'; /usr/bin/printf %s "$s"` wrote
    // ef bf bd (Differential RawBytesDifferentialTests.Export_ToAProgram_KeepsTheInvalidByte, row argv-noexport).
    [Fact]
    public void KeepsAny_ANotExportedValue_TurnsTheStandInOn()
    {
        if (ProcessScalars.EnvironmentHoldsEveryString) return; // nothing is ever kept there
        var name = Name();
        try
        {
            ProcessScalars.Set(name, Ff);
            Assert.True(ProcessScalars.KeepsAny);
            Assert.True(ProcessScalars.ChildNeedsExactBytes([Ff]));
        }
        finally { ProcessScalars.Set(name, null); }
    }

    // The switch turns off as soon as no exported scalar holds such a value: programs go back to PowerShell's launcher
    // (and keep the terminal). Each case is a way the value goes: unset, a plain value, and a direct environment write
    // (the host's reset); the export attribute removed is Differential (RawBytesDifferentialTests). Kept values of other tests may run meanwhile, so each case
    // asserts on its own name through ExactIfStored / Get rather than on the global switch alone.
    [Theory]
    [InlineData("unset")]
    [InlineData("plain")]
    [InlineData("direct")]
    public void KeepsAnyExported_TurnsOff_WhenTheValueGoes(string how)
    {
        if (ProcessScalars.EnvironmentHoldsEveryString) return; // nothing is ever kept there
        var name = Name();
        IDisposable? direct = null;
        try
        {
            ProcessScalars.Set(name, Ff);
            ShellExports.Export(name);
            Assert.True(ProcessScalars.KeepsAnyExported);
            switch (how)
            {
                case "unset": ProcessScalars.Set(name, null); break;
                case "plain": ProcessScalars.Set(name, "ok"); break;
                case "direct": direct = ProcessEnv.Set(name, "direct"); break;
            }
            Assert.False(KeptAndExported(name));
        }
        finally { direct?.Dispose(); ProcessScalars.Set(name, null); }
    }

    // This name's own part of KeepsAnyExported: its exact value is kept (the stored form maps back to it) and exported.
    private static bool KeptAndExported(string name)
        => Environment.GetEnvironmentVariable(name) is { } stored
           && !ReferenceEquals(ProcessScalars.ExactIfStored(name, stored), stored)
           && ShellExports.IsExported(name);

    // Measured, not assumed: where the environment holds the marker nothing is kept, and the store is the environment.
    [Fact]
    public void EnvironmentHoldsEveryString_MatchesWhatTheEnvironmentGivesBack()
    {
        var name = Name();
        using (ProcessEnv.Set(name, Ff))
            Assert.Equal(Environment.GetEnvironmentVariable(name) == Ff, ProcessScalars.EnvironmentHoldsEveryString);
    }
}
