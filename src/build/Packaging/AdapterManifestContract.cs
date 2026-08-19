using System.Security.Cryptography;
using System.Text.Json;

namespace Merkle.Build;

public static class AdapterManifestContract
{
    public static void ValidateFile(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            BuildJsonContext.Default.AdapterManifestDocument)
            ?? throw new InvalidDataException("The adapter manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("The adapter manifest schema must be 1.");
        if (string.IsNullOrWhiteSpace(manifest.MerkleVersion) ||
            string.IsNullOrWhiteSpace(manifest.Configuration) ||
            string.IsNullOrWhiteSpace(manifest.RuntimeIdentifier))
        {
            throw new InvalidDataException("The adapter manifest is missing build identity fields.");
        }
        if (manifest.Adapters.Length == 0)
        {
            throw new InvalidDataException("The adapter manifest must contain at least one built adapter.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var package = Path.GetDirectoryName(path)!;
        foreach (var adapter in manifest.Adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.Id) || !ids.Add(adapter.Id))
            {
                throw new InvalidDataException("Adapter manifest IDs must be non-empty and unique.");
            }

            if (string.IsNullOrWhiteSpace(adapter.Version) ||
                string.IsNullOrWhiteSpace(adapter.ProtocolVersion) ||
                string.IsNullOrWhiteSpace(adapter.Profile) ||
                adapter.Artifacts.Length == 0)
            {
                throw new InvalidDataException($"Adapter '{adapter.Id}' has incomplete metadata.");
            }

            foreach (var artifact in adapter.Artifacts)
            {
                var artifactPath = SafeArtifactPath(package, artifact.Path);
                if (artifact.Sha256.Length != 64 || artifact.Sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
                {
                    throw new InvalidDataException($"Adapter artifact '{artifact.Path}' has an invalid SHA-256 value.");
                }

                var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant();
                if (!StringComparer.Ordinal.Equals(actualHash, artifact.Sha256))
                {
                    throw new InvalidDataException($"Adapter artifact '{artifact.Path}' does not match its manifest checksum.");
                }
            }
        }
    }

    private static string SafeArtifactPath(string package, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Adapter artifact path '{relativePath}' is not relative.");
        }

        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Adapter artifact path '{relativePath}' is unsafe.");
        }

        if (!StringComparer.Ordinal.Equals(parts[0], "workers"))
        {
            throw new InvalidDataException($"Adapter artifact path '{relativePath}' must be beneath 'workers/'.");
        }

        var path = Path.Combine(package, Path.Combine(parts));
        if (!File.Exists(path)) throw new InvalidDataException($"Adapter artifact '{relativePath}' is missing.");
        return path;
    }
}

public static class BuildVersion
{
    public static string Current { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        var version = typeof(BuildVersion).Assembly.GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
