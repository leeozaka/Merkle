namespace Merkle.Build;

public sealed class BuildRunWorkspaceFactory : IBuildRunWorkspaceFactory
{
    public const string WorkspaceMarkerFileName = ".merkle-build-workspace";
    private readonly IBuildOutputPublisher _outputPublisher = new BuildOutputPublisher();

    public ValueTask<IBuildRunWorkspace> AcquireAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Directory.GetCurrentDirectory();
        var runtimeIdentifier = request.RuntimeIdentifier ?? BuildRuntimeIdentifier.Current;
        var output = request.OutputPath is null
            ? Path.Combine(
                root,
                "artifacts",
                request.Command.ToString().ToLowerInvariant(),
                request.Configuration,
                runtimeIdentifier)
            : Path.GetFullPath(request.OutputPath);
        var parent = Directory.GetParent(output)?.FullName
            ?? throw new IOException("The output path must have a parent directory.");
        Directory.CreateDirectory(parent);

        var lease = AcquireDestinationLock(output);
        try
        {
            if (request.Clean) CleanOwnedIntermediates(output);
            var reportPath = request.ReportPath is null ? null : Path.GetFullPath(request.ReportPath);
            if (reportPath is not null) ValidateReportPath(output, reportPath);
            var runDirectory = request.ReportPath is null
                ? output + ".run-" + Guid.NewGuid().ToString("N")
                : Path.GetDirectoryName(reportPath!) ?? root;
            var workspaceRoot = output + ".staging-" + Guid.NewGuid().ToString("N");
            var adapterStaging = Path.Combine(workspaceRoot, "adapters");
            var hostStaging = Path.Combine(workspaceRoot, "host");
            Directory.CreateDirectory(runDirectory);
            Directory.CreateDirectory(adapterStaging);
            Directory.CreateDirectory(hostStaging);
            File.WriteAllText(
                Path.Combine(workspaceRoot, WorkspaceMarkerFileName),
                "Merkle build workspace; ownership marker schema 1.\n");
            IBuildRunWorkspace workspace = new BuildRunWorkspace(
                new BuildContext(root, request.Configuration, runtimeIdentifier, runDirectory, adapterStaging, output, hostStaging),
                lease,
                _outputPublisher);
            return ValueTask.FromResult(workspace);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void ValidateReportPath(string output, string reportPath)
    {
        var comparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedOutput = Path.TrimEndingDirectorySeparator(output);
        var outputPrefix = normalizedOutput + Path.DirectorySeparatorChar;
        if (reportPath.Equals(normalizedOutput, comparison) || reportPath.StartsWith(outputPrefix, comparison))
        {
            throw new Merkle.Core.Errors.ConfigurationException(
                "ReportInsidePackage",
                "The build report must be outside the package output directory.");
        }

        foreach (var suffix in new[] { ".staging-", ".next-", ".previous-" })
        {
            if (reportPath.StartsWith(normalizedOutput + suffix, comparison))
            {
                throw new Merkle.Core.Errors.ConfigurationException(
                    "ReportInsideWorkspace",
                    "The build report must be outside helper staging and promotion directories.");
            }
        }
    }

    private static FileStream AcquireDestinationLock(string output)
    {
        try
        {
            return new FileStream(
                output + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.SequentialScan);
        }
        catch (IOException error)
        {
            throw new IOException($"The output destination '{output}' is already being built.", error);
        }
    }

    private static void CleanOwnedIntermediates(string output)
    {
        var parent = Directory.GetParent(output)!.FullName;
        var name = Path.GetFileName(output);
        foreach (var directory in Directory.EnumerateDirectories(parent, name + ".staging-*", SearchOption.TopDirectoryOnly))
        {
            DeleteIfMarked(directory, WorkspaceMarkerFileName);
        }

        foreach (var pattern in new[] { name + ".next-*", name + ".previous-*" })
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, pattern, SearchOption.TopDirectoryOnly))
            {
                DeleteIfMarked(directory, BuildOutputPublisher.OwnershipMarkerFileName);
            }
        }
    }

    private static void DeleteIfMarked(string directory, string marker)
    {
        if (File.Exists(Path.Combine(directory, marker))) Directory.Delete(directory, recursive: true);
    }

    private sealed class BuildRunWorkspace(
        BuildContext context,
        FileStream lease,
        IBuildOutputPublisher outputPublisher) : IBuildRunWorkspace
    {
        public BuildContext Context { get; } = context;

        public ValueTask<BuildOutputResult> PromoteAsync(
            BuildOutputRequest request,
            CancellationToken cancellationToken)
        {
            if (!SamePath(request.OutputPath, Context.OutputPath!) ||
                !SamePath(request.HostStagingDirectory, Context.HostStagingDirectory!) ||
                !SamePath(request.AdapterStagingDirectory, Context.StagingDirectory))
            {
                throw new InvalidOperationException("Package promotion paths do not belong to the acquired build workspace.");
            }

            return outputPublisher.PublishAsync(request, cancellationToken);
        }

        private static bool SamePath(string left, string right)
        {
            var comparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), comparison);
        }

        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
