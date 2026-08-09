using System.Security.Cryptography;
using System.Text;
using Merkle.Adapters.DotNet;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Tests.Adapters;

public sealed class DotNetDeepOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merkle-deep-tests-" + Guid.NewGuid().ToString("N"));

    public DotNetDeepOperationsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PrepareBuild_BuildsAndWritesDeterministicManifest()
    {
        var observer = Path.Combine(_root, "observer.dll");
        await File.WriteAllTextAsync(observer, "observer");
        var assembly = Path.Combine(_root, "src/App/bin/Debug/net8.0/App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        await File.WriteAllTextAsync(assembly, "assembly");
        var runner = new FakeRunner();
        var operations = new DotNetDeepOperations(runner, observer);
        var context = Context();

        var first = await operations.PrepareBuildAsync(new BuildPreparationRequest(context), default);
        var second = await operations.PrepareBuildAsync(new BuildPreparationRequest(context), default);

        Assert.Equal(first.Fingerprint.Value, second.Fingerprint.Value);
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "build");
        Assert.Single(first.Fingerprint.Artifacts);
    }

    [Fact]
    public async Task PrepareBuild_NoBuildWithoutManifestFailsBeforeTestExecution()
    {
        var observer = Path.Combine(_root, "observer.dll");
        await File.WriteAllTextAsync(observer, "observer");
        var operations = new DotNetDeepOperations(new FakeRunner(), observer);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await operations.PrepareBuildAsync(new BuildPreparationRequest(Context(), NoBuild: true), default));

        Assert.Equal("ArtifactsUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_UsesOneProcessPerTestAndHasNoImplicitTimeout()
    {
        var observer = Path.Combine(_root, "observer.dll");
        await File.WriteAllTextAsync(observer, "observer");
        var runner = new FakeRunner();
        var operations = new DotNetDeepOperations(runner, observer);
        var context = Context();
        var fingerprint = new BuildFingerprint("fingerprint", "worktree", "Repo.slnx", "Debug", "AnyCPU", "10.0", ["net8.0"], "adapter", "observer", []);
        var tests = new[]
        {
            new TestCatalogEntry("one", "One", "xunit", "tests/App.Tests/App.Tests.csproj", "App.Tests.One"),
            new TestCatalogEntry("two", "Two", "xunit", "tests/App.Tests/App.Tests.csproj", "App.Tests.Two")
        };

        var result = await operations.ExecuteAsync(new SelectedExecutionRequest(context, fingerprint, tests), default);

        Assert.Equal(2, result.Count);
        Assert.All(result, value => Assert.Equal(TestOutcome.Passed, value.Outcome));
        Assert.Equal(2, runner.Requests.Count(request => request.Arguments[0] == "test"));
    }

    [Fact]
    public async Task PrepareBuild_RejectsFailedBuild()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
            request.Arguments[0] == "build" ? new ProcessResult(1, string.Empty, "compile failure") : Success(request)), observer);

        var error = await Assert.ThrowsAsync<AnalysisException>(() => operations.PrepareBuildAsync(new BuildPreparationRequest(Context()), default).AsTask());

        Assert.Equal("BuildFailed", error.Code);
        Assert.Contains("compile failure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_ReturnsCatalogAndWarningForFailedProject()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
            request.Arguments.Contains("--list-tests")
                ? new ProcessResult(0, "Tests are available:\n  App.Tests.MathTests.Adds\n  unusual(display)", string.Empty)
                : Success(request)), observer);
        var context = Context();

        var catalog = await operations.DiscoverAsync(context, Fingerprint(), default);

        Assert.Equal(2, catalog.Tests.Count);
        Assert.Contains(catalog.Tests, test => test.Identity.StartsWith("dotnet:test:v1:", StringComparison.Ordinal));
        Assert.Contains(catalog.Warnings, warning => warning.Contains("runner identity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discover_ForcesStableEnglishToolOutputInsteadOfParsingLocalizedHeaders()
    {
        var observer = await ObserverAsync();
        var runner = new FakeRunner(request =>
        {
            if (!request.Arguments.Contains("--list-tests")) return Success(request);
            var language = request.Environment?.GetValueOrDefault("DOTNET_CLI_UI_LANGUAGE");
            return language == "en-US"
                ? new ProcessResult(0, "Tests are available:\n  App.Tests.MathTests.Adds", string.Empty)
                : new ProcessResult(0, "Os seguintes testes estao disponiveis:\n  App.Tests.MathTests.Adds", string.Empty);
        });
        var operations = new DotNetDeepOperations(runner, observer);

        var catalog = await operations.DiscoverAsync(Context(), Fingerprint(), default);

        Assert.Single(catalog.Tests);
        var discovery = Assert.Single(runner.Requests, request => request.Arguments.Contains("--list-tests"));
        Assert.Equal("en-US", discovery.Environment?.GetValueOrDefault("DOTNET_CLI_UI_LANGUAGE"));
        Assert.Equal("1033", discovery.Environment?.GetValueOrDefault("VSLANG"));
        Assert.Contains("-p:CollectCoverage=false", discovery.Arguments);
    }

    [Fact]
    public async Task Discover_UsesMethodIdentityForDataDrivenFullyQualifiedNameFilter()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
            request.Arguments.Contains("--list-tests")
                ? new ProcessResult(0, "Tests are available:\n  App.Tests.MathTests.Adds(value: 1)", string.Empty)
                : Success(request)), observer);

        var test = Assert.Single((await operations.DiscoverAsync(Context(), Fingerprint(), default)).Tests);

        Assert.Equal("App.Tests.MathTests.Adds", test.Selector);
        Assert.StartsWith("dotnet:runner-test:v1:", test.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_UsesCatalogSelectorAsFullyQualifiedNameFilter()
    {
        var observer = await ObserverAsync();
        var runner = new FakeRunner();
        var operations = new DotNetDeepOperations(runner, observer);
        var test = new TestCatalogEntry(
            "static-identity",
            "App.Tests.MathTests.Adds(int)",
            "xunit",
            "tests/App.Tests/App.Tests.csproj",
            "App.Tests.MathTests.Adds");

        await operations.ExecuteAsync(new SelectedExecutionRequest(Context(), Fingerprint(), [test]), default);

        var execution = Assert.Single(runner.Requests, request => request.Arguments.Contains("--filter"));
        Assert.Contains("FullyQualifiedName=App.Tests.MathTests.Adds", execution.Arguments);
        Assert.Contains("-p:CollectCoverage=false", execution.Arguments);
    }

    [Fact]
    public async Task ResolveSelectedTests_MapsMerkleStaticIdentityToRuntimeFullyQualifiedName()
    {
        var operations = new DotNetDeepOperations(new FakeRunner(), await ObserverAsync());
        const string project = "tests/Merkle.Tests/Merkle.Tests.csproj";
        const string identity = "dotnet:test:v1:tests/Merkle.Tests/Merkle.Tests.csproj:dotnet:type:tests/Merkle.Tests/Merkle.Tests.csproj:Merkle.Tests.Planning.PlanPolicyTests`0:RecommendationsAreExplicit`0():method";
        const string selector = "Merkle.Tests.Planning.PlanPolicyTests.RecommendationsAreExplicit";
        var catalog = new TestCatalogEntry(
            "dotnet:runner-test:v1:tests/Merkle.Tests/Merkle.Tests.csproj:abc",
            selector,
            "xunit",
            project,
            selector);

        var resolved = Assert.Single(operations.ResolveSelectedTests(
            [new SelectedTestReference(identity, "PlanPolicyTests.RecommendationsAreExplicit`0()")],
            [catalog]).Tests);

        Assert.Equal(identity, resolved.Identity);
        Assert.Equal(project, resolved.ProjectPath);
        Assert.Equal(selector, resolved.Selector);
    }

    [Fact]
    public async Task ResolveSelectedTests_ParsesGlobalAliasColonsInsideCanonicalSignature()
    {
        var operations = new DotNetDeepOperations(new FakeRunner(), await ObserverAsync());
        const string project = "tests/App.Tests/App.Tests.csproj";
        const string identity = "dotnet:test:v1:tests/App.Tests/App.Tests.csproj:dotnet:type:tests/App.Tests/App.Tests.csproj:App.Tests.ValuesTests`0:Accepts`0(global::System.String):method";
        const string selector = "App.Tests.ValuesTests.Accepts";
        var catalog = new TestCatalogEntry("runtime", selector, "xunit", project, selector);

        var resolved = Assert.Single(operations.ResolveSelectedTests(
            [new SelectedTestReference(identity, "ValuesTests.Accepts`0(global::System.String)")],
            [catalog]).Tests);

        Assert.Equal(identity, resolved.Identity);
        Assert.Equal(selector, resolved.Selector);
    }

    [Fact]
    public async Task ResolveSelectedTests_HandlesExactProjectAndDisplayFallbackWithoutInventingUnknownTests()
    {
        var operations = new DotNetDeepOperations(new FakeRunner(), await ObserverAsync());
        var catalog = new[]
        {
            new TestCatalogEntry("runner:one", "One", "xunit", "App.csproj", "Example.Tests.One"),
            new TestCatalogEntry("runner:two", "Two", "xunit", "App.csproj", "Example.Tests.Two"),
            new TestCatalogEntry("runner:other", "Other", "xunit", "Other.csproj", "Other.Tests.Test")
        };
        var selected = new[]
        {
            new SelectedTestReference("runner:one", "One"),
            new SelectedTestReference("dotnet-project:App.csproj", "App.csproj"),
            new SelectedTestReference("dotnet-project:Empty.csproj", "Empty.csproj"),
            new SelectedTestReference("dotnet:test:v1:Other.csproj:dotnet:type:Other.Tests:member:Test", "Other.Tests.Test()"),
            new SelectedTestReference("unknown:test", "Unknown")
        };

        var resolution = operations.ResolveSelectedTests(selected, catalog);

        Assert.Equal(
            ["dotnet:test:v1:Other.csproj:dotnet:type:Other.Tests:member:Test", "runner:one", "runner:two"],
            resolution.Tests.Select(test => test.Identity));
        Assert.Equal(
            ["dotnet-project:Empty.csproj", "unknown:test"],
            resolution.UnresolvedTests.Select(test => test.Identity));
    }

    [Fact]
    public async Task ResolveSelectedTests_RejectsNullInputs()
    {
        var operations = new DotNetDeepOperations(new FakeRunner(), await ObserverAsync());

        Assert.Throws<ArgumentNullException>(() => operations.ResolveSelectedTests(null!, []));
        Assert.Throws<ArgumentNullException>(() => operations.ResolveSelectedTests([], null!));
    }

    [Fact]
    public async Task Execute_ParsesTrxFailureAndBoundsDiagnostics()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
        {
            if (request.Arguments[0] != "test") return Success(request);
            var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "merkle.trx"), "<TestRun><Results><UnitTestResult outcome=\"Failed\" duration=\"00:00:01.2500000\"><Output><ErrorInfo><Message>bad</Message><StackTrace>trace</StackTrace></ErrorInfo></Output></UnitTestResult></Results></TestRun>");
            return new ProcessResult(1, string.Empty, string.Empty);
        }), observer);

        var result = Assert.Single(await operations.ExecuteAsync(new SelectedExecutionRequest(Context(), Fingerprint(), [Test()]), default));

        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1.25), result.Duration);
        Assert.Contains("bad", result.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_AggregatesEveryResultProducedByAMethodSelector()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
        {
            if (request.Arguments[0] != "test") return Success(request);
            var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "merkle.trx"),
                "<TestRun><Results>" +
                "<UnitTestResult outcome=\"Passed\" duration=\"00:00:00.2500000\" />" +
                "<UnitTestResult outcome=\"Failed\" duration=\"00:00:00.7500000\"><Output><ErrorInfo><Message>second row failed</Message></ErrorInfo></Output></UnitTestResult>" +
                "</Results></TestRun>");
            return new ProcessResult(1, string.Empty, string.Empty);
        }), observer);

        var result = Assert.Single(await operations.ExecuteAsync(new SelectedExecutionRequest(Context(), Fingerprint(), [Test()]), default));

        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
        Assert.Contains("second row failed", result.Diagnostics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Execute_ClassifiesMissingSelectedTestResultAsAnalysisFailure(int exitCode)
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
            request.Arguments[0] == "test"
                ? new ProcessResult(exitCode, "No test matches the given testcase filter", string.Empty)
                : Success(request), writeSuccessfulTrx: false), observer);

        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.ExecuteAsync(new SelectedExecutionRequest(Context(), Fingerprint(), [Test()]), default).AsTask());

        Assert.Equal("TestResultUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_ClassifiesEmptyTrxAsAnalysisFailureEvenWhenRunnerExitsZero()
    {
        var observer = await ObserverAsync();
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
        {
            if (request.Arguments[0] != "test") return Success(request);
            var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "merkle.trx"), "<TestRun><Results /></TestRun>");
            return new ProcessResult(0, string.Empty, string.Empty);
        }), observer);

        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.ExecuteAsync(new SelectedExecutionRequest(Context(), Fingerprint(), [Test()]), default).AsTask());

        Assert.Equal("TestResultUnavailable", error.Code);
    }

    [Fact]
    public async Task Observe_RecordsAssemblyEvidenceWhenHookWritesArtifactPath()
    {
        var observer = await ObserverAsync();
        var artifact = Path.Combine(_root, "src/App/bin/Debug/net8.0/App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, "assembly");
        var operations = new DotNetDeepOperations(new FakeRunner(request =>
        {
            if (request.Environment?.TryGetValue("MERKLE_OBSERVATION_FILE", out var output) == true)
            {
                File.WriteAllText(output!, artifact + Environment.NewLine);
            }
            return Success(request);
        }), observer);

        var scope = Assert.Single(await operations.ObserveAsync(new ObservationRequest(Context(), Fingerprint(artifact), [Test()], RunId: "run"), default));

        Assert.Equal(ObservationCompleteness.Complete, scope.Completeness);
        var observation = Assert.Single(scope.Observations);
        Assert.Equal("dotnet:project:src/App/App.csproj", observation.UnitIdentity);
        Assert.Equal("run", observation.RunId);
    }

    [Fact]
    public async Task Observe_ReturnsIncompleteScopeWhenHookDoesNotWriteOutput()
    {
        var operations = new DotNetDeepOperations(new FakeRunner(), await ObserverAsync());

        var scope = Assert.Single(await operations.ObserveAsync(new ObservationRequest(Context(), Fingerprint(), [Test()]), default));

        Assert.Equal(ObservationCompleteness.Incomplete, scope.Completeness);
        Assert.Empty(scope.Observations);
        Assert.Contains("ObservationIncomplete", Assert.Single(scope.Warnings), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DeepAdapterContext Context()
    {
        var files = new[]
        {
            Snapshot("Repo.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /><Project Path=\"tests/App.Tests/App.Tests.csproj\" /></Solution>"),
            Snapshot("src/App/App.csproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
            Snapshot("tests/App.Tests/App.Tests.csproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>")
        };
        var snapshot = new RepositorySnapshot(new SnapshotIdentity("worktree", "WORKTREE", "git"), _root, "repo", files);
        return new DeepAdapterContext(snapshot, StateDirectory: Path.Combine(_root, "state"));
    }

    private static SnapshotFile Snapshot(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new SnapshotFile(path, Convert.ToHexString(SHA256.HashData(bytes)), bytes);
    }

    private async Task<string> ObserverAsync()
    {
        var observer = Path.Combine(_root, "observer.dll");
        await File.WriteAllTextAsync(observer, "observer");
        return observer;
    }

    private static BuildFingerprint Fingerprint(string? assembly = null) => new(
        "fingerprint", "worktree", "Repo.slnx", "Debug", "AnyCPU", "10.0", ["net8.0"], "adapter", "observer",
        assembly is null ? [] : [new BuildArtifact("src/App/App.csproj", assembly, null, "hash", null)]);

    private static TestCatalogEntry Test() => new("test", "Test", "xunit", "tests/App.Tests/App.Tests.csproj", "App.Tests.Test");
    private static ProcessResult Success(ProcessRequest request) => request.Arguments[0] == "--version" ? new ProcessResult(0, "10.0.301\n", string.Empty) : new ProcessResult(0, string.Empty, string.Empty);

    private sealed class FakeRunner(
        Func<ProcessRequest, ProcessResult>? handler = null,
        bool writeSuccessfulTrx = true) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = (handler ?? Success)(request);
            if (writeSuccessfulTrx &&
                result.ExitCode == 0 &&
                request.Arguments.Contains("--results-directory"))
            {
                var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                Directory.CreateDirectory(directory);
                if (!Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Any())
                {
                    File.WriteAllText(
                        Path.Combine(directory, "merkle.trx"),
                        "<TestRun><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>");
                }
            }
            return ValueTask.FromResult(result);
        }
    }
}
