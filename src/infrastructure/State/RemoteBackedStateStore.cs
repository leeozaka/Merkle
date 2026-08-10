using System.Security.Cryptography;
using System.Text;
using Merkle.Core.Adapters;
using Merkle.Core.History;
using Merkle.Core.Reporting;
using Merkle.Core.State;

namespace Merkle.Infrastructure.State;

/// <summary>
/// Keeps reports and indexes local while making a user-owned remote endpoint authoritative
/// for compatible history. A history publication reaches the remote provider before its
/// local terminal pointer advances.
/// </summary>
public sealed class RemoteBackedStateStore(
    LocalStateStore local,
    IRemoteStateStore remote) : IStateStore, IStatePublicationStore, IIndexStore, IHistoryStore
{
    private const int MaximumPages = 10;

    public ValueTask<RunJournal> BeginRunAsync(string runId, CancellationToken cancellationToken) =>
        local.BeginRunAsync(runId, cancellationToken);

    public ValueTask PublishAsync(
        RunJournal journal,
        TerminalReport report,
        CancellationToken cancellationToken) =>
        local.PublishAsync(journal, report, cancellationToken);

    public async ValueTask PublishAsync(
        RunJournal journal,
        StatePublication publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        local.ValidatePublication(publication);
        foreach (var group in publication.PersistedHistoryRuns.GroupBy(run => run.Compatibility))
        {
            var records = group.Select((run, index) => new RemoteHistoricalRun(
                $"{publication.TerminalReport.RunId}:{index}",
                run)).ToArray();
            await PublishWithRetryAsync(
                group.Key,
                records,
                publication.TerminalReport.RunId,
                cancellationToken).ConfigureAwait(false);
        }

        await local.PublishAsync(journal, publication, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<TerminalReport?> ReadCurrentAsync(CancellationToken cancellationToken) =>
        local.ReadCurrentAsync(cancellationToken);

    public async ValueTask<StateStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await local.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status with { Provider = "remote-history+sqlite" };
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken) =>
        local.ResetAsync(cancellationToken);

    public ValueTask<AdapterIndex?> ReadIndexAsync(
        IndexCompatibilityKey compatibility,
        CancellationToken cancellationToken) =>
        local.ReadIndexAsync(compatibility, cancellationToken);

    public async ValueTask<IReadOnlyList<HistoricalRun>> ReadHistoryAsync(
        HistoryCompatibilityKey compatibility,
        CancellationToken cancellationToken)
    {
        var result = new List<HistoricalRun>();
        RemoteHistoryCursor? cursor = null;
        for (var pageNumber = 0; pageNumber < MaximumPages; pageNumber++)
        {
            var page = await remote.ReadCompatibleTerminalHistoryAsync(
                new RemoteHistoryRead(compatibility, cursor, 1_000),
                cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Runs.Select(item => item.Run));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                return result;
            }
        }

        throw new RemoteStateException(
            RemoteStateFailureKind.Analysis,
            "RemoteHistoryLimitExceeded",
            "Remote compatible history exceeds the 10,000-run client limit.");
    }

    private async ValueTask PublishWithRetryAsync(
        HistoryCompatibilityKey compatibility,
        IReadOnlyList<RemoteHistoricalRun> records,
        string runId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await remote.ReadCompatibleTerminalHistoryAsync(
                new RemoteHistoryRead(compatibility, maximumRuns: 1),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await remote.PublishTerminalHistoryAsync(
                    new RemoteHistoryPublication(
                        compatibility,
                        records,
                        current.Version,
                        IdempotencyKey(runId, compatibility)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RemoteStateException error) when (
                error.Kind == RemoteStateFailureKind.Concurrency)
            {
            }
        }

        throw new RemoteStateException(
            RemoteStateFailureKind.Concurrency,
            "RemoteConcurrencyConflict",
            "Remote history publication did not converge after three compare-and-swap attempts.");
    }

    private static string IdempotencyKey(string runId, HistoryCompatibilityKey compatibility)
    {
        var value = string.Join(
            '\0',
            runId,
            compatibility.RepositoryIdentity,
            compatibility.SchemaVersion,
            compatibility.AdapterIdentity,
            compatibility.BuildFingerprintFamily);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed class EnvironmentRemoteStateTokenSource(string environmentVariable) : IRemoteStateTokenSource
{
    private readonly string _environmentVariable = string.IsNullOrWhiteSpace(environmentVariable)
        ? throw new ArgumentException("A token environment variable is required.", nameof(environmentVariable))
        : environmentVariable;

    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Environment.GetEnvironmentVariable(_environmentVariable));
    }
}
