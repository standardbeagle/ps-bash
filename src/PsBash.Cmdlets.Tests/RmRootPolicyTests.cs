using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// rm <c>--preserve-root[=all]</c>, <c>--no-preserve-root</c>, <c>--one-file-system</c>. Wording, exit
/// statuses and option resolution were checked against GNU coreutils 9.4 (<c>wsl bash</c>); the real
/// root is never removed by these tests (the refusal is the behaviour under test, and ps-bash's
/// protected-path guard backs it up).
/// </summary>
public class RmRootPolicyTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public RmRootPolicyTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-rmr-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void File1(string rel)
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x\n");
    }

    private bool Exists(string rel) => File.Exists(Path.Combine(_tmp, rel)) || Directory.Exists(Path.Combine(_tmp, rel));

    // ───────────── option resolution (pure) ─────────────

    [Theory]
    [InlineData("root")]
    [InlineData("root", "--preserve-root")]
    [InlineData("all", "--preserve-root=all")]
    [InlineData("none", "--no-preserve-root")]
    [InlineData("root", "--no-preserve-root", "--preserve-root")]   // last wins
    [InlineData("none", "--preserve-root=all", "--no-preserve-root")]
    [InlineData("all", "--no-preserve-root", "--preserve-root=all")]
    [InlineData("root", "--preserve-root=all", "--preserve-root")]
    [InlineData("root", "--preserve")]                              // unique prefix of --preserve-root
    public void Resolve_LastOptionWins(string expected, params string[] argv)
    {
        var p = InvokeBashRmCommand.ScanArgs(argv.Append("a").ToArray());
        Assert.True(RmRootPolicyResolver.TryResolve(p, out var policy, out var error), error);
        Assert.Equal(expected, policy.ToString().ToLowerInvariant());
    }

    [Theory]
    [InlineData("rm: you may not abbreviate the --no-preserve-root option", "--no")]
    [InlineData("rm: you may not abbreviate the --no-preserve-root option", "--no-p")]
    [InlineData("rm: you may not abbreviate the --no-preserve-root option", "--no-preserve")]
    [InlineData("rm: unrecognized --preserve-root argument: 'x'", "--preserve-root=x")]
    [InlineData("rm: unrecognized --preserve-root argument: 'al'", "--preserve-root=al")]
    [InlineData("rm: unrecognized --preserve-root argument: ''", "--preserve-root=")]
    public void Resolve_ErrorsMatchGnu(string expected, string arg)
    {
        var p = InvokeBashRmCommand.ScanArgs(new[] { arg, "a" });
        Assert.Null(p.Error);
        Assert.False(RmRootPolicyResolver.TryResolve(p, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("/", "rm: it is dangerous to operate recursively on '/'\nrm: use --no-preserve-root to override this failsafe")]
    [InlineData("//", "rm: it is dangerous to operate recursively on '//' (same as '/')\nrm: use --no-preserve-root to override this failsafe")]
    [InlineData("///", "rm: it is dangerous to operate recursively on '/'\nrm: use --no-preserve-root to override this failsafe")]
    [InlineData("C:\\", "rm: it is dangerous to operate recursively on 'C:\\' (same as '/')\nrm: use --no-preserve-root to override this failsafe")]
    public void DangerousMessage_MatchesGnu(string display, string expected)
    {
        Assert.Equal(expected, RmRootPolicyResolver.DangerousMessage(display));
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("..", true)]
    [InlineData("d/.", true)]
    [InlineData("d/..", true)]
    [InlineData("d/./", true)]
    [InlineData("/.", true)]
    [InlineData("a", false)]
    [InlineData(".hidden", false)]
    [InlineData("..x", false)]
    [InlineData("d/.x", false)]
    public void IsDotOrDotDot_LooksAtTheLastComponent(string typed, bool expected)
    {
        Assert.Equal(expected, RmRootPolicyResolver.IsDotOrDotDot(typed));
    }

    // ───────────── cmdlet behaviour ─────────────

    [Fact]
    public void RecursiveOnRoot_IsRefusedWithGnuWording()
    {
        // The root must survive whatever this asserts: the refusal is the behaviour under test.
        InTmp("Invoke-BashRm -r /").AssertFailed(1,
            "rm: it is dangerous to operate recursively on '/'", "rm: use --no-preserve-root to override this failsafe");
    }

    [Fact]
    public void RecursiveForceOnRoot_IsRefused()
    {
        InTmp("Invoke-BashRm -rf /").AssertFailed(1, "dangerous to operate recursively on '/'");
    }

    [Fact]
    public void ExplicitPreserveRoot_StillRefusesTheRoot()
    {
        InTmp("Invoke-BashRm '-r' '--preserve-root' /").AssertFailed(1, "dangerous to operate recursively on '/'");
        InTmp("Invoke-BashRm '-r' '--preserve-root=all' /").AssertFailed(1, "dangerous to operate recursively on '/'");
    }

    [Fact]
    public void NoPreserveRoot_RemovesAnOrdinaryTreeAsBefore()
    {
        File1("d/e/f");
        InTmp("Invoke-BashRm '-r' '--no-preserve-root' d").AssertSuccess();
        Assert.False(Exists("d"));
    }

    [Fact]
    public void NoPreserveRoot_DoesNotLiftPsBashsOwnProtectedPathGuard()
    {
        // Deliberate difference from GNU (which would delete /): the protected-path guard stays.
        InTmp("Invoke-BashRm '-r' '--no-preserve-root' /").AssertFailed(1, "protected path");
    }

    [Fact]
    public void AbbreviatedNoPreserveRoot_IsRefused()
    {
        File1("d/f");
        InTmp("Invoke-BashRm '-r' '--no-pre' d").AssertFailed(1, "rm: you may not abbreviate the --no-preserve-root option");
        Assert.True(Exists("d/f"));
    }

    [Fact]
    public void BadPreserveRootArgument_IsRefusedAndRemovesNothing()
    {
        File1("d/f");
        InTmp("Invoke-BashRm '-r' '--preserve-root=bogus' d").AssertFailed(1, "rm: unrecognized --preserve-root argument: 'bogus'");
        Assert.True(Exists("d/f"));
    }

    [Fact]
    public void DotAndDotDot_AreNeverRemoved()
    {
        File1("d/f");
        InTmp("Set-Location d; Invoke-BashRm '-rf' .").AssertFailed(1, "rm: refusing to remove '.' or '..' directory: skipping '.'");
        InTmp("Set-Location d; Invoke-BashRm '-r' ..").AssertFailed(1, "rm: refusing to remove '.' or '..' directory: skipping '..'");
        InTmp("Invoke-BashRm '-r' d/.").AssertFailed(1, "skipping 'd/.'");
        Assert.True(Exists("d/f"));
    }

    [Fact]
    public void OneFileSystem_OnASingleDevice_RemovesEverything()
    {
        File1("d/a/f");
        File1("d/b/g");
        InTmp("Invoke-BashRm '-r' '--one-file-system' d").AssertSuccess();
        Assert.False(Exists("d"));
    }

    [Fact]
    public void OneFileSystem_WithoutRecursion_IsANoOp()
    {
        File1("f");
        InTmp("Invoke-BashRm '--one-file-system' f").AssertSuccess();
        Assert.False(Exists("f"));
    }

    // ───────────── --one-file-system skip logic (device ids injected) ─────────────

    [Fact]
    public void Remover_OneFileSystem_SkipsADirectoryOnAnotherDevice_AndKeepsItsParents()
    {
        File1("d/a/f");
        File1("d/mnt/inner/g");
        File1("d/z");
        var errors = new List<string>();
        var root = Path.Combine(_tmp, "d");
        var mnt = Path.Combine(_tmp, "d", "mnt");

        // The mount point and everything below it report device "2".
        string? DeviceOf(string p) => p.StartsWith(mnt, StringComparison.OrdinalIgnoreCase) ? "2" : "1";
        var remover = new RmRemover(recursive: true, dirOnly: false, promptEach: false, verbose: false,
            confirm: _ => true, say: _ => { }, error: errors.Add, oneFileSystem: true, deviceOf: DeviceOf);

        Assert.False(remover.Remove(root, "d"));
        Assert.Equal(new[] { "rm: skipping 'd/mnt', since it's on a different device" }, errors);
        Assert.False(Exists("d/a"));
        Assert.False(Exists("d/z"));
        Assert.True(Exists("d/mnt/inner/g"));   // the other device's tree is untouched
        Assert.True(Exists("d"));               // and so is the path leading to it
    }

    [Fact]
    public void Remover_WithoutOneFileSystem_IgnoresDeviceIds()
    {
        File1("d/mnt/g");
        var root = Path.Combine(_tmp, "d");
        string? DeviceOf(string p) => p.Contains("mnt") ? "2" : "1";
        var remover = new RmRemover(true, false, false, false, _ => true, _ => { }, _ => { },
            oneFileSystem: false, deviceOf: DeviceOf);
        Assert.True(remover.Remove(root, "d"));
        Assert.False(Exists("d"));
    }

    [Fact]
    public void Remover_UnknownDevice_NeverSkips()
    {
        File1("d/mnt/g");
        var remover = new RmRemover(true, false, false, false, _ => true, _ => { }, _ => { },
            oneFileSystem: true, deviceOf: _ => null);
        Assert.True(remover.Remove(Path.Combine(_tmp, "d"), "d"));
        Assert.False(Exists("d"));
    }

    [Fact]
    public void DeviceMessage_MatchesGnu()
    {
        Assert.Equal("rm: skipping '/dev/shm', since it's on a different device\nrm: and --preserve-root=all is in effect",
            RmRootPolicyResolver.DifferentDeviceMessage("/dev/shm"));
    }
}
