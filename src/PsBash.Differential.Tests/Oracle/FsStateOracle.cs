using System.Text;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Reusable GNU-vs-ps-bash comparison for MUTATING commands: runs a scenario in a fresh
/// scratch dir, starting from the same fixture tree, in real bash and in ps-bash, and compares
/// (1) the command's stdout, (2) its exit status and (3) the RESULTING FILESYSTEM STATE —
/// every relative path, whether it is a file or a directory, and each file's exact bytes.
/// Diagnostics go to <c>2&gt;/dev/null</c> by default: GNU quotes operands as typed while
/// ps-bash prints resolved paths, so message wording is not the contract here — what the
/// command did to the disk is (these bugs were silent data loss at exit 0).
///
/// <para>How it reuses the cassette machinery: the scenario is ONE bash script whose stdout is
/// <c>== stdout</c> / the command's output / <c>== exit N</c> / <c>== fs</c> / one snapshot line
/// per path (<c>D ./dir</c> or <c>F ./file BASE64</c>). Because the snapshot is plain stdout,
/// <see cref="AssertOracle.EqualAsync"/> records and replays it like any other case — the
/// cassette embeds the whole filesystem state, so replay needs no WSL/bash and diffs the
/// frozen oracle snapshot against a live ps-bash run. Record with
/// <c>PSBASH_ORACLE_RECORD=1</c>.</para>
///
/// <para>File bytes are base64 (not <c>cat</c>) so a missing/extra trailing newline, embedded
/// whitespace or a NUL survives the canonicalizer's per-line whitespace trimming — that is the
/// difference between <c>printf x | tee f</c> writing <c>x</c> and <c>x\n</c>.</para>
/// </summary>
public static class FsStateOracle
{
    // The snapshot function. Uses only find/sort/read/base64/echo, which both real bash and
    // ps-bash provide. `-w0` = one unwrapped line per file (an empty file yields an empty
    // payload). LC_ALL=C pins sort order on the bash side.
    public const string SnapshotFunction =
        "__fs_snap() { find . | LC_ALL=C sort | while IFS= read -r p; do " +
        "if [ -d \"$p\" ]; then echo \"D $p\"; else echo \"F $p $(base64 -w0 \"$p\")\"; fi; done; }";

    /// <summary>
    /// Composes the scenario script: <paramref name="setup"/> builds the fixture tree (output
    /// discarded), <paramref name="command"/> is the command under test (stdout recorded,
    /// stderr discarded unless <paramref name="compareStderr"/>), then exit status and the
    /// filesystem snapshot are printed. Pure — unit-testable without spawning anything.
    /// </summary>
    public static string Compose(string setup, string command, bool compareStderr = false)
    {
        var stderr = compareStderr ? "" : " 2>/dev/null";
        var sb = new StringBuilder();
        sb.Append(SnapshotFunction).Append('\n');
        sb.Append("d=$(mktemp -d) && cd \"$d\" && { :\n").Append(setup).Append("\n} >/dev/null 2>&1\n");
        sb.Append("echo '== stdout'\n");
        sb.Append("{ :\n").Append(command).Append("\n}").Append(stderr).Append('\n');
        sb.Append("echo \"== exit $?\"\n");
        sb.Append("echo '== fs'\n");
        sb.Append("__fs_snap\n");
        sb.Append("cd /; rm -rf \"$d\"");
        return sb.ToString();
    }

    /// <summary>
    /// Asserts ps-bash matches real bash for <paramref name="command"/> run against the tree
    /// <paramref name="setup"/> builds: stdout, exit status and resulting filesystem state.
    /// </summary>
    public static Task EqualAsync(
        string setup,
        string command,
        bool compareStderr = false,
        TimeSpan? timeout = null) =>
        AssertOracle.EqualAsync(
            Compose(setup, command, compareStderr),
            timeout: timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>
    /// Setup-script builder: one entry per path. <c>null</c> content = directory; otherwise the
    /// file holds the given string plus ONE trailing newline (<c>printf '%s\n'</c>). The newline
    /// is deliberate, not a convenience: ps-bash's <c>&gt; file</c> redirect appends a newline to
    /// <c>printf %s x &gt; f</c> (product bug, see the skipped
    /// <c>Redirect_PrintfWithoutNewline_*</c> case), so a fixture written without one would
    /// differ between the two shells before the command under test even ran. Parent directories
    /// are created.
    /// </summary>
    public static string Tree(params (string Path, string? Content)[] entries)
    {
        var parts = new List<string>();
        foreach (var (path, content) in entries)
        {
            var q = Quote(path);
            if (content is null)
            {
                parts.Add($"mkdir -p {q}");
                continue;
            }
            var slash = path.LastIndexOf('/');
            if (slash > 0) parts.Add($"mkdir -p {Quote(path[..slash])}");
            parts.Add($"printf '%s\\n' {Quote(content)} > {q}");
        }
        return string.Join("\n", parts);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
