using System.Security.Cryptography;
using System.Text;
using Merkle.Adapters.Go;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Tests.Adapters;

public sealed class GoDeepOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merkle-go-tests-" + Guid.NewGuid().ToString("N"));

    public GoDeepOperationsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PrepareBuild_UsesOwningNestedModuleAsWorkingDirectory_AndScopesArtifactsToThatModule()
    {
        var snapshot = Snapshot("nested/go.mod", "module example.test/nested\n\ngo 1.22\n", "nested/example.go", Source("nested"), "nested/example_test.go", TestSource("nested"));
        var context = Context(snapshot, reference: "IMMUTABLE");
        var runner = GoRunner(snapshot, createArtifacts: true);

        var result = await new GoDeepOperations(runner).PrepareBuildAsync(new BuildPreparationRequest(context), default);

        var build = Assert.Single(runner.Requests, request => request.Arguments.Contains("-c"));
        Assert.Contains(Path.Combine("workspaces", ""), build.WorkingDirectory, StringComparison.Ordinal);
        Assert.Equal("nested", Path.GetFileName(build.WorkingDirectory));
        Assert.Equal("example.test/nested", result.Fingerprint.Targets.Single());
        var artifact = Assert.Single(result.Fingerprint.Artifacts);
        Assert.Equal("example.test/nested", artifact.ScopePath);
        Assert.Contains(Path.Combine("state", "artifacts"), artifact.ArtifactPath, StringComparison.Ordinal);
        Assert.True(File.Exists(artifact.ArtifactPath));
    }

    [Fact]
    public async Task PrepareBuild_NoBuildRejectsMissingManifest_ThenAcceptsMatchingPriorBuild()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var context = Context(snapshot);
        var runner = GoRunner(snapshot, createArtifacts: true);
        var operations = new GoDeepOperations(runner);

        var missing = await Assert.ThrowsAsync<AnalysisException>(() => operations.PrepareBuildAsync(new BuildPreparationRequest(context, NoBuild: true), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", missing.Code);

        var prepared = await operations.PrepareBuildAsync(new BuildPreparationRequest(context), default);
        var noBuild = await operations.PrepareBuildAsync(new BuildPreparationRequest(context, NoBuild: true), default);

        Assert.Equal(prepared.Fingerprint.Value, noBuild.Fingerprint.Value);
        Assert.Empty(noBuild.Warnings);
    }

    [Fact]
    public async Task Discover_UsesExactIdentitiesAndFailsOnProcessOrMalformedOutput()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var context = Context(snapshot);
        var fingerprint = Fingerprint("example.test", CreateArtifact());

        var successful = new GoDeepOperations(GoRunner(snapshot, listOutput: "TestAlpha\nBenchmarkBeta\nFuzzGamma\nExample\nok example.test 0.001s\n"));
        var catalog = await successful.DiscoverAsync(context, fingerprint, default);
        Assert.Equal(
            ["golang:example.test:BenchmarkBeta", "golang:example.test:Example", "golang:example.test:FuzzGamma", "golang:example.test:TestAlpha"],
            catalog.Tests.Select(test => test.Identity));
        Assert.All(catalog.Tests, test => Assert.Equal("example.test", test.ExecutionScope));

        var failed = new GoDeepOperations(GoRunner(snapshot, testListExitCode: 1, listError: "compile error"));
        var processError = await Assert.ThrowsAsync<AnalysisException>(() => failed.DiscoverAsync(context, fingerprint, default).AsTask());
        Assert.Equal("TestDiscoveryFailed", processError.Code);

        var malformed = new GoDeepOperations(GoRunner(snapshot, listOutput: "TestAlpha\nnot-json-go-list-noise\n"));
        var malformedError = await Assert.ThrowsAsync<AnalysisException>(() => malformed.DiscoverAsync(context, fingerprint, default).AsTask());
        Assert.Equal("TestDiscoveryFailed", malformedError.Code);
    }

    [Theory]
    [InlineData("pass", TestOutcome.Passed)]
    [InlineData("fail", TestOutcome.Failed)]
    [InlineData("skip", TestOutcome.Skipped)]
    public async Task Execute_MapsGoTest2JsonOutcomes(string action, TestOutcome expected)
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var runner = GoRunner(snapshot, executionOutput: _ => $"{{\"Action\":\"run\",\"Test\":\"TestAlpha\"}}\n{{\"Action\":\"{action}\",\"Test\":\"TestAlpha\",\"Elapsed\":0.125}}\n");
        var result = await new GoDeepOperations(runner).ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), Fingerprint("example.test", CreateArtifact()), [test]), default);

        var execution = Assert.Single(result);
        Assert.Equal(expected, execution.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(0.125), execution.Duration);
        var invocation = Assert.Single(runner.Requests, request => request.Arguments[0] == "tool");
        Assert.Contains("-test.run", invocation.Arguments);
        Assert.Contains("^TestAlpha$", invocation.Arguments);
    }

    [Fact]
    public async Task Execute_UsesBenchmarkAndFuzzSpecificTest2JsonFlags()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var runner = GoRunner(snapshot, executionOutput: selector => $"{{\"Action\":\"run\",\"Test\":\"{selector}\"}}\n{{\"Action\":\"pass\",\"Test\":\"{selector}\"}}\n");
        var operations = new GoDeepOperations(runner);
        var fingerprint = Fingerprint("example.test", CreateArtifact());

        await operations.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [Test("BenchmarkBeta")]), default);
        await operations.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [Test("FuzzGamma")]), default);

        var benchmark = runner.Requests.Single(request => request.Arguments.Contains("-test.bench"));
        Assert.Contains("^$", benchmark.Arguments);
        Assert.Contains("^BenchmarkBeta$", benchmark.Arguments);
        Assert.Contains("1x", benchmark.Arguments);
        var fuzz = runner.Requests.Single(request => request.Arguments.Contains("-test.fuzz"));
        Assert.Contains("^$", fuzz.Arguments);
        Assert.Contains("^FuzzGamma$", fuzz.Arguments);
        Assert.Contains("1x", fuzz.Arguments);
    }

    [Fact]
    public async Task Execute_BenchmarkPackagePassRequiresMatchingRun_AndNoMatchFails()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var fingerprint = Fingerprint("example.test", CreateArtifact());
        var test = Test("BenchmarkBeta");

        var matching = new GoDeepOperations(GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Package\":\"example.test\",\"Test\":\"BenchmarkBeta\"}\n{\"Action\":\"pass\",\"Package\":\"example.test\"}\n"));
        var passed = await matching.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), default);
        Assert.Equal(TestOutcome.Passed, Assert.Single(passed).Outcome);

        var noMatch = new GoDeepOperations(GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"pass\",\"Package\":\"example.test\"}\n"));
        var error = await Assert.ThrowsAsync<AnalysisException>(() => noMatch.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), default).AsTask());
        Assert.Equal("TestResultUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_RejectsMissingOrTamperedArtifacts()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var context = Context(snapshot);
        var missing = Fingerprint("example.test", new BuildArtifact("example.test", Path.Combine(_root, "missing.test"), null, "hash", null));
        var missingError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(snapshot)).ExecuteAsync(new SelectedExecutionRequest(context, missing, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", missingError.Code);

        var path = CreateArtifact();
        var tampered = Fingerprint("example.test", new BuildArtifact("example.test", path, null, "not-the-file-hash", null));
        var hashError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(snapshot)).ExecuteAsync(new SelectedExecutionRequest(context, tampered, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", hashError.Code);
    }

    [Fact]
    public async Task Observe_MapsPositiveCoverProfileToRepoRelativeFile_AndAlwaysReportsBlindSpots()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var runner = GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n", writeCoverage: true);
        var result = await new GoDeepOperations(runner).ObserveAsync(new ObservationRequest(Context(snapshot), Fingerprint("example.test", CreateArtifact()), [test], RunId: "run-1"), default);

        var scope = Assert.Single(result);
        Assert.Equal(ObservationCompleteness.Complete, scope.Completeness);
        Assert.Equal("golang:file:example.go", Assert.Single(scope.Observations).UnitIdentity);
        Assert.Contains(scope.Warnings, warning => warning.Contains("Blind spots", StringComparison.Ordinal));
        Assert.Contains(scope.Observations, observation => observation.BlindSpots.Contains("reflection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Observe_EmptyOrZeroProfileIsIncompleteAndStillWarns()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        foreach (var profile in new[] { "mode: set\n", "mode: set\nexample.test/example.go:1.1,1.2 1 0\n" })
        {
            var runner = GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n", coverageText: profile);
            var result = await new GoDeepOperations(runner).ObserveAsync(new ObservationRequest(Context(snapshot), Fingerprint("example.test", CreateArtifact()), [test]), default);
            var scope = Assert.Single(result);
            Assert.Equal(ObservationCompleteness.Incomplete, scope.Completeness);
            Assert.Empty(scope.Observations);
            Assert.Contains(scope.Warnings, warning => warning.Contains("Blind spots", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Execute_ReportsTimeoutAndPropagatesCallerCancellation()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var fingerprint = Fingerprint("example.test", CreateArtifact());
        var hanging = new GoDeepOperations(new FakeRunner(async (request, token) =>
        {
            if (request.Arguments[0] == "tool") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success(request.Arguments[0] == "version" ? "go version go1.22.5 darwin/arm64\n" : string.Empty);
        }));
        var timed = await hanging.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test], TimeSpan.FromMilliseconds(20)), default);
        Assert.Equal(TestOutcome.TimedOut, Assert.Single(timed).Outcome);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task PrepareBuild_GoWorkspaceUsesAbsoluteGoworkAndNestedModuleCwd()
    {
        var snapshot = Snapshot("go.work", "go 1.22\n\nuse (\n\t./nested\n)\n", "nested/go.mod", "module example.test/nested\n\ngo 1.22\n", "nested/example.go", Source("nested"));
        var runner = GoRunner(snapshot, createArtifacts: true);
        var result = await new GoDeepOperations(runner).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default);

        Assert.NotEmpty(result.Fingerprint.Artifacts);
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(Path.GetFullPath(request.Environment!["GOWORK"]!), request.Environment["GOWORK"]);
            if (request.Arguments[0] == "list" || request.Arguments.Contains("-c"))
            {
                Assert.Contains(Path.Combine("workspaces", ""), request.WorkingDirectory, StringComparison.Ordinal);
                Assert.Equal("nested", Path.GetFileName(request.WorkingDirectory));
            }
        });
    }

    [Fact]
    public async Task SnapshotMaterializationRejectsUnsafePathsAndGitlinks()
    {
        var unsafePath = Snapshot("../outside.go", "package outside\n");
        var unsafeError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(unsafePath)).PrepareBuildAsync(new BuildPreparationRequest(Context(unsafePath, reference: "IMMUTABLE")), default).AsTask());
        Assert.Equal("UnsafeSnapshotPath", unsafeError.Code);

        var gitlink = new SnapshotFile("nested", "gitlink", [], SnapshotEntryKind.GitLink);
        var gitlinkSnapshot = FromFiles(
            FileSnapshot("go.mod", "module example.test\n\ngo 1.22\n"),
            FileSnapshot("example.go", Source("example")),
            gitlink);
        var gitlinkError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(gitlinkSnapshot)).PrepareBuildAsync(new BuildPreparationRequest(Context(gitlinkSnapshot, reference: "IMMUTABLE")), default).AsTask());
        Assert.Equal("GitLinkUnavailable", gitlinkError.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DeepAdapterContext Context(RepositorySnapshot snapshot, string? reference = null) => new(
        reference is null ? snapshot : new RepositorySnapshot(new SnapshotIdentity(snapshot.Identity.Value, reference, snapshot.Identity.Provider), snapshot.RepositoryRoot, snapshot.RepositoryIdentity, snapshot.Files),
        StateDirectory: Path.Combine(_root, "state"));

    private RepositorySnapshot Snapshot(params string[] pathAndContent)
    {
        var files = new List<SnapshotFile>();
        for (var index = 0; index < pathAndContent.Length; index += 2)
            files.Add(FileSnapshot(pathAndContent[index], pathAndContent[index + 1]));
        return new RepositorySnapshot(new SnapshotIdentity("snapshot", "WORKTREE", "git"), _root, "repo", files);
    }

    private SnapshotFile FileSnapshot(string path, string content) => new(path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Encoding.UTF8.GetBytes(content));

    private RepositorySnapshot FromFiles(params SnapshotFile[] files) => new(new SnapshotIdentity("snapshot", "WORKTREE", "git"), _root, "repo", files);

    private static string Source(string package) => $"package {package}\n\nfunc Add(a, b int) int {{ return a + b }}\n";
    private static string TestSource(string package) => $"package {package}\n\nimport \"testing\"\n\nfunc TestAlpha(t *testing.T) {{}}\nfunc BenchmarkBeta(b *testing.B) {{}}\nfunc FuzzGamma(f *testing.F) {{}}\n";
    private static TestCatalogEntry Test(string selector) => new($"golang:example.test:{selector}", selector, "go-testing", "example.test", selector);

    private string CreateArtifact()
    {
        var path = Path.Combine(_root, "state", "artifacts", Guid.NewGuid().ToString("N") + ".test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test artifact");
        return path;
    }

    private BuildFingerprint Fingerprint(string scope, string artifactPath) => new(
        "fingerprint", "snapshot", "<modules>", "Debug", "AnyCPU", "go1.22.5", [scope], GoDeepOperations.AdapterVersion, GoDeepOperations.ObserverVersion,
        [new BuildArtifact(scope, artifactPath, null, HashFile(artifactPath), null)]);

    private static BuildFingerprint Fingerprint(string scope, BuildArtifact artifact) => new(
        "fingerprint", "snapshot", "<modules>", "Debug", "AnyCPU", "go1.22.5", [scope], GoDeepOperations.AdapterVersion, GoDeepOperations.ObserverVersion,
        [artifact]);

    private FakeRunner GoRunner(
        RepositorySnapshot snapshot,
        string? listOutput = null,
        int listExitCode = 0,
        int testListExitCode = 0,
        string? listError = null,
        bool createArtifacts = false,
        Func<string, string>? executionOutput = null,
        bool writeCoverage = false,
        string? coverageText = null) => new(async (request, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (request.Arguments[0] == "version") return Success("go version go1.22.5 darwin/arm64\n");
        if (request.Arguments[0] == "list") return new ProcessResult(listExitCode, ListJson(snapshot, request.WorkingDirectory), listError ?? string.Empty);
        if (request.Arguments.Contains("-list")) return new ProcessResult(testListExitCode, listOutput ?? "TestAlpha\nok example.test 0.001s\n", listError ?? string.Empty);
        if (request.Arguments[0] == "test" && request.Arguments.Contains("-c"))
        {
            if (createArtifacts)
            {
                var output = request.Arguments[request.Arguments.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "test artifact");
            }
            return Success(string.Empty);
        }
        if (request.Arguments[0] == "tool")
        {
            var selectorFlag = request.Arguments.Contains("-test.bench") ? "-test.bench" : request.Arguments.Contains("-test.fuzz") ? "-test.fuzz" : "-test.run";
            var selectorIndex = request.Arguments.ToList().IndexOf(selectorFlag) + 1;
            var selector = selectorIndex > 0 && selectorIndex < request.Arguments.Count ? request.Arguments[selectorIndex].Trim('^', '$') : "TestAlpha";
            var profile = request.Arguments.FirstOrDefault(value => value.StartsWith("-test.coverprofile=", StringComparison.Ordinal))?["-test.coverprofile=".Length..];
            if (profile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                File.WriteAllText(profile, coverageText ?? (writeCoverage ? "mode: set\nexample.test/example.go:1.1,1.2 1 1\n" : "mode: set\n"));
            }
            return Success(executionOutput?.Invoke(selector) ?? "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n");
        }
        return Success(string.Empty);
    });

    private static string ListJson(RepositorySnapshot snapshot, string workingDirectory)
    {
        var module = snapshot.Files.FirstOrDefault(file => file.Path.EndsWith("go.mod", StringComparison.Ordinal))?.Path ?? "go.mod";
        var moduleText = snapshot.Files.FirstOrDefault(file => file.Path == module)?.Content.ToArray() ?? [];
        var modulePath = Encoding.UTF8.GetString(moduleText).Split('\n').FirstOrDefault(line => line.StartsWith("module ", StringComparison.Ordinal))?[7..].Trim() ?? "example.test";
        var packageDirectory = Path.Combine(workingDirectory, Path.GetDirectoryName(module) ?? string.Empty).Replace("\\", "\\\\");
        return $"{{\"ImportPath\":\"{modulePath}\",\"Dir\":\"{packageDirectory}\",\"TestGoFiles\":[\"example_test.go\"]}}\n";
    }

    private static ProcessResult Success(string output) => new(0, output, string.Empty);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class FakeRunner(Func<ProcessRequest, CancellationToken, ValueTask<ProcessResult>> handler) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public async ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await handler(request, cancellationToken);
        }
    }
}
