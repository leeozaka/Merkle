using System.Text.Json;
using Merkle.Core.Errors;

namespace Merkle.Build;

public sealed class BuildConsoleApplication
{
    private readonly IBuildCommandLineParser _parser;
    private readonly IBuildAdapterCatalog _catalog;
    private readonly IBuildOrchestrator _orchestrator;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public BuildConsoleApplication(
        IBuildCommandLineParser parser,
        IBuildAdapterCatalog catalog,
        IBuildOrchestrator orchestrator,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public async ValueTask<int> RunAsync(
        IReadOnlyList<string> arguments,
        bool interactive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            WriteHelp();
            return 0;
        }

        BuildRequest request;
        try
        {
            request = interactive && arguments.Count == 0
                ? await PromptForRequestAsync(cancellationToken).ConfigureAwait(false)
                : _parser.Parse(arguments);
        }
        catch (ConfigurationException exception)
        {
            _error.WriteLine($"ConfigurationError:{exception.Code}: {exception.Message}");
            return 2;
        }

        try
        {
            var report = await _orchestrator.RunAsync(request, cancellationToken).ConfigureAwait(false);
            Render(report, request);
            return report.ExitCode;
        }
        catch (ConfigurationException exception)
        {
            _error.WriteLine($"ConfigurationError:{exception.Code}: {exception.Message}");
            return 2;
        }
    }

    private async ValueTask<BuildRequest> PromptForRequestAsync(CancellationToken cancellationToken)
    {
        var readiness = await ProbeReadinessAsync(cancellationToken).ConfigureAwait(false);
        _output.WriteLine("Adapter readiness:");
        foreach (var adapter in _catalog.Adapters)
        {
            var status = readiness.TryGetValue(adapter.Definition.Id, out var value)
                ? value.Status == AdapterReadinessStatus.Ready ? "ready" : "unavailable"
                : "unavailable";
            var marker = adapter.Definition.Id == "dotnet" ? " (selected by default)" : string.Empty;
            _output.WriteLine($"  {adapter.Definition.Id}: {status}{marker}");
            if (value?.Reason is not null) _output.WriteLine($"    {value.Reason}");
        }

        while (true)
        {
            var command = ReadOrDefault("Build or publish [build]: ", "build");
            var adapters = ReadOrDefault("Adapters (comma-separated, dotnet preselected) [dotnet]: ", "dotnet");
            var policy = ReadOrDefault("Policy (strict/best-effort) [strict]: ", "strict");
            var request = _parser.Parse([
                command,
                "--adapters", adapters,
                "--adapter-policy", policy]);

            var selected = _catalog.ResolveSelection(request.Adapters);
            if (request.Policy != AdapterBuildPolicy.Strict ||
                !selected.Any(adapter => readiness.TryGetValue(adapter.Definition.Id, out var value) && value.Status == AdapterReadinessStatus.Unavailable))
            {
                return request;
            }

            _output.WriteLine("The selected adapter is unavailable in strict mode.");
            var revise = ReadOrDefault("Revise the selection? [y/N]: ", "n");
            if (!revise.Equals("y", StringComparison.OrdinalIgnoreCase)) return request;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async ValueTask<Dictionary<string, AdapterReadiness>> ProbeReadinessAsync(CancellationToken cancellationToken)
    {
        var context = new BuildContext(
            Directory.GetCurrentDirectory(),
            "Debug",
            null,
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory());
        var readiness = new Dictionary<string, AdapterReadiness>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _catalog.Adapters)
        {
            readiness[adapter.Definition.Id] = await adapter.PreflightAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return readiness;
    }

    private string ReadOrDefault(string prompt, string defaultValue)
    {
        _output.Write(prompt);
        return _input.ReadLine() is { } value && value.Trim().Length > 0 ? value.Trim() : defaultValue;
    }

    private void Render(BuildReport report, BuildRequest request)
    {
        if (request.Format == BuildOutputFormat.Json)
        {
            RenderDiagnostics(report, request.NoWarnings);
            var result = new ConsoleBuildReport(
                Outcome(report.Outcome),
                report.ExitCode,
                [.. report.Adapters.Select(adapter => new ConsoleAdapterBuildResult(
                    adapter.AdapterId,
                    Status(adapter.Status),
                    adapter.Artifacts,
                    adapter.Diagnostic,
                    adapter.Warnings,
                    adapter.RequiredTool,
                    adapter.DetectedVersion))],
                report.RunDirectory,
                report.ManifestPath,
                report.ReportPath,
                report.Diagnostic);
            _output.WriteLine(JsonSerializer.Serialize(result, BuildConsoleJsonContext.Default.ConsoleBuildReport));
            return;
        }

        foreach (var adapter in report.Adapters)
        {
            _output.WriteLine($"{adapter.AdapterId}: {Status(adapter.Status)}");
            if (adapter.Diagnostic is not null && (!request.NoWarnings || report.ExitCode != 0))
            {
                _output.WriteLine($"Diagnostic: {adapter.AdapterId}: {adapter.Diagnostic}");
            }
            if (!request.NoWarnings && adapter.Warnings is not null)
            {
                foreach (var warning in adapter.Warnings) _output.WriteLine($"Warning: {adapter.AdapterId}: {warning}");
            }
        }

        if (report.Diagnostic is not null) _output.WriteLine($"Diagnostic: {report.Diagnostic}");

        _output.WriteLine($"{Outcome(report.Outcome)} (exit {report.ExitCode})");
    }

    private void RenderDiagnostics(BuildReport report, bool noWarnings)
    {
        if (report.Diagnostic is not null) _error.WriteLine($"Diagnostic: {report.Diagnostic}");
        foreach (var adapter in report.Adapters)
        {
            if (adapter.Diagnostic is not null && (!noWarnings || report.ExitCode != 0))
            {
                _error.WriteLine($"Diagnostic: {adapter.AdapterId}: {adapter.Diagnostic}");
            }
            if (!noWarnings && adapter.Warnings is not null)
            {
                foreach (var warning in adapter.Warnings) _error.WriteLine($"Warning: {adapter.AdapterId}: {warning}");
            }
        }
    }

    private static string Status(AdapterBuildStatus status) => status switch
    {
        AdapterBuildStatus.Built => "built",
        AdapterBuildStatus.Skipped => "skipped",
        AdapterBuildStatus.Failed => "failed",
        AdapterBuildStatus.Cancelled => "cancelled",
        AdapterBuildStatus.NotRun => "not-run",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static string Outcome(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Success => "success",
        BuildOutcome.PartialSuccess => "partial-success",
        BuildOutcome.Failed => "failed",
        BuildOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private void WriteHelp()
    {
        _output.WriteLine("Merkle source build helper");
        _output.WriteLine();
        _output.WriteLine("./build [build|publish] [options]");
        _output.WriteLine();
        _output.WriteLine("  --adapters <dotnet,golang,python,java|all>  Select adapters (default: dotnet)");
        _output.WriteLine("  --adapter-policy <strict|best-effort>       Failure policy (default: strict)");
        _output.WriteLine("  --builds <sequential|parallel>              Adapter scheduling");
        _output.WriteLine("  --max-parallel <count>                      Parallel build limit");
        _output.WriteLine("  --test                                      Run host and selected adapter tests");
        _output.WriteLine("  --configuration <Debug|Release>             Build configuration");
        _output.WriteLine("  --runtime <rid>                             Current machine RID for publish");
        _output.WriteLine("  --output <path>                             Package destination");
        _output.WriteLine("  --report <path>                             External JSON report path");
        _output.WriteLine("  --format <text|json>                        Console result format");
        _output.WriteLine("  --clean                                     Remove marked stale intermediates");
        _output.WriteLine("  --no-warnings                               Hide warnings for successful partial builds");
    }
}
