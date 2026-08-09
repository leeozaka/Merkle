using System.Security.Cryptography;
using System.Text;
using Merkle.Core.Adapters;
using Merkle.Core.Indexing;
using Merkle.Core.Reporting;

namespace Merkle.Core.History;

public static class HistoryCompatibility
{
    public static HistoryCompatibilityKey ForAdapter(
        string repositoryIdentity,
        AdapterDescriptor descriptor,
        string? configuredSolution,
        string configuration = "Debug",
        string platform = "AnyCPU")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(descriptor);
        return new HistoryCompatibilityKey(
            repositoryIdentity,
            $"report:1;index:{MerkleIndex.SchemaVersion};unit:{descriptor.UnitIdentityVersion};test:{descriptor.TestIdentityVersion}",
            $"{descriptor.Producer}/{descriptor.AdapterVersion}/{descriptor.Language}",
            $"solution:{Digest(configuredSolution ?? "auto")};configuration:{configuration};platform:{platform}");
    }

    public static HistoryCompatibilityKey ForReportAdapter(
        string repositoryIdentity,
        ReportAdapter adapter,
        ReportBuildContext? buildContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(adapter);
        return new HistoryCompatibilityKey(
            repositoryIdentity,
            $"report:1;index:{MerkleIndex.SchemaVersion};unit:{adapter.UnitIdentityVersion};test:{adapter.TestIdentityVersion}",
            $"{adapter.Producer}/{adapter.Version}/{adapter.Language}",
            $"solution:{Digest(buildContext?.Solution ?? "auto")};configuration:{buildContext?.Configuration ?? "Debug"};platform:{buildContext?.Platform ?? "AnyCPU"}");
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
