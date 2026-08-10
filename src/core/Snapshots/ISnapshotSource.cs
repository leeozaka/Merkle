using Merkle.Core.Domain;

namespace Merkle.Core.Snapshots;

public interface ISnapshotSource
{
    ValueTask<SnapshotPair> BindAsync(
        string? baselineReference,
        string? candidateReference,
        CancellationToken cancellationToken);
}

