using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Tests.Adapters;

public sealed class AdapterRegistryTests
{
    [Fact]
    public void Resolve_RejectsUnavailableCapabilityWithStableCode()
    {
        var adapter = new StubAdapter(new AdapterDescriptor(
            "1.0", "dotnet", "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map],
            ["minimal"]));
        var registry = new AdapterRegistry([adapter]);

        var error = Assert.Throws<CapabilityException>(() => registry.Resolve(
            new LanguageSelection("dotnet", "deep"),
            [AdapterCapability.Observe]));

        Assert.Equal("CapabilityUnavailable", error.Code);
        Assert.Equal("Function not available for: dotnet", error.Message);
    }

    [Fact]
    public void Resolve_RejectsUnsupportedProtocolBeforeWorkBegins()
    {
        var adapter = new StubAdapter(new AdapterDescriptor(
            "2.0", "dotnet", "merkle", "0.1.0", "1", "1",
            [AdapterCapability.Detect], ["minimal"]));
        var registry = new AdapterRegistry([adapter], "1.0");

        var error = Assert.Throws<CapabilityException>(() => registry.Resolve(
            new LanguageSelection("dotnet", "minimal"),
            [AdapterCapability.Detect]));

        Assert.Equal("UnsupportedProtocol", error.Code);
    }

    [Fact]
    public void Resolve_RejectsMissingAdapterWithoutTryingAnotherLanguage()
    {
        var registry = new AdapterRegistry([]);

        var error = Assert.Throws<CapabilityException>(() => registry.Resolve(
            new LanguageSelection("golang", "minimal"),
            [AdapterCapability.Map]));

        Assert.Equal("AdapterUnavailable", error.Code);
        Assert.Equal("Function not available for: golang", error.Message);
    }

    [Fact]
    public void Resolve_RejectsIncompatibleIdentitySchemas()
    {
        var adapter = new StubAdapter(new AdapterDescriptor(
            "1.0", "dotnet", "merkle", "0.1.0", "2", "1",
            [AdapterCapability.Detect, AdapterCapability.Index, AdapterCapability.Map],
            ["minimal"]));

        var error = Assert.Throws<CapabilityException>(() => new AdapterRegistry([adapter]).Resolve(
            new LanguageSelection("dotnet", "minimal"),
            [AdapterCapability.Map]));

        Assert.Equal("IdentityIncompatible", error.Code);
    }

    private sealed class StubAdapter(AdapterDescriptor descriptor) : ILanguageAdapter
    {
        public AdapterDescriptor Describe() => descriptor;

        public ValueTask<AdapterIndex> IndexAsync(AdapterIndexRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AdapterIndex([], [], []));

        public ValueTask<MappingResult> MapAsync(AdapterMapRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MappingResult([], []));
    }
}
