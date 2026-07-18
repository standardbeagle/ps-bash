namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// One command-aware output filter. The reduction pipeline applies stages in a fixed
/// order (matchOutput short-circuit → replace → strip/trim → skip/keep → dedup →
/// success/failure template) — see <see cref="FilterStage"/>. Specs are plain data:
/// hand-built in tests, or loaded from JSON via <see cref="FilterJson"/>.
/// </summary>
/// <remarks>
/// Collection properties null-coalesce in their init setters: System.Text.Json's
/// source generator sets an <em>omitted</em> JSON property to <c>null</c> rather than
/// honoring the field initializer, so without this guard a spec parsed from
/// <c>{ "name": …, "match": … }</c> would carry null lists and NRE in the pipeline.
/// </remarks>
public sealed record FilterSpec
{
    /// <summary>Stable identifier (e.g. <c>git/status</c>). Higher-precedence specs shadow same-named ones.</summary>
    public required string Name { get; init; }

    /// <summary>Which command this filter claims.</summary>
    public required FilterMatch Match { get; init; }

    /// <summary>
    /// Optional reduced argv to run instead of the user's command (e.g.
    /// <c>git status --porcelain</c>). A hint for the launcher (P4); the pure engine
    /// does not execute it. Genuinely nullable — absence means "no override".
    /// </summary>
    public IReadOnlyList<string>? Override { get; init; }

    private readonly IReadOnlyList<MatchOutputRule> _matchOutput = [];
    /// <summary>Whole-output substring checks; first hit short-circuits and emits its template.</summary>
    public IReadOnlyList<MatchOutputRule> MatchOutput { get => _matchOutput; init => _matchOutput = value ?? []; }

    private readonly IReadOnlyList<ReplaceRule> _replace = [];
    /// <summary>Per-line regex transforms, applied in order.</summary>
    public IReadOnlyList<ReplaceRule> Replace { get => _replace; init => _replace = value ?? []; }

    public bool StripAnsi { get; init; }
    public bool TrimLines { get; init; }

    private readonly IReadOnlyList<string> _skip = [];
    /// <summary>Drop lines matching any of these regexes.</summary>
    public IReadOnlyList<string> Skip { get => _skip; init => _skip = value ?? []; }

    private readonly IReadOnlyList<string> _keep = [];
    /// <summary>Allow-list: when non-empty, keep only lines matching at least one regex.</summary>
    public IReadOnlyList<string> Keep { get => _keep; init => _keep = value ?? []; }

    public bool Dedup { get; init; }

    /// <summary>Template rendered when exit code == 0. <c>{{body}}</c> = processed lines. Null = emit body as-is.</summary>
    public string? OnSuccess { get; init; }

    /// <summary>Template rendered when exit code != 0 (or timeout). Null = emit body as-is.</summary>
    public string? OnFailure { get; init; }
}

/// <summary>Command selector: command name + an ordered argv prefix that must match.</summary>
public sealed record FilterMatch
{
    public required string Command { get; init; }

    /// <summary>
    /// Optional opaque route selected from a parsed command shape. Route matching is
    /// independent of the display command, which remains the user's full source text.
    /// </summary>
    public string? RouteKey { get; init; }

    private readonly IReadOnlyList<string> _args = [];
    /// <summary>Argv prefix that must appear in order (e.g. <c>["status"]</c> for <c>git status</c>).</summary>
    public IReadOnlyList<string> Args { get => _args; init => _args = value ?? []; }
}

/// <summary>Whole-output substring rule: if the output contains <see cref="Contains"/>, emit <see cref="Emit"/>.</summary>
public sealed record MatchOutputRule
{
    public required string Contains { get; init; }
    public required string Emit { get; init; }
}

/// <summary>Per-line regex replacement.</summary>
public sealed record ReplaceRule
{
    public required string Pattern { get; init; }
    public string With { get; init; } = "";
}
