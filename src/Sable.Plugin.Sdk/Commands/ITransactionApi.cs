namespace Sable.Plugin.Sdk.Commands;

/// <summary>
/// Groups multiple edits into ONE undo step (capability <c>undo.transaction</c>). Null on
/// <see cref="Host.IHostContext.Transactions"/> when not granted. Call <see cref="Run"/> and make
/// all your layer-write calls inside <paramref name="body"/>; the host records them as a single
/// named history entry, so the user undoes the whole batch at once. If <paramref name="body"/>
/// throws, nothing is recorded.
/// </summary>
public interface ITransactionApi
{
    void Run(string name, System.Action body);
}
