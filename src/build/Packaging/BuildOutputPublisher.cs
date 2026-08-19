using System.Security.Cryptography;
using System.Text.Json;

namespace Merkle.Build;

internal sealed class BuildOutputPublisher : IBuildOutputPublisher
{
    public const string OwnershipMarkerFileName = ".merkle-build-output";
    private const int ManifestSchemaVersion = 1;

    public async ValueTask<BuildOutputResult> PublishAsync(
        BuildOutputRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var output = Path.GetFullPath(request.OutputPath);
        var hostSource = Path.GetFullPath(request.HostStagingDirectory);
        var adapterSource = Path.GetFullPath(request.AdapterStagingDirectory);
        var parent = Directory.GetParent(output)?.FullName ?? throw new IOException("The output path has no parent directory.");
        Directory.CreateDirectory(parent);
        ValidateDestination(output);
        if (!Directory.Exists(hostSource))
        {
            throw new DirectoryNotFoundException($"The host staging directory '{hostSource}' does not exist.");
        }
        if (!Directory.Exists(adapterSource))
        {
            throw new DirectoryNotFoundException($"The adapter staging directory '{adapterSource}' does not exist.");
        }

        var package = output + ".next-" + Guid.NewGuid().ToString("N");
        string? backup = null;
        try
        {
            CopyHostOutput(hostSource, package, cancellationToken);
            CopySuccessfulAdapterArtifacts(adapterSource, package, request.Adapters, cancellationToken);
            WriteManifest(package, request);
            AdapterManifestContract.ValidateFile(Path.Combine(package, "adapters.json"));
            await File.WriteAllTextAsync(
                Path.Combine(package, OwnershipMarkerFileName),
                "Merkle build output; ownership marker schema 1.\n",
                cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(output))
            {
                backup = output + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(output, backup);
            }

            Directory.Move(package, output);
            package = string.Empty;
            if (backup is not null && Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (IOException)
                {
                    // Promotion already succeeded. A stale backup is safer than rolling back a valid package.
                }
                catch (UnauthorizedAccessException)
                {
                    // Promotion already succeeded. A stale backup is safer than rolling back a valid package.
                }
            }

            return new BuildOutputResult(output, Path.Combine(output, "adapters.json"));
        }
        catch (Exception error)
        {
            Exception? rollbackError = null;
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(output))
            {
                try
                {
                    Directory.Move(backup, output);
                }
                catch (Exception restoreError)
                {
                    rollbackError = restoreError;
                }
            }

            if (!string.IsNullOrEmpty(package) && Directory.Exists(package))
            {
                TryDeleteDirectory(package);
            }

            if (rollbackError is not null)
            {
                throw new IOException(
                    "Package promotion failed and the previous output could not be restored.",
                    new AggregateException(error, rollbackError));
            }

            throw;
        }
    }

    private static void ValidateDestination(string output)
    {
        if (File.Exists(output))
        {
            throw new IOException($"The output destination '{output}' is a file.");
        }

        if (!Directory.Exists(output))
        {
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(output).ToArray();
        if (entries.Length == 0 || File.Exists(Path.Combine(output, OwnershipMarkerFileName)))
        {
            return;
        }

        throw new IOException($"The output destination '{output}' is non-empty and is not owned by Merkle.");
    }

    private static void CopyHostOutput(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (!IsAdapterTree(relative)) Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (IsAdapterTree(relative) ||
                StringComparer.Ordinal.Equals(relative, BuildRunWorkspaceFactory.WorkspaceMarkerFileName))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopySuccessfulAdapterArtifacts(
        string source,
        string destination,
        IReadOnlyList<AdapterBuildResult> adapters,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in adapters
                     .Where(adapter => adapter.Status == AdapterBuildStatus.Built)
                     .SelectMany(adapter => adapter.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ValidateArtifact(source, artifact);
            var sourcePath = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            var targetPath = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static bool IsAdapterTree(string relativePath)
    {
        var firstSeparator = relativePath.IndexOf(Path.DirectorySeparatorChar);
        var firstSegment = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return StringComparer.Ordinal.Equals(firstSegment, "workers");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteManifest(string package, BuildOutputRequest request)
    {
        var adapters = request.Adapters
            .Where(result => result.Status == AdapterBuildStatus.Built)
            .OrderBy(result => result.AdapterId, StringComparer.Ordinal)
            .Select(result => new AdapterManifestEntry(
                result.AdapterId,
                result.Artifacts.FirstOrDefault()?.Version ?? "unknown",
                result.Artifacts.FirstOrDefault()?.ProtocolVersion ?? "1.0",
                result.Artifacts.FirstOrDefault()?.Profile ?? "minimal",
                result.Artifacts
                    .OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal)
                    .Select(artifact => new AdapterManifestArtifact(
                        ValidateArtifact(package, artifact),
                        artifact.Sha256.ToLowerInvariant()))
                    .ToArray()))
            .ToArray();

        var manifest = new AdapterManifestDocument(
            ManifestSchemaVersion,
            request.MerkleVersion,
            request.Configuration,
            request.RuntimeIdentifier,
            adapters);
        File.WriteAllText(
            Path.Combine(package, "adapters.json"),
            JsonSerializer.Serialize(manifest, BuildJsonContext.Default.AdapterManifestDocument) + Environment.NewLine);
    }

    private static string ValidateArtifact(string package, AdapterBuildArtifact artifact)
    {
        var relativePath = SafeRelativePath(artifact.RelativePath);
        var path = Path.Combine(package, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new IOException($"Built adapter artifact '{relativePath}' is missing from the package.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, artifact.Sha256))
        {
            throw new IOException($"Built adapter artifact '{relativePath}' does not match its recorded checksum.");
        }

        return relativePath;
    }

    private static string SafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new IOException($"Adapter artifact path '{relativePath}' is not a safe relative path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new IOException($"Adapter artifact path '{relativePath}' is not a safe relative path.");
        }

        if (!normalized.StartsWith("workers/", StringComparison.Ordinal))
        {
            throw new IOException($"Adapter artifact path '{relativePath}' must be beneath 'workers/'.");
        }

        return normalized;
    }
}
