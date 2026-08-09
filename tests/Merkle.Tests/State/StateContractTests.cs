using Merkle.Core.History;
using Merkle.Core.State;

namespace Merkle.Tests.State;

public sealed class StateContractTests
{
    [Fact]
    public void IndexCompatibilityKey_MatchesOnlyAnIdenticalKey()
    {
        var key = Key();

        Assert.True(key.Matches(Key()));
        Assert.False(key.Matches(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void IndexCompatibilityKey_DoesNotMatchWhenAnyNamespacePartDiffers(int part)
    {
        var changed = Key() with
        {
            RepositoryIdentity = part == 0 ? "other" : "repo",
            SnapshotIdentity = part == 1 ? "other" : "snapshot",
            IndexSchema = part == 2 ? 2 : 1,
            HashAlgorithmVersion = part == 3 ? "other" : "hash",
            SemanticNormalizationVersion = part == 4 ? "other" : "semantic",
            AdapterIdentity = part == 5 ? "other" : "adapter",
            AdapterProtocolVersion = part == 6 ? "other" : "protocol",
            UnitIdentityVersion = part == 7 ? "other" : "unit",
            TestIdentityVersion = part == 8 ? "other" : "test",
            Language = part == 9 ? "other" : "dotnet",
            SolutionBuildDigest = part == 10 ? "other" : "build"
        };

        Assert.False(Key().Matches(changed));
    }

    [Fact]
    public void StatePublication_UsesEmptyCollectionsForNullOptionalValues()
    {
        var publication = new StatePublication(null!, null, null);

        Assert.Empty(publication.PersistedIndexes);
        Assert.Empty(publication.PersistedHistoryRuns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RemoteHistoryCursor_RejectsBlankValues(string? value) =>
        Assert.Throws<ArgumentException>(() => new RemoteHistoryCursor(value!));

    [Fact]
    public void RemoteHistoryCursor_RejectsValuesBeyondTheBound() =>
        Assert.Throws<ArgumentException>(() => new RemoteHistoryCursor(new string('a', 513)));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad/value")]
    [InlineData(null)]
    public void RemoteHistoricalRun_RejectsInvalidIds(string? id) =>
        Assert.Throws<ArgumentException>(() => new RemoteHistoricalRun(id!, null!));

    [Fact]
    public void RemoteHistoricalRun_RejectsAnIdBeyondTheBound() =>
        Assert.Throws<ArgumentException>(() => new RemoteHistoricalRun(new string('a', 257), null!));

    [Fact]
    public void RemoteHistoricalRun_RejectsNullRunAfterValidatingId() =>
        Assert.Throws<ArgumentNullException>(() => new RemoteHistoricalRun("valid:run", null!));

    [Theory]
    [InlineData(0)]
    [InlineData(1_001)]
    public void RemoteHistoryRead_RejectsOutOfRangeMaximumRuns(int maximumRuns) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteHistoryRead(HistoryKey(), maximumRuns: maximumRuns));

    [Fact]
    public void RemoteHistoryRead_RejectsNullCompatibility() =>
        Assert.Throws<ArgumentNullException>(() => new RemoteHistoryRead(null!));

    [Fact]
    public void RemoteHistoryPage_RejectsNullRunsAndBlankVersion()
    {
        Assert.Throws<ArgumentNullException>(() => new RemoteHistoryPage(null!, null, "v1"));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPage([], null, null!));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPage([], null, " "));
    }

    [Fact]
    public void RemoteHistoryPage_MakesItsRunsImmutableFromCallerMutation()
    {
        var source = new List<RemoteHistoricalRun>();
        var page = new RemoteHistoryPage(source, null, "v1");
        source.Add(new RemoteHistoricalRun("run-1", Run()));

        Assert.Empty(page.Runs);
    }

    [Fact]
    public void RemoteHistoryPublication_RejectsNullRequiredValuesAndBlankText()
    {
        Assert.Throws<ArgumentNullException>(() => new RemoteHistoryPublication(null!, [], "v1", "key"));
        Assert.Throws<ArgumentNullException>(() => new RemoteHistoryPublication(HistoryKey(), null!, "v1", "key"));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPublication(HistoryKey(), [], null!, "key"));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPublication(HistoryKey(), [], " ", "key"));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPublication(HistoryKey(), [], "v1", null!));
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPublication(HistoryKey(), [], "v1", " "));
    }

    [Fact]
    public void RemoteHistoryPublication_RejectsAnIdempotencyKeyBeyondTheBound() =>
        Assert.Throws<ArgumentException>(() => new RemoteHistoryPublication(HistoryKey(), [], "v1", new string('k', 257)));

    private static IndexCompatibilityKey Key() =>
        new("repo", "snapshot", 1, "hash", "semantic", "adapter", "protocol", "unit", "test", "dotnet", "build");

    private static HistoryCompatibilityKey HistoryKey() => new("repo", "schema", "adapter", "build");

    private static HistoricalRun Run() => new(
        HistoryKey(),
        HistoryProvenance.Local,
        HistoryRunStatus.Succeeded,
        false,
        DateTimeOffset.UtcNow,
        ["unit"],
        []);
}
