using Merkle.Core.Errors;

namespace Merkle.Build;

public interface IBuildCommandLineParser
{
    BuildRequest Parse(IReadOnlyList<string> arguments);
}

public sealed class BuildCommandLineParser : IBuildCommandLineParser
{
    public BuildRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var command = BuildCommand.Build;
        var argumentIndex = 0;
        if (arguments.Count > 0 && arguments[0] is "build" or "publish")
        {
            command = arguments[0] == "publish" ? BuildCommand.Publish : BuildCommand.Build;
            argumentIndex++;
        }
        else if (arguments.Count > 0 && arguments[0].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ConfigurationException("UnknownOption", $"Unknown option '{arguments[0]}'.");
        }
        else if (arguments.Count > 0)
        {
            throw new ConfigurationException("UnknownCommand", $"Unknown command '{arguments[0]}'.");
        }

        var adapters = new List<string>();
        var policy = AdapterBuildPolicy.Strict;
        var scheduling = BuildScheduling.Sequential;
        int? maxParallel = null;
        var runTests = false;
        var noWarnings = false;
        var configuration = command == BuildCommand.Publish ? "Release" : "Debug";
        string? runtimeIdentifier = null;
        string? outputPath = null;
        string? reportPath = null;
        var format = BuildOutputFormat.Text;
        var clean = false;
        var maxParallelSpecified = false;

        for (; argumentIndex < arguments.Count; argumentIndex++)
        {
            var option = arguments[argumentIndex];
            switch (option)
            {
                case "--adapters":
                    AddAdapters(adapters, ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--adapter-policy":
                    policy = ParsePolicy(ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--builds":
                    scheduling = ParseScheduling(ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--max-parallel":
                    maxParallelSpecified = true;
                    maxParallel = ParsePositiveInteger(ReadValue(arguments, ref argumentIndex, option), option);
                    break;
                case "--test":
                    runTests = true;
                    break;
                case "--no-warnings":
                    noWarnings = true;
                    break;
                case "--configuration":
                    configuration = ParseConfiguration(ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--runtime":
                    runtimeIdentifier = BuildRuntimeIdentifier.ValidateCurrent(ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--output":
                    outputPath = ReadValue(arguments, ref argumentIndex, option);
                    break;
                case "--report":
                    reportPath = ReadValue(arguments, ref argumentIndex, option);
                    break;
                case "--format":
                    format = ParseFormat(ReadValue(arguments, ref argumentIndex, option));
                    break;
                case "--clean":
                    clean = true;
                    break;
                default:
                    throw new ConfigurationException("UnknownOption", $"Unknown option '{option}'.");
            }
        }

        if (adapters.Count == 0)
        {
            adapters.Add("dotnet");
        }

        if (maxParallelSpecified && scheduling == BuildScheduling.Sequential)
        {
            throw InvalidValue("--max-parallel cannot be used with sequential builds.");
        }

        if (command == BuildCommand.Publish && runtimeIdentifier is null)
        {
            runtimeIdentifier = BuildRuntimeIdentifier.Current;
        }

        return new BuildRequest(
            command,
            adapters,
            policy,
            scheduling,
            maxParallel,
            runTests,
            noWarnings,
            configuration,
            runtimeIdentifier,
            outputPath,
            reportPath,
            format,
            clean);
    }

    private static void AddAdapters(List<string> adapters, string value)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace) || values.Any(value => value.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidValue("At least one adapter must be selected.");
        }

        foreach (var item in values)
        {
            var normalized = NormalizeAdapter(item);
            if (!adapters.Contains(normalized, StringComparer.Ordinal))
            {
                adapters.Add(normalized);
            }
        }
    }

    private static string NormalizeAdapter(string value) => value.ToLowerInvariant() switch
    {
        "go" => "golang",
        "dotnet" or "golang" or "python" or "java" or "all" => value.ToLowerInvariant(),
        _ => value.ToLowerInvariant()
    };

    private static AdapterBuildPolicy ParsePolicy(string value) => value.ToLowerInvariant() switch
    {
        "strict" => AdapterBuildPolicy.Strict,
        "best-effort" => AdapterBuildPolicy.BestEffort,
        _ => throw InvalidValue($"Unknown adapter policy '{value}'.")
    };

    private static BuildScheduling ParseScheduling(string value) => value.ToLowerInvariant() switch
    {
        "sequential" => BuildScheduling.Sequential,
        "parallel" => BuildScheduling.Parallel,
        _ => throw InvalidValue($"Unknown build scheduling '{value}'.")
    };

    private static string ParseConfiguration(string value) => value switch
    {
        "Debug" or "Release" => value,
        _ => throw InvalidValue($"Unsupported configuration '{value}'.")
    };

    private static BuildOutputFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "text" => BuildOutputFormat.Text,
        "json" => BuildOutputFormat.Json,
        _ => throw InvalidValue($"Unknown output format '{value}'.")
    };

    private static int ParsePositiveInteger(string value, string option)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw InvalidValue($"Option '{option}' must be a positive integer.");
        }

        return result;
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ConfigurationException("MissingOptionValue", $"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static ConfigurationException InvalidValue(string message) =>
        new("InvalidOptionValue", message);
}
