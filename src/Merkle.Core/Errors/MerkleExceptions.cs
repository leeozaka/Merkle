using Merkle.Core.Domain;

namespace Merkle.Core.Errors;

public abstract class MerkleException(ErrorClass errorClass, string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public ErrorClass ErrorClass { get; } = errorClass;

    public string Code { get; } = code;
}

public sealed class ConfigurationException(string code, string message, Exception? innerException = null) : MerkleException(ErrorClass.ConfigurationError, code, message, innerException)
{
}

public sealed class CapabilityException(string code, string message, Exception? innerException = null) : MerkleException(ErrorClass.CapabilityError, code, message, innerException)
{
}

public sealed class AnalysisException(string code, string message, Exception? innerException = null) : MerkleException(ErrorClass.AnalysisError, code, message, innerException)
{
}

public sealed class PolicyException(string code, string message, Exception? innerException = null) : MerkleException(ErrorClass.PolicyFailure, code, message, innerException)
{
}

