using System.Text;
using Merkle.Core.Processes;

namespace Merkle.Build;

internal sealed class GoBuildAdapter : BuildAdapterBase
{
    public GoBuildAdapter(IProcessRunner processRunner) : base(processRunner) { }

    public override AdapterBuildDefinition Definition { get; } = new(
        "golang",
        ["go"],
        "0.1.0",
        ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public override async ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var manifest = ReadRequired(Path.Combine(context.RepositoryRoot, "src", "adapters", "go", "worker", "go.mod"));
        var versionOffset = manifest.IndexOf("go ", StringComparison.Ordinal);
        var required = versionOffset >= 0 ? ParseVersion(manifest[(versionOffset + 3)..].Split('\n')[0]) ?? new Version(1, 22) : new Version(1, 22);
        var result = await TryRunAsync("go", ["version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        if (result is null) return Unavailable(Definition.Id, "The Go toolchain is not available.", "go");
        var detected = result.StandardOutput.Trim();
        var version = ParseVersion(detected);
        return result.ExitCode == 0 && IsAtLeast(version, required)
            ? new AdapterReadiness(Definition.Id, AdapterReadinessStatus.Ready, DetectedVersion: detected)
            : Unavailable(Definition.Id, $"Go {required}+ is required.", "go", detected);
    }

    public override async ValueTask<AdapterBuildResult> BuildAsync(AdapterBuildRequest request, CancellationToken cancellationToken)
    {
        BeginBuildLog(request.Context, request.Readiness);
        if (request.Readiness.Status != AdapterReadinessStatus.Ready) return Skipped(Definition, request.Readiness);
        var root = Path.Combine(request.Context.RepositoryRoot, "src", "adapters", "go", "worker");
        var destination = Path.Combine(request.Context.StagingDirectory, "workers", "go");
        Directory.CreateDirectory(destination);
        var executable = Path.Combine(destination, "merkle-adapter-go");
        var (goOs, goArch) = Target(request.Context.RuntimeIdentifier);
        var environment = new Dictionary<string, string?> { ["CGO_ENABLED"] = "0", ["GOOS"] = goOs, ["GOARCH"] = goArch, ["GOTOOLCHAIN"] = "local" };
        if (request.RunTests)
        {
            var tests = await TryRunAsync("go", ["test", "./..."], root, environment, null, cancellationToken).ConfigureAwait(false);
            if (tests is null || tests.ExitCode != 0) return Failed(Definition, tests is null ? "The Go process could not be started." : Diagnostic(tests));
        }
        var build = await TryRunAsync("go", ["build", "-trimpath", "-o", executable, "."], root, environment, null, cancellationToken).ConfigureAwait(false);
        if (build is null || build.ExitCode != 0) return Failed(Definition, build is null ? "The Go process could not be started." : Diagnostic(build));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        }
        var smoke = await TryRunAsync(executable, [], root, null, Encoding.UTF8.GetBytes(ProtocolRequest()), cancellationToken).ConfigureAwait(false);
        if (!IsSuccessfulSmoke(smoke, "golang")) return Failed(Definition, "The Go worker failed the Protocol 1.0 describe smoke check.");
        return await CompleteAsync(Definition, request.Context, [(executable, "workers/go/merkle-adapter-go", "deep")], cancellationToken).ConfigureAwait(false);
    }
}
