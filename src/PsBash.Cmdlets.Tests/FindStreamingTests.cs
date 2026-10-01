using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>find</c> walks lazily and acts per match as it goes: a directory is listed only when the walk reaches it
/// (so output is observed before the rest of the tree is touched and <c>| head -1</c> stops the walk), a matched
/// directory under <c>-prune</c> is not descended, and <c>-depth</c> / <c>-delete</c> yield a directory after its
/// contents (true post-order).
/// </summary>
public class FindStreamingTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public FindStreamingTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var task = Task.Run(() => pwsh.AddScript(script).Invoke());
        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "find did not terminate");
        pwsh.Commands.Clear();
        return task.Result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private static string NewTree(Action<string> build)
    {
        string root = Path.Combine(Path.GetTempPath(), "psb_findstream_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        build(root);
        return root;
    }

    [Fact]
    public void Walk_ListsADirectoryOnlyWhenItReachesIt()
    {
        // The consumer deletes sub/f3 while handling the FIRST item (the root). A collect-everything find
        // had already listed it; a lazy walk lists `sub` after the consumer ran, so f3 never appears.
        string root = NewTree(r =>
        {
            Directory.CreateDirectory(Path.Combine(r, "sub"));
            foreach (var f in new[] { "f1", "f2", "f3" }) File.WriteAllText(Path.Combine(r, "sub", f), "x");
        });
        try
        {
            string rootArg = root.Replace('\\', '/');
            var r = Run(
                $"$root = '{rootArg}'; $first = $true; " +
                "$o = @(Invoke-BashFind $root | ForEach-Object { if ($first) { $first = $false; Remove-Item -LiteralPath (Join-Path $root 'sub/f3') }; $_.BashText }); " +
                "($o | ForEach-Object { $_.Substring($root.Length) } | Sort-Object) -join ','");
            Assert.Equal(new[] { ",/sub,/sub/f1,/sub/f2" }, r);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Head_StopsTheWalk_AfterTheFirstMatch()
    {
        string root = NewTree(r =>
        {
            for (int d = 0; d < 20; d++)
            {
                string dir = Path.Combine(r, "d" + d);
                Directory.CreateDirectory(dir);
                for (int f = 0; f < 20; f++) File.WriteAllText(Path.Combine(dir, "f" + f), "x");
            }
        });
        try
        {
            var r = Run($"@(Invoke-BashFind '{root.Replace('\\', '/')}' -type f | Invoke-BashHead -n 1).Count");
            Assert.Equal(new[] { "1" }, r);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Depth_YieldsADirectoryAfterItsContents()
    {
        string root = NewTree(r => { Directory.CreateDirectory(Path.Combine(r, "a", "b")); File.WriteAllText(Path.Combine(r, "a", "b", "c"), "x"); });
        try
        {
            var r = Run($"$root = '{root.Replace('\\', '/')}'; (Invoke-BashFind $root -depth | ForEach-Object {{ $_.BashText.Substring($root.Length) }}) -join ','");
            Assert.Equal(new[] { "/a/b/c,/a/b,/a," }, r);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Prune_MatchedDirectoryIsListedButNotDescended_InWalkOrder()
    {
        string root = NewTree(r =>
        {
            Directory.CreateDirectory(Path.Combine(r, "keep"));
            Directory.CreateDirectory(Path.Combine(r, "skip"));
            File.WriteAllText(Path.Combine(r, "keep", "k"), "x");
            File.WriteAllText(Path.Combine(r, "skip", "s"), "x");
        });
        try
        {
            var r = Run($"$root = '{root.Replace('\\', '/')}'; (Invoke-BashFind $root -name skip -prune | ForEach-Object {{ $_.BashText.Substring($root.Length) }}) -join ','");
            Assert.Equal(new[] { "/skip" }, r);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Print0_EmitsTheNulJoinedByteStream_OneRecordPerPath()
    {
        string root = NewTree(r => { File.WriteAllText(Path.Combine(r, "a"), "x"); File.WriteAllText(Path.Combine(r, "b"), "x"); });
        try
        {
            var r = Run(
                $"$root = '{root.Replace('\\', '/')}'; $o = @(Invoke-BashFind $root -type f -print0); " +
                "$o.Count.ToString() + ' ' + (($o | ForEach-Object { $_.BashText.Substring($root.Length).Replace([string][char]0, '|') } | Sort-Object) -join '')");
            Assert.Equal(new[] { "2 /a|/b|" }, r);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Delete_RemovesContentsBeforeTheirDirectory()
    {
        string root = NewTree(r => { Directory.CreateDirectory(Path.Combine(r, "d", "e")); File.WriteAllText(Path.Combine(r, "d", "e", "f"), "x"); });
        try
        {
            var r = Run($"Invoke-BashFind '{root.Replace('\\', '/')}/d' -delete; $LASTEXITCODE");
            Assert.Equal(new[] { "0" }, r);
            Assert.False(Directory.Exists(Path.Combine(root, "d")));
            Assert.True(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
