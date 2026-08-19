namespace Merkle.Build;

public interface IBuildAdapter
{
    AdapterBuildDefinition Definition { get; }

    ValueTask<AdapterReadiness> PreflightAsync(
        BuildContext context,
        CancellationToken cancellationToken);

    ValueTask<AdapterBuildResult> BuildAsync(
        AdapterBuildRequest request,
        CancellationToken cancellationToken);
}

public interface IBuildAdapterCatalog
{
    IReadOnlyList<IBuildAdapter> Adapters { get; }

    IReadOnlyList<IBuildAdapter> ResolveSelection(IReadOnlyList<string> names);
}

public interface IBuildOrchestrator
{
    ValueTask<BuildReport> RunAsync(
        BuildRequest request,
        CancellationToken cancellationToken);
}

public interface IHostPublisher
{
    ValueTask<HostPublishResult> PublishAsync(
        HostPublishRequest request,
        CancellationToken cancellationToken);
}

internal interface IBuildOutputPublisher
{
    ValueTask<BuildOutputResult> PublishAsync(
        BuildOutputRequest request,
        CancellationToken cancellationToken);
}

public interface IBuildReportWriter
{
    ValueTask<string> WriteAsync(
        BuildReportRequest request,
        CancellationToken cancellationToken);
}

public interface IBuildRunWorkspace : IAsyncDisposable
{
    BuildContext Context { get; }

    ValueTask<BuildOutputResult> PromoteAsync(
        BuildOutputRequest request,
        CancellationToken cancellationToken);
}

public interface IBuildRunWorkspaceFactory
{
    ValueTask<IBuildRunWorkspace> AcquireAsync(
        BuildRequest request,
        CancellationToken cancellationToken);
}
