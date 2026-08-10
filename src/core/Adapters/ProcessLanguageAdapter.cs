using System.Text.Json;
using System.Text.Json.Serialization;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Indexing;
using Merkle.Core.Processes;

namespace Merkle.Core.Adapters;

/// <summary>Configuration for an adapter implemented as an external executable.</summary>
public sealed record ProcessLanguageAdapterOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string UnitIdentityVersion = "1",
    string TestIdentityVersion = "1")
{
    public const int MaxProtocolBytes = 16 * 1024 * 1024;
    public const int MaxDiagnosticBytes = 1 * 1024 * 1024;
}

/// <summary>
/// Protocol 1.0 envelope. Payload is deliberately JSON-only so an adapter can be
/// authored in any language without taking a dependency on Merkle.Core.
/// </summary>
public sealed record AdapterProcessRequest(
    string ProtocolVersion,
    string RequestId,
    string Operation,
    JsonElement Payload);

/// <summary>Protocol 1.0 response envelope produced by an external adapter.</summary>
public sealed record AdapterProcessResponse(
    string ProtocolVersion,
    string RequestId,
    string Operation,
    bool Success,
    JsonElement Payload,
    AdapterProcessError? Error = null);

public sealed record AdapterProcessError(string Code, string Message);

/// <summary>Validation shared by the host and protocol-only conformance fixtures.</summary>
public static class AdapterProcessProtocol
{
    public const string Version = "1.0";
    public const int MaxEntries = 100_000;

    public static AdapterProcessResponse ParseResponse(ReadOnlySpan<byte> output, string requestId, string operation)
    {
        try
        {
            var response = JsonSerializer.Deserialize(output, AdapterProcessJsonContext.Default.AdapterProcessResponse)
                ?? throw new AnalysisException("AdapterProtocolMalformed", "The adapter returned an empty JSON response.");
            if (response.ProtocolVersion != Version || response.RequestId != requestId || response.Operation != operation)
            {
                throw new AnalysisException("AdapterProtocolMismatch", "The adapter response does not match the protocol request.");
            }

            if (!response.Success && (response.Error is null || !Bounded(response.Error.Code, 128) || !Bounded(response.Error.Message, 4_096)))
            {
                throw new AnalysisException("AdapterProtocolMalformed", "The adapter returned an invalid error response.");
            }

            if (response.Success && response.Payload.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("AdapterProtocolMalformed", "The adapter returned a successful response without an object payload.");
            }

            return response;
        }
        catch (JsonException error)
        {
            throw new AnalysisException("AdapterProtocolMalformed", "The adapter returned malformed or noisy JSON output.", error);
        }
    }

    public static void ValidateDescriptor(AdapterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ProtocolVersion != Version || !Bounded(descriptor.Language, 64) || !Bounded(descriptor.Producer, 128) ||
            !Bounded(descriptor.AdapterVersion, 128) || !Bounded(descriptor.UnitIdentityVersion, 64) || !Bounded(descriptor.TestIdentityVersion, 64) ||
            descriptor.Capabilities.Count == 0 || descriptor.Capabilities.Count > 32 || descriptor.Profiles.Count > 32 ||
            descriptor.Capabilities.Distinct().Count() != descriptor.Capabilities.Count ||
            descriptor.Profiles.Any(profile => !Bounded(profile, 128)))
        {
            throw new AnalysisException("AdapterDescriptorInvalid", "The adapter descriptor is incomplete, incompatible, or exceeds protocol limits.");
        }
    }

    public static void ValidateIndex(AdapterIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Units.Count > MaxEntries || index.Edges.Count > MaxEntries || index.Tests.Count > MaxEntries ||
            !Ordered(index.Units.Select(unit => unit.Identity)) ||
            !Ordered(index.Edges.Select(edge => $"{edge.SourceIdentity}\u001f{edge.TargetIdentity}\u001f{edge.Kind}")) ||
            !Ordered(index.Tests.Select(test => test.Identity)) ||
            index.Units.Any(unit => !Bounded(unit.Identity, 512) || !Bounded(unit.Path, 4_096) || !Bounded(unit.ContentHash, 256) || !Bounded(unit.SemanticSignature, 4_096)) ||
            index.Edges.Any(edge => !Bounded(edge.SourceIdentity, 512) || !Bounded(edge.TargetIdentity, 512)) ||
            index.Tests.Any(test => !Bounded(test.Identity, 512) || !Bounded(test.DisplayName, 4_096) || !Bounded(test.Framework, 128)))
        {
            throw new AnalysisException("AdapterOutputInvalid", "The adapter index is unordered, malformed, or exceeds protocol limits.");
        }
    }

    public static void ValidateMapping(MappingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RequestedTests.Count > MaxEntries || result.UnmappedUnits.Count > MaxEntries ||
            !Ordered(result.RequestedTests.Select(test => test.Identity)) || !Ordered(result.UnmappedUnits.Select(unit => unit.Identity)) ||
            result.RequestedTests.Any(test => !Bounded(test.Identity, 512) || !Bounded(test.DisplayName, 4_096) || !Bounded(test.Framework, 128)) ||
            result.UnmappedUnits.Any(unit => !Bounded(unit.Identity, 512)))
        {
            throw new AnalysisException("AdapterOutputInvalid", "The adapter mapping is unordered, malformed, or exceeds protocol limits.");
        }
    }

    private static bool Ordered(IEnumerable<string> values) => values.SequenceEqual(values.OrderBy(value => value, StringComparer.Ordinal));
    private static bool Bounded(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
}

/// <summary>Language adapter host for a bounded single-request JSON process protocol.</summary>
public sealed class ProcessLanguageAdapter : ILanguageAdapter
{
    private readonly IProcessRunner _runner;
    private readonly ProcessLanguageAdapterOptions _options;
    private AdapterDescriptor? _descriptor;

    public ProcessLanguageAdapter(IProcessRunner runner, ProcessLanguageAdapterOptions options)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Executable) || string.IsNullOrWhiteSpace(options.WorkingDirectory))
            throw new ArgumentException("An adapter executable and working directory are required.", nameof(options));
    }

    public AdapterDescriptor Describe()
    {
        if (_descriptor is not null) return _descriptor;
        var descriptor = InvokeAsync<AdapterEmptyPayload, AdapterDescriptor>("describe", new AdapterEmptyPayload(), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        AdapterProcessProtocol.ValidateDescriptor(descriptor);
        if (descriptor.UnitIdentityVersion != _options.UnitIdentityVersion || descriptor.TestIdentityVersion != _options.TestIdentityVersion)
            throw new AnalysisException("AdapterIdentityIncompatible", "The adapter uses unsupported unit or test identity versions.");
        return _descriptor = descriptor;
    }

    public async ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCapability(AdapterCapability.Index);
        var index = await InvokeAsync<AdapterIndexRequest, AdapterIndex>("index", request, cancellationToken).ConfigureAwait(false);
        AdapterProcessProtocol.ValidateIndex(index);
        return index;
    }

    public async ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCapability(AdapterCapability.Map);
        var result = await InvokeAsync<AdapterMapRequest, MappingResult>("map", request, cancellationToken).ConfigureAwait(false);
        AdapterProcessProtocol.ValidateMapping(result);
        return result;
    }

    private void RequireCapability(AdapterCapability capability)
    {
        if (!Describe().Capabilities.Contains(capability))
            throw new AnalysisException("AdapterCapabilityUnavailable", $"The adapter does not support '{capability}'.");
    }

    private async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string operation, TRequest payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = Guid.NewGuid().ToString("N");
        var envelope = new AdapterProcessRequest(
            AdapterProcessProtocol.Version,
            requestId,
            operation,
            JsonSerializer.SerializeToElement(payload, AdapterProcessJsonContext.Default.GetTypeInfo(typeof(TRequest))
                ?? throw new AnalysisException("AdapterProtocolUnsupported", $"No protocol serializer is registered for '{typeof(TRequest).Name}'.")));
        var input = JsonSerializer.SerializeToUtf8Bytes(envelope, AdapterProcessJsonContext.Default.AdapterProcessRequest);
        if (input.Length > ProcessLanguageAdapterOptions.MaxProtocolBytes)
            throw new AnalysisException("AdapterRequestLimitExceeded", "The adapter request exceeds the 16 MiB protocol limit.");

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(new ProcessRequest(
                _options.Executable,
                _options.Arguments,
                _options.WorkingDirectory,
                StandardInput: input,
                MaxStandardOutputBytes: ProcessLanguageAdapterOptions.MaxProtocolBytes,
                MaxStandardErrorBytes: ProcessLanguageAdapterOptions.MaxDiagnosticBytes), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (AnalysisException error)
        {
            throw new AnalysisException("AdapterProcessFailed", $"The adapter process could not complete: {Diagnostic(error.Message)}", error);
        }
        catch (Exception error)
        {
            throw new AnalysisException("AdapterProcessLaunchFailed", "The adapter process could not be started.", error);
        }

        if (result.ExitCode != 0)
            throw new AnalysisException("AdapterProcessFailed", $"The adapter exited with code {result.ExitCode}: {Diagnostic(result.StandardError)}");

        var response = AdapterProcessProtocol.ParseResponse(result.OutputBytes.Span, requestId, operation);
        if (!response.Success)
            throw new AnalysisException(response.Error!.Code, response.Error.Message);

        try
        {
            return JsonSerializer.Deserialize(response.Payload, AdapterProcessJsonContext.Default.GetTypeInfo(typeof(TResponse))
                ?? throw new AnalysisException("AdapterProtocolUnsupported", $"No protocol serializer is registered for '{typeof(TResponse).Name}'.")) is TResponse value
                ? value
                : throw new AnalysisException("AdapterProtocolMalformed", "The adapter response payload is empty.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new AnalysisException("AdapterProtocolMalformed", "The adapter response payload has an invalid shape.", error);
        }
    }

    private static string Diagnostic(string value) => value.Length <= 1_024 ? value : value[..1_024];
}

public sealed record AdapterEmptyPayload;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AdapterProcessRequest))]
[JsonSerializable(typeof(AdapterProcessResponse))]
[JsonSerializable(typeof(AdapterDescriptor))]
[JsonSerializable(typeof(AdapterIndexRequest))]
[JsonSerializable(typeof(AdapterMapRequest))]
[JsonSerializable(typeof(AdapterIndex))]
[JsonSerializable(typeof(MappingResult))]
[JsonSerializable(typeof(AdapterEmptyPayload))]
[JsonSerializable(typeof(RepositorySnapshot))]
[JsonSerializable(typeof(SnapshotFile))]
[JsonSerializable(typeof(SnapshotIdentity))]
[JsonSerializable(typeof(SourceUnit))]
[JsonSerializable(typeof(ImpactEdge))]
[JsonSerializable(typeof(TestDescriptor))]
[JsonSerializable(typeof(RequestedTest))]
[JsonSerializable(typeof(ChangedUnit))]
[JsonSerializable(typeof(ImpactReason))]
public sealed partial class AdapterProcessJsonContext : JsonSerializerContext;
