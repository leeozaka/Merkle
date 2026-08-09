using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Merkle.Adapters.DotNet;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const int maxInputBytes = 16 * 1024 * 1024;
DotNetWorkerRequest? request = null;
try
{
    var input = await ReadBoundedAsync(Console.OpenStandardInput(), maxInputBytes, CancellationToken.None);
    request = JsonSerializer.Deserialize(input, DotNetWorkerJsonContext.Default.DotNetWorkerRequest);
    if (request is null || request.ProtocolVersion != "1.0" || string.IsNullOrWhiteSpace(request.RequestId))
    {
        throw new WorkerException("WorkerProtocolMalformed", "The request is not a valid protocol 1.0 envelope.");
    }

    var index = SemanticIndexBuilder.Build(request);
    var response = new DotNetWorkerResponse("1.0", request.RequestId, true, index.Units, index.Edges, index.Tests, index.Warnings, null);
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), response, DotNetWorkerJsonContext.Default.DotNetWorkerResponse);
}
catch (WorkerException error)
{
    await WriteFailureAsync(request?.RequestId ?? "unknown", error.Code, error.Message);
    Environment.ExitCode = 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString());
    await WriteFailureAsync(request?.RequestId ?? "unknown", "WorkerUnhandledError", "The semantic worker encountered an unexpected error.");
    Environment.ExitCode = 3;
}

static async Task WriteFailureAsync(string requestId, string code, string message) =>
    await JsonSerializer.SerializeAsync(
        Console.OpenStandardOutput(),
        DotNetWorkerResponse.Failure(requestId, code, message),
        DotNetWorkerJsonContext.Default.DotNetWorkerResponse);

static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken cancellationToken)
{
    await using var buffer = new MemoryStream();
    var chunk = new byte[16_384];
    while (true)
    {
        var read = await input.ReadAsync(chunk, cancellationToken);
        if (read == 0)
        {
            return buffer.ToArray();
        }

        if (buffer.Length + read > limit)
        {
            throw new WorkerException("WorkerRequestLimitExceeded", "The request exceeds the 16 MiB protocol limit.");
        }

        await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
    }
}

internal sealed class WorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record WorkerIndex(
    IReadOnlyList<SourceUnit> Units,
    IReadOnlyList<ImpactEdge> Edges,
    IReadOnlyList<TestDescriptor> Tests,
    IReadOnlyList<string> Warnings);

internal static class SemanticIndexBuilder
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] SourceExtensions = [".cs", ".fs", ".vb"];
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] BuildExtensions = [".props", ".targets"];

    public static WorkerIndex Build(DotNetWorkerRequest request)
    {
        var files = request.Files
            .Select(file => file with { Path = NormalizePath(file.Path) })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        if (!files.Any(IsDotNetInput))
        {
            return new WorkerIndex([], [], [], []);
        }

        var solutionPath = ResolveSolution(files, request.ConfiguredSolution);
        var allProjects = files.Where(file => HasExtension(file.Path, ProjectExtensions)).Select(ParseProject).ToArray();
        if (allProjects.Length == 0)
        {
            throw new WorkerException("ProjectNotFound", "The selected .NET solution contains no discoverable project files.");
        }

        var projects = SelectProjects(files, solutionPath, allProjects);
        var units = new Dictionary<string, SourceUnit>(StringComparer.Ordinal);
        var edges = new HashSet<ImpactEdge>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal)
        {
            "Project membership was parsed from solution and project files; conditional MSBuild items were not evaluated."
        };
        foreach (var project in projects)
        {
            AddUnit(units, new SourceUnit(ProjectIdentity(project.Path), SourceUnitKind.Project, project.Path, project.ContentHash, string.Empty));
            foreach (var reference in project.References.Where(reference => projects.Any(candidate => candidate.Path == reference)))
            {
                edges.Add(new ImpactEdge(ProjectIdentity(reference), ProjectIdentity(project.Path), EvidenceKind.StaticDependency));
            }
        }

        var sourceFiles = files.Where(IsIndexable)
            .Where(file => !HasExtension(file.Path, ProjectExtensions))
            .Where(file => HasExtension(file.Path, SolutionExtensions) || IsSolutionWideInput(file.Path) || FindOwner(file.Path, projects) is not null)
            .ToArray();
        foreach (var file in sourceFiles)
        {
            AddUnit(units, new SourceUnit(FileIdentity(file.Path), SourceUnitKind.File, file.Path, file.ContentHash, string.Empty));
            var owner = FindOwner(file.Path, projects);
            if (owner is not null)
            {
                edges.Add(new ImpactEdge(FileIdentity(file.Path), ProjectIdentity(owner.Path), EvidenceKind.Containment));
            }
            else if (HasExtension(file.Path, SolutionExtensions) || HasExtension(file.Path, BuildExtensions) || IsSolutionWideInput(file.Path))
            {
                foreach (var project in projects)
                {
                    edges.Add(new ImpactEdge(FileIdentity(file.Path), ProjectIdentity(project.Path), EvidenceKind.AncestorFallback));
                }
            }
        }

        var csharp = sourceFiles
            .Where(file => Path.GetExtension(file.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(file => new CSharpFile(file, FindOwner(file.Path, projects)!))
            .ToArray();
        foreach (var other in sourceFiles.Where(file => Path.GetExtension(file.Path) is ".fs" or ".vb"))
        {
            warnings.Add($"{other.Path}: F#/Visual Basic semantic analysis is unavailable; file/project fallback was used.");
        }

        var semantic = CSharpSemanticSlice.Analyze(csharp, units, edges, warnings);
        var tests = DiscoverTests(projects, semantic.Members, edges, warnings);
        ValidateContainmentTree(edges);
        return new WorkerIndex(
            [.. units.Values.OrderBy(unit => unit.Identity, StringComparer.Ordinal)],
            [.. edges.OrderBy(edge => edge.SourceIdentity, StringComparer.Ordinal).ThenBy(edge => edge.TargetIdentity, StringComparer.Ordinal).ThenBy(edge => edge.Kind)],
            [.. tests.OrderBy(test => test.Identity, StringComparer.Ordinal)],
            [.. warnings]);
    }

    private static IReadOnlyList<TestDescriptor> DiscoverTests(
        IReadOnlyList<ProjectModel> projects,
        IReadOnlyList<MemberModel> members,
        ISet<ImpactEdge> edges,
        ISet<string> warnings)
    {
        var tests = new List<TestDescriptor>();
        foreach (var project in projects.Where(project => project.IsTestProject))
        {
            var discovered = false;
            foreach (var member in members.Where(member => member.ProjectPath == project.Path && member.Method is not null))
            {
                var attributes = member.Method!.AttributeLists.SelectMany(list => list.Attributes).ToArray();
                var framework = Framework(attributes);
                if (framework is null)
                {
                    continue;
                }

                discovered = true;
                var cases = LiteralCases(attributes).ToArray();
                if (cases.Length == 0)
                {
                    if (attributes.Any(attribute => IsAttribute(attribute, "Theory") || IsAttribute(attribute, "DataTestMethod")))
                    {
                        warnings.Add($"{member.Identity}: dynamic or member-supplied test data was not expanded; method-level identity was used.");
                    }

                    tests.Add(AddTest(member, framework, "method", edges));
                    continue;
                }

                foreach (var testCase in cases.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                {
                    tests.Add(AddTest(member, framework, "case-" + Hash(testCase)[..16], edges));
                }
            }

            if (!discovered)
            {
                warnings.Add($"{project.Path}: no individual static test methods were discovered; project-level ancestor fallback was used.");
                var identity = $"dotnet-project:{project.Path}";
                tests.Add(new TestDescriptor(identity, project.Path, "dotnet-project"));
                edges.Add(new ImpactEdge(ProjectIdentity(project.Path), identity, EvidenceKind.AncestorFallback));
            }
        }

        return [.. tests.DistinctBy(test => test.Identity, StringComparer.Ordinal)];
    }

    private static TestDescriptor AddTest(MemberModel member, string framework, string discriminator, ISet<ImpactEdge> edges)
    {
        var identity = $"dotnet:test:v1:{member.ProjectPath}:{member.TypeIdentity}:{member.Signature}:{discriminator}";
        edges.Add(new ImpactEdge(member.Identity, identity, EvidenceKind.StaticDependency));
        return new TestDescriptor(identity, $"{member.TypeDisplay}.{member.Signature}", framework);
    }

    private static IEnumerable<string> LiteralCases(IEnumerable<AttributeSyntax> attributes) => attributes
        .Where(attribute => IsAttribute(attribute, "InlineData") || IsAttribute(attribute, "TestCase") || IsAttribute(attribute, "DataRow"))
        .Where(attribute => attribute.ArgumentList?.Arguments.All(argument => argument.Expression is LiteralExpressionSyntax) == true)
        .Select(attribute => string.Join("|", attribute.ArgumentList!.Arguments.Select(argument => argument.Expression.WithoutTrivia().ToFullString())));

    private static string? Framework(IEnumerable<AttributeSyntax> attributes)
    {
        if (attributes.Any(attribute => IsAttribute(attribute, "Fact") || IsAttribute(attribute, "Theory"))) return "xunit";
        if (attributes.Any(attribute => IsAttribute(attribute, "Test") || IsAttribute(attribute, "TestCase"))) return "nunit";
        if (attributes.Any(attribute => IsAttribute(attribute, "TestMethod") || IsAttribute(attribute, "DataTestMethod"))) return "mstest";
        return null;
    }

    private static bool IsAttribute(AttributeSyntax attribute, string name)
    {
        var value = attribute.Name.ToString().Split('.').Last();
        return value.Equals(name, StringComparison.Ordinal) || value.Equals(name + "Attribute", StringComparison.Ordinal);
    }

    private static string ResolveSolution(IReadOnlyList<DotNetWorkerFile> files, string? configured)
    {
        var solutions = files.Where(file => HasExtension(file.Path, SolutionExtensions)).Select(file => file.Path).ToArray();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = NormalizePath(configured);
            if (!solutions.Contains(path, StringComparer.Ordinal)) throw new WorkerException("ConfiguredSolutionNotFound", $"The configured solution '{path}' is not present in the candidate snapshot.");
            return path;
        }
        if (solutions.Length == 0) throw new WorkerException("SolutionNotFound", "No .NET solution was found. Configure one solution explicitly.");
        if (solutions.Length > 1) throw new WorkerException("MultipleSolutions", $"Multiple .NET solutions were found; configure one explicitly: {string.Join(", ", solutions)}.");
        return solutions[0];
    }

    private static ProjectModel ParseProject(DotNetWorkerFile file)
    {
        try
        {
            var content = file.Content.Length > 0 && file.Content[0] == '\uFEFF' ? file.Content[1..] : file.Content;
            var document = XDocument.Parse(content, LoadOptions.None);
            var isTest = document.Descendants().Where(element => element.Name.LocalName == "IsTestProject").Any(element => bool.TryParse(element.Value.Trim(), out var value) && value) ||
                         document.Descendants().Where(element => element.Name.LocalName == "PackageReference").Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update")).Any(IsTestPackage);
            var references = document.Descendants().Where(element => element.Name.LocalName == "ProjectReference").Select(element => (string?)element.Attribute("Include")).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => ResolveRelative(file.Path, path!)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            return new ProjectModel(file.Path, file.ContentHash, isTest, references);
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException)
        {
            throw new WorkerException("InvalidProjectFile", $"The .NET project '{file.Path}' could not be parsed.");
        }
    }

    private static ProjectModel[] SelectProjects(IReadOnlyList<DotNetWorkerFile> files, string solution, IReadOnlyList<ProjectModel> projects)
    {
        var solutionFile = files.Single(file => file.Path == solution);
        var declared = solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? ReadSlnx(solution, solutionFile.Content) : ReadSln(solution, solutionFile.Content);
        if (declared.Count == 0) throw new WorkerException("SolutionHasNoProjects", $"The selected solution '{solution}' contains no recognized .NET project entries.");
        var all = projects.ToDictionary(project => project.Path, StringComparer.Ordinal);
        var selected = new HashSet<string>(declared, StringComparer.Ordinal);
        var pending = new Queue<string>(declared);
        while (pending.TryDequeue(out var path))
        {
            if (!all.TryGetValue(path, out var project)) throw new WorkerException("SolutionProjectNotFound", $"Solution project '{path}' is not present in the snapshot.");
            foreach (var reference in project.References) if (selected.Add(reference)) pending.Enqueue(reference);
        }
        return [.. selected.Select(path => all[path]).OrderBy(project => project.Path, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> ReadSlnx(string solution, string content)
    {
        try { return [.. XDocument.Parse(content).Descendants().Where(element => element.Name.LocalName == "Project").Select(element => (string?)element.Attribute("Path")).Where(path => path is not null && HasExtension(path, ProjectExtensions)).Select(path => ResolveRelative(solution, path!)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)]; }
        catch (System.Xml.XmlException) { throw new WorkerException("InvalidSolutionFile", $"The solution '{solution}' could not be parsed."); }
    }

    private static IReadOnlyList<string> ReadSln(string solution, string content) => [.. content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.TrimStart().StartsWith("Project(", StringComparison.Ordinal)).Select(line => line.Split(',').Length >= 2 ? line.Split(',')[1].Trim().Trim('"') : null)
        .Where(path => path is not null && HasExtension(path, ProjectExtensions)).Select(path => ResolveRelative(solution, path!)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)];

    private static ProjectModel? FindOwner(string path, IReadOnlyList<ProjectModel> projects)
    {
        var directory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        return projects.Where(project => { var projectDirectory = Path.GetDirectoryName(project.Path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty; return projectDirectory.Length == 0 || directory == projectDirectory || directory.StartsWith(projectDirectory + "/", StringComparison.Ordinal); }).OrderByDescending(project => project.Path.Length).FirstOrDefault();
    }

    private static void AddUnit(IDictionary<string, SourceUnit> units, SourceUnit unit)
    {
        if (!units.TryGetValue(unit.Identity, out var previous)) { units.Add(unit.Identity, unit); return; }
        if (previous.SemanticSignature.Length == 0) return;
        units[unit.Identity] = unit with { SemanticSignature = Hash(previous.SemanticSignature + "\0" + unit.SemanticSignature) };
    }

    private static void ValidateContainmentTree(IEnumerable<ImpactEdge> edges)
    {
        var parents = edges.Where(edge => edge.Kind == EvidenceKind.Containment).GroupBy(edge => edge.SourceIdentity, StringComparer.Ordinal);
        foreach (var group in parents) if (group.Select(edge => edge.TargetIdentity).Distinct(StringComparer.Ordinal).Skip(1).Any()) throw new WorkerException("ContainmentNotTree", $"Containment has multiple parents for '{group.Key}'.");
    }

    private static bool IsDotNetInput(DotNetWorkerFile file) => HasExtension(file.Path, SourceExtensions) || HasExtension(file.Path, ProjectExtensions) || HasExtension(file.Path, SolutionExtensions);
    private static bool IsIndexable(DotNetWorkerFile file) => HasExtension(file.Path, SourceExtensions) || HasExtension(file.Path, SolutionExtensions) || HasExtension(file.Path, BuildExtensions) || IsSolutionWideInput(file.Path);
    private static bool IsSolutionWideInput(string path) => Path.GetFileName(path) is "global.json" or "NuGet.config" or "nuget.config" or "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props";
    private static bool HasExtension(string path, IEnumerable<string> extensions) => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static bool IsTestPackage(string? package) => package is not null && (package.Contains("xunit", StringComparison.OrdinalIgnoreCase) || package.Contains("nunit", StringComparison.OrdinalIgnoreCase) || package.Contains("mstest", StringComparison.OrdinalIgnoreCase) || package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
    private static string ResolveRelative(string parent, string child)
    {
        var directory = Path.GetDirectoryName(parent.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var result = new List<string>();
        foreach (var segment in Path.Combine(directory, child.Replace('\\', Path.DirectorySeparatorChar)).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (result.Count == 0) throw new WorkerException("ProjectReferenceOutsideRepository", "A project reference escapes the repository root."); result.RemoveAt(result.Count - 1); }
            else result.Add(segment);
        }
        return string.Join('/', result);
    }
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string ProjectIdentity(string path) => "dotnet:project:" + path;
    internal static string FileIdentity(string path) => "dotnet:file:" + path;
    internal sealed record ProjectModel(string Path, string ContentHash, bool IsTestProject, IReadOnlyList<string> References);
}

internal sealed record CSharpFile(DotNetWorkerFile File, SemanticIndexBuilder.ProjectModel Project);
internal sealed record MemberModel(string Identity, string ProjectPath, string TypeIdentity, string TypeDisplay, string Signature, MethodDeclarationSyntax? Method, SyntaxNode Node);

internal sealed record SemanticSlice(IReadOnlyList<MemberModel> Members);

internal static class CSharpSemanticSlice
{
    public static SemanticSlice Analyze(IEnumerable<CSharpFile> files, IDictionary<string, SourceUnit> units, ISet<ImpactEdge> edges, ISet<string> warnings)
    {
        var declarations = new List<(CSharpFile File, SyntaxNode Node, string Identity, SourceUnitKind Kind, string Parent)>();
        var members = new List<MemberModel>();
        foreach (var file in files)
        {
            var root = CSharpSyntaxTree.ParseText(file.File.Content, path: file.File.Path).GetCompilationUnitRoot();
            var fileSignature = SemanticIndexBuilder.Hash(Tokens(root));
            units[SemanticIndexBuilder.FileIdentity(file.File.Path)] = new SourceUnit(
                SemanticIndexBuilder.FileIdentity(file.File.Path), SourceUnitKind.File, file.File.Path, fileSignature, fileSignature);
            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var typeId = TypeIdentity(file.Project.Path, type);
                var parent = type.Parent is TypeDeclarationSyntax outer ? TypeIdentity(file.Project.Path, outer) : NamespaceIdentity(file.Project.Path, Namespace(type));
                declarations.Add((file, type, typeId, SourceUnitKind.Type, parent));
                if (type.Parent is not TypeDeclarationSyntax) declarations.Add((file, type, parent, SourceUnitKind.Namespace, SemanticIndexBuilder.ProjectIdentity(file.Project.Path)));
                foreach (var member in type.ChildNodes().OfType<MemberDeclarationSyntax>())
                {
                    foreach (var model in Members(file.Project.Path, typeId, TypeDisplay(type), member))
                    {
                        members.Add(model);
                        declarations.Add((file, model.Node, model.Identity, SourceUnitKind.Member, typeId));
                    }
                }
            }
            foreach (var delegateType in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                var typeId = TypeIdentity(file.Project.Path, delegateType);
                var parent = NamespaceIdentity(file.Project.Path, Namespace(delegateType));
                declarations.Add((file, delegateType, typeId, SourceUnitKind.Type, parent));
                declarations.Add((file, delegateType, parent, SourceUnitKind.Namespace, SemanticIndexBuilder.ProjectIdentity(file.Project.Path)));
            }
        }

        foreach (var group in declarations.GroupBy(item => item.Identity, StringComparer.Ordinal))
        {
            var first = group.OrderBy(item => item.File.File.Path, StringComparer.Ordinal).ThenBy(item => item.Node.SpanStart).First();
            var signature = SemanticIndexBuilder.Hash(string.Join("\n", group.OrderBy(item => item.File.File.Path, StringComparer.Ordinal).ThenBy(item => item.Node.SpanStart).Select(item => Tokens(item.Node))));
            units[first.Identity] = new SourceUnit(first.Identity, first.Kind, first.File.File.Path, signature, signature);
            edges.Add(new ImpactEdge(first.Identity, first.Parent, EvidenceKind.Containment));
        }

        var memberNames = members.GroupBy(member => Name(member.Signature), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var typeNames = declarations.Where(item => item.Kind == SourceUnitKind.Type).GroupBy(item => SimpleTypeName(item.Identity), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.Identity).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var identifier in member.Node.DescendantNodes().OfType<IdentifierNameSyntax>().Select(node => node.Identifier.ValueText).Distinct(StringComparer.Ordinal))
            {
                if (memberNames.TryGetValue(identifier, out var matchingMembers))
                {
                    if (matchingMembers.Length == 1 && matchingMembers[0].Identity != member.Identity) edges.Add(new ImpactEdge(matchingMembers[0].Identity, member.Identity, EvidenceKind.StaticDependency));
                    else if (matchingMembers.Length > 1) warnings.Add($"{member.Identity}: ambiguous member reference '{identifier}' fell back to containing type/project evidence.");
                }
                if (typeNames.TryGetValue(identifier, out var matchingTypes))
                {
                    if (matchingTypes.Length == 1 && matchingTypes[0] != member.TypeIdentity) edges.Add(new ImpactEdge(matchingTypes[0], member.Identity, EvidenceKind.StaticDependency));
                    else if (matchingTypes.Length > 1) warnings.Add($"{member.Identity}: ambiguous type reference '{identifier}' fell back to containing type/project evidence.");
                }
            }
        }
        return new SemanticSlice(members);
    }

    private static IEnumerable<MemberModel> Members(string project, string typeIdentity, string typeDisplay, MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => [Create(project, typeIdentity, typeDisplay, Signature(method), method, method)],
        ConstructorDeclarationSyntax constructor => [Create(project, typeIdentity, typeDisplay, Signature(constructor), null, constructor)],
        PropertyDeclarationSyntax property => [Create(project, typeIdentity, typeDisplay, property.Identifier.ValueText, null, property)],
        EventDeclarationSyntax @event => [Create(project, typeIdentity, typeDisplay, @event.Identifier.ValueText, null, @event)],
        FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => Create(project, typeIdentity, typeDisplay, variable.Identifier.ValueText, null, field)),
        EnumMemberDeclarationSyntax value => [Create(project, typeIdentity, typeDisplay, value.Identifier.ValueText, null, value)],
        _ => []
    };
    private static MemberModel Create(string project, string typeIdentity, string typeDisplay, string signature, MethodDeclarationSyntax? method, SyntaxNode node) => new(
        $"dotnet:member:{project}:{typeIdentity}:{signature}", project, typeIdentity, typeDisplay, signature, method, node);
    private static string Signature(BaseMethodDeclarationSyntax method) => $"{MethodName(method)}({string.Join(",", method.ParameterList.Parameters.Select(parameter => parameter.Type?.WithoutTrivia().ToFullString() ?? "?"))})";
    private static string MethodName(BaseMethodDeclarationSyntax method) => method switch
    {
        MethodDeclarationSyntax named => named.Identifier.ValueText + "`" + (named.TypeParameterList?.Parameters.Count ?? 0),
        ConstructorDeclarationSyntax named => named.Identifier.ValueText,
        _ => ".ctor"
    };
    private static string TypeIdentity(string project, SyntaxNode type) => $"dotnet:type:{project}:{Namespace(type)}.{string.Join("+", type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(TypeSegment).Append(type is DelegateDeclarationSyntax delegateType ? delegateType.Identifier.ValueText + "`" + (delegateType.TypeParameterList?.Parameters.Count ?? 0) : string.Empty).Where(value => value.Length > 0))}";
    private static string TypeDisplay(SyntaxNode type) => string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(value => value.Identifier.ValueText));
    private static string Namespace(SyntaxNode node) => string.Join(".", node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(value => value.Name.WithoutTrivia().ToFullString()));
    private static string NamespaceIdentity(string project, string value) => $"dotnet:namespace:{project}:{value}";
    private static string Tokens(SyntaxNode node) => string.Join("\u001f", node.DescendantTokens(descendIntoTrivia: false).Select(token => token.Kind() + ":" + token.ValueText));
    private static string TypeSegment(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax generic => generic.Identifier.ValueText + "`" + (generic.TypeParameterList?.Parameters.Count ?? 0),
        _ => type.Identifier.ValueText + "`0"
    };
    private static string Name(string signature) { var separator = signature.IndexOf('('); return separator < 0 ? signature : signature[..separator]; }
    private static string SimpleTypeName(string identity) { var name = identity[(identity.LastIndexOf('.') + 1)..]; var tick = name.IndexOf('`'); return tick >= 0 ? name[..tick] : name; }
}
