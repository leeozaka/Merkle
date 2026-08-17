using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Adapters.Go;

/// <summary>Process-backed Go build, discovery, execution, and file observation.</summary>
public sealed class GoDeepOperations : IBuildPreparer, ITestDiscoverer, ISelectedTestResolver, ISelectedTestExecutor, ITestObserver
{
    public const string AdapterVersion = "0.1.0";
    public const string ObserverVersion = "0.1.0";

    private const int DiagnosticLimit = 8 * 1024;
    private const int OutputLimit = 4 * 1024 * 1024;
    private const string BlindSpots = "Observation is file-level. Blind spots include runtime-only subtests as separate identities, standard-library coverage, interface dispatch/reflection, plugins, subprocesses, cgo/native code, generated code, build-tag-dependent variants, and uninstrumented packages.";
    private static readonly Regex VersionPattern = new(@"\bgo(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CoverPattern = new(@"^(?<path>.+):\d+\.\d+,\d+\.\d+\s+\d+\s+(?<count>\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _runner;
    private readonly string _goPath;

    public GoDeepOperations(IProcessRunner runner, string goPath = "go")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _goPath = string.IsNullOrWhiteSpace(goPath) ? throw new ArgumentException("A go path is required.", nameof(goPath)) : goPath;
    }

    public bool IsConfigured => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !string.IsNullOrWhiteSpace(_goPath);

    public async ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var scope = ResolveScope(request.Context.Snapshot, request.Context.ConfiguredSolution);
        var goVersion = await GoVersionAsync(workspace.Root, scope, cancellationToken).ConfigureAwait(false);
        var packages = await ListPackagesAsync(workspace.Root, scope, cancellationToken, throwOnFailure: true).ConfigureAwait(false);
        var state = StateDirectory(request.Context);
        var manifestPath = ManifestPath(request.Context, scope);

        if (request.NoBuild)
        {
            try
            {
                var persisted = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), GoJsonContext.Default.GoManifest);
                if (persisted is null || !ManifestMatches(persisted, request.Context, scope, goVersion, packages, workspace.Root))
                    throw new InvalidDataException();
                foreach (var artifact in persisted.Fingerprint.Artifacts)
                {
                    if (!File.Exists(artifact.ArtifactPath) || !StringComparer.Ordinal.Equals(HashFile(artifact.ArtifactPath), artifact.ArtifactHash))
                        throw new InvalidDataException();
                }

                return new BuildPreparationResult(persisted.Fingerprint, []);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                throw new AnalysisException("ArtifactsUnavailable", "No compatible Go build manifest and artifacts are available for --no-build.", error);
            }
        }

        var artifacts = new List<BuildArtifact>();
        foreach (var package in packages.Where(value => value.HasTests).OrderBy(value => value.ImportPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifactPath = Path.Combine(state, "artifacts", Hash(Encoding.UTF8.GetBytes(request.Context.Snapshot.Identity.Value + "\0" + package.ImportPath)) + ".test");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            var coverPackages = string.Join(',', packages
                .Where(value => ReferenceEquals(value.Module, package.Module))
                .Select(value => value.ImportPath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
            var buildArguments = new List<string> { "test", "-mod=readonly", "-cover", "-covermode=set" };
            if (coverPackages.Length != 0) buildArguments.Add($"-coverpkg={coverPackages}");
            buildArguments.AddRange(["-c", "-o", artifactPath, package.ImportPath]);
            var result = await RunGoAsync(
                workspace.Root,
                scope,
                buildArguments,
                cancellationToken,
                package.Module.RootPath).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new AnalysisException("BuildFailed", $"go test -c failed for '{package.ImportPath}': {Diagnostic(result)}");
            if (!File.Exists(artifactPath))
                throw new AnalysisException("BuildFailed", $"go test -c did not produce an artifact for '{package.ImportPath}'.");
            artifacts.Add(new BuildArtifact(package.ImportPath, artifactPath, null, HashFile(artifactPath), null));
        }

        var fingerprint = CreateFingerprint(request.Context, scope, goVersion, packages, artifacts, workspace.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = new GoManifest(fingerprint, PackageManifest(packages), ModuleManifest(scope, workspace.Root));
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, GoJsonContext.Default.GoManifest), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new BuildPreparationResult(fingerprint, []);
    }

    public async ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fingerprint);
        EnsureConfigured();
        await using var workspace = await Workspace.CreateAsync(context, cancellationToken).ConfigureAwait(false);
        var scope = ResolveScope(context.Snapshot, context.ConfiguredSolution);
        var packages = await ListPackagesAsync(workspace.Root, scope, cancellationToken, throwOnFailure: false).ConfigureAwait(false);
        var tests = new List<TestCatalogEntry>();
        var warnings = new List<string>();
        foreach (var package in packages.Where(value => value.HasTests).OrderBy(value => value.ImportPath, StringComparer.Ordinal))
        {
            foreach (var name in await ListTestsAsync(workspace.Root, scope, package, cancellationToken).ConfigureAwait(false))
            {
                tests.Add(new TestCatalogEntry(
                    $"golang:{package.ImportPath}:{name}",
                    name,
                    "go-testing",
                    package.ImportPath,
                    name));
            }
        }

        if (tests.Count == 0) warnings.Add("No Go tests were discovered.");
        return new DiscoveryCatalog(
            fingerprint,
            [.. tests.OrderBy(value => value.Identity, StringComparer.Ordinal)],
            [.. warnings.OrderBy(value => value, StringComparer.Ordinal)]);
    }

    public SelectedTestResolution ResolveSelectedTests(IReadOnlyList<SelectedTestReference> selectedTests, IReadOnlyList<TestCatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(selectedTests);
        ArgumentNullException.ThrowIfNull(catalog);
        var exact = catalog.ToDictionary(value => value.Identity, StringComparer.Ordinal);
        var resolved = new Dictionary<string, TestCatalogEntry>(StringComparer.Ordinal);
        var unresolved = new List<SelectedTestReference>();
        foreach (var selected in selectedTests)
        {
            if (exact.TryGetValue(selected.Identity, out var test)) resolved[test.Identity] = test;
            else unresolved.Add(selected);
        }

        return new SelectedTestResolution(
            [.. resolved.Values.OrderBy(value => value.Identity, StringComparer.Ordinal)],
            [.. unresolved.OrderBy(value => value.Identity, StringComparer.Ordinal)]);
    }

    public async ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var scope = ResolveScope(request.Context.Snapshot, request.Context.ConfiguredSolution);
        var results = new List<TestExecutionResult>();
        foreach (var test in request.Tests.OrderBy(value => value.Identity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteOneAsync(workspace.Root, scope, request.Fingerprint, test, request.Timeout, null, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConfigured();
        await using var workspace = await Workspace.CreateAsync(request.Context, cancellationToken).ConfigureAwait(false);
        var scope = ResolveScope(request.Context.Snapshot, request.Context.ConfiguredSolution);
        var runId = request.RunId ?? Guid.NewGuid().ToString("N");
        var outputRoot = Path.Combine(StateDirectory(request.Context), "observations");
        Directory.CreateDirectory(outputRoot);
        var scopes = new List<ObservationScope>();
        foreach (var test in request.Tests.OrderBy(value => value.Identity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = Path.Combine(outputRoot, Hash(Encoding.UTF8.GetBytes(runId + "\0" + test.Identity)) + ".cov");
            try
            {
                var artifact = ArtifactFor(request.Fingerprint, test);
                var arguments = ExecutionArguments(test, artifact, profile);
                var execution = await ExecuteOneAsync(workspace.Root, scope, request.Fingerprint, test, request.Timeout, arguments, cancellationToken).ConfigureAwait(false);
                var observations = ReadObservations(profile, workspace.Root, scope.Modules, test.Identity, request.Fingerprint, runId);
                var complete = observations.Count != 0 && File.Exists(profile) && IsValidProfile(profile);
                IReadOnlyList<string> warnings = complete
                    ? [BlindSpots]
                    : [$"ObservationIncomplete: no valid nonempty attributable cover profile was produced; evidence was not admitted. {BlindSpots}"];
                scopes.Add(new ObservationScope(test.Identity, complete ? ObservationCompleteness.Complete : ObservationCompleteness.Incomplete, complete ? observations : [], execution, warnings));
            }
            catch (OperationCanceledException) when (request.Timeout.HasValue && !cancellationToken.IsCancellationRequested)
            {
                scopes.Add(new ObservationScope(
                    test.Identity,
                    ObservationCompleteness.Incomplete,
                    [],
                    new TestExecutionResult(test.Identity, TestOutcome.TimedOut, request.Timeout, "The explicit per-test timeout expired."),
                    [$"ObservationIncomplete: execution timed out before a complete scope could be observed. {BlindSpots}"]));
            }
        }

        return scopes;
    }

    private async ValueTask<TestExecutionResult> ExecuteOneAsync(
        string root,
        GoScope scope,
        BuildFingerprint fingerprint,
        TestCatalogEntry test,
        TimeSpan? timeout,
        IReadOnlyList<string>? observationArguments,
        CancellationToken cancellationToken)
    {
        var module = ModuleFor(scope, test.ExecutionScope);
        var artifact = ArtifactFor(fingerprint, test);
        using var timeoutSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        var arguments = observationArguments ?? ExecutionArguments(test, artifact, null);
        try
        {
            var result = await RunGoAsync(root, scope, arguments, token, module.RootPath).ConfigureAwait(false);
            var parsed = ParseExecution(result.StandardOutput, test.Identity, test.Selector);
            if (parsed is not null) return parsed;
            if (result.ExitCode != 0)
                return new TestExecutionResult(
                    test.Identity,
                    TestOutcome.Crashed,
                    null,
                    $"The Go test artifact exited before reporting the selected test: {Diagnostic(result)}");
            throw new AnalysisException("TestResultUnavailable", $"The Go test artifact did not report selected test '{test.Identity}'.");
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            return new TestExecutionResult(test.Identity, TestOutcome.TimedOut, timeout, "The explicit per-test timeout expired.");
        }
    }

    private static TestExecutionResult? ParseExecution(string output, string identity, string selector)
    {
        TestOutcome? outcome = null;
        TimeSpan? duration = null;
        var sawRun = false;
        foreach (var line in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var action = root.TryGetProperty("Action", out var actionValue) ? actionValue.GetString() : null;
                var test = root.TryGetProperty("Test", out var testValue) ? testValue.GetString() : null;
                if (test is null && sawRun && action == "fail")
                {
                    outcome = TestOutcome.Failed;
                    if (root.TryGetProperty("Elapsed", out var packageElapsed) && packageElapsed.TryGetDouble(out var packageSeconds))
                        duration = TimeSpan.FromSeconds(Math.Max(0, packageSeconds));
                    continue;
                }
                if (test is null && sawRun && action == "pass" &&
                    (selector.StartsWith("Benchmark", StringComparison.Ordinal) || selector.StartsWith("Fuzz", StringComparison.Ordinal)))
                {
                    outcome = TestOutcome.Passed;
                    if (root.TryGetProperty("Elapsed", out var packageElapsed) && packageElapsed.TryGetDouble(out var packageSeconds))
                        duration = TimeSpan.FromSeconds(Math.Max(0, packageSeconds));
                    continue;
                }
                if (!StringComparer.Ordinal.Equals(test, selector)) continue;
                if (action == "run")
                {
                    sawRun = true;
                    continue;
                }
                if (!sawRun) continue;
                outcome = action switch
                {
                    "pass" => TestOutcome.Passed,
                    "fail" => TestOutcome.Failed,
                    "skip" => TestOutcome.Skipped,
                    _ => outcome
                };
                if (root.TryGetProperty("Elapsed", out var elapsed) && elapsed.TryGetDouble(out var seconds))
                    duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
            catch (JsonException)
            {
                // go may print compiler diagnostics alongside JSON. The bounded process
                // result is still useful when a later event reports the selected test.
            }
        }

        if (outcome.HasValue) return new TestExecutionResult(identity, outcome.Value, duration);
        return null;
    }

    private async ValueTask<string> GoVersionAsync(string root, GoScope scope, CancellationToken cancellationToken)
    {
        var result = await RunGoAsync(root, scope, ["version"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new AnalysisException("GoToolchainUnavailable", $"The system Go resolver failed: {Diagnostic(result)}");
        var match = VersionPattern.Match(result.StandardOutput);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || major < 1 || (major == 1 && minor < 22))
            throw new AnalysisException("GoToolchainUnavailable", "Go 1.22 or newer is required for deep Go analysis.");
        return match.Value;
    }

    private async ValueTask<IReadOnlyList<GoPackage>> ListPackagesAsync(string root, GoScope scope, CancellationToken cancellationToken, bool throwOnFailure)
    {
        var packages = new List<GoPackage>();
        foreach (var module in scope.Modules.OrderBy(value => value.ModulePath, StringComparer.Ordinal))
        {
            var result = await RunGoAsync(root, scope, ["list", "-mod=readonly", "-json", "./..."], cancellationToken, module.RootPath).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (throwOnFailure) throw new AnalysisException("BuildFailed", $"go list failed for module '{module.ModulePath}': {Diagnostic(result)}");
                continue;
            }

            try
            {
                ParsePackages(result.StandardOutput, module, packages);
            }
            catch (JsonException error)
            {
                if (throwOnFailure) throw new AnalysisException("BuildFailed", $"go list returned malformed JSON for module '{module.ModulePath}'.", error);
            }
        }

        return [.. packages
            .GroupBy(value => value.ImportPath, StringComparer.Ordinal)
            .Select(value => value.First())
            .OrderBy(value => value.ImportPath, StringComparer.Ordinal)];
    }

    private static void ParsePackages(string output, GoModule module, ICollection<GoPackage> packages)
    {
        var bytes = Encoding.UTF8.GetBytes(output);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.TryGetProperty("Error", out var packageError) && packageError.ValueKind == JsonValueKind.Object && packageError.TryGetProperty("Err", out var errorValue) && !string.IsNullOrWhiteSpace(errorValue.GetString()))
                throw new JsonException(errorValue.GetString());
            if (!root.TryGetProperty("ImportPath", out var importPathValue)) continue;
            var importPath = importPathValue.GetString();
            if (string.IsNullOrWhiteSpace(importPath)) continue;
            var directory = root.TryGetProperty("Dir", out var dirValue) ? dirValue.GetString() : null;
            var testFiles = ReadStringArray(root, "TestGoFiles").Concat(ReadStringArray(root, "XTestGoFiles")).Distinct(StringComparer.Ordinal).ToArray();
            packages.Add(new GoPackage(importPath!, directory ?? string.Empty, module, testFiles, testFiles.Length != 0));
        }
    }

    private async ValueTask<IReadOnlyList<string>> ListTestsAsync(string root, GoScope scope, GoPackage package, CancellationToken cancellationToken)
    {
        var result = await RunGoAsync(root, scope, ["test", "-mod=readonly", "-list", ".", package.ImportPath], cancellationToken, package.Module.RootPath).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new AnalysisException("TestDiscoveryFailed", $"go test -list failed for '{package.ImportPath}': {Diagnostic(result)}");
        var lines = result.StandardOutput
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = lines.Where(IsGoTestName).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!lines.Any(value => value.StartsWith("ok ", StringComparison.Ordinal) || value.StartsWith("? ", StringComparison.Ordinal)))
            throw new AnalysisException("TestDiscoveryFailed", $"go test -list returned malformed output for '{package.ImportPath}'.");
        return names;
    }

    private static bool IsGoTestName(string value)
    {
        foreach (var prefix in new[] { "Test", "Benchmark", "Fuzz", "Example" })
        {
            if (StringComparer.Ordinal.Equals(value, prefix)) return true;
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length) continue;
            return !Rune.IsLower(Rune.GetRuneAt(value, prefix.Length));
        }

        return false;
    }

    private static IReadOnlyList<string> ExecutionArguments(TestCatalogEntry test, string artifact, string? profile)
    {
        var arguments = new List<string> { "tool", "test2json", "-p", test.ExecutionScope, artifact, "-test.v=test2json" };
        if (profile is not null) arguments.Add($"-test.coverprofile={profile}");
        if (test.Selector.StartsWith("Benchmark", StringComparison.Ordinal))
        {
            arguments.AddRange(["-test.run", "^$", "-test.bench", $"^{Regex.Escape(test.Selector)}$", "-test.benchtime", "1x"]);
        }
        else if (test.Selector.StartsWith("Fuzz", StringComparison.Ordinal))
        {
            arguments.AddRange(["-test.run", "^$", "-test.fuzz", $"^{Regex.Escape(test.Selector)}$", "-test.fuzztime", "1x"]);
        }
        else
        {
            arguments.AddRange(["-test.run", $"^{Regex.Escape(test.Selector)}$"]);
        }
        return arguments;
    }

    private static GoModule ModuleFor(GoScope scope, string executionScope) =>
        scope.Modules
            .Where(value => executionScope.Equals(value.ModulePath, StringComparison.Ordinal) || executionScope.StartsWith(value.ModulePath + "/", StringComparison.Ordinal))
            .OrderByDescending(value => value.ModulePath.Length)
            .FirstOrDefault() ?? throw new AnalysisException("ModuleNotFound", $"No owning Go module was found for package '{executionScope}'.");

    private static string ArtifactFor(BuildFingerprint fingerprint, TestCatalogEntry test)
    {
        var artifact = fingerprint.Artifacts.FirstOrDefault(value => StringComparer.Ordinal.Equals(value.ScopePath, test.ExecutionScope));
        if (artifact is null || !File.Exists(artifact.ArtifactPath) || !StringComparer.Ordinal.Equals(HashFile(artifact.ArtifactPath), artifact.ArtifactHash))
            throw new AnalysisException("ArtifactsUnavailable", $"No compatible prepared test artifact is available for '{test.ExecutionScope}'.");
        return artifact.ArtifactPath;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in value.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } text) yield return text;
    }

    private static BuildFingerprint CreateFingerprint(DeepAdapterContext context, GoScope scope, string goVersion, IReadOnlyList<GoPackage> packages, IReadOnlyList<BuildArtifact> artifacts, string root)
    {
        var platform = context.Platform;
        var packageEntries = packages.Select(value => $"{value.ImportPath}|{value.Module.ModulePath}|{string.Join(',', value.TestFiles.OrderBy(file => file, StringComparer.Ordinal))}").OrderBy(value => value, StringComparer.Ordinal);
        var moduleEntries = ModuleManifest(scope, root).OrderBy(value => value, StringComparer.Ordinal);
        var artifactEntries = artifacts.OrderBy(value => value.ScopePath, StringComparer.Ordinal).Select(value => $"{value.ScopePath}|{value.ArtifactHash}");
        var canonical = string.Join('\n', [context.Snapshot.Identity.Value, scope.ConfiguredPath ?? "<modules>", context.Configuration, platform, EffectivePlatform(), goVersion, AdapterVersion, ObserverVersion, .. moduleEntries, .. packageEntries, .. artifactEntries]);
        return new BuildFingerprint(
            Hash(Encoding.UTF8.GetBytes(canonical)),
            context.Snapshot.Identity.Value,
            scope.ConfiguredPath ?? "<modules>",
            context.Configuration,
            platform,
            goVersion,
            [.. scope.Modules.Select(value => value.ModulePath).OrderBy(value => value, StringComparer.Ordinal)],
            AdapterVersion,
            ObserverVersion,
            [.. artifacts.OrderBy(value => value.ScopePath, StringComparer.Ordinal)]);
    }

    private static bool ManifestMatches(GoManifest manifest, DeepAdapterContext context, GoScope scope, string goVersion, IReadOnlyList<GoPackage> packages, string root)
    {
        if (!StringComparer.Ordinal.Equals(manifest.Fingerprint.SnapshotId, context.Snapshot.Identity.Value) ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.WorkspacePath, scope.ConfiguredPath ?? "<modules>") ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.Configuration, context.Configuration) ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.Platform, context.Platform) ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.ToolchainVersion, goVersion) ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.AdapterVersion, AdapterVersion) ||
            !StringComparer.Ordinal.Equals(manifest.Fingerprint.ObserverVersion, ObserverVersion)) return false;
        var currentModules = ModuleManifest(scope, root).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var currentPackages = PackageManifest(packages).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expectedArtifacts = packages.Where(value => value.HasTests).Select(value => value.ImportPath).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var actualArtifacts = manifest.Fingerprint.Artifacts.Select(value => value.ScopePath).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return currentModules.SequenceEqual(manifest.Modules.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal) &&
               currentPackages.SequenceEqual(manifest.Packages.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal) &&
               expectedArtifacts.SequenceEqual(actualArtifacts, StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> PackageManifest(IEnumerable<GoPackage> packages) =>
        [.. packages.Select(value => $"{value.ImportPath}|{value.Module.ModulePath}|{string.Join(',', value.TestFiles.OrderBy(file => file, StringComparer.Ordinal))}").OrderBy(value => value, StringComparer.Ordinal)];

    private static IReadOnlyList<string> ModuleManifest(GoScope scope, string root) =>
        [.. scope.Modules.Select(value => $"{value.RootPath}|{value.ModulePath}|{HashFile(WorkspacePath(root, value.GoModPath))}|{(value.SumPath is null ? "" : HashFile(WorkspacePath(root, value.SumPath)))}|{(value.WorkPath is null ? "" : HashFile(WorkspacePath(root, value.WorkPath)))}|{(value.WorkSumPath is null ? "" : HashFile(WorkspacePath(root, value.WorkSumPath)))}").OrderBy(value => value, StringComparer.Ordinal)];

    private static string WorkspacePath(string root, string path) => Path.IsPathRooted(path) ? path : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    private static GoScope ResolveScope(RepositorySnapshot snapshot, string? configured)
    {
        var files = snapshot.Files.Select(value => Normalize(value.Path)).ToHashSet(StringComparer.Ordinal);
        var workspaces = files.Where(value => StringComparer.Ordinal.Equals(Path.GetFileName(value), "go.work")).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var modules = files.Where(value => StringComparer.Ordinal.Equals(Path.GetFileName(value), "go.mod")).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string? selected = null;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            selected = Normalize(configured);
            if (!files.Contains(selected) || (!selected.EndsWith("go.mod", StringComparison.Ordinal) && !selected.EndsWith("go.work", StringComparison.Ordinal)))
                throw new ConfigurationException("ConfiguredSolutionNotFound", $"The configured Go solution '{selected}' is not present in the candidate snapshot.");
        }
        else if (workspaces.Length > 1)
        {
            throw new ConfigurationException("MultipleSolutions", $"Multiple Go workspaces were found; configure one explicitly: {string.Join(", ", workspaces)}.");
        }
        else if (workspaces.Length == 1) selected = workspaces[0];

        var workPath = selected?.EndsWith("go.work", StringComparison.Ordinal) == true ? selected : null;
        IReadOnlyList<string> selectedModules;
        if (workPath is not null)
        {
            selectedModules = ReadWorkspaceUses(snapshot, workPath);
            if (selectedModules.Count == 0) throw new ConfigurationException("SolutionHasNoModules", $"The Go workspace '{workPath}' contains no usable modules.");
        }
        else if (selected is not null)
        {
            selectedModules = [selected];
        }
        else
        {
            selectedModules = modules;
        }
        if (selectedModules.Count == 0) throw new ConfigurationException("SolutionNotFound", "No go.mod or go.work was found in the candidate snapshot.");

        var workSumPath = workPath is null
            ? null
            : Normalize(Path.Combine(Path.GetDirectoryName(workPath) ?? string.Empty, "go.work.sum"));
        if (workSumPath is not null && !files.Contains(workSumPath)) workSumPath = null;
        var scopes = selectedModules.Select(path => ModuleFromSnapshot(snapshot, path, workPath, workSumPath)).OrderBy(value => value.ModulePath, StringComparer.Ordinal).ToArray();
        return new GoScope(selected, workPath, scopes);
    }

    private static IReadOnlyList<string> ReadWorkspaceUses(RepositorySnapshot snapshot, string workPath)
    {
        var text = Encoding.UTF8.GetString(snapshot.Files.Single(value => Normalize(value.Path) == workPath).Content.Span);
        var result = new List<string>();
        var inBlock = false;
        foreach (var sourceLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = sourceLine.Trim();
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment].Trim();
            comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].Trim();
            if (line == "use (") { inBlock = true; continue; }
            if (inBlock && line == ")") { inBlock = false; continue; }
            if (!inBlock && !line.StartsWith("use ", StringComparison.Ordinal)) continue;
            var value = (inBlock ? line : line[4..]).Trim().Trim('"');
            if (value.Length == 0) continue;
            var root = Path.GetDirectoryName(workPath)?.Replace('\\', '/') ?? string.Empty;
            var candidate = Normalize(Path.Combine(root, value, "go.mod"));
            if (candidate.StartsWith("../", StringComparison.Ordinal) || candidate == "..") throw new ConfigurationException("UnsafeSnapshotPath", $"Go workspace use path '{value}' escapes the repository.");
            if (!snapshot.Files.Any(file => Normalize(file.Path) == candidate)) throw new ConfigurationException("ModuleNotFound", $"Go workspace module '{value}' has no go.mod.");
            result.Add(candidate);
        }
        return [.. result.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)];
    }

    private static GoModule ModuleFromSnapshot(RepositorySnapshot snapshot, string goModPath, string? workPath, string? workSumPath)
    {
        var file = snapshot.Files.FirstOrDefault(value => Normalize(value.Path) == goModPath) ?? throw new ConfigurationException("ModuleNotFound", $"Go module '{goModPath}' is not present in the candidate snapshot.");
        var text = Encoding.UTF8.GetString(file.Content.Span);
        var moduleLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split("//", 2, StringSplitOptions.None)[0].Trim())
            .FirstOrDefault(value => value.StartsWith("module ", StringComparison.Ordinal));
        var modulePath = moduleLine?[7..].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(modulePath)) throw new AnalysisException("InvalidModuleFile", $"Go module '{goModPath}' does not declare a module path.");
        var root = Normalize(Path.GetDirectoryName(goModPath) ?? string.Empty);
        var sum = snapshot.Files.Any(value => Normalize(value.Path) == Normalize(Path.Combine(root, "go.sum"))) ? Normalize(Path.Combine(root, "go.sum")) : null;
        return new GoModule(
            root,
            modulePath!,
            SnapshotPath(snapshot, goModPath),
            sum is null ? null : SnapshotPath(snapshot, sum),
            workPath is null ? null : SnapshotPath(snapshot, workPath),
            workSumPath is null ? null : SnapshotPath(snapshot, workSumPath));
    }

    private static string SnapshotPath(RepositorySnapshot snapshot, string relative)
    {
        return relative;
    }

    private ValueTask<ProcessResult> RunGoAsync(string root, GoScope scope, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? moduleRoot = null)
    {
        var working = moduleRoot is null ? root : Path.Combine(root, moduleRoot.Replace('/', Path.DirectorySeparatorChar));
        return _runner.RunAsync(new ProcessRequest(_goPath, arguments, working, StableEnvironment(scope, root), MaxStandardOutputBytes: OutputLimit, MaxStandardErrorBytes: DiagnosticLimit), cancellationToken);
    }

    private static IReadOnlyDictionary<string, string?> StableEnvironment(GoScope scope, string root) => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["GO111MODULE"] = "on",
        ["GOTOOLCHAIN"] = "local",
        ["GOWORK"] = scope.WorkPath is null ? "off" : WorkspacePath(root, scope.WorkPath)
    };

    private static IReadOnlyList<DynamicObservation> ReadObservations(string profile, string root, IReadOnlyList<GoModule> modules, string testIdentity, BuildFingerprint fingerprint, string runId)
    {
        if (!File.Exists(profile)) return [];
        var units = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(profile).Skip(1))
        {
            var match = CoverPattern.Match(line.Trim());
            if (!match.Success || !int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0) continue;
            var path = NormalizeCoverPath(match.Groups["path"].Value, root, modules);
            if (path is not null && File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))) units.Add($"golang:file:{path}");
        }
        return [.. units.OrderBy(value => value, StringComparer.Ordinal).Select(value => new DynamicObservation(testIdentity, value, fingerprint.Value, AdapterVersion, ObserverVersion, runId, "file", BlindSpots))];
    }

    private static string? NormalizeCoverPath(string value, string root, IReadOnlyList<GoModule> modules)
    {
        value = value.Replace('\\', '/');
        if (Path.IsPathRooted(value))
        {
            var full = Path.GetFullPath(value);
            var repo = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(repo, StringComparison.Ordinal)) return null;
            value = full[repo.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }
        foreach (var module in modules.OrderByDescending(value => value.ModulePath.Length))
        {
            if (!value.Equals(module.ModulePath, StringComparison.Ordinal) && !value.StartsWith(module.ModulePath + "/", StringComparison.Ordinal)) continue;
            var suffix = value.Length == module.ModulePath.Length ? string.Empty : value[(module.ModulePath.Length + 1)..];
            value = string.IsNullOrEmpty(module.RootPath) ? suffix : $"{module.RootPath}/{suffix}";
            break;
        }
        value = Normalize(value);
        return value.StartsWith("../", StringComparison.Ordinal) || value == ".." ? null : value;
    }

    private static bool IsValidProfile(string profile)
    {
        try { return File.ReadLines(profile).FirstOrDefault()?.Trim() == "mode: set" && File.ReadLines(profile).Skip(1).Any(line => CoverPattern.IsMatch(line.Trim())); }
        catch (IOException) { return false; }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new CapabilityException("DeepToolchainUnavailable", "Function not available for: golang");
    }

    private static string StateDirectory(DeepAdapterContext context) => Path.GetFullPath(context.StateDirectory ?? Path.Combine(Path.GetTempPath(), "merkle-state"));
    private static string ManifestPath(DeepAdapterContext context, GoScope scope) => Path.Combine(StateDirectory(context), "fingerprints", Hash(Encoding.UTF8.GetBytes((scope.ConfiguredPath ?? "<modules>") + "\0" + context.Snapshot.Identity.Value)) + ".manifest.json");
    private static string EffectivePlatform() => $"{(OperatingSystem.IsMacOS() ? "darwin" : "linux")}/{RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", Architecture.Arm => "arm", _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() }}";
    private static string Normalize(string value)
    {
        var normalized = value.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
    private static string Diagnostic(ProcessResult result) => Diagnostic(result.StandardError.Length == 0 ? result.StandardOutput : result.StandardError);
    private static string Diagnostic(string value) => value.Length <= DiagnosticLimit ? value : value[..DiagnosticLimit];
    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private sealed record GoScope(string? ConfiguredPath, string? WorkPath, IReadOnlyList<GoModule> Modules);
    private sealed record GoModule(string RootPath, string ModulePath, string GoModPath, string? SumPath, string? WorkPath, string? WorkSumPath);
    private sealed record GoPackage(string ImportPath, string Directory, GoModule Module, IReadOnlyList<string> TestFiles, bool HasTests);


    private sealed class Workspace : IAsyncDisposable
    {
        private Workspace(string root) => Root = root;
        public string Root { get; }

        public static async ValueTask<Workspace> CreateAsync(DeepAdapterContext context, CancellationToken cancellationToken)
        {
            ValidateSnapshot(context.Snapshot);
            var state = StateDirectory(context);
            Directory.CreateDirectory(state);
            var root = Path.Combine(state, "workspaces", Hash(Encoding.UTF8.GetBytes(context.Snapshot.Identity.Value)));
            var marker = Path.Combine(root, ".merkle-workspace");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            try
            {
                foreach (var file in context.Snapshot.Files.OrderBy(value => Normalize(value.Path), StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = SafePath(root, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (file.Kind == SnapshotEntryKind.SymbolicLink)
                    {
                        var target = Encoding.UTF8.GetString(file.Content.Span);
                        ValidateLink(file.Path, target, root);
                        File.CreateSymbolicLink(destination, target);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(destination, file.Content.ToArray(), cancellationToken).ConfigureAwait(false);
                        if (file.Kind == SnapshotEntryKind.ExecutableFile && !OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(destination) | UnixFileMode.UserExecute);
                    }
                }
                await File.WriteAllTextAsync(marker, context.Snapshot.Identity.Value, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return new Workspace(root);
            }
            catch
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void ValidateSnapshot(RepositorySnapshot snapshot)
        {
            foreach (var file in snapshot.Files)
            {
                if (file.Kind == SnapshotEntryKind.GitLink) throw new AnalysisException("GitLinkUnavailable", $"Snapshot contains gitlink '{file.Path}', which cannot be materialized safely.");
                _ = SafePath(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merkle-safe-root")), file.Path);
                if (file.Kind == SnapshotEntryKind.SymbolicLink) ValidateLink(file.Path, Encoding.UTF8.GetString(file.Content.Span), Path.Combine(Path.GetTempPath(), "merkle-safe-root"));
            }
        }

        private static void ValidateLink(string path, string target, string root)
        {
            if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) || target.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal) || Regex.IsMatch(target, "^[A-Za-z]:[\\\\/]"))
                throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot symlink '{path}' has an unsafe target.");
            var destination = SafePath(root, path);
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(destination)!, target));
            if (!resolved.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot symlink '{path}' has an unsafe target.");
        }

        private static string SafePath(string root, string relative)
        {
            var normalized = Normalize(relative);
            if (string.IsNullOrWhiteSpace(normalized) || relative.StartsWith("/", StringComparison.Ordinal) || relative.StartsWith("\\", StringComparison.Ordinal) || Regex.IsMatch(relative, "^[A-Za-z]:") || normalized.Split('/').Contains("..", StringComparer.Ordinal))
                throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot path '{relative}' is unsafe.");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(fullRoot, StringComparison.Ordinal)) throw new AnalysisException("UnsafeSnapshotPath", $"Snapshot path '{relative}' escapes its workspace.");
            return full;
        }
    }
}

internal sealed record GoManifest(BuildFingerprint Fingerprint, IReadOnlyList<string> Packages, IReadOnlyList<string> Modules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GoManifest))]
[JsonSerializable(typeof(BuildFingerprint))]
[JsonSerializable(typeof(BuildArtifact))]
internal partial class GoJsonContext : JsonSerializerContext;
