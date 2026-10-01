using System.Linq;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>How hard <c>rm -r</c> protects the filesystem root (<c>--preserve-root[=all]</c> / <c>--no-preserve-root</c>).</summary>
internal enum RmRootPolicy
{
    /// <summary><c>--no-preserve-root</c>: GNU would remove <c>/</c>. ps-bash's own protected-path guard still refuses.</summary>
    None,

    /// <summary>The GNU default and <c>--preserve-root</c>: refuse a recursive removal of <c>/</c>.</summary>
    Root,

    /// <summary><c>--preserve-root=all</c>: also refuse a directory operand on another device than its parent.</summary>
    All,
}

/// <summary>
/// Pure resolution of rm's root-protection options, in command-line order (the last one wins), plus the
/// GNU wording. Oracle: coreutils 9.4 — a typed abbreviation of <c>--no-preserve-root</c> is refused
/// outright ("you may not abbreviate the --no-preserve-root option"), the argument of
/// <c>--preserve-root</c> must be exactly <c>all</c>.
/// </summary>
internal static class RmRootPolicyResolver
{
    public const string OptPreserveRoot = "preserve-root", OptNoPreserveRoot = "no-preserve-root";

    public static bool TryResolve(ParsedArgs parsed, out RmRootPolicy policy, out string? error)
    {
        policy = RmRootPolicy.Root;
        error = null;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            if (t.OptId == OptNoPreserveRoot)
            {
                if (t.Raw != "--no-preserve-root")
                {
                    error = "rm: you may not abbreviate the --no-preserve-root option";
                    return false;
                }
                policy = RmRootPolicy.None;
            }
            else if (t.OptId == OptPreserveRoot)
            {
                if (t.Value is null) policy = RmRootPolicy.Root;
                else if (t.Value == "all") policy = RmRootPolicy.All;
                else
                {
                    error = $"rm: unrecognized --preserve-root argument: '{t.Value}'";
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>GNU's two-line refusal of a recursive removal of the root; <c>(same as '/')</c> is added
    /// when the operand was not literally <c>/</c> (<c>//</c>, <c>C:\</c>). A run of three or more
    /// slashes is just <c>/</c> to fts, so it prints as such.</summary>
    public static string DangerousMessage(string display)
    {
        var shown = display.Length > 0 && display.All(c => c == '/') && display.Length != 2 ? "/" : display;
        return $"rm: it is dangerous to operate recursively on '{shown}'"
            + (shown == "/" ? "" : " (same as '/')")
            + "\nrm: use --no-preserve-root to override this failsafe";
    }

    /// <summary>The refusal of <c>--preserve-root=all</c> for a directory on another device.</summary>
    public static string DifferentDeviceMessage(string display) =>
        $"rm: skipping '{display}', since it's on a different device\nrm: and --preserve-root=all is in effect";

    /// <summary>GNU never removes <c>.</c> / <c>..</c> (or <c>dir/.</c>): the last path component decides.</summary>
    public static bool IsDotOrDotDot(string typed)
    {
        var trimmed = typed.TrimEnd('/', '\\');
        var leaf = trimmed.Length == 0 ? "" : trimmed[(trimmed.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        return leaf is "." or "..";
    }
}
