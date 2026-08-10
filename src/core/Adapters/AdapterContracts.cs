using Merkle.Core.Domain;
using Merkle.Core.Indexing;

namespace Merkle.Core.Adapters;

public enum AdapterCapability
{
    Detect,
    Index,
    Map,
    Discover,
    Observe,
    Execute,
    Report
}

public sealed record AdapterDescriptor(
    string ProtocolVersion,
    string Language,
    string Producer,
    string AdapterVersion,
    string UnitIdentityVersion,
    string TestIdentityVersion,
    IReadOnlyCollection<AdapterCapability> Capabilities,
    IReadOnlyCollection<string> Profiles,
    IReadOnlyCollection<string>? SupportedTargets = null,
    IReadOnlyCollection<string>? SupportedPlatforms = null);

public sealed record AdapterIndexRequest(
    RepositorySnapshot Snapshot,
    string? ConfiguredSolution);

public sealed record AdapterMapRequest(
    RepositorySnapshot Snapshot,
    AdapterIndex Index,
    IReadOnlyList<ChangedUnit> ChangedUnits);

public sealed record AdapterIndex(
    IReadOnlyList<SourceUnit> Units,
    IReadOnlyList<ImpactEdge> Edges,
    IReadOnlyList<TestDescriptor> Tests,
    IReadOnlyList<string>? Warnings = null);

public sealed record MappingResult(
    IReadOnlyList<RequestedTest> RequestedTests,
    IReadOnlyList<ChangedUnit> UnmappedUnits,
    IReadOnlyList<string>? Warnings = null);

public interface ILanguageAdapter
{
    AdapterDescriptor Describe();

    ValueTask<AdapterIndex> IndexAsync(
        AdapterIndexRequest request,
        CancellationToken cancellationToken);

    ValueTask<MappingResult> MapAsync(
        AdapterMapRequest request,
        CancellationToken cancellationToken);
}
