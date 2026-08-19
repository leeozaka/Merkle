using System.Diagnostics;
using System.Text.Json;
using Merkle.Build;

namespace Merkle.Tests.Build;

public sealed class DirectDotNetBuildTests
{
    [Fact]
    public async Task DirectDotNetBuildDoesNotInvokeGo()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var runDirectory = Path.Combine(Path.GetTempPath(), $"merkle-direct-build-{Guid.NewGuid():N}");
        var fakeToolDirectory = Path.Combine(runDirectory, "tools");
        var fakeGoPath = Path.Combine(fakeToolDirectory, "go");
        var invocationMarker = Path.Combine(runDirectory, "go-invoked");
        var isolatedArtifacts = Path.Combine(runDirectory, "artifacts");
        var configuration = $"DirectNoGo_{Guid.NewGuid():N}";

        Directory.CreateDirectory(fakeToolDirectory);
        await File.WriteAllTextAsync(
            fakeGoPath,
            "#!/bin/sh\nprintf 'go was invoked\\n' > \"$FAKE_GO_INVOCATION_MARKER\"\nexit 97\n");
        File.SetUnixFileMode(
            fakeGoPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var startInfo = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("src/cli/Merkle.Cli.csproj");
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(configuration);
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("-m:1");
            startInfo.ArgumentList.Add("-nodeReuse:false");
            startInfo.ArgumentList.Add("--artifacts-path");
            startInfo.ArgumentList.Add(isolatedArtifacts);
            startInfo.Environment["PATH"] = fakeToolDirectory + Path.PathSeparator + path;
            startInfo.Environment["FAKE_GO_INVOCATION_MARKER"] = invocationMarker;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start dotnet.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;

            Assert.True(
                process.ExitCode == 0,
                $"dotnet build should succeed without Go. Exit code: {process.ExitCode}\nstdout:\n{output}\nstderr:\n{error}\nGo marker exists: {File.Exists(invocationMarker)}");
            Assert.False(File.Exists(invocationMarker), "The direct .NET build invoked the Go executable.");
        }
        finally
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectDotNetPublishWritesDotNetOnlyAdapterManifest()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var runDirectory = Path.Combine(Path.GetTempPath(), $"merkle-direct-publish-{Guid.NewGuid():N}");
        var publishDirectory = Path.Combine(runDirectory, "publish");
        var configuration = $"DirectManifest_{Guid.NewGuid():N}";
        Directory.CreateDirectory(runDirectory);

        try
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var startInfo = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "publish",
                "src/cli/Merkle.Cli.csproj",
                "--configuration", configuration,
                "--no-restore",
                "--nologo",
                "--output", publishDirectory,
                "--self-contained", "false",
                "-p:PublishAot=false",
                "-m:1",
                "-nodeReuse:false"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start dotnet.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;

            Assert.True(
                process.ExitCode == 0,
                $"dotnet publish failed. Exit code: {process.ExitCode}\nstdout:\n{output}\nstderr:\n{error}");

            var manifestPath = Path.Combine(publishDirectory, "adapters.json");
            Assert.True(File.Exists(manifestPath), "Direct publish did not emit adapters.json.");
            AdapterManifestContract.ValidateFile(manifestPath);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var adapters = manifest.RootElement.GetProperty("adapters").EnumerateArray().ToArray();
            var adapter = Assert.Single(adapters);
            Assert.Equal("dotnet", adapter.GetProperty("id").GetString());
            Assert.All(
                adapter.GetProperty("artifacts").EnumerateArray(),
                artifact => Assert.Matches("^[0-9a-f]{64}$", artifact.GetProperty("sha256").GetString()));
        }
        finally
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
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
}
