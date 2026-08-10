using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;
using Merkle.Core.Snapshots;

namespace Merkle.Infrastructure.Snapshots;

public sealed partial class GitSnapshotSource : ISnapshotSource
{
    private readonly string _repositoryRoot;
    private readonly IProcessRunner _processRunner;
    private readonly SnapshotLimits _limits;
    private readonly IEnvironmentReader _environment;
    private readonly string? _configuredRepositoryIdentity;

    public GitSnapshotSource(
        string repositoryRoot,
        IProcessRunner processRunner,
        SnapshotLimits? limits = null,
        IEnvironmentReader? environment = null,
        string? repositoryIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _limits = limits ?? SnapshotLimits.Default;
        _environment = environment ?? new SystemEnvironmentReader();
        _configuredRepositoryIdentity = string.IsNullOrWhiteSpace(repositoryIdentity)
            ? null
            : repositoryIdentity;
    }

    public async ValueTask<SnapshotPair> BindAsync(
        string? baselineReference,
        string? candidateReference,
        CancellationToken cancellationToken)
    {
        var references = await ResolveReferencesAsync(
            baselineReference,
            candidateReference,
            cancellationToken).ConfigureAwait(false);

        var repositoryIdentity = _configuredRepositoryIdentity ?? ComputeRepositoryIdentity(_repositoryRoot);
        var baseline = await ReadCommitAsync(
            references.Baseline,
            repositoryIdentity,
            cancellationToken).ConfigureAwait(false);
        var candidate = StringComparer.OrdinalIgnoreCase.Equals(references.Candidate, "WORKTREE")
            ? await ReadWorktreeAsync(repositoryIdentity, cancellationToken).ConfigureAwait(false)
            : await ReadCommitAsync(references.Candidate, repositoryIdentity, cancellationToken).ConfigureAwait(false);

        return new SnapshotPair(baseline, candidate);
    }

    private async ValueTask<RepositorySnapshot> ReadCommitAsync(
        string reference,
        string repositoryIdentity,
        CancellationToken cancellationToken)
    {
        var resolved = await RunGitAsync(
            ["rev-parse", "--verify", $"{reference}^{{commit}}"],
            cancellationToken).ConfigureAwait(false);
        if (resolved.ExitCode != 0)
        {
            throw new AnalysisException(
                "GitReferenceUnavailable",
                $"Git could not resolve '{reference}'. Fetch the required history and verify the reference, then retry.");
        }

        var commit = resolved.StandardOutput.Trim();
        var listing = await RunGitAsync(
            ["ls-tree", "-r", "-z", "--format=%(objectmode) %(objecttype) %(objectname)%x09%(path)", commit],
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(listing, "GitTreeUnavailable", "Git could not enumerate the requested snapshot.");
        var entries = ParseTreeEntries(listing.OutputBytes.Span);
        EnsureFileCount(entries.Count);
        var files = new List<SnapshotFile>(entries.Count);
        long totalBytes = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            if (entry.Kind == SnapshotEntryKind.GitLink)
            {
                bytes = Encoding.UTF8.GetBytes(entry.ObjectIdentity);
            }
            else
            {
                var content = await RunGitAsync(["show", $"{commit}:{entry.Path}"], cancellationToken)
                    .ConfigureAwait(false);
                EnsureGitSuccess(content, "GitObjectUnavailable", $"Git could not read '{entry.Path}' from '{reference}'.");
                bytes = content.OutputBytes.ToArray();
            }

            totalBytes = AddContentLength(entry.Path, bytes.Length, totalBytes);
            files.Add(new SnapshotFile(entry.Path, Hash(bytes), bytes, entry.Kind, entry.Mode));
        }

        var orderedFiles = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return new RepositorySnapshot(
            CreateSnapshotIdentity(reference, orderedFiles),
            _repositoryRoot,
            repositoryIdentity,
            orderedFiles);
    }

    private async ValueTask<RepositorySnapshot> ReadWorktreeAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken)
    {
        var headResult = await RunGitAsync(
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(headResult, "GitHeadUnavailable", "Git could not resolve HEAD for the working-tree snapshot.");

        var firstListing = await RunGitAsync(
            ["ls-files", "--cached", "--others", "--exclude-standard", "--stage", "-z"],
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(firstListing, "GitWorktreeUnavailable", "Git could not enumerate tracked and untracked worktree inputs.");
        var firstEntries = ParseWorktreeEntries(firstListing.OutputBytes.Span);
        EnsureFileCount(firstEntries.Count);
        await RejectDirtySubmodulesAsync(firstEntries, cancellationToken).ConfigureAwait(false);
        var files = await ReadWorktreeFilesAsync(firstEntries, cancellationToken).ConfigureAwait(false);

        var secondListing = await RunGitAsync(
            ["ls-files", "--cached", "--others", "--exclude-standard", "--stage", "-z"],
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(secondListing, "GitWorktreeUnavailable", "Git could not verify tracked and untracked worktree inputs.");
        var secondEntries = ParseWorktreeEntries(secondListing.OutputBytes.Span);
        EnsureFileCount(secondEntries.Count);
        var verifiedFiles = await ReadWorktreeFilesAsync(secondEntries, cancellationToken).ConfigureAwait(false);
        if (!files.Select(file => (file.Path, file.ContentHash, file.Kind, file.Mode))
                .SequenceEqual(verifiedFiles.Select(file => (file.Path, file.ContentHash, file.Kind, file.Mode))))
        {
            throw new AnalysisException(
                "WorkingTreeChangedDuringSnapshot",
                "The working tree changed while Merkle was freezing it. Retry after the files stop changing.");
        }

        return new RepositorySnapshot(
            CreateSnapshotIdentity("WORKTREE", files),
            _repositoryRoot,
            repositoryIdentity,
            files);
    }

    private async ValueTask<IReadOnlyList<SnapshotFile>> ReadWorktreeFilesAsync(
        IReadOnlyList<TreeEntry> entries,
        CancellationToken cancellationToken)
    {
        var files = new List<SnapshotFile>(entries.Count);
        long totalBytes = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = ResolveRepositoryPath(entry.Path);
            var file = new FileInfo(absolutePath);
            file.Refresh();
            var linkTarget = file.LinkTarget;
            if (!file.Exists && linkTarget is null && entry.Kind != SnapshotEntryKind.GitLink)
            {
                continue;
            }

            byte[] bytes;
            SnapshotEntryKind kind;
            string mode;
            if (entry.Kind == SnapshotEntryKind.GitLink)
            {
                bytes = Encoding.UTF8.GetBytes(entry.ObjectIdentity);
                kind = SnapshotEntryKind.GitLink;
                mode = "160000";
            }
            else if (linkTarget is not null)
            {
                bytes = Encoding.UTF8.GetBytes(linkTarget);
                kind = SnapshotEntryKind.SymbolicLink;
                mode = "120000";
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
                var executable = !OperatingSystem.IsWindows() &&
                                 (File.GetUnixFileMode(absolutePath) &
                                  (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                kind = executable ? SnapshotEntryKind.ExecutableFile : SnapshotEntryKind.RegularFile;
                mode = executable ? "100755" : "100644";
            }

            totalBytes = AddContentLength(entry.Path, bytes.Length, totalBytes);
            files.Add(new SnapshotFile(entry.Path, Hash(bytes), bytes, kind, mode));
        }

        return [.. files.OrderBy(file => file.Path, StringComparer.Ordinal)];
    }

    private ValueTask<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _processRunner.RunAsync(new ProcessRequest("git", arguments, _repositoryRoot), cancellationToken);

    private async ValueTask RejectDirtySubmodulesAsync(
        IReadOnlyList<TreeEntry> entries,
        CancellationToken cancellationToken)
    {
        var gitlinks = entries
            .Where(entry => entry.Kind == SnapshotEntryKind.GitLink)
            .Select(entry => entry.Path)
            .ToArray();
        if (gitlinks.Length == 0)
        {
            return;
        }

        var arguments = new List<string>
        {
            "status",
            "--porcelain=v1",
            "--untracked-files=no",
            "--ignore-submodules=none",
            "--"
        };
        arguments.AddRange(gitlinks);
        var status = await RunGitAsync(arguments, cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(status, "GitSubmoduleStatusUnavailable", "Git could not inspect submodule state.");
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new AnalysisException(
                "DirtySubmoduleUnsupported",
                "The working tree contains a dirty or moved submodule. Commit or clean it before creating a snapshot.");
        }
    }

    private string ResolveRepositoryPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, normalized));
        var prefix = _repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repositoryRoot
            : _repositoryRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new AnalysisException("UnsafeRepositoryPath", "Git returned a path outside the repository root.");
        }

        return fullPath;
    }

    private static IReadOnlyList<TreeEntry> ParseTreeEntries(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var entries = text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .ToArray();
        return ValidateAndOrderEntries(entries);
    }

    private static IReadOnlyList<TreeEntry> ParseWorktreeEntries(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var entries = text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(value =>
            {
                var match = StageEntryPattern().Match(value);
                return match.Success
                    ? CreateTreeEntry(match.Groups[4].Value, match.Groups[1].Value, match.Groups[2].Value)
                    : CreateTreeEntry(value, "100644", string.Empty);
            })
            .ToArray();
        return ValidateAndOrderEntries(entries);
    }

    private static TreeEntry ParseTreeEntry(string value)
    {
        var tab = value.IndexOf('\t');
        if (tab < 0)
        {
            return CreateTreeEntry(value, "100644", string.Empty);
        }

        var metadata = value[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length != 3)
        {
            throw new AnalysisException("InvalidGitTree", "Git returned an invalid tree entry.");
        }

        return CreateTreeEntry(value[(tab + 1)..], metadata[0], metadata[2]);
    }

    private static TreeEntry CreateTreeEntry(string path, string mode, string objectIdentity)
    {
        var normalizedPath = path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var kind = mode switch
        {
            "100755" => SnapshotEntryKind.ExecutableFile,
            "120000" => SnapshotEntryKind.SymbolicLink,
            "160000" => SnapshotEntryKind.GitLink,
            _ => SnapshotEntryKind.RegularFile
        };
        return new TreeEntry(normalizedPath, mode, objectIdentity, kind);
    }

    private static IReadOnlyList<TreeEntry> ValidateAndOrderEntries(IEnumerable<TreeEntry> entries)
    {
        var ordered = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var duplicate = ordered
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new AnalysisException(
                "SnapshotPathCollision",
                $"Snapshot paths collide after Unicode/case normalization: {string.Join(", ", duplicate.Select(entry => entry.Path))}.");
        }

        return ordered;
    }

    private static string ComputeRepositoryIdentity(string repositoryRoot) =>
        $"repository:{Hash(Encoding.UTF8.GetBytes(repositoryRoot))}";

    private static SnapshotIdentity CreateSnapshotIdentity(
        string reference,
        IReadOnlyList<SnapshotFile> files)
    {
        var identityFields = files
            .SelectMany(file => new[]
            {
                file.Path,
                file.Kind.ToString(),
                file.Mode,
                file.ContentHash
            })
            .ToArray();
        return new SnapshotIdentity($"snapshot:{HashCanonical(identityFields)}", reference, "git");
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashCanonical(IReadOnlyList<string> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            var length = BitConverter.GetBytes(bytes.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(length);
            }

            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureGitSuccess(ProcessResult result, string code, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new AnalysisException(code, message);
        }
    }

    private void EnsureFileCount(int count)
    {
        if (count > _limits.MaxFiles)
        {
            throw new AnalysisException(
                "SnapshotFileLimitExceeded",
                $"The snapshot contains {count} files; the configured limit is {_limits.MaxFiles}.");
        }
    }

    private long AddContentLength(string path, int length, long currentTotal)
    {
        if (length > _limits.MaxFileBytes)
        {
            throw new AnalysisException(
                "SnapshotFileSizeLimitExceeded",
                $"Snapshot input '{path}' exceeds the configured per-file byte limit.");
        }

        var total = checked(currentTotal + length);
        if (total > _limits.MaxTotalBytes)
        {
            throw new AnalysisException(
                "SnapshotSizeLimitExceeded",
                "The snapshot exceeds the configured total byte limit.");
        }

        return total;
    }

    private async ValueTask<ResolvedReferences> ResolveReferencesAsync(
        string? baselineReference,
        string? candidateReference,
        CancellationToken cancellationToken)
    {
        var hasBaseline = !string.IsNullOrWhiteSpace(baselineReference);
        var hasCandidate = !string.IsNullOrWhiteSpace(candidateReference);
        if (hasBaseline != hasCandidate)
        {
            throw new ConfigurationException(
                "SnapshotReferencePairRequired",
                "Specify both --base and --head, or omit both and use a recognized pull-request context.");
        }

        if (hasBaseline)
        {
            return new ResolvedReferences(baselineReference!, candidateReference!);
        }

        var context = PullRequestContext.TryRead(_environment) ?? throw new ConfigurationException(
                "SnapshotReferencesRequired",
                "Local planning requires explicit or configured base/head references; no supported pull-request context was detected.");
        var mergeBase = await RunGitAsync(
            ["merge-base", context.TargetReference, context.HeadReference],
            cancellationToken).ConfigureAwait(false);
        if (mergeBase.ExitCode != 0 || string.IsNullOrWhiteSpace(mergeBase.StandardOutput))
        {
            throw new AnalysisException(
                "GitMergeBaseUnavailable",
                "Git could not resolve the pull-request merge base. Fetch the target and head history, then retry.");
        }

        return new ResolvedReferences(mergeBase.StandardOutput.Trim(), context.HeadReference);
    }

    [GeneratedRegex("^([0-9]{6}) ([0-9a-fA-F]+) ([0-3])\\t(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StageEntryPattern();

    private sealed record TreeEntry(
        string Path,
        string Mode,
        string ObjectIdentity,
        SnapshotEntryKind Kind);

    private sealed record ResolvedReferences(string Baseline, string Candidate);
}

public sealed record SnapshotLimits(int MaxFiles, long MaxTotalBytes, int MaxFileBytes)
{
    public static SnapshotLimits Default { get; } = new(
        MaxFiles: 100_000,
        MaxTotalBytes: 1_073_741_824,
        MaxFileBytes: 268_435_456);
}

public interface IEnvironmentReader
{
    string? Get(string name);
}

public sealed class SystemEnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

internal sealed record PullRequestContext(string Provider, string TargetReference, string HeadReference)
{
    public static PullRequestContext? TryRead(IEnvironmentReader environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var neutralBase = environment.Get("MERKLE_PR_BASE_REF");
        var neutralHead = environment.Get("MERKLE_PR_HEAD_REF");
        if (!string.IsNullOrWhiteSpace(neutralBase) && !string.IsNullOrWhiteSpace(neutralHead))
        {
            return new PullRequestContext("merkle", neutralBase, neutralHead);
        }

        var githubBase = environment.Get("GITHUB_BASE_REF");
        var githubHead = environment.Get("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(githubBase) && !string.IsNullOrWhiteSpace(githubHead))
        {
            var target = githubBase.Contains('/', StringComparison.Ordinal)
                ? githubBase
                : $"origin/{githubBase}";
            return new PullRequestContext("github", target, githubHead);
        }

        var gitlabBase = environment.Get("CI_MERGE_REQUEST_TARGET_BRANCH_SHA") ??
                         environment.Get("CI_MERGE_REQUEST_TARGET_BRANCH_NAME");
        var gitlabHead = environment.Get("CI_MERGE_REQUEST_SOURCE_BRANCH_SHA") ??
                         environment.Get("CI_COMMIT_SHA");
        return !string.IsNullOrWhiteSpace(gitlabBase) && !string.IsNullOrWhiteSpace(gitlabHead)
            ? new PullRequestContext("gitlab", gitlabBase, gitlabHead)
            : null;
    }
}
