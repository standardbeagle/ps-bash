using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// bash's hash table (<c>hash</c>), the store behind it. Oracle (bash 5.2): <c>ls; ls; whoami; hash</c> lists
/// <c>2 /usr/bin/ls</c> and <c>1 /usr/bin/whoami</c>; <c>hash NAME</c> adds it with 0 hits; <c>hash -t</c> is a lookup
/// (one more hit); assigning a new PATH empties the table.
/// </summary>
public class CommandHashTableTests
{
    private const string Path1 = "/usr/bin:/bin";

    [Fact]
    public void RecordUse_CountsHitsPerCommand_InFirstSeenOrder()
    {
        var t = CommandHashTable.For(new object());
        t.RecordUse("ls", "/usr/bin/ls", Path1);
        t.RecordUse("whoami", "/usr/bin/whoami", Path1);
        t.RecordUse("ls", "/usr/bin/ls", Path1);
        Assert.Equal(
            [new("ls", "/usr/bin/ls", 2), new("whoami", "/usr/bin/whoami", 1)],
            t.Snapshot(Path1));
    }

    [Fact]
    public void AddLookupRemoveClear_FollowHashBuiltin()
    {
        var t = CommandHashTable.For(new object());
        t.Add("cat", "/usr/bin/cat", Path1);
        Assert.Equal([new("cat", "/usr/bin/cat", 0)], t.Snapshot(Path1));
        Assert.Equal("/usr/bin/cat", t.Lookup("cat", Path1));
        Assert.Equal(1, t.Snapshot(Path1)[0].Hits);
        Assert.Null(t.Lookup("nosuch", Path1));
        Assert.False(t.Remove("nosuch", Path1));
        Assert.True(t.Remove("cat", Path1));
        Assert.Empty(t.Snapshot(Path1));
        t.Add("x", "/x", Path1);
        t.Clear(Path1);
        Assert.Empty(t.Snapshot(Path1));
    }

    [Fact]
    public void ChangedPath_EmptiesTheTable()
    {
        var t = CommandHashTable.For(new object());
        t.RecordUse("ls", "/usr/bin/ls", Path1);
        Assert.Empty(t.Snapshot("/opt/bin:" + Path1));
        Assert.Null(t.Lookup("ls", "/opt/bin:" + Path1));
    }

    // A ps-bash command asking what a name resolves to (Enter-BashNativeStdin before every program, type, command -v)
    // fires the same lookup hook as running it; inside a Probe the host records nothing, so `x; x` counts 2, not 4.
    [Fact]
    public void Probe_MarksLookupsAsQuestions_UntilDisposed_AndNests()
    {
        Assert.False(CommandHashTable.InProbe);
        using (CommandHashTable.Probe())
        {
            Assert.True(CommandHashTable.InProbe);
            using (CommandHashTable.Probe()) Assert.True(CommandHashTable.InProbe);
            Assert.True(CommandHashTable.InProbe);
        }
        Assert.False(CommandHashTable.InProbe);
    }

    [Fact]
    public void EachShellHasItsOwnTable()
    {
        object a = new(), b = new();
        CommandHashTable.For(a).RecordUse("ls", "/usr/bin/ls", Path1);
        Assert.Single(CommandHashTable.For(a).Snapshot(Path1));
        Assert.Empty(CommandHashTable.For(b).Snapshot(Path1));
    }
}
