using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Adapters.DotNet;

/// <summary>
/// Deep .NET work behind the four capability-specific seams.  It deliberately owns
/// process orchestration and disposable snapshot materialisation so callers cannot
/// accidentally substitute a static mapping for runtime evidence.
/// </summary>
public sealed class DotNetDeepOperations(IProcessRunner runner, string observerAssemblyPath, string dotnetPath = "dotnet") : IBuildPreparer, ITestDiscoverer, ISelectedTestExecutor, ITestObserver
{
    public const string AdapterVersion = "0.2.0";
    public const string ObserverVersion = "0.1.0";
    private const int DiagnosticLimit = 8_192;
    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly string _dotnetPath = string.IsNullOrWhiteSpace(dotnetPath) ? throw new ArgumentException("A dotnet path is required.", nameof(dotnetPath)) : dotnetPath;
    private readonly string _observerAssemblyPath = string.IsNullOrWhiteSpace(observerAssemblyPath)
            ? throw new ArgumentException("An observer assembly path is required.", nameof(observerAssemblyPath))
            : Path.GetFullPath(observerAssemblyPath);

    public bool IsConfigured => File.Exists(_observerAssemblyPath);

    public async ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConfigured) throw Unavailable("Build preparation requires a configured observer artifact.");
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var solution = ResolveSolution(request.Context.Snapshot, request.Context.ConfiguredSolution);
        var version = await DotNetVersionAsync(workspace.Root, cancellationToken).ConfigureAwait(false);
        var targetFrameworks = TargetFrameworks(request.Context.Snapshot, solution);
        if (request.NoBuild)
        {
            var manifest = ManifestPath(request.Context, solution);
            var persisted = ReadManifest(manifest);
            var candidate = await CreateFingerprintAsync(request.Context, workspace.Root, solution, version, targetFrameworks, cancellationToken).ConfigureAwait(false);
            if (persisted is null || !StringComparer.Ordinal.Equals(persisted.Value, candidate.Value))
            {
                throw new AnalysisException("ArtifactsUnavailable", "No compatible build fingerprint is available for --no-build.");
            }

            return new BuildPreparationResult(candidate, []);
        }

        var build = await RunAsync(workspace.Root, ["build", solution, "--configuration", request.Context.Configuration, "--nologo"], null, cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            throw new AnalysisException("BuildFailed", $"dotnet build failed: {Diagnostic(build)}");
        }

        var fingerprint = await CreateFingerprintAsync(request.Context, workspace.Root, solution, version, targetFrameworks, cancellationToken).ConfigureAwait(false);
        WriteManifest(ManifestPath(request.Context, solution), fingerprint);
        return new BuildPreparationResult(fingerprint, []);
    }

    public async ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fingerprint);
        await using var workspace = await Workspace.CreateAsync(context, cancellationToken).ConfigureAwait(false);
        var projects = TestProjects(context.Snapshot, ResolveSolution(context.Snapshot, context.ConfiguredSolution));
        var tests = new List<TestCatalogEntry>();
        var warnings = new List<string>();
        foreach (var project in projects)
        {
            var result = await RunAsync(workspace.Root, ["test", project, "--list-tests", "--no-build", "--configuration", context.Configuration, "--nologo"], null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                warnings.Add($"{project}: test discovery failed: {Diagnostic(result)}");
                continue;
            }

            foreach (var name in ParseListedTests(result.StandardOutput))
            {
                var (Identity, RunnerOnly) = NormalizeRunnerIdentity(project, name);
                if (RunnerOnly) warnings.Add($"{project}: runner identity '{name}' could not be matched to a static test case.");
                tests.Add(new TestCatalogEntry(Identity, name, DetectFramework(name), project, name));
            }
        }

        return new DiscoveryCatalog(fingerprint, [.. tests.OrderBy(test => test.Identity, StringComparer.Ordinal)], [.. warnings.OrderBy(warning => warning, StringComparer.Ordinal)]);
    }

    public async ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var results = new List<TestExecutionResult>();
        foreach (var test in request.Tests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteOneAsync(workspace.Root, request.Context, test, request.Timeout, null, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConfigured) throw Unavailable("Observation requires a configured startup-hook observer.");
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var runId = request.RunId ?? Guid.NewGuid().ToString("N");
        var scopes = new List<ObservationScope>();
        foreach (var test in request.Tests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Path.Combine(TemporaryStateDirectory(request.Context), $"observation-{Guid.NewGuid():N}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            try
            {
                var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["DOTNET_STARTUP_HOOKS"] = _observerAssemblyPath,
                    ["MERKLE_OBSERVATION_FILE"] = output
                };
                var execution = await ExecuteOneAsync(workspace.Root, request.Context, test, request.Timeout, environment, cancellationToken).ConfigureAwait(false);
                var loaded = ReadObservedAssemblies(output);
                var observations = ToObservations(test.Identity, loaded, request.Fingerprint, runId);
                var complete = File.Exists(output) && observations.Count != 0;
                var warnings = complete
                    ? Array.Empty<string>()
                    : ["ObservationIncomplete: the startup hook did not report a repository output assembly; this scope was not admitted as dynamic evidence."];
                scopes.Add(new ObservationScope(test.Identity, complete ? ObservationCompleteness.Complete : ObservationCompleteness.Incomplete, observations, execution, warnings));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && request.Timeout.HasValue)
            {
                scopes.Add(new ObservationScope(test.Identity, ObservationCompleteness.Incomplete, [], new TestExecutionResult(test.Identity, TestOutcome.TimedOut, request.Timeout, "The explicit per-test timeout expired."), ["ObservationIncomplete: execution timed out before a complete scope could be observed."]));
            }
        }

        return scopes;
    }

    private async ValueTask<TestExecutionResult> ExecuteOneAsync(string root, DeepAdapterContext context, TestCatalogEntry test, TimeSpan? timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var resultsDirectory = Path.Combine(TemporaryStateDirectory(context), $"trx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultsDirectory);
        var trxName = "merkle.trx";
        using var deadline = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await RunAsync(root, ["test", test.ProjectPath, "--no-build", "--configuration", context.Configuration, "--filter", $"FullyQualifiedName={test.Selector}", "--logger", $"trx;LogFileName={trxName}", "--results-directory", resultsDirectory, "--nologo"], environment, linked?.Token ?? cancellationToken).ConfigureAwait(false);
            var trx = Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            var parsed = trx is null ? null : ParseTrx(trx, test.Identity);
            if (parsed is not null) return parsed;
            return new TestExecutionResult(test.Identity, result.ExitCode == 0 ? TestOutcome.Passed : TestOutcome.Crashed, null, Diagnostic(result));
        }
        catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            return new TestExecutionResult(test.Identity, TestOutcome.TimedOut, timeout, "The explicit per-test timeout expired.");
        }
    }

    private static async ValueTask<BuildFingerprint> CreateFingerprintAsync(DeepAdapterContext context, string root, string solution, string version, IReadOnlyList<string> tfms, CancellationToken cancellationToken)
    {
        var artifacts = new List<BuildArtifact>();
        foreach (var project in ProjectPaths(context.Snapshot, solution))
        {
            var model = Project(context.Snapshot, project);
            foreach (var tfm in TargetFrameworks(model))
            {
                var name = AssemblyName(model, project);
                var assembly = Path.Combine(root, Path.GetDirectoryName(project) ?? string.Empty, "bin", context.Configuration, tfm, name + ".dll");
                if (!File.Exists(assembly)) continue;
                var pdb = Path.ChangeExtension(assembly, ".pdb");
                artifacts.Add(new BuildArtifact(project, assembly, File.Exists(pdb) ? pdb : null, HashFile(assembly), File.Exists(pdb) ? HashFile(pdb) : null));
            }
        }
        var canonical = string.Join("\n", new[] { context.Snapshot.Identity.Value, solution, context.Configuration, context.Platform, version, AdapterVersion, ObserverVersion }
            .Concat(tfms.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(artifacts.OrderBy(value => value.ProjectPath, StringComparer.Ordinal).Select(value => $"{value.ProjectPath}|{value.AssemblyHash}|{value.PdbHash}")));
        return new BuildFingerprint(Hash(Encoding.UTF8.GetBytes(canonical)), context.Snapshot.Identity.Value, solution, context.Configuration, context.Platform, version, tfms, AdapterVersion, ObserverVersion, [.. artifacts.OrderBy(value => value.ProjectPath, StringComparer.Ordinal)]);
    }

    private async ValueTask<string> DotNetVersionAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, ["--version"], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new AnalysisException("DotNetUnavailable", $"The system dotnet resolver failed: {Diagnostic(result)}");
        return result.StandardOutput.Trim();
    }

    private ValueTask<ProcessResult> RunAsync(string root, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        _runner.RunAsync(new ProcessRequest(_dotnetPath, arguments, root, environment, MaxStandardOutputBytes: 4 * 1024 * 1024, MaxStandardErrorBytes: DiagnosticLimit), cancellationToken);

    private static List<DynamicObservation> ToObservations(string testIdentity, IReadOnlyList<string> loaded, BuildFingerprint fingerprint, string runId)
    {
        var result = new List<DynamicObservation>();
        foreach (var artifact in fingerprint.Artifacts)
        {
            if (!loaded.Any(path => MatchesArtifact(path, artifact))) continue;
            result.Add(new DynamicObservation(testIdentity, $"dotnet:project:{artifact.ProjectPath}", fingerprint.Value, AdapterVersion, ObserverVersion, runId, "assembly", "Assembly-load observation only; it cannot observe members, reflection-only resolution, native code, child processes, or assemblies loaded before the hook."));
        }
        return [.. result.OrderBy(value => value.UnitIdentity, StringComparer.Ordinal)];
    }

    private static bool MatchesArtifact(string loadedPath, BuildArtifact artifact)
    {
        try
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(loadedPath),
                    Path.GetFullPath(artifact.AssemblyPath)))
            {
                return true;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(
                       Path.GetFileName(loadedPath),
                       Path.GetFileName(artifact.AssemblyPath)) &&
                   File.Exists(loadedPath) &&
                   StringComparer.Ordinal.Equals(HashFile(loadedPath), artifact.AssemblyHash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static TestExecutionResult? ParseTrx(string path, string identity)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var result = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "UnitTestResult");
        if (result is null) return null;
        var value = ((string?)result.Attribute("outcome") ?? "Failed").ToLowerInvariant();
        var outcome = value switch { "passed" => TestOutcome.Passed, "failed" => TestOutcome.Failed, "notexecuted" or "inconclusive" => TestOutcome.Skipped, "timeout" => TestOutcome.TimedOut, "aborted" => TestOutcome.Cancelled, _ => TestOutcome.Crashed };
        TimeSpan? duration = TimeSpan.TryParse(
            (string?)result.Attribute("duration"),
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
        var diagnostic = string.Join("\n", result.Descendants().Where(element => element.Name.LocalName is "Message" or "StackTrace").Select(element => element.Value)).Trim();
        return new TestExecutionResult(identity, outcome, duration, diagnostic.Length == 0 ? null : diagnostic[..Math.Min(diagnostic.Length, DiagnosticLimit)]);
    }

    private static IReadOnlyList<string> ParseListedTests(string stdout)
    {
        var lines = stdout.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var header = Array.FindIndex(lines, line => line.Contains("Tests are available", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return [];
        return [.. lines[(header + 1)..].Select(line => line.Trim()).Where(line => line.Length != 0 && !line.StartsWith("Total tests:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Test Run", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal)];
    }

    private static (string Identity, bool RunnerOnly) NormalizeRunnerIdentity(string project, string runner)
    {
        var method = runner.Split('(', 2)[0];
        var lastDot = method.LastIndexOf('.');
        if (lastDot > 0 && !runner.Contains('(', StringComparison.Ordinal))
        {
            var type = method[..lastDot];
            var name = method[(lastDot + 1)..];
            return ($"dotnet:test:v1:{project}:dotnet:type:{project}:{type}:{name}():method", false);
        }
        return ($"dotnet:runner-test:v1:{project}:{Hash(Encoding.UTF8.GetBytes(runner))[..24]}", true);
    }

    private static string DetectFramework(string name) => name.Contains('[', StringComparison.Ordinal) ? "runner" : "dotnet";
    private static IReadOnlyList<string> ReadObservedAssemblies(string path) => !File.Exists(path) ? [] : File.ReadLines(path).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string Diagnostic(ProcessResult result) => Diagnostic(result.StandardError.Length == 0 ? result.StandardOutput : result.StandardError);
    private static string Diagnostic(string value) => value.Length <= DiagnosticLimit ? value : value[..DiagnosticLimit];
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static CapabilityException Unavailable(string detail) => new("DeepToolchainUnavailable", $"Function not available for: dotnet. {detail}");

    private static string TemporaryStateDirectory(DeepAdapterContext context) => Path.GetFullPath(context.StateDirectory ?? Path.Combine(Path.GetTempPath(), "merkle-state"));
    private static string ManifestPath(DeepAdapterContext context, string solution) => Path.Combine(TemporaryStateDirectory(context), "fingerprints", Hash(Encoding.UTF8.GetBytes(solution + "\0" + context.Snapshot.Identity.Value)) + ".manifest");
    private static void WriteManifest(string path, BuildFingerprint fingerprint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, fingerprint.Value, Encoding.UTF8);
    }
    private static BuildFingerprint? ReadManifest(string path) => File.Exists(path) ? new BuildFingerprint(File.ReadAllText(path, Encoding.UTF8).Trim(), "", "", "", "", "", [], "", "", []) : null;

    private static string ResolveSolution(RepositorySnapshot snapshot, string? configured)
    {
        var solutions = snapshot.Files.Where(file => Path.GetExtension(file.Path) is ".sln" or ".slnx").Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = configured.Replace('\\', '/');
            if (solutions.Contains(normalized, StringComparer.Ordinal)) return normalized;
            throw new ConfigurationException("ConfiguredSolutionNotFound", $"The configured solution '{normalized}' is not present in the candidate snapshot.");
        }
        return solutions.Length switch { 0 => throw new ConfigurationException("SolutionNotFound", "No .NET solution was found. Configure one solution explicitly."), 1 => solutions[0], _ => throw new ConfigurationException("MultipleSolutions", $"Multiple .NET solutions were found; configure one explicitly: {string.Join(", ", solutions)}.") };
    }

    private static IReadOnlyList<string> ProjectPaths(RepositorySnapshot snapshot, string solution)
    {
        var file = snapshot.Files.Single(value => value.Path == solution);
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new MemoryStream(file.Content.ToArray(), false);
            return [.. XDocument.Load(stream).Descendants().Where(value => value.Name.LocalName == "Project").Select(value => (string?)value.Attribute("Path")).Where(value => value is not null && IsProject(value!)).Select(value => Relative(solution, value!)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)];
        }
        return [.. Encoding.UTF8.GetString(file.Content.Span).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => line.TrimStart().StartsWith("Project(", StringComparison.Ordinal)).Select(line => line.Split(',').ElementAtOrDefault(1)?.Trim().Trim('"')).Where(value => value is not null && IsProject(value!)).Select(value => Relative(solution, value!)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)];
    }
    private static IReadOnlyList<string> TestProjects(RepositorySnapshot snapshot, string solution) => [.. ProjectPaths(snapshot, solution).Where(path => IsTestProject(Project(snapshot, path)))];
    private static SnapshotFile Project(RepositorySnapshot snapshot, string path) => snapshot.Files.Single(file => StringComparer.Ordinal.Equals(file.Path, path));
    private static bool IsProject(string path) => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj";
    private static string Relative(string solution, string path)
    {
        var normalized = Path.GetFullPath(Path.Combine("/", Path.GetDirectoryName(solution) ?? string.Empty, path))
            .Replace('\\', '/')
            .TrimStart('/');
        if (normalized.StartsWith("../", StringComparison.Ordinal) || normalized == "..")
        {
            throw new AnalysisException("ProjectReferenceOutsideRepository", $"Solution project '{path}' is outside the snapshot.");
        }
        return normalized;
    }
    private static IReadOnlyList<string> TargetFrameworks(RepositorySnapshot snapshot, string solution) => [.. ProjectPaths(snapshot, solution).SelectMany(path => TargetFrameworks(Project(snapshot, path))).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)];
    private static IReadOnlyList<string> TargetFrameworks(SnapshotFile project)
    {
        using var stream = new MemoryStream(project.Content.ToArray(), false); var document = XDocument.Load(stream);
        var values = document.Descendants().Where(value => value.Name.LocalName is "TargetFramework" or "TargetFrameworks").Select(value => value.Value).SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? ["unknown"] : values;
    }
    private static string AssemblyName(SnapshotFile project, string path)
    {
        using var stream = new MemoryStream(project.Content.ToArray(), false); var document = XDocument.Load(stream);
        return document.Descendants().FirstOrDefault(value => value.Name.LocalName == "AssemblyName")?.Value.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(path);
    }
    private static bool IsTestProject(SnapshotFile project)
    {
        using var stream = new MemoryStream(project.Content.ToArray(), false); var document = XDocument.Load(stream);
        return document.Descendants().Any(value => value.Name.LocalName == "IsTestProject" && bool.TryParse(value.Value.Trim(), out var yes) && yes) || document.Descendants().Where(value => value.Name.LocalName == "PackageReference").Select(value => (string?)value.Attribute("Include")).Any(value => value?.Contains("test", StringComparison.OrdinalIgnoreCase) == true || value?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true || value?.Contains("nunit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class Workspace : IAsyncDisposable
    {
        private Workspace(string root, bool disposable) { Root = root; _disposable = disposable; }
        private readonly bool _disposable;
        public string Root { get; }
        public static async ValueTask<Workspace> CreateAsync(DeepAdapterContext context, CancellationToken cancellationToken)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(context.Snapshot.Identity.Reference, "WORKTREE")) return new Workspace(context.Snapshot.RepositoryRoot, false);
            var state = TemporaryStateDirectory(context);
            Directory.CreateDirectory(state);
            var key = Hash(Encoding.UTF8.GetBytes(context.Snapshot.Identity.Value));
            var root = Path.Combine(state, "workspaces", key);
            var ready = Path.Combine(root, ".merkle-workspace");
            if (File.Exists(ready)) return new Workspace(root, false);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            try
            {
                foreach (var file in context.Snapshot.Files.OrderBy(value => value.Path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Kind == SnapshotEntryKind.GitLink) throw new AnalysisException("GitLinkUnavailable", $"Snapshot contains gitlink '{file.Path}', which cannot be materialized safely.");
                    var path = SafePath(root, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (file.Kind == SnapshotEntryKind.SymbolicLink)
                    {
                        var target = Encoding.UTF8.GetString(file.Content.Span);
                        if (Path.IsPathRooted(target) || target.Split('/', '\\').Contains("..", StringComparer.Ordinal)) throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot symlink '{file.Path}' has an unsafe target.");
                        File.CreateSymbolicLink(path, target);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(path, file.Content.ToArray(), cancellationToken).ConfigureAwait(false);
                        if (file.Kind == SnapshotEntryKind.ExecutableFile && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
                    }
                }
                await File.WriteAllTextAsync(ready, context.Snapshot.Identity.Value, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return new Workspace(root, false);
            }
            catch { Directory.Delete(root, true); throw; }
        }
        public ValueTask DisposeAsync() { if (_disposable && Directory.Exists(Root)) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
        private static string SafePath(string root, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal)) throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot path '{relative}' is unsafe.");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot path '{relative}' escapes its workspace.");
            return path;
        }
    }
}
