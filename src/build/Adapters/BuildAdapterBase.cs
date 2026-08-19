using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Merkle.Core.Processes;

namespace Merkle.Build;

internal abstract class BuildAdapterBase : IBuildAdapter
{
    private string? _logPath;

    protected BuildAdapterBase(IProcessRunner processRunner)
    {
        Runner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    protected IProcessRunner Runner { get; }

    public abstract AdapterBuildDefinition Definition { get; }

    public abstract ValueTask<AdapterReadiness> PreflightAsync(
        BuildContext context,
        CancellationToken cancellationToken);

    public abstract ValueTask<AdapterBuildResult> BuildAsync(
        AdapterBuildRequest request,
        CancellationToken cancellationToken);

    protected void BeginBuildLog(BuildContext context, AdapterReadiness readiness)
    {
        var directory = Path.Combine(context.RunDirectory, "logs");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, Definition.Id + ".log");
        File.WriteAllLines(
            _logPath,
            [
                $"adapter: {Definition.Id}",
                $"readiness: {readiness.Status.ToString().ToLowerInvariant()}",
                $"detected-version: {readiness.DetectedVersion ?? "unknown"}"
            ]);
    }

    protected async ValueTask<ProcessResult?> TryRunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        ReadOnlyMemory<byte>? standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendLogAsync(
                $"\n$ {fileName} {string.Join(' ', arguments)}\nworking-directory: {workingDirectory}\n",
                cancellationToken).ConfigureAwait(false);
            var result = await Runner.RunAsync(
                new ProcessRequest(
                    fileName,
                    arguments,
                    workingDirectory,
                    environment,
                    standardInput),
                cancellationToken).ConfigureAwait(false);
            await AppendLogAsync(
                $"exit-code: {result.ExitCode}\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}\n",
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException)
        {
            await AppendLogAsync($"process-start-error: {error.Message}\n", cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async ValueTask AppendLogAsync(string value, CancellationToken cancellationToken)
    {
        if (_logPath is not null)
        {
            await File.AppendAllTextAsync(_logPath, value, cancellationToken).ConfigureAwait(false);
        }
    }

    protected static AdapterReadiness Unavailable(
        string id,
        string reason,
        string requiredTool,
        string? detectedVersion = null) =>
        new(id, AdapterReadinessStatus.Unavailable, reason, requiredTool, detectedVersion);

    protected static AdapterBuildResult Skipped(AdapterBuildDefinition definition, AdapterReadiness readiness) =>
        new(
            definition.Id,
            AdapterBuildStatus.Skipped,
            [],
            readiness.Reason ?? "The adapter toolchain is unavailable.",
            RequiredTool: readiness.RequiredTool,
            DetectedVersion: readiness.DetectedVersion);

    protected static AdapterBuildResult Failed(AdapterBuildDefinition definition, string diagnostic) =>
        new(definition.Id, AdapterBuildStatus.Failed, [], diagnostic);

    protected static string Diagnostic(ProcessResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();

    protected static bool IsSuccessfulSmoke(ProcessResult? result, string language)
    {
        if (result is null || result.ExitCode != 0)
        {
            return false;
        }

        var output = result.StandardOutput.Replace(" ", string.Empty, StringComparison.Ordinal);
        return output.Contains("\"protocolVersion\":\"1.0\"", StringComparison.Ordinal) &&
               output.Contains($"\"language\":\"{language}\"", StringComparison.Ordinal);
    }

    protected static bool IsSuccessfulDotNetSmoke(ProcessResult? result)
    {
        if (result is null || result.ExitCode != 0) return false;
        var output = result.StandardOutput.Replace(" ", string.Empty, StringComparison.Ordinal);
        return output.Contains("\"protocolVersion\":\"1.0\"", StringComparison.Ordinal) &&
               output.Contains("\"success\":true", StringComparison.Ordinal);
    }

    protected static AdapterBuildArtifact Artifact(
        AdapterBuildDefinition definition,
        string absolutePath,
        string relativePath,
        string profile = "minimal")
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"The adapter did not produce '{relativePath}'.", absolutePath);
        }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolutePath))).ToLowerInvariant();
        return new AdapterBuildArtifact(
            definition.Id,
            relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            hash,
            definition.Version,
            "1.0",
            profile);
    }

    protected static string ReadRequired(string path) => File.ReadAllText(path, Encoding.UTF8);

    protected static int? MajorVersion(string value)
    {
        var digits = new string(value.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var version) ? version : null;
    }

    protected static Version? ParseVersion(string value)
    {
        var match = Regex.Match(value, @"(?<!\d)(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+][0-9A-Za-z.-]+)?");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major)) return null;
        var minor = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var parsedMinor) ? parsedMinor : 0;
        var patch = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var parsedPatch) ? parsedPatch : 0;
        return new Version(major, minor, patch);
    }

    protected static bool IsAtLeast(Version? detected, Version required) => detected is not null && detected >= required;

    protected static (string GoOs, string GoArch) Target(string? runtimeIdentifier)
    {
        var rid = runtimeIdentifier ??
                  (OperatingSystem.IsMacOS()
                      ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                      : "linux-x64");
        return rid switch
        {
            "linux-x64" => ("linux", "amd64"),
            "linux-arm64" => ("linux", "arm64"),
            "osx-x64" => ("darwin", "amd64"),
            "osx-arm64" => ("darwin", "arm64"),
            _ => throw new ArgumentException($"Unsupported runtime identifier '{rid}'.", nameof(runtimeIdentifier))
        };
    }

    protected static string ProtocolRequest() =>
        "{\"protocolVersion\":\"1.0\",\"requestId\":\"build-smoke\",\"operation\":\"describe\",\"payload\":{}}";

    protected static async ValueTask<AdapterBuildResult> CompleteAsync(
        AdapterBuildDefinition definition,
        BuildContext context,
        IReadOnlyList<(string Path, string RelativePath, string Profile)> artifacts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = artifacts
            .Select(item => Artifact(definition, item.Path, item.RelativePath, item.Profile))
            .ToArray();
        await Task.CompletedTask.ConfigureAwait(false);
        return new AdapterBuildResult(definition.Id, AdapterBuildStatus.Built, values);
    }
}
