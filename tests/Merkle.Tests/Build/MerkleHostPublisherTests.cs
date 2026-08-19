using Merkle.Build;
using Merkle.Core.Processes;

namespace Merkle.Tests.Build;

public sealed class MerkleHostPublisherTests
{
    [Fact]
    public async Task PublishWithTests_ValidatesHostBeforePublishingWithoutDirectAdapterCopy()
    {
        var runner = new RecordingProcessRunner();
        var context = Context("osx-arm64");
        var request = new BuildRequest(
            BuildCommand.Publish,
            ["python"],
            RunTests: true,
            Configuration: "Release",
            RuntimeIdentifier: "osx-arm64");

        var result = await new MerkleHostPublisher(runner).PublishAsync(
            new HostPublishRequest(request, context, []),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            runner.Requests,
            test => Assert.Equal("test", test.Arguments[0]),
            publish =>
            {
                Assert.Equal("publish", publish.Arguments[0]);
                Assert.Contains("-p:MerkleIncludeDotNetAdapter=false", publish.Arguments);
                Assert.Contains("osx-arm64", publish.Arguments);
                Assert.Contains(context.HostStagingDirectory!, publish.Arguments);
            });
    }

    [Fact]
    public async Task FailedHostTests_PreventHostCompilation()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(1, "", "tests failed"));
        var context = Context("linux-x64");
        var request = new BuildRequest(BuildCommand.Build, ["dotnet"], RunTests: true);

        var result = await new MerkleHostPublisher(runner).PublishAsync(
            new HostPublishRequest(request, context, []),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("tests failed", result.Diagnostic);
        Assert.Single(runner.Requests);
    }

    private static BuildContext Context(string runtimeIdentifier)
    {
        var root = Path.Combine(Path.GetTempPath(), "merkle-host-publisher-tests");
        return new BuildContext(
            root,
            "Release",
            runtimeIdentifier,
            Path.Combine(root, "run"),
            Path.Combine(root, "adapters"),
            HostStagingDirectory: Path.Combine(root, "host"));
    }

    private sealed class RecordingProcessRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);

        public List<ProcessRequest> Requests { get; } = [];

        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_results.Count == 0 ? new ProcessResult(0, "", "") : _results.Dequeue());
        }
    }
}
