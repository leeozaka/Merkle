using Merkle.Core.Domain;
using Merkle.Core.Indexing;

namespace Merkle.Tests.Indexing;

public sealed class ImpactIndexTests
{
    [Fact]
    public void FindRequestedTests_TraversesCyclesOnceAndReturnsStablePaths()
    {
        var graph = new ImpactIndex([
            new ImpactEdge("a", "b", EvidenceKind.StaticDependency),
            new ImpactEdge("b", "a", EvidenceKind.StaticDependency),
            new ImpactEdge("b", "test:z", EvidenceKind.StaticDependency),
            new ImpactEdge("a", "test:a", EvidenceKind.DynamicObservation)]);

        var tests = graph.FindRequestedTests(
            [new ChangedUnit("a", SourceUnitKind.Member, ChangeKind.Modified, true)],
            new Dictionary<string, TestDescriptor>
            {
                ["test:z"] = new("test:z", "Z test", "xunit"),
                ["test:a"] = new("test:a", "A test", "xunit")
            });

        Assert.Equal(["test:a", "test:z"], tests.RequestedTests.Select(test => test.Identity));
        Assert.All(tests.RequestedTests, test => Assert.NotEmpty(test.Reasons));
        Assert.Empty(tests.UnmappedUnits);
    }

    [Fact]
    public void FindRequestedTests_ReportsChangedUnitsWithoutKnownRelationship()
    {
        var graph = new ImpactIndex([]);
        var changed = new ChangedUnit("file:orphan.cs", SourceUnitKind.File, ChangeKind.Added, false);

        var result = graph.FindRequestedTests([changed], new Dictionary<string, TestDescriptor>());

        Assert.Empty(result.RequestedTests);
        Assert.Equal([changed], result.UnmappedUnits);
    }

    [Fact]
    public void FindRequestedTests_RetainsDistinctDiamondPaths()
    {
        var graph = new ImpactIndex([
            new ImpactEdge("changed", "left", EvidenceKind.StaticDependency),
            new ImpactEdge("changed", "right", EvidenceKind.StaticDependency),
            new ImpactEdge("left", "shared", EvidenceKind.StaticDependency),
            new ImpactEdge("right", "shared", EvidenceKind.StaticDependency),
            new ImpactEdge("shared", "test:a", EvidenceKind.AncestorFallback)]);

        var result = graph.FindRequestedTests(
            [new ChangedUnit("changed", SourceUnitKind.Member, ChangeKind.Modified, true)],
            new Dictionary<string, TestDescriptor>
            {
                ["test:a"] = new("test:a", "A", "xunit")
            });

        Assert.Equal(2, Assert.Single(result.RequestedTests).Reasons.Count);
    }

    [Fact]
    public void FindRequestedTests_TruncatesAfterOneHundredReasonsWithoutLosingTheTest()
    {
        var edges = Enumerable.Range(0, 101)
            .SelectMany(index => new[]
            {
                new ImpactEdge("changed", $"node:{index}", EvidenceKind.StaticDependency),
                new ImpactEdge($"node:{index}", "test:a", EvidenceKind.DynamicObservation)
            });
        var graph = new ImpactIndex(edges);

        var result = graph.FindRequestedTests(
            [new ChangedUnit("changed", SourceUnitKind.Member, ChangeKind.Modified, true)],
            new Dictionary<string, TestDescriptor> { ["test:a"] = new("test:a", "A", "xunit") });

        Assert.Equal(100, Assert.Single(result.RequestedTests).Reasons.Count);
        Assert.NotEmpty(result.Warnings!);
    }

    [Fact]
    public void Constructor_RejectsMoreThanConfiguredEdgeLimit()
    {
        var edges = Enumerable.Range(0, 1_000_001)
            .Select(index => new ImpactEdge($"source:{index}", $"target:{index}", EvidenceKind.StaticDependency));

        Assert.Throws<ArgumentException>(() => new ImpactIndex(edges));
    }
}
