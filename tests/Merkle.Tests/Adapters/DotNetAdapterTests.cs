using System.Text;
using Merkle.Adapters.DotNet;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Tests.Adapters;

public sealed class DotNetAdapterTests
{
    [Fact]
    public async Task Index_RejectsMultipleUnconfiguredSolutions()
    {
        var snapshot = Snapshot(
            ("One.sln", string.Empty),
            ("Two.slnx", string.Empty));
        var adapter = new DotNetAdapter();

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await adapter.IndexAsync(new AdapterIndexRequest(snapshot, null), default));

        Assert.Equal("MultipleSolutions", error.Code);
        Assert.Contains("One.sln", error.Message, StringComparison.Ordinal);
        Assert.Contains("Two.slnx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_MapsChangedProductionProjectToReferencingTestProjectTarget()
    {
        var snapshot = Snapshot(
            ("Repo.slnx", "<Solution />"),
            ("src/Domain/Domain.csproj", "<Project />"),
            ("src/Domain/Price.cs", "namespace Domain; public sealed class Price {}"),
            ("tests/Domain.Tests/Domain.Tests.csproj", """
                <Project>
                  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../../src/Domain/Domain.csproj" /></ItemGroup>
                </Project>
                """),
            ("tests/Domain.Tests/PriceTests.cs", "public sealed class PriceTests {}"));
        var adapter = new DotNetAdapter();
        var index = await adapter.IndexAsync(new AdapterIndexRequest(snapshot, null), default);
        var changed = new ChangedUnit("dotnet:file:src/Domain/Price.cs", SourceUnitKind.File,
            ChangeKind.Modified, true);

        var result = await adapter.MapAsync(new AdapterMapRequest(snapshot, index, [changed]), default);

        var requested = Assert.Single(result.RequestedTests);
        Assert.Equal("dotnet-project:tests/Domain.Tests/Domain.Tests.csproj", requested.Identity);
        Assert.Contains(requested.Reasons, reason => reason.Kind == EvidenceKind.AncestorFallback);
        Assert.Empty(result.UnmappedUnits);
    }

    [Fact]
    public async Task Map_ReportsSourceOutsideKnownProjectAsUnmapped()
    {
        var snapshot = Snapshot(
            ("Repo.sln", string.Empty),
            ("src/App/App.csproj", "<Project />"),
            ("scripts/tool.cs", "Console.WriteLine();"));
        var adapter = new DotNetAdapter();
        var index = await adapter.IndexAsync(new AdapterIndexRequest(snapshot, null), default);
        var changed = new ChangedUnit("dotnet:file:scripts/tool.cs", SourceUnitKind.File,
            ChangeKind.Added, false);

        var result = await adapter.MapAsync(new AdapterMapRequest(snapshot, index, [changed]), default);

        Assert.Equal([changed], result.UnmappedUnits);
    }

    [Fact]
    public async Task Index_RejectsRepositoryWithoutSolution()
    {
        var snapshot = Snapshot(("src/App/App.csproj", "<Project />"));

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await new DotNetAdapter().IndexAsync(new AdapterIndexRequest(snapshot, null), default));

        Assert.Equal("SolutionNotFound", error.Code);
    }

    [Fact]
    public async Task Index_DetectsTestProjectFromKnownTestPackage()
    {
        var snapshot = Snapshot(
            ("Repo.sln", string.Empty),
            ("tests/App.Tests/App.Tests.csproj", """
                <Project>
                  <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
                </Project>
                """));

        var index = await new DotNetAdapter().IndexAsync(
            new AdapterIndexRequest(snapshot, null), default);

        Assert.Equal("dotnet-project:tests/App.Tests/App.Tests.csproj", Assert.Single(index.Tests).Identity);
    }

    [Fact]
    public async Task Index_RejectsProjectReferenceOutsideRepository()
    {
        var snapshot = Snapshot(
            ("Repo.sln", string.Empty),
            ("App.csproj", """
                <Project>
                  <ItemGroup><ProjectReference Include="../Outside.csproj" /></ItemGroup>
                </Project>
                """));

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await new DotNetAdapter().IndexAsync(new AdapterIndexRequest(snapshot, null), default));

        Assert.Equal("ProjectReferenceOutsideRepository", error.Code);
    }

    [Fact]
    public async Task Map_GlobalJsonChangeInvalidatesAllTestProjects()
    {
        var snapshot = Snapshot(
            ("Repo.sln", string.Empty),
            ("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}"),
            ("src/App/App.csproj", "<Project />"),
            ("tests/App.Tests/App.Tests.csproj", """
                <Project>
                  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
                </Project>
                """));
        var adapter = new DotNetAdapter();
        var index = await adapter.IndexAsync(new AdapterIndexRequest(snapshot, null), default);
        var changed = new ChangedUnit("dotnet:file:global.json", SourceUnitKind.File,
            ChangeKind.Modified, false);

        var result = await adapter.MapAsync(new AdapterMapRequest(snapshot, index, [changed]), default);

        Assert.Equal("dotnet-project:tests/App.Tests/App.Tests.csproj",
            Assert.Single(result.RequestedTests).Identity);
    }

    [Fact]
    public async Task Index_ConfiguredSolutionScopesProjects()
    {
        var snapshot = Snapshot(
            ("One.slnx", "<Solution><Project Path=\"src/One/One.csproj\" /></Solution>"),
            ("Two.slnx", "<Solution><Project Path=\"src/Two/Two.csproj\" /></Solution>"),
            ("src/One/One.csproj", "<Project />"),
            ("src/One/One.cs", "public sealed class One {}"),
            ("src/Two/Two.csproj", "<Project />"),
            ("src/Two/Two.cs", "public sealed class Two {}"));

        var index = await new DotNetAdapter().IndexAsync(
            new AdapterIndexRequest(snapshot, "One.slnx"), default);

        Assert.Contains(index.Units, unit => unit.Identity == "dotnet:project:src/One/One.csproj");
        Assert.DoesNotContain(index.Units, unit => unit.Identity.Contains("src/Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Index_ReturnsEmptyFragmentWhenLanguageIsAbsentFromOneSnapshot()
    {
        var snapshot = Snapshot(("README.md", "text"));

        var index = await new DotNetAdapter().IndexAsync(
            new AdapterIndexRequest(snapshot, null), default);

        Assert.Empty(index.Units);
        Assert.Empty(index.Edges);
        Assert.Empty(index.Tests);
    }

    [Fact]
    public async Task Index_DelegatesToSemanticWorkerWhenConfigured()
    {
        var expected = new AdapterIndex(
            [new SourceUnit("dotnet:file:App.cs", SourceUnitKind.File, "App.cs", "hash", "signature")], [],
            [new TestDescriptor("test", "App.cs", "xunit")], ["semantic"]);
        var adapter = new DotNetAdapter(new DeterministicDotNetAnalysisWorker(_ => expected));

        var index = await adapter.IndexAsync(new AdapterIndexRequest(Snapshot(("App.cs", "class App {}")), null), default);

        Assert.Same(expected, index);
        Assert.Contains(AdapterCapability.Discover, adapter.Describe().Capabilities);
        Assert.DoesNotContain(AdapterCapability.Execute, adapter.Describe().Capabilities);
        Assert.Contains("semantic", adapter.Describe().Profiles);
    }

    [Fact]
    public void Describe_AdvertisesDeepCapabilitiesOnlyWithConfiguredObserver()
    {
        var root = Path.Combine(Path.GetTempPath(), "merkle-adapter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var observer = Path.Combine(root, "observer.dll");
            File.WriteAllText(observer, "observer");
            var deep = new DotNetDeepOperations(new NoopRunner(), observer);
            var descriptor = new DotNetAdapter(null, deep).Describe();

            Assert.Equal(DotNetDeepOperations.AdapterVersion, descriptor.AdapterVersion);
            Assert.Contains(AdapterCapability.Observe, descriptor.Capabilities);
            Assert.Contains(AdapterCapability.Execute, descriptor.Capabilities);
            Assert.Contains("deep", descriptor.Profiles);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeepOperations_RejectWhenObserverIsNotConfigured()
    {
        var missing = Path.Combine(Path.GetTempPath(), "merkle-missing-observer-" + Guid.NewGuid().ToString("N") + ".dll");
        var adapter = new DotNetAdapter(null, new DotNetDeepOperations(new NoopRunner(), missing));
        var context = new DeepAdapterContext(Snapshot(("Repo.sln", string.Empty), ("App.csproj", "<Project />")));

        var error = await Assert.ThrowsAsync<CapabilityException>(() => adapter.PrepareBuildAsync(new BuildPreparationRequest(context), default).AsTask());

        Assert.Equal("DeepToolchainUnavailable", error.Code);
        Assert.DoesNotContain(AdapterCapability.Execute, adapter.Describe().Capabilities);
    }

    private static RepositorySnapshot Snapshot(params (string Path, string Content)[] files)
    {
        var projectPaths = files
            .Select(file => file.Path)
            .Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj")
            .ToArray();
        var snapshotFiles = files.Select(file =>
        {
            var content = file.Content;
            if (Path.GetExtension(file.Path) == ".sln" && string.IsNullOrWhiteSpace(content))
            {
                content = string.Join('\n', projectPaths.Select(path =>
                    $"Project(\"{{FAKE}}\") = \"{Path.GetFileNameWithoutExtension(path)}\", \"{path}\", \"{{FAKE}}\""));
            }
            else if (Path.GetExtension(file.Path) == ".slnx" &&
                     (string.IsNullOrWhiteSpace(content) || content.Trim() == "<Solution />"))
            {
                content = $"<Solution>{string.Concat(projectPaths.Select(path => $"<Project Path=\"{path}\" />"))}</Solution>";
            }

            return new SnapshotFile(
                file.Path,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                Encoding.UTF8.GetBytes(content));
        }).ToArray();
        return new RepositorySnapshot(
            new SnapshotIdentity("snapshot", "HEAD", "git"),
            "/repo",
            "repository",
            snapshotFiles);
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}
