using System.Management.Automation;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Smoke tests for the Strata integration cmdlet <c>Format-Styled</c>. Verifies the
/// end-to-end pipeline (PSObject -&gt; Strata adapter -&gt; CSS cascade -&gt; Spectre projection)
/// runs in-process and that selectors/classes drive the output.
/// </summary>
public class FormatStyledCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public FormatStyledCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    // Spectre emits ANSI SGR escapes; strip them to assert on plain content.
    private static string StripAnsi(string s) => Regex.Replace(s, "\\[[0-9;]*m", string.Empty);

    // ── Filesystem view (Get-ChildItem → curated, classified, human-formatted table) ──────────

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(9, "9B")]
    [InlineData(1023, "1023B")]
    [InlineData(1024, "1.0K")]
    [InlineData(1536, "1.5K")]
    [InlineData(250000, "244K")]
    [InlineData(10485760, "10M")]
    [InlineData(2147483648, "2.0G")]
    public void HumanSize_FormatsBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, FormatStyledCommand.HumanSize(bytes));
    }

    [Fact]
    public void SizeBar_IsLogScaledFixedWidthMeter()
    {
        // Empty / no-max → all-empty (dim baseline) meter; the max-size file fills it; width is fixed.
        Assert.Equal("▁▁▁▁▁▁", FormatStyledCommand.SizeBar(0, 1000));
        Assert.Equal("▁▁▁▁▁▁", FormatStyledCommand.SizeBar(100, 0));
        // 1000B is in the B range (exp 0 → ▃); fully filled against an equal max.
        Assert.Equal("▃▃▃▃▃▃", FormatStyledCommand.SizeBar(1000, 1000));
        var mid = FormatStyledCommand.SizeBar(100, 1_000_000);
        Assert.Equal(6, mid.Length);
        Assert.Contains("▃", mid);   // non-empty B-range file shows at least some fill
        Assert.Contains("▁", mid);   // but well below max, so not full (dim baseline shows)
    }

    [Fact]
    public void SizeBar_FilledGlyphThicknessTracksByteRange()
    {
        // Filled glyph weight rises with the size's binary-unit exponent (B < K < M < G < T < P),
        // so the range is legible from the mark alone even at the same fill fraction.
        Assert.Equal(0, FormatStyledCommand.SizeExponent(512));            // B
        Assert.Equal(1, FormatStyledCommand.SizeExponent(4L * 1024));      // K
        Assert.Equal(2, FormatStyledCommand.SizeExponent(4L * 1024 * 1024)); // M
        Assert.Equal(3, FormatStyledCommand.SizeExponent(4L * 1024 * 1024 * 1024)); // G

        // Same fraction (fully filled vs equal max), heavier glyph as the range climbs.
        Assert.Equal("▃▃▃▃▃▃", FormatStyledCommand.SizeBar(512, 512));                       // B → ▃
        Assert.Equal("▄▄▄▄▄▄", FormatStyledCommand.SizeBar(4L * 1024, 4L * 1024));            // K → ▄
        Assert.Equal("▅▅▅▅▅▅", FormatStyledCommand.SizeBar(4L * 1024 * 1024, 4L * 1024 * 1024)); // M → ▅
        Assert.Equal("▆▆▆▆▆▆", FormatStyledCommand.SizeBar(4L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024)); // G → ▆
    }

    // Terminal-oriented ASCII type tags replaced the old type emoji. Pure classifier — no temp files.
    [Theory]
    [InlineData("cs", "code", "src")]
    [InlineData("ps1", "script", "scr")]
    [InlineData("png", "image", "img")]
    [InlineData("mp4", "video", "vid")]
    [InlineData("mp3", "audio", "aud")]
    [InlineData("zip", "archive", "arc")]
    [InlineData("exe", "app", "exe")]
    [InlineData("json", "data", "dat")]
    [InlineData("pdf", "doc", "doc")]
    [InlineData("unknownext", "text", "txt")]
    public void ClassifyFsByExt_MapsExtensionToBucketAndTag(string ext, string expectedClass, string expectedTag)
    {
        var (tag, cls) = FormatStyledCommand.ClassifyFsByExt(isDir: false, isLink: false, ext);
        Assert.Equal(expectedClass, cls);
        Assert.Equal(expectedTag, tag);
    }

    [Fact]
    public void ClassifyFsByExt_DirectoryAndSymlink()
    {
        Assert.Equal(("dir", "dir"), FormatStyledCommand.ClassifyFsByExt(isDir: true, isLink: false, "anything"));
        // A symlink wins over extension classification.
        Assert.Equal(("lnk", "symlink"), FormatStyledCommand.ClassifyFsByExt(isDir: false, isLink: true, "cs"));
    }

    [Fact]
    public void ClassifyFs_FileSystemInfoOverload_DelegatesToPrimitive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-classify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(("dir", "dir"), FormatStyledCommand.ClassifyFs(new DirectoryInfo(dir)));
            var path = Path.Combine(dir, "a.cs");
            File.WriteAllText(path, "x");
            Assert.Equal(("src", "code"), FormatStyledCommand.ClassifyFs(new FileInfo(path)));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5m ago")]
    [InlineData(3 * 3600, "3h ago")]
    [InlineData(2 * 86400, "2d ago")]
    public void HumanTime_RecentTimestampsAreRelative(int secondsAgo, string expected)
    {
        Assert.Equal(expected, FormatStyledCommand.HumanTime(DateTime.Now.AddSeconds(-secondsAgo)));
    }

    // ── Curated per-type row projectors (pure — no runspace, no filesystem) ────────────────────

    private static PSObject Typed(string typeName, System.Collections.IDictionary props)
    {
        var o = new PSObject();
        o.TypeNames.Insert(0, typeName);
        foreach (System.Collections.DictionaryEntry e in props)
        {
            o.Properties.Add(new PSNoteProperty((string)e.Key, e.Value));
        }
        return o;
    }

    private static string Prop(PSObject o, string name) => o.Properties[name]?.Value?.ToString() ?? string.Empty;

    [Fact]
    public void BuildFsRow_File_TagNameSizeMeterAndClass()
    {
        var row = FormatStyledCommand.BuildFsRow("app.cs", isDir: false, isLink: false,
            sizeBytes: 4400, modified: DateTime.Now.AddHours(-3), hidden: false, max: 4400);
        Assert.Equal("src  app.cs", Prop(row, "Name"));
        Assert.Contains("4.3K", Prop(row, "Size"));
        Assert.Contains("▄", Prop(row, "Size"));        // meter present (K range → ▄ fill)
        Assert.Equal("3h ago", Prop(row, "Modified"));
        Assert.Equal("code", Prop(row, "class"));
    }

    [Fact]
    public void BuildFsRow_Directory_DashSizeTrailingSlashAndDirClass()
    {
        var row = FormatStyledCommand.BuildFsRow("sub", isDir: true, isLink: false,
            sizeBytes: 0, modified: DateTime.Now.AddDays(-2), hidden: false, max: 4400);
        Assert.Equal("dir  sub/", Prop(row, "Name"));
        Assert.Contains("—", Prop(row, "Size"));        // no size/meter for a dir
        Assert.DoesNotContain("▁", Prop(row, "Size"));  // no meter track at all
        Assert.Equal("dir", Prop(row, "class"));
    }

    [Fact]
    public void BuildFsRow_Hidden_AppendsHiddenClassLast()
    {
        var row = FormatStyledCommand.BuildFsRow(".secret.cs", isDir: false, isLink: false,
            sizeBytes: 10, modified: DateTime.Now, hidden: true, max: 10);
        Assert.Equal("code hidden", Prop(row, "class"));   // type first, hidden last so it wins the cascade
    }

    [Fact]
    public void ProjectFsLike_LsEntry_MapsTypedPropertiesIntoFsRow()
    {
        var ls = Typed("PsBash.LsEntry", new System.Collections.Hashtable
        {
            { "Name", "logo.png" }, { "IsDirectory", false }, { "IsSymlink", false },
            { "SizeBytes", 250000L }, { "LastModified", DateTime.Now.AddHours(-3) },
        });
        var row = FormatStyledCommand.ProjectFsLike(ls, max: 250000);
        Assert.Equal("img  logo.png", Prop(row, "Name"));
        Assert.Contains("244K", Prop(row, "Size"));
        Assert.Equal("image", Prop(row, "class"));
    }

    [Fact]
    public void ProjectFindEntry_KeysOnDisplayPath()
    {
        var find = Typed("PsBash.FindEntry", new System.Collections.Hashtable
        {
            { "Path", "./src/app.cs" }, { "Name", "app.cs" }, { "IsDirectory", false },
            { "SizeBytes", 100L }, { "LastModified", DateTime.Now },
        });
        var row = FormatStyledCommand.ProjectFindEntry(find, max: 100);
        Assert.Equal("src  ./src/app.cs", Prop(row, "Name"));   // full match path, classified by extension
        Assert.Equal("code", Prop(row, "class"));
    }

    [Fact]
    public void ProjectDuEntry_SizeAndPath_TotalRowGetsClass()
    {
        var du = Typed("PsBash.DuEntry", new System.Collections.Hashtable
        {
            { "SizeHuman", "12K" }, { "Path", "./src" }, { "IsTotal", false },
        });
        var row = FormatStyledCommand.ProjectDuEntry(du);
        Assert.Contains("12K", Prop(row, "Size"));
        Assert.Equal("./src", Prop(row, "Path"));
        Assert.Equal("", Prop(row, "class"));

        var total = Typed("PsBash.DuEntry", new System.Collections.Hashtable
        {
            { "SizeHuman", "40K" }, { "Path", "total" }, { "IsTotal", true },
        });
        Assert.Equal("total", Prop(FormatStyledCommand.ProjectDuEntry(total), "class"));
    }

    [Theory]
    [InlineData("0.0", "")]
    [InlineData("12.5", "")]
    [InlineData("87.3", "busy")]
    public void ProjectPsEntry_BusyClassWhenCpuOverFifty(string cpu, string expectedClass)
    {
        var ps = Typed("PsBash.PsEntry", new System.Collections.Hashtable
        {
            { "PID", "1234" }, { "User", "me" }, { "CPU", cpu }, { "MemoryMB", "42" }, { "Command", "chrome" },
        });
        var row = FormatStyledCommand.ProjectPsEntry(ps);
        Assert.Equal("1234", Prop(row, "PID"));
        Assert.Equal("chrome", Prop(row, "Command"));
        Assert.Equal(expectedClass, Prop(row, "class"));
    }

    [Fact]
    public void FilesystemView_GciFsSheet_ParsesToCuratedColumnsWithTagSizeAndTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-fsview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "subdir"));
            File.WriteAllText(Path.Combine(dir, "app.cs"), new string('x', 4400));
            File.WriteAllBytes(Path.Combine(dir, "photo.png"), new byte[250000]);
            File.WriteAllText(Path.Combine(dir, "notes.md"), "hello");

            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript($"Get-ChildItem -LiteralPath '{dir}' | Format-Styled fs").Invoke();

            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            Assert.Single(result);
            var raw = result[0].ToString() ?? string.Empty;
            var plain = StripAnsi(raw);

            // Curated columns only — NOT the raw FileInfo property dump.
            Assert.Contains("Name", plain);
            Assert.Contains("Modified", plain);
            Assert.DoesNotContain("Attributes", plain);
            Assert.DoesNotContain("LastWriteTimeUtc", plain);
            // Terminal-oriented ASCII type tags per kind (no emoji).
            Assert.Contains("dir  subdir/", plain);
            Assert.Contains("src  app.cs", plain);   // code
            Assert.Contains("img  photo.png", plain);
            Assert.DoesNotContain("📁", plain);       // emoji removed
            Assert.DoesNotContain("📘", plain);
            // Human size + visual meter.
            Assert.Contains("K", plain);    // 244K / 4.3K
            Assert.Contains("▄", plain);    // size meter (K-range fill glyph)
            // Colour applied (ANSI SGR present in the raw output).
            Assert.Matches("\\[[0-9;]*m", raw);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void FilesystemView_ExplicitProperty_OptsOutToRawColumns()
    {
        // -Property is the escape hatch: the user drives columns, so the curated view does NOT apply
        // (no type tag injected) — the named property renders as-is.
        var dir = Path.Combine(Path.GetTempPath(), "psbash-fsview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.cs"), "x");

            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript($"Get-ChildItem -LiteralPath '{dir}' | Format-Styled fs -Property Name").Invoke();

            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            Assert.Single(result);
            var plain = StripAnsi(result[0].ToString() ?? string.Empty);
            Assert.Contains("app.cs", plain);
            Assert.DoesNotContain("src  app.cs", plain);   // no curated tag when -Property drives columns
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── Curated views for ps-bash typed output (end-to-end through Format-Styled) ───────────────

    // Build a typed ps-bash object in the session (PSTypeName drives the Strata kind).
    private const string MakeLsEntries = """
        function New-Ls($name,$dir,$size,$hoursAgo) {
          $o = [pscustomobject]@{ Name=$name; FullPath='x'; IsDirectory=[bool]$dir; IsSymlink=$false;
            SizeBytes=[long]$size; Permissions='-rw-r--r--'; LinkCount=1; Owner='me'; Group='me';
            LastModified=(Get-Date).AddHours(-$hoursAgo); BashText='' }
          $o.PSObject.TypeNames.Insert(0,'PsBash.LsEntry'); $o }
        """;

    [Fact]
    public void CuratedView_BashLsEntries_RenderFsColumnsNotEveryProperty()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(MakeLsEntries +
            "\n@(New-Ls 'app.cs' $false 4400 3; New-Ls 'sub' $true 4096 48) | Format-Styled").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        // Curated fs columns — the "huge grid of every column" the user hit is gone.
        Assert.Contains("Name", plain);
        Assert.Contains("Modified", plain);
        Assert.Contains("src  app.cs", plain);
        Assert.Contains("dir  sub/", plain);
        Assert.DoesNotContain("Permissions", plain);   // raw LsEntry property NOT dumped
        Assert.DoesNotContain("Owner", plain);
        Assert.DoesNotContain("BashText", plain);
    }

    [Fact]
    public void CuratedView_TextLineType_CollapsesToPlainLines()
    {
        // A BashText text-line type (grep/cat/…) must render its lines, not a one-column property grid.
        var pwsh = _fixture.AcquireFresh();
        var script = """
            function New-Grep($t) { $o=[pscustomobject]@{ BashText=$t }; $o.PSObject.TypeNames.Insert(0,'PsBash.GrepMatch'); $o }
            @(New-Grep 'file.txt:1:hello'; New-Grep 'file.txt:5:world') | Format-Styled
            """;
        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        // One string per line, emitted verbatim (no header row, no "BashText" column label).
        var lines = result.Select(r => (r?.ToString() ?? string.Empty)).ToArray();
        Assert.Contains("file.txt:1:hello", lines);
        Assert.Contains("file.txt:5:world", lines);
        Assert.DoesNotContain(lines, l => l.Contains("BashText"));
    }

    [Fact]
    public void CuratedView_DuEntries_RenderSizeAndPathColumns()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            function New-Du($h,$p,$total) { $o=[pscustomobject]@{ Size=[long]1; SizeBytes=[long]1; SizeHuman=$h; Path=$p; Depth=0; IsTotal=[bool]$total; BashText="$h`t$p" }; $o.PSObject.TypeNames.Insert(0,'PsBash.DuEntry'); $o }
            @(New-Du '12K' './src' $false; New-Du '40K' 'total' $true) | Format-Styled
            """;
        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        Assert.Contains("Size", plain);
        Assert.Contains("Path", plain);
        Assert.Contains("12K", plain);
        Assert.Contains("./src", plain);
        Assert.DoesNotContain("SizeBytes", plain);   // internal property not dumped
        Assert.DoesNotContain("Depth", plain);
    }

    [Fact]
    public void CuratedView_NamedSheetOtherThanTypes_KeepsUserSheet()
    {
        // Naming a sheet that is not the type's own opts out of the curated projection (user intent wins).
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(MakeLsEntries +
            "\n@(New-Ls 'app.cs' $false 4400 3) | Format-Styled -Css 'FileInfo { color: red }' -Property Name").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        Assert.Contains("app.cs", plain);
        Assert.DoesNotContain("src  app.cs", plain);   // -Property drove columns → no curated tag
    }

    [Fact]
    public void DefaultDisplayPropertySet_CuratesColumnsWhenNoPropertyGiven()
    {
        // A typed object that declares DefaultDisplayPropertySet (as the psgit Git* objects do)
        // renders ONLY those columns — not every property — even without -Property.
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $o = [pscustomobject]@{ Keep1='a'; Keep2='b'; Hidden='SECRET'; class='' }
            $o.PSObject.TypeNames.Insert(0,'Demo')
            $o | Add-Member -MemberType MemberSet PSStandardMembers (
                [System.Management.Automation.PSMemberInfo[]]@(
                  New-Object System.Management.Automation.PSPropertySet 'DefaultDisplayPropertySet', ([string[]]@('Keep1','Keep2'))))
            $o | Format-Styled -Table
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        Assert.Contains("Keep1", plain);
        Assert.Contains("Keep2", plain);
        Assert.DoesNotContain("Hidden", plain);   // not in the default set
        Assert.DoesNotContain("SECRET", plain);
    }

    [Fact]
    public void StylesRows_ByPropertyAndKind()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = @(
              [pscustomobject]@{ PSTypeName='Proc'; Name='chrome'; class='high-cpu' },
              [pscustomobject]@{ PSTypeName='Proc'; Name='vim';    class='' }
            )
            $css = 'Proc { color: grey } .high-cpu { color: red; font-weight: bold }'
            $rows | Format-Styled -Css $css -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        Assert.Contains("chrome", plain);
        Assert.Contains("vim", plain);
    }

    [Fact]
    public void EmitsAnsi_ForStyledRow()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Proc'; Name='chrome'; class='high-cpu' })
            $css = '.high-cpu { color: red; font-weight: bold }'
            $rows | Format-Styled -Css $css -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        // A red+bold style must produce at least one ANSI SGR escape sequence.
        Assert.Matches("\\[[0-9;]*m", raw);
    }

    [Fact]
    public void NoInput_ProducesNoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("@() | Format-Styled 'Proc { color: red }'").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Empty(result);
    }

    [Fact]
    public void Default_AppliesWhenNoStylesheetArgument()
    {
        var pwsh = _fixture.AcquireFresh();
        // No stylesheet arg -> the built-in `default` sheet, which colors a Process kind.
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Process'; Name='chrome' })
            $rows | Format-Styled -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        Assert.Contains("chrome", StripAnsi(raw));
        Assert.Matches("\\[[0-9;]*m", raw); // the default sheet styled the row
    }

    [Fact]
    public void NamedBuiltin_ResolvesViaStyleAlias()
    {
        var pwsh = _fixture.AcquireFresh();
        // -Style ps loads the embedded ps.pcss; a 'busy' process picks up its .busy rule.
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Process'; Name='chrome'; class='busy' })
            $rows | Format-Styled -Style ps -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
    }

    [Theory]
    [InlineData("fs")]
    [InlineData("procsvc")]
    [InlineData("object")]
    [InlineData("error")]
    public void InteractiveBuiltinSheet_ParsesAndRenders(string style)
    {
        // The button/expansion sheets declare the `command:` interaction property and the
        // :focused / :expanded pseudo-classes. Parsing them through the static grid path proves
        // the InteractionProperties descriptor is registered (else the parser throws
        // "Unknown property 'command'") and that the sheet is a valid cascade input. The bindings
        // are inert here (no input loop) — we only assert the render path stays clean and styled.
        var pwsh = _fixture.AcquireFresh();
        var script = $$"""
            $rows = @(
              [pscustomobject]@{ PSTypeName='Process'; Name='chrome'; class='busy' },
              [pscustomobject]@{ PSTypeName='Process'; Name='vim';    class='idle' }
            )
            $rows | Format-Styled -Style {{style}} -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        Assert.Contains("chrome", StripAnsi(raw));
        Assert.Matches("\\[[0-9;]*m", raw);
    }

    [Fact]
    public void UserOverride_CascadesOverBuiltinDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        try
        {
            // A user `default.pcss` adds a `.zzz` rule the built-in default lacks. The row's
            // only possible style source is that .zzz rule, so it renders with ANSI ONLY if
            // the user override cascaded in (it would be plain text otherwise).
            File.WriteAllText(Path.Combine(dir, "default.pcss"), ".zzz { color: green; font-weight: bold }");
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", dir);

            var pwsh = _fixture.AcquireFresh();
            var script = """
                $rows = @([pscustomobject]@{ PSTypeName='Plain'; Name='x'; class='zzz' })
                $rows | Format-Styled -Property Name
                """;

            var result = pwsh.AddScript(script).Invoke();

            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            Assert.Single(result);
            Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", prior);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UserOverride_LegacyCssExtension_StillLoadsAsFallback()
    {
        // Back-compat: a user override authored as `<name>.css` (pre-`.pcss`-rename) still cascades.
        var dir = Path.Combine(Path.GetTempPath(), "psbash-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        try
        {
            File.WriteAllText(Path.Combine(dir, "default.css"), ".zzz { color: green; font-weight: bold }");
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", dir);

            var pwsh = _fixture.AcquireFresh();
            var script = """
                $rows = @([pscustomobject]@{ PSTypeName='Plain'; Name='x'; class='zzz' })
                $rows | Format-Styled -Property Name
                """;

            var result = pwsh.AddScript(script).Invoke();

            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            Assert.Single(result);
            Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", prior);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // A bold SGR is the parameter `1` bounded by `[`/`;` on the left and `;`/`m` on the right,
    // so it is not mistaken for the `1` inside a colour code like `31` (red) or `36` (cyan).
    private const string BoldSgr = @"\x1b\[(?:[0-9;]*;)?1(?:;[0-9;]*)?m";
    private const string UnderlineSgr = @"\x1b\[(?:[0-9;]*;)?4(?:;[0-9;]*)?m";

    [Fact]
    public void List_RendersPropertyNamesBoldInTwoColumnGrid()
    {
        var pwsh = _fixture.AcquireFresh();
        // -List with no stylesheet -> built-in `list` sheet: bold property names, plain values.
        var script = """
            $o = [pscustomobject]@{ Name='nginx'; Status='Running' }
            $o | Format-Styled -List
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        var plain = StripAnsi(raw);
        // Both the property names and their values are present (name/value grid cells).
        Assert.Contains("Name", plain);
        Assert.Contains("nginx", plain);
        Assert.Contains("Status", plain);
        Assert.Contains("Running", plain);
        // The built-in list sheet renders property names bold.
        Assert.Matches(BoldSgr, raw);
    }

    [Fact]
    public void Table_RendersBoldUnderlinedHeaderRow()
    {
        var pwsh = _fixture.AcquireFresh();
        // -Table with no stylesheet -> built-in `table` sheet: bold+underlined header row.
        var script = """
            $rows = @(
              [pscustomobject]@{ Name='nginx'; Id=42 },
              [pscustomobject]@{ Name='redis'; Id=99 }
            )
            $rows | Format-Styled -Table -Property Name,Id
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        var plain = StripAnsi(raw);
        // Header (the property names) plus every data row are present.
        Assert.Contains("Name", plain);
        Assert.Contains("Id", plain);
        Assert.Contains("nginx", plain);
        Assert.Contains("redis", plain);
        // The built-in table sheet renders the header row bold and underlined.
        Assert.Matches(BoldSgr, raw);
        Assert.Matches(UnderlineSgr, raw);
    }

    [Fact]
    public void Auto_MultipleObjects_RenderAsTable_HeaderAppearsOnce()
    {
        var pwsh = _fixture.AcquireFresh();
        // No -List/-Table: multiple objects auto-select TABLE. A property name then appears once
        // (the header row) rather than once per object (which is how the LIST layout repeats keys).
        var script = """
            $rows = @(
              [pscustomobject]@{ Alpha='a1'; Bravo='b1' },
              [pscustomobject]@{ Alpha='a2'; Bravo='b2' }
            )
            $rows | Format-Styled -Property Alpha,Bravo
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        var lines = plain.Split('\n');
        // Header row only — exactly one line mentions the property name "Alpha".
        Assert.Equal(1, lines.Count(l => l.Contains("Alpha")));
        Assert.Contains("a1", plain);
        Assert.Contains("a2", plain);
    }

    [Fact]
    public void Auto_SingleObject_RendersAsList_KeyAndValueShareLine()
    {
        var pwsh = _fixture.AcquireFresh();
        // No -List/-Table: a single object auto-selects LIST, so each property's key and value
        // sit on the same line (the TABLE layout would put the key in a header row above the value).
        var script = """
            $o = [pscustomobject]@{ Alpha='aval'; Bravo='bval' }
            $o | Format-Styled -Property Alpha,Bravo
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var lines = StripAnsi(result[0].ToString() ?? string.Empty).Split('\n');
        Assert.Contains(lines, l => l.Contains("Alpha") && l.Contains("aval"));
        Assert.Contains(lines, l => l.Contains("Bravo") && l.Contains("bval"));
    }

    [Fact]
    public void UnknownStylesheetName_SurfacesError()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "@([pscustomobject]@{ Name='x' }) | Format-Styled -Style does-not-exist-12345 -Property Name").Invoke();

        Assert.True(pwsh.HadErrors);
        Assert.Empty(result);
    }
}
