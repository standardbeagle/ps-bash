using Xunit;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Unit tests for the oracle cassette record/replay store. Pure serialization
/// and mode-detection logic — no process spawning, so these run on every
/// platform regardless of bash availability.
/// </summary>
public class OracleCassetteTests
{
    [Fact]
    public void Serialize_RoundTrips_AllFields()
    {
        var entry = new OracleCassette.CassetteEntry(
            Script: "echo 'hello world'",
            BashVersion: "5.2.21(1)-release",
            Stdout: "hello world\n",
            Stderr: "",
            ExitCode: 0);

        var text = OracleCassette.Serialize(entry);
        Assert.True(OracleCassette.TryParse(text, out var parsed));

        Assert.Equal(entry.Script, parsed!.Script);
        Assert.Equal(entry.BashVersion, parsed.BashVersion);
        Assert.Equal(entry.Stdout, parsed.Stdout);
        Assert.Equal(entry.Stderr, parsed.Stderr);
        Assert.Equal(entry.ExitCode, parsed.ExitCode);
    }

    [Fact]
    public void Serialize_RoundTrips_MultilineAndMarkerLikeContent()
    {
        // Content that itself contains the section markers and multiple lines
        // must survive because the parser uses byte-length headers, not marker
        // scanning, to slice sections.
        var entry = new OracleCassette.CassetteEntry(
            Script: "printf '==== STDOUT ====\\nline2\\n'",
            BashVersion: "5.2",
            Stdout: "==== STDOUT ====\nline2\n",
            Stderr: "warn: something\n",
            ExitCode: 3);

        var text = OracleCassette.Serialize(entry);
        Assert.True(OracleCassette.TryParse(text, out var parsed));
        Assert.Equal(entry.Script, parsed!.Script);
        Assert.Equal(entry.Stdout, parsed.Stdout);
        Assert.Equal(entry.Stderr, parsed.Stderr);
        Assert.Equal(entry.ExitCode, parsed.ExitCode);
    }

    [Fact]
    public void Serialize_RoundTrips_ThroughCrlfConversion()
    {
        // git on Windows may check the file out with CRLF line endings; the
        // parser must normalize to LF before applying the length headers.
        var entry = new OracleCassette.CassetteEntry(
            Script: "echo a; echo b",
            BashVersion: "5.2",
            Stdout: "a\nb\n",
            Stderr: "",
            ExitCode: 0);

        var text = OracleCassette.Serialize(entry).Replace("\n", "\r\n");
        Assert.True(OracleCassette.TryParse(text, out var parsed));
        Assert.Equal("a\nb\n", parsed!.Stdout);
        Assert.Equal(entry.Script, parsed.Script);
    }

    [Fact]
    public void Serialize_EmbedsScriptAndVersion_ForHumanDiff()
    {
        var entry = new OracleCassette.CassetteEntry(
            Script: "echo hi",
            BashVersion: "5.2.21(1)-release",
            Stdout: "hi\n",
            Stderr: "",
            ExitCode: 0);

        var text = OracleCassette.Serialize(entry);
        Assert.Contains("echo hi", text);                 // script is greppable
        Assert.Contains("5.2.21(1)-release", text);        // version bump shows in diff
        Assert.Contains("==== STDOUT ====", text);         // human-readable sections
    }

    [Fact]
    public void KeyFor_IsStableAndScriptDependent()
    {
        var a1 = OracleCassette.KeyFor("echo hello");
        var a2 = OracleCassette.KeyFor("echo hello");
        var b = OracleCassette.KeyFor("echo world");

        Assert.Equal(a1, a2);           // deterministic
        Assert.NotEqual(a1, b);         // distinct scripts -> distinct keys
        Assert.Matches("^[0-9a-f]{64}$", a1);
    }

    [Fact]
    public void ModeFromEnv_DefaultsToReplay()
    {
        Assert.Equal(OracleRunMode.Replay, OracleCassette.ModeFromEnv(record: null, live: null));
        Assert.Equal(OracleRunMode.Replay, OracleCassette.ModeFromEnv(record: "0", live: "0"));
    }

    [Fact]
    public void ModeFromEnv_RecordWinsOverLive()
    {
        Assert.Equal(OracleRunMode.Record, OracleCassette.ModeFromEnv(record: "1", live: "1"));
        Assert.Equal(OracleRunMode.Live, OracleCassette.ModeFromEnv(record: null, live: "1"));
    }

    [Fact]
    public void MissingMessage_TellsDeveloperToRecord()
    {
        var msg = OracleCassette.MissingMessage("echo hello");
        Assert.Contains("PSBASH_ORACLE_RECORD=1", msg);
        Assert.Contains("echo hello", msg);
    }

    [Fact]
    public void SaveAndTryLoad_RoundTripsThroughDisk()
    {
        // A script no committed cassette will ever use, so this test owns it.
        var script = $"echo cassette-disk-roundtrip-{Guid.NewGuid():N}";
        var entry = new OracleCassette.CassetteEntry(
            Script: script,
            BashVersion: "unit-test",
            Stdout: "line-a\nline-b\n",
            Stderr: "",
            ExitCode: 0);
        var path = Path.Combine(OracleCassette.CassettesDir, $"{OracleCassette.KeyFor(script)}.cassette");
        try
        {
            OracleCassette.Save(entry);
            Assert.True(OracleCassette.TryLoad(script, out var loaded));
            Assert.Equal(entry, loaded);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TryLoad_MissingScript_ReturnsFalse()
    {
        var script = $"echo definitely-no-cassette-{Guid.NewGuid():N}";
        Assert.False(OracleCassette.TryLoad(script, out var loaded));
        Assert.Null(loaded);
    }
}
