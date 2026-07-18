using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Conservatively identifies command chains that may be presented as one compact action.
/// This classifier is deliberately independent of parsing and emission.
/// </summary>
public static class CompactCommandChain
{
    public const string GitStageCommitPushRoute = "git.stage-commit-push.v1";
    public const string GitStageCommitPushSummary = "Stage changes, create a commit, and push it.";

    public sealed record Classification(string RouteKey, string ActionSummary);

    /// <summary>
    /// Recognizes the exact, literal <c>git add ... &amp;&amp; git commit ... &amp;&amp; git push ...</c>
    /// shape. Anything requiring shell evaluation is intentionally rejected.
    /// </summary>
    public static bool TryClassify(Command.AndOrList chain, out Classification? classification)
    {
        classification = null;

        if (chain.Commands.Length != 3 || chain.Ops.Length != 2 ||
            chain.Ops[0] != "&&" || chain.Ops[1] != "&&")
            return false;

        if (!TryGetLiteralWords(chain.Commands[0], out var add) ||
            !TryGetLiteralWords(chain.Commands[1], out var commit) ||
            !TryGetLiteralWords(chain.Commands[2], out var push))
            return false;

        if (!HasPrefixAndOperand(add, "git", "add") ||
            !HasPrefixAndOperand(commit, "git", "commit") ||
            !HasExactPrefix(push, "git", "push"))
            return false;

        classification = new Classification(GitStageCommitPushRoute, GitStageCommitPushSummary);
        return true;
    }

    private static bool TryGetLiteralWords(Command command, out string[] words)
    {
        words = [];
        if (command is not Command.Simple simple ||
            simple.EnvPairs.Length != 0 || simple.Redirects.Length != 0 ||
            (!simple.HereDocs.IsDefault && simple.HereDocs.Length != 0) ||
            simple.Words.Length == 0)
            return false;

        words = new string[simple.Words.Length];
        for (var i = 0; i < simple.Words.Length; i++)
        {
            var parts = simple.Words[i].Parts;
            if (parts.Length != 1 || parts[0] is not WordPart.Literal literal)
                return false;
            words[i] = literal.Value;
        }

        return true;
    }

    private static bool HasPrefixAndOperand(string[] words, string command, string subcommand) =>
        words.Length >= 3 && words[0] == command && words[1] == subcommand;

    private static bool HasExactPrefix(string[] words, string command, string subcommand) =>
        words.Length >= 2 && words[0] == command && words[1] == subcommand;
}
