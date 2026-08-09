using System.Text.Json;
using Merkle.Core.Domain;
using Merkle.Core.Reporting;

namespace Merkle.Tests.Reporting;

public sealed class ReportRendererTests
{
    [Fact]
    public void Json_IsVersionedAndKeepsProbabilitySeparateFromConfidence()
    {
        var report = TerminalReportFactory.Success(
            "run-1",
            new SnapshotIdentity("base", "main", "git"),
            new SnapshotIdentity("head", "HEAD", "git"),
            "repository",
            tests: [new PlannedTest("test:a", "A", true, 0.75, 0.40, 100, [], null)]);

        var json = new JsonReportRenderer().Render(report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var test = root.GetProperty("tests")[0];
        Assert.Equal(0.75, test.GetProperty("impactProbability").GetDouble());
        Assert.Equal(0.40, test.GetProperty("evidenceConfidence").GetDouble());
    }

    [Fact]
    public void Text_IsUnderstandableWithoutColorAndStatesAdvisoryBoundary()
    {
        var report = TerminalReportFactory.Success(
            "run-1",
            new SnapshotIdentity("base", "main", "git"),
            new SnapshotIdentity("head", "HEAD", "git"),
            "repository");

        var text = new TextReportRenderer().Render(report);

        Assert.Contains("advisory", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IncludesChangedUnitsReasonsAndUnmappedWarnings()
    {
        var changed = new ChangedUnit("file:a.cs", SourceUnitKind.File, ChangeKind.Modified, true);
        var unmapped = new ChangedUnit("file:b.cs", SourceUnitKind.File, ChangeKind.Added, false);
        var reason = new ImpactReason(EvidenceKind.StaticDependency, changed.Identity,
            [changed.Identity, "test:a"]);
        var baseline = new SnapshotIdentity("base", "main", "git");
        var candidate = new SnapshotIdentity("head", "HEAD", "git");
        var seed = TerminalReportFactory.Success("run-1", baseline, candidate, "repository");
        var report = seed with
        {
            ChangedUnits = [changed, unmapped],
            Tests = [new PlannedTest("test:a", "A", true, 1, 0, null, [reason], null)],
            UnmappedUnits = [unmapped]
        };

        var text = new TextReportRenderer().Render(report);

        Assert.Contains("file:a.cs", text, StringComparison.Ordinal);
        Assert.Contains("StaticDependency", text, StringComparison.Ordinal);
        Assert.Contains("Unmapped source units", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IncludesCanonicalReportMetadata()
    {
        var seed = TerminalReportFactory.Success(
            "run-42",
            new SnapshotIdentity("base", "main", "git"),
            new SnapshotIdentity("head", "WORKTREE", "git"),
            "repository-7");
        var report = seed with
        {
            Languages =
            [
                new DetectedLanguage("dotnet", "high", [new DetectionEvidence("project", "App.sln")])
            ],
            Adapters =
            [
                new ReportAdapter(
                    "dotnet", "merkle", "0.1.0", "1.0", "1", "1",
                    ["detect", "index", "map"], ["net6.0+"], ["macos", "linux"])
            ],
            Capabilities = ["detect", "index", "map"],
            BuildFingerprint = "build-9",
            History = new ReportHistory(2, 3, ["local"]),
            Economics = new ReportEconomics(100, 250, 60),
            EvidenceCutoff = DateTimeOffset.Parse("2026-08-07T12:34:56Z", null,
                System.Globalization.DateTimeStyles.AssumeUniversal)
        };

        var text = new TextReportRenderer().Render(report);

        Assert.Contains("Schema version: 1", text, StringComparison.Ordinal);
        Assert.Contains("Run: run-42", text, StringComparison.Ordinal);
        Assert.Contains("Repository: repository-7", text, StringComparison.Ordinal);
        Assert.Contains("Capabilities: detect, index, map", text, StringComparison.Ordinal);
        Assert.Contains("Index schema: 1", text, StringComparison.Ordinal);
        Assert.Contains("Identity schemas: unit:1, test:1", text, StringComparison.Ordinal);
        Assert.Contains("Build fingerprint: build-9", text, StringComparison.Ordinal);
        Assert.Contains("History: compatible=2; unmatched=3; provenance=local", text, StringComparison.Ordinal);
        Assert.Contains("Economics: selectedMeanMs=100; fullMeanMs=250; savingsPercent=60", text,
            StringComparison.Ordinal);
        Assert.Contains("Evidence cutoff: 2026-08-07T12:34:56.0000000+00:00", text,
            StringComparison.Ordinal);
        Assert.Contains("targets=net6.0+; platforms=macos,linux", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_RedactsSecretsAcrossCanonicalReport(bool json)
    {
        var report = TerminalReportFactory.Success(
            "run-1",
            new SnapshotIdentity("base", "token=secret-value", "git"),
            new SnapshotIdentity("head", "HEAD", "git"),
            "repository");

        var output = json
            ? new JsonReportRenderer().Render(report)
            : new TextReportRenderer().Render(report);

        Assert.DoesNotContain("secret-value", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
        if (json)
        {
            using var _ = JsonDocument.Parse(output);
        }
    }
}
