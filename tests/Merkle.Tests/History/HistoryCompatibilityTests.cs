using Merkle.Core.Adapters;
using Merkle.Core.History;
using Merkle.Core.Reporting;

namespace Merkle.Tests.History;

public sealed class HistoryCompatibilityTests
{
    [Fact]
    public void ForAdapter_IsStableForEquivalentInputsAndChangesWithBuildInputs()
    {
        var descriptor = new AdapterDescriptor("1.0", "dotnet", "merkle", "1", "u1", "t1", [], []);

        var first = HistoryCompatibility.ForAdapter("repo", descriptor, "App.sln", "Release", "x64");
        var same = HistoryCompatibility.ForAdapter("repo", descriptor, "App.sln", "Release", "x64");
        var changed = HistoryCompatibility.ForAdapter("repo", descriptor, "Other.sln", "Release", "x64");

        Assert.True(first.Matches(same));
        Assert.False(first.Matches(changed));
        Assert.DoesNotContain("App.sln", first.BuildFingerprintFamily, StringComparison.Ordinal);
    }

    [Fact]
    public void ForReportAdapter_UsesDefaultsAndRejectsMissingIdentity()
    {
        var adapter = new ReportAdapter("dotnet", "merkle", "1", "1.0", "u1", "t1", [], [], []);

        var key = HistoryCompatibility.ForReportAdapter("repo", adapter, null);

        Assert.Contains("configuration:Debug", key.BuildFingerprintFamily, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HistoryCompatibility.ForReportAdapter(" ", adapter, null));
        Assert.Throws<ArgumentNullException>(() => HistoryCompatibility.ForReportAdapter("repo", null!, null));
    }
}
