using Merkle.Core.Adapters;
using Merkle.Core.Errors;

namespace Merkle.Adapters.Go;

/// <summary>Go language adapter host. Static analysis is supplied by the Go worker.</summary>
public sealed class GoAdapter : ILanguageAdapter, IBuildPreparer, ITestDiscoverer, ISelectedTestResolver, ISelectedTestExecutor, ITestObserver
{
    private readonly ILanguageAdapter? _analysisWorker;
    private readonly GoDeepOperations? _deepOperations;

    public GoAdapter(ILanguageAdapter? analysisWorker = null, GoDeepOperations? deepOperations = null)
    {
        _analysisWorker = analysisWorker;
        _deepOperations = deepOperations;
    }

    public AdapterDescriptor Describe()
    {
        var descriptor = Analysis().Describe();

        if (_deepOperations is not { IsConfigured: true }) return descriptor;

        var capabilities = descriptor.Capabilities
            .Concat([AdapterCapability.Discover, AdapterCapability.Observe, AdapterCapability.Execute])
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var profiles = descriptor.Profiles
            .Concat(["deep"])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return descriptor with { Capabilities = capabilities, Profiles = profiles };
    }

    public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Analysis().IndexAsync(request, cancellationToken);
    }

    public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Analysis().MapAsync(request, cancellationToken);
    }

    public ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken) =>
        Deep().PrepareBuildAsync(request, cancellationToken);

    public ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken) =>
        Deep().DiscoverAsync(context, fingerprint, cancellationToken);

    public ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken) =>
        Deep().ExecuteAsync(request, cancellationToken);

    public SelectedTestResolution ResolveSelectedTests(IReadOnlyList<SelectedTestReference> selectedTests, IReadOnlyList<TestCatalogEntry> catalog) =>
        Deep().ResolveSelectedTests(selectedTests, catalog);

    public ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) =>
        Deep().ObserveAsync(request, cancellationToken);

    private GoDeepOperations Deep() => _deepOperations is { IsConfigured: true } deep
        ? deep
        : throw new CapabilityException("DeepToolchainUnavailable", "Function not available for: golang");

    private ILanguageAdapter Analysis() => _analysisWorker
        ?? throw new CapabilityException("AnalysisWorkerUnavailable", "Static analysis is unavailable for: golang. The Go adapter worker is not configured.");
}
