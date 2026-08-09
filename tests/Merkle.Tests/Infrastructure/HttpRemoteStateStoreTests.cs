using System.Net;
using System.Text;
using Merkle.Core.History;
using Merkle.Core.State;
using Merkle.Infrastructure.State;

namespace Merkle.Tests.Infrastructure;

public sealed class HttpRemoteStateStoreTests
{
    [Fact]
    public async Task Publish_SendsBearerCasAndIdempotencyWithoutSourceContent()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        using var client = Client(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK, "{}", "\"v2\"");
        });
        var store = Store(client, "secret-token");

        var version = await store.PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), [new RemoteHistoricalRun("run-1", Run(HistoryProvenance.Local))], "\"v1\"", "attempt-1"), default);

        Assert.Equal("\"v2\"", version);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization.Parameter);
        Assert.Equal("\"v1\"", captured.Headers.GetValues("If-Match").Single());
        Assert.Equal("attempt-1", captured.Headers.GetValues("Idempotency-Key").Single());
        Assert.DoesNotContain("source", capturedBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", capturedBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_UsesCursorAndPreservesProvenance()
    {
        using var client = Client(request =>
        {
            Assert.Contains("cursor=after-1", request.RequestUri!.Query, StringComparison.Ordinal);
            return Response(HttpStatusCode.OK, """{"schema":1,"compatibility":{"repositoryIdentity":"repo","schemaVersion":"1","adapterIdentity":"adapter","buildFingerprintFamily":"build"},"runs":[{"id":"run-1","provenance":"Imported","status":"Succeeded","isCompleteSuite":true,"completedAt":"2026-01-01T00:00:00+00:00","changedUnitIdentities":["unit"],"tests":[]}],"nextCursor":"after-2"}""", "\"v1\"");
        });

        var page = await Store(client, null, anonymous: true).ReadCompatibleTerminalHistoryAsync(new RemoteHistoryRead(Key(), new RemoteHistoryCursor("after-1")), default);

        Assert.Equal("after-2", page.NextCursor!.Value);
        Assert.Equal(HistoryProvenance.Imported, page.Runs.Single().Run.Provenance);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RemoteStateFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, RemoteStateFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Conflict, RemoteStateFailureKind.Concurrency)]
    [InlineData(HttpStatusCode.PreconditionFailed, RemoteStateFailureKind.Concurrency)]
    public async Task Publish_MapsAuthorizationAndConcurrencyFailures(HttpStatusCode status, RemoteStateFailureKind kind)
    {
        using var client = Client(_ => Response(status, "{}", null));
        var error = await Assert.ThrowsAsync<RemoteStateException>(async () => await Store(client, "token").PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), [new RemoteHistoricalRun("run-1", Run(HistoryProvenance.OfficialCi))], "\"v1\"", "attempt"), default));
        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] collection = ["MalformedRemoteResponse", "IncompatibleRemoteSchema", "PayloadTooLarge"];

    [Fact]
    public async Task Read_RejectsMalformedIncompatibleAndOversizeResponses()
    {
        foreach (var content in new[] { "not-json", "{\"schema\":2,\"runs\":[]}", new string('x', 16 * 1024 * 1024 + 1) })
        {
            using var client = Client(_ => Response(HttpStatusCode.OK, content, "\"v1\""));
            var error = await Assert.ThrowsAsync<RemoteStateException>(async () => await Store(client, null, anonymous: true).ReadCompatibleTerminalHistoryAsync(new RemoteHistoryRead(Key()), default));
            Assert.Contains(error.Code, collection);
        }
    }

    [Fact]
    public async Task Publish_RequiresTokenAndTerminalUniqueRecords()
    {
        using var client = Client(_ => Response(HttpStatusCode.OK, "{}", "\"v1\""));
        var store = Store(client, null);
        await Assert.ThrowsAsync<RemoteStateException>(async () => await store.PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), [new RemoteHistoricalRun("run-1", Run(HistoryProvenance.Local))], "\"v0\"", "key"), default));
        await Assert.ThrowsAsync<RemoteStateException>(async () => await Store(client, "token").PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), [new RemoteHistoricalRun("run-1", Run(HistoryProvenance.Local, HistoryRunStatus.Interrupted))], "\"v0\"", "key"), default));
    }

    [Fact]
    public void Options_RejectCredentialAndNonHttpsEndpoints()
    {
        Assert.Throws<ArgumentException>(() => new HttpRemoteStateStoreOptions(new Uri("http://example.test/history")));
        Assert.Throws<ArgumentException>(() => new HttpRemoteStateStoreOptions(new Uri("https://token@example.test/history")));
        _ = new HttpRemoteStateStoreOptions(new Uri("http://localhost/history"), allowInsecureLocalhostForTests: true);
    }

    [Fact]
    public async Task Read_PropagatesCancellation()
    {
        using var client = Client(_ => throw new OperationCanceledException());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store(client, null, anonymous: true).ReadCompatibleTerminalHistoryAsync(new RemoteHistoryRead(Key()), cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, RemoteStateFailureKind.Configuration)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteStateFailureKind.Analysis)]
    public async Task Read_MapsRejectedAndServerResponses(HttpStatusCode status, RemoteStateFailureKind kind)
    {
        using var client = Client(_ => Response(status, "{}", null));

        var error = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await Store(client, null, anonymous: true).ReadCompatibleTerminalHistoryAsync(new RemoteHistoryRead(Key()), default));

        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public async Task ReadAndPublish_RequireVersionEtags()
    {
        using var client = Client(_ => Response(HttpStatusCode.OK,
            """{"schema":1,"compatibility":{"repositoryIdentity":"repo","schemaVersion":"1","adapterIdentity":"adapter","buildFingerprintFamily":"build"},"runs":[]}""", null));
        var store = Store(client, "token", anonymous: true);

        var readError = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await store.ReadCompatibleTerminalHistoryAsync(new RemoteHistoryRead(Key()), default));
        var publishError = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await store.PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), [new RemoteHistoricalRun("id", Run(HistoryProvenance.Local))], "\"old\"", "attempt"), default));

        Assert.Equal("MissingRemoteVersion", readError.Code);
        Assert.Equal("MissingRemoteVersion", publishError.Code);
    }

    [Fact]
    public async Task Publish_RejectsDuplicateAndNonterminalRecordsBeforeNetwork()
    {
        using var client = Client(_ => throw new InvalidOperationException("network must not be used"));
        var store = Store(client, "token");
        RemoteHistoricalRun[] records = [new RemoteHistoricalRun("duplicate", Run(HistoryProvenance.Local)), new RemoteHistoricalRun("duplicate", Run(HistoryProvenance.Local))];

        var error = await Assert.ThrowsAsync<RemoteStateException>(async () =>
            await store.PublishTerminalHistoryAsync(new RemoteHistoryPublication(Key(), records, "\"old\"", "attempt"), default));

        Assert.Equal("DuplicateRemoteId", error.Code);
    }

    private static HttpRemoteStateStore Store(HttpClient client, string? token, bool anonymous = false) => new(client, new Token(token), new HttpRemoteStateStoreOptions(new Uri("https://history.example/"), anonymous));
    private static HistoryCompatibilityKey Key() => new("repo", "1", "adapter", "build");
    private static HistoricalRun Run(HistoryProvenance provenance, HistoryRunStatus status = HistoryRunStatus.Succeeded) => new(Key(), provenance, status, true, DateTimeOffset.UtcNow, ["unit"], []);
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private static HttpResponseMessage Response(HttpStatusCode status, string text, string? etag)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private sealed class Token(string? value) : IRemoteStateTokenSource { public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(value); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
}
