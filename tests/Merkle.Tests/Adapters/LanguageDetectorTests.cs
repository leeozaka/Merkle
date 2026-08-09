using Merkle.Core.Adapters;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Tests.Adapters;

public sealed class LanguageDetectorTests
{
    [Fact]
    public void Detect_ReturnsEveryLanguageAndItsEvidence()
    {
        var detector = LanguageDetector.CreateDefault();

        var detected = detector.Detect(["src/App/App.csproj", "web/app.ts", "go.mod"]);

        Assert.Equal(["dotnet", "golang", "typescript"], detected.Select(item => item.Language));
        Assert.All(detected, item => Assert.NotEmpty(item.Evidence));
    }

    [Fact]
    public void ValidateSelection_ListsAllDetectionsForMixedRepository()
    {
        var detector = LanguageDetector.CreateDefault();
        var detected = detector.Detect(["src/App/App.csproj", "web/app.ts"]);

        var error = Assert.Throws<ConfigurationException>(() =>
            LanguageDetector.ValidateSelection(detected, []));

        Assert.Equal("MixedLanguagesRequireSelection", error.Code);
        Assert.Contains("dotnet", error.Message, StringComparison.Ordinal);
        Assert.Contains("typescript", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSelection_RejectsLanguageWithoutDetectionEvidence()
    {
        var detector = LanguageDetector.CreateDefault();
        var detected = detector.Detect(["src/App/App.csproj"]);

        var error = Assert.Throws<ConfigurationException>(() => LanguageDetector.ValidateSelection(
            detected,
            [new LanguageSelection("golang", "minimal")]));

        Assert.Equal("SelectedLanguageNotDetected", error.Code);
    }
}
