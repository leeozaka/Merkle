using System.Security.Cryptography;
using System.Text;
using Merkle.Build;
using Merkle.Core.Processes;

namespace Merkle.Tests.Build;

public sealed class AdapterBuildDefinitionsTests
{
    [Fact]
    public async Task PreflightDerivesMinimumVersionsFromRepositoryMetadata()
    {
        using var repository = new TemporaryRepository();
        repository.Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
        repository.Write("src/adapters/go/worker/go.mod", "module merkle\n\ngo 1.22\n");
        repository.Write("src/adapters/python/pyproject.toml", "[project]\nrequires-python = \">=3.10\"\n");
        repository.Write("src/adapters/java/pom.xml", "<project><properties><maven.compiler.source>17</maven.compiler.source></properties></project>");
        var runner = new ScriptedProcessRunner(request => request.FileName switch
        {
            "dotnet" => Result("10.0.301\n"),
            "go" => Result("go version go1.22.0 darwin/arm64\n"),
            "python3" => Result("Python 3.10.2\n"),
            "java" => Result("", "openjdk 17.0.1 2021-10-19\n"),
            "javac" => Result("javac 17.0.1\n"),
            "mvn" => Result("Apache Maven 3.9.9\n"),
            _ => throw new InvalidOperationException(request.FileName)
        });
        var catalog = new AdapterBuildCatalog(runner);
        var context = repository.Context;

        foreach (var adapter in catalog.ResolveAll())
        {
            var readiness = await adapter.PreflightAsync(context, CancellationToken.None);
            Assert.Equal(AdapterReadinessStatus.Ready, readiness.Status);
        }
    }

    [Fact]
    public async Task OldToolchainIsUnavailableWithDetectedAndRequiredDetails()
    {
        using var repository = new TemporaryRepository();
        repository.Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
        var runner = new ScriptedProcessRunner(_ => Result("9.0.100\n"));
        var readiness = await new AdapterBuildCatalog(runner).Resolve("dotnet").PreflightAsync(repository.Context, CancellationToken.None);

        Assert.Equal(AdapterReadinessStatus.Unavailable, readiness.Status);
        Assert.Contains("10.0.301", readiness.Reason);
        Assert.Equal("9.0.100", readiness.DetectedVersion);
        Assert.Equal("dotnet", readiness.RequiredTool);
    }

    [Fact]
    public async Task GoBuildUsesRidEnvironmentAndReportsCommandFailure()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/go/worker/go.mod", "module merkle\n\ngo 1.22\n");
        var runner = new ScriptedProcessRunner(request => request.FileName == "go" && request.Arguments.SequenceEqual(["version"])
            ? Result("go version go1.22.0 darwin/arm64\n")
            : Result("", "compile failed", 1));
        var adapter = new AdapterBuildCatalog(runner).Resolve("go");
        var readiness = await adapter.PreflightAsync(repository.Context, CancellationToken.None);
        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Failed, result.Status);
        var build = Assert.Single(runner.Requests, request => request.Arguments.Contains("build"));
        Assert.Equal("darwin", build.Environment!["GOOS"]);
        Assert.Equal("arm64", build.Environment["GOARCH"]);
        var log = await File.ReadAllTextAsync(Path.Combine(repository.Context.RunDirectory, "logs", "golang.log"));
        Assert.Contains("go build", log, StringComparison.Ordinal);
        Assert.Contains("compile failed", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PythonBuildCreatesCurrentRunZipAppAndChecksum()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/python/pyproject.toml", "[project]\nrequires-python = \">=3.10\"\n");
        repository.Write("src/adapters/python/merkle_adapter/__init__.py", "");
        repository.Write("src/adapters/python/merkle_adapter/__main__.py", "");
        var runner = new ScriptedProcessRunner(request => request.FileName == "python3" && request.Arguments.SequenceEqual(["--version"])
            ? Result("Python 3.12.4\n")
            : Result("{\"protocolVersion\":\"1.0\",\"language\":\"python\"}\n"));
        var adapter = new AdapterBuildCatalog(runner).Resolve("python");
        var readiness = await adapter.PreflightAsync(repository.Context, CancellationToken.None);
        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        var artifact = Assert.Single(result.Artifacts);
        var path = Path.Combine(repository.Context.StagingDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(AdapterBuildStatus.Built, result.Status);
        Assert.True(File.Exists(path));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), artifact.Sha256);
        Assert.DoesNotContain("target", artifact.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessResult Result(string stdout, string stderr = "", int exitCode = 0) => new(exitCode, stdout, stderr);

    private sealed class ScriptedProcessRunner(Func<ProcessRequest, ProcessResult> script) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(script(request));
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), $"merkle-adapter-definitions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Context = new BuildContext(Root, "Debug", "osx-arm64", Path.Combine(Root, "run"), Path.Combine(Root, "staging"));
            Directory.CreateDirectory(Context.StagingDirectory);
        }

        public string Root { get; }
        public BuildContext Context { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
