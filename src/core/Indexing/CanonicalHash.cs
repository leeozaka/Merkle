using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Merkle.Core.Indexing;

internal static class CanonicalHash
{
    public const string Algorithm = "sha256";
    public const int SchemaVersion = 1;

    public static string Compute(params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];

        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

