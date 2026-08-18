using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Cli;

public interface ICommandLineParser
{
    CliCommand Parse(IReadOnlyList<string> arguments);
}

public sealed class CommandLineParser : ICommandLineParser
{
    public CliCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
        {
            return new HelpCommand();
        }

        return arguments[0] switch
        {
            "plan" => ParsePlan([.. arguments.Skip(1)]),
            "observe" => ParseObserve([.. arguments.Skip(1)]),
            "run" => ParseRun([.. arguments.Skip(1)]),
            "state" => ParseState([.. arguments.Skip(1)]),
            "history" => ParseHistory([.. arguments.Skip(1)]),
            _ => throw new ConfigurationException("UnknownCommand", $"Unknown command '{arguments[0]}'.")
        };
    }

    private static PlanCommand ParsePlan(IReadOnlyList<string> arguments)
    {
        string? baseline = null;
        string? candidate = null;
        var languages = new List<LanguageSelection>();
        var format = ReportFormat.Text;
        var pedantic = false;
        string? solution = null;
        double? minSavings = null;
        double? confidence = null;
        string? onLowConfidence = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            switch (option)
            {
                case "--base":
                    baseline = ReadValue(arguments, ref index, option);
                    break;
                case "--head":
                    candidate = ReadValue(arguments, ref index, option);
                    break;
                case "--languages":
                    languages.AddRange(ParseLanguages(ReadValue(arguments, ref index, option)));
                    break;
                case "--format":
                    format = ParseFormat(ReadValue(arguments, ref index, option));
                    break;
                case "--pedantic":
                    pedantic = true;
                    break;
                case "--solution":
                    solution = ReadValue(arguments, ref index, option);
                    break;
                case "--min-savings-percent":
                    minSavings = ParseDouble(ReadValue(arguments, ref index, option), option, 0, 100);
                    break;
                case "--confidence-threshold":
                    confidence = ParseDouble(ReadValue(arguments, ref index, option), option, 0, 1);
                    break;
                case "--on-low-confidence":
                    onLowConfidence = ParseLowConfidenceAction(ReadValue(arguments, ref index, option));
                    break;
                default:
                    throw new ConfigurationException("UnknownOption", $"Unknown option '{option}'.");
            }
        }

        return new PlanCommand(
            baseline,
            candidate,
            languages,
            format,
            pedantic,
            solution,
            minSavings,
            confidence,
            onLowConfidence);
    }

    private static ObserveCommand ParseObserve(IReadOnlyList<string> arguments)
    {
        var options = ParseExecution(arguments, allowPolicy: false);
        return new ObserveCommand(
            options.Baseline,
            options.Candidate,
            options.Languages,
            options.Format,
            options.Solution,
            options.NoBuild,
            options.TimeoutMs);
    }

    private static RunCommand ParseRun(IReadOnlyList<string> arguments)
    {
        var options = ParseExecution(arguments, allowPolicy: true);
        return new RunCommand(
            options.Baseline,
            options.Candidate,
            options.Languages,
            options.Format,
            options.Pedantic,
            options.Solution,
            options.NoBuild,
            options.TimeoutMs,
            options.MinSavingsPercent,
            options.ConfidenceThreshold,
            options.OnLowConfidence);
    }

    private static ExecutionOptions ParseExecution(IReadOnlyList<string> arguments, bool allowPolicy)
    {
        string? baseline = null;
        string? candidate = null;
        var languages = new List<LanguageSelection>();
        var format = ReportFormat.Text;
        var pedantic = false;
        string? solution = null;
        var noBuild = false;
        int? timeoutMs = null;
        double? minSavings = null;
        double? confidence = null;
        string? onLowConfidence = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            switch (option)
            {
                case "--base":
                    baseline = ReadValue(arguments, ref index, option);
                    break;
                case "--head":
                    candidate = ReadValue(arguments, ref index, option);
                    break;
                case "--languages":
                    languages.AddRange(ParseLanguages(ReadValue(arguments, ref index, option)));
                    break;
                case "--format":
                    format = ParseFormat(ReadValue(arguments, ref index, option));
                    break;
                case "--solution":
                    solution = ReadValue(arguments, ref index, option);
                    break;
                case "--no-build":
                    noBuild = true;
                    break;
                case "--timeout-ms":
                    timeoutMs = ParsePositiveInteger(ReadValue(arguments, ref index, option), option);
                    break;
                case "--pedantic" when allowPolicy:
                    pedantic = true;
                    break;
                case "--min-savings-percent" when allowPolicy:
                    minSavings = ParseDouble(ReadValue(arguments, ref index, option), option, 0, 100);
                    break;
                case "--confidence-threshold" when allowPolicy:
                    confidence = ParseDouble(ReadValue(arguments, ref index, option), option, 0, 1);
                    break;
                case "--on-low-confidence" when allowPolicy:
                    onLowConfidence = ParseLowConfidenceAction(ReadValue(arguments, ref index, option));
                    break;
                default:
                    throw new ConfigurationException("UnknownOption", $"Unknown option '{option}'.");
            }
        }

        return new ExecutionOptions(
            baseline,
            candidate,
            languages,
            format,
            pedantic,
            solution,
            noBuild,
            timeoutMs,
            minSavings,
            confidence,
            onLowConfidence);
    }

    private static CliCommand ParseState(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1 && arguments[0] == "status")
        {
            return new StateStatusCommand();
        }

        if (arguments.Count == 2 && arguments[0] == "reset" && arguments[1] == "--local")
        {
            return new StateResetCommand();
        }

        if (arguments.Count > 0 && arguments[0] == "reset")
        {
            throw new ConfigurationException(
                "LocalResetConfirmationRequired",
                "State reset requires the explicit --local flag.");
        }

        throw new ConfigurationException("UnknownStateCommand", "Expected 'state status' or 'state reset --local'.");
    }

    private static CliCommand ParseHistory(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 && arguments[0] == "import")
        {
            return new HistoryImportCommand(arguments[1]);
        }

        throw new ConfigurationException("UnknownHistoryCommand", "Expected 'history import <terminal-report>'.");
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ConfigurationException("MissingOptionValue", $"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static IEnumerable<LanguageSelection> ParseLanguages(string value)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator <= 0 || separator == item.Length - 1 || item.IndexOf(':', separator + 1) >= 0)
            {
                throw new ConfigurationException(
                    "InvalidLanguageSelection",
                    $"Language selection '{item}' must use language:profile syntax.");
            }

            var language = item[..separator].ToLowerInvariant() switch
            {
                "go" => "golang",
                var normalized => normalized
            };

            yield return new LanguageSelection(language, item[(separator + 1)..].ToLowerInvariant());
        }
    }

    private static ReportFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "text" => ReportFormat.Text,
        "json" => ReportFormat.Json,
        _ => throw new ConfigurationException("InvalidReportFormat", "Report format must be 'text' or 'json'.")
    };

    private static int ParsePositiveInteger(string value, string option)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) || parsed <= 0)
        {
            throw new ConfigurationException("InvalidOptionValue", $"Option '{option}' requires a positive integer.");
        }

        return parsed;
    }

    private static double ParseDouble(string value, string option, double minimum, double maximum)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ConfigurationException(
                "InvalidOptionValue",
                $"Option '{option}' must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static string ParseLowConfidenceAction(string value) => value switch
    {
        "full-suite" or "plan-only" or "fail" => value,
        _ => throw new ConfigurationException(
            "InvalidOptionValue",
            "--on-low-confidence must be 'full-suite', 'plan-only', or 'fail'.")
    };

    private sealed record ExecutionOptions(
        string? Baseline,
        string? Candidate,
        IReadOnlyList<LanguageSelection> Languages,
        ReportFormat Format,
        bool Pedantic,
        string? Solution,
        bool NoBuild,
        int? TimeoutMs,
        double? MinSavingsPercent,
        double? ConfidenceThreshold,
        string? OnLowConfidence);
}
