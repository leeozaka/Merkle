using Merkle.Core.Errors;
using Merkle.Core.Processes;
using Merkle.Infrastructure.Processes;

namespace Merkle.Tests.Infrastructure;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Run_CapturesArgumentsOutputAndExitCodeWithoutShellInterpolation()
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("/usr/bin/printf", ["%s", "hello world"], "/tmp"),
            default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello world", result.StandardOutput);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("hello world"), result.OutputBytes.ToArray());
    }

    [Fact]
    public async Task Run_CancellationStopsChildProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ProcessRunner().RunAsync(
                new ProcessRequest("/bin/sh", ["-c", "sleep 30"], "/tmp"),
                cancellation.Token));
    }

    [Fact]
    public async Task Run_PassesEnvironmentAndStandardInputWithoutShellInterpolation()
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest(
                "/bin/sh",
                ["-c", "read value; printf '%s:%s' \"$MERKLE_PROCESS_TEST\" \"$value\""],
                "/tmp",
                new Dictionary<string, string?> { ["MERKLE_PROCESS_TEST"] = "environment" },
                System.Text.Encoding.UTF8.GetBytes("input\n")),
            default);

        Assert.Equal("environment:input", result.StandardOutput);
    }

    [Fact]
    public async Task Run_RejectsOutputBeyondTheConfiguredBound()
    {
        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await new ProcessRunner().RunAsync(
                new ProcessRequest(
                    "/usr/bin/printf",
                    ["12345"],
                    "/tmp",
                    MaxStandardOutputBytes: 4),
                default));

        Assert.Equal("ProcessOutputLimitExceeded", error.Code);
    }

    [Fact]
    public async Task Run_RejectsNonPositiveOutputBounds()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new ProcessRunner().RunAsync(
                new ProcessRequest(
                    "/usr/bin/printf",
                    ["unused"],
                    "/tmp",
                    MaxStandardErrorBytes: 0),
                default));
    }

    [Fact]
    public async Task Run_RejectsStandardErrorBeyondTheConfiguredBound()
    {
        var error = await Assert.ThrowsAsync<AnalysisException>(async () =>
            await new ProcessRunner().RunAsync(
                new ProcessRequest(
                    "/bin/sh",
                    ["-c", "printf 12345 >&2"],
                    "/tmp",
                    MaxStandardErrorBytes: 4),
                default));

        Assert.Equal("ProcessOutputLimitExceeded", error.Code);
    }
}
