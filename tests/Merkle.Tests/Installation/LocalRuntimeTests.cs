using System.Diagnostics;

namespace Merkle.Tests.Installation;

public sealed class LocalRuntimeTests
{
    [Fact]
    public async Task RuntimeCommandMountsRepositoryAndForwardsArgumentsThroughCompose()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new RuntimeFixture();
        var result = await fixture.Run("plan", "--base", "main", "--head", "WORKTREE");

        Assert.True(
            result.ExitCode == 0,
            $"Wrapper failed with {result.ExitCode}.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
        var invocation = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.Contains("docker compose", invocation, StringComparison.Ordinal);
        Assert.Contains("run --rm --no-deps", invocation, StringComparison.Ordinal);
        Assert.True(
            invocation.Contains($"--workdir {fixture.WorkingDirectory}", StringComparison.Ordinal),
            invocation);
        Assert.True(
            invocation.Contains($"--volume {fixture.RepositoryRoot}:{fixture.RepositoryRoot}", StringComparison.Ordinal),
            invocation);
        Assert.True(invocation.Contains("plan --base main --head WORKTREE", StringComparison.Ordinal), invocation);
    }

    [Fact]
    public async Task RuntimeConfigurationForwardsOnlyNamedEnvironmentAndRepositoryMounts()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new RuntimeFixture();
        fixture.AddRuntimeConfiguration(
            """
            image: team/merkle-runtime:local
            environment:
              - PRIVATE_FEED_TOKEN
            mounts:
              - .tool-cache

            """);
        fixture.SetEnvironment("PRIVATE_FEED_TOKEN", "never-write-this-value");

        var result = await fixture.Run("--offline", "plan", "--base", "main", "--head", "WORKTREE");

        Assert.True(result.ExitCode == 0, result.StandardError);
        var invocation = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.Contains("NETWORK=none", invocation, StringComparison.Ordinal);
        Assert.Contains("IMAGE=team/merkle-runtime:local", invocation, StringComparison.Ordinal);
        Assert.Contains("- PRIVATE_FEED_TOKEN", invocation, StringComparison.Ordinal);
        Assert.Contains(".tool-cache", invocation, StringComparison.Ordinal);
        Assert.DoesNotContain("never-write-this-value", invocation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectUseWritesExactPinAtTheGitRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new RuntimeFixture();
        var list = await fixture.Run("list");
        var use = await fixture.Run("use", fixture.InstallationId, "--project");

        Assert.True(list.ExitCode == 0, list.StandardError);
        Assert.Contains($"* {fixture.InstallationId}", list.StandardOutput, StringComparison.Ordinal);
        Assert.True(use.ExitCode == 0, use.StandardError);
        var pin = await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryRoot, ".merkle-version"));
        Assert.Contains("version: v1.0.0", pin, StringComparison.Ordinal);
        Assert.Contains("commit: 0123456789abcdef", pin, StringComparison.Ordinal);
        Assert.Contains("architecture: amd64", pin, StringComparison.Ordinal);
        Assert.Contains("adapters: dotnet,golang,python,java", pin, StringComparison.Ordinal);
        Assert.Contains($"installation_id: {fixture.InstallationId}", pin, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.WorkingDirectory, ".merkle-version")));
    }

    [Fact]
    public async Task LinkedWorktreeMountsOnlyItsExactExternalGitDirectories()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new RuntimeFixture(linkedWorktree: true);
        var result = await fixture.Run("plan", "--base", "main", "--head", "WORKTREE");

        Assert.True(result.ExitCode == 0, result.StandardError);
        var invocation = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.Contains($"source: '{fixture.GitDirectory}'", invocation, StringComparison.Ordinal);
        Assert.Contains($"source: '{fixture.GitCommonDirectory}'", invocation, StringComparison.Ordinal);
        Assert.Contains("read_only: true", invocation, StringComparison.Ordinal);
        Assert.DoesNotContain($"--volume {fixture.Home}:{fixture.Home}", invocation, StringComparison.Ordinal);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _wrapper;
        private readonly Dictionary<string, string> _environment;

        public RuntimeFixture(bool linkedWorktree = false)
        {
            var merkleRepository = FindMerkleRepositoryRoot();
            _wrapper = Path.Combine(merkleRepository, "scripts", "merkle");
            var temporaryRoot = Path.Combine(Path.GetTempPath(), $"merkle-runtime-tests-{Guid.NewGuid():N}");
            _root = OperatingSystem.IsMacOS() && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal)
                ? "/private" + temporaryRoot
                : temporaryRoot;
            RepositoryRoot = Path.Combine(_root, "repository");
            WorkingDirectory = Path.Combine(RepositoryRoot, "src", "nested");
            GitDirectory = linkedWorktree
                ? Path.Combine(_root, "main-git", "worktrees", "agent")
                : Path.Combine(RepositoryRoot, ".git");
            GitCommonDirectory = linkedWorktree
                ? Path.Combine(_root, "main-git")
                : GitDirectory;
            var dataRoot = Path.Combine(_root, "data", "merkle");
            InstallationId = "0123456789abcdef-amd64-dotnet-golang-python-java";
            var installation = Path.Combine(dataRoot, "installs", InstallationId);
            var fakeBin = Path.Combine(_root, "bin");
            InvocationLog = Path.Combine(_root, "docker.log");

            Directory.CreateDirectory(WorkingDirectory);
            Directory.CreateDirectory(GitDirectory);
            Directory.CreateDirectory(GitCommonDirectory);
            Directory.CreateDirectory(Path.Combine(installation, "source"));
            Directory.CreateDirectory(fakeBin);
            File.WriteAllText(Path.Combine(installation, "source", "compose.yaml"), "services: { merkle: {} }\n");
            File.WriteAllText(
                Path.Combine(installation, "install.env"),
                $"""
                schema=1
                resolved_ref=v1.0.0
                commit=0123456789abcdef
                architecture=amd64
                runtime=linux-x64
                adapters=dotnet,golang,python,java
                image=merkle-local:{InstallationId}
                image_id=sha256:feedface
                installation_id={InstallationId}

                """);
            Directory.CreateSymbolicLink(Path.Combine(dataRoot, "current"), Path.Combine("installs", InstallationId));

            WriteExecutable(
                Path.Combine(fakeBin, "git"),
                $$"""
                #!/bin/sh
                if [ "$1" = "-C" ]; then shift 2; fi
                if [ "$1" = "rev-parse" ]; then
                    case "$2" in
                        --show-toplevel) printf '%s\n' '{{RepositoryRoot}}' ;;
                        --git-dir) printf '%s\n' '{{GitDirectory}}' ;;
                        --git-common-dir) printf '%s\n' '{{GitCommonDirectory}}' ;;
                        *) exit 91 ;;
                    esac
                    exit 0
                fi
                exit 92
                """);
            WriteExecutable(
                Path.Combine(fakeBin, "docker"),
                """
                #!/bin/sh
                printf 'docker %s NETWORK=%s IMAGE=%s\n' "$*" "${MERKLE_NETWORK_MODE:-}" "${MERKLE_IMAGE:-}" >> "$MERKLE_TEST_LOG"
                for argument in "$@"; do
                    case "$argument" in *.override.yaml) printf '%s\n' '--- override ---' >> "$MERKLE_TEST_LOG"; cat "$argument" >> "$MERKLE_TEST_LOG" ;; esac
                done
                if [ "$1" = "context" ] && [ "$2" = "inspect" ]; then printf '%s\n' unix:///tmp/docker.sock; exit 0; fi
                if [ "$1" = "compose" ] && [ "$2" = "version" ]; then exit 0; fi
                if [ "$1" = "image" ] && [ "$2" = "inspect" ]; then
                    case "$*" in
                        *org.merkle.installation-id*) printf '%s\n' 0123456789abcdef-amd64-dotnet-golang-python-java ;;
                        *org.merkle.managed*) printf '%s\n' true ;;
                        *org.merkle.adapters*) printf '%s\n' dotnet,golang,python,java ;;
                        *--format*) printf '%s\n' sha256:feedface ;;
                    esac
                    exit 0
                fi
                if [ "$1" = "compose" ]; then exit 0; fi
                exit 93
                """);

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            _environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeBin + Path.PathSeparator + existingPath,
                ["HOME"] = Home,
                ["MERKLE_DATA_HOME"] = dataRoot,
                ["MERKLE_CACHE_HOME"] = Path.Combine(_root, "cache"),
                ["MERKLE_TEST_LOG"] = InvocationLog,
                ["MERKLE_LOCK_ROOT"] = Path.Combine(_root, "locks")
            };
        }

        public string RepositoryRoot { get; }
        public string WorkingDirectory { get; }
        public string InvocationLog { get; }
        public string InstallationId { get; }
        public string GitDirectory { get; }
        public string GitCommonDirectory { get; }
        public string Home => Path.Combine(_root, "home");

        public void AddRuntimeConfiguration(string contents)
        {
            File.WriteAllText(Path.Combine(RepositoryRoot, ".merkle-runtime.yml"), contents);
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".tool-cache"));
        }

        public void SetEnvironment(string name, string value)
        {
            _environment[name] = value;
        }

        public async Task<ProcessResult> Run(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(_wrapper)
            {
                WorkingDirectory = WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            foreach (var pair in _environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {_wrapper}.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void WriteExecutable(string path, string contents)
        {
            File.WriteAllText(path, contents);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        private static string FindMerkleRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Merkle.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the Merkle repository root.");
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
