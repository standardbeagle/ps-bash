using System.Text;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Walks parsed AST nodes and emits equivalent PowerShell strings.
/// Currently handles SimpleCommand only; other command types are not yet supported.
/// </summary>
public static class PsEmitter
{
    /// <summary>
    /// Emit PowerShell for the given command AST node.
    /// </summary>
    public static string Emit(Command cmd) => cmd switch
    {
        Command.Simple simple => EmitSimple(simple),
        Command.Pipeline => throw new NotSupportedException("Pipeline commands are not yet supported."),
        Command.AndOrList => throw new NotSupportedException("AndOr commands are not yet supported."),
        Command.CommandList => throw new NotSupportedException("CommandList commands are not yet supported."),
        Command.ShAssignment => throw new NotSupportedException("ShAssignment commands are not yet supported."),
        _ => throw new NotSupportedException($"Unknown command type: {cmd.GetType().Name}"),
    };

    /// <summary>
    /// Parse bash input and emit equivalent PowerShell.
    /// Returns null if the input is empty or whitespace-only.
    /// </summary>
    public static string? Transpile(string bash)
    {
        var cmd = BashParser.Parse(bash);
        if (cmd is null)
            return null;
        return Emit(cmd);
    }

    private static string EmitSimple(Command.Simple cmd)
    {
        var sb = new StringBuilder();

        foreach (var envPair in cmd.EnvPairs)
        {
            sb.Append("$env:");
            sb.Append(envPair.Name);
            sb.Append(" = ");
            if (envPair.Value is not null)
            {
                sb.Append('"');
                sb.Append(EmitWord(envPair.Value));
                sb.Append('"');
            }
            else
            {
                sb.Append("\"\"");
            }
            sb.Append("; ");
        }

        for (var i = 0; i < cmd.Words.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(EmitWord(cmd.Words[i]));
        }

        return sb.ToString();
    }

    private static string EmitWord(CompoundWord word)
    {
        if (word.Parts.Length == 1)
            return EmitWordPart(word.Parts[0]);

        var sb = new StringBuilder();
        foreach (var part in word.Parts)
            sb.Append(EmitWordPart(part));
        return sb.ToString();
    }

    private static string EmitWordPart(WordPart part) => part switch
    {
        WordPart.Literal lit => lit.Value,
        WordPart.EscapedLiteral el => $"`{el.Value}",
        WordPart.SingleQuoted sq => $"'{sq.Value}'",
        WordPart.DoubleQuoted dq => EmitDoubleQuoted(dq),
        WordPart.SimpleVarSub vs => EmitSimpleVar(vs.Name),
        WordPart.BracedVarSub bvs => $"$env:{bvs.Name}",
        WordPart.CommandSub => throw new NotSupportedException("Command substitution is not yet supported."),
        WordPart.TildeSub ts => ts.User is null ? "$HOME" : throw new NotSupportedException("~user substitution is not yet supported."),
        _ => throw new NotSupportedException($"Unknown word part type: {part.GetType().Name}"),
    };

    private static string EmitSimpleVar(string name) => name switch
    {
        "null" or "true" or "false" or "HOME" or "LASTEXITCODE" => $"${name}",
        _ => $"$env:{name}",
    };

    private static string EmitDoubleQuoted(WordPart.DoubleQuoted dq)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var part in dq.Parts)
            sb.Append(EmitWordPart(part));
        sb.Append('"');
        return sb.ToString();
    }
}
