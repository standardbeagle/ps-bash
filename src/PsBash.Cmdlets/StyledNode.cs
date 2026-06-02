using System.Management.Automation;
using Strata;
using Strata.Interaction;

namespace PsBash.Cmdlets;

/// <summary>
/// A mutable Strata tree node for the interactive <c>Show-Styled</c> viewer. Unlike the static
/// <c>Format-Styled</c> path (which wraps PSObjects through the immutable
/// <c>PsObjectTreeAdapter</c>), the interactive projection needs <b>stable reference identity</b>
/// across re-cascades (so <c>TerminalGuiProjection</c> reconciles the same View in place) and
/// <b>mutable pseudo-state</b> (so <c>:focused</c> / <c>:expanded</c> can toggle at runtime). This
/// is the authoring contract from Strata's <c>docs/06-stateful-projection.md</c>, mirrored by the
/// <c>Show-Processes</c> sample's <c>ProcessNode</c>.
/// </summary>
internal sealed class StyledNode : ITreeNode, IPseudoStateMutable
{
    private readonly List<StyledNode> _children = new();
    private readonly HashSet<string> _pseudoStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _attributes = new(StringComparer.Ordinal);

    public StyledNode(string kind, string? id = null, IEnumerable<string>? classes = null)
    {
        Kind = kind;
        Id = id;
        Classes = (classes ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
    }

    public string Kind { get; }

    public string? Id { get; }

    public IReadOnlySet<string> Classes { get; }

    public IReadOnlySet<string> PseudoStates => _pseudoStates;

    public bool AddPseudoState(string state) => _pseudoStates.Add(state);

    public bool RemovePseudoState(string state) => _pseudoStates.Remove(state);

    public ITreeNode? Parent { get; private set; }

    public IEnumerable<ITreeNode> Children => _children;

    /// <summary>The children as concrete nodes (for host-side wiring: focus ring, expand targets).</summary>
    public IReadOnlyList<StyledNode> ChildNodes => _children;

    /// <summary>The source PSObject this node was built from, when it wraps one (Row nodes).</summary>
    public PSObject? Source { get; init; }

    public object? Underlying => (object?)Source ?? this;

    public StyledNode Add(StyledNode child)
    {
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// <summary>Replace this node's children wholesale (used when toggling a Row's expanded detail).</summary>
    public void SetChildren(IEnumerable<StyledNode> children)
    {
        _children.Clear();
        foreach (var c in children)
        {
            c.Parent = this;
            _children.Add(c);
        }
    }

    public void SetAttribute(string name, object? value) => _attributes[name] = value;

    public bool TryGetAttribute(string name, out object? value)
        => _attributes.TryGetValue(name, out value);
}
