using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Core.Indexing;

public sealed class MerkleNode
{
    private MerkleNode(
        SourceUnitKind kind,
        string identity,
        string hash,
        SourceUnit? unit,
        IReadOnlyList<MerkleNode> children)
    {
        Kind = kind;
        Identity = identity;
        Hash = hash;
        Unit = unit;
        Children = children;
    }

    public SourceUnitKind Kind { get; }

    public string Identity { get; }

    public string Hash { get; }

    public SourceUnit? Unit { get; }

    public IReadOnlyList<MerkleNode> Children { get; }

    public bool IsLeaf => Children.Count == 0;

    public static MerkleNode Leaf(SourceUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var hash = CanonicalHash.Compute(
            "merkle/unit/v1",
            unit.Kind.ToString(),
            unit.Identity,
            unit.ContentHash,
            unit.SemanticSignature);
        return new MerkleNode(unit.Kind, unit.Identity, hash, unit, []);
    }

    public static MerkleNode Branch(
        SourceUnitKind kind,
        string identity,
        IEnumerable<MerkleNode> children)
        => Branch(kind, identity, unit: null, children);

    internal static MerkleNode Branch(SourceUnit unit, IEnumerable<MerkleNode> children) =>
        Branch(unit.Kind, unit.Identity, unit, children);

    private static MerkleNode Branch(
        SourceUnitKind kind,
        string identity,
        SourceUnit? unit,
        IEnumerable<MerkleNode> children)
    {
        var ordered = children
            .OrderBy(child => child.Identity, StringComparer.Ordinal)
            .ToArray();
        var fields = new List<string>(3 + ordered.Length * 2)
        {
            "merkle/node/v1",
            kind.ToString(),
            identity
        };

        if (unit is not null)
        {
            fields.Add(unit.ContentHash);
            fields.Add(unit.SemanticSignature);
        }

        foreach (var child in ordered)
        {
            fields.Add(child.Identity);
            fields.Add(child.Hash);
        }

        return new MerkleNode(kind, identity, CanonicalHash.Compute([.. fields]), unit, ordered);
    }
}

public sealed class MerkleIndex
{
    private MerkleIndex(MerkleNode root)
    {
        Root = root;
    }

    public const int SchemaVersion = CanonicalHash.SchemaVersion;

    public const string HashAlgorithm = CanonicalHash.Algorithm;

    public MerkleNode Root { get; }

    public static MerkleIndex Build(
        IEnumerable<SourceUnit> units,
        IEnumerable<ImpactEdge>? edges = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        Dictionary<string, SourceUnit> unitByIdentity;
        try
        {
            unitByIdentity = units.ToDictionary(unit => unit.Identity, StringComparer.Ordinal);
        }
        catch (ArgumentException error)
        {
            throw new AnalysisException(
                "DuplicateUnitIdentity",
                "The adapter returned more than one source unit with the same stable identity.",
                error);
        }
        var containmentEdges = (edges ?? [])
            .Where(edge => edge.Kind == EvidenceKind.Containment)
            .Distinct()
            .ToArray();
        var unknownEdge = containmentEdges.FirstOrDefault(edge =>
            !unitByIdentity.ContainsKey(edge.SourceIdentity) ||
            !unitByIdentity.ContainsKey(edge.TargetIdentity));
        if (unknownEdge is not null)
        {
            throw new AnalysisException(
                "UnknownContainmentUnit",
                "A containment edge references a source unit that is absent from the adapter index.");
        }

        var multipleParent = containmentEdges
            .GroupBy(edge => edge.SourceIdentity, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(edge => edge.TargetIdentity).Distinct(StringComparer.Ordinal).Count() > 1);
        if (multipleParent is not null)
        {
            throw new AnalysisException(
                "MultipleContainmentParents",
                $"Source unit '{multipleParent.Key}' has more than one containment parent.");
        }

        var childrenByParent = containmentEdges
            .GroupBy(edge => edge.TargetIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.SourceIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(identity => identity, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        ValidateAcyclicContainment(unitByIdentity, childrenByParent);
        var contained = childrenByParent.Values
            .SelectMany(identity => identity)
            .ToHashSet(StringComparer.Ordinal);
        var roots = unitByIdentity.Keys
            .Where(identity => !contained.Contains(identity))
            .GroupBy(LanguageIdentity, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => MerkleNode.Branch(
                SourceUnitKind.Language,
                $"language:{group.Key}",
                group.OrderBy(identity => identity, StringComparer.Ordinal)
                    .Select(identity => BuildNode(identity, unitByIdentity, childrenByParent, []))))
            .ToArray();
        return new MerkleIndex(MerkleNode.Branch(SourceUnitKind.Repository, "repository", roots));
    }

    public static IReadOnlyList<ChangedUnit> Compare(MerkleIndex baseline, MerkleIndex candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (StringComparer.Ordinal.Equals(baseline.Root.Hash, candidate.Root.Hash))
        {
            return [];
        }

        var changes = new List<ChangedUnit>();
        CompareNodes(baseline.Root, candidate.Root, changes);
        return [.. changes.OrderBy(change => change.Identity, StringComparer.Ordinal)];
    }

    private static void CompareNodes(MerkleNode baseline, MerkleNode candidate, List<ChangedUnit> changes)
    {
        if (StringComparer.Ordinal.Equals(baseline.Hash, candidate.Hash))
        {
            return;
        }

        if (baseline.Unit is not null && candidate.Unit is not null &&
            (!StringComparer.Ordinal.Equals(baseline.Unit.ContentHash, candidate.Unit.ContentHash) ||
             !StringComparer.Ordinal.Equals(baseline.Unit.SemanticSignature, candidate.Unit.SemanticSignature)))
        {
            changes.Add(new ChangedUnit(candidate.Identity, candidate.Kind, ChangeKind.Modified, false));
        }

        if (baseline.IsLeaf && candidate.IsLeaf)
        {
            return;
        }

        var baselineChildren = baseline.Children.ToDictionary(child => child.Identity, StringComparer.Ordinal);
        var candidateChildren = candidate.Children.ToDictionary(child => child.Identity, StringComparer.Ordinal);

        foreach (var identity in baselineChildren.Keys
                     .Concat(candidateChildren.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var hasBaseline = baselineChildren.TryGetValue(identity, out var baselineChild);
            var hasCandidate = candidateChildren.TryGetValue(identity, out var candidateChild);

            if (!hasBaseline)
            {
                AddUnits(candidateChild!, ChangeKind.Added, changes);
            }
            else if (!hasCandidate)
            {
                AddUnits(baselineChild!, ChangeKind.Deleted, changes);
            }
            else
            {
                CompareNodes(baselineChild!, candidateChild!, changes);
            }
        }
    }

    private static void AddUnits(MerkleNode node, ChangeKind kind, List<ChangedUnit> changes)
    {
        if (node.Unit is not null)
        {
            changes.Add(new ChangedUnit(node.Identity, node.Kind, kind, false));
        }

        foreach (var child in node.Children)
        {
            AddUnits(child, kind, changes);
        }
    }

    private static MerkleNode BuildNode(
        string identity,
        IReadOnlyDictionary<string, SourceUnit> units,
        IReadOnlyDictionary<string, string[]> childrenByParent,
        HashSet<string> ancestors)
    {
        if (!ancestors.Add(identity))
        {
            throw new AnalysisException("ContainmentCycle", $"The source-unit containment graph contains a cycle at '{identity}'.");
        }

        var unit = units[identity];
        if (!childrenByParent.TryGetValue(identity, out var childIdentities) || childIdentities.Length == 0)
        {
            ancestors.Remove(identity);
            return MerkleNode.Leaf(unit);
        }

        var children = childIdentities
            .Select(child => BuildNode(child, units, childrenByParent, ancestors))
            .ToArray();
        ancestors.Remove(identity);
        return MerkleNode.Branch(unit, children);
    }

    private static void ValidateAcyclicContainment(
        IReadOnlyDictionary<string, SourceUnit> units,
        IReadOnlyDictionary<string, string[]> childrenByParent)
    {
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in units.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            Visit(identity, childrenByParent, complete, []);
        }
    }

    private static void Visit(
        string identity,
        IReadOnlyDictionary<string, string[]> childrenByParent,
        HashSet<string> complete,
        HashSet<string> path)
    {
        if (complete.Contains(identity))
        {
            return;
        }

        if (!path.Add(identity))
        {
            throw new AnalysisException(
                "ContainmentCycle",
                $"The source-unit containment graph contains a cycle at '{identity}'.");
        }

        if (childrenByParent.TryGetValue(identity, out var children))
        {
            foreach (var child in children)
            {
                Visit(child, childrenByParent, complete, path);
            }
        }

        path.Remove(identity);
        complete.Add(identity);
    }

    private static string LanguageIdentity(string identity)
    {
        var separator = identity.IndexOf(':');
        return separator > 0 ? identity[..separator] : "generic";
    }
}
