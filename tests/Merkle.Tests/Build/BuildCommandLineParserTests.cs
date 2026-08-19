using Merkle.Build;
using Merkle.Core.Errors;

namespace Merkle.Tests.Build;

public sealed class BuildCommandLineParserTests
{
    private readonly IBuildCommandLineParser _parser = new BuildCommandLineParser();

    [Fact]
    public void ParseBuild_UsesDotNetStrictSequentialDefaults()
    {
        var request = _parser.Parse(["build"]);

        Assert.Equal(BuildCommand.Build, request.Command);
        Assert.Equal(["dotnet"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.Strict, request.Policy);
        Assert.Equal(BuildScheduling.Sequential, request.Scheduling);
        Assert.Null(request.MaxParallel);
        Assert.False(request.RunTests);
        Assert.False(request.NoWarnings);
        Assert.Equal("Debug", request.Configuration);
        Assert.Null(request.RuntimeIdentifier);
    }

    [Fact]
    public void Parse_EmptyInvocationUsesBuildDefaults()
    {
        var request = _parser.Parse([]);

        Assert.Equal(BuildCommand.Build, request.Command);
        Assert.Equal(["dotnet"], request.Adapters);
    }

    [Fact]
    public void ParsePublish_UsesReleaseDefault()
    {
        var request = _parser.Parse(["publish", "--adapters", "java"]);

        Assert.Equal(BuildCommand.Publish, request.Command);
        Assert.Equal(["java"], request.Adapters);
        Assert.Equal("Release", request.Configuration);
        Assert.Equal(BuildRuntimeIdentifier.Current, request.RuntimeIdentifier);
    }

    [Fact]
    public void Parse_NormalizesGoAliasAndReadsAutomationOptions()
    {
        var request = _parser.Parse([
            "publish",
            "--adapters", "go,java,go",
            "--adapter-policy", "best-effort",
            "--builds", "parallel",
            "--max-parallel", "3",
            "--test",
            "--no-warnings",
            "--configuration", "Release",
            "--runtime", BuildRuntimeIdentifier.Current,
            "--output", "artifacts/out",
            "--report", "artifacts/report.json",
            "--format", "json",
            "--clean"]);

        Assert.Equal(["golang", "java"], request.Adapters);
        Assert.Equal(AdapterBuildPolicy.BestEffort, request.Policy);
        Assert.Equal(BuildScheduling.Parallel, request.Scheduling);
        Assert.Equal(3, request.MaxParallel);
        Assert.True(request.RunTests);
        Assert.True(request.NoWarnings);
        Assert.Equal("Release", request.Configuration);
        Assert.Equal(BuildRuntimeIdentifier.Current, request.RuntimeIdentifier);
        Assert.Equal("artifacts/out", request.OutputPath);
        Assert.Equal("artifacts/report.json", request.ReportPath);
        Assert.Equal(BuildOutputFormat.Json, request.Format);
        Assert.True(request.Clean);
    }

    [Fact]
    public void Parse_AllPreservesCatalogExpansionToken()
    {
        var request = _parser.Parse(["build", "--adapters", "all"]);

        Assert.Equal(["all"], request.Adapters);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    public void Parse_RejectsInvalidAdapterSelection(string adapters)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            _parser.Parse(["build", "--adapters", adapters]));

        Assert.Equal("InvalidOptionValue", error.Code);
    }

    [Fact]
    public void Parse_RejectsMaxParallelForSequentialBuilds()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            _parser.Parse(["build", "--max-parallel", "2"]));

        Assert.Equal("InvalidOptionValue", error.Code);
    }

    [Theory]
    [InlineData("build", "--adapter-policy", "sometimes")]
    [InlineData("build", "--builds", "eventually")]
    [InlineData("build", "--max-parallel", "0")]
    [InlineData("build", "--format", "xml")]
    [InlineData("build", "--configuration", "Profile")]
    public void Parse_RejectsInvalidOptionValues(string command, string option, string value)
    {
        var error = Assert.Throws<ConfigurationException>(() => _parser.Parse([command, option, value]));

        Assert.Equal("InvalidOptionValue", error.Code);
    }

    [Fact]
    public void Parse_RejectsUnknownOptions()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            _parser.Parse(["build", "--mystery"]));

        Assert.Equal("UnknownOption", error.Code);
        Assert.Equal(
            "UnknownOption",
            Assert.Throws<ConfigurationException>(() => _parser.Parse(["--mystery"])).Code);
        Assert.Equal(
            "UnknownCommand",
            Assert.Throws<ConfigurationException>(() => _parser.Parse(["deploy"])).Code);
    }

    [Fact]
    public void Parse_RejectsMissingOptionValues()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            _parser.Parse(["publish", "--adapters"]));

        Assert.Equal("MissingOptionValue", error.Code);
    }

    [Fact]
    public void Parse_RejectsRuntimeThatCannotBeSmokedOnThisBuilder()
    {
        var differentRuntime = BuildRuntimeIdentifier.Current == "linux-x64" ? "osx-arm64" : "linux-x64";

        var error = Assert.Throws<ConfigurationException>(() =>
            _parser.Parse(["publish", "--runtime", differentRuntime]));

        Assert.Equal("UnsupportedRuntimeIdentifier", error.Code);
    }

    [Fact]
    public void Parse_AcceptsExplicitSequentialSchedulingAndRejectsNonNumericParallelism()
    {
        Assert.Equal(
            BuildScheduling.Sequential,
            _parser.Parse(["build", "--builds", "sequential"]).Scheduling);
        Assert.Equal(
            "InvalidOptionValue",
            Assert.Throws<ConfigurationException>(() =>
                _parser.Parse(["build", "--builds", "parallel", "--max-parallel", "many"])).Code);
    }
}
