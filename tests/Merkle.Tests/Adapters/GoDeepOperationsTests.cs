using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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

        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(_root, "state", "fingerprints"), "*.manifest.json"));
        var contents = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(manifest, contents.Replace(
            prepared.Fingerprint.Value,
            new string('0', prepared.Fingerprint.Value.Length),
            StringComparison.Ordinal));
        var incompatible = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.PrepareBuildAsync(new BuildPreparationRequest(context, NoBuild: true), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", incompatible.Code);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("scope")]
    [InlineData("configuration")]
    [InlineData("platform")]
    [InlineData("toolchain")]
    [InlineData("adapter")]
    [InlineData("observer")]
    [InlineData("targets")]
    [InlineData("packages")]
    [InlineData("modules")]
    [InlineData("artifact-scope")]
    [InlineData("artifact-path")]
    [InlineData("artifact-hash")]
    [InlineData("artifacts-null")]
    [InlineData("fingerprint-null")]
    public async Task PrepareBuild_NoBuildRejectsTamperedManifestMetadata(string field)
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var context = Context(snapshot);
        var operations = new GoDeepOperations(GoRunner(snapshot, createArtifacts: true));
        await operations.PrepareBuildAsync(new BuildPreparationRequest(context), default);
        var manifestPath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "state", "fingerprints"), "*.manifest.json"));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var fingerprint = manifest["fingerprint"]!.AsObject();

        switch (field)
        {
            case "snapshot": fingerprint["snapshotId"] = "other-snapshot"; break;
            case "scope": fingerprint["workspacePath"] = "other/go.mod"; break;
            case "configuration": fingerprint["configuration"] = "Release"; break;
            case "platform": fingerprint["platform"] = "linux"; break;
            case "toolchain": fingerprint["toolchainVersion"] = "go1.22.5 linux/amd64"; break;
            case "adapter": fingerprint["adapterVersion"] = "other-adapter"; break;
            case "observer": fingerprint["observerVersion"] = "other-observer"; break;
            case "targets": fingerprint["targets"] = new JsonArray("other.test"); break;
            case "packages": manifest["packages"] = new JsonArray("other.test"); break;
            case "modules": manifest["modules"] = new JsonArray("other-module"); break;
            case "artifact-scope": fingerprint["artifacts"]![0]!["scopePath"] = "other.test"; break;
            case "artifact-path": fingerprint["artifacts"]![0]!["artifactPath"] = Path.Combine(_root, "other.test"); break;
            case "artifact-hash": fingerprint["artifacts"]![0]!["artifactHash"] = new string('0', 64); break;
            case "artifacts-null": fingerprint["artifacts"] = null; break;
            case "fingerprint-null": manifest["fingerprint"] = null; break;
            default: throw new InvalidOperationException($"Unknown manifest field '{field}'.");
        }

        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.PrepareBuildAsync(new BuildPreparationRequest(context, NoBuild: true), default).AsTask());

        Assert.Equal("ArtifactsUnavailable", error.Code);
    }

    [Fact]
    public async Task PrepareBuild_SeparatesArtifactsAcrossEffectiveBuildConfigurations()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var debug = Context(snapshot);
        var release = debug with { Configuration = "Release" };
        var operations = new GoDeepOperations(GoRunner(snapshot, createArtifacts: true));

        var debugBuild = await operations.PrepareBuildAsync(new BuildPreparationRequest(debug), default);
        var releaseBuild = await operations.PrepareBuildAsync(new BuildPreparationRequest(release), default);

        Assert.NotEqual(
            Assert.Single(debugBuild.Fingerprint.Artifacts).ArtifactPath,
            Assert.Single(releaseBuild.Fingerprint.Artifacts).ArtifactPath);
        var reusedDebug = await operations.PrepareBuildAsync(new BuildPreparationRequest(debug, NoBuild: true), default);
        Assert.Equal(debugBuild.Fingerprint.Value, reusedDebug.Fingerprint.Value);
    }

    [Fact]
    public async Task Discover_UsesExactIdentitiesAndFailsOnProcessOrMalformedOutput()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var context = Context(snapshot);
        var fingerprint = await PrepareFingerprintAsync(snapshot);

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

        var packageListFailure = new GoDeepOperations(GoRunner(snapshot, listExitCode: 1, listError: "module resolution failed"));
        var listError = await Assert.ThrowsAsync<AnalysisException>(() => packageListFailure.DiscoverAsync(context, fingerprint, default).AsTask());
        Assert.Equal("TestDiscoveryFailed", listError.Code);

        var packageErrorRunner = new FakeRunner((request, _) => ValueTask.FromResult(request.Arguments[0] == "env"
            ? Success(ToolchainJson())
            : request.Arguments[0] == "list"
                ? Success("{\"ImportPath\":\"example.test\",\"Error\":{\"Err\":\"package failed\"}}\n")
                : Success("ok example.test 0.001s\n")));
        var packageError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(packageErrorRunner).DiscoverAsync(context, fingerprint, default).AsTask());
        Assert.Equal("TestDiscoveryFailed", packageError.Code);
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
        var result = await new GoDeepOperations(runner).ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [test]), default);

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
        var fingerprint = await PrepareFingerprintAsync(snapshot);

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
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var test = Test("BenchmarkBeta");

        var matching = new GoDeepOperations(GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Package\":\"example.test\",\"Test\":\"BenchmarkBeta\"}\n{\"Action\":\"pass\",\"Package\":\"example.test\"}\n"));
        var passed = await matching.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), default);
        Assert.Equal(TestOutcome.Passed, Assert.Single(passed).Outcome);

        var noMatch = new GoDeepOperations(GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"pass\",\"Package\":\"example.test\"}\n"));
        var error = await Assert.ThrowsAsync<AnalysisException>(() => noMatch.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), default).AsTask());
        Assert.Equal("TestResultUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_PackageInitializationCrashIsATestCrashNotACompilationFailure()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var runner = GoRunner(
            snapshot,
            executionOutput: _ => "{\"Action\":\"start\",\"Package\":\"example.test\"}\n{\"Action\":\"output\",\"Package\":\"example.test\",\"Output\":\"panic in init\"}\n{\"Action\":\"fail\",\"Package\":\"example.test\"}\n",
            executionExitCode: 1,
            executionError: "test binary exited");

        var result = await new GoDeepOperations(runner).ExecuteAsync(
            new SelectedExecutionRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [Test("TestAlpha")]),
            default);

        var execution = Assert.Single(result);
        Assert.Equal(TestOutcome.Crashed, execution.Outcome);
        Assert.Contains("test binary exited", execution.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ArtifactLaunchFailureIsClassifiedAsArtifactsUnavailable()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var runner = GoRunner(snapshot, executionOutput: _ => "test2json: fork/exec artifact: permission denied\n", executionExitCode: 1, executionError: "permission denied");
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(runner).ExecuteAsync(
            new SelectedExecutionRequest(Context(snapshot), fingerprint, [Test("TestAlpha")]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_RejectsTestWhosePackageHasNoOwningModule()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var test = new TestCatalogEntry("golang:unknown.test:TestAlpha", "TestAlpha", "go-testing", "unknown.test", "TestAlpha");
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(snapshot)).ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [test]), default).AsTask());
        Assert.Equal("ModuleNotFound", error.Code);
    }

    [Fact]
    public async Task Execute_RejectsMissingOrTamperedArtifacts()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var context = Context(snapshot);
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var artifactPath = Assert.Single(fingerprint.Artifacts).ArtifactPath;
        var operations = new GoDeepOperations(GoRunner(snapshot));

        File.Delete(artifactPath);
        var missingError = await Assert.ThrowsAsync<AnalysisException>(() => operations.ExecuteAsync(new SelectedExecutionRequest(context, fingerprint, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", missingError.Code);

        RestoreArtifact(artifactPath);
        await File.AppendAllTextAsync(artifactPath, "tampered");
        var hashError = await Assert.ThrowsAsync<AnalysisException>(() => operations.ExecuteAsync(new SelectedExecutionRequest(context, fingerprint, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", hashError.Code);

        RestoreArtifact(artifactPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(artifactPath, File.GetUnixFileMode(artifactPath) & ~UnixFileMode.UserExecute & ~UnixFileMode.GroupExecute & ~UnixFileMode.OtherExecute);
        var permissionError = await Assert.ThrowsAsync<AnalysisException>(() => operations.ExecuteAsync(new SelectedExecutionRequest(context, fingerprint, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", permissionError.Code);

        RestoreArtifact(artifactPath);
        var wrongSnapshot = fingerprint with { SnapshotId = "different-snapshot" };
        var snapshotError = await Assert.ThrowsAsync<AnalysisException>(() => operations.ExecuteAsync(new SelectedExecutionRequest(context, wrongSnapshot, [test]), default).AsTask());
        Assert.Equal("ArtifactsUnavailable", snapshotError.Code);
    }

    [Fact]
    public async Task DeepOperations_RejectStaleToolchainAndFingerprintValue()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var context = Context(snapshot);
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var staleToolchain = fingerprint with { ToolchainVersion = "go1.22.5 linux/amd64" };
        var staleValue = fingerprint with { Value = new string('0', fingerprint.Value.Length) };
        var operations = new GoDeepOperations(GoRunner(snapshot));

        var discoveryError = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.DiscoverAsync(context, staleToolchain, default).AsTask());
        var executionError = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.ExecuteAsync(new SelectedExecutionRequest(context, staleValue, [Test("TestAlpha")]), default).AsTask());
        var observationError = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.ObserveAsync(new ObservationRequest(context, staleToolchain, [Test("TestAlpha")]), default).AsTask());

        Assert.All([discoveryError, executionError, observationError], error => Assert.Equal("ArtifactsUnavailable", error.Code));
    }

    [Theory]
    [InlineData("value")]
    [InlineData("scope")]
    [InlineData("configuration")]
    [InlineData("platform")]
    [InlineData("adapter")]
    [InlineData("observer")]
    [InlineData("targets")]
    public async Task Execute_RejectsFingerprintMetadataMismatch(string field)
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var incompatible = field switch
        {
            "value" => fingerprint with { Value = string.Empty },
            "scope" => fingerprint with { WorkspacePath = "other/go.mod" },
            "configuration" => fingerprint with { Configuration = "Release" },
            "platform" => fingerprint with { Platform = "linux" },
            "adapter" => fingerprint with { AdapterVersion = "other-adapter" },
            "observer" => fingerprint with { ObserverVersion = "other-observer" },
            "targets" => fingerprint with { Targets = ["other.test"] },
            _ => throw new InvalidOperationException($"Unknown fingerprint field '{field}'.")
        };

        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            new GoDeepOperations(GoRunner(snapshot)).ExecuteAsync(
                new SelectedExecutionRequest(Context(snapshot), incompatible, [Test("TestAlpha")]),
                default).AsTask());

        Assert.Equal("ArtifactsUnavailable", error.Code);
    }

    [Fact]
    public async Task Execute_UsesAnIsolatedMaterializedWorkspacePerOperation()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var runner = GoRunner(snapshot);
        var operations = new GoDeepOperations(runner);
        var request = new SelectedExecutionRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [Test("TestAlpha")]);

        await operations.ExecuteAsync(request, default);
        await operations.ExecuteAsync(request, default);

        var workingDirectories = runner.Requests
            .Where(value => value.Arguments[0] == "tool")
            .Select(value => value.WorkingDirectory)
            .ToArray();
        Assert.Equal(2, workingDirectories.Length);
        Assert.NotEqual(workingDirectories[0], workingDirectories[1]);
    }

    [Fact]
    public async Task Observe_MapsPositiveCoverProfileToRepoRelativeFile_AndAlwaysReportsBlindSpots()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var runner = GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n", writeCoverage: true);
        var result = await new GoDeepOperations(runner).ObserveAsync(new ObservationRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [test], RunId: "run-1"), default);

        var scope = Assert.Single(result);
        Assert.Equal(ObservationCompleteness.Complete, scope.Completeness);
        Assert.Equal("golang:file:example.go", Assert.Single(scope.Observations).UnitIdentity);
        Assert.Contains(scope.Warnings, warning => warning.Contains("Blind spots", StringComparison.Ordinal));
        Assert.Contains(scope.Observations, observation => observation.BlindSpots.Contains("reflection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Observe_ReusedRunIdStillUsesOperationUniqueProfiles()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var runner = GoRunner(
            snapshot,
            executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n",
            writeCoverage: true);
        var operations = new GoDeepOperations(runner);
        var request = new ObservationRequest(Context(snapshot), fingerprint, [Test("TestAlpha")], RunId: "reused-run");

        await operations.ObserveAsync(request, default);
        await operations.ObserveAsync(request, default);

        var profiles = runner.Requests
            .Where(request => request.Arguments[0] == "tool")
            .Select(request => request.Arguments.Single(argument => argument.StartsWith("-test.coverprofile=", StringComparison.Ordinal)))
            .ToArray();
        Assert.Equal(2, profiles.Length);
        Assert.NotEqual(profiles[0], profiles[1]);
    }

    [Fact]
    public async Task Observe_EmptyOrZeroProfileIsIncompleteAndStillWarns()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        foreach (var profile in new[] { "mode: set\n", "mode: set\nexample.test/example.go:1.1,1.2 1 0\n", "mode: set\n/tmp/outside.go:1.1,1.2 1 1\n" })
        {
            var runner = GoRunner(snapshot, executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n", coverageText: profile);
            var result = await new GoDeepOperations(runner).ObserveAsync(new ObservationRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [test]), default);
            var scope = Assert.Single(result);
            Assert.Equal(ObservationCompleteness.Incomplete, scope.Completeness);
            Assert.Empty(scope.Observations);
            Assert.Contains(scope.Warnings, warning => warning.Contains("Blind spots", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Observe_MixedValidAndMalformedProfileIsIncomplete()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var runner = GoRunner(
            snapshot,
            executionOutput: _ => "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n",
            coverageText: "mode: set\nexample.test/example.go:1.1,1.2 1 1\nmalformed coverage line\n");

        var result = await new GoDeepOperations(runner).ObserveAsync(
            new ObservationRequest(Context(snapshot), await PrepareFingerprintAsync(snapshot), [Test("TestAlpha")]),
            default);

        var scope = Assert.Single(result);
        Assert.Equal(ObservationCompleteness.Incomplete, scope.Completeness);
        Assert.Empty(scope.Observations);
    }

    [Fact]
    public async Task Execute_ReportsTimeoutAndPropagatesCallerCancellation()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var test = Test("TestAlpha");
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var hanging = new GoDeepOperations(new FakeRunner(async (request, token) =>
        {
            if (request.Arguments[0] == "tool") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (request.Arguments[0] == "env") return Success(ToolchainJson());
            if (request.Arguments[0] == "list") return Success(ListJson(snapshot, request.WorkingDirectory));
            return Success(string.Empty);
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
    public async Task PrepareBuild_GoWorkspaceResolvesParentRelativeModuleWithinRepository()
    {
        var snapshot = Snapshot(
            "configs/go.work", "go 1.22\n\nuse ../module\n",
            "module/go.mod", "module example.test/module\n\ngo 1.22\n",
            "module/example.go", Source("module"),
            "module/example_test.go", TestSource("module"));

        var result = await new GoDeepOperations(GoRunner(snapshot, createArtifacts: true))
            .PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default);

        Assert.Equal("configs/go.work", result.Fingerprint.WorkspacePath);
        Assert.Equal("example.test/module", Assert.Single(result.Fingerprint.Targets));
        Assert.Equal("example.test/module", Assert.Single(result.Fingerprint.Artifacts).ScopePath);
    }

    [Fact]
    public async Task PrepareBuild_RejectsConfiguredFileThatIsNotAGoManifest()
    {
        var snapshot = Snapshot("not-go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var context = Context(snapshot) with { ConfiguredSolution = "not-go.mod" };

        var error = await Assert.ThrowsAsync<ConfigurationException>(() =>
            new GoDeepOperations(GoRunner(snapshot)).PrepareBuildAsync(new BuildPreparationRequest(context), default).AsTask());

        Assert.Equal("ConfiguredSolutionNotFound", error.Code);
    }

    [Fact]
    public async Task PrepareBuild_ClassifiesGoLaunchFailureAsToolchainUnavailable()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n\ngo 1.22\n", "example.go", Source("example"));
        var goPath = Path.Combine(_root, "disappearing-go");
        await File.WriteAllTextAsync(goPath, "placeholder");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(goPath, File.GetUnixFileMode(goPath) | UnixFileMode.UserExecute);
        var operations = new GoDeepOperations(
            new FakeRunner((_, _) => throw new Win32Exception("could not start")),
            goPath);

        var error = await Assert.ThrowsAsync<AnalysisException>(() =>
            operations.PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());

        Assert.Equal("GoToolchainUnavailable", error.Code);
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

    [Fact]
    public async Task SnapshotMaterializationClassifiesInvalidPathCharactersAsUnsafe()
    {
        var invalidPath = Snapshot("invalid\0.go", "package invalid\n");
        var pathError = await Assert.ThrowsAsync<AnalysisException>(() =>
            new GoDeepOperations(GoRunner(invalidPath)).PrepareBuildAsync(
                new BuildPreparationRequest(Context(invalidPath, reference: "IMMUTABLE")),
                default).AsTask());

        var invalidLink = FromFiles(
            FileSnapshot("go.mod", "module example.test\n"),
            new SnapshotFile("link.go", "link", Encoding.UTF8.GetBytes("invalid\0target"), SnapshotEntryKind.SymbolicLink));
        var linkError = await Assert.ThrowsAsync<AnalysisException>(() =>
            new GoDeepOperations(GoRunner(invalidLink)).PrepareBuildAsync(
                new BuildPreparationRequest(Context(invalidLink, reference: "IMMUTABLE")),
                default).AsTask());

        Assert.Equal("UnsafeSnapshotPath", pathError.Code);
        Assert.Equal("UnsafeSnapshotPath", linkError.Code);
    }

    [Fact]
    public void ResolveSelectedTests_ReturnsExactMatchesAndUnresolvedReferencesDeterministically()
    {
        var operations = new GoDeepOperations(GoRunner(Snapshot("go.mod", "module example.test\n")));
        var catalog = new[] { Test("TestAlpha") };
        var result = operations.ResolveSelectedTests(
            [new SelectedTestReference("missing", "missing"), new SelectedTestReference("golang:example.test:TestAlpha", "TestAlpha"), new SelectedTestReference("golang:example.test:TestAlpha", "TestAlpha")],
            catalog);

        Assert.Equal("golang:example.test:TestAlpha", Assert.Single(result.Tests).Identity);
        Assert.Equal("missing", Assert.Single(result.UnresolvedTests).Identity);
    }

    [Fact]
    public async Task Discover_ReportsNoTestsWhenListOutputContainsOnlyPackageStatus()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var result = await new GoDeepOperations(GoRunner(snapshot, listOutput: "ok example.test 0.001s\n"))
            .DiscoverAsync(Context(snapshot), fingerprint, default);

        Assert.Empty(result.Tests);
        Assert.Contains("No Go tests were discovered.", result.Warnings);
    }

    [Fact]
    public async Task Discover_IgnoresNonArrayTestFileProperties()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var runner = new FakeRunner((request, _) => ValueTask.FromResult(request.Arguments[0] == "env"
            ? Success(ToolchainJson())
            : request.Arguments[0] == "list"
                ? Success("{\"ImportPath\":\"example.test\",\"Dir\":\"work\",\"TestGoFiles\":\"not-an-array\"}\n")
                : Success("ok example.test 0.001s\n")));
        var operations = new GoDeepOperations(runner);
        var fingerprint = await operations.PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default);
        var result = await operations.DiscoverAsync(Context(snapshot), fingerprint.Fingerprint, default);
        Assert.Empty(result.Tests);
        Assert.Contains("No Go tests were discovered.", result.Warnings);
    }

    [Fact]
    public async Task Execute_ParsesNonJsonNoiseSkipAndRuntimeCrashBranches()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var skipped = new GoDeepOperations(GoRunner(snapshot, executionOutput: _ => "compiler noise\n{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"skip\",\"Test\":\"TestAlpha\",\"Elapsed\":-1}\n"));
        var skippedResult = await skipped.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [Test("TestAlpha")]), default);
        Assert.Equal(TestOutcome.Skipped, Assert.Single(skippedResult).Outcome);
        Assert.Equal(TimeSpan.Zero, skippedResult[0].Duration);

        var crashed = new GoDeepOperations(GoRunner(
            snapshot,
            executionOutput: _ => "compiler noise\n{\"Action\":\"output\",\"Package\":\"example.test\",\"Output\":\"panic: runtime failure\"}\n",
            executionExitCode: 1,
            executionError: "runtime exited"));
        var crashedResult = await crashed.ExecuteAsync(new SelectedExecutionRequest(Context(snapshot), fingerprint, [Test("TestAlpha")]), default);
        Assert.Equal(TestOutcome.Crashed, Assert.Single(crashedResult).Outcome);
    }

    [Fact]
    public async Task Observe_TimeoutProducesIncompleteTimedOutScope()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var fingerprint = await PrepareFingerprintAsync(snapshot);
        var runner = new FakeRunner(async (request, token) =>
        {
            if (request.Arguments[0] == "env") return Success(ToolchainJson());
            if (request.Arguments[0] == "list") return new ProcessResult(0, ListJson(snapshot, request.WorkingDirectory), string.Empty);
            if (request.Arguments[0] == "tool") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success(string.Empty);
        });
        var result = await new GoDeepOperations(runner).ObserveAsync(
            new ObservationRequest(Context(snapshot), fingerprint, [Test("TestAlpha")], Timeout: TimeSpan.FromMilliseconds(20)), default);

        var scope = Assert.Single(result);
        Assert.Equal(ObservationCompleteness.Incomplete, scope.Completeness);
        Assert.Equal(TestOutcome.TimedOut, scope.Execution.Outcome);
    }

    [Fact]
    public async Task ResolveScope_RejectsMissingModulesAmbiguousWorkspacesAndInvalidModuleFiles()
    {
        var placeholderFingerprint = PlaceholderFingerprint();
        var empty = Snapshot();
        var noSolution = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(empty)).DiscoverAsync(Context(empty), placeholderFingerprint, default).AsTask());
        Assert.Equal("SolutionNotFound", noSolution.Code);

        var ambiguous = Snapshot("a/go.work", "go 1.22\nuse ./mod\n", "a/mod/go.mod", "module example.test/a\n", "b/go.work", "go 1.22\nuse ./mod\n", "b/mod/go.mod", "module example.test/b\n");
        var multiple = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(ambiguous)).DiscoverAsync(Context(ambiguous), placeholderFingerprint, default).AsTask());
        Assert.Equal("MultipleSolutions", multiple.Code);

        var noModules = Snapshot("go.work", "go 1.22\n");
        var noModulesError = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(noModules)).DiscoverAsync(Context(noModules), placeholderFingerprint, default).AsTask());
        Assert.Equal("SolutionHasNoModules", noModulesError.Code);

        var invalid = Snapshot("go.mod", "package not-a-module\n");
        var invalidError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(invalid)).DiscoverAsync(Context(invalid), placeholderFingerprint, default).AsTask());
        Assert.Equal("InvalidModuleFile", invalidError.Code);

        var missingUse = Snapshot("go.work", "go 1.22\nuse ./missing\n");
        var missingUseError = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(missingUse)).DiscoverAsync(Context(missingUse), placeholderFingerprint, default).AsTask());
        Assert.Equal("ModuleNotFound", missingUseError.Code);

        var unsafeUse = Snapshot("go.work", "go 1.22\nuse ../outside\n");
        var unsafeUseError = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(unsafeUse)).DiscoverAsync(Context(unsafeUse), placeholderFingerprint, default).AsTask());
        Assert.Equal("UnsafeSnapshotPath", unsafeUseError.Code);

        var configuredMissing = Snapshot("go.mod", "module example.test\n");
        var configuredMissingError = await Assert.ThrowsAsync<ConfigurationException>(() => new GoDeepOperations(GoRunner(configuredMissing)).DiscoverAsync(Context(configuredMissing) with { ConfiguredSolution = "missing.go.mod" }, placeholderFingerprint, default).AsTask());
        Assert.Equal("ConfiguredSolutionNotFound", configuredMissingError.Code);
    }

    [Fact]
    public async Task PrepareBuild_RejectsOldGoBuildFailureAndMissingArtifact()
    {
        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"), "example_test.go", TestSource("example"));
        var oldGo = new FakeRunner((request, _) => ValueTask.FromResult(request.Arguments[0] == "env"
            ? Success(ToolchainJson("go1.21.9"))
            : Success(string.Empty)));
        var oldError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(oldGo).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());
        Assert.Equal("GoToolchainUnavailable", oldError.Code);

        var buildFailure = new FakeRunner((request, _) => ValueTask.FromResult(request.Arguments[0] == "env"
            ? Success(ToolchainJson())
            : request.Arguments[0] == "list"
                ? new ProcessResult(0, ListJson(snapshot, request.WorkingDirectory), string.Empty)
                : request.Arguments.Contains("-c") ? new ProcessResult(1, string.Empty, "compiler failed") : Success(string.Empty)));
        var buildError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(buildFailure).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());
        Assert.Equal("BuildFailed", buildError.Code);

        var missingArtifact = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(GoRunner(snapshot)).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());
        Assert.Equal("BuildFailed", missingArtifact.Code);
    }

    [Fact]
    public async Task SnapshotMaterializationAcceptsSafeSymlink()
    {
        var snapshot = FromFiles(
            FileSnapshot("go.mod", "module example.test\n"),
            FileSnapshot("example.go", Source("example")),
            new SnapshotFile("link.go", "link", Encoding.UTF8.GetBytes("example.go"), SnapshotEntryKind.SymbolicLink));
        var result = await new GoDeepOperations(GoRunner(snapshot, createArtifacts: true)).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default);
        Assert.NotEmpty(result.Fingerprint.Artifacts);
    }

    [Fact]
    public async Task ConstructorAndToolchainValidationRejectInvalidConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => new GoDeepOperations(null!));
        Assert.Throws<ArgumentException>(() => new GoDeepOperations(GoRunner(Snapshot()), " "));

        var snapshot = Snapshot("go.mod", "module example.test\n", "example.go", Source("example"));
        var missing = new GoDeepOperations(GoRunner(snapshot), Path.Combine(_root, "missing-go"));
        var error = await Assert.ThrowsAsync<CapabilityException>(() => missing.PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());
        Assert.Equal("DeepToolchainUnavailable", error.Code);

        var malformedVersion = new FakeRunner((request, _) => ValueTask.FromResult(request.Arguments[0] == "env" ? Success(ToolchainJson("devel")) : Success(string.Empty)));
        var versionError = await Assert.ThrowsAsync<AnalysisException>(() => new GoDeepOperations(malformedVersion).PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default).AsTask());
        Assert.Equal("GoToolchainUnavailable", versionError.Code);
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

    private async ValueTask<BuildFingerprint> PrepareFingerprintAsync(RepositorySnapshot snapshot)
    {
        var result = await new GoDeepOperations(GoRunner(snapshot, createArtifacts: true))
            .PrepareBuildAsync(new BuildPreparationRequest(Context(snapshot)), default);
        return result.Fingerprint;
    }

    private string CreateArtifact()
    {
        var path = Path.Combine(_root, "state", "artifacts", Guid.NewGuid().ToString("N") + ".test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RestoreArtifact(path);
        return path;
    }

    private static void RestoreArtifact(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test artifact");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }

    private BuildFingerprint PlaceholderFingerprint()
    {
        var artifactPath = CreateArtifact();
        return new BuildFingerprint(
            "placeholder",
            "snapshot",
            "<modules>",
            "Debug",
            "AnyCPU",
            "go1.22.5 darwin/arm64",
            ["example.test"],
            GoDeepOperations.AdapterVersion,
            GoDeepOperations.ObserverVersion,
            [new BuildArtifact("example.test", artifactPath, null, HashFile(artifactPath), null)]);
    }

    private FakeRunner GoRunner(
        RepositorySnapshot snapshot,
        string? listOutput = null,
        int listExitCode = 0,
        int testListExitCode = 0,
        string? listError = null,
        bool createArtifacts = false,
        Func<string, string>? executionOutput = null,
        int executionExitCode = 0,
        string? executionError = null,
        bool writeCoverage = false,
        string? coverageText = null) => new(async (request, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (request.Arguments[0] == "env") return Success(ToolchainJson());
        if (request.Arguments[0] == "list") return new ProcessResult(listExitCode, ListJson(snapshot, request.WorkingDirectory), listError ?? string.Empty);
        if (request.Arguments.Contains("-list")) return new ProcessResult(testListExitCode, listOutput ?? "TestAlpha\nok example.test 0.001s\n", listError ?? string.Empty);
        if (request.Arguments[0] == "test" && request.Arguments.Contains("-c"))
        {
            if (createArtifacts)
            {
                var output = request.Arguments[request.Arguments.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "test artifact");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(output, File.GetUnixFileMode(output) | UnixFileMode.UserExecute);
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
            return new ProcessResult(
                executionExitCode,
                executionOutput?.Invoke(selector) ?? "{\"Action\":\"run\",\"Test\":\"TestAlpha\"}\n{\"Action\":\"pass\",\"Test\":\"TestAlpha\"}\n",
                executionError ?? string.Empty);
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
    private static string ToolchainJson(string version = "go1.22.5") => $"{{\"GOARCH\":\"arm64\",\"GOOS\":\"darwin\",\"GOVERSION\":\"{version}\"}}";
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
