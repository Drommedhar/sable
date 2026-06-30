using System.Collections.Generic;

namespace Sable.Core.Undo;

/// <summary>
/// Groups several <see cref="IUndoableCommand"/>s into a single undo entry (PLAN §13 transaction
/// undo / PLUGIN_SDK_PLAN §13, capability <c>undo.transaction</c>). <see cref="Do"/> applies the
/// children in order; <see cref="Undo"/> reverses them in reverse order, so the whole batch undoes
/// and redoes as one step. If a child throws while applying, the already-applied children are
/// rolled back and the exception propagates (atomic — the stack never records a half-done macro).
/// </summary>
public sealed class MacroCommand : IUndoableCommand
{
    private readonly List<IUndoableCommand> _children;

    public MacroCommand(string name, IEnumerable<IUndoableCommand> children)
    {
        Name = name;
        _children = new List<IUndoableCommand>(children);
    }

    public MacroCommand(string name, params IUndoableCommand[] children)
        : this(name, (IEnumerable<IUndoableCommand>)children) { }

    public string Name { get; }

    /// <summary>Number of grouped child commands.</summary>
    public int Count => _children.Count;

    public void Do()
    {
        for (int i = 0; i < _children.Count; i++)
        {
            try { _children[i].Do(); }
            catch
            {
                // roll back the ones already applied, in reverse, then rethrow
                for (int j = i - 1; j >= 0; j--) _children[j].Undo();
                throw;
            }
        }
    }

    public void Undo()
    {
        for (int i = _children.Count - 1; i >= 0; i--) _children[i].Undo();
    }
}
