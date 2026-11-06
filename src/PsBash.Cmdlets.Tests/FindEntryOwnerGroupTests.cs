using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The default `find` entry exposes Owner/Group, which on Linux/macOS need a /usr/bin/stat process per entry (24 ms each
/// on macOS: `find .` over 701 files took 17 s). They are computed on first read, so a listing forks nothing, and a
/// reader still gets the real names. Hand-written: the typed PsBash.FindEntry surface has no bash analogue.
/// </summary>
public class FindEntryOwnerGroupTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public FindEntryOwnerGroupTests(SharedPwshFixture fixture) { _fixture = fixture; }

    [SkippableFact]
    public void FindEntry_OwnerAndGroup_ResolveWhenRead_OnUnix()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows takes owner/group from the environment, not stat");
        string root = Path.Combine(Path.GetTempPath(), "psb_findowner_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "f"), "x");
        try
        {
            var pwsh = _fixture.AcquireFresh();
            var results = pwsh.AddScript($"Invoke-BashFind '{root}' | ForEach-Object {{ $_.Owner + '/' + $_.Group }}").Invoke();
            pwsh.Commands.Clear();

            Assert.Equal(2, results.Count);   // the directory and the file
            foreach (var r in results)
            {
                var parts = (r?.ToString() ?? "").Split('/');
                Assert.Equal(2, parts.Length);
                Assert.NotEmpty(parts[0]);
                Assert.NotEmpty(parts[1]);
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
