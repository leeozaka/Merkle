using Merkle.Core.Domain;
using Merkle.Core.Configuration;
using Merkle.Core.Engine;
using Merkle.Core.Errors;
using Merkle.Core.Reporting;
using Merkle.Core.State;
using Merkle.Core.History;
using System.Text.Json;

namespace Merkle.Cli;

public sealed class CliApplication
{
    private readonly ImpactEngine _engine;
    private readonly IStateStore _stateStore;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private readonly MerkleConfiguration _configuration;
    private readonly IDeepExecutionEngine? _deepExecutionEngine;
    private readonly IHistoryImportService? _historyImportService;
    private readonly string _stateDirectory;
    private readonly SecretRedactor _redactor;
    private readonly ICommandLineParser _parser;

    public CliApplication(
        ImpactEngine engine,
        IStateStore stateStore,
        TextWriter standardOutput,
        TextWriter standardError,
        MerkleConfiguration? configuration = null,
        IDeepExecutionEngine? deepExecutionEngine = null,
        IHistoryImportService? historyImportService = null,
        string? stateDirectory = null,
        SecretRedactor? redactor = null,
        ICommandLineParser? parser = null)
    {
        _engine = engine;
        _stateStore = stateStore;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _configuration = configuration ?? MerkleConfiguration.Default;
        _deepExecutionEngine = deepExecutionEngine;
        _historyImportService = historyImportService;
        _stateDirectory = stateDirectory ?? Path.GetFullPath(_configuration.Repository.StateDirectory);
        _redactor = redactor ?? SecretRedactor.Default;
        _parser = parser ?? new CommandLineParser();
    }

    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(_parser.Parse(arguments), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MerkleException error)
        {
            await _standardError.WriteLineAsync($"{error.ErrorClass}:{error.Code}: {_redactor.Redact(error.Message)}")
                .ConfigureAwait(false);
            return ExitCode(error.ErrorClass);
        }
        catch (OperationCanceledException)
        {
            await _standardError.WriteLineAsync("Interrupted:RunCancelled: The operation was cancelled.")
                .ConfigureAwait(false);
            return 130;
        }
        catch (Exception error)
        {
            var message = _redactor.Redact(error.Message);
            await _standardError.WriteLineAsync($"AnalysisError:UnexpectedFailure: {message}")
                .ConfigureAwait(false);
            return 4;
        }
    }

    private async Task<int> ExecuteAsync(CliCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case PlanCommand plan:
                {
                    var policy = new PolicyConfiguration(
                        plan.MinSavingsPercent ?? _configuration.Policy.MinSavingsPercent,
                        plan.ConfidenceThreshold ?? _configuration.Policy.ConfidenceThreshold,
                        plan.OnLowConfidence ?? _configuration.Policy.OnLowConfidence,
                        plan.Pedantic ? UnmappedBehavior.Fail : _configuration.Policy.Unmapped);
                    var report = await _engine.PlanAsync(ToPlanRequest(
                        plan.BaseReference,
                        plan.HeadReference,
                        plan.Languages,
                        plan.Pedantic,
                        plan.Solution,
                        policy,
                        deepDefault: false), cancellationToken).ConfigureAwait(false);
                    return await WriteReportAsync(report, plan.Format).ConfigureAwait(false);
                }
            case ObserveCommand observe:
                {
                    var engine = _deepExecutionEngine ?? throw DeepUnavailable();
                    var planRequest = ToPlanRequest(
                            observe.BaseReference,
                            observe.HeadReference,
                            observe.Languages,
                            false,
                            observe.Solution,
                            new PolicyConfiguration(0, 0, "full-suite", UnmappedBehavior.Warn),
                            deepDefault: true);
                    EnsureDeepSelection(planRequest.Languages);
                    var report = await engine.ExecuteAsync(new DeepExecutionRequest(
                        planRequest,
                        DeepExecutionMode.Observe,
                        observe.NoBuild || !_configuration.Execution.Build,
                        Timeout(observe.TimeoutMs),
                        _stateDirectory), cancellationToken).ConfigureAwait(false);
                    return await WriteReportAsync(report, observe.Format).ConfigureAwait(false);
                }
            case RunCommand run:
                {
                    var engine = _deepExecutionEngine ?? throw DeepUnavailable();
                    var policy = new PolicyConfiguration(
                        run.MinSavingsPercent ?? _configuration.Policy.MinSavingsPercent,
                        run.ConfidenceThreshold ?? _configuration.Policy.ConfidenceThreshold,
                        run.OnLowConfidence ?? _configuration.Policy.OnLowConfidence,
                        run.Pedantic ? UnmappedBehavior.Fail : _configuration.Policy.Unmapped);
                    var planRequest = ToPlanRequest(
                            run.BaseReference,
                            run.HeadReference,
                            run.Languages,
                            run.Pedantic,
                            run.Solution,
                            policy,
                            deepDefault: true);
                    EnsureDeepSelection(planRequest.Languages);
                    var report = await engine.ExecuteAsync(new DeepExecutionRequest(
                        planRequest,
                        DeepExecutionMode.RunSelected,
                        run.NoBuild || !_configuration.Execution.Build,
                        Timeout(run.TimeoutMs),
                        _stateDirectory), cancellationToken).ConfigureAwait(false);
                    return await WriteReportAsync(report, run.Format).ConfigureAwait(false);
                }
            case HistoryImportCommand import:
                {
                    var importer = _historyImportService ?? throw new CapabilityException(
                        "HistoryImportUnavailable",
                        "The configured state provider cannot import terminal history.");
                    var source = await ReadReportAsync(import.ReportPath, cancellationToken).ConfigureAwait(false);
                    var report = await importer.ImportAsync(source, cancellationToken).ConfigureAwait(false);
                    return await WriteReportAsync(report, ReportFormat.Text).ConfigureAwait(false);
                }
            case StateStatusCommand:
                {
                    var status = await _stateStore.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    await _standardOutput.WriteLineAsync($"Provider: {status.Provider}").ConfigureAwait(false);
                    await _standardOutput.WriteLineAsync($"Schema version: {status.SchemaVersion}").ConfigureAwait(false);
                    await _standardOutput.WriteLineAsync($"Size: {status.SizeBytes} bytes").ConfigureAwait(false);
                    await _standardOutput.WriteLineAsync($"Last compatible run: {status.LastCompatibleRunId ?? "none"}")
                        .ConfigureAwait(false);
                    await _standardOutput.WriteLineAsync($"Rebuild required: {status.RebuildRequired}").ConfigureAwait(false);
                    return 0;
                }
            case StateResetCommand:
                await _stateStore.ResetAsync(cancellationToken).ConfigureAwait(false);
                await _standardOutput.WriteLineAsync("Local Merkle state reset.").ConfigureAwait(false);
                return 0;
            case HelpCommand:
                await _standardOutput.WriteLineAsync(HelpText).ConfigureAwait(false);
                return 0;
            default:
                throw new ConfigurationException("UnknownCommand", "The command is not supported.");
        }
    }

    private PlanRequest ToPlanRequest(
        string? baseReference,
        string? headReference,
        IReadOnlyList<LanguageSelection> requestedLanguages,
        bool pedantic,
        string? solution,
        PolicyConfiguration policy,
        bool deepDefault)
    {
        IReadOnlyList<LanguageSelection> languages = requestedLanguages.Count > 0
            ? requestedLanguages
            : [.. _configuration.Languages
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new LanguageSelection(item.Key, item.Value.Profile))];
        if (deepDefault && languages.Count == 0)
        {
            languages = [new LanguageSelection("dotnet", "deep")];
        }
        var baseline = baseReference ?? _configuration.Baseline.LocalRef;
        var candidate = headReference ??
                        (baseReference is null && _configuration.Baseline.LocalRef is not null
                            ? "WORKTREE"
                            : null);
        return new PlanRequest(
            baseline,
            candidate,
            languages,
            pedantic,
            solution ?? _configuration.Repository.Solution,
            policy,
            _configuration.Execution.Configuration ?? "Debug",
            EffectivePlatform());
    }

    private string EffectivePlatform()
    {
        var current = OperatingSystem.IsMacOS()
            ? "macos"
            : OperatingSystem.IsLinux()
                ? "linux"
                : throw new CapabilityException(
                    "PlatformUnavailable",
                    "Merkle supports macOS and Linux; native Windows is out of scope.");
        if (_configuration.Execution.Platform is { } configured &&
            !StringComparer.Ordinal.Equals(configured, current))
        {
            throw new ConfigurationException(
                "PlatformMismatch",
                $"Configured platform '{configured}' does not match the current '{current}' runner.");
        }

        return current;
    }

    private static void EnsureDeepSelection(IReadOnlyList<LanguageSelection> languages)
    {
        if (languages.Count != 1 ||
            !StringComparer.Ordinal.Equals(languages[0].Profile, "deep"))
        {
            throw new CapabilityException(
                "DeepProfileRequired",
                "Observe and run require exactly one '<language>:deep' language selection.");
        }
    }

    private TimeSpan? Timeout(int? commandValue)
    {
        var milliseconds = commandValue ?? _configuration.Execution.TimeoutMs;
        return milliseconds.HasValue ? TimeSpan.FromMilliseconds(milliseconds.Value) : null;
    }

    private async Task<int> WriteReportAsync(TerminalReport report, ReportFormat format)
    {
        IReportRenderer renderer = format == ReportFormat.Json
            ? new JsonReportRenderer(_redactor)
            : new TextReportRenderer(_redactor);
        await _standardOutput.WriteAsync(renderer.Render(report)).ConfigureAwait(false);
        return report.TerminalStatus == TerminalStatus.Succeeded
            ? 0
            : ExitCode(report.ErrorClass ?? ErrorClass.AnalysisError);
    }

    private static async ValueTask<TerminalReport> ReadReportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new ConfigurationException(
                    "ImportReportNotFound",
                    $"The terminal report '{path}' does not exist.");
            }

            if (file.Length > 16 * 1024 * 1024)
            {
                throw new ConfigurationException(
                    "ImportReportTooLarge",
                    "The terminal report exceeds the 16 MiB import limit.");
            }

            await using var stream = file.OpenRead();
            return await JsonSerializer.DeserializeAsync(
                       stream,
                       MerkleJsonContext.Default.TerminalReport,
                       cancellationToken).ConfigureAwait(false) ??
                   throw new ConfigurationException(
                       "ImportReportMalformed",
                       "The terminal report is empty.");
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ConfigurationException(
                "ImportReportMalformed",
                "The terminal report could not be read as schema 1 JSON.",
                error);
        }
    }

    private static CapabilityException DeepUnavailable() => new(
        "DeepToolchainUnavailable",
        "The deep execution toolchain is not available in this installation.");

    private static int ExitCode(ErrorClass errorClass) => errorClass switch
    {
        ErrorClass.ConfigurationError => 2,
        ErrorClass.CapabilityError => 3,
        ErrorClass.AnalysisError => 4,
        ErrorClass.TestFailure => 5,
        ErrorClass.PolicyFailure => 6,
        ErrorClass.Interrupted => 130,
        _ => 4
    };

    private const string HelpText = """
        Merkle advisory test planner

        merkle plan --base <ref> --head <ref|WORKTREE>
                    [--languages <language:profile,...>]
                    [--format text|json] [--pedantic] [--solution <path>]
        merkle observe [plan options] [--no-build] [--timeout-ms <milliseconds>]
        merkle run [plan options] [--no-build] [--timeout-ms <milliseconds>]
        merkle state status
        merkle state reset --local
        merkle history import <terminal-report>
        """;
}
