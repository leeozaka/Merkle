using System.Security.Cryptography;
using System.Text;
using Merkle.Adapters.DotNet;
using Merkle.Adapters.Go;
using Merkle.Cli;
using Merkle.Core.Adapters;
using Merkle.Core.Configuration;
using Merkle.Core.Engine;
using Merkle.Core.History;
using Merkle.Core.State;
using Merkle.Core.Reporting;
using Merkle.Infrastructure.Processes;
using Merkle.Infrastructure.Snapshots;
using Merkle.Infrastructure.State;

var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
var configuration = new MerkleConfigurationLoader().Load(repositoryRoot);
var repositoryIdentitySource = configuration.Repository.RepositoryId ?? repositoryRoot;
var repositoryIdentity = "repository:" + Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(repositoryIdentitySource))).ToLowerInvariant();
var redactor = new SecretRedactor(configuration.Security.RedactionPatterns);
var localStateStore = new LocalStateStore(
    repositoryRoot,
    configuration.Repository.StateDirectory,
    repositoryIdentity,
    redactor: redactor);
IStateStore stateStore = localStateStore;
if (configuration.History.Provider == "remote")
{
    var remote = new HttpRemoteStateStore(
        new HttpClient(),
        new EnvironmentRemoteStateTokenSource(configuration.History.TokenEnvironment!),
        new HttpRemoteStateStoreOptions(
            new Uri(configuration.History.Endpoint!),
            allowAnonymousReads: true));
    stateStore = new RemoteBackedStateStore(localStateStore, remote);
}
var processRunner = new ProcessRunner();
var snapshotSource = new GitSnapshotSource(
    repositoryRoot,
    processRunner,
    repositoryIdentity: repositoryIdentity);
var workerPath = FindArtifact(
    repositoryRoot,
    "MERKLE_DOTNET_WORKER",
    "Merkle.Adapters.DotNet.Worker.dll",
    "Merkle.Adapters.DotNet.Worker");
var observerPath = FindArtifact(
    repositoryRoot,
    "MERKLE_DOTNET_OBSERVER",
    "Merkle.Adapters.DotNet.Observer.dll",
    "Merkle.Adapters.DotNet.Observer");
IDotNetAnalysisWorker? analysisWorker = workerPath is null
    ? null
    : new DotNetProcessAnalysisWorker(processRunner, workerPath);
DotNetDeepOperations? deepOperations = observerPath is null
    ? null
    : new DotNetDeepOperations(processRunner, observerPath);
var adapter = new DotNetAdapter(analysisWorker, deepOperations);

var pythonAdapterPath = FindArtifact(
    repositoryRoot,
    "MERKLE_PYTHON_ADAPTER",
    "merkle-adapter-python.pyz",
    "adapters/python");
ILanguageAdapter? pythonAdapter = null;
if (pythonAdapterPath is not null)
{
    var isPyz = pythonAdapterPath.EndsWith(".pyz", StringComparison.OrdinalIgnoreCase);
    var executable = isPyz ? "python3" : pythonAdapterPath;
    var arguments = isPyz ? new[] { pythonAdapterPath } : Array.Empty<string>();
    pythonAdapter = new ProcessLanguageAdapter(
        processRunner,
        new ProcessLanguageAdapterOptions(executable, arguments, repositoryRoot));
}

var javaAdapterPath = FindArtifact(
    repositoryRoot,
    "MERKLE_JAVA_ADAPTER",
    "merkle-adapter-java.jar",
    "adapters/java/target");
ILanguageAdapter? javaAdapter = null;
if (javaAdapterPath is not null)
{
    var isJar = javaAdapterPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    var executable = isJar ? "java" : javaAdapterPath;
    var arguments = isJar ? new[] { "-jar", javaAdapterPath } : Array.Empty<string>();
    javaAdapter = new ProcessLanguageAdapter(
        processRunner,
        new ProcessLanguageAdapterOptions(executable, arguments, repositoryRoot));
}

var adapters = new List<ILanguageAdapter> { adapter };
if (pythonAdapter is not null)
{
    adapters.Add(pythonAdapter);
}

if (javaAdapter is not null)
{
    adapters.Add(javaAdapter);
}

var goAdapterPath = FindArtifact(
    repositoryRoot,
    "MERKLE_GO_ADAPTER",
    "merkle-adapter-go",
    "adapters/go/worker");
if (goAdapterPath is not null)
{
    var goAnalysisAdapter = new ProcessLanguageAdapter(
        processRunner,
        new ProcessLanguageAdapterOptions(goAdapterPath, [], repositoryRoot));
    adapters.Add(new GoAdapter(goAnalysisAdapter, new GoDeepOperations(processRunner)));
}

var adapterRegistry = new AdapterRegistry(adapters);
var engine = new ImpactEngine(
    snapshotSource,
    LanguageDetector.CreateDefault(),
    adapterRegistry,
    stateStore,
    TimeProvider.System,
    repositoryIdentity,
    redactor: redactor);
IDeepExecutionEngine deepEngine = new DeepExecutionEngine(
    engine,
    snapshotSource,
    adapterRegistry,
    stateStore,
    TimeProvider.System,
    redactor);
var application = new CliApplication(
    engine,
    stateStore,
    Console.Out,
    Console.Error,
    configuration,
    deepEngine,
    new HistoryImportService(stateStore, repositoryIdentity, TimeProvider.System),
    localStateStore.StatePath,
    redactor);
return await application.RunAsync(args, CancellationToken.None);

static string FindRepositoryRoot(string start)
{
    DirectoryInfo? directory = new(Path.GetFullPath(start));
    while (directory is not null)
    {
        var gitPath = Path.Combine(directory.FullName, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Path.GetFullPath(start);
}

static string? FindArtifact(string repositoryRoot, string environmentVariable, string fileName, string projectName)
{
    var configured = Environment.GetEnvironmentVariable(environmentVariable);
    var candidates = new[]
    {
        configured,
        Path.Combine(AppContext.BaseDirectory, "workers", "dotnet", fileName),
        Path.Combine(AppContext.BaseDirectory, "workers", "go", fileName),
        Path.Combine(AppContext.BaseDirectory, fileName),
        Path.Combine(repositoryRoot, "src", "adapters", "python", fileName),
        Path.Combine(repositoryRoot, "src", "adapters", "java", "target", fileName),
        Path.Combine(repositoryRoot, "src", "adapters", "go", "worker", fileName),
        Path.Combine(repositoryRoot, "src", projectName, "bin", "Debug", projectName.EndsWith("Observer", StringComparison.Ordinal) ? "net8.0" : "net10.0", fileName),
        Path.Combine(repositoryRoot, "src", projectName, "bin", "Release", projectName.EndsWith("Observer", StringComparison.Ordinal) ? "net8.0" : "net10.0", fileName)
    };
    return candidates
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path!))
        .FirstOrDefault(File.Exists);
}
