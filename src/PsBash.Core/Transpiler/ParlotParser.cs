using Parlot;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace PsBash.Core.Transpiler;

public sealed record WordNode(string Value);

public static class ParlotParser
{
    private static readonly Parser<WordNode> Word =
        Terms.NonWhiteSpace().Then<WordNode>(static span => new WordNode(span.ToString()));

    public static WordNode? ParseWord(string input)
    {
        return Word.Parse(input);
    }
}
