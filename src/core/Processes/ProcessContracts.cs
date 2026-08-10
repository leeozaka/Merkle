using System.Text;

namespace Merkle.Core.Processes;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    ReadOnlyMemory<byte>? StandardInput = null,
    int MaxStandardOutputBytes = 16_777_216,
    int MaxStandardErrorBytes = 1_048_576);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    byte[]? StandardOutputBytes = null)
{
    public ReadOnlyMemory<byte> OutputBytes =>
        StandardOutputBytes ?? Encoding.UTF8.GetBytes(StandardOutput);
}

public interface IProcessRunner
{
    ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}
