using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;
using Merkle.Infrastructure.Processes;
using Merkle.Infrastructure.Snapshots;

namespace Merkle.Tests.Infrastructure;

public sealed class GitSnapshotSourceTests
{
    [Fact]
    public async Task Bind_RequiresExplicitLocalReferencesInsteadOfGuessing()
    {
        var source = new GitSnapshotSource("/repo", new FakeProcessRunner());

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await source.BindAsync(null, null, default));

        Assert.Equal("SnapshotReferencesRequired", error.Code);
    }

    [Fact]
    public async Task Bind_WorktreeIdentityChangesWhenUntrackedContentChanges()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "new.txt"), "first");
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments.Contains("rev-parse")
                ? Result("abc123\n")
                : Result("new.txt\0")
        };
        var source = new GitSnapshotSource(directory.Path, runner);

        var first = await source.BindAsync("main", "WORKTREE", default);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "new.txt"), "second");
        var second = await source.BindAsync("main", "WORKTREE", default);

        Assert.NotEqual(first.Candidate.Identity.Value, second.Candidate.Identity.Value);
    }

    [Fact]
    public async Task Bind_MissingRefIsAnAnalysisErrorWithRemediation()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments.Contains("rev-parse")
                ? new ProcessResult(128, string.Empty, "unknown revision")
                : Result(string.Empty)
        };
        var source = new GitSnapshotSource("/repo", runner);

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await source.BindAsync("missing", "HEAD", default));

        Assert.Equal("GitReferenceUnavailable", error.Code);
        Assert.Contains("fetch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bind_RejectsSnapshotAboveConfiguredFileLimit()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments.Contains("rev-parse")
                ? Result("abc123\n")
                : Result("one.cs\0")
        };
        var source = new GitSnapshotSource(
            "/repo",
            runner,
            new SnapshotLimits(MaxFiles: 0, MaxTotalBytes: 100, MaxFileBytes: 100));

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await source.BindAsync("main", "HEAD", default));

        Assert.Equal("SnapshotFileLimitExceeded", error.Code);
    }

    [Fact]
    public async Task Bind_CommittedSnapshotsReadImmutableFileContent()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments[0] switch
            {
                "rev-parse" => Result(request.Arguments[2].StartsWith("main", StringComparison.Ordinal)
                    ? "basehash\n"
                    : "headhash\n"),
                "ls-tree" => Result("src/App.cs\0"),
                "show" => Result(request.Arguments[1].StartsWith("basehash", StringComparison.Ordinal)
                    ? "old"
                    : "new"),
                _ => Result(string.Empty)
            }
        };

        var pair = await new GitSnapshotSource("/repo", runner)
            .BindAsync("main", "HEAD", default);

        Assert.NotEqual(pair.Baseline.Identity.Value, pair.Candidate.Identity.Value);
        Assert.Equal("main", pair.Baseline.Identity.Reference);
        Assert.Equal("HEAD", pair.Candidate.Identity.Reference);
        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(pair.Baseline.Files[0].Content.Span));
        Assert.Equal("new", System.Text.Encoding.UTF8.GetString(pair.Candidate.Files[0].Content.Span));
    }

    [Fact]
    public async Task Bind_UsesTheSameIdentityForEquivalentCommitAndWorktreeContent()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "src.cs"), "same");
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments[0] switch
            {
                "rev-parse" => Result(request.Arguments[2].StartsWith("main", StringComparison.Ordinal)
                    ? "commit-one\n"
                    : "commit-two\n"),
                "ls-tree" => Result("src.cs\0"),
                "show" => Result("same"),
                "ls-files" => Result("src.cs\0"),
                _ => Result(string.Empty)
            }
        };

        var pair = await new GitSnapshotSource(directory.Path, runner)
            .BindAsync("main", "WORKTREE", default);

        Assert.Equal(pair.Baseline.Identity.Value, pair.Candidate.Identity.Value);
        Assert.Equal("main", pair.Baseline.Identity.Reference);
        Assert.Equal("WORKTREE", pair.Candidate.Identity.Reference);
    }

    [Fact]
    public async Task Bind_RejectsWorktreeThatChangesWhileBeingFrozen()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "file.cs");
        File.WriteAllText(path, "first");
        var listingCalls = 0;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.Contains("rev-parse"))
                {
                    return Result("abc123\n");
                }

                if (request.Arguments.Contains("ls-files"))
                {
                    listingCalls++;
                    if (listingCalls == 2)
                    {
                        File.WriteAllText(path, "second");
                    }

                    return Result("file.cs\0");
                }

                if (request.Arguments.Contains("ls-tree"))
                {
                    return Result(string.Empty);
                }

                return Result(string.Empty);
            }
        };

        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await new GitSnapshotSource(directory.Path, runner)
                .BindAsync("main", "WORKTREE", default));

        Assert.Equal("WorkingTreeChangedDuringSnapshot", error.Code);
    }

    [Fact]
    public async Task Bind_InfersPullRequestMergeBaseFromProviderNeutralEnvironment()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments[0] switch
            {
                "merge-base" => Result("merge-base-id\n"),
                "rev-parse" => Result(request.Arguments[2].StartsWith("merge-base-id", StringComparison.Ordinal)
                    ? "merge-base-id\n"
                    : "head-id\n"),
                "ls-tree" => Result(string.Empty),
                _ => Result(string.Empty)
            }
        };
        var environment = new FakeEnvironmentReader(new Dictionary<string, string>
        {
            ["MERKLE_PR_BASE_REF"] = "origin/main",
            ["MERKLE_PR_HEAD_REF"] = "pull-head"
        });

        var pair = await new GitSnapshotSource("/repo", runner, environment: environment)
            .BindAsync(null, null, default);

        Assert.Equal("merge-base-id", pair.Baseline.Identity.Reference);
        Assert.Equal("pull-head", pair.Candidate.Identity.Reference);
    }

    [Fact]
    public async Task Bind_RejectsIncompleteExplicitReferencePair()
    {
        var source = new GitSnapshotSource("/repo", new FakeProcessRunner());

        var error = await Assert.ThrowsAsync<ConfigurationException>(async () =>
            await source.BindAsync("main", null, default));

        Assert.Equal("SnapshotReferencePairRequired", error.Code);
    }

    [Fact]
    public async Task Bind_IncludesGitModeAndEntryKindInSnapshotIdentity()
    {
        var mode = "100644";
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments[0] switch
            {
                "rev-parse" => Result(request.Arguments[2].StartsWith("main", StringComparison.Ordinal)
                    ? "base\n"
                    : "head\n"),
                "ls-tree" => Result($"{mode} blob abcdef\tscript.sh\0"),
                "show" => Result("same"),
                _ => Result(string.Empty)
            }
        };
        var source = new GitSnapshotSource("/repo", runner);
        var regular = await source.BindAsync("main", "HEAD", default);

        mode = "100755";
        var executable = await source.BindAsync("main", "HEAD", default);

        Assert.NotEqual(regular.Candidate.Identity.Value, executable.Candidate.Identity.Value);
        Assert.Equal(SnapshotEntryKind.ExecutableFile, executable.Candidate.Files[0].Kind);
    }

    private static ProcessResult Result(string stdout) => new(0, stdout, string.Empty);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<ProcessRequest, ProcessResult> Handler { get; init; } = _ => Result(string.Empty);

        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Handler(request));
    }

    private sealed class FakeEnvironmentReader(IReadOnlyDictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"merkle-git-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
