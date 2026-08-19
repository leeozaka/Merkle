using Merkle.Build;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Tests.Build;

public sealed class AdapterBuildCatalogTests
{
    [Fact]
    public void CatalogExposesCanonicalAdaptersInStableOrderAndResolvesAliases()
    {
        var catalog = new AdapterBuildCatalog(new ThrowingProcessRunner());

        Assert.Equal(
            ["dotnet", "golang", "python", "java"],
            catalog.Adapters.Select(adapter => adapter.Definition.Id));
        Assert.Same(catalog.Adapters[1], catalog.Resolve("go"));
        Assert.Same(catalog.Adapters[1], catalog.Resolve("golang"));
        Assert.Equal(catalog.Adapters, catalog.ResolveAll());
    }

    [Fact]
    public void CatalogRejectsUnknownAdapterNames()
    {
        var catalog = new AdapterBuildCatalog(new ThrowingProcessRunner());

        Assert.Throws<ArgumentException>(() => catalog.Resolve("typescript"));
        Assert.Throws<ArgumentException>(() => catalog.Resolve("  "));
    }

    [Fact]
    public void ResolveSelectionSupportsAllAndDeduplicatesCanonicalAliases()
    {
        var catalog = new AdapterBuildCatalog(new ThrowingProcessRunner());

        Assert.Equal(catalog.Adapters, catalog.ResolveSelection(["ALL"]));
        Assert.Equal(
            ["golang", "python"],
            catalog.ResolveSelection(["go", "golang", "python", "go"])
                .Select(adapter => adapter.Definition.Id));
        Assert.Equal(
            ["golang", "python"],
            catalog.ResolveMany(["go", "golang", "python"])
                .Select(adapter => adapter.Definition.Id));
    }

    [Fact]
    public void ResolveSelectionRejectsAmbiguousOrUnknownSelections()
    {
        var catalog = new AdapterBuildCatalog(new ThrowingProcessRunner());

        Assert.Equal(
            "InvalidOptionValue",
            Assert.Throws<ConfigurationException>(() => catalog.ResolveSelection([])).Code);
        Assert.Equal(
            "InvalidOptionValue",
            Assert.Throws<ConfigurationException>(() => catalog.ResolveSelection(["all", "java"])).Code);
        Assert.Equal(
            "UnknownAdapter",
            Assert.Throws<ConfigurationException>(() => catalog.ResolveSelection(["typescript"])).Code);
        Assert.Equal(
            "UnknownAdapter",
            Assert.Throws<ConfigurationException>(() => catalog.ResolveSelection([" "])).Code);
        Assert.Throws<ArgumentNullException>(() => catalog.ResolveSelection(null!));
        Assert.Throws<ArgumentNullException>(() => catalog.ResolveMany(null!));
    }

    [Fact]
    public void CatalogConstructorRejectsInvalidDefinitions()
    {
        Assert.Throws<ArgumentNullException>(() => new AdapterBuildCatalog((IEnumerable<IBuildAdapter>)null!));
        Assert.Throws<ArgumentException>(() => new AdapterBuildCatalog([]));
        Assert.Throws<ArgumentNullException>(() => new AdapterBuildCatalog([null!]));
        Assert.Throws<ArgumentException>(() => new AdapterBuildCatalog([
            new StubAdapter("java", []),
            new StubAdapter("java", [])
        ]));
        Assert.Throws<ArgumentException>(() => new AdapterBuildCatalog([
            new StubAdapter("golang", ["go"]),
            new StubAdapter("future", ["go"])
        ]));

        var catalog = new AdapterBuildCatalog([new StubAdapter("java", ["JAVA"])]);

        Assert.Same(catalog.Adapters[0], catalog.Resolve("java"));
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The catalog test must not probe toolchains.");
    }

    private sealed class StubAdapter(string id, IReadOnlyList<string> aliases) : IBuildAdapter
    {
        public AdapterBuildDefinition Definition { get; } = new(id, aliases, "1.0", ["osx-arm64"]);

        public ValueTask<AdapterReadiness> PreflightAsync(
            BuildContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The catalog test must not probe toolchains.");

        public ValueTask<AdapterBuildResult> BuildAsync(
            AdapterBuildRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The catalog test must not build adapters.");
    }
}
