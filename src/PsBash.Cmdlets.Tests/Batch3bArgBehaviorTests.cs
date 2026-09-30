using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end behavior of the batch-3b argv migration (fold expand unexpand paste join comm split
/// strings base64) through the cmdlets, in the shape the transpiler emits (every dash word
/// single-quoted, so it reaches Arguments verbatim) plus DIRECT calls (`Invoke-BashFold -w 4`) for
/// the decoy re-injection the Pester gate exercises. Oracle: GNU coreutils 9.4 / binutils 2.42
/// (`wsl bash`) — every expectation was checked there. Tests marked FIX assert output that used to
/// be silently wrong.
/// </summary>
public class Batch3bArgBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public Batch3bArgBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-b3b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    // Quoted path of a fixture file (LF endings, trailing newline).
    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return "'" + p.Replace("'", "''") + "'";
    }

    // ── fold ────────────────────────────────────────────────────────────────

    [Fact]
    public void Fold_WidthForms_AllWrap()
    {
        Assert.Equal(new[] { "abcd", "efgh", "ij" },
            Run("'abcdefghij' | Invoke-BashFold '-w' 4").AssertSuccess().Lines);
        Assert.Equal(new[] { "abcd", "efgh", "ij" },
            Run("'abcdefghij' | Invoke-BashFold '--wid=4'").AssertSuccess().Lines);   // FIX (abbreviation)
        Assert.Equal(new[] { "abcde", "fghij" },
            Run("'abcdefghij' | Invoke-BashFold '-5'").AssertSuccess().Lines);        // FIX (obsolete -NUM)
        Assert.Equal(new[] { "abc", "def", "ghi", "j" },
            Run("'abcdefghij' | Invoke-BashFold '-w' 4 '-w' 3").AssertSuccess().Lines);
    }

    [Fact]
    public void Fold_DirectCall_DecoysStillBind()
    {
        Assert.Equal(new[] { "abc", "def", "ghi", "j" },
            Run("'abcdefghij' | Invoke-BashFold -w 3").AssertSuccess().Lines);
        Assert.Equal(new[] { "hello ", "world" },
            Run("'hello world' | Invoke-BashFold -s -w 8").AssertSuccess().Lines);
    }

    [Theory]
    [InlineData("'-w' 0", "invalid number of columns: '0'")]   // FIX (was: never wrap)
    [InlineData("'-w' x", "invalid number of columns: 'x'")]   // FIX (was: silently 80)
    [InlineData("'-w4k'", "invalid number of columns: '4k'")]
    [InlineData("'-c'", "invalid option -- 'c'")]
    public void Fold_BadArgs_ExitOne(string args, string message)
        => Run($"'abc' | Invoke-BashFold {args}").AssertFailed(1, "fold: " + message);

    // ── expand / unexpand ───────────────────────────────────────────────────

    [Fact]
    public void Expand_TabList_FollowsGnu()
    {
        // `printf 'x\t\t\ty\tz\n' | expand -t 4,8`: stops at 4 and 8, then single spaces.
        Assert.Equal("x" + new string(' ', 3 + 4 + 1) + "y z",
            Run("\"x`t`t`ty`tz\" | Invoke-BashExpand '-t' '4,8'").AssertSuccess().Lines.Single());  // FIX (was: 8)
        Assert.Equal("a   b",
            Run("\"a`tb\" | Invoke-BashExpand '-4'").AssertSuccess().Lines.Single());               // -NUM
    }

    [Theory]
    [InlineData("'-t' 0", "tab size cannot be 0")]
    [InlineData("'-t' '4,2'", "tab sizes must be ascending")]
    [InlineData("'-t' x", "tab size contains invalid character(s): 'x'")]
    public void Expand_BadTabSpec_ExitOne(string args, string message)
        => Run($"\"a`tb\" | Invoke-BashExpand {args}").AssertFailed(1, "expand: " + message);

    [Fact]
    public void Unexpand_MinusT_ImpliesAll_ButDefaultIsLeadingOnly()
    {
        // a + 7 spaces + b: default (leading blanks only) leaves it alone; -t 4 implies -a (FIX).
        Assert.Equal("a       b", Run("'a       b' | Invoke-BashUnexpand").AssertSuccess().Lines.Single());
        Assert.Equal("a\t\tb", Run("'a       b' | Invoke-BashUnexpand '-t' 4").AssertSuccess().Lines.Single());
        Assert.Equal("a       b", Run("'a       b' | Invoke-BashUnexpand '-t' 4 '--first-only'").AssertSuccess().Lines.Single());
        Assert.Equal("a\tb", Run("'a       b' | Invoke-BashUnexpand -a").AssertSuccess().Lines.Single());   // direct call: -a decoy
    }

    [Fact]
    public void Unexpand_BadTabSpec_ExitOne()
        => Run("'a' | Invoke-BashUnexpand '-t' 0").AssertFailed(1, "unexpand: tab size cannot be 0");

    // ── paste ───────────────────────────────────────────────────────────────

    [Fact]
    public void Paste_DelimiterListCyclesPerColumn()
    {
        string a = F("p1", "1\n2\n3\n"), b = F("p2", "a\nb\nc\n"), c = F("p3", "x\ny\nz\n");
        Assert.Equal(new[] { "1,a;x", "2,b;y", "3,c;z" },
            Run($"Invoke-BashPaste '-d' ',;' {a} {b} {c}").AssertSuccess().Lines);   // FIX (was "1,;a,;x")
        Assert.Equal(new[] { "1,2;3", "a,b;c" },
            Run($"Invoke-BashPaste '-s' '-d' ',;' {a} {b}").AssertSuccess().Lines);   // FIX
        Assert.Equal(new[] { "1a", "2b", "3c" },
            Run($"Invoke-BashPaste '-d' '' {a} {b}").AssertSuccess().Lines);           // empty list = \0
        Assert.Equal(new[] { "1,2,3" },
            Run($"Invoke-BashPaste '-sd,' {a}").AssertSuccess().Lines);               // bundle, d takes the rest
        Assert.Equal(new[] { "1,a", "2,b", "3,c" },
            Run($"Invoke-BashPaste {a} '-d,' {b}").AssertSuccess().Lines);            // FIX (options after operands)
    }

    [Fact]
    public void Paste_DirectCall_DelimiterDecoyBinds()
    {
        string a = F("p1", "1\n2\n"), b = F("p2", "a\nb\n"), c = F("p3", "x\ny\n");
        Assert.Equal(new[] { "1;a,x", "2;b,y" },
            Run($"Invoke-BashPaste -d ';,' {a} {b} {c}").AssertSuccess().Lines);
    }

    [Fact]
    public void Paste_DashOperandIsStdin_AndSharedAcrossDashes()
    {
        // `paste - -` pairs consecutive stdin lines (the classic idiom); previously `-` was a file.
        Assert.Equal(new[] { "1\t2", "3\t" },
            Run("'1','2','3' | Invoke-BashPaste '-' '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void Paste_BadArgs()
    {
        Run("Invoke-BashPaste '-d' '\\'").AssertFailed(1, "delimiter list ends with an unescaped backslash");
        Run("Invoke-BashPaste '-x'").AssertFailed(1, "invalid option -- 'x'");
        Run("Invoke-BashPaste '-z'").AssertFailed(2, "recognized but not supported");
    }

    // ── join ────────────────────────────────────────────────────────────────

    [Fact]
    public void Join_UnsupportedAndInvalid_AreLoud()
    {
        string a = F("j1", "1 a\n2 b\n"), b = F("j2", "1 x\n3 y\n");
        Assert.Equal(new[] { "1 a x" }, Run($"Invoke-BashJoin {a} {b}").AssertSuccess().Lines);
        Run($"Invoke-BashJoin '-o' '0,1.2' {a} {b}").AssertFailed(2, "option '-o' is recognized but not supported");   // FIX (was: default format)
        Run($"Invoke-BashJoin '-e' X {a} {b}").AssertFailed(2, "option '-e' is recognized but not supported");
        Run($"Invoke-BashJoin '-1' x {a} {b}").AssertFailed(1, "invalid field number: 'x'");                             // FIX (was: field 1)
        Run($"Invoke-BashJoin '-a3' {a} {b}").AssertFailed(1, "invalid field number: '3'");                             // FIX (was: an operand)
        Run($"Invoke-BashJoin '-t' ab {a} {b}").AssertFailed(1, "multi-character tab 'ab'");
        Run($"Invoke-BashJoin {a} {b} {b}").AssertFailed(1, "extra operand");
        Run($"Invoke-BashJoin {a}").AssertFailed(1, "missing operand after");
    }

    [Fact]
    public void Join_OptionsInAnyOrder_AndDirectDecoys()
    {
        string a = F("j1", "1 a\n2 b\n"), b = F("j2", "1 x\n3 y\n");
        Assert.Equal(new[] { "1 a x", "2 b" }, Run($"Invoke-BashJoin '-a1' {a} {b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "1 a x", "2 b" }, Run($"Invoke-BashJoin {a} {b} '-a' 1").AssertSuccess().Lines);   // FIX (options after operands)
        Assert.Equal(new[] { "1 a x", "2 b" }, Run($"Invoke-BashJoin -a 1 {a} {b}").AssertSuccess().Lines);     // direct: -a decoy
        Assert.Equal(new[] { "2 b" }, Run($"Invoke-BashJoin '-v' 1 {a} {b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "1 a x" }, Run($"Invoke-BashJoin '--ignore' {a} {b}").AssertSuccess().Lines);      // FIX (abbreviation)
    }

    // ── comm ────────────────────────────────────────────────────────────────

    [Fact]
    public void Comm_OutputDelimiter_AndTotal()
    {
        string a = F("c1", "a\nb\nc\n"), b = F("c2", "b\nc\nd\n");
        Assert.Equal(new[] { "a", "::b", "::c", ":d" },
            Run($"Invoke-BashComm '--output-delimiter=:' {a} {b}").AssertSuccess().Lines);   // FIX (was: refused)
        Assert.Equal(new[] { "a", "::b", "::c", ":d", "1:1:2:total" },
            Run($"Invoke-BashComm '--total' '--output-delimiter' ':' {a} {b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b", "c" }, Run($"Invoke-BashComm '-12' {a} {b}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b", "c" }, Run($"Invoke-BashComm {a} {b} '-1' '-2'").AssertSuccess().Lines);   // FIX (options after operands)
        Assert.Equal(new[] { "a", "\t\tb", "\t\tc", "\td", "1\t1\t2\ttotal" },
            Run($"Invoke-BashComm '--tot' {a} {b}").AssertSuccess().Lines);
    }

    [Fact]
    public void Comm_BadArgs()
    {
        string a = F("c1", "a\n"), b = F("c2", "b\n");
        Run($"Invoke-BashComm {a} {b} {b}").AssertFailed(1, "comm: extra operand");             // FIX (was: ignored)
        Run($"Invoke-BashComm {a}").AssertFailed(1, "comm: missing operand after");
        Run($"Invoke-BashComm '-4' {a} {b}").AssertFailed(1, "invalid option -- '4'");
        Run($"Invoke-BashComm '--check' {a} {b}").AssertFailed(2, "'--check-order' is recognized but not supported");
    }

    // ── split ───────────────────────────────────────────────────────────────

    private string[] SplitFiles(string script, string content = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n")
    {
        var d = Path.Combine(_dir, Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "in"), content);
        Run($"Set-Location '{d.Replace("'", "''")}'; {script}").AssertSuccess();
        return Directory.GetFiles(d).Select(Path.GetFileName).Where(n => n != "in").OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
    }

    [Fact]
    public void Split_Forms()
    {
        Assert.Equal(new[] { "xaa", "xab", "xac", "xad" }, SplitFiles("Invoke-BashSplit '-3' in"));                 // FIX (obsolete -NUM)
        Assert.Equal(new[] { "x05", "x06", "x07" }, SplitFiles("Invoke-BashSplit '--numeric-suffixes=5' '-l' 4 in")); // FIX (FROM)
        Assert.Equal(new[] { "xaa", "xab", "xac", "xad" }, SplitFiles("Invoke-BashSplit '-b' 6 in"));
        Assert.Equal(new[] { "xaa" }, SplitFiles("Invoke-BashSplit '-b' 1K in"));
        Assert.Equal(new[] { "xaa" }, SplitFiles("Invoke-BashSplit '-b' 1KB in"));                                  // FIX (was: 1 byte = 21 files)
        Assert.Equal(new[] { "yaa", "yab", "yac", "yad" }, SplitFiles("Invoke-BashSplit '-l3' in y"));
        Assert.Equal(new[] { "xaaa.txt", "xaab.txt", "xaac.txt" }, SplitFiles("Invoke-BashSplit '-l' 4 '-a' 3 '--add=.txt' in"));
    }

    [Fact]
    public void Split_DirectCall_DecoysBind()
    {
        Assert.Equal(new[] { "x000", "x001", "x002" }, SplitFiles("Invoke-BashSplit -l 4 -d -a 3 in"));
    }

    [Fact]
    public void Split_BadArgs_CreateNothing()
    {
        var d = Path.Combine(_dir, "bad"); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "in"), "1\n2\n");
        string cd = $"Set-Location '{d.Replace("'", "''")}'; ";
        Run(cd + "Invoke-BashSplit '-l' 0 in").AssertFailed(1, "invalid number of lines: '0'");          // FIX (was: 1000 lines)
        Run(cd + "Invoke-BashSplit '-b' 1x in").AssertFailed(1, "invalid number of bytes: '1x'");
        Run(cd + "Invoke-BashSplit '-l' 2 '-b' 3 in").AssertFailed(1, "cannot split in more than one way");
        Run(cd + "Invoke-BashSplit in y z").AssertFailed(1, "extra operand 'z'");
        Run(cd + "Invoke-BashSplit '-n' 3 in").AssertFailed(2, "recognized but not supported");
        Run(cd + "Invoke-BashSplit -e in").AssertFailed(2, "recognized but not supported");                 // direct: -e decoy
        Assert.Equal(new[] { "in" }, Directory.GetFiles(d).Select(Path.GetFileName).ToArray());
    }

    // ── strings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Strings_Forms()
    {
        Assert.Equal(new[] { "hello world" }, Run("\"ab`thello world`tcd\" | Invoke-BashStrings '-n' 6").AssertSuccess().Lines);
        Assert.Equal(new[] { "hello world" }, Run("\"ab`thello world`tcd\" | Invoke-BashStrings '--by=6'").AssertSuccess().Lines);  // FIX
        Assert.Equal(new[] { "hello world" }, Run("\"ab`thello world`tcd\" | Invoke-BashStrings '-6'").AssertSuccess().Lines);      // FIX
        Assert.Equal(new[] { "hello world" }, Run("\"ab`thello world`tcd\" | Invoke-BashStrings '-a' '-n6'").AssertSuccess().Lines); // FIX (-a accepted)
        Assert.Equal(new[] { "hello world" }, Run("\"ab`thello world`tcd\" | Invoke-BashStrings -a -n 6").AssertSuccess().Lines);    // direct: -a decoy
    }

    [Fact]
    public void Strings_BadArgs()
    {
        Run("'abc' | Invoke-BashStrings '-n' 0").AssertFailed(1, "strings: minimum string length is too small: 0");   // FIX (was: clamped)
        Run("'abc' | Invoke-BashStrings '-n' x").AssertFailed(1, "strings: invalid integer argument x");           // FIX (was: ignored)
        Run("'abc' | Invoke-BashStrings '-x'").AssertFailed(1, "invalid option -- 'x'");
        Run("'abc' | Invoke-BashStrings '-d'").AssertFailed(2, "recognized but not supported");
        Run("'abc' | Invoke-BashStrings -e").AssertFailed(2, "recognized but not supported");                      // direct: -e decoy
    }

    // ── base64 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Base64_Forms()
    {
        Assert.Equal("aGVsbG8gd29ybGQK", Run("'hello world' | Invoke-BashBase64 '-w0'").AssertSuccess().Lines.Single());
        Assert.Equal("hello world", Run("'aGVsbG8gd29ybGQK' | Invoke-BashBase64 '--dec'").AssertSuccess().Lines.Single());   // FIX (abbrev)
        Assert.Equal("hello world", Run("'aGVsbG8gd29ybGQK' | Invoke-BashBase64 '-dw0'").AssertSuccess().Lines.Single());    // FIX (bundle)
        Assert.Equal("hello world", Run("'aGVsbG8g!d29ybGQK' | Invoke-BashBase64 '-di'").AssertSuccess().Lines.Single());
        // direct calls: -d / -w / -i decoys
        Assert.Equal("aGVsbG8gd29ybGQK", Run("'hello world' | Invoke-BashBase64 -w 0").AssertSuccess().Lines.Single());
        Assert.Equal("hello world", Run("'aGVsbG8gd29ybGQK' | Invoke-BashBase64 -d").AssertSuccess().Lines.Single());
        Assert.Equal("hello world", Run("'aGVsbG8g!d29ybGQK' | Invoke-BashBase64 -d -i").AssertSuccess().Lines.Single());
    }

    [Fact]
    public void Base64_FileOperand_DashIsStdin_AndExtraOperandErrors()
    {
        string f = F("b64", "hello world\n");
        Assert.Equal("aGVsbG8gd29ybGQK", Run($"Invoke-BashBase64 '-w' 0 {f}").AssertSuccess().Lines.Single());
        Assert.Equal("aGVsbG8gd29ybGQK", Run("'hello world' | Invoke-BashBase64 '-w0' '-'").AssertSuccess().Lines.Single());  // FIX (- was a file)
        Run($"Invoke-BashBase64 {f} {f}").AssertFailed(1, "base64: extra operand");                                          // FIX (was: ignored)
        Run("'x' | Invoke-BashBase64 '-w' x").AssertFailed(1, "invalid wrap size: 'x'");                                      // FIX (was: silently 76)
        Run("'x' | Invoke-BashBase64 '-z'").AssertFailed(1, "invalid option -- 'z'");
    }
}
