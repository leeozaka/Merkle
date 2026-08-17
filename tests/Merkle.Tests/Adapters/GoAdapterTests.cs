using Merkle.Adapters.Go;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Tests.Adapters;

public sealed class GoAdapterTests
{
    [Fact]
    public void Describe_UpgradesWorkerDescriptorWithoutChangingIdentityFields()
    {
        var worker = new StubAdapter();
        var deep = new GoDeepOperations(new StubRunner());
        var descriptor = new GoAdapter(worker, deep).Describe();

        Assert.Equal("golang", descriptor.Language);
        Assert.Equal("worker", descriptor.Producer);
        Assert.Equal("worker-version", descriptor.AdapterVersion);
        Assert.Equal("unit-v1", descriptor.UnitIdentityVersion);
        Assert.Equal("test-v1", descriptor.TestIdentityVersion);
        Assert.Contains(AdapterCapability.Discover, descriptor.Capabilities);
        Assert.Contains(AdapterCapability.Observe, descriptor.Capabilities);
        Assert.Contains(AdapterCapability.Execute, descriptor.Capabilities);
        Assert.Contains("deep", descriptor.Profiles);
    }

    [Fact]
    public async Task IndexAndMap_DelegateToConfiguredWorker()
    {
        var expectedIndex = new AdapterIndex([], [], [new TestDescriptor("test", "test", "go-testing")]);
        var worker = new StubAdapter(expectedIndex);
        var adapter = new GoAdapter(worker);
        var snapshot = Snapshot();

        var index = await adapter.IndexAsync(new AdapterIndexRequest(snapshot, null), default);
        var mapping = await adapter.MapAsync(new AdapterMapRequest(snapshot, index, []), default);

        Assert.Same(expectedIndex, index);
        Assert.Same(worker.Mapping, mapping);
    }

    [Fact]
    public async Task DeepCalls_WhenUnconfiguredReportGolangCapability()
    {
        var adapter = new GoAdapter();
        var error = await Assert.ThrowsAsync<CapabilityException>(() =>
            adapter.PrepareBuildAsync(new BuildPreparationRequest(new DeepAdapterContext(Snapshot())), default).AsTask());

        Assert.Equal("DeepToolchainUnavailable", error.Code);
        Assert.Contains("golang", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotAdvertiseDeepWhenGoExecutableIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "go");
        var descriptor = new GoAdapter(new StubAdapter(), new GoDeepOperations(new StubRunner(), missing)).Describe();

        Assert.DoesNotContain("deep", descriptor.Profiles);
        Assert.DoesNotContain(AdapterCapability.Discover, descriptor.Capabilities);
        Assert.DoesNotContain(AdapterCapability.Execute, descriptor.Capabilities);
        Assert.DoesNotContain(AdapterCapability.Observe, descriptor.Capabilities);
    }

    private static RepositorySnapshot Snapshot() => new(
        new SnapshotIdentity("snapshot", "WORKTREE", "test"),
        Path.GetTempPath(),
        "repo",
        []);

    private sealed class StubAdapter(AdapterIndex? index = null) : ILanguageAdapter
    {
        public MappingResult Mapping { get; } = new([], []);
        public AdapterDescriptor Describe() => new("1.0", "golang", "worker", "worker-version", "unit-v1", "test-v1", [AdapterCapability.Index, AdapterCapability.Map], ["semantic"]);
        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(index ?? new AdapterIndex([], [], []));
        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(Mapping);
    }

    private sealed class StubRunner : Merkle.Core.Processes.IProcessRunner
    {
        public ValueTask<Merkle.Core.Processes.ProcessResult> RunAsync(Merkle.Core.Processes.ProcessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Merkle.Core.Processes.ProcessResult(1, string.Empty, "go unavailable"));
    }
}
