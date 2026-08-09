using Merkle.Core.Configuration;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Tests.Configuration;

public sealed class MerkleConfigurationLoaderTests : IDisposable
{
    private readonly string _repositoryRoot = Path.Combine(Path.GetTempPath(), $"merkle-config-{Guid.NewGuid():N}");
    private readonly IMerkleConfigurationLoader _loader = new MerkleConfigurationLoader();

    public MerkleConfigurationLoaderTests() => Directory.CreateDirectory(_repositoryRoot);

    [Fact]
    public void Load_WhenFileIsMissing_ReturnsSchemaVersionOneDefaults()
    {
        var configuration = _loader.Load(_repositoryRoot);

        Assert.Equal(1, configuration.SchemaVersion);
        Assert.Equal(".merkle", configuration.Repository.StateDirectory);
        Assert.Empty(configuration.Languages);
        Assert.True(configuration.Execution.Build);
        Assert.True(configuration.Execution.SerialObservation);
        Assert.Null(configuration.Execution.TimeoutMs);
        Assert.Equal(30, configuration.Policy.MinSavingsPercent);
        Assert.Equal(UnmappedBehavior.Warn, configuration.Policy.Unmapped);
        Assert.Equal("local", configuration.History.Provider);
    }

    [Fact]
    public void Load_BindsDocumentedFieldsAndNormalizesLanguageNames()
    {
        Write("""
            # a reviewed repository policy
            schemaVersion: 1
            repository:
              solution: App.sln
              stateDirectory: .state
              repositoryId: 019fde48-89db-7230-b822-c9f25c100df8
            languages:
              CSharp:
                profile: deep
              go:
                profile: minimal
            baseline:
              localRef: main
              prStrategy: merge-base
            execution:
              build: false
              serialObservation: false
              timeoutMs: 2500
              configuration: Release
              platform: linux
            policy:
              minSavingsPercent: 12.5
              confidenceThreshold: 0.8
              onLowConfidence: full-suite
              unmapped: fail
            history:
              provider: local
            security:
              redactionPatterns:
                - token-[a-z]+ # comment
                - "password=.*"
            """);

        var configuration = _loader.Load(_repositoryRoot);

        Assert.Equal("App.sln", configuration.Repository.Solution);
        Assert.Equal(".state", configuration.Repository.StateDirectory);
        Assert.Equal("019fde48-89db-7230-b822-c9f25c100df8", configuration.Repository.RepositoryId);
        Assert.Equal("deep", configuration.Languages["dotnet"].Profile);
        Assert.Equal("minimal", configuration.Languages["golang"].Profile);
        Assert.Equal("main", configuration.Baseline.LocalRef);
        Assert.False(configuration.Execution.Build);
        Assert.False(configuration.Execution.SerialObservation);
        Assert.Equal(2500, configuration.Execution.TimeoutMs);
        Assert.Equal("Release", configuration.Execution.Configuration);
        Assert.Equal("linux", configuration.Execution.Platform);
        Assert.Equal(12.5, configuration.Policy.MinSavingsPercent);
        Assert.Equal(0.8, configuration.Policy.ConfidenceThreshold);
        Assert.Equal("full-suite", configuration.Policy.OnLowConfidence);
        Assert.Equal(UnmappedBehavior.Fail, configuration.Policy.Unmapped);
        Assert.Equal(["token-[a-z]+", "password=.*"], configuration.Security.RedactionPatterns);
    }

    [Theory]
    [InlineData("schemaVersion: 2", "UnsupportedSchemaVersion")]
    [InlineData("schemaVersion: 1\nunknown: value", "UnknownConfigurationField")]
    [InlineData("schemaVersion: 1\nschemaVersion: 1", "DuplicateConfigurationField")]
    [InlineData("schemaVersion: 1\n repository: {}", "InvalidYamlIndentation")]
    [InlineData("schemaVersion: 1\nrepository:\n\tsolution: App.sln", "InvalidYamlIndentation")]
    public void Load_RejectsUnsupportedOrAmbiguousDocuments(string document, string code)
    {
        Write(document);

        var error = Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot));

        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("languages:\n  dotnet:\n    profile: unknown", "InvalidConfigurationValue")]
    [InlineData("languages:\n  dotnet:\n    profile: minimal\n  .net:\n    profile: deep", "DuplicateLanguage")]
    [InlineData("execution:\n  build: yes", "InvalidConfigurationValue")]
    [InlineData("execution:\n  timeoutMs: 0", "InvalidTimeout")]
    [InlineData("execution:\n  platform: windows", "InvalidConfigurationValue")]
    [InlineData("policy:\n  minSavingsPercent: 101", "InvalidSavingsThreshold")]
    [InlineData("policy:\n  confidenceThreshold: -0.1", "InvalidConfidenceThreshold")]
    [InlineData("policy:\n  onLowConfidence: selected", "InvalidConfigurationValue")]
    [InlineData("policy:\n  unmapped: ignore", "InvalidUnmappedBehavior")]
    [InlineData("history:\n  provider: remote", "InvalidHistoryEndpoint")]
    [InlineData("history:\n  provider: remote\n  endpoint: http://state.example.test\n  tokenEnvironment: TOKEN", "InvalidHistoryEndpoint")]
    [InlineData("history:\n  provider: remote\n  endpoint: https://state.example.test", "MissingHistoryCredential")]
    [InlineData("history:\n  endpoint: https://state.example.test", "InvalidHistoryConfiguration")]
    [InlineData("repository:\n  repositoryId: team/app", "InvalidRepositoryIdentity")]
    public void Load_RejectsInvalidValues(string body, string code)
    {
        Write($"schemaVersion: 1\n{body}");

        var error = Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Load_RejectsNonListAndOversizedSecurityPatterns()
    {
        Write("""
            schemaVersion: 1
            security:
              redactionPatterns: secret
            """);
        Assert.Equal("InvalidConfigurationValue", Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot)).Code);

        Write($"schemaVersion: 1\nsecurity:\n  redactionPatterns:\n    - {new string('x', 257)}");
        Assert.Equal("RedactionPatternTooLong", Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot)).Code);

        Write("schemaVersion: 1\nsecurity:\n  redactionPatterns:\n" + string.Join("\n", Enumerable.Range(0, 33).Select(index => $"    - pattern-{index}")));
        Assert.Equal("TooManyRedactionPatterns", Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot)).Code);
    }

    [Fact]
    public void Load_ReturnsImmutableCollections()
    {
        Write("""
            schemaVersion: 1
            languages:
              dotnet:
                profile: minimal
            security:
              redactionPatterns:
                - secret
            """);

        var configuration = _loader.Load(_repositoryRoot);

        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, LanguageConfiguration>)configuration.Languages).Add("go", new("minimal")));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)configuration.Security.RedactionPatterns).Add("another"));
    }

    [Fact]
    public void Load_BindsAuthenticatedRemoteHistoryAndRejectsUnsafeRegex()
    {
        Write("""
            schemaVersion: 1
            history:
              provider: remote
              endpoint: https://state.example.test/v1
              tokenEnvironment: MERKLE_STATE_TOKEN
            """);

        var configuration = _loader.Load(_repositoryRoot);

        Assert.Equal("remote", configuration.History.Provider);
        Assert.Equal("https://state.example.test/v1", configuration.History.Endpoint);
        Assert.Equal("MERKLE_STATE_TOKEN", configuration.History.TokenEnvironment);

        Write("schemaVersion: 1\nsecurity:\n  redactionPatterns:\n    - '(?=unsafe)'");
        var error = Assert.Throws<ConfigurationException>(() => _loader.Load(_repositoryRoot));
        Assert.Equal("InvalidRedactionPattern", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot)) Directory.Delete(_repositoryRoot, recursive: true);
    }

    private void Write(string content) => File.WriteAllText(Path.Combine(_repositoryRoot, ".merkle.yml"), content);
}
