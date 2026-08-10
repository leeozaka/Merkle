using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Merkle.Core.History;
using Merkle.Core.Errors;
using Merkle.Core.State;

namespace Merkle.Infrastructure.State;

public sealed record HttpRemoteStateStoreOptions
{
    public Uri Endpoint { get; }
    public bool AllowAnonymousReads { get; }
    public bool AllowInsecureLocalhostForTests { get; }
    public HttpRemoteStateStoreOptions(Uri endpoint, bool allowAnonymousReads = false, bool allowInsecureLocalhostForTests = false)
    {
        Endpoint = ValidateEndpoint(endpoint, allowInsecureLocalhostForTests);
        AllowAnonymousReads = allowAnonymousReads;
        AllowInsecureLocalhostForTests = allowInsecureLocalhostForTests;
    }

    private static Uri ValidateEndpoint(Uri endpoint, bool allowInsecureLocalhostForTests)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !(allowInsecureLocalhostForTests && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new ArgumentException("Remote history endpoints must be HTTPS, credential-free absolute URIs (HTTP loopback is test-only).", nameof(endpoint));
        }

        return endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
    }
}

public enum RemoteStateFailureKind { Authentication, Concurrency, Configuration, Analysis }

public sealed class RemoteStateException(RemoteStateFailureKind kind, string code, string message, Exception? innerException = null)
    : MerkleException(
        kind is RemoteStateFailureKind.Authentication or RemoteStateFailureKind.Configuration
            ? Merkle.Core.Domain.ErrorClass.ConfigurationError
            : Merkle.Core.Domain.ErrorClass.AnalysisError,
        code,
        message,
        innerException)
{
    public RemoteStateFailureKind Kind { get; } = kind;
}

/// <summary>HTTP adapter for a team-owned history endpoint; it never transmits source or environment content.</summary>
public sealed class HttpRemoteStateStore(HttpClient client, IRemoteStateTokenSource tokenSource, HttpRemoteStateStoreOptions options) : IRemoteStateStore
{
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private const string ProtocolVersion = "1";

    public async ValueTask<RemoteHistoryPage> ReadCompatibleTerminalHistoryAsync(RemoteHistoryRead read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("history", ToQuery(read)));
        await AddTokenAsync(request, allowBlank: options.AllowAnonymousReads, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await ReadPayloadAsync(response, cancellationToken).ConfigureAwait(false);
        return ToPage(payload, read.Compatibility, RequiredEtag(response));
    }

    public async ValueTask<string> PublishTerminalHistoryAsync(RemoteHistoryPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ValidatePublication(publication);
        var body = new RemotePublishDto { Compatibility = ToDto(publication.Compatibility), Runs = [.. publication.Runs.Select(ToDto)] };
        var json = JsonSerializer.Serialize(body, RemoteStateJsonContext.Default.RemotePublishDto);
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw Failure("PayloadTooLarge", "Remote history publication exceeds 16 MiB.");
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("history", null)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", publication.ExpectedVersion);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", publication.IdempotencyKey);
        await AddTokenAsync(request, allowBlank: false, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await DrainBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        return RequiredEtag(response);
    }

    private async Task AddTokenAsync(HttpRequestMessage request, bool allowBlank, CancellationToken cancellationToken)
    {
        var token = await tokenSource.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            if (allowBlank) return;
            throw new RemoteStateException(RemoteStateFailureKind.Authentication, "MissingRemoteCredential", "A remote history write requires an authentication token.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Merkle-Protocol-Version", ProtocolVersion);
        request.Headers.TryAddWithoutValidation("X-Merkle-History-Schema", "1");
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                throw ToFailure(response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RemoteStateException) { throw; }
        catch (HttpRequestException exception) { throw new RemoteStateException(RemoteStateFailureKind.Analysis, "RemoteTransportFailure", "Remote history transport failed.", exception); }
    }

    private static RemoteStateException ToFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(RemoteStateFailureKind.Authentication, "RemoteAuthorizationFailed", "Remote history authorization failed."),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => new(RemoteStateFailureKind.Concurrency, "RemoteConcurrencyConflict", "Remote history version conflict."),
        >= HttpStatusCode.InternalServerError => new(RemoteStateFailureKind.Analysis, "RemoteServerFailure", "Remote history server failed."),
        _ => new(RemoteStateFailureKind.Configuration, "RemoteRequestRejected", "Remote history request was rejected.")
    };

    private static async Task<RemoteReadDto> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var bounded = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize(bounded.ToArray(), RemoteStateJsonContext.Default.RemoteReadDto) ?? throw Failure("MalformedRemoteResponse", "Remote history response is empty."); }
        catch (JsonException exception) { throw new RemoteStateException(RemoteStateFailureKind.Analysis, "MalformedRemoteResponse", "Remote history response is malformed.", exception); }
    }

    private static async Task DrainBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var bounded = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MemoryStream> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumPayloadBytes) throw Failure("PayloadTooLarge", "Remote history response exceeds 16 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bounded.Length + read > MaximumPayloadBytes) throw Failure("PayloadTooLarge", "Remote history response exceeds 16 MiB.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return bounded;
    }

    private static RemoteHistoryPage ToPage(RemoteReadDto payload, HistoryCompatibilityKey requested, string version)
    {
        try
        {
            if (payload.Schema != 1 || payload.Compatibility is null || !requested.Matches(FromDto(payload.Compatibility))) throw Failure("IncompatibleRemoteSchema", "Remote history response is not compatible with this request.");
            var compatibility = FromDto(payload.Compatibility);
            var runs = (payload.Runs ?? []).Select(run => FromDto(run, compatibility)).ToArray();
            ValidateRuns(runs);
            return new RemoteHistoryPage(runs, string.IsNullOrWhiteSpace(payload.NextCursor) ? null : new RemoteHistoryCursor(payload.NextCursor), version);
        }
        catch (RemoteStateException) { throw; }
        catch (ArgumentException exception) { throw new RemoteStateException(RemoteStateFailureKind.Analysis, "MalformedRemoteResponse", "Remote history response contains invalid values.", exception); }
    }

    private static void ValidatePublication(RemoteHistoryPublication publication)
    {
        if (publication.Runs.Count is < 1 or > 1_000) throw Failure("InvalidPublication", "Remote publications must contain between 1 and 1000 runs.");
        ValidateRuns(publication.Runs);
    }

    private static void ValidateRuns(IReadOnlyList<RemoteHistoricalRun> runs)
    {
        if (runs.Select(run => run.Id).Distinct(StringComparer.Ordinal).Count() != runs.Count) throw Failure("DuplicateRemoteId", "Remote history run IDs must be unique.");
        foreach (var record in runs)
        {
            if (record.Run.Status is HistoryRunStatus.Interrupted or HistoryRunStatus.InProgress) throw Failure("NonTerminalHistory", "Only terminal history runs may be shared remotely.");
            if (record.Run.ChangedUnitIdentities.Distinct(StringComparer.Ordinal).Count() != record.Run.ChangedUnitIdentities.Count || record.Run.Tests.Select(test => test.TestIdentity).Distinct(StringComparer.Ordinal).Count() != record.Run.Tests.Count) throw Failure("DuplicateHistoryIdentity", "Remote history records cannot contain duplicate identities.");
        }
    }

    private Uri BuildUri(string path, string? query) => new(options.Endpoint, path + (query is null ? string.Empty : "?" + query));
    private static string ToQuery(RemoteHistoryRead read) => string.Join("&", new[] { ("repositoryIdentity", read.Compatibility.RepositoryIdentity), ("schemaVersion", read.Compatibility.SchemaVersion), ("adapterIdentity", read.Compatibility.AdapterIdentity), ("buildFingerprintFamily", read.Compatibility.BuildFingerprintFamily), ("cursor", read.Cursor?.Value), ("limit", read.MaximumRuns.ToString(System.Globalization.CultureInfo.InvariantCulture)) }.Where(pair => pair.Item2 is not null).Select(pair => Uri.EscapeDataString(pair.Item1) + "=" + Uri.EscapeDataString(pair.Item2!)));
    private static string RequiredEtag(HttpResponseMessage response) => response.Headers.ETag?.Tag is { Length: > 0 } tag ? tag : throw Failure("MissingRemoteVersion", "Remote history response did not provide a valid ETag.");
    private static RemoteStateException Failure(string code, string message) => new(RemoteStateFailureKind.Analysis, code, message);
    private static RemoteCompatibilityDto ToDto(HistoryCompatibilityKey value) => new() { RepositoryIdentity = value.RepositoryIdentity, SchemaVersion = value.SchemaVersion, AdapterIdentity = value.AdapterIdentity, BuildFingerprintFamily = value.BuildFingerprintFamily };
    private static HistoryCompatibilityKey FromDto(RemoteCompatibilityDto value) => new(value.RepositoryIdentity ?? throw Failure("MalformedRemoteResponse", "Remote compatibility is missing repository identity."), value.SchemaVersion ?? throw Failure("MalformedRemoteResponse", "Remote compatibility is missing schema version."), value.AdapterIdentity ?? throw Failure("MalformedRemoteResponse", "Remote compatibility is missing adapter identity."), value.BuildFingerprintFamily ?? throw Failure("MalformedRemoteResponse", "Remote compatibility is missing build fingerprint family."));
    private static RemoteRunDto ToDto(RemoteHistoricalRun value) => new() { Id = value.Id, Provenance = value.Run.Provenance, Status = value.Run.Status, IsCompleteSuite = value.Run.IsCompleteSuite, CompletedAt = value.Run.CompletedAt, ChangedUnitIdentities = [.. value.Run.ChangedUnitIdentities], Tests = [.. value.Run.Tests.Select(test => new RemoteTestDto { TestIdentity = test.TestIdentity, Executed = test.Executed, Outcome = test.Outcome, DurationMs = test.DurationMs, ObservedUnitIdentities = [.. test.ObservedUnitIdentities] })] };
    private static RemoteHistoricalRun FromDto(RemoteRunDto value, HistoryCompatibilityKey compatibility) => new(value.Id ?? throw Failure("MalformedRemoteResponse", "Remote history record ID is missing."), new HistoricalRun(compatibility, value.Provenance, value.Status, value.IsCompleteSuite, value.CompletedAt, value.ChangedUnitIdentities, [.. (value.Tests ?? []).Select(test => new HistoricalTestExecution(test.TestIdentity ?? throw Failure("MalformedRemoteResponse", "Remote test identity is missing."), test.Executed, test.Outcome, test.DurationMs, test.ObservedUnitIdentities))]));
}
