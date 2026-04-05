using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Hand-rolled recursive descent parser for bash input.
/// Consumes the flat token list produced by <see cref="BashLexer"/> and builds an AST.
/// </summary>
public sealed class BashParser
{
    private readonly List<BashToken> _tokens;
    private int _pos;

    private BashParser(List<BashToken> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    /// <summary>
    /// Parse the given bash input into an AST command node.
    /// Returns null if the input is empty or contains only whitespace/comments.
    /// </summary>
    public static Command? Parse(string input)
    {
        var tokens = BashLexer.Tokenize(input);
        var parser = new BashParser(tokens);
        return parser.ParseCommand();
    }

    private BashToken Peek() => _tokens[_pos];

    private BashToken Advance()
    {
        var token = _tokens[_pos];
        _pos++;
        return token;
    }

    private Command? ParseCommand()
    {
        SkipNewlines();

        if (Peek().Kind == BashTokenKind.Eof)
            return null;

        return ParseList();
    }

    private Command ParseList()
    {
        var first = ParseAndOr();

        if (Peek().Kind != BashTokenKind.Semi)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        commands.Add(first);

        while (Peek().Kind == BashTokenKind.Semi)
        {
            Advance(); // consume ;
            SkipNewlines();

            if (Peek().Kind == BashTokenKind.Eof)
                break;

            commands.Add(ParseAndOr());
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    private Command ParseAndOr()
    {
        var first = ParsePipeline();

        if (Peek().Kind is not BashTokenKind.AndIf and not BashTokenKind.OrIf)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.AndIf or BashTokenKind.OrIf)
        {
            var opToken = Advance();
            ops.Add(opToken.Value);
            commands.Add(ParsePipeline());
        }

        return new Command.AndOrList(commands.ToImmutable(), ops.ToImmutable());
    }

    private Command ParsePipeline()
    {
        var first = ParseSimpleCommand();
        if (Peek().Kind != BashTokenKind.Pipe)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind == BashTokenKind.Pipe)
        {
            Advance(); // consume |

            // |& is Pipe followed by Amp -- means stderr-merge pipe
            if (Peek().Kind == BashTokenKind.Amp)
            {
                Advance(); // consume &
                ops.Add("|&");
            }
            else
            {
                ops.Add("|");
            }

            commands.Add(ParseSimpleCommand());
        }

        return new Command.Pipeline(commands.ToImmutable(), ops.ToImmutable(), Negated: false);
    }

    private Command ParseSimpleCommand()
    {
        // Check for "export" keyword followed by assignment words.
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "export")
        {
            int saved = _pos;
            Advance(); // consume "export"

            if (Peek().Kind == BashTokenKind.AssignmentWord)
            {
                var pairs = ImmutableArray.CreateBuilder<Assignment>();
                while (Peek().Kind == BashTokenKind.AssignmentWord)
                    pairs.Add(ParseAssignmentWord());
                return new Command.ShAssignment(pairs.ToImmutable());
            }

            // Not followed by assignment — rewind and parse as normal command.
            _pos = saved;
        }

        // Collect leading assignment words (VAR=val ...).
        var assignments = ImmutableArray.CreateBuilder<EnvPair>();
        while (Peek().Kind == BashTokenKind.AssignmentWord)
        {
            var (name, value) = SplitAssignmentWord(Advance().Value);
            assignments.Add(new EnvPair(name, value));
        }

        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        var redirects = ImmutableArray.CreateBuilder<Redirect>();

        while (true)
        {
            var kind = Peek().Kind;

            if (kind == BashTokenKind.Word)
            {
                var token = Advance();
                var parts = DecomposeWord(token.Value);
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.IoNumber || IsRedirectOp(kind))
            {
                redirects.Add(ParseRedirect());
            }
            else
            {
                break;
            }
        }

        // If only assignments and no command words, it's a bare assignment.
        if (assignments.Count > 0 && words.Count == 0 && redirects.Count == 0)
        {
            var pairs = assignments.Select(
                e => new Assignment(e.Name, AssignOp.Equal, e.Value)).ToImmutableArray();
            return new Command.ShAssignment(pairs);
        }

        return new Command.Simple(
            words.ToImmutable(),
            assignments.ToImmutable(),
            redirects.ToImmutable());
    }

    private Assignment ParseAssignmentWord()
    {
        var token = Advance();
        var (name, value) = SplitAssignmentWord(token.Value);
        return new Assignment(name, AssignOp.Equal, value);
    }

    private (string Name, CompoundWord? Value) SplitAssignmentWord(string raw)
    {
        int eqIndex = raw.IndexOf('=');
        string name = raw[..eqIndex];
        string valueRaw = raw[(eqIndex + 1)..];

        if (valueRaw.Length == 0)
            return (name, null);

        var parts = DecomposeWord(valueRaw);
        return (name, new CompoundWord(parts));
    }

    private Redirect ParseRedirect()
    {
        int fd = -1;

        if (Peek().Kind == BashTokenKind.IoNumber)
        {
            fd = int.Parse(Advance().Value);
        }

        var opToken = Advance();
        string op = opToken.Kind switch
        {
            BashTokenKind.Great => ">",
            BashTokenKind.DGreat => ">>",
            BashTokenKind.Less => "<",
            BashTokenKind.GreatAnd => ">&",
            BashTokenKind.LessAnd => "<&",
            BashTokenKind.DLess => "<<",
            BashTokenKind.DLessDash => "<<-",
            _ => opToken.Value,
        };

        // Default fd: 0 for input redirects, 1 for output redirects.
        if (fd == -1)
        {
            fd = op[0] == '<' ? 0 : 1;
        }

        // Consume the target word.
        var targetToken = Advance();
        var targetParts = DecomposeWord(targetToken.Value);
        var target = new CompoundWord(targetParts);

        return new Redirect(op, fd, target);
    }

    private static bool IsRedirectOp(BashTokenKind kind) =>
        kind is BashTokenKind.Great or BashTokenKind.DGreat
            or BashTokenKind.Less or BashTokenKind.GreatAnd
            or BashTokenKind.LessAnd or BashTokenKind.DLess
            or BashTokenKind.DLessDash;

    /// <summary>
    /// Sub-parse a WORD token's raw text into typed WordPart children.
    /// Handles single quotes, double quotes, backslash escapes, $VAR references,
    /// and bare literal text.
    /// </summary>
    internal static ImmutableArray<WordPart> DecomposeWord(string raw)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        int pos = 0;
        int len = raw.Length;

        // Detect tilde at the start of a word.
        if (len > 0 && raw[0] == '~')
        {
            pos = ParseTilde(raw, parts);
        }

        while (pos < len)
        {
            char c = raw[pos];

            if (c == '\'')
            {
                pos = ParseSingleQuoted(raw, pos, parts);
            }
            else if (c == '"')
            {
                pos = ParseDoubleQuoted(raw, pos, parts);
            }
            else if (c == '\\' && pos + 1 < len)
            {
                parts.Add(new WordPart.EscapedLiteral(raw[pos + 1].ToString()));
                pos += 2;
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '{')
            {
                pos = ParseBracedVar(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && IsVarStart(raw[pos + 1]))
            {
                pos = ParseSimpleVar(raw, pos, parts);
            }
            else if (c == '`')
            {
                pos = ParseBacktickCommandSub(raw, pos, parts);
            }
            else
            {
                pos = ParseBareLiteral(raw, pos, parts);
            }
        }

        return parts.ToImmutable();
    }

    private static int ParseSingleQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip opening '
        int start = pos;
        while (pos < raw.Length && raw[pos] != '\'')
            pos++;
        parts.Add(new WordPart.SingleQuoted(raw[start..pos]));
        if (pos < raw.Length)
            pos++; // skip closing '
        return pos;
    }

    private static int ParseDoubleQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip opening "
        var innerParts = ImmutableArray.CreateBuilder<WordPart>();
        int len = raw.Length;

        while (pos < len && raw[pos] != '"')
        {
            char c = raw[pos];

            if (c == '\\' && pos + 1 < len)
            {
                char next = raw[pos + 1];
                // Inside double quotes, only these characters are special after backslash:
                // $ ` " \ newline
                if (next is '$' or '`' or '"' or '\\' or '\n')
                {
                    innerParts.Add(new WordPart.Literal(next.ToString()));
                    pos += 2;
                }
                else
                {
                    // Backslash is literal when not before a special char
                    innerParts.Add(new WordPart.Literal("\\"));
                    pos++;
                }
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '{')
            {
                pos = ParseBracedVar(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && IsVarStart(raw[pos + 1]))
            {
                pos = ParseSimpleVar(raw, pos, innerParts);
            }
            else if (c == '`')
            {
                pos = ParseBacktickCommandSub(raw, pos, innerParts);
            }
            else
            {
                int start = pos;
                while (pos < len && raw[pos] != '"' && raw[pos] != '\\' && raw[pos] != '$' && raw[pos] != '`')
                    pos++;
                innerParts.Add(new WordPart.Literal(raw[start..pos]));
            }
        }

        if (pos < len)
            pos++; // skip closing "

        parts.Add(new WordPart.DoubleQuoted(innerParts.ToImmutable()));
        return pos;
    }

    private static int ParseSimpleVar(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip $
        int start = pos;
        // Special single-char variables: $? $! $# $$ $@ $* $- $0-$9
        if (pos < raw.Length && raw[pos] is '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9'))
        {
            parts.Add(new WordPart.SimpleVarSub(raw[pos].ToString()));
            return pos + 1;
        }
        // Named variable: letter/underscore followed by alnum/underscore
        while (pos < raw.Length && IsVarChar(raw[pos]))
            pos++;
        if (pos > start)
            parts.Add(new WordPart.SimpleVarSub(raw[start..pos]));
        return pos;
    }

    private static int ParseBracedVar(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 2; // skip ${
        int len = raw.Length;

        // ${#VAR} -> length operator
        if (pos < len && raw[pos] == '#')
        {
            pos++; // skip #
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];
            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(name, "#"));
            return pos;
        }

        // Read variable name
        int varStart = pos;
        while (pos < len && IsVarChar(raw[pos]))
            pos++;
        string varName = raw[varStart..pos];

        // Check for suffix operator or closing brace
        string? suffix = null;
        if (pos < len && raw[pos] != '}')
        {
            // Read operator: :-, :=, :+, :?, %, %%, #, ##
            int opStart = pos;
            if (pos < len && raw[pos] == ':' && pos + 1 < len && raw[pos + 1] is '-' or '=' or '+' or '?')
            {
                pos += 2;
            }
            else if (pos < len && raw[pos] == '%')
            {
                pos++;
                if (pos < len && raw[pos] == '%')
                    pos++;
            }
            else if (pos < len && raw[pos] == '#')
            {
                pos++;
                if (pos < len && raw[pos] == '#')
                    pos++;
            }
            string op = raw[opStart..pos];

            // Read value until closing brace
            int valStart = pos;
            int braceDepth = 1;
            while (pos < len && braceDepth > 0)
            {
                if (raw[pos] == '{') braceDepth++;
                else if (raw[pos] == '}') braceDepth--;
                if (braceDepth > 0) pos++;
            }
            string val = raw[valStart..pos];
            suffix = op + val;
        }

        if (pos < len && raw[pos] == '}')
            pos++; // skip }

        parts.Add(new WordPart.BracedVarSub(varName, suffix));
        return pos;
    }

    private static int ParseCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 2; // skip $(
        int depth = 1;
        int start = pos;
        while (pos < raw.Length && depth > 0)
        {
            if (raw[pos] == '(') depth++;
            else if (raw[pos] == ')') depth--;
            if (depth > 0) pos++;
        }
        string inner = raw[start..pos];
        if (pos < raw.Length)
            pos++; // skip closing )

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
    }

    private static int ParseBacktickCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip opening `
        int start = pos;
        while (pos < raw.Length && raw[pos] != '`')
        {
            if (raw[pos] == '\\' && pos + 1 < raw.Length)
                pos++; // skip escaped char
            pos++;
        }
        string inner = raw[start..pos];
        if (pos < raw.Length)
            pos++; // skip closing `

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
    }

    private static int ParseTilde(string raw, ImmutableArray<WordPart>.Builder parts)
    {
        int pos = 1; // skip ~
        int len = raw.Length;

        // Read optional username until '/' or end of word.
        int start = pos;
        while (pos < len && raw[pos] != '/')
            pos++;

        string? user = pos > start ? raw[start..pos] : null;
        parts.Add(new WordPart.TildeSub(user));

        // Consume the '/' separator so it doesn't appear in the following literal.
        if (pos < len && raw[pos] == '/')
            pos++;

        return pos;
    }

    private static int ParseBareLiteral(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos;
        int len = raw.Length;
        while (pos < len)
        {
            char c = raw[pos];
            if (c is '\'' or '"' or '\\' or '`')
                break;
            if (c == '$' && pos + 1 < len && (IsVarStart(raw[pos + 1]) || raw[pos + 1] == '{' || raw[pos + 1] == '('))
                break;
            pos++;
        }
        parts.Add(new WordPart.Literal(raw[start..pos]));
        return pos;
    }

    private static bool IsVarStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'
            or '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9');

    private static bool IsVarChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');

    private void SkipNewlines()
    {
        while (Peek().Kind == BashTokenKind.Newline)
            _pos++;
    }
}
