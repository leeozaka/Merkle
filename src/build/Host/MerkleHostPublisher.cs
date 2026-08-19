using Merkle.Core.Processes;

namespace Merkle.Build;

public sealed class MerkleHostPublisher : IHostPublisher
{
    private readonly IProcessRunner _processRunner;

    public MerkleHostPublisher(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async ValueTask<HostPublishResult> PublishAsync(
        HostPublishRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = request.Context.RepositoryRoot;
        var cliProject = Path.Combine(root, "src", "cli", "Merkle.Cli.csproj");
        if (request.Request.RunTests)
        {
            var testResult = await RunAsync(
                [
                    "test",
                    Path.Combine(root, "tests", "Merkle.Tests", "Merkle.Tests.csproj"),
                    "--configuration", request.Context.Configuration,
                    "--nologo",
                    "-m:1",
                    "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"
                ],
                root,
                cancellationToken).ConfigureAwait(false);
            if (testResult.ExitCode != 0)
            {
                return new HostPublishResult(false, Diagnostic("Merkle helper/host tests failed", testResult));
            }
        }

        var arguments = new List<string>
        {
            request.Request.Command == BuildCommand.Publish ? "publish" : "build",
            cliProject,
            "--configuration", request.Context.Configuration,
            "--output", request.Context.HostStagingDirectory
                ?? throw new InvalidOperationException("The host staging directory is not configured."),
            "--nologo",
            "-m:1",
            "-nodeReuse:false",
            "-p:UseSharedCompilation=false",
            "-p:MerkleIncludeDotNetAdapter=false"
        };
        if (request.Request.Command == BuildCommand.Publish)
        {
            arguments.Add("--runtime");
            arguments.Add(request.Context.RuntimeIdentifier!);
            arguments.Add("--self-contained");
            arguments.Add("true");
        }

        var result = await RunAsync(arguments, root, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new HostPublishResult(true)
            : new HostPublishResult(false, Diagnostic("Merkle host compilation failed", result));
    }

    private ValueTask<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string root,
        CancellationToken cancellationToken) =>
        _processRunner.RunAsync(new ProcessRequest("dotnet", arguments, root), cancellationToken);

    private static string Diagnostic(string prefix, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(details) ? prefix + "." : $"{prefix}: {details.Trim()}";
    }
}
