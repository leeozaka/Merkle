using Merkle.Build;
using Merkle.Infrastructure.Processes;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var processRunner = new ProcessRunner();
var catalog = new AdapterBuildCatalog(processRunner);
var orchestrator = new BuildOrchestrator(
    catalog,
    new MerkleHostPublisher(processRunner),
    new BuildReportWriter(),
    new BuildRunWorkspaceFactory());
var application = new BuildConsoleApplication(
    new BuildCommandLineParser(),
    catalog,
    orchestrator,
    Console.In,
    Console.Out,
    Console.Error);
var interactive = args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected;

try
{
    return await application.RunAsync(args, interactive, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Build cancelled.");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Build failed: {error.Message}");
    return 4;
}
