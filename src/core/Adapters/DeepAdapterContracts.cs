using Merkle.Core.Domain;

namespace Merkle.Core.Adapters;

/// <summary>Immutable context shared by build, discovery, execution, and observation.</summary>
public sealed record DeepAdapterContext(
    RepositorySnapshot Snapshot,
    string? ConfiguredSolution = null,
    string Configuration = "Debug",
    string Platform = "AnyCPU",
    string? StateDirectory = null);

public sealed record BuildPreparationRequest(
    DeepAdapterContext Context,
    bool NoBuild = false);

public sealed record BuildArtifact(string ProjectPath, string AssemblyPath, string? PdbPath, string AssemblyHash, string? PdbHash);

public sealed record BuildFingerprint(
    string Value,
    string SnapshotId,
    string SolutionPath,
    string Configuration,
    string Platform,
    string DotNetVersion,
    IReadOnlyList<string> TargetFrameworks,
    string AdapterVersion,
    string ObserverVersion,
    IReadOnlyList<BuildArtifact> Artifacts);

public sealed record BuildPreparationResult(BuildFingerprint Fingerprint, IReadOnlyList<string> Warnings);

public sealed record TestCatalogEntry(
    string Identity,
    string DisplayName,
    string Framework,
    string ProjectPath,
    string Selector);

public sealed record DiscoveryCatalog(BuildFingerprint Fingerprint, IReadOnlyList<TestCatalogEntry> Tests, IReadOnlyList<string> Warnings);

public sealed record SelectedTestReference(string Identity, string DisplayName);

public sealed record SelectedTestResolution(
    IReadOnlyList<TestCatalogEntry> Tests,
    IReadOnlyList<SelectedTestReference> UnresolvedTests);

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    TimedOut,
    Crashed,
    Cancelled
}

public sealed record TestExecutionResult(
    string TestIdentity,
    TestOutcome Outcome,
    TimeSpan? Duration,
    string? Diagnostics = null);

public sealed record SelectedExecutionRequest(
    DeepAdapterContext Context,
    BuildFingerprint Fingerprint,
    IReadOnlyList<TestCatalogEntry> Tests,
    TimeSpan? Timeout = null);

public enum ObservationCompleteness
{
    Complete,
    Incomplete
}

public sealed record DynamicObservation(
    string TestIdentity,
    string UnitIdentity,
    string BuildFingerprint,
    string AdapterVersion,
    string ObserverVersion,
    string RunId,
    string Granularity,
    string BlindSpots);

public sealed record ObservationScope(
    string TestIdentity,
    ObservationCompleteness Completeness,
    IReadOnlyList<DynamicObservation> Observations,
    TestExecutionResult Execution,
    IReadOnlyList<string> Warnings);

public sealed record ObservationRequest(
    DeepAdapterContext Context,
    BuildFingerprint Fingerprint,
    IReadOnlyList<TestCatalogEntry> Tests,
    TimeSpan? Timeout = null,
    string? RunId = null);

public interface IBuildPreparer
{
    ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken);
}

public interface ITestDiscoverer
{
    ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken);
}

public interface ISelectedTestExecutor
{
    ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken);
}

public interface ISelectedTestResolver
{
    SelectedTestResolution ResolveSelectedTests(
        IReadOnlyList<SelectedTestReference> selectedTests,
        IReadOnlyList<TestCatalogEntry> catalog);
}

public interface ITestObserver
{
    ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken);
}
