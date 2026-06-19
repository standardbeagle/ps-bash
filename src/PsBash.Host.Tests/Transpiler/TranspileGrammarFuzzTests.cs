using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Generative grammar fuzzer (docs/specs/transpile-fuzz-grammar.md §3). A seeded recursive
/// producer emits VALID bash across the documented construct grammar; each generated program
/// must satisfy the parseability contract — almost always by emitting valid PowerShell
/// (branch A), exercising the emitter far more densely than any hand-written corpus could.
///
/// Deterministic (qa-rubric Directive 6): the master seed is fixed, so the exact program
/// sequence is reproducible. A failure prints the offending bash + emitted PowerShell, which
/// drops straight into TranspileParseabilityCorpusTests as a permanent regression row.
/// </summary>
public class TranspileGrammarFuzzTests
{
    private const int MasterSeed = 0x5EED_1;
    private const int Cases = 2000;

    [Fact]
    public void Generated_Bash_AlwaysSatisfiesParseabilityContract()
    {
        var rng = new Random(MasterSeed);
        for (int i = 0; i < Cases; i++)
        {
            var g = new Gen(rng);
            string bash = g.Stmt(depth: 0);
            // Contract: valid PS, or a clean ParseException — never a crash / broken PS.
            ParseabilityContract.Assert(bash, $"grammar seed={MasterSeed} case={i}");
        }
    }

    /// <summary>One recursive bash producer instance sharing the test's seeded RNG.</summary>
    private sealed class Gen
    {
        private readonly Random _r;
        public Gen(Random r) => _r = r;

        private static readonly string[] Vars = { "x", "y", "foo", "PATH", "n", "arr" };
        private static readonly string[] Lits = { "a", "hello", "bar", "1", "file.txt", "" };
        private static readonly string[] Cmds = { "echo", "cat", "grep foo", "head -1", "wc -l", "true", "ls" };
        private static readonly string[] ScalarOps =
        {
            ":-{0}", ":={0}", ":+{0}", ":?{0}", "-{0}", "+{0}", "#{0}", "##{0}", "%{0}", "%%{0}",
            "/{0}/r", "//{0}/r", ":1", ":1:2", "^^", ",,", "@Q", "@U",
        };
        private static readonly string[] ArithOps = { "+", "-", "*", "/", "%", "**", "<<", "&", "|", "^", "<", ">", "==", "!=" };
        private static readonly string[] TestBin = { "=", "!=", "-eq", "-ne", "-lt", "-gt", "-le", "-ge" };
        private static readonly string[] FileOps = { "-e", "-f", "-d", "-r", "-w", "-x", "-s", "-h", "-L" };

        private string Pick(string[] a) => a[_r.Next(a.Length)];
        private bool Chance(int pct) => _r.Next(100) < pct;

        // A word: literal / quoted / expansion / arith-sub / command-sub, by weighted choice.
        public string Word(int depth)
        {
            switch (_r.Next(depth >= 3 ? 4 : 9))
            {
                case 0: return Pick(Lits);
                case 1: return "'" + Pick(Lits) + "'";
                case 2: return "\"" + Pick(Lits) + " $" + Pick(Vars) + "\"";
                case 3: return "$" + Pick(Vars);
                case 4: return ParamExp(depth);
                case 5: return "$((" + ArithExpr(depth) + "))";
                case 6: return "$(" + Pick(Cmds) + ")";
                case 7: return "{" + Pick(Lits) + "," + Pick(Lits) + "}";   // brace tuple
                default: return "$'a\\t" + Pick(Lits) + "'";                 // ANSI-C
            }
        }

        // ${VAR<op>} — the op's {0} placeholder (if any) recursively takes a word.
        public string ParamExp(int depth)
        {
            string op = Pick(ScalarOps);
            string arg = op.Contains("{0}") ? (depth < 3 && Chance(40) ? Word(depth + 1) : Pick(Lits)) : "";
            string suffix = op.Contains("{0}") ? string.Format(op, arg) : op;
            // Occasionally an array subscript form.
            if (Chance(15)) return "${" + Pick(Vars) + "[" + (Chance(50) ? "@" : _r.Next(3).ToString()) + "]}";
            return "${" + Pick(Vars) + suffix + "}";
        }

        public string ArithExpr(int depth)
        {
            if (depth >= 3 || Chance(40)) return Chance(50) ? _r.Next(100).ToString() : Pick(Vars);
            return ArithExpr(depth + 1) + " " + Pick(ArithOps) + " " + ArithExpr(depth + 1);
        }

        public string SimpleCmd(int depth)
        {
            var sb = new System.Text.StringBuilder(Pick(Cmds));
            int args = _r.Next(0, 3);
            for (int i = 0; i < args; i++) { sb.Append(' '); sb.Append(Word(depth)); }
            if (Chance(20)) sb.Append(Chance(50) ? " > /dev/null" : " 2>&1");
            return sb.ToString();
        }

        public string Pipeline(int depth)
        {
            var sb = new System.Text.StringBuilder(SimpleCmd(depth));
            int stages = _r.Next(0, 3);
            for (int i = 0; i < stages; i++) { sb.Append(" | "); sb.Append(SimpleCmd(depth)); }
            return sb.ToString();
        }

        // A single test OPERAND must be a non-empty word — a bare/empty operand
        // (`[ -f  ]`, `[ $x != ]`) is a bash syntax error, not valid input to fuzz.
        private static readonly string[] NonEmptyLits = { "a", "hello", "bar", "1", "file.txt" };
        public string TestOperand() => Chance(50) ? "\"$" + Pick(Vars) + "\"" : "'" + Pick(NonEmptyLits) + "'";

        public string Test(int depth)
        {
            bool ext = Chance(40);
            string open = ext ? "[[ " : "[ ", close = ext ? " ]]" : " ]";
            switch (_r.Next(4))
            {
                case 0: return open + Pick(FileOps) + " " + TestOperand() + close;
                case 1: return open + (Chance(50) ? "-z" : "-n") + " \"$" + Pick(Vars) + "\"" + close;
                case 2: return open + "\"$" + Pick(Vars) + "\" " + Pick(TestBin) + " " + TestOperand() + close;
                default: // POSIX combinator / extended logical
                    string j = ext ? (Chance(50) ? "&&" : "||") : (Chance(50) ? "-a" : "-o");
                    return open + Pick(FileOps) + " " + TestOperand() + " " + j + " " +
                           Pick(FileOps) + " " + TestOperand() + close;
            }
        }

        public string Stmt(int depth)
        {
            if (depth >= 3) return Pipeline(depth);
            switch (_r.Next(11))
            {
                case 0: case 1: case 2: return Pipeline(depth);
                case 3: return SimpleCmd(depth) + (Chance(50) ? " && " : " || ") + SimpleCmd(depth);
                case 4: return Pick(Vars) + "=" + Word(depth);
                case 5: return Test(depth);
                case 6: return "if " + Test(depth) + "; then " + Stmt(depth + 1) + "; fi";
                case 7: return "for " + Pick(Vars) + " in " + Word(depth) + " " + Word(depth) +
                               "; do " + Stmt(depth + 1) + "; done";
                case 8: return "while " + Test(depth) + "; do " + Stmt(depth + 1) + "; done";
                case 9: return "case $" + Pick(Vars) + " in " + Pick(Lits) + ") " + Stmt(depth + 1) + ";; esac";
                default: return "echo " + Word(depth) + " " + Word(depth);
            }
        }
    }
}
