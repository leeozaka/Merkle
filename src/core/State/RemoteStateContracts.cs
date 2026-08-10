using Merkle.Core.History;

namespace Merkle.Core.State;

public interface IRemoteStateTokenSource
{
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed record RemoteHistoryCursor
{
    public string Value { get; }
    public RemoteHistoryCursor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("A remote cursor must be non-empty and at most 512 characters.", nameof(value));
        Value = value;
    }
}

public sealed record RemoteHistoricalRun
{
    public string Id { get; }
    public HistoricalRun Run { get; }
    public RemoteHistoricalRun(string id, HistoricalRun run)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':'))) throw new ArgumentException("Remote history IDs must use only ASCII letters, digits, '.', '_', '-', or ':'.", nameof(id));
        Id = id;
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }
}

public sealed record RemoteHistoryRead
{
    public HistoryCompatibilityKey Compatibility { get; }
    public RemoteHistoryCursor? Cursor { get; }
    public int MaximumRuns { get; }
    public RemoteHistoryRead(HistoryCompatibilityKey compatibility, RemoteHistoryCursor? cursor = null, int maximumRuns = 100)
    {
        if (maximumRuns is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximumRuns), "Remote reads must request between 1 and 1000 runs.");
        Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        Cursor = cursor;
        MaximumRuns = maximumRuns;
    }
}

public sealed record RemoteHistoryPage
{
    public IReadOnlyList<RemoteHistoricalRun> Runs { get; }
    public RemoteHistoryCursor? NextCursor { get; }
    public string Version { get; }
    public RemoteHistoryPage(IReadOnlyList<RemoteHistoricalRun> runs, RemoteHistoryCursor? nextCursor, string version)
    {
        Runs = Array.AsReadOnly((runs ?? throw new ArgumentNullException(nameof(runs))).ToArray());
        NextCursor = nextCursor;
        Version = string.IsNullOrWhiteSpace(version) ? throw new ArgumentException("A remote version is required.", nameof(version)) : version;
    }
}

public sealed record RemoteHistoryPublication
{
    public HistoryCompatibilityKey Compatibility { get; }
    public IReadOnlyList<RemoteHistoricalRun> Runs { get; }
    public string ExpectedVersion { get; }
    public string IdempotencyKey { get; }
    public RemoteHistoryPublication(HistoryCompatibilityKey compatibility, IReadOnlyList<RemoteHistoricalRun> runs, string expectedVersion, string idempotencyKey)
    {
        Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        Runs = Array.AsReadOnly((runs ?? throw new ArgumentNullException(nameof(runs))).ToArray());
        ExpectedVersion = string.IsNullOrWhiteSpace(expectedVersion) ? throw new ArgumentException("An expected version is required.", nameof(expectedVersion)) : expectedVersion;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256 ? throw new ArgumentException("An idempotency key is required and must be at most 256 characters.", nameof(idempotencyKey)) : idempotencyKey;
    }
}

/// <summary>Remote history seam for user-owned storage. Implementations expose terminal evidence atomically.</summary>
public interface IRemoteStateStore
{
    ValueTask<RemoteHistoryPage> ReadCompatibleTerminalHistoryAsync(RemoteHistoryRead read, CancellationToken cancellationToken);
    ValueTask<string> PublishTerminalHistoryAsync(RemoteHistoryPublication publication, CancellationToken cancellationToken);
}
