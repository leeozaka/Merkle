using System.Text.Json;
using Merkle.Adapters.DotNet;
using Merkle.Core.Domain;
using Merkle.Core.Reporting;

namespace Merkle.Tests.Conformance;

public sealed class ContractConformanceTests
{
    [Fact]
    public void TerminalReportJson_MatchesVersionOneContractFixture()
    {
        using var contract = ReadFixture("terminal-report-v1.contract.json");
        var report = TerminalReportFactory.Success(
            "run-1",
            new SnapshotIdentity("base", "main", "git"),
            new SnapshotIdentity("head", "HEAD", "git"),
            "repository",
            [new PlannedTest("test:a", "A", true, null, null, null, [], null)]);
        using var rendered = JsonDocument.Parse(new JsonReportRenderer().Render(report));

        Assert.Equal(
            contract.RootElement.GetProperty("schemaVersion").GetInt32(),
            rendered.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ReadStringArray(contract.RootElement, "requiredProperties").Order(),
            rendered.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(
            ReadStringArray(contract.RootElement, "plannedTestProperties").Order(),
            rendered.RootElement.GetProperty("tests")[0]
                .EnumerateObject()
                .Select(property => property.Name)
                .Order());
    }

    [Fact]
    public void OfficialDotNetAdapter_MatchesVersionOneContractFixture()
    {
        using var contract = ReadFixture("dotnet-adapter-v1.contract.json");
        var expected = contract.RootElement;
        var actual = new DotNetAdapter().Describe();

        Assert.Equal(expected.GetProperty("protocolVersion").GetString(), actual.ProtocolVersion);
        Assert.Equal(expected.GetProperty("language").GetString(), actual.Language);
        Assert.Equal(expected.GetProperty("producer").GetString(), actual.Producer);
        Assert.Equal(expected.GetProperty("adapterVersion").GetString(), actual.AdapterVersion);
        Assert.Equal(expected.GetProperty("unitIdentityVersion").GetString(), actual.UnitIdentityVersion);
        Assert.Equal(expected.GetProperty("testIdentityVersion").GetString(), actual.TestIdentityVersion);
        Assert.Equal(ReadStringArray(expected, "capabilities"),
            actual.Capabilities.Select(capability => capability.ToString()));
        Assert.Equal(ReadStringArray(expected, "profiles"), actual.Profiles);
        Assert.Equal(ReadStringArray(expected, "supportedTargets"), actual.SupportedTargets);
        Assert.Equal(ReadStringArray(expected, "supportedPlatforms"), actual.SupportedPlatforms);
    }

    private static JsonDocument ReadFixture(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "Fixtures",
            fileName)));

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).EnumerateArray().Select(value => value.GetString()!);
}
