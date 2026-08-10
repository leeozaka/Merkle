using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Core.Adapters;

public sealed class LanguageDetector
{
    private readonly IReadOnlyList<LanguageRule> _rules;

    public LanguageDetector(IEnumerable<LanguageRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules.OrderBy(rule => rule.Language, StringComparer.Ordinal)];
    }

    public static LanguageDetector CreateDefault() => new([
        new LanguageRule("dotnet", [".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"]),
        new LanguageRule("golang", [".go", "go.mod", "go.work"]),
        new LanguageRule("typescript", [".ts", ".tsx", "tsconfig.json"])
    ]);

    public IReadOnlyList<DetectedLanguage> Detect(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalizedPaths = paths
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return [.. _rules
            .Select(rule => Detect(rule, normalizedPaths))
            .Where(result => result is not null)
            .Cast<DetectedLanguage>()
            .OrderBy(result => result.Language, StringComparer.Ordinal)];
    }

    public static void ValidateSelection(
        IReadOnlyList<DetectedLanguage> detected,
        IReadOnlyList<LanguageSelection> selected)
    {
        if (detected.Count > 1 && selected.Count == 0)
        {
            var details = string.Join(", ", detected.Select(language => language.Language));
            throw new ConfigurationException(
                "MixedLanguagesRequireSelection",
                $"Multiple languages were detected; select the intended languages explicitly: {details}.");
        }

        var detectedNames = detected.Select(item => item.Language).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailable = selected
            .Where(selection => !detectedNames.Contains(selection.Language))
            .Select(selection => selection.Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unavailable.Length > 0)
        {
            throw new ConfigurationException(
                "SelectedLanguageNotDetected",
                $"Selected language was not detected: {string.Join(", ", unavailable)}.");
        }
    }

    private static DetectedLanguage? Detect(LanguageRule rule, IReadOnlyList<string> paths)
    {
        var matches = paths
            .Where(path => rule.Patterns.Any(pattern => Matches(path, pattern)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var evidence = matches
            .Take(20)
            .Select(path => new DetectionEvidence(IsManifest(path) ? "manifest" : "source", path))
            .ToArray();
        return new DetectedLanguage(rule.Language, "high", evidence);
    }

    private static bool Matches(string path, string pattern) =>
        pattern.Length > 0 && pattern[0] == '.'
            ? path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), pattern);

    private static bool IsManifest(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" ||
               Path.GetFileName(path) is "go.mod" or "go.work" or "tsconfig.json";
    }
}

public sealed record LanguageRule(string Language, IReadOnlyList<string> Patterns);
