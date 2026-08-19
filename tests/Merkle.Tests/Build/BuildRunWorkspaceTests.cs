using Merkle.Build;
using Merkle.Core.Errors;

namespace Merkle.Tests.Build;

public sealed class BuildRunWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"merkle-workspace-tests-{Guid.NewGuid():N}");

    public BuildRunWorkspaceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Acquire_HoldsDestinationLeaseForTheWholeRun()
    {
        var request = Request(Path.Combine(_root, "output"));
        var factory = new BuildRunWorkspaceFactory();
        await using var first = await factory.AcquireAsync(request, default);

        var error = await Assert.ThrowsAsync<IOException>(() => factory.AcquireAsync(request, default).AsTask());

        Assert.Contains("already being built", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_RemovesOnlyMarkedHelperIntermediates()
    {
        var output = Path.Combine(_root, "output");
        var owned = output + ".staging-owned";
        var unowned = output + ".staging-user";
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(unowned);
        await File.WriteAllTextAsync(Path.Combine(owned, BuildRunWorkspaceFactory.WorkspaceMarkerFileName), "owned");
        await File.WriteAllTextAsync(Path.Combine(unowned, "keep.txt"), "user");

        await using var workspace = await new BuildRunWorkspaceFactory().AcquireAsync(
            Request(output) with { Clean = true },
            default);

        Assert.False(Directory.Exists(owned));
        Assert.True(Directory.Exists(unowned));
        Assert.True(File.Exists(Path.Combine(
            Directory.GetParent(workspace.Context.StagingDirectory)!.FullName,
            BuildRunWorkspaceFactory.WorkspaceMarkerFileName)));
    }

    [Fact]
    public async Task Acquire_RejectsReportInsidePackageOutput()
    {
        var output = Path.Combine(_root, "output");
        var request = Request(output) with { ReportPath = Path.Combine(output, "build-report.json") };

        var error = await Assert.ThrowsAsync<ConfigurationException>(() =>
            new BuildRunWorkspaceFactory().AcquireAsync(request, default).AsTask());

        Assert.Equal("ReportInsidePackage", error.Code);
    }

    [Fact]
    public async Task Promote_RejectsPathsThatDoNotBelongToLease()
    {
        var output = Path.Combine(_root, "output");
        await using var workspace = await new BuildRunWorkspaceFactory().AcquireAsync(Request(output), default);
        var request = new BuildOutputRequest(
            Path.Combine(_root, "different-output"),
            workspace.Context.HostStagingDirectory!,
            workspace.Context.StagingDirectory,
            "Debug",
            BuildRuntimeIdentifier.Current,
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.PromoteAsync(request, default).AsTask());

        Assert.Contains("acquired build workspace", error.Message, StringComparison.Ordinal);
    }

    private static BuildRequest Request(string output) =>
        new(BuildCommand.Build, ["dotnet"], OutputPath: output);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
