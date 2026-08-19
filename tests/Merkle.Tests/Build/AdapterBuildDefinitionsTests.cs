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

    [Fact]
    public async Task DotNetBuildProducesWorkerAndObserverAfterProtocolSmoke()
    {
        using var repository = new TemporaryRepository();
        repository.Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
        var runner = new ScriptedProcessRunner(request =>
        {
            if (request.Arguments.SequenceEqual(["--version"])) return Result("10.0.301\n");
            if (request.Arguments.Count > 0 && request.Arguments[0] == "build")
            {
                var destination = ArgumentValue(request.Arguments, "--output");
                Directory.CreateDirectory(destination);
                var fileName = request.Arguments[1].Contains("Worker", StringComparison.Ordinal)
                    ? "Merkle.Adapters.DotNet.Worker.dll"
                    : "Merkle.Adapters.DotNet.Observer.dll";
                File.WriteAllText(Path.Combine(destination, fileName), fileName, Encoding.UTF8);
                return Result("");
            }

            return Result("{\"protocolVersion\":\"1.0\",\"success\":true}\n");
        });
        var adapter = new AdapterBuildCatalog(runner).Resolve("dotnet");
        var readiness = await adapter.PreflightAsync(repository.Context, CancellationToken.None);

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Built, result.Status);
        Assert.Equal(2, result.Artifacts.Count);
        Assert.Contains(result.Artifacts, artifact => artifact.RelativePath.EndsWith("Worker.dll", StringComparison.Ordinal));
        Assert.Contains(result.Artifacts, artifact => artifact.RelativePath.EndsWith("Observer.dll", StringComparison.Ordinal));
        Assert.Equal(3, runner.Requests.Count(request => request.FileName == "dotnet" && !request.Arguments.SequenceEqual(["--version"])));
    }

    [Fact]
    public async Task GoBuildRunsSelectedTestsAndSmokesProducedExecutable()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/go/worker/go.mod", "module merkle\n\ngo 1.22\n");
        var runner = new ScriptedProcessRunner(request =>
        {
            if (request.FileName == "go" && request.Arguments.Contains("build"))
            {
                var executable = ArgumentValue(request.Arguments, "-o");
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                File.WriteAllText(executable, "go worker", Encoding.UTF8);
                return Result("");
            }

            return request.FileName == "go"
                ? Result("")
                : Result("{\"protocolVersion\":\"1.0\",\"language\":\"golang\"}\n");
        });
        var adapter = new AdapterBuildCatalog(runner).Resolve("go");
        var readiness = new AdapterReadiness("golang", AdapterReadinessStatus.Ready, DetectedVersion: "go1.22.0");

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: true, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Built, result.Status);
        Assert.Single(result.Artifacts);
        Assert.Contains(runner.Requests, request => request.FileName == "go" && request.Arguments.SequenceEqual(["test", "./..."]));
        Assert.Contains(runner.Requests, request => request.FileName != "go" && request.StandardInput.HasValue);
    }

    [Fact]
    public async Task JavaBuildRunsSelectedTestsAndSmokesProducedJar()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/java/pom.xml", "<project><properties><maven.compiler.source>17</maven.compiler.source></properties></project>");
        var runner = new ScriptedProcessRunner(request =>
        {
            if (request.FileName == "mvn")
            {
                const string property = "-Dproject.build.directory=";
                var destination = request.Arguments.Single(argument => argument.StartsWith(property, StringComparison.Ordinal))[property.Length..];
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "merkle-adapter-java.jar"), "java worker", Encoding.UTF8);
                return Result("");
            }

            return Result("{\"protocolVersion\":\"1.0\",\"language\":\"java\"}\n");
        });
        var adapter = new AdapterBuildCatalog(runner).Resolve("java");
        var readiness = new AdapterReadiness("java", AdapterReadinessStatus.Ready, DetectedVersion: "OpenJDK 21");

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: true, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Built, result.Status);
        Assert.Single(result.Artifacts);
        var maven = Assert.Single(runner.Requests, request => request.FileName == "mvn");
        Assert.DoesNotContain("-DskipTests", maven.Arguments);
        Assert.Contains(runner.Requests, request => request.FileName == "java" && request.Arguments[0] == "-jar");
    }

    [Fact]
    public async Task JavaBuildReportsMissingJarBeforeStartingSmokeProcess()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/java/pom.xml", "<project />");
        var runner = new ScriptedProcessRunner(request => request.FileName == "mvn"
            ? Result("")
            : throw new InvalidOperationException("Java must not start without the expected JAR."));
        var adapter = new AdapterBuildCatalog(runner).Resolve("java");
        var readiness = new AdapterReadiness("java", AdapterReadinessStatus.Ready);

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Failed, result.Status);
        Assert.Contains("did not produce", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request => request.FileName == "java");
    }

    [Fact]
    public async Task JavaBuildIncludesSmokeProcessFailureInDiagnostic()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/java/pom.xml", "<project />");
        var runner = new ScriptedProcessRunner(request =>
        {
            if (request.FileName == "mvn")
            {
                const string property = "-Dproject.build.directory=";
                var destination = request.Arguments.Single(argument => argument.StartsWith(property, StringComparison.Ordinal))[property.Length..];
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "merkle-adapter-java.jar"), "invalid jar", Encoding.UTF8);
                return Result("");
            }

            return Result("", "no main manifest attribute", 1);
        });
        var adapter = new AdapterBuildCatalog(runner).Resolve("java");
        var readiness = new AdapterReadiness("java", AdapterReadinessStatus.Ready);

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Failed, result.Status);
        Assert.Contains("no main manifest attribute", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JavaBuildNormalizesSingleVersionedMavenJarBeforeSmoke()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/java/pom.xml", "<project />");
        var runner = new ScriptedProcessRunner(request =>
        {
            if (request.FileName == "mvn")
            {
                const string property = "-Dproject.build.directory=";
                var destination = request.Arguments.Single(argument => argument.StartsWith(property, StringComparison.Ordinal))[property.Length..];
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "merkle-adapter-java-1.0.0.jar"), "shaded jar", Encoding.UTF8);
                File.WriteAllText(Path.Combine(destination, "original-merkle-adapter-java-1.0.0.jar"), "original jar", Encoding.UTF8);
                return Result("");
            }

            Assert.Equal("-jar", request.Arguments[0]);
            Assert.EndsWith("merkle-adapter-java.jar", request.Arguments[1], StringComparison.Ordinal);
            Assert.True(File.Exists(request.Arguments[1]));
            return Result("{\"protocolVersion\":\"1.0\",\"language\":\"java\"}\n");
        });
        var adapter = new AdapterBuildCatalog(runner).Resolve("java");
        var readiness = new AdapterReadiness("java", AdapterReadinessStatus.Ready);

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: false, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Built, result.Status);
        Assert.Equal("workers/java/merkle-adapter-java.jar", Assert.Single(result.Artifacts).RelativePath);
    }

    [Fact]
    public async Task PythonBuildReportsSelectedTestFailureBeforePackaging()
    {
        using var repository = new TemporaryRepository();
        repository.Write("src/adapters/python/pyproject.toml", "[project]\nrequires-python = \">=3.10\"\n");
        var runner = new ScriptedProcessRunner(_ => Result("", "python tests failed", 1));
        var adapter = new AdapterBuildCatalog(runner).Resolve("python");
        var readiness = new AdapterReadiness("python", AdapterReadinessStatus.Ready, DetectedVersion: "Python 3.12.4");

        var result = await adapter.BuildAsync(
            new AdapterBuildRequest(repository.Context, RunTests: true, readiness),
            CancellationToken.None);

        Assert.Equal(AdapterBuildStatus.Failed, result.Status);
        Assert.Equal("python tests failed", result.Diagnostic);
        Assert.Single(runner.Requests, request => request.Arguments.Contains("unittest"));
        Assert.Empty(result.Artifacts);
    }

    private static ProcessResult Result(string stdout, string stderr = "", int exitCode = 0) => new(exitCode, stdout, stderr);

    private static string ArgumentValue(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        return arguments[index + 1];
    }

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
