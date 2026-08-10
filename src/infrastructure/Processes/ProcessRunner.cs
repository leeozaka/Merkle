using System.Diagnostics;
using System.Text;
using Merkle.Core.Errors;
using Merkle.Core.Processes;

namespace Merkle.Infrastructure.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public async ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxStandardOutputBytes <= 0 || request.MaxStandardErrorBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Process output limits must be greater than zero.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput.HasValue,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var variable in request.Environment)
            {
                process.StartInfo.Environment[variable.Key] = variable.Value;
            }
        }

        process.Start();
        var stdoutRead = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            request.MaxStandardOutputBytes,
            cancellationToken);
        var stderrRead = ReadBoundedAsync(
            process.StandardError.BaseStream,
            request.MaxStandardErrorBytes,
            cancellationToken);

        try
        {
            if (request.StandardInput is { } input)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var exit = process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(exit, stdoutRead, stderrRead).ConfigureAwait(false);
            var bytes = await stdoutRead.ConfigureAwait(false);
            var stderrBytes = await stderrRead.ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                Encoding.UTF8.GetString(bytes),
                Encoding.UTF8.GetString(stderrBytes),
                bytes);
        }
        catch (Exception error) when (error is OperationCanceledException or AnalysisException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream(Math.Min(limit, 16_384));
        var chunk = new byte[Math.Min(limit + 1, 16_384)];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > limit)
            {
                throw new AnalysisException(
                    "ProcessOutputLimitExceeded",
                    $"A child process exceeded its configured {limit}-byte output limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
