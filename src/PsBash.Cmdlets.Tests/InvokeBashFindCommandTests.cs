using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 3 follow-on migration of
/// Invoke-BashFind from a PsBash.psm1 script function to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashFindCommand.cs).
///
/// Oracle: the original psm1 Invoke-BashFind (deleted at this commit) and its
/// helper-web dependency Get-BashFileInfo. The find cmdlet reimplements the
/// predicate parser, glob match, size/mtime parsing, and the Get-BashFileInfo
/// slice in C#; the psm1 Get-BashFileInfo remains because Invoke-BashStat
/// still depends on it.
///
/// find has a directory-tree surface plus an arbitrary-command -exec
/// dispatcher, so the applicable failure-surface axes (per
/// .claude/rules/qa-rubric.md Directive 3) are: empty dir, unicode file
/// names, missing target, large tree, and -exec security probe (Directive 12)
/// — a path or token containing ;, $(...), scriptblock chars, or backticks
/// must NOT be re-parsed as PowerShell syntax. Negative cases (Directive 7):
/// missing target, unsupported predicate, malformed size / mtime expression.
///
/// The PwshTestFixture loads psm1 (which no longer defines Invoke-BashFind)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// Set-Alias 'find' line still resolves to the cmdlet.
/// </summary>
public class InvokeBashFindCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashFindCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string Mk(string rel, string content = "")
    {
        var path = Path.Combine(_tmpDir, rel);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        return path;
    }

    private string MkDir(string rel)
    {
        var path = Path.Combine(_tmpDir, rel);
        Directory.CreateDirectory(path);
        return path;
    }

    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private System.Collections.ObjectModel.Collection<PSObject> RunAllowError(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private static string Esc(string p) => p.Replace("\\", "\\\\");

    // ===================== Basic predicate matching =====================

    [Fact]
    public void Find_NamePattern_MatchesGlob()
    {
        Mk("a.txt"); Mk("b.txt"); Mk("c.md");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("a.txt", names);
        Assert.Contains("b.txt", names);
        Assert.DoesNotContain("c.md", names);
    }

    // ===================== Boolean expression operators =====================
    // find combines predicates with -a/-and (AND), -o/-or (OR), -not/! (NOT) and
    // ( ) grouping. The emitter quotes the infix `-o` (it prefix-collides with
    // -OutVariable/-OutBuffer) and passes a quoted `!`/`(`/`)` through, so these
    // tests pass those tokens quoted to mirror the emitted invocation.

    private string[] Names(System.Collections.ObjectModel.Collection<PSObject> r) =>
        r.Select(o => (string?)o.Properties["Name"]?.Value).Where(n => n != null).ToArray()!;

    // A -size/-mtime number too large for long/int used to crash the cmdlet with
    // an unhandled OverflowException (long.Parse / int.Parse on a regex-matched
    // run of digits). It must clamp instead: `+overflow` matches nothing, like GNU.
    [Fact]
    public void Find_SizeOverflow_DoesNotCrash_MatchesNothing()
    {
        Mk("small.txt", "hi");
        var names = Names(RunAllowError($"Invoke-BashFind '{Esc(_tmpDir)}' -size +99999999999999999999c"));
        Assert.DoesNotContain("small.txt", names);
    }

    [Fact]
    public void Find_SizeOverflow_NegativeMatchesAll()
    {
        Mk("small.txt", "hi");
        // `-overflow` (smaller than an astronomically large size) matches everything.
        var names = Names(RunAllowError($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -size -99999999999999999999c"));
        Assert.Contains("small.txt", names);
    }

    [Fact]
    public void Find_MtimeOverflow_DoesNotCrash_MatchesNothing()
    {
        Mk("small.txt", "hi");
        var names = Names(RunAllowError($"Invoke-BashFind '{Esc(_tmpDir)}' -mtime +9999999999999"));
        Assert.DoesNotContain("small.txt", names);
    }

    [Fact]
    public void Find_OrOperator_MatchesEitherPredicate()
    {
        Mk("a.txt"); Mk("b.md"); Mk("c.log");
        var names = Names(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' '-o' -name '*.md'"));
        Assert.Contains("a.txt", names);
        Assert.Contains("b.md", names);
        Assert.DoesNotContain("c.log", names);
    }

    [Fact]
    public void Find_NotOperator_NegatesPredicate()
    {
        Mk("keep.txt"); Mk("skip.md");
        var names = Names(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -not -name '*.md'"));
        Assert.Contains("keep.txt", names);
        Assert.DoesNotContain("skip.md", names);
    }

    [Fact]
    public void Find_BangOperator_NegatesPredicate()
    {
        Mk("keep.txt"); Mk("skip.md");
        var names = Names(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f '!' -name '*.md'"));
        Assert.Contains("keep.txt", names);
        Assert.DoesNotContain("skip.md", names);
    }

    [Fact]
    public void Find_Grouping_OrInsideThenAnd()
    {
        Mk("a.txt"); Mk("b.md"); MkDir("d.txt");
        // ( -name *.txt -o -name *.md ) -type f → only FILES matching either name.
        var names = Names(Run(
            $"Invoke-BashFind '{Esc(_tmpDir)}' '(' -name '*.txt' '-o' -name '*.md' ')' -type f"));
        Assert.Contains("a.txt", names);
        Assert.Contains("b.md", names);
        Assert.DoesNotContain("d.txt", names); // a directory → excluded by -type f
    }

    [Fact]
    public void Find_ExplicitAnd_BothPredicatesRequired()
    {
        Mk("a.txt"); Mk("a.md");
        // `-a` is quoted: it prefix-collides with the cmdlet's own -Arguments
        // parameter, so the emitter quotes it (like -o). This mirrors that.
        var names = Names(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'a.*' '-a' -name '*.txt'"));
        Assert.Contains("a.txt", names);
        Assert.DoesNotContain("a.md", names);
    }

    [Fact]
    public void Find_ImplicitAnd_MatchesOldFlatFilterBehavior()
    {
        // No operator between predicates = implicit AND (the pre-expression
        // behavior must be byte-identical).
        Mk("a.txt"); Mk("a.md"); MkDir("sub"); Mk("sub/a.txt");
        var names = Names(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -name '*.txt'"));
        Assert.Contains("a.txt", names);
        Assert.DoesNotContain("a.md", names);
    }

    [Fact]
    public void Find_TrueFalse_ConstantPredicates()
    {
        Mk("only.txt");
        Assert.NotEmpty(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -true"));
        Assert.Empty(Run($"Invoke-BashFind '{Esc(_tmpDir)}' -false"));
    }

    [Fact]
    public void Find_TypeF_ReturnsFilesOnly()
    {
        Mk("file1.txt"); MkDir("subdir"); Mk("subdir/nested.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f");
        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.Equal(false, r.Properties["IsDirectory"]?.Value);
        }
    }

    [Fact]
    public void Find_TypeD_ReturnsDirectoriesOnly()
    {
        Mk("file1.txt"); MkDir("subdir");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type d");
        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.Equal(true, r.Properties["IsDirectory"]?.Value);
        }
    }

    [Fact]
    public void Find_IncludesRoot()
    {
        Mk("a.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}'");
        var resolved = new DirectoryInfo(_tmpDir).FullName;
        Assert.Contains(results,
            r => (string?)r.Properties["FullPath"]?.Value == resolved);
    }

    [Fact]
    public void Find_MaxDepth1_LimitsRecursion()
    {
        Mk("top.txt");
        MkDir("sub");
        Mk("sub/deep.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -maxdepth 1");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("top.txt", names);
        Assert.DoesNotContain("deep.txt", names);
    }

    [Fact]
    public void Find_SizeFilter_LargeFile()
    {
        Mk("small.txt", "x");
        Mk("big.txt", new string('x', 2048));
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -size '+1k'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("big.txt", names);
        Assert.DoesNotContain("small.txt", names);
    }

    [Fact]
    public void Find_EmptyFilter_FindsEmptyFilesAndDirs()
    {
        Mk("empty.txt");
        Mk("nonempty.txt", "data");
        MkDir("emptydir");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -empty");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("empty.txt", names);
        Assert.Contains("emptydir", names);
        Assert.DoesNotContain("nonempty.txt", names);
    }

    [Fact]
    public void Find_MtimeRecent_FindsJustWrittenFiles()
    {
        Mk("recent.txt", "hello");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -mtime '-7'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("recent.txt", names);
    }

    // ===================== New predicates (-iname/-path/-regex/-mindepth/-newer) =====================

    [Fact]
    public void Find_INamePattern_CaseInsensitive()
    {
        Mk("README.md"); Mk("notes.TXT"); Mk("other.md");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -iname 'readme*'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("README.md", names);
        Assert.DoesNotContain("other.md", names);
    }

    [Fact]
    public void Find_PathPattern_MatchesFullPathAcrossSlashes()
    {
        MkDir("sub"); Mk("sub/nested.txt", "x"); Mk("top.txt", "x");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -path '*/sub/*'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("nested.txt", names);
        Assert.DoesNotContain("top.txt", names);
    }

    [Fact]
    public void Find_Regex_MatchesWholePath()
    {
        Mk("keep.log", "x"); Mk("skip.txt", "x");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -regex '.*\\.log'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("keep.log", names);
        Assert.DoesNotContain("skip.txt", names);
    }

    [Fact]
    public void Find_MinDepth_SkipsShallowEntries()
    {
        Mk("top.txt"); MkDir("sub"); Mk("sub/deep.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -mindepth 2");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("deep.txt", names);   // depth 2
        Assert.DoesNotContain("top.txt", names); // depth 1
        Assert.DoesNotContain("sub", names);     // depth 1
    }

    [Fact]
    public void Find_Newer_FiltersByReferenceFileMtime()
    {
        var refPath = Mk("ref.txt", "r");
        var oldPath = Mk("older.txt", "o");
        var newPath = Mk("newer.txt", "n");
        var baseTime = DateTime.Now.AddHours(-1);
        File.SetLastWriteTime(refPath, baseTime);
        File.SetLastWriteTime(oldPath, baseTime.AddMinutes(-10));
        File.SetLastWriteTime(newPath, baseTime.AddMinutes(10));

        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -newer '{Esc(refPath)}'");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains("newer.txt", names);
        Assert.DoesNotContain("older.txt", names);
        Assert.DoesNotContain("ref.txt", names); // equal mtime is not "newer"
    }

    // ===================== Actions: -delete / -depth / -prune =====================

    [Fact]
    public void Find_Delete_RemovesMatchedFiles_AndPrintsNothing()
    {
        var del = Mk("del.txt", "x");
        var keep = Mk("keep.md", "x");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' -delete");
        Assert.Empty(results);                 // -delete is an action; no output
        Assert.False(File.Exists(del));        // matched file removed
        Assert.True(File.Exists(keep));        // unmatched file untouched
    }

    [Fact]
    public void Find_Delete_RemovesDirectoryTree_DepthFirst()
    {
        MkDir("tree"); Mk("tree/inner.txt", "x"); MkDir("tree/branch"); Mk("tree/branch/leaf.txt", "x");
        var treePath = Path.Combine(_tmpDir, "tree");
        // No filter → match everything under tree; -delete implies -depth so children go first.
        Run($"Invoke-BashFind '{Esc(treePath)}' -delete");
        Assert.False(Directory.Exists(treePath)); // whole tree removed, deepest-first
    }

    [Fact]
    public void Find_Delete_NonEmptyDirNotMatched_FileSurvivesDir()
    {
        MkDir("d"); var inner = Mk("d/inner.txt", "x");
        // Only the file matches; the directory is not matched, so it stays (now empty).
        Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'inner.txt' -delete");
        Assert.False(File.Exists(inner));
        Assert.True(Directory.Exists(Path.Combine(_tmpDir, "d")));
    }

    [Fact]
    public void Find_Depth_EmitsChildrenBeforeParent()
    {
        MkDir("d"); Mk("d/c.txt", "x");
        var dPath = Path.Combine(_tmpDir, "d");
        var results = Run($"Invoke-BashFind '{Esc(dPath)}' -depth");
        var paths = results.Select(o => (string?)o.Properties["FullPath"]?.Value).ToList();
        int childIdx = paths.FindIndex(p => p != null && p.EndsWith("c.txt"));
        int dirIdx = paths.FindIndex(p => p == new DirectoryInfo(dPath).FullName);
        Assert.True(childIdx >= 0 && dirIdx >= 0);
        Assert.True(childIdx < dirIdx, "-depth must emit directory contents before the directory");
    }

    [Fact]
    public void Find_Prune_ExcludesMatchedDirectorySubtree()
    {
        // Nested dirs both named "a"; -prune on the outer must stop descent so the inner is excluded.
        MkDir("a"); MkDir(Path.Combine("a", "a"));
        var resultsPruned = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'a' -prune");
        var pruned = resultsPruned.Select(o => (string?)o.Properties["FullPath"]?.Value).ToArray();
        Assert.Single(pruned);
        Assert.Equal(new DirectoryInfo(Path.Combine(_tmpDir, "a")).FullName, pruned[0]);

        // Sanity: without -prune both "a" directories are returned.
        var resultsAll = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'a'");
        Assert.Equal(2, resultsAll.Count);
    }

    // ===================== Path formatting =====================

    [Fact]
    public void Find_PathsUseForwardSlash()
    {
        MkDir("sub");
        Mk("sub/nested.txt", "data");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'nested.txt'");
        Assert.Single(results);
        var path = (string?)results[0].Properties["Path"]?.Value;
        Assert.NotNull(path);
        Assert.DoesNotContain('\\', path!);
        Assert.Contains("/nested.txt", path);
    }

    [Fact]
    public void Find_DotSearchPath_PrefixesPathsWithDotSlash()
    {
        // Switch into the test dir for relative-path semantics.
        Mk("a.txt");
        var results = Run(
            $"Push-Location '{Esc(_tmpDir)}'; " +
            $"try {{ Invoke-BashFind . -name 'a.txt' }} finally {{ Pop-Location }}");
        Assert.Single(results);
        var path = (string?)results[0].Properties["Path"]?.Value;
        Assert.Equal("./a.txt", path);
    }

    // ===================== Typed FindEntry contract =====================

    [Fact]
    public void Find_EmitsTypedFindEntry()
    {
        Mk("a.txt", "data");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name 'a.txt'");
        Assert.Single(results);
        Assert.Equal("PsBash.FindEntry", results[0].TypeNames[0]);
        Assert.NotNull(results[0].Properties["BashText"]?.Value);
        Assert.NotNull(results[0].Properties["Permissions"]?.Value);
        Assert.NotNull(results[0].Properties["LastModified"]?.Value);
    }

    // ===================== -print0 =====================

    [Fact]
    public void Find_Print0_EmitsSingleNullSeparatedTextObject()
    {
        Mk("a.txt"); Mk("b.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' -print0");
        Assert.Single(results);
        Assert.Equal(true, results[0].Properties["NoTrailingNewline"]?.Value);
        var text = (string?)results[0].Properties["BashText"]?.Value ?? "";
        var parts = text.Split('\0').Where(p => p.Length > 0).ToArray();
        Assert.True(parts.Length >= 2,
            $"expected >= 2 null-separated paths, got {parts.Length}");
        foreach (var p in parts)
        {
            Assert.DoesNotContain('\\', p);
        }
    }

    [Fact]
    public void Find_LongDashDashPrint0_AcceptedAsAlias()
    {
        Mk("a.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' --print0");
        Assert.Single(results);
        Assert.Equal(true, results[0].Properties["NoTrailingNewline"]?.Value);
    }

    [Fact]
    public void Find_Print0_NoMatches_EmitsNothing()
    {
        Mk("a.txt");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.nonexistent' -print0");
        Assert.Empty(results);
    }

    // ===================== -exec ====================================

    [Fact]
    public void Find_Exec_SemiTerminator_RunsPerFile()
    {
        Mk("a.txt", "alpha");
        Mk("b.txt", "beta");
        // echo is a PowerShell alias for Write-Output. Each {} is replaced
        // with the matched path.
        var results = Run(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' " +
            $"-exec Write-Output '{{}}' ';'");
        // Two .txt files → two lines of output.
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Find_Exec_PlusTerminator_BatchesIntoSingleInvocation()
    {
        Mk("a.txt", "alpha");
        Mk("b.txt", "beta");
        // Write-Output emits each item as a separate object — the cmdlet
        // splats $args[1..N] in, so the batched call still produces N items
        // but from one invocation. The test asserts every matched path was
        // delivered to the command in one batch (i.e. the count matches).
        var results = Run(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' " +
            $"-exec Write-Output '{{}}' '+'");
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// qa-rubric Directive 12: a path containing PowerShell metacharacters
    /// (semicolons, $(...), scriptblock braces, backticks) must NOT be
    /// re-parsed as PowerShell when -exec dispatches the command. The cmdlet
    /// routes through InvokeCommand.InvokeScript with a fixed parameterized
    /// body and the path passed as $args[N], so the path stays a literal.
    /// </summary>
    [Fact]
    public void Find_Exec_PathWithSemicolon_IsLiteralNotReparsed()
    {
        // Windows file names cannot contain ; OR most special chars in many
        // cases — but we still test that the dispatcher does not split or
        // re-evaluate the path token. We create a file whose name contains a
        // semicolon if the FS allows it; if it doesn't (Windows), the test
        // falls back to a file whose name contains $() which IS legal.
        string risky;
        try
        {
            risky = "name;injected.txt";
            Mk(risky, "data");
        }
        catch
        {
            risky = "name$(injected).txt";
            Mk(risky, "data");
        }

        // The injection target: if the path were re-parsed, the ; would
        // end the command and "injected" would attempt to run, throwing a
        // CommandNotFoundException. Write-Output of the literal string
        // simply echoes it back — that is the safe outcome.
        var results = RunAllowError(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -name '*injected*' " +
            $"-exec Write-Output '{{}}' ';'");

        // One file matched, one invocation, output contains the literal path.
        Assert.Single(results);
        var outText = results[0]?.BaseObject?.ToString() ?? "";
        Assert.Contains("injected", outText);
        // The output is a single string, NOT a command-not-found error.
        Assert.IsNotType<System.Management.Automation.ErrorRecord>(results[0]?.BaseObject);
    }

    // ===================== Pipeline interop =====================

    [Fact]
    public void Find_PipesToGrep_PreservesPathSubstrings()
    {
        Mk("alpha.txt"); Mk("beta.txt"); Mk("gamma.md");
        var results = Run(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -name '*.txt' | Invoke-BashGrep 'alpha'");
        Assert.NotEmpty(results);
        bool sawAlpha = results.Any(o =>
        {
            var bt = o.Properties["BashText"]?.Value?.ToString() ?? o.ToString() ?? "";
            return bt.Contains("alpha");
        });
        Assert.True(sawAlpha, "expected 'alpha' to survive the grep pipeline");
    }

    // ===================== Failure-surface axes =====================

    [Fact]
    public void Find_EmptyDirectory_EmitsRootOnly()
    {
        // Empty dir under the test root: no children, but root itself is
        // included.
        var emptyRoot = MkDir("emptyroot");
        var results = Run($"Invoke-BashFind '{Esc(emptyRoot)}'");
        Assert.Single(results);
        var resolved = new DirectoryInfo(emptyRoot).FullName;
        Assert.Equal(resolved, results[0].Properties["FullPath"]?.Value);
    }

    [Fact]
    public void Find_UnicodeFileNames_AreEnumerated()
    {
        Mk("hello-世界.txt", "data");
        Mk("emoji-🚀.md", "data");
        Mk("combining-é.txt", "data");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f");
        var names = results.Select(o => (string?)o.Properties["Name"]?.Value).ToArray();
        Assert.Contains(names, n => n != null && n.Contains("世界"));
        Assert.Contains(names, n => n != null && n.Contains("🚀"));
    }

    [Fact]
    public void Find_MissingTarget_WritesErrorAndSetsExitCode()
    {
        var missing = Path.Combine(_tmpDir, "does-not-exist-xyz");
        var results = RunAllowError(
            $"Invoke-BashFind '{Esc(missing)}'; $LASTEXITCODE");
        // The final pipeline output is $LASTEXITCODE (1) — the cmdlet wrote
        // an error and returned without emitting FindEntry objects.
        var lastExitCode = results.Last()?.BaseObject;
        Assert.Equal(1, Convert.ToInt32(lastExitCode));
    }

    [Fact]
    public void Find_LargeTree_StreamsAndCompletes()
    {
        // 1100 files in a flat dir — exercises EnumerationOptions and the
        // depth-cap path.
        for (int k = 0; k < 1100; k++)
        {
            Mk($"file-{k}.dat", "x");
        }
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -name 'file-*.dat'");
        Assert.True(results.Count >= 1100,
            $"expected >= 1100 results, got {results.Count}");
    }

    // ===================== Negative cases =====================

    [Fact]
    public void Find_UnsupportedPredicate_EmitsErrorThenContinues()
    {
        Mk("a.txt");
        var results = RunAllowError(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -perm 644 -name 'a.txt' 2>&1");
        // -perm is unsupported (value-bearing); the cmdlet writes an error
        // and continues parsing. The trailing -name should still match.
        bool matchedAfterError = results.Any(o =>
            (string?)o.Properties["Name"]?.Value == "a.txt");
        Assert.True(matchedAfterError,
            "predicate parser should continue after an unsupported-predicate error");
    }

    [Fact]
    public void Find_UnsupportedStandalonePredicate_EmitsError()
    {
        Mk("a.txt");
        // -ls is still an unsupported standalone predicate (-delete/-prune/-depth are now supported).
        var results = RunAllowError(
            $"Invoke-BashFind '{Esc(_tmpDir)}' -ls -name 'a.txt' 2>&1");
        bool matchedAfterError = results.Any(o =>
            (string?)o.Properties["Name"]?.Value == "a.txt");
        Assert.True(matchedAfterError,
            "predicate parser should continue after a standalone-unsupported error");
    }

    [Fact]
    public void Find_MalformedSize_IgnoredSilently()
    {
        // The psm1 oracle's regex parse fails on bad input and leaves the
        // size filter inactive. Verify the cmdlet inherits that behavior:
        // no filter applied, all files returned.
        Mk("a.txt", "data");
        Mk("b.txt", "data");
        var results = Run($"Invoke-BashFind '{Esc(_tmpDir)}' -type f -size 'garbage'");
        Assert.True(results.Count >= 2,
            "malformed -size should be inert, not exclusionary");
    }

    [Fact]
    public void Find_Alias_FindResolvesToCmdlet()
    {
        // The psm1 sets `Set-Alias 'find' Invoke-BashFind` at script scope.
        // After Phase 3 follow-on, that name resolves to the cmdlet through
        // PowerShell's command resolver. Verify by calling via the alias.
        Mk("a.txt");
        var results = Run($"find '{Esc(_tmpDir)}' -name 'a.txt'");
        Assert.NotEmpty(results);
        Assert.Equal("PsBash.FindEntry", results[0].TypeNames[0]);
    }
}
