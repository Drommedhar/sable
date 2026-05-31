namespace Sable.Core.Undo;

/// <summary>
/// A reversible document mutation. Per PLAN §5B, every state change goes through
/// the undo stack — both non-destructive graph edits and destructive pixel writes.
/// </summary>
public interface IUndoableCommand
{
    string Name { get; }
    void Do();
    void Undo();
}
