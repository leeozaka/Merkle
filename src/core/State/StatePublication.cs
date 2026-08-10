using Merkle.Core.Adapters;
using Merkle.Core.History;
using Merkle.Core.Reporting;

namespace Merkle.Core.State;

/// <summary>Immutable compatibility namespace for a rebuildable adapter index.</summary>
public sealed record IndexCompatibilityKey(
    string RepositoryIdentity,
    string SnapshotIdentity,
    int IndexSchema,
    string HashAlgorithmVersion,
    string SemanticNormalizationVersion,
    string AdapterIdentity,
    string AdapterProtocolVersion,
    string UnitIdentityVersion,
    string TestIdentityVersion,
    string Language,
    string? SolutionBuildDigest)
{
    public bool Matches(IndexCompatibilityKey other) =>
        other is not null &&
        string.Equals(RepositoryIdentity, other.RepositoryIdentity, StringComparison.Ordinal) &&
        string.Equals(SnapshotIdentity, other.SnapshotIdentity, StringComparison.Ordinal) &&
        IndexSchema == other.IndexSchema &&
        string.Equals(HashAlgorithmVersion, other.HashAlgorithmVersion, StringComparison.Ordinal) &&
        string.Equals(SemanticNormalizationVersion, other.SemanticNormalizationVersion, StringComparison.Ordinal) &&
        string.Equals(AdapterIdentity, other.AdapterIdentity, StringComparison.Ordinal) &&
        string.Equals(AdapterProtocolVersion, other.AdapterProtocolVersion, StringComparison.Ordinal) &&
        string.Equals(UnitIdentityVersion, other.UnitIdentityVersion, StringComparison.Ordinal) &&
        string.Equals(TestIdentityVersion, other.TestIdentityVersion, StringComparison.Ordinal) &&
        string.Equals(Language, other.Language, StringComparison.Ordinal) &&
        string.Equals(SolutionBuildDigest, other.SolutionBuildDigest, StringComparison.Ordinal);
}

public sealed record PersistedAdapterIndex(IndexCompatibilityKey Compatibility, AdapterIndex Index);

/// <summary>
/// The one visible outcome of a run. Implementations publish this aggregate in one transaction;
/// indexes and history are optional because a terminal planning report need not learn evidence.
/// </summary>
public sealed record StatePublication(
    TerminalReport TerminalReport,
    IReadOnlyList<PersistedAdapterIndex>? Indexes = null,
    IReadOnlyList<HistoricalRun>? HistoryRuns = null)
{
    public IReadOnlyList<PersistedAdapterIndex> PersistedIndexes { get; } = Indexes ?? [];
    public IReadOnlyList<HistoricalRun> PersistedHistoryRuns { get; } = HistoryRuns ?? [];
}

public interface IIndexStore
{
    ValueTask<AdapterIndex?> ReadIndexAsync(IndexCompatibilityKey compatibility, CancellationToken cancellationToken);
}

public interface IStatePublicationStore
{
    ValueTask PublishAsync(
        RunJournal journal,
        StatePublication publication,
        CancellationToken cancellationToken);
}

public interface IHistoryStore
{
    ValueTask<IReadOnlyList<HistoricalRun>> ReadHistoryAsync(
        HistoryCompatibilityKey compatibility,
        CancellationToken cancellationToken);
}
