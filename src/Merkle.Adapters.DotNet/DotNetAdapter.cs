using System.Text;
using System.Xml.Linq;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Indexing;

namespace Merkle.Adapters.DotNet;

public sealed class DotNetAdapter(IDotNetAnalysisWorker? analysisWorker = null, DotNetDeepOperations? deepOperations = null) : ILanguageAdapter, IBuildPreparer, ITestDiscoverer, ISelectedTestExecutor, ITestObserver
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] SourceExtensions = [".cs", ".fs", ".vb"];
    private static readonly string[] BuildExtensions = [".props", ".targets"];
    private readonly IDotNetAnalysisWorker? _analysisWorker = analysisWorker;
    private readonly DotNetDeepOperations? _deepOperations = deepOperations;

    public AdapterDescriptor Describe() => new(
        ProtocolVersion: "1.0",
        Language: "dotnet",
        Producer: "merkle",
        AdapterVersion: _deepOperations is { IsConfigured: true } ? DotNetDeepOperations.AdapterVersion : "0.1.0",
        UnitIdentityVersion: "1",
        TestIdentityVersion: "1",
        Capabilities: _deepOperations is { IsConfigured: true }
            ? [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map, AdapterCapability.Discover, AdapterCapability.Observe, AdapterCapability.Execute]
            : _analysisWorker is null
            ? [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map]
            : [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map, AdapterCapability.Discover],
        Profiles: _deepOperations is { IsConfigured: true } ? ["minimal", "semantic", "deep"] : _analysisWorker is null ? ["minimal"] : ["minimal", "semantic"],
        SupportedTargets: ["net6.0+"],
        SupportedPlatforms: ["linux", "macos"]);

    public async ValueTask<AdapterIndex> IndexAsync(
        AdapterIndexRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Snapshot.Files.Any(IsDotNetLanguageInput))
        {
            return new AdapterIndex([], [], []);
        }

        if (_analysisWorker is not null)
        {
            return await _analysisWorker.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var solution = ResolveSolution(request.Snapshot, request.ConfiguredSolution);

        var allProjects = request.Snapshot.Files
            .Where(file => ProjectExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase))
            .Select(ParseProject)
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToArray();
        if (allProjects.Length == 0)
        {
            throw new ConfigurationException("ProjectNotFound", "The selected .NET solution contains no discoverable project files.");
        }

        var projects = SelectSolutionProjects(request.Snapshot, solution, allProjects);

        var units = BuildUnits(request.Snapshot, projects);
        var tests = projects
            .Where(project => project.IsTestProject)
            .Select(project => new TestDescriptor(
                TestIdentity(project.Path),
                project.Path,
                "dotnet-project"))
            .OrderBy(test => test.Identity, StringComparer.Ordinal)
            .ToArray();
        var edges = BuildEdges(request.Snapshot, projects, tests, units);

        return new AdapterIndex(units, edges, tests);
    }

    public ValueTask<MappingResult> MapAsync(
        AdapterMapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var testLookup = request.Index.Tests.ToDictionary(test => test.Identity, StringComparer.Ordinal);
        var result = new ImpactIndex(request.Index.Edges).FindRequestedTests(request.ChangedUnits, testLookup);
        return ValueTask.FromResult(result);
    }

    public ValueTask<BuildPreparationResult> PrepareBuildAsync(BuildPreparationRequest request, CancellationToken cancellationToken) =>
        Deep().PrepareBuildAsync(request, cancellationToken);

    public ValueTask<DiscoveryCatalog> DiscoverAsync(DeepAdapterContext context, BuildFingerprint fingerprint, CancellationToken cancellationToken) =>
        Deep().DiscoverAsync(context, fingerprint, cancellationToken);

    public ValueTask<IReadOnlyList<TestExecutionResult>> ExecuteAsync(SelectedExecutionRequest request, CancellationToken cancellationToken) =>
        Deep().ExecuteAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObservationScope>> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) =>
        Deep().ObserveAsync(request, cancellationToken);

    private DotNetDeepOperations Deep() => _deepOperations is { IsConfigured: true } configured
        ? configured
        : throw new CapabilityException("DeepToolchainUnavailable", "Function not available for: dotnet. The deep toolchain and observer are not configured.");

    private static string ResolveSolution(RepositorySnapshot snapshot, string? configuredSolution)
    {
        var solutions = snapshot.Files
            .Where(file => SolutionExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(configuredSolution))
        {
            var normalized = NormalizePath(configuredSolution);
            if (!solutions.Contains(normalized, StringComparer.Ordinal))
            {
                throw new ConfigurationException(
                    "ConfiguredSolutionNotFound",
                    $"The configured solution '{normalized}' is not present in the candidate snapshot.");
            }

            return normalized;
        }

        if (solutions.Length == 0)
        {
            throw new ConfigurationException("SolutionNotFound", "No .NET solution was found. Configure one solution explicitly.");
        }

        if (solutions.Length > 1)
        {
            throw new ConfigurationException(
                "MultipleSolutions",
                $"Multiple .NET solutions were found; configure one explicitly: {string.Join(", ", solutions)}.");
        }

        return solutions[0];
    }

    private static ProjectModel ParseProject(SnapshotFile file)
    {
        try
        {
            using var stream = new MemoryStream(file.Content.ToArray(), writable: false);
            var document = XDocument.Load(stream, LoadOptions.None);
            var isTestProject = document.Descendants()
                .Where(element => element.Name.LocalName == "IsTestProject")
                .Any(element => bool.TryParse(element.Value.Trim(), out var value) && value) ||
                document.Descendants()
                    .Where(element => element.Name.LocalName == "PackageReference")
                    .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
                    .Any(IsTestPackage);
            var references = document.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => ResolveRelativeProject(file.Path, include!))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            return new ProjectModel(file.Path, file.ContentHash, isTestProject, references);
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException)
        {
            throw new AnalysisException(
                "InvalidProjectFile",
                $"The .NET project '{file.Path}' could not be parsed.",
                error);
        }
    }

    private static ProjectModel[] SelectSolutionProjects(
        RepositorySnapshot snapshot,
        string solutionPath,
        IReadOnlyList<ProjectModel> allProjects)
    {
        var solutionFile = snapshot.Files.Single(file => StringComparer.Ordinal.Equals(file.Path, solutionPath));
        var declaredPaths = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadSlnxProjects(solutionPath, solutionFile.Content)
            : ReadSlnProjects(solutionPath, solutionFile.Content);
        if (declaredPaths.Count == 0)
        {
            throw new ConfigurationException(
                "SolutionHasNoProjects",
                $"The selected solution '{solutionPath}' contains no recognized .NET project entries.");
        }

        var allByPath = allProjects.ToDictionary(project => project.Path, StringComparer.Ordinal);
        var selected = new HashSet<string>(declaredPaths, StringComparer.Ordinal);
        var pending = new Queue<string>(declaredPaths);
        while (pending.TryDequeue(out var path))
        {
            if (!allByPath.TryGetValue(path, out var project))
            {
                throw new ConfigurationException(
                    "SolutionProjectNotFound",
                    $"Solution project '{path}' is not present in the snapshot.");
            }

            foreach (var reference in project.References)
            {
                if (selected.Add(reference))
                {
                    pending.Enqueue(reference);
                }
            }
        }

        return [.. selected
            .Select(path => allByPath[path])
            .OrderBy(project => project.Path, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> ReadSlnxProjects(
        string solutionPath,
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            var document = XDocument.Load(stream, LoadOptions.None);
            return [.. document.Descendants()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => (string?)element.Attribute("Path"))
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(path => ResolveRelativeSolutionPath(solutionPath, path!))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)];
        }
        catch (System.Xml.XmlException error)
        {
            throw new AnalysisException("InvalidSolutionFile", $"The solution '{solutionPath}' could not be parsed.", error);
        }
    }

    private static IReadOnlyList<string> ReadSlnProjects(
        string solutionPath,
        ReadOnlyMemory<byte> content)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        return [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith("Project(", StringComparison.Ordinal))
            .Select(TryReadSlnProjectPath)
            .Where(path => path is not null &&
                           ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => ResolveRelativeSolutionPath(solutionPath, path!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static string? TryReadSlnProjectPath(string line)
    {
        var fields = line.Split(',');
        return fields.Length >= 2 ? fields[1].Trim().Trim('"') : null;
    }

    private static string ResolveRelativeSolutionPath(string solutionPath, string projectPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        return NormalizeRelativePath(Path.Combine(
            solutionDirectory,
            projectPath.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static IReadOnlyList<SourceUnit> BuildUnits(
        RepositorySnapshot snapshot,
        IReadOnlyList<ProjectModel> projects)
    {
        var units = new List<SourceUnit>();
        foreach (var project in projects)
        {
            units.Add(new SourceUnit(
                ProjectIdentity(project.Path),
                SourceUnitKind.Project,
                project.Path,
                project.ContentHash,
                string.Empty));
        }

        foreach (var file in snapshot.Files.Where(file =>
                     IsIndexableFile(file) &&
                     !ProjectExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) &&
                     (SolutionExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) ||
                      IsSolutionWideInput(file.Path) ||
                      FindOwningProject(file.Path, projects) is not null)))
        {
            units.Add(new SourceUnit(
                FileIdentity(file.Path),
                SourceUnitKind.File,
                file.Path,
                file.ContentHash,
                string.Empty));
        }

        return [.. units
            .DistinctBy(unit => unit.Identity, StringComparer.Ordinal)
            .OrderBy(unit => unit.Identity, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<ImpactEdge> BuildEdges(
        RepositorySnapshot snapshot,
        IReadOnlyList<ProjectModel> projects,
        IReadOnlyList<TestDescriptor> tests,
        IReadOnlyList<SourceUnit> units)
    {
        var edges = new List<ImpactEdge>();
        var projectByPath = projects.ToDictionary(project => project.Path, StringComparer.Ordinal);
        var indexedIdentities = units.Select(unit => unit.Identity).ToHashSet(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            foreach (var reference in project.References.Where(projectByPath.ContainsKey))
            {
                edges.Add(new ImpactEdge(
                    ProjectIdentity(reference),
                    ProjectIdentity(project.Path),
                    EvidenceKind.StaticDependency));
            }

            var testIdentity = TestIdentity(project.Path);
            if (tests.Any(test => StringComparer.Ordinal.Equals(test.Identity, testIdentity)))
            {
                edges.Add(new ImpactEdge(
                    ProjectIdentity(project.Path),
                    testIdentity,
                    EvidenceKind.AncestorFallback));
            }
        }

        foreach (var file in snapshot.Files.Where(file =>
                     IsIndexableFile(file) && indexedIdentities.Contains(FileIdentity(file.Path))))
        {
            var owningProject = FindOwningProject(file.Path, projects);
            if (owningProject is not null)
            {
                edges.Add(new ImpactEdge(
                    FileIdentity(file.Path),
                    ProjectIdentity(owningProject.Path),
                    EvidenceKind.Containment));
            }

            if (SolutionExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) ||
                BuildExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) ||
                IsSolutionWideInput(file.Path))
            {
                foreach (var project in projects)
                {
                    edges.Add(new ImpactEdge(
                        FileIdentity(file.Path),
                        ProjectIdentity(project.Path),
                        EvidenceKind.AncestorFallback));
                }
            }
        }

        return [.. edges
            .Distinct()
            .OrderBy(edge => edge.SourceIdentity, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetIdentity, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)];
    }

    private static ProjectModel? FindOwningProject(
        string path,
        IReadOnlyList<ProjectModel> projects)
    {
        var directory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        return projects
            .Where(project =>
            {
                var projectDirectory = Path.GetDirectoryName(project.Path.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
                return projectDirectory.Length == 0 ||
                       directory.Equals(projectDirectory, StringComparison.Ordinal) ||
                       directory.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            })
            .OrderByDescending(project => project.Path.Length)
            .FirstOrDefault();
    }

    private static bool IsIndexableFile(SnapshotFile file)
    {
        var extension = Path.GetExtension(file.Path);
        return SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               BuildExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               IsSolutionWideInput(file.Path);
    }

    private static bool IsDotNetLanguageInput(SnapshotFile file)
    {
        var extension = Path.GetExtension(file.Path);
        return SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSolutionWideInput(string path) => Path.GetFileName(path) is
        "global.json" or
        "NuGet.config" or
        "nuget.config" or
        "Directory.Build.props" or
        "Directory.Build.targets" or
        "Directory.Packages.props";

    private static bool IsTestPackage(string? package) => package is not null &&
        (package.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
         package.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
         package.Contains("mstest", StringComparison.OrdinalIgnoreCase) ||
         package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

    private static string ResolveRelativeProject(string projectPath, string include)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        return NormalizeRelativePath(Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static string NormalizeRelativePath(string combined)
    {
        var segments = new List<string>();
        foreach (var segment in combined.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new AnalysisException("ProjectReferenceOutsideRepository", "A project reference escapes the repository root.");
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string ProjectIdentity(string path) => $"dotnet:project:{NormalizePath(path)}";

    private static string FileIdentity(string path) => $"dotnet:file:{NormalizePath(path)}";

    private static string TestIdentity(string path) => $"dotnet-project:{NormalizePath(path)}";

    private sealed record ProjectModel(
        string Path,
        string ContentHash,
        bool IsTestProject,
        IReadOnlyList<string> References);
}
