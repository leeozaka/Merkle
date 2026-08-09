using Merkle.Core.Reporting;

namespace Merkle.Tests.Reporting;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("token=abc123", "token=[REDACTED]")]
    [InlineData("Authorization: Bearer abc.def", "Authorization: Bearer [REDACTED]")]
    [InlineData("password: hunter2", "password: [REDACTED]")]
    public void Redact_RemovesCommonSecretForms(string value, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Default.Redact(value));
    }

    [Fact]
    public void Redact_AppliesBoundedCustomPatterns()
    {
        var redactor = new SecretRedactor(["customer-[0-9]+"]);

        Assert.Equal("id=[REDACTED]", redactor.Redact("id=customer-123"));
    }

    [Fact]
    public void Constructor_RejectsUnsafePatternBounds()
    {
        Assert.Throws<ArgumentException>(() => new SecretRedactor(Enumerable.Repeat("x", 33)));
        Assert.Throws<ArgumentException>(() => new SecretRedactor([new string('x', 257)]));
        Assert.Throws<ArgumentException>(() => new SecretRedactor(["(?=lookahead)"]));
    }

    [Fact]
    public void Redact_PreservesNonSecretsAndAppliesPatternsAfterBuiltIns()
    {
        var redactor = new SecretRedactor(["REDACTED"]);

        Assert.Equal("safe text", redactor.Redact("safe text"));
        Assert.Equal("token=[REDACTED]", redactor.Redact("token=abc"));
    }
}
