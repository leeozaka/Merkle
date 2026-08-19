using System.Runtime.InteropServices;
using Merkle.Core.Errors;

namespace Merkle.Build;

public static class BuildRuntimeIdentifier
{
    public static string Current
    {
        get
        {
            var platform = OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("Merkle source builds support macOS and Linux.");
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => throw new PlatformNotSupportedException(
                    $"Merkle source builds do not support {RuntimeInformation.ProcessArchitecture}.")
            };
            return $"{platform}-{architecture}";
        }
    }

    public static string ValidateCurrent(string value)
    {
        if (!StringComparer.Ordinal.Equals(value, Current))
        {
            throw new ConfigurationException(
                "UnsupportedRuntimeIdentifier",
                $"Runtime '{value}' cannot be smoke-tested on this builder; use '{Current}'.");
        }

        return value;
    }
}
