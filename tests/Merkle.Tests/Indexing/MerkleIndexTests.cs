using Merkle.Core.Domain;
using Merkle.Core.Indexing;
using Merkle.Core.Errors;

namespace Merkle.Tests.Indexing;

public sealed class MerkleIndexTests
{
    [Fact]
    public void Build_IsIndependentOfInputEnumerationOrder()
    {
        var first = Unit("file:a.cs", "a.cs", "alpha");
        var second = Unit("file:b.cs", "b.cs", "beta");

        var forward = MerkleIndex.Build([first, second]);
        var reverse = MerkleIndex.Build([second, first]);

        Assert.Equal(forward.Root.Hash, reverse.Root.Hash);
        Assert.Equal(forward.Root.Children.Select(child => child.Identity),
            reverse.Root.Children.Select(child => child.Identity));
    }

    [Fact]
    public void Build_RejectsAContainmentCycleEvenWhenTheCycleHasNoRoot()
    {
        var units = new[]
        {
            Unit("dotnet:a", "a", "a"),
            Unit("dotnet:b", "b", "b")
        };
        var edges = new[]
        {
            new ImpactEdge("dotnet:a", "dotnet:b", EvidenceKind.Containment),
            new ImpactEdge("dotnet:b", "dotnet:a", EvidenceKind.Containment)
        };

        var error = Assert.Throws<AnalysisException>(() => MerkleIndex.Build(units, edges));

        Assert.Equal("ContainmentCycle", error.Code);
    }

    [Fact]
    public void Build_RejectsUnknownUnitsAndMultipleContainmentParents()
    {
        var units = new[]
        {
            Unit("dotnet:child", "child", "child"),
            Unit("dotnet:parent-a", "a", "a"),
            Unit("dotnet:parent-b", "b", "b")
        };

        var unknown = Assert.Throws<AnalysisException>(() => MerkleIndex.Build(units,
            [new ImpactEdge("dotnet:missing", "dotnet:parent-a", EvidenceKind.Containment)]));
        Assert.Equal("UnknownContainmentUnit", unknown.Code);

        var multiple = Assert.Throws<AnalysisException>(() => MerkleIndex.Build(units,
            [
                new ImpactEdge("dotnet:child", "dotnet:parent-a", EvidenceKind.Containment),
                new ImpactEdge("dotnet:child", "dotnet:parent-b", EvidenceKind.Containment)
            ]));
        Assert.Equal("MultipleContainmentParents", multiple.Code);
    }

    [Fact]
    public void Compare_EqualRootsPrunesAllChildren()
    {
        var index = MerkleIndex.Build([Unit("file:a.cs", "a.cs", "alpha")]);

        var result = MerkleIndex.Compare(index, index);

        Assert.Empty(result);
    }

    [Fact]
    public void Compare_ReturnsAddedDeletedAndModifiedLeavesInStableOrder()
    {
        var baseline = MerkleIndex.Build([
            Unit("file:a.cs", "a.cs", "old"),
            Unit("file:deleted.cs", "deleted.cs", "gone")]);
        var candidate = MerkleIndex.Build([
            Unit("file:a.cs", "a.cs", "new"),
            Unit("file:added.cs", "added.cs", "here")]);

        var result = MerkleIndex.Compare(baseline, candidate);

        Assert.Collection(result,
            change => Assert.Equal(("file:a.cs", ChangeKind.Modified), (change.Identity, change.ChangeKind)),
            change => Assert.Equal(("file:added.cs", ChangeKind.Added), (change.Identity, change.ChangeKind)),
            change => Assert.Equal(("file:deleted.cs", ChangeKind.Deleted), (change.Identity, change.ChangeKind)));
    }

    [Fact]
    public void Hash_DomainSeparatesLeavesFromBranches()
    {
        var leaf = MerkleNode.Leaf(Unit("same", "same", "content"));
        var branch = MerkleNode.Branch(SourceUnitKind.Repository, "same", [leaf]);

        Assert.NotEqual(leaf.Hash, branch.Hash);
    }

    [Fact]
    public void Hash_LengthDelimitsFieldsThatWouldOtherwiseConcatenateEqually()
    {
        var first = MerkleNode.Leaf(new SourceUnit(
            "ab", SourceUnitKind.File, "first", "c", string.Empty));
        var second = MerkleNode.Leaf(new SourceUnit(
            "a", SourceUnitKind.File, "second", "bc", string.Empty));

        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Build_NestsContainedFileBelowLanguageAndProject()
    {
        var project = new SourceUnit(
            "dotnet:project:src/App/App.csproj", SourceUnitKind.Project,
            "src/App/App.csproj", "project", string.Empty);
        var file = new SourceUnit(
            "dotnet:file:src/App/Program.cs", SourceUnitKind.File,
            "src/App/Program.cs", "file", string.Empty);

        var index = MerkleIndex.Build(
            [file, project],
            [new ImpactEdge(file.Identity, project.Identity, EvidenceKind.Containment)]);

        var language = Assert.Single(index.Root.Children);
        var projectNode = Assert.Single(language.Children);
        Assert.Equal(project.Identity, projectNode.Identity);
        Assert.Equal(file.Identity, Assert.Single(projectNode.Children).Identity);
    }

    [Fact]
    public void Build_RejectsDuplicateStableUnitIdentity()
    {
        var first = Unit("file:a.cs", "a.cs", "one");
        var second = Unit("file:a.cs", "a.cs", "two");

        var error = Assert.Throws<AnalysisException>(() => MerkleIndex.Build([first, second]));

        Assert.Equal("DuplicateUnitIdentity", error.Code);
    }

    private static SourceUnit Unit(string identity, string path, string content) =>
        new(identity, SourceUnitKind.File, path, content, string.Empty);
}
