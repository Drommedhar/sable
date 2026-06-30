namespace Sable.Plugin.Sdk.Automation;

/// <summary>
/// A batch operation a plugin contributes (capability <c>automation.batch</c>). The user picks it
/// in the batch UI and queues input files; the host then invokes <see cref="Run"/> with an
/// <see cref="IBatchApi"/> scoped to those files. <see cref="Run"/> executes headlessly (off the UI
/// thread): iterate <see cref="IBatchApi.InputFiles"/>, open/edit/save each, report progress, and
/// honour <see cref="IBatchApi.Cancellation"/> between items.
/// </summary>
public sealed record BatchOperation
{
    /// <summary>Stable id, unique within the plugin.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Optional grouping label shown in the batch UI. Optional.</summary>
    public string? Category { get; init; }

    public required Action<IBatchApi> Run { get; init; }
}

/// <summary>Batch-operation registration surface (capability <c>automation.batch</c>). Null on
/// <see cref="Host.IHostContext.Automation"/> when not granted.</summary>
public interface IBatchRegistry
{
    void Register(BatchOperation operation);
}
