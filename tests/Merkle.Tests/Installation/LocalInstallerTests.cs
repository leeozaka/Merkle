using System.Diagnostics;

namespace Merkle.Tests.Installation;

public sealed class LocalInstallerTests
{
    [Fact]
    public void DockerfileRestoresRuntimeCachePermissionsAfterPublishing()
    {
        var dockerfile = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Dockerfile"));
        var publish = dockerfile.IndexOf("./build publish", StringComparison.Ordinal);
        var finalPermissionRepair = dockerfile.LastIndexOf(
            "chmod -R 0777 /var/cache/merkle",
            StringComparison.Ordinal);

        Assert.True(publish >= 0, "Dockerfile must publish the Merkle runtime.");
        Assert.True(
            finalPermissionRepair > publish,
            "Dockerfile must restore non-root cache permissions after the root-owned publish step.");
    }

    [Fact]
    public async Task HelpDescribesVersionAndAdapterSelectionWithoutProvisioning()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var result = await Run(
            Path.Combine(repositoryRoot, "install"),
            repositoryRoot,
            null,
            "--help");

        Assert.True(
            result.ExitCode == 0,
            $"Installer failed with {result.ExitCode}.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
        Assert.Contains("--ref", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--adapters", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("docker", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultInstallPromotesHighestStableAllAdapterVariant()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var result = await Run(
            fixture.InstallerPath,
            fixture.RepositoryRoot,
            fixture.ProcessEnvironment);

        Assert.True(
            result.ExitCode == 0,
            $"Installer failed with {result.ExitCode}.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
        Assert.Contains("v1.10.0", result.StandardOutput, StringComparison.Ordinal);

        var current = Path.Combine(fixture.DataHome, "merkle", "current");
        Assert.True(File.Exists(Path.Combine(current, "install.env")));
        var manifest = await File.ReadAllTextAsync(Path.Combine(current, "install.env"));
        Assert.Contains("resolved_ref=v1.10.0", manifest, StringComparison.Ordinal);
        Assert.Contains("commit=0123456789abcdef0123456789abcdef01234567", manifest, StringComparison.Ordinal);
        Assert.Contains("architecture=arm64", manifest, StringComparison.Ordinal);
        Assert.Contains("adapters=dotnet,golang,python,java", manifest, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Home, ".local", "bin", "merkle")));

        var invocations = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.Contains("git ls-remote --tags https://github.com/leeozaka/merkle.git", invocations, StringComparison.Ordinal);
        Assert.Contains("MERKLE_ADAPTERS=dotnet,golang,python,java", invocations, StringComparison.Ordinal);
        Assert.Contains("docker compose", invocations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterAliasesNormalizeBeforeTheImageBuild()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var result = await Run(
            fixture.InstallerPath,
            fixture.RepositoryRoot,
            fixture.ProcessEnvironment,
            "--ref",
            "v1.9.0",
            "--adapters",
            "python,go,golang");

        Assert.True(result.ExitCode == 0, result.StandardError);
        var current = Path.Combine(fixture.DataHome, "merkle", "current", "install.env");
        var manifest = await File.ReadAllTextAsync(current);
        Assert.Contains("adapters=golang,python", manifest, StringComparison.Ordinal);
        var invocations = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.Contains("MERKLE_ADAPTERS=golang,python", invocations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedBuildDoesNotReplaceTheCurrentInstallation()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var first = await Run(
            fixture.InstallerPath,
            fixture.RepositoryRoot,
            fixture.ProcessEnvironment,
            "--ref",
            "v1.9.0");
        Assert.True(first.ExitCode == 0, first.StandardError);

        var current = Path.Combine(fixture.DataHome, "merkle", "current");
        var before = Directory.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName;
        var failingEnvironment = new Dictionary<string, string>(fixture.ProcessEnvironment, StringComparer.Ordinal)
        {
            ["MERKLE_TEST_FAIL_BUILD"] = "true"
        };
        var second = await Run(
            fixture.InstallerPath,
            fixture.RepositoryRoot,
            failingEnvironment,
            "--ref",
            "v1.11.0",
            "--adapters",
            "golang");

        Assert.NotEqual(0, second.ExitCode);
        Assert.Equal(before, Directory.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(fixture.DataHome, "merkle", "installs")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.DataHome, "merkle"), ".staging.*"));
    }

    [Fact]
    public async Task UnknownAdapterFailsBeforeCloneOrBuild()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var result = await Run(
            fixture.InstallerPath,
            fixture.RepositoryRoot,
            fixture.ProcessEnvironment,
            "--adapters",
            "ruby");

        Assert.Equal(2, result.ExitCode);
        var invocations = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.DoesNotContain("git clone", invocations, StringComparison.Ordinal);
        Assert.DoesNotContain(" build ", invocations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedUnqualifiedInstallDoesNotCloneOrBuildAgain()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var first = await Run(fixture.InstallerPath, fixture.RepositoryRoot, fixture.ProcessEnvironment);
        Assert.True(first.ExitCode == 0, first.StandardError);
        File.WriteAllText(fixture.InvocationLog, string.Empty);

        var second = await Run(fixture.InstallerPath, fixture.RepositoryRoot, fixture.ProcessEnvironment);

        Assert.True(second.ExitCode == 0, second.StandardError);
        Assert.Contains("no newer stable version", second.StandardOutput, StringComparison.Ordinal);
        var invocations = await File.ReadAllTextAsync(fixture.InvocationLog);
        Assert.DoesNotContain("git clone", invocations, StringComparison.Ordinal);
        Assert.DoesNotContain(" build ", invocations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingStableTagFallsBackToTheOfficialDefaultBranch()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new InstallerFixture();
        var environment = new Dictionary<string, string>(fixture.ProcessEnvironment, StringComparer.Ordinal)
        {
            ["MERKLE_TEST_NO_TAGS"] = "true"
        };

        var result = await Run(fixture.InstallerPath, fixture.RepositoryRoot, environment);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("installing default branch main", result.StandardOutput, StringComparison.Ordinal);
        var manifest = await File.ReadAllTextAsync(Path.Combine(fixture.DataHome, "merkle", "current", "install.env"));
        Assert.Contains("resolved_ref=main", manifest, StringComparison.Ordinal);
        Assert.Contains("development_build=true", manifest, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> Run(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class InstallerFixture : IDisposable
    {
        private readonly string _root;

        public InstallerFixture()
        {
            RepositoryRoot = FindRepositoryRoot();
            InstallerPath = Path.Combine(RepositoryRoot, "install");
            _root = Path.Combine(Path.GetTempPath(), $"merkle-installer-tests-{Guid.NewGuid():N}");
            Home = Path.Combine(_root, "home");
            DataHome = Path.Combine(_root, "data");
            InvocationLog = Path.Combine(_root, "invocations.log");
            var fakeBin = Path.Combine(_root, "bin");
            var sourceTemplate = Path.Combine(_root, "source-template");
            Directory.CreateDirectory(fakeBin);
            Directory.CreateDirectory(Path.Combine(sourceTemplate, "scripts"));
            Directory.CreateDirectory(Home);
            File.WriteAllText(Path.Combine(sourceTemplate, "compose.yaml"), "services: { merkle: {} }\n");
            File.WriteAllText(Path.Combine(sourceTemplate, "Dockerfile"), "FROM scratch\n");
            File.WriteAllText(Path.Combine(sourceTemplate, "scripts", "merkle"), "#!/bin/sh\nexit 0\n");
            MakeExecutable(Path.Combine(sourceTemplate, "scripts", "merkle"));

            WriteExecutable(
                Path.Combine(fakeBin, "git"),
                """
                #!/bin/sh
                printf 'git %s\n' "$*" >> "$MERKLE_TEST_LOG"
                if [ "$1" = "ls-remote" ] && [ "$2" = "--tags" ]; then
                    if [ "${MERKLE_TEST_NO_TAGS:-}" = "true" ]; then exit 0; fi
                    printf '%s\t%s\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/tags/v1.9.0
                    printf '%s\t%s\n' bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb refs/tags/v2.0.0-rc.1
                    printf '%s\t%s\n' 0123456789abcdef0123456789abcdef01234567 refs/tags/v1.10.0
                    exit 0
                fi
                if [ "$1" = "ls-remote" ] && [ "$2" = "--symref" ]; then
                    printf '%s\n' 'ref: refs/heads/main HEAD'
                    exit 0
                fi
                if [ "$1" = "clone" ]; then
                    destination=""
                    for argument in "$@"; do destination="$argument"; done
                    mkdir -p "$destination"
                    cp -R "$MERKLE_TEST_SOURCE/." "$destination/"
                    exit 0
                fi
                if [ "$1" = "-C" ] && [ "$3" = "rev-parse" ]; then
                    printf '%s\n' "${MERKLE_TEST_COMMIT:-0123456789abcdef0123456789abcdef01234567}"
                    exit 0
                fi
                if [ "$1" = "-C" ]; then exit 0; fi
                exit 97
                """);

            WriteExecutable(
                Path.Combine(fakeBin, "docker"),
                """
                #!/bin/sh
                printf 'docker %s MERKLE_ADAPTERS=%s\n' "$*" "${MERKLE_ADAPTERS:-}" >> "$MERKLE_TEST_LOG"
                if [ "$1" = "context" ] && [ "$2" = "inspect" ]; then printf '%s\n' unix:///tmp/docker.sock; exit 0; fi
                if [ "$1" = "info" ]; then printf '%s\n' arm64; exit 0; fi
                if [ "$1" = "compose" ] && [ "$2" = "version" ]; then printf '%s\n' 'Docker Compose version v2.30.0'; exit 0; fi
                if [ "$1" = "image" ] && [ "$2" = "inspect" ]; then
                    case "$*" in
                        *org.merkle.installation-id*) printf '%s\n' "${MERKLE_INSTALLATION_ID:-}" ;;
                        *org.merkle.managed*) printf '%s\n' true ;;
                        *org.merkle.adapters*) printf '%s\n' "${MERKLE_ADAPTERS:-}" ;;
                        *) printf '%s\n' sha256:feedface ;;
                    esac
                    exit 0
                fi
                if [ "$1" = "compose" ] && [ "${MERKLE_TEST_FAIL_BUILD:-}" = "true" ]; then
                    for argument in "$@"; do [ "$argument" = "build" ] && exit 66; done
                fi
                if [ "$1" = "compose" ]; then exit 0; fi
                exit 98
                """);

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            ProcessEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = fakeBin + Path.PathSeparator + existingPath,
                ["HOME"] = Home,
                ["XDG_DATA_HOME"] = DataHome,
                ["XDG_CACHE_HOME"] = Path.Combine(_root, "cache"),
                ["MERKLE_TEST_LOG"] = InvocationLog,
                ["MERKLE_TEST_SOURCE"] = sourceTemplate
            };
        }

        public string RepositoryRoot { get; }
        public string InstallerPath { get; }
        public string Home { get; }
        public string DataHome { get; }
        public string InvocationLog { get; }
        public IReadOnlyDictionary<string, string> ProcessEnvironment { get; }

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
            MakeExecutable(path);
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }
}
