using System.Text;
using Merkle.Core.Processes;

namespace Merkle.Build;

internal sealed class JavaBuildAdapter : BuildAdapterBase
{
    public JavaBuildAdapter(IProcessRunner processRunner) : base(processRunner) { }

    public override AdapterBuildDefinition Definition { get; } = new(
        "java",
        [],
        "1.0.0",
        ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);

    public override async ValueTask<AdapterReadiness> PreflightAsync(BuildContext context, CancellationToken cancellationToken)
    {
        var pom = ReadRequired(Path.Combine(context.RepositoryRoot, "src", "adapters", "java", "pom.xml"));
        var sourceOffset = pom.IndexOf("maven.compiler.source", StringComparison.Ordinal);
        var source = sourceOffset >= 0 ? ParseVersion(pom[sourceOffset..]) ?? new Version(17, 0) : new Version(17, 0);
        var java = await TryRunAsync("java", ["--version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        if (java is null) return Unavailable(Definition.Id, "A JDK is not available.", "java");
        var detected = (java.StandardOutput + java.StandardError).Trim();
        var version = ParseVersion(detected);
        if (java.ExitCode != 0 || !IsAtLeast(version, source)) return Unavailable(Definition.Id, $"JDK {source}+ is required.", "java", detected);
        var compiler = await TryRunAsync("javac", ["--version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        if (compiler is null || compiler.ExitCode != 0)
        {
            return Unavailable(Definition.Id, "The Java compiler is not available; install a JDK rather than a JRE.", "javac", detected);
        }
        var maven = await TryRunAsync("mvn", ["--version"], context.RepositoryRoot, null, null, cancellationToken).ConfigureAwait(false);
        return maven is { ExitCode: 0 }
            ? new AdapterReadiness(Definition.Id, AdapterReadinessStatus.Ready, DetectedVersion: detected)
            : Unavailable(Definition.Id, "Maven is not available.", "mvn");
    }

    public override async ValueTask<AdapterBuildResult> BuildAsync(AdapterBuildRequest request, CancellationToken cancellationToken)
    {
        BeginBuildLog(request.Context, request.Readiness);
        if (request.Readiness.Status != AdapterReadinessStatus.Ready) return Skipped(Definition, request.Readiness);
        var source = Path.Combine(request.Context.RepositoryRoot, "src", "adapters", "java");
        var destination = Path.Combine(request.Context.StagingDirectory, "workers", Definition.Id);
        Directory.CreateDirectory(destination);
        var arguments = new List<string> { "-q", "package" };
        if (!request.RunTests) arguments.Add("-DskipTests");
        arguments.Add($"-Dmerkle.build.directory={destination}");
        var build = await TryRunAsync("mvn", arguments, source, null, null, cancellationToken).ConfigureAwait(false);
        if (build is null || build.ExitCode != 0) return Failed(Definition, build is null ? "Maven could not be started." : Diagnostic(build));
        var jar = NormalizeJar(destination);
        if (jar is null)
        {
            return Failed(Definition, "Maven did not produce the expected Java adapter JAR.");
        }

        var smoke = await TryRunAsync("java", ["-jar", jar], source, null, Encoding.UTF8.GetBytes(ProtocolRequest()), cancellationToken).ConfigureAwait(false);
        if (!IsSuccessfulSmoke(smoke, "java"))
        {
            var details = smoke is null ? "The Java process could not be started." : Diagnostic(smoke);
            return Failed(Definition, $"The Java adapter failed the Protocol 1.0 describe smoke check: {details}");
        }

        return await CompleteAsync(Definition, request.Context, [(jar, $"workers/{Definition.Id}/{Path.GetFileName(jar)}", "minimal")], cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeJar(string destination)
    {
        var canonical = Path.Combine(destination, "merkle-adapter-java.jar");
        if (File.Exists(canonical)) return canonical;
        var candidates = Directory.EnumerateFiles(destination, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("original-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length != 1) return null;
        File.Move(candidates[0], canonical);
        return canonical;
    }
}
