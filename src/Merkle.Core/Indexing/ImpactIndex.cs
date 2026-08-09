using Merkle.Core.Adapters;
using Merkle.Core.Domain;

namespace Merkle.Core.Indexing;

public sealed record ImpactEdge(string SourceIdentity, string TargetIdentity, EvidenceKind Kind);

public sealed class ImpactIndex
{
    private const int MaxReasonsPerTest = 100;
    private const int MaxEdges = 1_000_000;
    private const int MaxTraversalStates = 100_000;
    private readonly IReadOnlyDictionary<string, ImpactEdge[]> _outgoing;

    public ImpactIndex(IEnumerable<ImpactEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var materialized = edges.Distinct().Take(MaxEdges + 1).ToArray();
        if (materialized.Length > MaxEdges)
        {
            throw new ArgumentException($"An impact index may contain at most {MaxEdges} edges.", nameof(edges));
        }

        _outgoing = materialized
            .GroupBy(edge => edge.SourceIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(edge => edge.TargetIdentity, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Kind)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    public MappingResult FindRequestedTests(
        IReadOnlyList<ChangedUnit> changedUnits,
        IReadOnlyDictionary<string, TestDescriptor> tests)
    {
        var requested = new Dictionary<string, List<ImpactReason>>(StringComparer.Ordinal);
        var unmapped = new List<ChangedUnit>();
        var truncated = false;

        foreach (var changed in changedUnits.OrderBy(unit => unit.Identity, StringComparer.Ordinal))
        {
            var found = Traverse(changed, tests, requested, ref truncated);
            if (!found)
            {
                unmapped.Add(changed);
            }
        }

        var mappedTests = requested
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var descriptor = tests[pair.Key];
                return new RequestedTest(
                    descriptor.Identity,
                    descriptor.DisplayName,
                    descriptor.Framework,
                    pair.Value,
                    Mandatory: true);
            })
            .ToArray();

        return new MappingResult(
            mappedTests,
            unmapped,
            truncated ? ["Explanation paths were truncated at the internal safety limit."] : []);
    }

    private bool Traverse(
        ChangedUnit changed,
        IReadOnlyDictionary<string, TestDescriptor> tests,
        Dictionary<string, List<ImpactReason>> requested,
        ref bool truncated)
    {
        var found = false;
        var queue = new Queue<(string Identity, string[] Path)>();
        queue.Enqueue((changed.Identity, [changed.Identity]));
        var traversalStates = 1;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!_outgoing.TryGetValue(current.Identity, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing)
            {
                if (current.Path.Contains(edge.TargetIdentity, StringComparer.Ordinal))
                {
                    continue;
                }

                var path = current.Path.Append(edge.TargetIdentity).ToArray();
                if (tests.ContainsKey(edge.TargetIdentity))
                {
                    found = true;
                    if (!requested.TryGetValue(edge.TargetIdentity, out var reasons))
                    {
                        reasons = [];
                        requested.Add(edge.TargetIdentity, reasons);
                    }

                    if (reasons.Count < MaxReasonsPerTest &&
                        !reasons.Any(reason => reason.Path.SequenceEqual(path, StringComparer.Ordinal)))
                    {
                        reasons.Add(new ImpactReason(edge.Kind, changed.Identity, path));
                    }
                    else
                    {
                        truncated = true;
                    }

                    continue;
                }

                if (traversalStates < MaxTraversalStates)
                {
                    queue.Enqueue((edge.TargetIdentity, path));
                    traversalStates++;
                }
                else
                {
                    truncated = true;
                }
            }
        }

        return found;
    }
}
