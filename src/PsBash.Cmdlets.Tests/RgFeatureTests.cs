using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// rg batch 8: types, colour, -m/-q/-H/-I, --replace, --passthru, --column/--vimgrep/-b, -0, --files, --sort,
/// --max-depth, -L, --json, --stats, -z, -a, -E, -P, -U, -f, --trim, --iglob and friends. Every expectation was
/// read from ripgrep 14.1.0 (`wsl rg`) on the fixture below (the fixture is byte-identical to the oracle's /tmp/rgx).
/// Output is normalised to forward slashes; walked-directory runs use `--sort path` (ripgrep's unsorted order is
/// thread-dependent). Runs with the native passthrough OFF (PSBASH_RG_NATIVE unset).
/// </summary>
[Collection("PsBashSearchEnv")]
public class RgFeatureTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private const string Esc = "\u001b";
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public RgFeatureTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-rgf-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        Write("a.txt", "foo bar\nbaz foo foo\nqux\nlast foo");           // no final newline
        Write("m.rs", "fn main() { foo(); }\n");
        Write("p.py", "def foo():\n  pass\n");
        Write("sub/s.txt", "foo in sub\n");
        Write("sub/deep/d.txt", "foo deep\n");
        Write("ml.txt", "a\nb\nc\n");
        Write("fb.txt", "x\nfoo\nbar\ny\nfoo\nbar\n");
        Write("u.txt", "\u00e9 foo\n");
        Write("t.txt", "   indented foo\n");
        Write("pats", "qux\nbaz\n");
        File.WriteAllBytes(Path.Combine(_dir, "lat.txt"), new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x66, 0x6F, 0x6F, 0x0A });
        File.WriteAllBytes(Path.Combine(_dir, "bin.dat"), new byte[] { 0x66, 0x6F, 0x6F, 0x00, 0x62, 0x61, 0x72, 0x0A });
        File.WriteAllBytes(Path.Combine(_dir, "u16.txt"), Encoding.Unicode.GetBytes("foo x\n"));
        using (var fs = File.Create(Path.Combine(_dir, "z.gz")))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
            gz.Write(Encoding.UTF8.GetBytes("zipped foo\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
    }

    /// <summary>Run a script with the fixture directory as the location (relative operands resolve there).</summary>
    private CmdResult Run(string body) =>
        CmdResult.Run(_fixture.AcquireFresh(),
            $"Push-Location -LiteralPath '{_dir.Replace("'", "''")}'; try {{ {body} }} finally {{ Pop-Location }}");

    private static string[] N(CmdResult r) => r.Lines.Select(l => l.Replace('\\', '/')).ToArray();

    private static string[] Fwd(params string[] lines) => lines;

    // ------------------------------------------------------------------ types

    [Fact]
    public void Types_SelectNegateAndOverride()
    {
        Assert.Equal(new[] { "./p.py:def foo():" }, N(Run("Invoke-BashRg '-t' py foo .").AssertSuccess()));
        Assert.Equal(new[] { "./p.py:def foo():" }, N(Run("Invoke-BashRg '-tpython' foo .").AssertSuccess()));
        var notPy = N(Run("Invoke-BashRg '-T' py '--sort' path '-l' foo .").AssertSuccess());
        Assert.DoesNotContain("./p.py", notPy);
        Assert.Contains("./m.rs", notPy);
        Assert.Equal(new[] { "./m.rs", "./p.py" }, N(Run("Invoke-BashRg '-tpy' '-trust' '--sort' path '-l' foo .").AssertSuccess()));
        // -g overrides the type selection (ripgrep: `-t rust -g '*.py'` lists p.py)
        Assert.Equal(new[] { "./p.py" }, N(Run("Invoke-BashRg '-t' rust '-g' '*.py' '-l' foo .").AssertSuccess()));
        // files named on the command line bypass -t / -T
        Assert.Equal(new[] { "m.rs:fn main() { foo(); }", "a.txt:foo bar", "a.txt:baz foo foo", "a.txt:last foo", "p.py:def foo():" },
            N(Run("Invoke-BashRg '-t' py foo m.rs a.txt p.py").AssertSuccess()));
    }

    [Fact]
    public void Types_TypeListIsRipgreps_AndTypeAddClearWork()
    {
        var list = N(Run("Invoke-BashRg '--type-list'").AssertSuccess());
        Assert.Equal(203, list.Length);
        Assert.Contains("rust: *.rs", list);
        Assert.Contains("py: *.py, *.pyi", list);
        Assert.Contains("c: *.[chH], *.[chH].in, *.cats", list);
        Assert.Equal(list.OrderBy(x => x, StringComparer.Ordinal).ToArray(), list);
        Assert.Equal(new[] { "./m.rs" }, N(Run("Invoke-BashRg '--type-add' 'foo:*.rs' '-tfoo' '--files' .").AssertSuccess()));
        Assert.Equal(new[] { "./m.rs" }, N(Run("Invoke-BashRg '--type-add' 'web:include:rust,py' '-tweb' '--files' '--sort' path . | Select-Object -First 1").AssertSuccess()));
        Run("Invoke-BashRg '--type-clear' rust '-t' rust '--files' .").AssertFailed(2, "unrecognized file type: rust");
        Run("Invoke-BashRg '-t' nosuch foo .").AssertFailed(2, "unrecognized file type: nosuch");
        Assert.Contains("foo: *.rs", N(Run("Invoke-BashRg '--type-add' 'foo:*.rs' '--type-list'").AssertSuccess()));
    }

    // ------------------------------------------------------------------ -m -q -H -I

    [Fact]
    public void MaxCount_IsPerFile_AndKeepsTrailingContext()
    {
        Assert.Equal(new[] { "foo bar" }, N(Run("Invoke-BashRg '-m1' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "1" }, N(Run("Invoke-BashRg '-m1' '-c' foo a.txt").AssertSuccess()));
        // ripgrep prints the trailing -A window after the last allowed match, whatever it contains
        Assert.Equal(new[] { "foo bar", "baz foo foo" }, N(Run("Invoke-BashRg '-m1' '-A1' foo a.txt").AssertSuccess()));
        var multi = N(Run("Invoke-BashRg '-m1' '--sort' path foo a.txt m.rs sub").AssertSuccess());
        Assert.Equal(new[] { "a.txt:foo bar", "m.rs:fn main() { foo(); }", "sub/s.txt:foo in sub", "sub/deep/d.txt:foo deep" }.Length, multi.Length);
        Assert.Contains("a.txt:foo bar", multi);
        Assert.Contains("sub/deep/d.txt:foo deep", multi);
        Run("Invoke-BashRg '-m0' foo a.txt").AssertFailed(1);
    }

    [Fact]
    public void Quiet_PrintsNothing_ExitsLikeAMatch()
    {
        var r = Run("Invoke-BashRg '-q' foo a.txt").AssertSuccess();
        Assert.Empty(r.Lines);
        Run("Invoke-BashRg '-q' nomatch a.txt").AssertFailed(1);
        Assert.Empty(Run("Invoke-BashRg '-q' '-c' foo a.txt").AssertSuccess().Lines);
        Run("Invoke-BashRg '-q' foo a.txt m.rs").AssertSuccess();
    }

    [Fact]
    public void WithAndNoFilename()
    {
        Assert.Equal(new[] { "a.txt:foo bar", "a.txt:baz foo foo", "a.txt:last foo" }, N(Run("Invoke-BashRg '-H' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo bar", "baz foo foo", "last foo", "fn main() { foo(); }" },
            N(Run("Invoke-BashRg '-I' foo a.txt m.rs").AssertSuccess()));
        Assert.Equal(new[] { "<stdin>:foo" }, N(Run("'foo' | Invoke-BashRg '-H' foo").AssertSuccess()));
        Assert.Equal(new[] { "foo" }, N(Run("'foo' | Invoke-BashRg '--no-filename' foo").AssertSuccess()));
        Assert.Equal(new[] { "a.txt:1:foo bar" }, N(Run("Invoke-BashRg '-H' '-n' '-m1' foo a.txt").AssertSuccess()));
    }

    // ------------------------------------------------------------------ replace / passthru

    [Fact]
    public void Replace_ExpandsGroupsLikeRust()
    {
        // `$0Y` is the (unknown) group "0Y": empty — ripgrep 14.1 prints "X bar"
        Assert.Equal(new[] { "X bar", "baz X X", "last X" }, N(Run("Invoke-BashRg '-r' 'X$0Y' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "[f] bar", "baz [f] [f]", "last [f]" }, N(Run("Invoke-BashRg '-r' '[$1]' '(f)oo' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo b" }, N(Run("Invoke-BashRg '-N' '-r' '${n}' '(?P<n>b)ar' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "$ bar", "baz $ $", "last $" }, N(Run("Invoke-BashRg '-r' '$$' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "fx bar", "baz fx fx", "last fx" }, N(Run("Invoke-BashRg '-r' '${1}x' '(f)oo' a.txt").AssertSuccess()));
        Assert.Equal(new[] { " bar", "baz  ", "last " }, N(Run("Invoke-BashRg '-r' '$1x' '(f)oo' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "<foo>", "<foo>", "<foo>", "<foo>" }, N(Run("Invoke-BashRg '-o' '-r' '<$0>' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "X", "last foo" }, N(Run("Invoke-BashRg '-r' X '-A1' qux a.txt").AssertSuccess()));   // context lines are not rewritten
        Assert.Equal(new[] { "qux" }, N(Run("Invoke-BashRg '-r' X '-v' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "3" }, N(Run("Invoke-BashRg '-r' X '-c' foo a.txt").AssertSuccess()));
    }

    [Fact]
    public void Passthru_PrintsEveryLine_MatchesInOrContextStyle()
    {
        Assert.Equal(new[] { "foo bar", "baz foo foo", "qux", "last foo" }, N(Run("Invoke-BashRg '--passthru' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "1-foo bar", "2-baz foo foo", "3:qux", "4-last foo" }, N(Run("Invoke-BashRg '--passthru' '-n' qux a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo", "foo", "foo", "qux", "foo" }, N(Run("Invoke-BashRg '--passthru' '-o' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo bar", "baz foo foo", "qux", "last foo" }, N(Run("Invoke-BashRg '--passthru' '-v' foo a.txt").AssertSuccess()));
    }

    // ------------------------------------------------------------------ -b --column --vimgrep -0

    [Fact]
    public void ByteOffset_Column_Vimgrep()
    {
        Assert.Equal(new[] { "0:foo bar", "8:baz foo foo", "24:last foo" }, N(Run("Invoke-BashRg '-b' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "0:foo", "12:foo", "16:foo", "29:foo" }, N(Run("Invoke-BashRg '-b' '-o' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "24:last" }, N(Run("Invoke-BashRg '-o' '-b' last a.txt").AssertSuccess()));
        Assert.Equal(new[] { "1:0:foo bar", "2:8:baz foo foo", "4:24:last foo" }, N(Run("Invoke-BashRg '-b' '-n' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "1:1:foo bar", "2:5:baz foo foo", "4:6:last foo" }, N(Run("Invoke-BashRg '--column' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "1:1:foo", "2:5:foo", "2:9:foo", "4:6:foo" }, N(Run("Invoke-BashRg '--column' '-o' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "a.txt:1:1:0:foo bar", "a.txt:2:5:8:baz foo foo", "a.txt:4:6:24:last foo" },
            N(Run("Invoke-BashRg '-H' '-n' '--column' '-b' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "a.txt:1:1:foo bar", "a.txt:2:5:baz foo foo", "a.txt:2:9:baz foo foo", "a.txt:4:6:last foo" },
            N(Run("Invoke-BashRg '--vimgrep' foo a.txt").AssertSuccess()));
        Assert.Equal(new[] { "a.txt:1:foo bar", "a.txt:5:baz foo foo", "a.txt:9:baz foo foo", "a.txt:6:last foo" },
            N(Run("Invoke-BashRg '--vimgrep' '-N' foo a.txt").AssertSuccess()));
        // columns and offsets count UTF-8 bytes: "é foo" is column 4
        Assert.Equal(new[] { "1:4:\u00e9 foo" }, N(Run("Invoke-BashRg '--column' foo u.txt").AssertSuccess()));
        Assert.Equal(new[] { "0:\u00e9 foo" }, N(Run("Invoke-BashRg '-b' foo u.txt").AssertSuccess()));
    }

    [Fact]
    public void Null_ReplacesThePathSeparator()
    {
        // -l -0: the NUL ends the path and no newline follows it (the records are exact-bytes records)
        Assert.Equal(new[] { "a.txt\0", "m.rs\0" }, N(Run("Invoke-BashRg '-0' '-l' foo a.txt m.rs").AssertSuccess()));
        var c = Run("Invoke-BashRg '-0' '-c' foo a.txt m.rs").AssertSuccess().Lines.Select(x => x.Replace('\\', '/')).ToArray();
        Assert.Equal(new[] { "a.txt\03", "m.rs\01" }, c);
        var m = Run("Invoke-BashRg '-0' foo a.txt m.rs").AssertSuccess().Lines.Select(x => x.Replace('\\', '/')).ToArray();
        Assert.Contains("m.rs\0fn main() { foo(); }", m);
        Assert.Contains("a.txt\0foo bar", m);
        var f = Run("Invoke-BashRg '--files' '-0' '-g' '*.rs' .").AssertSuccess().Stdout;
        Assert.Contains("m.rs\0", f);
    }

    // ------------------------------------------------------------------ --files, sort, depth, follow

    [Fact]
    public void Files_ListsWalkedFiles_WithFilters()
    {
        var all = N(Run("Invoke-BashRg '--files' '--sort' path .").AssertSuccess());
        Assert.Equal(new[] { "./a.txt", "./bin.dat", "./fb.txt", "./lat.txt", "./m.rs", "./ml.txt", "./p.py", "./pats", "./sub/deep/d.txt", "./sub/s.txt", "./t.txt", "./u.txt", "./u16.txt", "./z.gz" }, all);
        Assert.Equal(new[] { "./m.rs" }, N(Run("Invoke-BashRg '--files' '-g' '*.rs' .").AssertSuccess()));
        Assert.Equal(new[] { "./m.rs" }, N(Run("Invoke-BashRg '--files' '-t' rust .").AssertSuccess()));
        Assert.Equal(new[] { "./m.rs" }, N(Run("Invoke-BashRg '--iglob' '*.RS' '-l' foo .").AssertSuccess()));
        // implicit root: bare relative names, like the oracle's `rg --files`
        Assert.Contains("sub/deep/d.txt", N(Run("Invoke-BashRg '--files'").AssertSuccess()));
    }

    [Fact]
    public void Sort_PathAndModified_AndReverse()
    {
        var fwd = N(Run("Invoke-BashRg '--sort' path '-l' foo .").AssertSuccess());
        Assert.Equal(new[] { "./a.txt", "./fb.txt", "./lat.txt", "./m.rs", "./p.py", "./sub/deep/d.txt", "./sub/s.txt", "./t.txt", "./u.txt" }.Length, fwd.Length);
        Assert.Equal(fwd.OrderBy(x => x, StringComparer.Ordinal).ToArray(), fwd);
        var rev = N(Run("Invoke-BashRg '--sortr' path '-l' foo .").AssertSuccess());
        Assert.Equal(fwd.Reverse().ToArray(), rev);
        // modified: oldest first; make two files with known times
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "m.rs"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "p.py"), new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var mod = N(Run("Invoke-BashRg '--sort' modified '-l' foo .").AssertSuccess());
        Assert.Equal(new[] { "./m.rs", "./p.py" }, mod.Take(2).ToArray());
        var modR = N(Run("Invoke-BashRg '--sortr' modified '-l' foo .").AssertSuccess());
        Assert.Equal("./p.py", modR[^2]);
        Assert.Equal("./m.rs", modR[^1]);
        Run("Invoke-BashRg '--sort' bogus foo .").AssertFailed(2, "choice 'bogus' is unrecognized");
    }

    [Fact]
    public void MaxDepth_CountsDirectoriesBelowTheRoot()
    {
        var d1 = N(Run("Invoke-BashRg '--max-depth' 1 '--sort' path '-l' foo .").AssertSuccess());
        Assert.DoesNotContain("./sub/s.txt", d1);
        Assert.Contains("./a.txt", d1);
        Run("Invoke-BashRg '--max-depth' 0 '-l' foo .").AssertFailed(1);
        var d2 = N(Run("Invoke-BashRg '--max-depth' 2 '--sort' path '-l' foo .").AssertSuccess());
        Assert.Contains("./sub/s.txt", d2);
        Assert.DoesNotContain("./sub/deep/d.txt", d2);
        Assert.Contains("./sub/deep/d.txt", N(Run("Invoke-BashRg '-d' 3 '-l' foo .").AssertSuccess()));
        // explicit files are never depth-limited
        Run("Invoke-BashRg '--max-depth' 0 foo a.txt").AssertSuccess();
    }

    [Fact]
    public void Follow_DescendsIntoLinkedDirectories()
    {
        var link = Path.Combine(_dir, "link");
        var linkTarget = Path.Combine(_dir, "sub");
        try
        {
            if (OperatingSystem.IsWindows())
                Run($"New-Item -ItemType Junction -Path '{link}' -Target '{linkTarget}' | Out-Null");
            else
                Directory.CreateSymbolicLink(link, linkTarget);
        }
        catch { return; }   // links not creatable here: nothing to assert
        if (!Directory.Exists(link)) return;
        var plain = N(Run("Invoke-BashRg '--sort' path '-l' foo .").AssertSuccess());
        Assert.DoesNotContain(plain, l => l.StartsWith("./link/"));
        var followed = N(Run("Invoke-BashRg '-L' '--sort' path '-l' foo .").AssertSuccess());
        Assert.Contains("./link/s.txt", followed);
        Assert.Contains("./link/deep/d.txt", followed);
        Assert.Contains("./sub/s.txt", followed);
    }

    // ------------------------------------------------------------------ colour

    [Fact]
    public void Color_Always_UsesRipgrepsDefaultSequences()
    {
        string M(string t) => $"{Esc}[0m{Esc}[1m{Esc}[31m{t}{Esc}[0m";
        Assert.Equal(new[] { M("foo") + " bar", "baz " + M("foo") + " " + M("foo"), "last " + M("foo") },
            Run("Invoke-BashRg '--color' always foo a.txt").AssertSuccess().Lines.ToArray());
        string Ln(int n) => $"{Esc}[0m{Esc}[32m{n}{Esc}[0m";
        Assert.Equal(new[] { Ln(1) + ":" + M("foo") + " bar", Ln(2) + ":baz " + M("foo") + " " + M("foo"), Ln(4) + ":last " + M("foo") },
            Run("Invoke-BashRg '--color' always '-n' foo a.txt").AssertSuccess().Lines.ToArray());
        string P(string p) => $"{Esc}[0m{Esc}[35m{p}{Esc}[0m";
        var multi = Run("Invoke-BashRg '--color' always '-n' foo a.txt m.rs").AssertSuccess().Lines.ToArray();
        Assert.Contains(P("m.rs") + ":" + Ln(1) + ":fn main() { " + M("foo") + "(); }", multi);
        Assert.Contains(P("a.txt") + ":" + Ln(4) + ":last " + M("foo"), multi);
        Assert.Equal(new[] { M("foo"), M("foo"), M("foo"), M("foo") }, Run("Invoke-BashRg '--color' always '-o' foo a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { M("X") + " bar", "baz " + M("X") + " " + M("X"), "last " + M("X") }, Run("Invoke-BashRg '--color' always '-r' X foo a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { "qux" }, Run("Invoke-BashRg '--color' always '-v' foo a.txt").AssertSuccess().Lines.ToArray());
        // -c / -l / --column / context
        Assert.Equal(new[] { P("a.txt") + ":3", P("m.rs") + ":1" }, Run("Invoke-BashRg '--color' always '-c' foo a.txt m.rs").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { P("a.txt"), P("m.rs") }, Run("Invoke-BashRg '--color' always '-l' foo a.txt m.rs").AssertSuccess().Lines.ToArray());
        string Col(int n) => $"{Esc}[0m{n}{Esc}[0m";
        Assert.Equal(new[] { Ln(1) + ":" + Col(1) + ":" + M("foo") + " bar", Ln(2) + ":" + Col(5) + ":baz " + M("foo") + " " + M("foo"), Ln(4) + ":" + Col(6) + ":last " + M("foo") },
            Run("Invoke-BashRg '--color' always '--column' foo a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { Ln(3) + ":" + M("qux"), Ln(4) + "-last foo" }, Run("Invoke-BashRg '--color' always '-n' '-A1' qux a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { "foo bar", "baz foo foo", "last foo" }, Run("Invoke-BashRg '--color' never foo a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(Run("Invoke-BashRg '--color' always foo a.txt").Lines, Run("Invoke-BashRg '--color' ansi foo a.txt").Lines);
    }

    [Fact]
    public void Color_Specs_FgBgStyleNone()
    {
        string Ln(int n, string fg = "32") => $"{Esc}[0m{Esc}[{fg}m{n}{Esc}[0m";
        var l = Run("Invoke-BashRg '--color' always '--colors' 'match:fg:green' '--colors' 'match:style:nobold' '--colors' 'path:fg:blue' '--colors' 'line:fg:yellow' '-n' foo a.txt m.rs").AssertSuccess().Lines.ToArray();
        string G(string t) => $"{Esc}[0m{Esc}[32m{t}{Esc}[0m";
        Assert.Contains($"{Esc}[0m{Esc}[34ma.txt{Esc}[0m:" + Ln(1, "33") + ":" + G("foo") + " bar", l);
        // style + rgb background: bold, underline, fg, bg in termcolor's order
        var bg = Run("Invoke-BashRg '--color' always '--colors' 'match:bg:0x33,0x66,0xff' '--colors' 'match:style:underline' foo a.txt").AssertSuccess().Lines.ToArray();
        Assert.Equal($"{Esc}[0m{Esc}[1m{Esc}[4m{Esc}[31m{Esc}[48;2;51;102;255mfoo{Esc}[0m bar", bg[0]);
        // 256-colour index and intense
        var c256 = Run("Invoke-BashRg '--color' always '--colors' 'match:fg:208' '--colors' 'match:style:nobold' foo a.txt").AssertSuccess().Lines[0];
        Assert.Equal($"{Esc}[0m{Esc}[38;5;208mfoo{Esc}[0m bar", c256);
        var intense = Run("Invoke-BashRg '--color' always '--colors' 'match:style:intense' '--colors' 'match:style:nobold' foo a.txt").AssertSuccess().Lines[0];
        Assert.Equal($"{Esc}[0m{Esc}[91mfoo{Esc}[0m bar", intense);
        // match:none turns highlighting off entirely
        Assert.Equal(new[] { "foo bar", "baz foo foo", "last foo" }, Run("Invoke-BashRg '--color' always '--colors' 'match:none' foo a.txt").AssertSuccess().Lines.ToArray());
        Run("Invoke-BashRg '--colors' 'bogus:fg:red' foo a.txt").AssertFailed(2, "unrecognized output type 'bogus'. Choose from: path, line, column, match.");
        Run("Invoke-BashRg '--colors' 'match:fg:nocolor' foo a.txt").AssertFailed(2, "unrecognized color name 'nocolor'");
    }

    [Fact]
    public void Color_Pretty_IsColorHeadingLineNumbers_AndAutoFollowsTheRealTerminal()
    {
        string M(string t) => $"{Esc}[0m{Esc}[1m{Esc}[31m{t}{Esc}[0m";
        string Ln(int n) => $"{Esc}[0m{Esc}[32m{n}{Esc}[0m";
        var p = Run("Invoke-BashRg '-p' foo a.txt").AssertSuccess().Lines.ToArray();
        Assert.Equal(Ln(1) + ":" + M("foo") + " bar", p[0]);   // one file: no path, no heading line
        // `auto` colours only on a real terminal (PSBASH_PTY_ATTACHED); PSBASH_RG_TTY alone is a layout override
        Assert.Equal("foo bar", Run("$env:PSBASH_RG_TTY='1'; try { Invoke-BashRg '-N' foo a.txt } finally { Remove-Item Env:PSBASH_RG_TTY }").AssertSuccess().Lines[0]);
        Assert.Equal(M("foo") + " bar", Run("$env:PSBASH_RG_COLOR='1'; try { Invoke-BashRg '-N' foo a.txt } finally { Remove-Item Env:PSBASH_RG_COLOR }").AssertSuccess().Lines[0]);
    }

    // ------------------------------------------------------------------ --json / --stats

    [Fact]
    public void Json_EmitsBeginMatchEndSummary()
    {
        var lines = Run("Invoke-BashRg '--json' foo a.txt").AssertSuccess().Lines.ToArray();
        Assert.Equal(6, lines.Length);   // begin, 3 matches, end, summary
        var docs = lines.Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("begin", docs[0].GetProperty("type").GetString());
        Assert.Equal("a.txt", docs[0].GetProperty("data").GetProperty("path").GetProperty("text").GetString());
        var m2 = docs[2].GetProperty("data");
        Assert.Equal("baz foo foo\n", m2.GetProperty("lines").GetProperty("text").GetString());
        Assert.Equal(2, m2.GetProperty("line_number").GetInt32());
        Assert.Equal(8, m2.GetProperty("absolute_offset").GetInt32());
        var subs = m2.GetProperty("submatches");
        Assert.Equal(2, subs.GetArrayLength());
        Assert.Equal(4, subs[0].GetProperty("start").GetInt32());
        Assert.Equal(7, subs[0].GetProperty("end").GetInt32());
        Assert.Equal(8, subs[1].GetProperty("start").GetInt32());
        Assert.Equal("foo", subs[1].GetProperty("match").GetProperty("text").GetString());
        // the last line has no newline: the text carries none
        Assert.Equal("last foo", docs[3].GetProperty("data").GetProperty("lines").GetProperty("text").GetString());
        Assert.Equal(24, docs[3].GetProperty("data").GetProperty("absolute_offset").GetInt32());
        var end = docs[4].GetProperty("data");
        Assert.Equal(JsonValueKind.Null, end.GetProperty("binary_offset").ValueKind);
        var st = end.GetProperty("stats");
        Assert.Equal(1, st.GetProperty("searches").GetInt32());
        Assert.Equal(1, st.GetProperty("searches_with_match").GetInt32());
        Assert.Equal(32, st.GetProperty("bytes_searched").GetInt32());
        Assert.Equal(3, st.GetProperty("matched_lines").GetInt32());
        Assert.Equal(4, st.GetProperty("matches").GetInt32());
        Assert.Equal("summary", docs[5].GetProperty("type").GetString());
        var sum = docs[5].GetProperty("data").GetProperty("stats");
        Assert.Equal(4, sum.GetProperty("matches").GetInt32());
        Assert.Equal(1, sum.GetProperty("searches_with_match").GetInt32());
        Assert.True(docs[5].GetProperty("data").GetProperty("elapsed_total").TryGetProperty("human", out _));
        // summary key order is alphabetical (data before type); messages put type first
        Assert.StartsWith("{\"data\":{\"elapsed_total\":", lines[5]);
        Assert.StartsWith("{\"type\":\"begin\",\"data\":{\"path\":{\"text\":\"a.txt\"}}}", lines[0]);
    }

    [Fact]
    public void Json_ContextMessages_NoMatchFiles_Stdin_AndBytes()
    {
        var ctx = Run("Invoke-BashRg '--json' '-A1' qux a.txt").AssertSuccess().Lines.Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("context", ctx[2].GetProperty("type").GetString());
        Assert.Equal("last foo", ctx[2].GetProperty("data").GetProperty("lines").GetProperty("text").GetString());
        Assert.Equal(0, ctx[2].GetProperty("data").GetProperty("submatches").GetArrayLength());
        // a file without a match gets no begin/end, only the summary remains
        var none = Run("Invoke-BashRg '--json' nomatch a.txt").Lines.ToArray();
        Assert.Single(none);
        Assert.Equal("summary", JsonDocument.Parse(none[0]).RootElement.GetProperty("type").GetString());
        Assert.Equal("<stdin>", JsonDocument.Parse(Run("'foo' | Invoke-BashRg '--json' foo").Lines[0]).RootElement.GetProperty("data").GetProperty("path").GetProperty("text").GetString());
        // non-UTF-8 content is reported as base64 "bytes"
        var lat = Run("Invoke-BashRg '--json' foo lat.txt").AssertSuccess().Lines.Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("Y2Fm6SBmb28K", lat[1].GetProperty("data").GetProperty("lines").GetProperty("bytes").GetString());
        // --json is ignored by -c / -l
        Assert.Equal(new[] { "3" }, Run("Invoke-BashRg '--json' '-c' foo a.txt").AssertSuccess().Lines.ToArray());
        Assert.Equal(new[] { "a.txt" }, N(Run("Invoke-BashRg '--json' '-l' foo a.txt").AssertSuccess()));
    }

    [Fact]
    public void Stats_PrintAfterTheResults()
    {
        var l = Run("Invoke-BashRg '--stats' foo a.txt").AssertSuccess().Lines.ToArray();
        Assert.Equal(new[] { "foo bar", "baz foo foo", "last foo", "", "4 matches", "3 matched lines", "1 files contained matches", "1 files searched", "29 bytes printed", "32 bytes searched" },
            l.Take(10).ToArray());
        Assert.Matches(@"^\d+\.\d{6} seconds spent searching$", l[10]);
        Assert.Matches(@"^\d+\.\d{6} seconds$", l[11]);
        var none = Run("Invoke-BashRg '--stats' nomatch a.txt").AssertFailed(1);
        Assert.Equal("0 matches", none.Lines[1]);
        Assert.Equal("1 files searched", none.Lines[4]);
        // -c prints 0 bytes
        Assert.Equal("0 bytes printed", Run("Invoke-BashRg '--stats' '-c' foo a.txt").AssertSuccess().Lines[6]);
    }

    // ------------------------------------------------------------------ content: -z -a -E -P -U -f

    [Fact]
    public void SearchZip_ReadsGzip()
    {
        Assert.Equal(new[] { "zipped foo" }, N(Run("Invoke-BashRg '-z' foo z.gz").AssertSuccess()));
        Run("Invoke-BashRg foo z.gz").AssertFailed(1);   // without -z the compressed bytes do not match
    }

    [Fact]
    public void Text_AndBinaryFiles()
    {
        // an explicitly named binary file is not printed: ripgrep reports the match instead
        Assert.Equal(new[] { "binary file matches (found \"\\0\" byte around offset 3)" }, Run("Invoke-BashRg foo bin.dat").AssertSuccess().Lines.ToArray());
        var a = Run("Invoke-BashRg '-a' foo bin.dat").AssertSuccess().Lines.ToArray();
        Assert.Equal("foo\0bar", a[0]);
        // the directory walk skips binary files unless -a
        Assert.DoesNotContain("./bin.dat", N(Run("Invoke-BashRg '-l' foo .").AssertSuccess()));
        Assert.Contains("./bin.dat", N(Run("Invoke-BashRg '-a' '-l' foo .").AssertSuccess()));
    }

    [Fact]
    public void Encoding_Latin1_Utf16_None()
    {
        Assert.Equal(new[] { "caf\u00e9 foo" }, N(Run("Invoke-BashRg '-E' latin1 foo lat.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo x" }, N(Run("Invoke-BashRg '-E' utf-16le foo u16.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo x" }, N(Run("Invoke-BashRg '-E' utf-16 foo u16.txt").AssertSuccess()));
        Run("Invoke-BashRg '-E' bogus foo lat.txt").AssertFailed(2, "unknown encoding: bogus");
        Assert.Single(Run("Invoke-BashRg '-E' none foo lat.txt").AssertSuccess().Lines);
    }

    [Fact]
    public void Pcre_MapsToDotNet_AndRejectsWhatItCannot()
    {
        Assert.Equal(new[] { "fo", "fo", "fo", "fo" }, N(Run("Invoke-BashRg '-P' 'fo(?=o)' '-o' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "baz foo foo" }, N(Run("Invoke-BashRg '-P' '(?<=baz )foo' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo" }, N(Run("Invoke-BashRg '-P' '-o' '(?P<n>f)o\\k<n>?o' m.rs").AssertSuccess()));
        Assert.Equal(new[] { "oo" }, N(Run("Invoke-BashRg '-P' '-o' 'o++' u.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo bar" }, N(Run("Invoke-BashRg '-P' '\\Qfoo bar\\E' a.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo bar" }, N(Run("Invoke-BashRg '-P' 'foo\\h+bar' a.txt").AssertSuccess()));
        // constructs .NET cannot express are errors (ripgrep itself supports \K), never silent mismatches
        Run("Invoke-BashRg '-P' 'foo\\K bar' a.txt").AssertFailed(2, "PCRE2", "\\K");
        Run("Invoke-BashRg '-P' '(?R)' a.txt").AssertFailed(2, "recursion");
        Run("Invoke-BashRg '-P' '(' a.txt").AssertFailed(2, "PCRE2");
    }

    [Fact]
    public void Regex_PerlisedRustSyntax_AndLiteralNewlineRule()
    {
        Assert.Equal(new[] { "foo b" }, N(Run("Invoke-BashRg '-o' '(?P<n>foo) b' a.txt").AssertSuccess()));
        Run("Invoke-BashRg 'a\\nb' ml.txt").AssertFailed(2, "the literal \"\\n\" is not allowed in a regex", "--multiline");
        Run("Invoke-BashRg '(' a.txt").AssertFailed(2, "regex parse error");
        Assert.Equal(new[] { "foo bar", "baz foo foo", "last foo" }, N(Run("Invoke-BashRg '-w' 'foo|bar' a.txt").AssertSuccess()));
    }

    [Fact]
    public void Multiline_SpansLines()
    {
        Assert.Equal(new[] { "a", "b" }, N(Run("Invoke-BashRg '-U' 'a\\nb' ml.txt").AssertSuccess()));
        Assert.Equal(new[] { "1:a", "2:b" }, N(Run("Invoke-BashRg '-U' '-n' 'a\\nb' ml.txt").AssertSuccess()));
        Assert.Equal(new[] { "2:foo", "3:bar", "5:foo", "6:bar" }, N(Run("Invoke-BashRg '-U' '-n' 'foo\\nbar' fb.txt").AssertSuccess()));
        Assert.Equal(new[] { "2" }, N(Run("Invoke-BashRg '-U' '-c' 'foo\\nbar' fb.txt").AssertSuccess()));
        Assert.Equal(new[] { "foo\nbar", "foo\nbar" }.Length, Run("Invoke-BashRg '-U' '-o' 'foo\\nbar' fb.txt").AssertSuccess().Stdout.Split("foo").Length - 1);
        Assert.Equal(new[] { "2:foo", "3:bar", "4-y", "5:foo", "6:bar" }, N(Run("Invoke-BashRg '-U' '-n' '-A1' 'foo\\nbar' fb.txt").AssertSuccess()));
        // `.` does not cross a newline unless --multiline-dotall
        Run("Invoke-BashRg '-U' 'a.b' ml.txt").AssertFailed(1);
        Assert.Equal(new[] { "a", "b" }, N(Run("Invoke-BashRg '-U' '--multiline-dotall' 'a.b' ml.txt").AssertSuccess()));
        // json: one match message carries both lines
        var j = Run("Invoke-BashRg '--json' '-U' 'a\\nb' ml.txt").AssertSuccess().Lines.Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal("a\nb\n", j[1].GetProperty("data").GetProperty("lines").GetProperty("text").GetString());
        Assert.Equal(0, j[1].GetProperty("data").GetProperty("submatches")[0].GetProperty("start").GetInt32());
        Assert.Equal(3, j[1].GetProperty("data").GetProperty("submatches")[0].GetProperty("end").GetInt32());
        Assert.Equal(2, j[2].GetProperty("data").GetProperty("stats").GetProperty("matched_lines").GetInt32());
    }

    [Fact]
    public void PatternFiles_AreOred_AndAnEmptyFileMatchesNothing()
    {
        Assert.Equal(new[] { "baz foo foo", "qux" }, N(Run("Invoke-BashRg '-f' pats a.txt").AssertSuccess()));
        Assert.Equal(new[] { "baz foo foo", "qux", "last foo" }, N(Run("Invoke-BashRg '-f' pats '-e' last a.txt").AssertSuccess()));
        Assert.Equal(new[] { "qux" }, N(Run("'qux' | Invoke-BashRg '-f' - a.txt").AssertSuccess()));
        Write("empty.pats", "");
        Run("Invoke-BashRg '-f' empty.pats a.txt").AssertFailed(1);
        Run("Invoke-BashRg '-f' nonexistent.pats a.txt").AssertFailed(2, "nonexistent.pats", "No such file or directory");
    }

    // ------------------------------------------------------------------ small ones

    [Fact]
    public void Trim_IglobCountMatchesFilesWithout()
    {
        Assert.Equal(new[] { "indented foo" }, N(Run("Invoke-BashRg '--trim' foo t.txt").AssertSuccess()));
        Assert.Equal(new[] { "a.txt:4", "m.rs:1" }, N(Run("Invoke-BashRg '--count-matches' foo a.txt m.rs").AssertSuccess()));
        Assert.Equal(new[] { "ml.txt" }, N(Run("Invoke-BashRg '--files-without-match' foo a.txt ml.txt").AssertSuccess()));
        // -c omits files without a match (ripgrep)
        Assert.Equal(new[] { "a.txt:3" }, N(Run("Invoke-BashRg '-c' foo a.txt ml.txt").AssertSuccess()));
        Assert.Equal(new[] { "4" }, N(Run("Invoke-BashRg '-c' '' a.txt").AssertSuccess()));
    }
}
