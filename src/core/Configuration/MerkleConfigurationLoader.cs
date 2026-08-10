using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Core.Configuration;

public interface IMerkleConfigurationLoader
{
    MerkleConfiguration Load(string repositoryRoot);
}

/// <summary>Loads the deliberately small, versioned repository configuration schema.</summary>
public sealed partial class MerkleConfigurationLoader : IMerkleConfigurationLoader
{
    private const string ConfigurationFileName = ".merkle.yml";

    public MerkleConfiguration Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), ConfigurationFileName);
        if (!File.Exists(path))
        {
            return MerkleConfiguration.Default;
        }

        try
        {
            return Bind(new RestrictedYamlParser(path).Parse());
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException("ConfigurationReadFailed", $"Could not read '{ConfigurationFileName}'.", exception);
        }
    }

    private static MerkleConfiguration Bind(YamlMap root)
    {
        EnsureFields(root, "root", "schemaVersion", "repository", "languages", "baseline", "execution", "policy", "history", "security");
        var schemaVersion = RequiredInteger(root, "schemaVersion", "root");
        if (schemaVersion != 1)
        {
            throw Invalid("UnsupportedSchemaVersion", "schemaVersion must be 1.");
        }

        var repository = BindRepository(OptionalMap(root, "repository", "root"));
        var languages = BindLanguages(OptionalMap(root, "languages", "root"));
        var baseline = BindBaseline(OptionalMap(root, "baseline", "root"));
        var execution = BindExecution(OptionalMap(root, "execution", "root"));
        var policy = BindPolicy(OptionalMap(root, "policy", "root"));
        var history = BindHistory(OptionalMap(root, "history", "root"));
        var security = BindSecurity(OptionalMap(root, "security", "root"));
        return new MerkleConfiguration(schemaVersion, repository, languages, baseline, execution, policy, history, security);
    }

    private static RepositoryConfiguration BindRepository(YamlMap? map)
    {
        if (map is null) return new(null, ".merkle", null);
        EnsureFields(map, "repository", "solution", "stateDirectory", "repositoryId");
        var repositoryId = OptionalString(map, "repositoryId", "repository");
        if (repositoryId is not null && !Guid.TryParse(repositoryId, out _))
        {
            throw Invalid(
                "InvalidRepositoryIdentity",
                "repository.repositoryId must be a reviewed UUID shared by trusted clones.");
        }

        return new(
            OptionalString(map, "solution", "repository"),
            OptionalString(map, "stateDirectory", "repository") ?? ".merkle",
            repositoryId);
    }

    private static IReadOnlyDictionary<string, LanguageConfiguration> BindLanguages(YamlMap? map)
    {
        if (map is null) return EmptyLanguages;
        var values = new Dictionary<string, LanguageConfiguration>(StringComparer.Ordinal);
        foreach (var (name, node) in map.Values)
        {
            var normalized = NormalizeLanguage(name);
            var language = AsMap(node, $"languages.{name}");
            EnsureFields(language, $"languages.{name}", "profile");
            var profile = RequiredString(language, "profile", $"languages.{name}");
            RequireOneOf(profile, "profile", "minimal", "semantic", "deep");
            if (!values.TryAdd(normalized, new LanguageConfiguration(profile)))
            {
                throw Invalid("DuplicateLanguage", $"Language '{normalized}' is configured more than once.");
            }
        }
        return new ReadOnlyDictionary<string, LanguageConfiguration>(values);
    }

    private static BaselineConfiguration BindBaseline(YamlMap? map)
    {
        if (map is null) return new(null, "merge-base");
        EnsureFields(map, "baseline", "localRef", "prStrategy");
        var strategy = OptionalString(map, "prStrategy", "baseline") ?? "merge-base";
        RequireOneOf(strategy, "prStrategy", "merge-base");
        return new(OptionalString(map, "localRef", "baseline"), strategy);
    }

    private static ExecutionConfiguration BindExecution(YamlMap? map)
    {
        if (map is null) return new(true, true, null, null, null);
        EnsureFields(map, "execution", "build", "serialObservation", "timeoutMs", "configuration", "platform");
        var timeout = OptionalInteger(map, "timeoutMs", "execution");
        if (timeout is <= 0) throw Invalid("InvalidTimeout", "execution.timeoutMs must be greater than zero.");
        var configuration = OptionalString(map, "configuration", "execution");
        var platform = OptionalString(map, "platform", "execution");
        if (platform is not null) RequireOneOf(platform, "platform", "linux", "macos");
        return new(OptionalBoolean(map, "build", "execution") ?? true, OptionalBoolean(map, "serialObservation", "execution") ?? true, timeout, configuration, platform);
    }

    private static PolicyFileConfiguration BindPolicy(YamlMap? map)
    {
        if (map is null) return new(30, null, null, UnmappedBehavior.Warn);
        EnsureFields(map, "policy", "minSavingsPercent", "confidenceThreshold", "onLowConfidence", "unmapped");
        var savings = OptionalDouble(map, "minSavingsPercent", "policy") ?? 30;
        if (savings is < 0 or > 100) throw Invalid("InvalidSavingsThreshold", "policy.minSavingsPercent must be between 0 and 100.");
        var confidence = OptionalDouble(map, "confidenceThreshold", "policy");
        if (confidence is < 0 or > 1) throw Invalid("InvalidConfidenceThreshold", "policy.confidenceThreshold must be between 0 and 1.");
        var lowConfidence = OptionalString(map, "onLowConfidence", "policy");
        if (lowConfidence is not null) RequireOneOf(lowConfidence, "onLowConfidence", "full-suite", "plan-only", "fail");
        var unmapped = OptionalString(map, "unmapped", "policy") ?? "warn";
        return new(savings, confidence, lowConfidence, unmapped switch { "warn" => UnmappedBehavior.Warn, "fail" => UnmappedBehavior.Fail, _ => throw Invalid("InvalidUnmappedBehavior", "policy.unmapped must be 'warn' or 'fail'.") });
    }

    private static HistoryConfiguration BindHistory(YamlMap? map)
    {
        if (map is null) return new("local", null, null);
        EnsureFields(map, "history", "provider", "endpoint", "tokenEnvironment");
        var provider = OptionalString(map, "provider", "history") ?? "local";
        RequireOneOf(provider, "provider", "local", "remote");
        var endpoint = OptionalString(map, "endpoint", "history");
        var tokenEnvironment = OptionalString(map, "tokenEnvironment", "history");
        if (provider == "remote" &&
            (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
             !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            throw Invalid("InvalidHistoryEndpoint", "history.endpoint must be an absolute HTTPS URI for the remote provider.");
        }

        if (provider == "remote" && string.IsNullOrWhiteSpace(tokenEnvironment))
        {
            throw Invalid("MissingHistoryCredential", "history.tokenEnvironment is required for the remote provider.");
        }

        if (tokenEnvironment is not null &&
            !MyRegex().IsMatch(tokenEnvironment))
        {
            throw Invalid(
                "InvalidHistoryCredential",
                "history.tokenEnvironment must be an environment variable name, not a credential value.");
        }

        if (provider == "local" && (endpoint is not null || tokenEnvironment is not null))
        {
            throw Invalid("InvalidHistoryConfiguration", "Local history cannot configure a remote endpoint or credential.");
        }

        return new(provider, endpoint, tokenEnvironment);
    }

    private static SecurityConfiguration BindSecurity(YamlMap? map)
    {
        if (map is null) return new([]);
        EnsureFields(map, "security", "redactionPatterns");
        if (!map.Values.TryGetValue("redactionPatterns", out var node)) return new([]);
        var patterns = node switch
        {
            YamlNull => Array.Empty<string>(),
            YamlList list => [.. list.Values.Select((value, index) => Scalar(value, $"security.redactionPatterns[{index}]"))],
            _ => throw Invalid("InvalidConfigurationValue", "security.redactionPatterns must be a list.")
        };
        if (patterns.Length > 32) throw Invalid("TooManyRedactionPatterns", "security.redactionPatterns may contain at most 32 patterns.");
        if (patterns.Any(pattern => pattern.Length > 256)) throw Invalid("RedactionPatternTooLong", "Each redaction pattern may contain at most 256 characters.");
        foreach (var pattern in patterns)
        {
            try
            {
                _ = new Regex(
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new ConfigurationException(
                    "InvalidRedactionPattern",
                    "A security.redactionPatterns entry is not a valid bounded non-backtracking regular expression.",
                    error);
            }
        }
        return new(Array.AsReadOnly(patterns));
    }

    private static readonly IReadOnlyDictionary<string, LanguageConfiguration> EmptyLanguages = new ReadOnlyDictionary<string, LanguageConfiguration>(new Dictionary<string, LanguageConfiguration>());
    private static string NormalizeLanguage(string name) => name.Trim().ToLowerInvariant() switch { "c#" or "csharp" or ".net" => "dotnet", "go" => "golang", var normalized when normalized.Length > 0 => normalized, _ => throw Invalid("InvalidLanguage", "Language names cannot be empty.") };
    private static void EnsureFields(YamlMap map, string scope, params string[] allowed) { foreach (var key in map.Values.Keys) if (!allowed.Contains(key, StringComparer.Ordinal)) throw Invalid("UnknownConfigurationField", $"Unknown field '{scope}.{key}'."); }
    private static YamlMap? OptionalMap(YamlMap map, string key, string scope) => map.Values.TryGetValue(key, out var value) ? AsMap(value, $"{scope}.{key}") : null;
    private static YamlMap AsMap(YamlNode node, string scope) => node as YamlMap ?? throw Invalid("InvalidConfigurationValue", $"'{scope}' must be a mapping.");
    private static string RequiredString(YamlMap map, string key, string scope) => OptionalString(map, key, scope) ?? throw Invalid("MissingConfigurationValue", $"'{scope}.{key}' is required.");
    private static string? OptionalString(YamlMap map, string key, string scope) => map.Values.TryGetValue(key, out var value) && value is not YamlNull ? Scalar(value, $"{scope}.{key}") : null;
    private static int RequiredInteger(YamlMap map, string key, string scope) => OptionalInteger(map, key, scope) ?? throw Invalid("MissingConfigurationValue", $"'{scope}.{key}' is required.");
    private static int? OptionalInteger(YamlMap map, string key, string scope) => map.Values.TryGetValue(key, out var value) && value is not YamlNull ? int.TryParse(Scalar(value, $"{scope}.{key}"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid("InvalidConfigurationValue", $"'{scope}.{key}' must be an integer.") : null;
    private static double? OptionalDouble(YamlMap map, string key, string scope) => map.Values.TryGetValue(key, out var value) && value is not YamlNull ? double.TryParse(Scalar(value, $"{scope}.{key}"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid("InvalidConfigurationValue", $"'{scope}.{key}' must be a number.") : null;
    private static bool? OptionalBoolean(YamlMap map, string key, string scope) => map.Values.TryGetValue(key, out var value) && value is not YamlNull ? Scalar(value, $"{scope}.{key}") switch { "true" => true, "false" => false, _ => throw Invalid("InvalidConfigurationValue", $"'{scope}.{key}' must be true or false.") } : null;
    private static string Scalar(YamlNode node, string scope) => node is YamlScalar scalar ? scalar.Value : throw Invalid("InvalidConfigurationValue", $"'{scope}' must be a scalar.");
    private static void RequireOneOf(string value, string field, params string[] values) { if (!values.Contains(value, StringComparer.Ordinal)) throw Invalid("InvalidConfigurationValue", $"'{field}' must be one of: {string.Join(", ", values)}."); }
    private static ConfigurationException Invalid(string code, string message) => new(code, message);

    private abstract record YamlNode;
    private sealed record YamlMap(Dictionary<string, YamlNode> Values) : YamlNode;
    private sealed record YamlList(List<YamlNode> Values) : YamlNode;
    private sealed record YamlScalar(string Value) : YamlNode;
    private sealed record YamlNull : YamlNode;

    private sealed class RestrictedYamlParser(string path)
    {
        private readonly string[] _lines = File.ReadAllLines(path);
        private int _index;

        public YamlMap Parse()
        {
            SkipIgnorable();
            if (_index == _lines.Length) throw Invalid("EmptyConfiguration", "Configuration file cannot be empty.");
            var root = ParseMap(0);
            SkipIgnorable();
            if (_index != _lines.Length) throw Invalid("InvalidYamlIndentation", $"Unexpected indentation on line {_index + 1}.");
            return root;
        }

        private YamlMap ParseMap(int indent)
        {
            var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            while (TryCurrent(indent, out var content))
            {
                if (content.StartsWith("- ", StringComparison.Ordinal)) break;
                var colon = content.IndexOf(':');
                if (colon <= 0) throw Invalid("InvalidYamlSyntax", $"Expected a key and ':' on line {_index + 1}.");
                var key = content[..colon].Trim();
                if (key.Length == 0 || key.Contains(' ')) throw Invalid("InvalidYamlSyntax", $"Invalid key on line {_index + 1}.");
                if (!values.TryAdd(key, null!)) throw Invalid("DuplicateConfigurationField", $"Duplicate field '{key}' on line {_index + 1}.");
                var remainder = StripComment(content[(colon + 1)..]).Trim();
                _index++;
                values[key] = remainder.Length > 0 ? ParseScalar(remainder) : ParseNested(indent, key);
            }
            return new(values);
        }

        private YamlNode ParseNested(int parentIndent, string key)
        {
            SkipIgnorable();
            if (_index == _lines.Length || IndentOf(_lines[_index]) <= parentIndent) return new YamlNull();
            if (IndentOf(_lines[_index]) != parentIndent + 2) throw Invalid("InvalidYamlIndentation", $"Expected two-space indentation beneath '{key}' on line {_index + 1}.");
            return ContentOf(_lines[_index]).StartsWith("- ", StringComparison.Ordinal) ? ParseList(parentIndent + 2) : ParseMap(parentIndent + 2);
        }

        private YamlList ParseList(int indent)
        {
            var values = new List<YamlNode>();
            while (TryCurrent(indent, out var content) && content.StartsWith("- ", StringComparison.Ordinal))
            {
                var value = StripComment(content[2..]).Trim();
                if (value.Length == 0) throw Invalid("InvalidYamlSyntax", $"List item on line {_index + 1} must be a scalar.");
                values.Add(ParseScalar(value));
                _index++;
            }
            return new(values);
        }

        private bool TryCurrent(int indent, out string content)
        {
            SkipIgnorable();
            content = string.Empty;
            if (_index == _lines.Length) return false;
            var actual = IndentOf(_lines[_index]);
            if (actual < indent) return false;
            if (actual > indent) throw Invalid("InvalidYamlIndentation", $"Unexpected indentation on line {_index + 1}.");
            content = ContentOf(_lines[_index]);
            return true;
        }

        private void SkipIgnorable()
        {
            while (_index < _lines.Length &&
                (string.IsNullOrWhiteSpace(_lines[_index]) || ContentOf(_lines[_index]).StartsWith('#')))
            {
                _index++;
            }
        }
        private static int IndentOf(string line) { if (line.Contains('\t')) throw Invalid("InvalidYamlIndentation", "Tabs are not allowed for indentation."); return line.Length - line.TrimStart(' ').Length; }
        private static string ContentOf(string line) => line.TrimStart(' ');
        private static YamlNode ParseScalar(string value) { if (value == "null" || value == "~") return new YamlNull(); if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))) return new YamlScalar(value[1..^1]); if (value.StartsWith('[') || value.StartsWith('{')) throw Invalid("UnsupportedYamlFeature", "Inline collections are not supported."); return new YamlScalar(value); }
        private static string StripComment(string value) { var quote = '\0'; for (var i = 0; i < value.Length; i++) { var character = value[i]; if (character is '\'' or '"') quote = quote == '\0' ? character : quote == character ? '\0' : quote; else if (character == '#' && quote == '\0' && (i == 0 || char.IsWhiteSpace(value[i - 1]))) return value[..i]; } return value; }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}

public sealed record MerkleConfiguration(int SchemaVersion, RepositoryConfiguration Repository, IReadOnlyDictionary<string, LanguageConfiguration> Languages, BaselineConfiguration Baseline, ExecutionConfiguration Execution, PolicyFileConfiguration Policy, HistoryConfiguration History, SecurityConfiguration Security)
{
    public static MerkleConfiguration Default { get; } = new(1, new(null, ".merkle", null), new ReadOnlyDictionary<string, LanguageConfiguration>(new Dictionary<string, LanguageConfiguration>()), new(null, "merge-base"), new(true, true, null, null, null), new(30, null, null, UnmappedBehavior.Warn), new("local", null, null), new([]));
}

public sealed record RepositoryConfiguration(string? Solution, string StateDirectory, string? RepositoryId);
public sealed record LanguageConfiguration(string Profile);
public sealed record BaselineConfiguration(string? LocalRef, string PrStrategy);
public sealed record ExecutionConfiguration(bool Build, bool SerialObservation, int? TimeoutMs, string? Configuration, string? Platform);
public sealed record PolicyFileConfiguration(double MinSavingsPercent, double? ConfidenceThreshold, string? OnLowConfidence, UnmappedBehavior Unmapped);
public sealed record HistoryConfiguration(string Provider, string? Endpoint, string? TokenEnvironment);
public sealed record SecurityConfiguration(IReadOnlyList<string> RedactionPatterns);
