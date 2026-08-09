using Merkle.Core.Domain;

namespace Merkle.Tests.Domain;

public sealed class SnapshotTests
{
    [Fact]
    public void Snapshot_DefensivelyCopiesFileBytesAndManifestCollection()
    {
        var content = new byte[] { 1, 2, 3 };
        var files = new List<SnapshotFile>
        {
            new("a.cs", "hash", content)
        };
        var snapshot = new RepositorySnapshot(
            new SnapshotIdentity("id", "HEAD", "git"),
            "/repo",
            "repository",
            files);

        content[0] = 9;
        files.Clear();

        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.Files[0].Content.ToArray());
        Assert.Single(snapshot.Files);
    }
}
