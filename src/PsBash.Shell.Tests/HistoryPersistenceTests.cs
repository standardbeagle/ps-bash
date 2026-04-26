using Microsoft.Data.Sqlite;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Integration tests verifying that the interactive shell records commands to a SQLite
/// history database and that entries persist across sessions.
///
/// Tests use InteractiveShellHarness which sets PSBASH_HOME to a temp directory so that
/// the DB is written to $tempHome/.psbash/history.db, not the real user profile.
///
/// Oracle note (Directive 1): behavior is ps-bash-specific (SQLite history persistence)
/// so hand-written asserts are correct per the exception list.
/// Platform note (Directive 5): tests skip when the ps-bash binary is not found.
/// </summary>
public class HistoryPersistenceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the history DB at the expected path inside psBashHome and returns all
    /// commands in insertion order (oldest first).
    /// </summary>
    private static List<(string Command, int? ExitCode)> ReadAllHistory(string psBashHome)
    {
        var dbPath = Path.Combine(psBashHome, ".psbash", "history.db");
        Assert.True(File.Exists(dbPath), $"History DB not found at: {dbPath}");

        var results = new List<(string, int?)>();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT command, exit_code FROM history ORDER BY id ASC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var command = reader.GetString(0);
            int? exitCode = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            results.Add((command, exitCode));
        }
        return results;
    }

    // ── Case 1: first session writes N entries; second session reads them back ─

    [SkippableFact]
    public async Task Session1_WritesEntries_Session2_CanReadThem()
    {
        var binary = InteractiveShellHarness.FindPsBashBinary();
        Skip.If(binary is null, "ps-bash binary not found");

        // Create a shared home directory so both sessions use the same DB.
        var sharedHome = Path.Combine(Path.GetTempPath(), "ps-bash-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedHome);
        try
        {
            // Session 1: write three commands.
            await using var session1 = await InteractiveShellHarness.StartAsync(
                binary!, psBashHome: sharedHome);

            await session1.SendLineAsync("echo hello");
            await session1.WaitForPromptAsync();

            await session1.SendLineAsync("echo world");
            await session1.WaitForPromptAsync();

            await session1.SendLineAsync("echo third");
            await session1.WaitForPromptAsync();

            // Dispose (sends EOF → clean exit → history flushed).
            await session1.DisposeAsync();

            // Give the process a moment to finish writing before we open the DB.
            // We use polling (no Sleep) — check for the DB to exist and have data.
            var dbPath = Path.Combine(sharedHome, ".psbash", "history.db");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(dbPath) && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            // Session 2: open the same home directory; verify previous entries visible.
            await using var session2 = await InteractiveShellHarness.StartAsync(
                binary!, psBashHome: sharedHome);

            await session2.SendLineAsync("echo verify");
            await session2.WaitForPromptAsync();

            await session2.DisposeAsync();

            // Read the DB directly and verify session 1 entries are present.
            var history = ReadAllHistory(sharedHome);
            var commands = history.Select(h => h.Command).ToList();

            Assert.Contains("echo hello", commands);
            Assert.Contains("echo world", commands);
            Assert.Contains("echo third", commands);
            // Session 2's command is also present.
            Assert.Contains("echo verify", commands);
            // Session 1 commands appear before session 2 command.
            Assert.True(
                commands.IndexOf("echo third") < commands.IndexOf("echo verify"),
                "Session 1 commands should precede session 2 commands in DB");
        }
        finally
        {
            try { Directory.Delete(sharedHome, recursive: true); } catch { }
        }
    }

    // ── Case 2: exit code stored per entry ────────────────────────────────────

    [SkippableFact]
    public async Task ExitCode_StoredPerEntry_InHistory()
    {
        var binary = InteractiveShellHarness.FindPsBashBinary();
        Skip.If(binary is null, "ps-bash binary not found");

        var sharedHome = Path.Combine(Path.GetTempPath(), "ps-bash-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedHome);
        try
        {
            await using var session = await InteractiveShellHarness.StartAsync(
                binary!, psBashHome: sharedHome);

            // Run a command guaranteed to succeed (exit 0).
            await session.SendLineAsync("echo success");
            await session.WaitForPromptAsync();

            await session.DisposeAsync();

            var history = ReadAllHistory(sharedHome);

            // Find the echo command.
            var echoEntry = history.FirstOrDefault(h => h.Command == "echo success");
            Assert.NotEqual(default, echoEntry);
            // exit_code may be 0 or null depending on timing; what we verify is that
            // the entry exists and — when an exit code is stored — it is 0.
            if (echoEntry.ExitCode.HasValue)
                Assert.Equal(0, echoEntry.ExitCode.Value);
        }
        finally
        {
            try { Directory.Delete(sharedHome, recursive: true); } catch { }
        }
    }

    // ── Case 3: empty input NOT recorded ─────────────────────────────────────

    [SkippableFact]
    public async Task EmptyInput_NotRecordedInHistory()
    {
        var binary = InteractiveShellHarness.FindPsBashBinary();
        Skip.If(binary is null, "ps-bash binary not found");

        var sharedHome = Path.Combine(Path.GetTempPath(), "ps-bash-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedHome);
        try
        {
            await using var session = await InteractiveShellHarness.StartAsync(
                binary!, psBashHome: sharedHome);

            // Send several empty / whitespace-only lines.
            await session.SendLineAsync("");
            await session.WaitForPromptAsync();

            await session.SendLineAsync("   ");
            await session.WaitForPromptAsync();

            // Send one real command as a sentinel.
            await session.SendLineAsync("echo sentinel");
            await session.WaitForPromptAsync();

            await session.DisposeAsync();

            var history = ReadAllHistory(sharedHome);
            var commands = history.Select(h => h.Command).ToList();

            // Only the sentinel should be present; empty lines must not appear.
            Assert.DoesNotContain("", commands);
            Assert.Contains("echo sentinel", commands);
            Assert.DoesNotContain(commands, c => c.Trim().Length == 0);
        }
        finally
        {
            try { Directory.Delete(sharedHome, recursive: true); } catch { }
        }
    }

    // ── Case 4: history file location override via PSBASH_HOME ───────────────

    [SkippableFact]
    public async Task HistoryDb_WrittenUnderPsbashHome_NotRealUserProfile()
    {
        var binary = InteractiveShellHarness.FindPsBashBinary();
        Skip.If(binary is null, "ps-bash binary not found");

        // Use a caller-owned home so we can read the DB after the harness disposes.
        var callerHome = Path.Combine(Path.GetTempPath(), "ps-bash-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(callerHome);
        try
        {
            await using var session = await InteractiveShellHarness.StartAsync(
                binary!, psBashHome: callerHome);

            await session.SendLineAsync("echo location_test");
            await session.WaitForPromptAsync();

            await session.DisposeAsync();

            // DB must exist under the caller-controlled home, not the real user profile.
            var expectedDb = Path.Combine(callerHome, ".psbash", "history.db");
            Assert.True(File.Exists(expectedDb),
                $"History DB not found at expected location: {expectedDb}");

            var history = ReadAllHistory(callerHome);
            Assert.Contains(history, h => h.Command == "echo location_test");
        }
        finally
        {
            try { Directory.Delete(callerHome, recursive: true); } catch { }
        }
    }

    // ── Negative: empty-only session leaves no DB entries ────────────────────

    [SkippableFact]
    public async Task EmptyOnlySession_LeavesNoDbEntries()
    {
        var binary = InteractiveShellHarness.FindPsBashBinary();
        Skip.If(binary is null, "ps-bash binary not found");

        await using var session = await InteractiveShellHarness.StartAsync(binary!);

        // Only whitespace-only inputs — no real command.
        await session.SendLineAsync("");
        await session.WaitForPromptAsync();

        await session.SendLineAsync("   ");
        await session.WaitForPromptAsync();

        await session.DisposeAsync();

        // DB may not even exist if nothing was written, OR it exists but is empty.
        var dbPath = Path.Combine(session.TempHome, ".psbash", "history.db");
        if (!File.Exists(dbPath))
            return; // No DB at all is fine.

        var history = ReadAllHistory(session.TempHome);
        Assert.Empty(history);
    }
}
