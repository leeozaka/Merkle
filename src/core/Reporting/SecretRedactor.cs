using System.Text.RegularExpressions;

namespace Merkle.Core.Reporting;

public sealed partial class SecretRedactor
{
    public static SecretRedactor Default { get; } = new();

    private readonly IReadOnlyList<Regex> _customPatterns;

    public SecretRedactor(IEnumerable<string>? customPatterns = null)
    {
        var patterns = (customPatterns ?? []).ToArray();
        if (patterns.Length > 32)
        {
            throw new ArgumentException("At most 32 custom redaction patterns are allowed.", nameof(customPatterns));
        }

        _customPatterns = [.. patterns.Select(pattern =>
        {
            if (pattern.Length > 256)
            {
                throw new ArgumentException("Custom redaction patterns may contain at most 256 characters.", nameof(customPatterns));
            }

            try
            {
                return new Regex(
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException(
                    "Custom redaction patterns must support bounded non-backtracking evaluation.",
                    nameof(customPatterns),
                    error);
            }
        })];
    }

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = TokenPattern().Replace(value, "$1[REDACTED]");
        redacted = PasswordPattern().Replace(redacted, "$1[REDACTED]");
        redacted = BearerPattern().Replace(redacted, "$1[REDACTED]");
        foreach (var pattern in _customPatterns)
        {
            redacted = string.Join(
                "[REDACTED]",
                redacted.Split("[REDACTED]", StringSplitOptions.None)
                    .Select(segment => pattern.Replace(segment, "[REDACTED]")));
        }

        return redacted;
    }

    [GeneratedRegex("(?i)(token\\s*[=:]\\s*)([^\\s,;\"]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("(?i)(password\\s*[=:]\\s*)([^\\s,;\"]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex("(?i)(authorization\\s*:\\s*bearer\\s+)([^\\s,;\"]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();
}
