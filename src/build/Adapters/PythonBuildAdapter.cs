using System.IO.Compression;
using System.Text;
using Merkle.Core.Processes;

namespace Merkle.Build;

internal sealed class PythonBuildAdapter : BuildAdapterBase
{
    public PythonBuildAdapter(IProcessRunner processRunner) : base(processRunner) { }

    public override AdapterBuildDefinition Definition { get; } = new(
        "python",
        ["python3"],
        "1.0.0",
        ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public override async ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var metadata = ReadRequired(Path.Combine(context.RepositoryRoot, "src", "adapters", "python", "pyproject.toml"));
        var requirementOffset = metadata.IndexOf("requires-python", StringComparison.Ordinal);
        var required = requirementOffset >= 0 ? ParseVersion(metadata[requirementOffset..]) ?? new Version(3, 10) : new Version(3, 10);
        var result = await TryRunAsync("python3", ["--version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        if (result is null) return Unavailable(Definition.Id, "Python 3 is not available.", "python3");
        var detected = (result.StandardOutput + result.StandardError).Trim();
        var version = ParseVersion(detected);
        return result.ExitCode == 0 && IsAtLeast(version, required)
            ? new AdapterReadiness(Definition.Id, AdapterReadinessStatus.Ready, DetectedVersion: detected)
            : Unavailable(Definition.Id, $"Python {required}+ is required.", "python3", detected);
    }

    public override async ValueTask<AdapterBuildResult> BuildAsync(AdapterBuildRequest request, CancellationToken cancellationToken)
    {
        BeginBuildLog(request.Context, request.Readiness);
        if (request.Readiness.Status != AdapterReadinessStatus.Ready) return Skipped(Definition, request.Readiness);
        var source = Path.Combine(request.Context.RepositoryRoot, "src", "adapters", "python");
        var destination = Path.Combine(request.Context.StagingDirectory, "workers", Definition.Id);
        Directory.CreateDirectory(destination);
        var archive = Path.Combine(destination, "merkle-adapter-python.pyz");
        if (request.RunTests)
        {
            var tests = await TryRunAsync("python3", ["-m", "unittest", "discover", "-s", "tests"], source, null, null, cancellationToken).ConfigureAwait(false);
            if (tests is null || tests.ExitCode != 0) return Failed(Definition, tests is null ? "The Python process could not be started." : Diagnostic(tests));
        }
        CreateZipApp(source, archive);
        var smoke = await TryRunAsync("python3", [archive], source, null, Encoding.UTF8.GetBytes(ProtocolRequest()), cancellationToken).ConfigureAwait(false);
        if (!IsSuccessfulSmoke(smoke, "python")) return Failed(Definition, "The Python adapter failed the Protocol 1.0 describe smoke check.");
        return await CompleteAsync(Definition, request.Context, [(archive, $"workers/{Definition.Id}/merkle-adapter-python.pyz", "minimal")], cancellationToken).ConfigureAwait(false);
    }

    private static void CreateZipApp(string source, string destination)
    {
        using var stream = File.Create(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var files = Directory.EnumerateFiles(Path.Combine(source, "merkle_adapter"), "*.py", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var target = entry.Open();
            using var input = File.OpenRead(file);
            input.CopyTo(target);
        }
        var main = archive.CreateEntry("__main__.py", CompressionLevel.Optimal);
        main.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(main.Open(), Encoding.UTF8);
        writer.Write("from merkle_adapter.__main__ import main\nmain()\n");
    }
}
