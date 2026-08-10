using Merkle.Core.Domain;

namespace Merkle.Cli;

public abstract record CliCommand;

public sealed record PlanCommand(
    string? BaseReference,
    string? HeadReference,
    IReadOnlyList<LanguageSelection> Languages,
    ReportFormat Format,
    bool Pedantic,
    string? Solution = null,
    double? MinSavingsPercent = null,
    double? ConfidenceThreshold = null,
    string? OnLowConfidence = null) : CliCommand;

public sealed record ObserveCommand(
    string? BaseReference,
    string? HeadReference,
    IReadOnlyList<LanguageSelection> Languages,
    ReportFormat Format,
    string? Solution,
    bool NoBuild,
    int? TimeoutMs) : CliCommand;

public sealed record RunCommand(
    string? BaseReference,
    string? HeadReference,
    IReadOnlyList<LanguageSelection> Languages,
    ReportFormat Format,
    bool Pedantic,
    string? Solution,
    bool NoBuild,
    int? TimeoutMs,
    double? MinSavingsPercent,
    double? ConfidenceThreshold,
    string? OnLowConfidence) : CliCommand;

public sealed record StateStatusCommand : CliCommand;

public sealed record StateResetCommand : CliCommand;

public sealed record HistoryImportCommand(string ReportPath) : CliCommand;

public sealed record HelpCommand : CliCommand;
