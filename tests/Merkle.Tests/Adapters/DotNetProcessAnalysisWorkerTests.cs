using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Merkle.Adapters.DotNet;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Tests.Adapters;

public sealed class DotNetProcessAnalysisWorkerTests
{
    [Fact]
    public async Task Analyze_SendsBoundedProtocolRequestAndReturnsWorkerFragment()
    {
        var runner = new FakeRunner(request =>
        {
            var sent = JsonSerializer.Deserialize(request.StandardInput!.Value.Span, DotNetWorkerJsonContext.Default.DotNetWorkerRequest)!;
            var response = new DotNetWorkerResponse("1.0", sent.RequestId, true,
                [new SourceUnit("dotnet:file:App.cs", SourceUnitKind.File, "App.cs", "hash", "signature")], [],
                [new TestDescriptor("test", "App.cs", "xunit")], ["conditional items were not evaluated"], null);
            return new ProcessResult(0, string.Empty, string.Empty,
                JsonSerializer.SerializeToUtf8Bytes(response, DotNetWorkerJsonContext.Default.DotNetWorkerResponse));
        });
        var worker = new DotNetProcessAnalysisWorker(runner, "worker.dll", "dotnet-test");

        var index = await worker.AnalyzeAsync(new AdapterIndexRequest(Snapshot(("App.cs", "class App {}")), null), default);

        Assert.Single(index.Units);
        Assert.Single(index.Tests);
        Assert.Equal("conditional items were not evaluated", Assert.Single(index.Warnings!));
        var process = Assert.Single(runner.Requests);
        Assert.Equal("dotnet-test", process.FileName);
        Assert.Equal(["worker.dll"], process.Arguments);
        Assert.Equal(32 * 1024 * 1024, process.MaxStandardOutputBytes);
        Assert.NotNull(process.StandardInput);
    }

    [Theory]
    [InlineData("wrong-version", "WorkerProtocolMismatch")]
    [InlineData("wrong-request", "WorkerProtocolMismatch")]
    public async Task Analyze_RejectsProtocolEnvelopeMismatch(string mismatch, string code)
    {
        var runner = new FakeRunner(request =>
        {
            var sent = JsonSerializer.Deserialize(request.StandardInput!.Value.Span, DotNetWorkerJsonContext.Default.DotNetWorkerRequest)!;
            var response = mismatch == "wrong-version"
                ? DotNetWorkerResponse.Failure(sent.RequestId, "unused", "unused") with { ProtocolVersion = "2.0" }
                : DotNetWorkerResponse.Failure("other", "unused", "unused");
            return Json(response);
        });

        var error = await Assert.ThrowsAsync<AnalysisException>(() => new DotNetProcessAnalysisWorker(runner, "worker.dll").AnalyzeAsync(new AdapterIndexRequest(Snapshot(("App.cs", "x")), null), default).AsTask());

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Analyze_UsesWorkerFailureBeforeExitCodeAndBoundsExitDiagnostic()
    {
        var runner = new FakeRunner(request =>
        {
            var sent = JsonSerializer.Deserialize(request.StandardInput!.Value.Span, DotNetWorkerJsonContext.Default.DotNetWorkerRequest)!;
            var response = DotNetWorkerResponse.Failure(sent.RequestId, "SemanticFailure", "source was invalid");
            return new ProcessResult(7, string.Empty, new string('e', 2_000), JsonSerializer.SerializeToUtf8Bytes(response, DotNetWorkerJsonContext.Default.DotNetWorkerResponse));
        });

        var error = await Assert.ThrowsAsync<AnalysisException>(() => new DotNetProcessAnalysisWorker(runner, "worker.dll").AnalyzeAsync(new AdapterIndexRequest(Snapshot(("App.cs", "x")), null), default).AsTask());

        Assert.Equal("SemanticFailure", error.Code);
    }

    [Fact]
    public async Task Analyze_RejectsMalformedJsonAndLaunchFailure()
    {
        var malformed = new DotNetProcessAnalysisWorker(new FakeRunner(_ => new ProcessResult(0, "not json", string.Empty)), "worker.dll");
        var malformedError = await Assert.ThrowsAsync<AnalysisException>(() => malformed.AnalyzeAsync(new AdapterIndexRequest(Snapshot(("App.cs", "x")), null), default).AsTask());
        Assert.Equal("WorkerProtocolMalformed", malformedError.Code);

        var launch = new DotNetProcessAnalysisWorker(new ThrowingRunner(), "worker.dll");
        var launchError = await Assert.ThrowsAsync<AnalysisException>(() => launch.AnalyzeAsync(new AdapterIndexRequest(Snapshot(("App.cs", "x")), null), default).AsTask());
        Assert.Equal("DotNetWorkerLaunchFailed", launchError.Code);
    }

    private static ProcessResult Json(DotNetWorkerResponse response) => new(0, string.Empty, string.Empty,
        JsonSerializer.SerializeToUtf8Bytes(response, DotNetWorkerJsonContext.Default.DotNetWorkerResponse));

    private static RepositorySnapshot Snapshot(params (string Path, string Content)[] files) => new(
        new SnapshotIdentity("id", "HEAD", "git"), "/repo", "repository",
        [.. files.Select(file => new SnapshotFile(file.Path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))), Encoding.UTF8.GetBytes(file.Content)))]);

    private sealed class FakeRunner(Func<ProcessRequest, ProcessResult> handler) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(handler(request));
        }
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("cannot launch");
    }
}
