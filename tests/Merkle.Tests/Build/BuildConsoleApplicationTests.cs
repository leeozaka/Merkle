using System.Text.Json;
using Merkle.Build;
using Merkle.Core.Errors;

namespace Merkle.Tests.Build;

public sealed class BuildConsoleApplicationTests
{
    [Fact]
    public async Task Run_InteractiveProbesEveryAdapterAndUsesBuildDotNetStrictDefaults()
    {
        var dotnet = FakeAdapter.Ready("dotnet");
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var output = new StringWriter();
        var application = Application(
            new StringReader("\n\n\n"),
            output,
            new StringWriter(),
            new FakeCatalog(dotnet, java),
            orchestrator);

        var exitCode = await application.RunAsync([], interactive: true, default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, dotnet.PreflightCalls);
        Assert.Equal(1, java.PreflightCalls);
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(BuildCommand.Build, request.Command);
        Assert.Equal(["dotnet"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.Strict, request.Policy);
        Assert.Contains("dotnet: ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("java: unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Build or publish", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InteractiveStrictUnavailableSelectionCanBeRevised()
    {
        var dotnet = FakeAdapter.Ready("dotnet");
        var java = FakeAdapter.Unavailable("java", "JDK 17+ was not found");
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var output = new StringWriter();
        var application = Application(
            new StringReader("publish\njava\nstrict\ny\npublish\ndotnet\nstrict\n"),
            output,
            new StringWriter(),
            new FakeCatalog(dotnet, java),
            orchestrator);

        var exitCode = await application.RunAsync([], interactive: true, default);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(BuildCommand.Publish, request.Command);
        Assert.Equal(["dotnet"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.Strict, request.Policy);
        Assert.Contains("revise", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_InteractiveAllExpandsBeforeStrictReadinessDecision()
    {
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var application = Application(
            new StringReader("build\nall\nstrict\ny\nbuild\ndotnet\nstrict\n"),
            new StringWriter(),
            new StringWriter(),
            new FakeCatalog(
                FakeAdapter.Ready("dotnet"),
                FakeAdapter.Unavailable("java", "JDK unavailable")),
            orchestrator);

        var exitCode = await application.RunAsync([], interactive: true, default);

        Assert.Equal(0, exitCode);
        Assert.Equal(["dotnet"], Assert.Single(orchestrator.Requests).Adapters);
    }

    [Fact]
    public async Task Run_NonInteractiveNeverPromptsAndUsesBuildDotNetStrictDefaults()
    {
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var output = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            new StringWriter(),
            new FakeCatalog(FakeAdapter.Ready("dotnet")),
            orchestrator);

        var exitCode = await application.RunAsync([], interactive: false, default);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(BuildCommand.Build, request.Command);
        Assert.Equal(["dotnet"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.Strict, request.Policy);
        Assert.DoesNotContain("Build or publish", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_HelpListsAutomationControlsWithoutRunningBuild()
    {
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var output = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            new StringWriter(),
            new FakeCatalog(FakeAdapter.Ready("dotnet")),
            orchestrator);

        var exitCode = await application.RunAsync(["--help"], interactive: false, default);

        Assert.Equal(0, exitCode);
        Assert.Empty(orchestrator.Requests);
        Assert.Contains("--adapter-policy", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--builds", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AutomationArgumentsBypassInteractivePrompts()
    {
        var orchestrator = new FakeOrchestrator(SuccessReport());
        var output = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            new StringWriter(),
            new FakeCatalog(FakeAdapter.Unavailable("java", "JDK 17+ was not found")),
            orchestrator);

        var exitCode = await application.RunAsync(
            ["publish", "--adapters", "java", "--adapter-policy", "best-effort"],
            interactive: true,
            default);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(BuildCommand.Publish, request.Command);
        Assert.Equal(["java"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.BestEffort, request.Policy);
        Assert.DoesNotContain("Build or publish", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_UnknownCatalogAdapterIsAnInvalidInvocation()
    {
        var error = new StringWriter();
        var catalog = new FakeCatalog(FakeAdapter.Ready("dotnet"));
        var application = Application(
            new ThrowingReader(),
            new StringWriter(),
            error,
            catalog,
            new BuildOrchestrator(
                catalog,
                new SuccessfulHostPublisher(),
                new BuildReportWriter(),
                new BuildRunWorkspaceFactory()));

        var exitCode = await application.RunAsync(["build", "--adapters", "ruby"], interactive: false, default);

        Assert.Equal(2, exitCode);
        Assert.Contains("UnknownAdapter", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_JsonWritesExactlyOneResultAndSendsWarningsToDiagnostics()
    {
        var report = new BuildReport(
            BuildOutcome.PartialSuccess,
            0,
            [
                new AdapterBuildResult("dotnet", AdapterBuildStatus.Built, []),
                new AdapterBuildResult("java", AdapterBuildStatus.Skipped, [], "JDK unavailable", ["JDK 17+ was not found"])
            ]);
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            error,
            new FakeCatalog(FakeAdapter.Ready("dotnet")),
            new FakeOrchestrator(report));

        var exitCode = await application.RunAsync(["build", "--format", "json"], interactive: false, default);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("partial-success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("built", document.RootElement.GetProperty("adapters")[0].GetProperty("status").GetString(), StringComparison.Ordinal);
        Assert.Contains("skipped", document.RootElement.GetProperty("adapters")[1].GetProperty("status").GetString(), StringComparison.Ordinal);
        Assert.Contains("JDK 17+ was not found", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("partial-success", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TextWarningsCanBeSuppressedWithoutSuppressingFinalSummary()
    {
        var report = new BuildReport(
            BuildOutcome.PartialSuccess,
            0,
            [new AdapterBuildResult("java", AdapterBuildStatus.Skipped, [], "JDK unavailable", ["JDK 17+ was not found"])]);
        var output = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            new StringWriter(),
            new FakeCatalog(FakeAdapter.Ready("dotnet")),
            new FakeOrchestrator(report));

        var exitCode = await application.RunAsync(
            ["build", "--format", "text", "--no-warnings"], interactive: false, default);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("JDK 17+ was not found", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("JDK unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("partial-success", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TextUsesHyphenatedStatusesAndOutcomes()
    {
        var report = new BuildReport(
            BuildOutcome.PartialSuccess,
            0,
            [
                new AdapterBuildResult("dotnet", AdapterBuildStatus.Built, []),
                new AdapterBuildResult("java", AdapterBuildStatus.NotRun, []),
                new AdapterBuildResult("python", AdapterBuildStatus.Cancelled, [])
            ]);
        var output = new StringWriter();
        var application = Application(
            new ThrowingReader(),
            output,
            new StringWriter(),
            new FakeCatalog(FakeAdapter.Ready("dotnet")),
            new FakeOrchestrator(report));

        await application.RunAsync(["build"], interactive: false, default);

        Assert.Contains("partial-success", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("not-run", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("cancelled", output.ToString(), StringComparison.Ordinal);
    }

    private static BuildConsoleApplication Application(
        TextReader input,
        TextWriter output,
        TextWriter error,
        IBuildAdapterCatalog catalog,
        IBuildOrchestrator orchestrator) =>
        new(new BuildCommandLineParser(), catalog, orchestrator, input, output, error);

    private static BuildReport SuccessReport() =>
        new(BuildOutcome.Success, 0, [new AdapterBuildResult("dotnet", AdapterBuildStatus.Built, [])]);

    private sealed class FakeCatalog(params FakeAdapter[] adapters) : IBuildAdapterCatalog
    {
        public IReadOnlyList<IBuildAdapter> Adapters => adapters;

        public IReadOnlyList<IBuildAdapter> ResolveSelection(IReadOnlyList<string> names)
        {
            if (names.Contains("all", StringComparer.OrdinalIgnoreCase)) return Adapters;
            return names.Select(name => Adapters.FirstOrDefault(adapter =>
                    StringComparer.OrdinalIgnoreCase.Equals(adapter.Definition.Id, name) ||
                    adapter.Definition.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
                ?? throw new ConfigurationException("UnknownAdapter", $"Unknown adapter '{name}'."))
                .Distinct()
                .ToArray();
        }
    }

    private sealed class SuccessfulHostPublisher : IHostPublisher
    {
        public ValueTask<HostPublishResult> PublishAsync(
            HostPublishRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new HostPublishResult(true));
    }

    private sealed class FakeOrchestrator(BuildReport report) : IBuildOrchestrator
    {
        public List<BuildRequest> Requests { get; } = [];

        public ValueTask<BuildReport> RunAsync(BuildRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(report);
        }
    }

    private sealed class FakeAdapter : IBuildAdapter
    {
        private readonly AdapterReadiness _readiness;

        private FakeAdapter(AdapterReadiness readiness)
        {
            _readiness = readiness;
            Definition = new AdapterBuildDefinition(readiness.AdapterId, [], "1.0", []);
        }

        public AdapterBuildDefinition Definition { get; }
        public int PreflightCalls { get; private set; }

        public static FakeAdapter Ready(string id) => new(new AdapterReadiness(id, AdapterReadinessStatus.Ready));

        public static FakeAdapter Unavailable(string id, string reason) =>
            new(new AdapterReadiness(id, AdapterReadinessStatus.Unavailable, reason));

        public ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            return ValueTask.FromResult(_readiness);
        }

        public ValueTask<AdapterBuildResult> BuildAsync(AdapterBuildRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AdapterBuildResult(Definition.Id, AdapterBuildStatus.Built, []));
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("Interactive input was unexpectedly requested.");
    }
}
