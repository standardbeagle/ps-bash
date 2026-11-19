using System.Collections.Generic;
using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Every stdin feed (a pipe, a here-document, a here-string, <c>&lt; file</c>) reaches its command as the next pipe
/// stage, so a script block the command's emission wraps it in (an RC-7 splat hoist, with or without an env prefix)
/// must forward the block's <c>$input</c> (<see cref="PsBuild.ForwardedStageInput"/>). The pipe set that flag
/// (<c>WithPipeStageFeed</c>); the here-document and <c>&lt; file</c> paths did not, so <c>g=-; cat $g &lt;&lt;EOF</c> and
/// <c>g=-; cat $g &lt; f</c> read nothing, and <c>$SH &gt;stdout &lt;&lt;'EOF'</c> ran an empty script (oils xtrace spec
/// "xtrace with unprintable chars"). A new feed that skips the flag fails here for every wrapper at once.
/// <para>ps-bash-specific (the emitted shape); the behavior against bash is
/// <c>RunTimeCommandNameTests.HereDocument_ReachesACommandInsideAWrapper</c>.</para>
/// </summary>
public class StdinFeedWrapperGuardTests
{
    /// <summary>Each feed, <c>{0}</c> being the command.</summary>
    public static readonly string[] Feeds =
    {
        "printf 'x\\n' | {0}",
        "{0} <<EOF\nx\nEOF",
        "{0} <<'EOF'\nx\nEOF",
        "{0} <<< x",
        "{0} < f",
    };

    /// <summary>Commands whose emission wraps them in a script block.</summary>
    public static readonly string[] WrappedCommands =
    {
        "g=-; cat $g",
        "g=-; FOO=1 cat $g",
        "g='-n -'; cat $g",
    };

    public static IEnumerable<object[]> FeedByCommand()
    {
        foreach (var feed in Feeds)
            foreach (var command in WrappedCommands)
                yield return new object[] { feed, command };
    }

    [Theory]
    [MemberData(nameof(FeedByCommand))]
    public void Transpile_FedCommandInAWrapper_ForwardsTheStageInput(string feed, string command)
    {
        // The assignment (if any) is its own statement before the fed command.
        var split = command.LastIndexOf("; ", System.StringComparison.Ordinal);
        var prefix = split < 0 ? "" : command[..(split + 2)];
        var bash = prefix + string.Format(feed, split < 0 ? command : command[(split + 2)..]);

        var ps = PsEmitter.Transpile(bash)!;

        Assert.Contains(PsBuild.ForwardedStageInput, ps);
    }
}
