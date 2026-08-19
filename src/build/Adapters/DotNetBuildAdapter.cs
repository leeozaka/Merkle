using System.Text.Json;
using Merkle.Core.Processes;

namespace Merkle.Build;

internal sealed class DotNetBuildAdapter : BuildAdapterBase
{
    public DotNetBuildAdapter(IProcessRunner processRunner) : base(processRunner) { }

    public override AdapterBuildDefinition Definition { get; } = new(
        "dotnet",
        [".net"],
        "1.0.0",
        ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public override async ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var required = ParseVersion(ReadRequired(Path.Combine(context.RepositoryRoot, "global.json"))) ?? new Version(10, 0);
        var result = await TryRunAsync("dotnet", ["--version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Unavailable(Definition.Id, "The .NET SDK is not available.", "dotnet");
        }

        var detected = result.StandardOutput.Trim();
        var version = ParseVersion(detected);
        return result.ExitCode == 0 && IsAtLeast(version, required)
            ? new AdapterReadiness(Definition.Id, AdapterReadinessStatus.Ready, DetectedVersion: detected)
            : Unavailable(Definition.Id, $"The .NET SDK must be at least {required}.", "dotnet", detected);
    }

    public override async ValueTask<AdapterBuildResult> BuildAsync(AdapterBuildRequest request, CancellationToken cancellationToken)
    {
        BeginBuildLog(request.Context, request.Readiness);
        if (request.Readiness.Status != AdapterReadinessStatus.Ready) return Skipped(Definition, request.Readiness);

        var root = request.Context.RepositoryRoot;
        var workerProject = Path.Combine(root, "src", "adapters", "dotnet", "worker", "Merkle.Adapters.DotNet.Worker.csproj");
        var observerProject = Path.Combine(root, "src", "adapters", "dotnet", "observer", "Merkle.Adapters.DotNet.Observer.csproj");
        var destination = Path.Combine(request.Context.StagingDirectory, "workers", Definition.Id);
        Directory.CreateDirectory(destination);
        var arguments = new[] { "build", workerProject, "--configuration", request.Context.Configuration, "--output", destination, "--nologo", "-m:1", "-nodeReuse:false" };
        var workerBuild = await TryRunAsync("dotnet", arguments, root, null, null, cancellationToken).ConfigureAwait(false);
        if (workerBuild is null) return Failed(Definition, "The dotnet process could not be started.");
        if (workerBuild.ExitCode != 0) return Failed(Definition, Diagnostic(workerBuild));

        var observerBuild = await TryRunAsync("dotnet", ["build", observerProject, "--configuration", request.Context.Configuration, "--output", destination, "--nologo", "-m:1", "-nodeReuse:false"], root, null, null, cancellationToken).ConfigureAwait(false);
        if (observerBuild is null) return Failed(Definition, "The dotnet process could not be started.");
        if (observerBuild.ExitCode != 0) return Failed(Definition, Diagnostic(observerBuild));
        var worker = Path.Combine(destination, "Merkle.Adapters.DotNet.Worker.dll");
        var observer = Path.Combine(destination, "Merkle.Adapters.DotNet.Observer.dll");
        if (!File.Exists(worker) || !File.Exists(observer))
        {
            return Failed(Definition, "The .NET build did not produce both the worker and observer payloads.");
        }
        var workerSmokeRequest = JsonSerializer.SerializeToUtf8Bytes(
            new DotNetSmokeRequest("1.0", "build-smoke", root, null, []),
            BuildJsonContext.Default.DotNetSmokeRequest);
        var workerSmoke = await TryRunAsync("dotnet", [worker], root, null, workerSmokeRequest, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessfulDotNetSmoke(workerSmoke)) return Failed(Definition, "The .NET worker failed its Protocol 1.0 smoke check.");
        var copiedArtifacts = new Dictionary<string, (string Path, string RelativePath, string Profile)>(StringComparer.Ordinal);
        AddArtifacts(destination, copiedArtifacts);
        return await CompleteAsync(Definition, request.Context, copiedArtifacts.Values.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static void AddArtifacts(string destination, IDictionary<string, (string Path, string RelativePath, string Profile)> artifacts)
    {
        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destination, file);
            var packagePath = $"workers/dotnet/{relative.Replace(Path.DirectorySeparatorChar, '/')}";
            artifacts[packagePath] = (file, packagePath, "deep");
        }
    }
}
