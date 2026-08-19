namespace Merkle.Build;

public sealed class BuildOrchestrator : IBuildOrchestrator
{
    private readonly IBuildAdapterCatalog _catalog;
    private readonly IHostPublisher _hostPublisher;
    private readonly IBuildReportWriter _reportWriter;
    private readonly IBuildRunWorkspaceFactory _workspaceFactory;

    public BuildOrchestrator(
        IBuildAdapterCatalog catalog,
        IHostPublisher hostPublisher,
        IBuildReportWriter reportWriter,
        IBuildRunWorkspaceFactory workspaceFactory)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _hostPublisher = hostPublisher ?? throw new ArgumentNullException(nameof(hostPublisher));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _workspaceFactory = workspaceFactory ?? throw new ArgumentNullException(nameof(workspaceFactory));
    }

    public async ValueTask<BuildReport> RunAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selected = _catalog.ResolveSelection(request.Adapters);
        var results = selected.ToDictionary(
            adapter => adapter.Definition.Id,
            adapter => new AdapterBuildResult(adapter.Definition.Id, AdapterBuildStatus.NotRun, []),
            StringComparer.Ordinal);
        IBuildRunWorkspace acquiredWorkspace;
        try
        {
            acquiredWorkspace = await _workspaceFactory.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Merkle.Core.Errors.ConfigurationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return await WriteWorkspaceFailureAsync(request, selected, results, BuildOutcome.Cancelled, 130, "cancelled").ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return await WriteWorkspaceFailureAsync(request, selected, results, BuildOutcome.Failed, 4, error.Message).ConfigureAwait(false);
        }

        await using var workspace = acquiredWorkspace;
        var context = workspace.Context;
        var ready = new List<AdapterBuildPlanEntry>();
        var unavailable = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hostSdk = _catalog.Adapters.FirstOrDefault(adapter =>
                StringComparer.Ordinal.Equals(adapter.Definition.Id, "dotnet"));
            AdapterReadiness? hostReadiness = null;
            if (hostSdk is not null)
            {
                hostReadiness = await hostSdk.PreflightAsync(context, cancellationToken).ConfigureAwait(false);
            }

            foreach (var adapter in selected)
            {
                var readiness = adapter == hostSdk
                    ? hostReadiness!
                    : await adapter.PreflightAsync(context, cancellationToken).ConfigureAwait(false);
                if (readiness.Status == AdapterReadinessStatus.Ready)
                {
                    ready.Add(new AdapterBuildPlanEntry(adapter, readiness));
                    results[adapter.Definition.Id] = results[adapter.Definition.Id] with
                    {
                        RequiredTool = readiness.RequiredTool,
                        DetectedVersion = readiness.DetectedVersion
                    };
                    continue;
                }

                unavailable = true;
                results[adapter.Definition.Id] = new AdapterBuildResult(
                    adapter.Definition.Id,
                    AdapterBuildStatus.Skipped,
                    [],
                    readiness.Reason,
                    RequiredTool: readiness.RequiredTool,
                    DetectedVersion: readiness.DetectedVersion);
            }

            if (hostReadiness is { Status: not AdapterReadinessStatus.Ready })
            {
                return await CompleteAsync(
                    request,
                    workspace,
                    selected,
                    results,
                    BuildOutcome.Failed,
                    4,
                    publish: false,
                    cancellationToken,
                    ReadinessDiagnostic(hostReadiness, "The .NET SDK is required to build Merkle.")).ConfigureAwait(false);
            }

            if (request.Policy == AdapterBuildPolicy.Strict && unavailable)
            {
                return await CompleteAsync(
                    request,
                    workspace,
                    selected,
                    results,
                    BuildOutcome.Failed,
                    3,
                    publish: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Scheduling == BuildScheduling.Parallel)
            {
                await RunParallelAsync(ready, request, context, results, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunSequentialAsync(ready, request, context, results, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var built = results.Values.Count(result => result.Status == AdapterBuildStatus.Built);
            var hasFailure = results.Values.Any(result => result.Status is AdapterBuildStatus.Failed or AdapterBuildStatus.Cancelled);
            if (built == 0 || (request.Policy == AdapterBuildPolicy.Strict && hasFailure))
            {
                return await CompleteAsync(
                    request,
                    workspace,
                    selected,
                    results,
                    BuildOutcome.Failed,
                    3,
                    publish: false,
                    cancellationToken).ConfigureAwait(false);
            }

            var partial = hasFailure || results.Values.Any(result => result.Status is AdapterBuildStatus.Skipped or AdapterBuildStatus.NotRun);
            return await CompleteAsync(
                request,
                workspace,
                selected,
                results,
                partial ? BuildOutcome.PartialSuccess : BuildOutcome.Success,
                0,
                publish: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var adapter in selected)
            {
                if (results[adapter.Definition.Id].Status is AdapterBuildStatus.NotRun)
                {
                    results[adapter.Definition.Id] = new AdapterBuildResult(adapter.Definition.Id, AdapterBuildStatus.Cancelled, [], "cancelled");
                }
            }

            return await CompleteAsync(
                request,
                workspace,
                selected,
                results,
                BuildOutcome.Cancelled,
                130,
                publish: false,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return await CompleteAsync(
                request,
                workspace,
                selected,
                results,
                BuildOutcome.Failed,
                4,
                publish: false,
                CancellationToken.None,
                error.Message).ConfigureAwait(false);
        }
    }

    private static async Task RunSequentialAsync(
        IReadOnlyList<AdapterBuildPlanEntry> adapters,
        BuildRequest request,
        BuildContext context,
        IDictionary<string, AdapterBuildResult> results,
        CancellationToken cancellationToken)
    {
        foreach (var entry in adapters)
        {
            var result = await RunOneAsync(
                entry,
                request,
                context,
                cancellationToken,
                cancellationToken).ConfigureAwait(false);
            results[entry.Adapter.Definition.Id] = result;
            if (request.Policy == AdapterBuildPolicy.Strict && result.Status != AdapterBuildStatus.Built)
            {
                foreach (var remaining in adapters.SkipWhile(item => item != entry).Skip(1))
                {
                    results[remaining.Adapter.Definition.Id] = new AdapterBuildResult(remaining.Adapter.Definition.Id, AdapterBuildStatus.NotRun, []);
                }

                break;
            }
        }
    }

    private static async Task RunParallelAsync(
        IReadOnlyList<AdapterBuildPlanEntry> adapters,
        BuildRequest request,
        BuildContext context,
        IDictionary<string, AdapterBuildResult> results,
        CancellationToken cancellationToken)
    {
        var limit = request.MaxParallel ?? Math.Max(1, Environment.ProcessorCount);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new Dictionary<Task<AdapterBuildResult>, AdapterBuildPlanEntry>();
        var next = 0;
        var stopStarting = false;

        while (next < adapters.Count || active.Count > 0)
        {
            while (!stopStarting && next < adapters.Count && active.Count < limit)
            {
                var entry = adapters[next++];
                active[RunOneAsync(
                    entry,
                    request,
                    context,
                    linkedCancellation.Token,
                    cancellationToken)] = entry;
            }

            if (active.Count == 0)
            {
                break;
            }

            var completed = await Task.WhenAny(active.Keys).ConfigureAwait(false);
            var entryForResult = active[completed];
            active.Remove(completed);
            var result = await completed.ConfigureAwait(false);
            results[entryForResult.Adapter.Definition.Id] = result;

            if (request.Policy == AdapterBuildPolicy.Strict && result.Status != AdapterBuildStatus.Built)
            {
                stopStarting = true;
                linkedCancellation.Cancel();
                foreach (var pending in active.Keys.ToArray())
                {
                    var pendingEntry = active[pending];
                    var pendingResult = await pending.ConfigureAwait(false);
                    results[pendingEntry.Adapter.Definition.Id] = pendingResult.Status == AdapterBuildStatus.Built
                        ? pendingResult
                        : pendingResult with { Status = AdapterBuildStatus.Cancelled };
                    active.Remove(pending);
                }

                while (next < adapters.Count)
                {
                    var notRun = adapters[next++].Adapter;
                    results[notRun.Definition.Id] = new AdapterBuildResult(notRun.Definition.Id, AdapterBuildStatus.NotRun, []);
                }
            }
        }
    }

    private static async Task<AdapterBuildResult> RunOneAsync(
        AdapterBuildPlanEntry entry,
        BuildRequest request,
        BuildContext context,
        CancellationToken cancellationToken,
        CancellationToken externalCancellationToken = default)
    {
        try
        {
            var result = await entry.Adapter.BuildAsync(
                new AdapterBuildRequest(context, request.RunTests, entry.Readiness),
                cancellationToken).ConfigureAwait(false);
            return result with
            {
                RequiredTool = result.RequiredTool ?? entry.Readiness.RequiredTool,
                DetectedVersion = result.DetectedVersion ?? entry.Readiness.DetectedVersion
            };
        }
        catch (OperationCanceledException) when (!externalCancellationToken.IsCancellationRequested)
        {
            return new AdapterBuildResult(
                entry.Adapter.Definition.Id,
                AdapterBuildStatus.Cancelled,
                [],
                "cancelled",
                RequiredTool: entry.Readiness.RequiredTool,
                DetectedVersion: entry.Readiness.DetectedVersion);
        }
        catch (FileNotFoundException error)
        {
            return new AdapterBuildResult(
                entry.Adapter.Definition.Id,
                AdapterBuildStatus.Failed,
                [],
                error.Message,
                RequiredTool: entry.Readiness.RequiredTool,
                DetectedVersion: entry.Readiness.DetectedVersion);
        }
    }

    private sealed record AdapterBuildPlanEntry(IBuildAdapter Adapter, AdapterReadiness Readiness);

    private static string ReadinessDiagnostic(AdapterReadiness readiness, string fallback)
    {
        var message = readiness.Reason ?? fallback;
        if (readiness.RequiredTool is not null) message += $" Required tool: {readiness.RequiredTool}.";
        if (readiness.DetectedVersion is not null) message += $" Detected version: {readiness.DetectedVersion}.";
        return message;
    }

    private static BuildReport Report(
        BuildOutcome outcome,
        int exitCode,
        IReadOnlyList<IBuildAdapter> selected,
        IReadOnlyDictionary<string, AdapterBuildResult> results,
        BuildContext context) =>
        new(
            outcome,
            exitCode,
            [.. selected.Select(adapter => results[adapter.Definition.Id])],
            context.RunDirectory);

    private async ValueTask<BuildReport> CompleteAsync(
        BuildRequest request,
        IBuildRunWorkspace workspace,
        IReadOnlyList<IBuildAdapter> selected,
        IReadOnlyDictionary<string, AdapterBuildResult> results,
        BuildOutcome outcome,
        int exitCode,
        bool publish,
        CancellationToken cancellationToken,
        string? diagnostic = null)
    {
        var context = workspace.Context;
        var report = Report(outcome, exitCode, selected, results, context) with { Diagnostic = diagnostic };
        BuildOutputRequest? outputRequest = null;
        if (publish)
        {
            var successful = report.Adapters
                .Where(adapter => adapter.Status == AdapterBuildStatus.Built)
                .ToArray();
            try
            {
                var host = await _hostPublisher.PublishAsync(
                    new HostPublishRequest(request, context, successful),
                    cancellationToken).ConfigureAwait(false);
                if (!host.Succeeded)
                {
                    report = report with
                    {
                        Outcome = BuildOutcome.Failed,
                        ExitCode = 4,
                        Diagnostic = host.Diagnostic
                    };
                }
                else
                {
                    outputRequest = new BuildOutputRequest(
                        context.OutputPath ?? Path.Combine(context.RepositoryRoot, "artifacts", request.Command.ToString().ToLowerInvariant()),
                        context.HostStagingDirectory
                            ?? throw new InvalidOperationException("The host staging directory is not configured."),
                        context.StagingDirectory,
                        request.Configuration,
                        context.RuntimeIdentifier ?? "portable",
                        successful,
                        BuildVersion.Current);
                    report = report with
                    {
                        ManifestPath = Path.Combine(outputRequest.OutputPath, "adapters.json")
                    };
                }
            }
            catch (OperationCanceledException)
            {
                report = report with { Outcome = BuildOutcome.Cancelled, ExitCode = 130 };
                outputRequest = null;
            }
            catch (Exception error)
            {
                report = report with
                {
                    Outcome = BuildOutcome.Failed,
                    ExitCode = 4,
                    Diagnostic = error.Message
                };
                outputRequest = null;
            }
        }

        var reportPath = request.ReportPath ?? Path.Combine(context.RunDirectory, "build-report.json");
        try
        {
            var written = await _reportWriter.WriteAsync(
                new BuildReportRequest(
                    reportPath,
                    request.Command,
                    request.Configuration,
                    context.RuntimeIdentifier ?? "portable",
                    report,
                    BuildVersion.Current),
                report.Outcome == BuildOutcome.Cancelled ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            report = report with { ReportPath = written };
        }
        catch (OperationCanceledException)
        {
            report = report with
            {
                Outcome = BuildOutcome.Cancelled,
                ExitCode = 130,
                ManifestPath = null
            };
            outputRequest = null;
            return await RewriteReportAsync(request, context, reportPath, report).ConfigureAwait(false);
        }
        catch
        {
            report = report with
            {
                Outcome = BuildOutcome.Failed,
                ExitCode = 4,
                ManifestPath = null,
                Diagnostic = report.Diagnostic ?? "The build report could not be written."
            };
            return await RewriteReportAsync(request, context, reportPath, report).ConfigureAwait(false);
        }

        if (outputRequest is null || report.ExitCode != 0) return report;

        try
        {
            var output = await workspace.PromoteAsync(outputRequest, cancellationToken).ConfigureAwait(false);
            return report with { ManifestPath = output.ManifestPath };
        }
        catch (OperationCanceledException)
        {
            report = report with
            {
                Outcome = BuildOutcome.Cancelled,
                ExitCode = 130,
                ManifestPath = null
            };
        }
        catch (Exception error)
        {
            report = report with
            {
                Outcome = BuildOutcome.Failed,
                ExitCode = 4,
                ManifestPath = null,
                Diagnostic = error.Message
            };
        }

        return await RewriteReportAsync(request, context, reportPath, report).ConfigureAwait(false);
    }

    private async ValueTask<BuildReport> RewriteReportAsync(
        BuildRequest request,
        BuildContext context,
        string reportPath,
        BuildReport report)
    {
        try
        {
            var written = await _reportWriter.WriteAsync(
                new BuildReportRequest(
                    reportPath,
                    request.Command,
                    request.Configuration,
                    context.RuntimeIdentifier ?? "portable",
                    report,
                    BuildVersion.Current),
                CancellationToken.None).ConfigureAwait(false);
            return report with { ReportPath = written };
        }
        catch
        {
            return report;
        }
    }

    private async ValueTask<BuildReport> WriteWorkspaceFailureAsync(
        BuildRequest request,
        IReadOnlyList<IBuildAdapter> selected,
        IReadOnlyDictionary<string, AdapterBuildResult> results,
        BuildOutcome outcome,
        int exitCode,
        string diagnostic)
    {
        var runDirectory = Path.Combine(
            Path.GetTempPath(),
            "merkle-build-runs",
            Guid.NewGuid().ToString("N"));
        var report = new BuildReport(
            outcome,
            exitCode,
            [.. selected.Select(adapter => results[adapter.Definition.Id])],
            runDirectory,
            Diagnostic: diagnostic);
        try
        {
            var reportPath = await _reportWriter.WriteAsync(
                new BuildReportRequest(
                    Path.Combine(runDirectory, "build-report.json"),
                    request.Command,
                    request.Configuration,
                    request.RuntimeIdentifier ?? BuildRuntimeIdentifier.Current,
                    report,
                    BuildVersion.Current),
                CancellationToken.None).ConfigureAwait(false);
            return report with { ReportPath = reportPath };
        }
        catch
        {
            return report with { Diagnostic = diagnostic + " The fallback build report could not be written." };
        }
    }
}
