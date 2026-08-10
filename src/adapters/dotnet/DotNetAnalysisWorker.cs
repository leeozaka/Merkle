using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Indexing;
using Merkle.Core.Processes;

namespace Merkle.Adapters.DotNet;

/// <summary>Single-request seam for managed .NET source analysis.</summary>
public interface IDotNetAnalysisWorker
{
    ValueTask<AdapterIndex> AnalyzeAsync(AdapterIndexRequest request, CancellationToken cancellationToken);
}

/// <summary>Deterministic test adapter for callers that need to isolate the process seam.</summary>
public sealed class DeterministicDotNetAnalysisWorker(Func<AdapterIndexRequest, AdapterIndex> analyze) : IDotNetAnalysisWorker
{
    private readonly Func<AdapterIndexRequest, AdapterIndex> _analyze = analyze ?? throw new ArgumentNullException(nameof(analyze));

    public ValueTask<AdapterIndex> AnalyzeAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_analyze(request));
    }
}

/// <summary>Runs the non-AOT Roslyn worker using a bounded, versioned JSON protocol.</summary>
public sealed class DotNetProcessAnalysisWorker(IProcessRunner processRunner, string workerAssemblyPath, string dotnetPath = "dotnet") : IDotNetAnalysisWorker
{
    private const int MaxRequestBytes = 16 * 1024 * 1024;
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly string _workerAssemblyPath = string.IsNullOrWhiteSpace(workerAssemblyPath)
            ? throw new ArgumentException("A worker assembly path is required.", nameof(workerAssemblyPath))
            : workerAssemblyPath;
    private readonly string _dotnetPath = string.IsNullOrWhiteSpace(dotnetPath)
            ? throw new ArgumentException("A dotnet executable path is required.", nameof(dotnetPath))
            : dotnetPath;

    public async ValueTask<AdapterIndex> AnalyzeAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = DotNetWorkerRequest.From(request, requestId);
        var input = JsonSerializer.SerializeToUtf8Bytes(envelope, DotNetWorkerJsonContext.Default.DotNetWorkerRequest);
        if (input.Length > MaxRequestBytes)
        {
            throw new AnalysisException("WorkerRequestLimitExceeded", "The .NET analysis request exceeds the 16 MiB protocol limit.");
        }

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(new ProcessRequest(
                _dotnetPath,
                [_workerAssemblyPath],
                request.Snapshot.RepositoryRoot,
                StandardInput: input,
                MaxStandardOutputBytes: 32 * 1024 * 1024,
                MaxStandardErrorBytes: 1 * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new AnalysisException("DotNetWorkerLaunchFailed", "The .NET semantic worker could not be started.", error);
        }

        DotNetWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(result.OutputBytes.Span, DotNetWorkerJsonContext.Default.DotNetWorkerResponse);
        }
        catch (JsonException error)
        {
            throw new AnalysisException("WorkerProtocolMalformed", "The .NET semantic worker returned invalid JSON.", error);
        }

        if (response is null || response.ProtocolVersion != "1.0" || response.RequestId != requestId)
        {
            throw new AnalysisException("WorkerProtocolMismatch", "The .NET semantic worker returned an invalid protocol envelope.");
        }

        if (!response.Success)
        {
            var error = response.Error;
            throw new AnalysisException(error?.Code ?? "DotNetWorkerError", error?.Message ?? "The .NET semantic worker reported an unknown error.");
        }

        if (result.ExitCode != 0)
        {
            throw new AnalysisException(
                "DotNetWorkerFailed",
                $"The .NET semantic worker exited with code {result.ExitCode}: {BoundedDiagnostic(result.StandardError)}");
        }

        return new AdapterIndex(
            response.Units ?? [],
            response.Edges ?? [],
            response.Tests ?? [],
            response.Warnings ?? []);
    }

    private static string BoundedDiagnostic(string value) => value.Length <= 1_024 ? value : value[..1_024];
}

public sealed record DotNetWorkerRequest(
    string ProtocolVersion,
    string RequestId,
    string RepositoryRoot,
    string? ConfiguredSolution,
    IReadOnlyList<DotNetWorkerFile> Files)
{
    public static DotNetWorkerRequest From(AdapterIndexRequest request, string requestId) => new(
        "1.0",
        requestId,
        request.Snapshot.RepositoryRoot,
        request.ConfiguredSolution,
        [.. request.Snapshot.Files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => new DotNetWorkerFile(file.Path, file.ContentHash, Encoding.UTF8.GetString(file.Content.Span)))]);
}

public sealed record DotNetWorkerFile(string Path, string ContentHash, string Content);

public sealed record DotNetWorkerError(string Code, string Message);

public sealed record DotNetWorkerResponse(
    string ProtocolVersion,
    string RequestId,
    bool Success,
    IReadOnlyList<SourceUnit>? Units,
    IReadOnlyList<ImpactEdge>? Edges,
    IReadOnlyList<TestDescriptor>? Tests,
    IReadOnlyList<string>? Warnings,
    DotNetWorkerError? Error)
{
    public static DotNetWorkerResponse Failure(string requestId, string code, string message) =>
        new("1.0", requestId, false, null, null, null, null, new DotNetWorkerError(code, message));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DotNetWorkerRequest))]
[JsonSerializable(typeof(DotNetWorkerResponse))]
[JsonSerializable(typeof(SourceUnit))]
[JsonSerializable(typeof(ImpactEdge))]
[JsonSerializable(typeof(TestDescriptor))]
public sealed partial class DotNetWorkerJsonContext : JsonSerializerContext;
