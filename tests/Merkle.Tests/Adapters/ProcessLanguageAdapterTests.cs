using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;
using Merkle.Core.Indexing;
using Merkle.Core.Processes;

namespace Merkle.Tests.Adapters;

public sealed class ProcessLanguageAdapterTests
{
    [Theory]
    [InlineData("0.9", "request-1", "describe", "AdapterProtocolMismatch")]
    [InlineData("1.0", "other", "describe", "AdapterProtocolMismatch")]
    [InlineData("1.0", "request-1", "index", "AdapterProtocolMismatch")]
    public void Protocol_RejectsEnvelopeThatDoesNotMatchRequest(string version, string requestId, string operation, string code)
    {
        var response = new AdapterProcessResponse(version, requestId, operation, true,
            JsonSerializer.SerializeToElement(new AdapterEmptyPayload(), AdapterProcessJsonContext.Default.AdapterEmptyPayload));

        var error = Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ParseResponse(
            JsonSerializer.SerializeToUtf8Bytes(response, AdapterProcessJsonContext.Default.AdapterProcessResponse), "request-1", "describe"));

        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData(null, "message")]
    [InlineData("", "message")]
    [InlineData("code", "")]
    public void Protocol_RejectsInvalidRemoteError(string? code, string message)
    {
        var response = new AdapterProcessResponse("1.0", "request-1", "describe", false, EmptyPayload(), new AdapterProcessError(code!, message));

        var error = Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ParseResponse(
            JsonSerializer.SerializeToUtf8Bytes(response, AdapterProcessJsonContext.Default.AdapterProcessResponse), "request-1", "describe"));

        Assert.Equal("AdapterProtocolMalformed", error.Code);
    }

    [Fact]
    public void Protocol_RejectsMalformedJsonAndOverlongRemoteError()
    {
        var malformed = Assert.Throws<AnalysisException>(() =>
            AdapterProcessProtocol.ParseResponse("{"u8, "request-1", "describe"));
        Assert.Equal("AdapterProtocolMalformed", malformed.Code);

        var response = new AdapterProcessResponse("1.0", "request-1", "describe", false, EmptyPayload(),
            new AdapterProcessError("code", new string('x', 4_097)));
        var oversized = Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ParseResponse(
            JsonSerializer.SerializeToUtf8Bytes(response, AdapterProcessJsonContext.Default.AdapterProcessResponse), "request-1", "describe"));
        Assert.Equal("AdapterProtocolMalformed", oversized.Code);
    }

    [Fact]
    public async Task Index_UsesProtocolOnlyJsonAndValidatesDeterministicOutput()
    {
        var runner = new FixtureRunner(request =>
        {
            var sent = JsonSerializer.Deserialize(request.StandardInput!.Value.Span, AdapterProcessJsonContext.Default.AdapterProcessRequest)!;
            return sent.Operation switch
            {
                "describe" => Json(sent, Descriptor()),
                "index" => Json(sent, new AdapterIndex(
                    [new SourceUnit("example:file:a", SourceUnitKind.File, "a", "hash", "sig")], [],
                    [new TestDescriptor("example:test:a", "a", "fixture")], [])),
                _ => throw new InvalidOperationException(sent.Operation)
            };
        });
        var adapter = Adapter(runner);

        var index = await adapter.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default);

        Assert.Single(index.Units);
        Assert.Equal(["describe", "index"], runner.Operations);
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(ProcessLanguageAdapterOptions.MaxProtocolBytes, request.MaxStandardOutputBytes);
            Assert.Equal(ProcessLanguageAdapterOptions.MaxDiagnosticBytes, request.MaxStandardErrorBytes);
        });
        Assert.Equal("adapter-fixture", runner.Requests.First().FileName);
    }

    [Fact]
    public void ConformanceFixture_ParsesJsonProducedWithoutMerkleTypes()
    {
        const string fixture = """
            {"protocolVersion":"1.0","requestId":"request-1","operation":"describe","success":true,"payload":{"protocolVersion":"1.0","language":"fixture","producer":"example","adapterVersion":"1","unitIdentityVersion":"1","testIdentityVersion":"1","capabilities":["detect","index","map"],"profiles":["minimal"]}}
            """;

        var response = AdapterProcessProtocol.ParseResponse(Encoding.UTF8.GetBytes(fixture), "request-1", "describe");
        var descriptor = JsonSerializer.Deserialize(response.Payload, AdapterProcessJsonContext.Default.AdapterDescriptor)!;

        AdapterProcessProtocol.ValidateDescriptor(descriptor);
        Assert.Equal("fixture", descriptor.Language);
    }

    [Fact]
    public void ConformanceFixture_RejectsSuccessfulResponseWithoutObjectPayload()
    {
        const string fixture = """
            {"protocolVersion":"1.0","requestId":"request-1","operation":"index","success":true}
            """;

        var error = Assert.Throws<AnalysisException>(() =>
            AdapterProcessProtocol.ParseResponse(Encoding.UTF8.GetBytes(fixture), "request-1", "index"));

        Assert.Equal("AdapterProtocolMalformed", error.Code);
    }

    [Fact]
    public async Task Index_MapsNoisyOutputExitAndUnorderedPayloadToTypedFailures()
    {
        var noisy = Adapter(new FixtureRunner(_ => new ProcessResult(0, "log before json", string.Empty)));
        var malformed = await Assert.ThrowsAsync<AnalysisException>(() => noisy.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("AdapterProtocolMalformed", malformed.Code);

        var exited = Adapter(new FixtureRunner(_ => new ProcessResult(12, string.Empty, new string('e', 2_000))));
        var failed = await Assert.ThrowsAsync<AnalysisException>(() => exited.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("AdapterProcessFailed", failed.Code);
        Assert.True(failed.Message.Length < 1_200);

        var unordered = Adapter(new FixtureRunner(request =>
        {
            var sent = JsonSerializer.Deserialize(request.StandardInput!.Value.Span, AdapterProcessJsonContext.Default.AdapterProcessRequest)!;
            return sent.Operation == "describe"
                ? Json(sent, Descriptor())
                : Json(sent, new AdapterIndex(
                    [new SourceUnit("z", SourceUnitKind.File, "z", "hash", "sig"), new SourceUnit("a", SourceUnitKind.File, "a", "hash", "sig")], [], [], []));
        }));
        var invalid = await Assert.ThrowsAsync<AnalysisException>(() => unordered.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("AdapterOutputInvalid", invalid.Code);
    }

    [Fact]
    public async Task Adapter_ValidatesDescriptorCapabilitiesIdentityAndCachesSuccessfulDescribe()
    {
        var noMap = Adapter(new FixtureRunner(request => Json(Request(request), Descriptor() with { Capabilities = [AdapterCapability.Index] })));
        var unavailable = await Assert.ThrowsAsync<AnalysisException>(() => noMap.MapAsync(MapRequest(), default).AsTask());
        Assert.Equal("AdapterCapabilityUnavailable", unavailable.Code);

        var incompatible = Adapter(new FixtureRunner(request => Json(Request(request), Descriptor() with { UnitIdentityVersion = "2" })));
        var identity = Assert.Throws<AnalysisException>(() => incompatible.Describe());
        Assert.Equal("AdapterIdentityIncompatible", identity.Code);

        var calls = 0;
        var cached = Adapter(new FixtureRunner(request =>
        {
            calls++;
            return Json(Request(request), Descriptor());
        }));
        Assert.Same(cached.Describe(), cached.Describe());
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(InvalidDescriptors))]
    public void Descriptor_RejectsInvalidAlternatives(AdapterDescriptor descriptor)
    {
        var error = Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ValidateDescriptor(descriptor));
        Assert.Equal("AdapterDescriptorInvalid", error.Code);
    }

    public static TheoryData<AdapterDescriptor> InvalidDescriptors => new()
    {
        Descriptor() with { ProtocolVersion = "2.0" },
        Descriptor() with { Language = " " },
        Descriptor() with { Capabilities = [] },
        Descriptor() with { Capabilities = [AdapterCapability.Index, AdapterCapability.Index] },
        Descriptor() with { Profiles = [new string('p', 129)] },
        Descriptor() with { Capabilities = Enumerable.Repeat(AdapterCapability.Index, 33).ToArray() },
        Descriptor() with { Profiles = Enumerable.Repeat("minimal", 33).ToArray() }
    };

    [Fact]
    public void IndexAndMapping_RejectOrderBoundsAndInvalidFields()
    {
        var badIndex = new AdapterIndex(
            [new SourceUnit("a", SourceUnitKind.File, "a", "hash", "sig"), new SourceUnit("a", SourceUnitKind.File, "b", "hash", "sig")],
            [new ImpactEdge("z", "a", EvidenceKind.StaticDependency), new ImpactEdge("a", "z", EvidenceKind.StaticDependency)],
            [new TestDescriptor(" ", "test", "fixture")]);
        var index = Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ValidateIndex(badIndex));
        Assert.Equal("AdapterOutputInvalid", index.Code);

        var tooMany = new AdapterIndex(Enumerable.Range(0, AdapterProcessProtocol.MaxEntries + 1)
            .Select(value => new SourceUnit(value.ToString("D6"), SourceUnitKind.File, "a", "hash", "sig")).ToArray(), [], []);
        Assert.Equal("AdapterOutputInvalid", Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ValidateIndex(tooMany)).Code);

        var mapping = new MappingResult(
            [new RequestedTest("z", "z", "fixture", []), new RequestedTest("a", "a", "fixture", [])],
            [new ChangedUnit(" ", SourceUnitKind.File, ChangeKind.Modified, false)]);
        Assert.Equal("AdapterOutputInvalid", Assert.Throws<AnalysisException>(() => AdapterProcessProtocol.ValidateMapping(mapping)).Code);
    }

    [Fact]
    public async Task Adapter_MapsCancellationLaunchAnalysisAndRemoteFailures()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessLanguageAdapter(null!, new ProcessLanguageAdapterOptions("tool", [], "/repo")));
        Assert.Throws<ArgumentException>(() => new ProcessLanguageAdapter(new FixtureRunner(_ => new ProcessResult(0, string.Empty, string.Empty)), new ProcessLanguageAdapterOptions(" ", [], "/repo")));

        var launch = Adapter(new ThrowingRunner(new InvalidOperationException("missing")));
        var launchError = await Assert.ThrowsAsync<AnalysisException>(() => launch.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("AdapterProcessLaunchFailed", launchError.Code);

        var analysis = Adapter(new ThrowingRunner(new AnalysisException("BoundExceeded", "detail")));
        var analysisError = await Assert.ThrowsAsync<AnalysisException>(() => analysis.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("AdapterProcessFailed", analysisError.Code);

        var cancelled = Adapter(new ThrowingRunner(new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());

        var remote = Adapter(new FixtureRunner(request =>
        {
            var sent = Request(request);
            var response = new AdapterProcessResponse("1.0", sent.RequestId, sent.Operation, false, EmptyPayload(), new AdapterProcessError("RemoteRejected", "nope"));
            return new ProcessResult(0, string.Empty, string.Empty,
                JsonSerializer.SerializeToUtf8Bytes(response, AdapterProcessJsonContext.Default.AdapterProcessResponse));
        }));
        var remoteError = await Assert.ThrowsAsync<AnalysisException>(() => remote.IndexAsync(new AdapterIndexRequest(Snapshot(), null), default).AsTask());
        Assert.Equal("RemoteRejected", remoteError.Code);
    }

    private static ProcessLanguageAdapter Adapter(IProcessRunner runner) => new(runner,
        new ProcessLanguageAdapterOptions("adapter-fixture", ["--stdio"], "/repo"));

    private static AdapterDescriptor Descriptor() => new("1.0", "fixture", "example", "1", "1", "1",
        [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map], ["minimal"]);

    private static ProcessResult Json<T>(AdapterProcessRequest request, T payload)
    {
        var response = new AdapterProcessResponse("1.0", request.RequestId, request.Operation, true,
            JsonSerializer.SerializeToElement(payload, AdapterProcessJsonContext.Default.GetTypeInfo(typeof(T))!), null);
        return new ProcessResult(0, string.Empty, string.Empty,
            JsonSerializer.SerializeToUtf8Bytes(response, AdapterProcessJsonContext.Default.AdapterProcessResponse));
    }

    private static AdapterProcessRequest Request(ProcessRequest request) =>
        JsonSerializer.Deserialize(request.StandardInput!.Value.Span, AdapterProcessJsonContext.Default.AdapterProcessRequest)!;

    private static AdapterMapRequest MapRequest() => new(Snapshot(), new AdapterIndex([], [], []), []);

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new AdapterEmptyPayload(), AdapterProcessJsonContext.Default.AdapterEmptyPayload);

    private static RepositorySnapshot Snapshot() => new(new SnapshotIdentity("id", "HEAD", "git"), "/repo", "repository",
        [new SnapshotFile("a", Convert.ToHexString(SHA256.HashData("a"u8)), "a"u8.ToArray())]);

    private sealed class FixtureRunner(Func<ProcessRequest, ProcessResult> handler) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public IReadOnlyList<string> Operations => [.. Requests.Select(request =>
            JsonSerializer.Deserialize(request.StandardInput!.Value.Span, AdapterProcessJsonContext.Default.AdapterProcessRequest)!.Operation)];

        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(handler(request));
        }
    }

    private sealed class ThrowingRunner(Exception exception) : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<ProcessResult>(exception);
    }
}
