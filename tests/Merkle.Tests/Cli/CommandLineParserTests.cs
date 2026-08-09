using Merkle.Cli;
using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Tests.Cli;

public sealed class CommandLineParserTests
{
    [Fact]
    public void ParsePlan_ParsesExplicitSnapshotLanguageAndPolicyOptions()
    {
        var command = new CommandLineParser().Parse([
            "plan", "--base", "main", "--head", "WORKTREE",
            "--languages", "dotnet:minimal", "--format", "json", "--pedantic"]);

        var plan = Assert.IsType<PlanCommand>(command);
        Assert.Equal("main", plan.BaseReference);
        Assert.Equal("WORKTREE", plan.HeadReference);
        Assert.Equal([new LanguageSelection("dotnet", "minimal")], plan.Languages);
        Assert.Equal(ReportFormat.Json, plan.Format);
        Assert.True(plan.Pedantic);
    }

    [Fact]
    public void ParseStateReset_RequiresLocalSafetyFlag()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["state", "reset"]));

        Assert.Equal("LocalResetConfirmationRequired", error.Code);
    }

    [Fact]
    public void Parse_RejectsUnknownOptions()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["plan", "--mystery"]));

        Assert.Equal("UnknownOption", error.Code);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet:")]
    [InlineData(":minimal")]
    public void ParsePlan_RejectsInvalidLanguageSelection(string value)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["plan", "--languages", value]));

        Assert.Equal("InvalidLanguageSelection", error.Code);
    }

    [Fact]
    public void ParsePlan_RejectsInvalidReportFormat()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["plan", "--format", "xml"]));

        Assert.Equal("InvalidReportFormat", error.Code);
    }

    [Fact]
    public void ParseObserve_ParsesBuildAndTimeoutOptions()
    {
        var command = Assert.IsType<ObserveCommand>(new CommandLineParser().Parse([
            "observe", "--languages", "dotnet:deep", "--solution", "App.sln",
            "--no-build", "--timeout-ms", "120000", "--format", "json"
        ]));

        Assert.True(command.NoBuild);
        Assert.Equal(120000, command.TimeoutMs);
        Assert.Equal("App.sln", command.Solution);
        Assert.Equal(ReportFormat.Json, command.Format);
    }

    [Fact]
    public void ParseRun_ParsesExplicitRiskPolicy()
    {
        var command = Assert.IsType<RunCommand>(new CommandLineParser().Parse([
            "run", "--min-savings-percent", "20", "--confidence-threshold", "0.8",
            "--on-low-confidence", "full-suite", "--pedantic"
        ]));

        Assert.Equal(20, command.MinSavingsPercent);
        Assert.Equal(.8, command.ConfidenceThreshold);
        Assert.Equal("full-suite", command.OnLowConfidence);
        Assert.True(command.Pedantic);
    }

    [Theory]
    [InlineData("observe", "--timeout-ms", "0")]
    [InlineData("run", "--confidence-threshold", "2")]
    [InlineData("run", "--on-low-confidence", "guess")]
    public void ParseExecution_RejectsInvalidValues(string command, string option, string value)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse([command, option, value]));

        Assert.Equal("InvalidOptionValue", error.Code);
    }

    [Fact]
    public void ParseHistoryImport_RequiresOneReportPath()
    {
        var command = Assert.IsType<HistoryImportCommand>(
            new CommandLineParser().Parse(["history", "import", "report.json"]));
        Assert.Equal("report.json", command.ReportPath);

        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["history", "import"]));
        Assert.Equal("UnknownHistoryCommand", error.Code);
    }

    [Theory]
    [InlineData("plan", "--base")]
    [InlineData("observe", "--solution")]
    [InlineData("run", "--timeout-ms")]
    public void Parse_RejectsMissingOptionValues(string command, string option)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse([command, option]));

        Assert.Equal("MissingOptionValue", error.Code);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("state", "wat")]
    [InlineData("history")]
    [InlineData("unknown")]
    public void Parse_RejectsUnknownCommandShapes(params string[] arguments)
    {
        Assert.Throws<ConfigurationException>(() => new CommandLineParser().Parse(arguments));
    }

    [Fact]
    public void ParseObserve_RejectsRunOnlyPolicyFlags()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new CommandLineParser().Parse(["observe", "--pedantic"]));

        Assert.Equal("UnknownOption", error.Code);
    }
}
