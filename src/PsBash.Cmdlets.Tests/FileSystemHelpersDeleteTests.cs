using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end tests for the destructive recovery walk behind <c>rm -rf</c>
/// (<c>FileSystemHelpers.DeleteDirectoryForce</c>). The headline case: when the
/// read-only recovery path runs, it must NOT follow a directory symlink/junction —
/// recursing would enumerate and delete the link TARGET's contents (a destructive
/// escape out of the tree being removed), and a cycle would overflow the stack.
/// </summary>
public class FileSystemHelpersDeleteTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _root;
    private readonly SharedPwshFixture _fixture;

    public FileSystemHelpersDeleteTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), "psb-fsdel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { ClearTree(_root); Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ClearTree(string dir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            foreach (var d in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                ClearTree(d);
            }
        }
        catch { }
    }

    private void Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
    }

    private static string Q(string p) => "'" + p.Replace("'", "''") + "'";

    [SkippableFact]
    public void RmRf_DoesNotFollowDirectorySymlink_NoDestructiveEscape()
    {
        // An OUTSIDE directory whose contents must survive `rm -rf` of `target`.
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "precious");

        // The tree we remove. A read-only file forces the slow recovery walk on
        // Windows (the fast Directory.Delete throws on the read-only descendant);
        // a symlink inside it points at `outside`.
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var ro = Path.Combine(target, "ro.txt");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var link = Path.Combine(target, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) { Skip.If(true, $"cannot create directory symlink (needs privilege): {ex.Message}"); }

        Run($"Invoke-BashRm -r -f {Q(target)}");

        Assert.False(Directory.Exists(target), "target should be removed");
        Assert.True(File.Exists(sentinel), "the symlink TARGET's contents must NOT be deleted");
        Assert.True(Directory.Exists(outside), "the outside directory must survive");
    }

    [Fact]
    public void RmRf_RemovesReadOnlyDescendants()
    {
        var target = Path.Combine(_root, "ro-tree");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        var ro = Path.Combine(target, "sub", "locked.txt");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        Run($"Invoke-BashRm -r -f {Q(target)}");

        Assert.False(Directory.Exists(target));
    }
}
