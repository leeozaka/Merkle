using System.Text.Json;

namespace Merkle.Build;

public sealed class BuildReportWriter : IBuildReportWriter
{
    public async ValueTask<string> WriteAsync(
        BuildReportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.GetFullPath(request.ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var report = new BuildRunReportDocument(
            1,
            request.MerkleVersion,
            request.Command == BuildCommand.Publish ? "publish" : "build",
            request.Configuration,
            request.RuntimeIdentifier,
            Outcome(request.Report.Outcome),
            request.Report.ExitCode,
            request.Report.Diagnostic,
            request.Report.Adapters
                .OrderBy(adapter => adapter.AdapterId, StringComparer.Ordinal)
                .Select(adapter => new BuildRunAdapterEntry(
                    adapter.AdapterId,
                    Status(adapter.Status),
                    adapter.Diagnostic,
                    [.. adapter.Warnings ?? []],
                    adapter.Artifacts
                        .OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal)
                        .Select(artifact => new BuildRunArtifactEntry(
                            artifact.RelativePath.Replace('\\', '/'),
                            artifact.Sha256))
                        .ToArray(),
                    adapter.RequiredTool,
                    adapter.DetectedVersion))
                .ToArray(),
            request.Report.RunDirectory,
            request.Report.ManifestPath);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(report, BuildJsonContext.Default.BuildRunReportDocument) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            await WriteAdapterLogsAsync(path, request.Report.Adapters, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteAdapterLogsAsync(
        string reportPath,
        IReadOnlyList<AdapterBuildResult> adapters,
        CancellationToken cancellationToken)
    {
        var logDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, "logs");
        Directory.CreateDirectory(logDirectory);
        foreach (var adapter in adapters)
        {
            var lines = new List<string>
            {
                $"adapter: {adapter.AdapterId}",
                $"status: {Status(adapter.Status)}"
            };
            if (adapter.Diagnostic is not null) lines.Add("diagnostic: " + adapter.Diagnostic);
            if (adapter.RequiredTool is not null) lines.Add("required-tool: " + adapter.RequiredTool);
            if (adapter.DetectedVersion is not null) lines.Add("detected-version: " + adapter.DetectedVersion);
            foreach (var warning in adapter.Warnings ?? []) lines.Add("warning: " + warning);
            foreach (var artifact in adapter.Artifacts.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                lines.Add($"artifact: {artifact.RelativePath.Replace('\\', '/')} sha256={artifact.Sha256}");
            }

            await File.AppendAllLinesAsync(
                Path.Combine(logDirectory, adapter.AdapterId + ".log"),
                ["", .. lines],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Status(AdapterBuildStatus status) => status switch
    {
        AdapterBuildStatus.Built => "built",
        AdapterBuildStatus.Skipped => "skipped",
        AdapterBuildStatus.Failed => "failed",
        AdapterBuildStatus.Cancelled => "cancelled",
        AdapterBuildStatus.NotRun => "not-run",
        _ => status.ToString().ToLowerInvariant()
    };

    private static string Outcome(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Success => "success",
        BuildOutcome.PartialSuccess => "partial-success",
        BuildOutcome.Failed => "failed",
        BuildOutcome.Cancelled => "cancelled",
        _ => outcome.ToString().ToLowerInvariant()
    };
}
