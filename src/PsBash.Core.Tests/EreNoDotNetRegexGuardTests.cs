using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <c>[[ s =~ re ]]</c> runs ONLY in the POSIX ERE primitive (<see cref="BashEre"/>, docs/specs/regex-ere.md).
/// It used to be PowerShell's <c>-cmatch</c>, i.e. .NET regex: leftmost-first alternation (<c>(a|ab)</c> on ab gave
/// a), lazy <c>*?</c>, Perl groups <c>(?:)</c>/<c>(?=)</c> accepted, <c>\d</c>/<c>\w</c>/<c>\s</c> as Perl classes,
/// <c>$</c> before a final newline, <c>.</c> not matching a newline. Any route back to .NET regex brings that class
/// back, so this fails when the <c>=~</c> emit, its runtime entry points or the engine reach .NET regex: directly
/// (the type, <c>[GeneratedRegex]</c>, PowerShell's regex operators <c>-match</c>/<c>-replace</c>/<c>-split</c>,
/// <c>switch -regex</c>, <c>Select-String</c>, <c>[regex]</c>) or through a helper that does (the static methods of
/// the runtime/pattern/builder classes, followed transitively).
/// Oracle note (qa-rubric Directive 1): a source invariant; the behaviour is oracle-checked in BashEreTests and
/// RegexEreDifferentialTests.
/// </summary>
public class EreNoDotNetRegexGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static string Read(params string[] parts) => Normalize(File.ReadAllText(Path.Combine([RepoRoot(), .. parts])));

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    // .NET regex by name: the type (Regex, Regex.IsMatch, RegexOptions, new Regex(…)) at a word start, the source
    // generator, the namespace and PowerShell's accelerator. WordToRegex / SedRegexToNet are names, not the type.
    private static readonly Regex DotNetRegex = new(@"(?<![\w])Regex|GeneratedRegex|System\.Text\.RegularExpressions");

    private static readonly Regex PsAccelerator = new(@"\[regex\]", RegexOptions.IgnoreCase);

    // PowerShell's regex operators (any case variant), switch -regex (PowerShell takes any prefix of -regex) and
    // Select-String.
    private static readonly Regex PsRegexOperator = new(
        @"(?<![\w-])-[ci]?(not)?match\b|(?<![\w-])-[ci]?replace\b|(?<![\w-])-[ci]?split\b|\bswitch\b[^{\n]*?(?<![\w-])-r(e(g(e(x)?)?)?)?\b|\bSelect-String\b",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// C# source with its comments blanked (same length, newlines kept). With <paramref name="blankStrings"/>, string
    /// and char literal contents are blanked too (for brace matching); otherwise they are kept, since a string may be
    /// emitted PowerShell.
    /// </summary>
    internal static string StripComments(string src, bool blankStrings = false)
    {
        var sb = new StringBuilder(src);
        int n = src.Length, i = 0;
        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < n; k++)
                if (sb[k] != '\n') sb[k] = ' ';
        }
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int e = src.IndexOf('\n', i);
                if (e < 0) e = n;
                Blank(i, e);
                i = e;
            }
            else if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int e = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? n : e + 2;
                Blank(i, e);
                i = e;
            }
            else if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (src[i + 1] == '"' || src[i + 1] is '@' or '$')))
            {
                int j = i;
                bool verbatim = false;
                while (j < n && src[j] is '@' or '$')
                {
                    if (src[j] == '@') verbatim = true;
                    j++;
                }
                if (j >= n || src[j] != '"')
                {
                    i++;
                    continue;
                }
                int q = 0;
                while (j + q < n && src[j + q] == '"') q++;
                int bodyStart, end;
                if (q >= 3)
                {
                    // Raw string: ends at the same run of quotes.
                    bodyStart = j + q;
                    int e = src.IndexOf(new string('"', q), bodyStart, StringComparison.Ordinal);
                    end = e < 0 ? n : e + q;
                    if (blankStrings) Blank(bodyStart, end - q);
                }
                else if (q == 2 && !verbatim)
                {
                    end = j + 2; // ""
                }
                else
                {
                    bodyStart = j + 1;
                    int k = bodyStart;
                    while (k < n)
                    {
                        if (verbatim && src[k] == '"' && k + 1 < n && src[k + 1] == '"') { k += 2; continue; }
                        if (!verbatim && src[k] == '\\') { k += 2; continue; }
                        if (src[k] == '"' || (!verbatim && src[k] == '\n')) break;
                        k++;
                    }
                    end = Math.Min(k + 1, n);
                    if (blankStrings) Blank(bodyStart, k);
                }
                i = end;
            }
            else if (c == '\'')
            {
                int k = i + 1;
                while (k < n && src[k] != '\'' && src[k] != '\n') k += src[k] == '\\' ? 2 : 1;
                if (blankStrings) Blank(i + 1, k);
                i = Math.Min(k + 1, n);
            }
            else i++;
        }
        return sb.ToString();
    }

    /// <summary>Lines of C# <paramref name="code"/> (comments dropped, strings kept) that name .NET regex.</summary>
    internal static List<string> Offenders(string where, string code) => Scan(where, StripComments(Normalize(code)));

    /// <summary>Lines of emitted PowerShell that name .NET regex.</summary>
    internal static List<string> PsOffenders(string where, string ps) => Scan(where, Normalize(ps));

    private static List<string> Scan(string where, string text)
    {
        var offenders = new List<string>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i];
            if (DotNetRegex.IsMatch(t) || PsAccelerator.IsMatch(t) || PsRegexOperator.IsMatch(t))
                offenders.Add($"{where}:{i + 1}: {t.Trim()}");
        }
        return offenders;
    }

    // A static member's signature: modifiers including `static`, a return type, the name, an optional generic list.
    private static readonly Regex StaticSignature = new(
        @"^[ \t]*(?:\[[^\n]*\][ \t]*)?(?:(?:public|internal|private|protected|new|unsafe|extern|partial)\s+)*static\s+(?:(?:readonly|unsafe|extern|partial|async)\s+)*[\w<>\[\],.?() ]+?\s(\w+)\s*(?:<[^()\n]*>)?\s*\(",
        RegexOptions.Multiline);

    /// <summary>Every static method of a C# source: its name and body (from the signature to the end of its body).</summary>
    internal static List<(string Name, string Body)> StaticMethods(string source)
    {
        var src = Normalize(source);
        var skeleton = StripComments(src, blankStrings: true);
        var result = new List<(string, string)>();
        foreach (Match m in StaticSignature.Matches(skeleton))
        {
            int end = BodyEnd(skeleton, m.Index + m.Length - 1);
            if (end > m.Index) result.Add((m.Groups[1].Value, src[m.Index..end]));
        }
        return result;
    }

    // From the parameter list's `(`: past its `)`, then a `{...}` block or an `=> ...;` body. -1: not a method.
    private static int BodyEnd(string sk, int open)
    {
        int i = Matching(sk, open);
        if (i < 0) return -1;
        for (; i < sk.Length; i++)
        {
            if (sk[i] == '{') return Matching(sk, i);
            if (sk[i] == ';') return -1; // a declaration (extern, partial) or a field of a delegate type
            if (sk[i] == '=' && i + 1 < sk.Length && sk[i + 1] == '>')
            {
                int depth = 0;
                for (int k = i + 2; k < sk.Length; k++)
                {
                    char c = sk[k];
                    if (c is '(' or '{' or '[') depth++;
                    else if (c is ')' or '}' or ']') depth--;
                    else if (c == ';' && depth == 0) return k + 1;
                }
                return -1;
            }
        }
        return -1;
    }

    // The index just past the bracket closing the one at `open` (strings and comments already blanked).
    private static int Matching(string sk, int open)
    {
        char o = sk[open], cl = o == '(' ? ')' : '}';
        int depth = 0;
        for (int k = open; k < sk.Length; k++)
        {
            if (sk[k] == o) depth++;
            else if (sk[k] == cl && --depth == 0) return k + 1;
        }
        return -1;
    }

    // .NET-regex PATTERN producers: they translate to .NET syntax and call no Regex themselves, so the scan cannot
    // see them; any route through them is a route to .NET regex.
    internal static readonly string[] PatternProducers =
        ["SedRegexToNet", "TranslatePosixClasses", "TranslateBre", "LiteralBracketBackslashes", "PosixClass", "PosixClassBody"];

    // The same, as types: BashPattern.Converter turns a glob into .NET regex text.
    internal static readonly string[] PatternProducerTypes = ["Converter"];

    /// <summary>
    /// What reaches .NET regex in the helper <paramref name="classes"/> (class name -> source): the nested or other
    /// types whose bodies use it (<c>BashPatternProgram</c>, <c>BashPattern.Converter</c>), and the static methods
    /// that use it, produce its patterns, name such a type, or call such a method (fixpoint).
    /// </summary>
    internal static (HashSet<(string Class, string Name)> Methods, HashSet<string> Types) RegexHelpers(
        IReadOnlyDictionary<string, string> classes)
    {
        var types = new HashSet<string>();
        foreach (var src in classes.Values)
        {
            var text = Normalize(src);
            var skeleton = StripComments(text, blankStrings: true);
            foreach (Match m in TypeDeclaration.Matches(skeleton))
            {
                var name = m.Groups[1].Value;
                if (classes.ContainsKey(name)) continue;
                int open = skeleton.IndexOfAny(['{', ';'], m.Index + m.Length);
                if (open < 0 || skeleton[open] != '{') continue;
                int end = Matching(skeleton, open);
                if (Array.IndexOf(PatternProducerTypes, name) >= 0 || (end > open && Offenders(name, text[open..end]).Count > 0))
                    types.Add(name);
            }
        }
        var methods = new List<(string Class, string Name, string Body)>();
        foreach (var (cls, src) in classes)
            foreach (var (name, body) in StaticMethods(src))
                methods.Add((cls, name, body));
        var tainted = new HashSet<(string, string)>();
        foreach (var (cls, name, body) in methods)
            if (Array.IndexOf(PatternProducers, name) >= 0 || Offenders(name, body).Count > 0 || TypeRefs(body, types).Count > 0)
                tainted.Add((cls, name));
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (cls, name, body) in methods)
                if (!tainted.Contains((cls, name)) && Calls(body, cls, tainted).Count > 0)
                    grew |= tainted.Add((cls, name));
        }
        return (tainted, types);
    }

    private static readonly Regex TypeDeclaration = new(@"\b(?:class|struct|record)\s+(?:class\s+|struct\s+)?(\w+)");

    /// <summary>The names of <paramref name="types"/> that <paramref name="code"/> uses.</summary>
    internal static List<string> TypeRefs(string code, HashSet<string> types)
    {
        var text = StripComments(Normalize(code));
        var found = new List<string>();
        foreach (var t in types)
            if (Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(t)}(?![\w])")) found.Add(t);
        return found;
    }

    /// <summary>
    /// The calls in <paramref name="code"/> (of class <paramref name="ownClass"/>) to a method of
    /// <paramref name="helpers"/>: <c>Class.Name(</c> or <c>[Class]::Name(</c> (PowerShell), or a bare <c>Name(</c> of
    /// its own class.
    /// </summary>
    internal static List<string> Calls(string code, string ownClass, HashSet<(string Class, string Name)> helpers)
    {
        var text = StripComments(Normalize(code));
        var found = new List<string>();
        foreach (var (cls, name) in helpers)
        {
            var qualified = new Regex($@"(?<![\w]){Regex.Escape(cls)}(?:\.|\]::){Regex.Escape(name)}\s*\(");
            var bare = new Regex($@"(?<![\w.]){Regex.Escape(name)}\s*\(");
            if (qualified.IsMatch(text) || (cls == ownClass && bare.IsMatch(text))) found.Add($"{cls}.{name}");
        }
        return found;
    }

    /// <summary>Every way <paramref name="code"/> (of class <paramref name="ownClass"/>) reaches .NET regex.</summary>
    private static IEnumerable<string> Reaches(string where, string code, string ownClass,
        (HashSet<(string Class, string Name)> Methods, HashSet<string> Types) reach) =>
        Offenders(where, code)
            .Concat(Calls(code, ownClass, reach.Methods).Select(c => $"{where} calls {c}"))
            .Concat(TypeRefs(code, reach.Types).Select(t => $"{where} uses {t}"));

    /// <summary>
    /// The member that starts at <paramref name="signature"/>: an expression body up to its <c>;</c>, a block body up
    /// to its closing brace, or up to <paramref name="endMarker"/> when one is given. Line endings do not matter.
    /// </summary>
    private static string Member(string source, string signature, string? endMarker = null)
    {
        source = Normalize(source);
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found: update the guard with the member's new name.");
        int end;
        if (endMarker is not null)
            end = source.IndexOf(endMarker, start, StringComparison.Ordinal) + endMarker.Length;
        else
        {
            var skeleton = StripComments(source, blankStrings: true);
            int open = skeleton.IndexOf('(', start + signature.Length - 1);
            end = BodyEnd(skeleton, open);
        }
        Assert.True(end > start, $"end of '{signature}' not found");
        return source[start..end];
    }

    // The classes whose static helpers an ERE entry point could call: the runtime, the pattern classes, the builder.
    private static Dictionary<string, string> HelperClasses() => new()
    {
        ["BashRuntime"] = Read("src", "PsBash.Cmdlets", "BashRuntime.cs"),
        ["BashPatternMatch"] = Read("src", "PsBash.Cmdlets", "BashPatternMatch.cs"),
        ["BashPattern"] = Read("src", "PsBash.Transpiler", "Parser", "BashPattern.cs"),
        ["PsBuild"] = Read("src", "PsBash.Transpiler", "Parser", "PsBuild.cs"),
    };

    [Fact]
    public void RegexHelpers_FindsTheRuntimesDotNetRegexHelpers()
    {
        // The transitive check is only as good as what it finds: the known .NET-regex helpers and types, and every
        // pattern producer still exists under its name (a rename must update the list).
        var (methods, types) = RegexHelpers(HelperClasses());
        Assert.Contains(("BashRuntime", "SedRegexToNet"), methods);
        Assert.Contains(("BashRuntime", "TranslatePosixClasses"), methods);
        Assert.Contains(("BashRuntime", "TranslateBre"), methods);
        Assert.Contains(("BashPattern", "PosixClass"), methods);
        Assert.Contains("BashPatternProgram", types);
        Assert.Contains("Converter", types);
        Assert.DoesNotContain(("BashRuntime", "GetBashText"), methods);
        var all = string.Concat(HelperClasses().Values);
        foreach (var name in PatternProducers)
            Assert.True(all.Contains(" " + name + "(", StringComparison.Ordinal), $"pattern producer '{name}' not found: update the guard");
    }

    [Fact]
    public void EreEngineSources_DoNotUseDotNetRegex()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var files = Directory.GetFiles(dir, "BashEre*.cs");
        Assert.True(files.Length >= 4, "BashEre engine files not found");
        var reach = RegexHelpers(HelperClasses());
        var offenders = new List<string>();
        foreach (var f in files)
            offenders.AddRange(Reaches(Path.GetFileName(f), File.ReadAllText(f), Path.GetFileNameWithoutExtension(f), reach));
        Assert.True(offenders.Count == 0, "the ERE engine reaches .NET regex:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EreEmitAndRuntimeEntryPoints_DoNotUseDotNetRegex()
    {
        var emitter = Read("src", "PsBash.Transpiler", "Parser", "PsEmitter.cs");
        var classes = HelperClasses();
        var reach = RegexHelpers(classes);

        var members = new List<(string Where, string Class, string Code)>
        {
            ("PsEmitter =~ branch", "PsEmitter", Member(emitter, "if (op == \"=~\")", "return PsBuild.EreTest(")),
            ("PsEmitter.BuildEreWord", "PsEmitter", Member(emitter, "private static string BuildEreWord(")),
            ("PsEmitter.TryEmitEreOperand", "PsEmitter", Member(emitter, "private static bool TryEmitEreOperand(")),
        };
        foreach (var sig in new[] { "public static string EreTest(", "public static string EreWord(", "public static string EreLiteral(",
                     "public static string EreQuoted(", "public static string EreUnquoted(", "public static string EreOperand(",
                     "public static string EreExpansion(" })
            members.Add(("PsBuild." + sig, "PsBuild", Member(classes["PsBuild"], sig)));
        foreach (var sig in new[] { "public static PsBash.Core.Parser.BashEreResult EreTest(", "public static string EreQuote(", "public static string EreRaw(",
                     "public static string EreExpansion(" })
            members.Add(("BashPatternMatch." + sig, "BashPatternMatch", Member(classes["BashPatternMatch"], sig)));

        var offenders = new List<string>();
        foreach (var (where, cls, code) in members)
            offenders.AddRange(Reaches(where, code, cls, reach));
        Assert.True(offenders.Count == 0, "=~ reaches .NET regex:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("[[ $a =~ ^(a|ab)$ ]]")]
    [InlineData("[[ $a =~ $re ]]")]
    [InlineData("[[ $a =~ \"$q\"x$re ]]")]
    [InlineData("[[ $a =~ ^a\\.b$ ]] && echo y")]
    [InlineData("if [[ ! $a =~ [[:alpha:]] ]]; then :; fi")]
    [InlineData("shopt -s nocasematch; [[ AB =~ ab ]]")]
    public void Transpile_RegexMatch_EmitsNoDotNetRegex(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("[PsBash.Cmdlets.BashPatternMatch]::EreTest(", ps);
        Assert.Empty(PsOffenders("emitted", ps));
    }

    [Fact]
    public void Guard_CatchesADotNetRegexRoute()
    {
        // The guard's own detector must see the shapes the old emit used, and the other ways to reach .NET regex.
        Assert.NotEmpty(PsOffenders("x", "$( $global:__psbash_m = $env:a -cmatch '^a$' )"));
        Assert.NotEmpty(Offenders("x", "return System.Text.RegularExpressions.Regex.IsMatch(s, p);"));
        Assert.NotEmpty(Offenders("x", "return \"[regex]::Escape($v)\";"));
        Assert.NotEmpty(Offenders("x", "[GeneratedRegex(\"a+\")] private static partial Rx Pat();"));
        Assert.NotEmpty(Offenders("x", "var o = RegexOptions.CultureInvariant;"));
        Assert.NotEmpty(PsOffenders("x", "$v -creplace 'a', 'b'"));
        Assert.NotEmpty(PsOffenders("x", "$v -iSplit ','"));
        Assert.NotEmpty(PsOffenders("x", "$v -cnotmatch 'a'"));
        Assert.NotEmpty(PsOffenders("x", "switch -regex ($v) { 'a' { 1 } }"));
        Assert.NotEmpty(PsOffenders("x", "switch -CaseSensitive -r ($v) { 'a' { 1 } }"));
        Assert.NotEmpty(PsOffenders("x", "$v | Select-String 'a'"));
        // A ` //` inside a string is not a comment: what follows is still emitted code.
        Assert.NotEmpty(Offenders("x", "return \"http://x\" + \" -cmatch '^a$'\";"));
        Assert.NotEmpty(Offenders("x", "return @\"a // b -match c\";"));
        Assert.NotEmpty(Offenders("x", "return \"\"\"a // \" -replace x\"\"\";"));
        // Comments are not code; names that merely contain the word are not the type.
        Assert.Empty(Offenders("x", "// .NET Regex is not used here"));
        Assert.Empty(Offenders("x", "x = 1; // -cmatch was the old way"));
        Assert.Empty(Offenders("x", "/* Regex\n -match */ y = 2;"));
        Assert.Empty(Offenders("x", "var r = BashEre.WordToRegex(w); var t = \"--replace-all\";"));
    }

    [Fact]
    public void Guard_FollowsHelpersTransitively()
    {
        const string helper = """
            public static class H
            {
                // Regex here is only a comment.
                public static string Clean(string s) => s.Trim();

                private static string Translate(string s) =>
                    System.Text.RegularExpressions.Regex.Replace(s, "a", "b");

                internal static bool Test(string s, string re)
                {
                    var t = Translate(re);
                    return t.Length > 0;
                }

                public static string Ps(string e) => e + " -cmatch 'x'";

                internal sealed class Prog
                {
                    private readonly System.Text.RegularExpressions.Regex _r = new("a");
                }

                public static bool UsesProg() => new Prog() != null;
            }
            """;
        var (helpers, types) = RegexHelpers(new Dictionary<string, string> { ["H"] = helper });
        Assert.Contains("Prog", types);
        Assert.Contains(("H", "UsesProg"), helpers);    // names a type that holds a Regex
        Assert.NotEmpty(TypeRefs("var p = new H.Prog();", types));
        Assert.Contains(("H", "Translate"), helpers);
        Assert.Contains(("H", "Test"), helpers);        // through Translate
        Assert.Contains(("H", "Ps"), helpers);          // emitted PowerShell in a string
        Assert.DoesNotContain(("H", "Clean"), helpers);
        Assert.NotEmpty(Calls("return H.Test(a, b);", "Other", helpers));
        Assert.NotEmpty(Calls("\"[H]::Ps(\" + x + \")\"", "Other", helpers));
        Assert.NotEmpty(Calls("return Test(a, b);", "H", helpers));
        Assert.Empty(Calls("return Test(a, b);", "Other", helpers)); // another class's Test
        Assert.Empty(Calls("return H.Clean(a);", "Other", helpers));
    }

    [Fact]
    public void Guard_MemberExtraction_IgnoresLineEndings()
    {
        // A CRLF checkout must not hide a member's body from the scan.
        const string src = "public static class C\r\n{\r\n    public static string F(string s)\r\n    {\r\n        return s -cmatch x;\r\n    }\r\n}\r\n";
        var methods = StaticMethods(src);
        Assert.Single(methods);
        Assert.Contains("-cmatch", methods[0].Body);
        Assert.Contains("-cmatch", Member(src, "public static string F("));
    }
}
