using PsBash.Host.Shell;

namespace PsBash.Shell.Tests;

/// <summary>
/// Test-only projections from <see cref="CompletionItem"/> back to flat string lists, so the
/// many existing string-based assertions stay readable. <c>Texts()</c> is what gets inserted;
/// <c>Labels()</c> is what the completion list shows.
/// </summary>
internal static class CompletionItemTestExtensions
{
    public static IReadOnlyList<string> Texts(this IReadOnlyList<CompletionItem> items)
        => items.Select(i => i.InsertText).ToList();

    public static IReadOnlyList<string> Labels(this IReadOnlyList<CompletionItem> items)
        => items.Select(i => i.DisplayText).ToList();
}
