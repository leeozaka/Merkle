using Merkle.Core.Reporting;

namespace Merkle.Core.State;

public sealed record RunJournal(string RunId, string JournalPath);

public sealed record StateStatus(
    string Provider,
    int SchemaVersion,
    long SizeBytes,
    string? LastCompatibleRunId,
    bool RebuildRequired);

public interface IStateStore
{
    ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken);

    ValueTask PublishAsync(
        RunJournal journal,
        TerminalReport report,
        CancellationToken cancellationToken);

    ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken);

    ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask ResetAsync(CancellationToken cancellationToken);
}
