using System.Text;
using System.Text.Json;
using System.Globalization;
using Merkle.Core.Domain;

namespace Merkle.Core.Reporting;

public interface IReportRenderer
{
    string Render(TerminalReport report);
}

public sealed class JsonReportRenderer(SecretRedactor? redactor = null) : IReportRenderer
{
    private readonly SecretRedactor _redactor = redactor ?? SecretRedactor.Default;

    public string Render(TerminalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var json = JsonSerializer.Serialize(report, MerkleJsonContext.Default.TerminalReport);
        return _redactor.Redact(json);
    }
}

public sealed class TextReportRenderer(SecretRedactor? redactor = null) : IReportRenderer
{
    private readonly SecretRedactor _redactor = redactor ?? SecretRedactor.Default;

    public string Render(TerminalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("Merkle terminal report");
        builder.Append("Schema version: ").AppendLine(report.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append("Run: ").AppendLine(report.RunId);
        builder.Append("Status: ").AppendLine(report.TerminalStatus.ToString());
        AppendSnapshot(builder, "Baseline", report.Baseline);
        AppendSnapshot(builder, "Candidate", report.Candidate);
        builder.Append("Repository: ").AppendLine(report.RepositoryIdentity);
        builder.Append("Capabilities: ")
            .AppendLine(report.Capabilities.Count == 0 ? "none" : string.Join(", ", report.Capabilities));
        builder.Append("Index schema: ").AppendLine(report.IndexSchema.ToString(CultureInfo.InvariantCulture));
        builder.Append("Identity schemas: ")
            .AppendLine(report.IdentitySchemas.Count == 0 ? "none" : string.Join(", ", report.IdentitySchemas));
        builder.Append("Build fingerprint: ").AppendLine(report.BuildFingerprint ?? "unavailable");
        builder.Append("Evidence cutoff: ").AppendLine(report.EvidenceCutoff.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("Recommendation: ").AppendLine(report.Policy.Recommendation.ToString());
        builder.Append("Reason: ").AppendLine(report.Policy.DecisiveReason);
        builder.Append("Policy: minSavingsPercent=")
            .Append(report.Policy.EffectiveConfiguration.MinSavingsPercent.ToString(CultureInfo.InvariantCulture))
            .Append("; confidenceThreshold=")
            .Append(FormatNullable(report.Policy.EffectiveConfiguration.ConfidenceThreshold))
            .Append("; onLowConfidence=")
            .Append(report.Policy.EffectiveConfiguration.OnLowConfidence ?? "unavailable")
            .Append("; unmapped=")
            .AppendLine(report.Policy.EffectiveConfiguration.Unmapped.ToString());
        builder.Append("History: compatible=")
            .Append(report.History.CompatibleRuns.ToString(CultureInfo.InvariantCulture))
            .Append("; unmatched=")
            .Append(report.History.UnmatchedRuns.ToString(CultureInfo.InvariantCulture))
            .Append("; provenance=")
            .AppendLine(report.History.ProvenanceTiers.Count == 0
                ? "none"
                : string.Join(',', report.History.ProvenanceTiers));
        builder.Append("Economics: selectedMeanMs=")
            .Append(FormatNullable(report.Economics.SelectedMeanMs))
            .Append("; fullMeanMs=")
            .Append(FormatNullable(report.Economics.FullMeanMs))
            .Append("; savingsPercent=")
            .AppendLine(FormatNullable(report.Economics.SavingsPercent));

        if (report.ErrorClass.HasValue)
        {
            builder.Append("Error: ")
                .Append(report.ErrorClass.Value)
                .Append(':')
                .AppendLine(report.ErrorCode);
        }

        if (report.Languages.Count > 0)
        {
            builder.AppendLine("Languages:");
            foreach (var language in report.Languages.OrderBy(language => language.Language, StringComparer.Ordinal))
            {
                builder.Append("  - ")
                    .Append(language.Language)
                    .Append("; confidence=")
                    .Append(language.Confidence)
                    .Append("; evidence=")
                    .AppendLine(language.Evidence.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var adapter in report.Adapters.OrderBy(adapter => adapter.Language, StringComparer.Ordinal))
        {
            builder.Append("Adapter: ")
                .Append(adapter.Language)
                .Append(' ')
                .Append(adapter.Producer)
                .Append('/')
                .Append(adapter.Version)
                .Append("; protocol=")
                .Append(adapter.ProtocolVersion)
                .Append("; unitIdentity=")
                .Append(adapter.UnitIdentityVersion)
                .Append("; testIdentity=")
                .Append(adapter.TestIdentityVersion)
                .Append("; capabilities=")
                .Append(string.Join(',', adapter.Capabilities))
                .Append("; targets=")
                .Append(adapter.SupportedTargets.Count == 0 ? "none" : string.Join(',', adapter.SupportedTargets))
                .Append("; platforms=")
                .AppendLine(adapter.SupportedPlatforms.Count == 0
                    ? "none"
                    : string.Join(',', adapter.SupportedPlatforms));
        }

        if (report.ChangedUnits.Count > 0)
        {
            builder.AppendLine("Changed source units:");
            foreach (var unit in report.ChangedUnits.OrderBy(unit => unit.Identity, StringComparer.Ordinal))
            {
                builder.Append("  - ")
                    .Append(unit.Identity)
                    .Append(" [")
                    .Append(unit.Kind)
                    .Append(", ")
                    .Append(unit.ChangeKind)
                    .Append(", ")
                    .Append(unit.Mapped ? "mapped" : "unmapped")
                    .AppendLine("]");
            }
        }

        if (report.Tests.Count == 0)
        {
            builder.AppendLine("Requested tests: none");
        }
        else
        {
            builder.AppendLine("Requested tests:");
            foreach (var test in report.Tests.OrderBy(test => test.Identity, StringComparer.Ordinal))
            {
                builder.Append("  - ").Append(test.Identity);
                if (!StringComparer.Ordinal.Equals(test.Identity, test.DisplayName))
                {
                    builder.Append(" (").Append(test.DisplayName).Append(')');
                }

                builder.Append("; selected=")
                    .Append(test.Selected ? "true" : "false")
                    .Append("; probability=")
                    .Append(test.ImpactProbability?.ToString("F3", CultureInfo.InvariantCulture) ?? "unavailable")
                    .Append("; confidence=")
                    .Append(test.EvidenceConfidence?.ToString("F3", CultureInfo.InvariantCulture) ?? "unavailable")
                    .Append("; expectedDurationMs=")
                    .Append(FormatNullable(test.ExpectedDurationMs))
                    .Append("; estimateStatus=")
                    .Append(test.Estimates.ImpactProbability.Status.ToString())
                    .Append("; excludedBy=")
                    .Append(test.ExcludedBy ?? "none");
                builder.AppendLine();
                foreach (var reason in test.Reasons)
                {
                    builder.Append("      reason: ")
                        .Append(reason.Kind)
                        .Append(" via ")
                        .AppendLine(string.Join(" -> ", reason.Path));
                }
            }
        }

        if (report.UnmappedUnits.Count > 0)
        {
            builder.AppendLine("Unmapped source units:");
            foreach (var unit in report.UnmappedUnits.OrderBy(unit => unit.Identity, StringComparer.Ordinal))
            {
                builder.Append("  - ").AppendLine(unit.Identity);
            }
        }

        if (report.Executions is { Count: > 0 })
        {
            builder.AppendLine("Test executions:");
            foreach (var execution in report.Executions.OrderBy(value => value.TestIdentity, StringComparer.Ordinal))
            {
                builder.Append("  - ")
                    .Append(execution.TestIdentity)
                    .Append("; outcome=")
                    .Append(execution.Outcome)
                    .Append("; durationMs=")
                    .Append(FormatNullable(execution.DurationMs))
                    .Append("; observationComplete=")
                    .AppendLine(execution.ObservationComplete ? "true" : "false");
            }
        }

        foreach (var warning in report.Warnings)
        {
            builder.Append("Warning: ").AppendLine(warning);
        }

        return _redactor.Redact(builder.ToString());
    }

    private static void AppendSnapshot(
        StringBuilder builder,
        string label,
        SnapshotIdentity snapshot)
    {
        builder.Append(label)
            .Append(": ")
            .Append(snapshot.Value)
            .Append(" (reference=")
            .Append(snapshot.Reference)
            .Append("; provider=")
            .Append(snapshot.Provider)
            .AppendLine(")");
    }

    private static string FormatNullable(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? "unavailable";
}
