namespace Sable.Core.Undo;

/// <summary>
/// Linear multi-level undo/redo. Non-linear history states (PLAN §5B.4) layer on
/// top of this later. Pushing a new command after an undo truncates the redo tail.
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableCommand> _history = new();
    private int _cursor; // number of applied commands

    public int Capacity { get; set; } = 200;

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count;

    /// <summary>Applied + redoable commands (oldest first), for the History panel.</summary>
    public IReadOnlyList<IUndoableCommand> History => _history;

    /// <summary>Number of applied commands (0 = the initial state). History index = this value.</summary>
    public int Cursor => _cursor;

    /// <summary>Undo/redo until exactly <paramref name="target"/> commands are applied (History-panel jump).</summary>
    public void JumpTo(int target)
    {
        target = Math.Clamp(target, 0, _history.Count);
        while (_cursor > target) _history[--_cursor].Undo();
        while (_cursor < target) _history[_cursor++].Do();
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Execute several commands as ONE undo entry (transaction). No-op on an empty set;
    /// a single command is recorded directly (no wrapper). See <see cref="MacroCommand"/>.</summary>
    public void ExecuteMacro(string name, IReadOnlyList<IUndoableCommand> commands)
    {
        if (commands.Count == 0) return;
        Execute(commands.Count == 1 ? commands[0] : new MacroCommand(name, commands));
    }

    /// <summary>Execute a command and record it.</summary>
    public void Execute(IUndoableCommand command)
    {
        command.Do();
        // drop any redo tail
        if (_cursor < _history.Count)
            _history.RemoveRange(_cursor, _history.Count - _cursor);
        _history.Add(command);
        _cursor++;
        if (_history.Count > Capacity)
        {
            int overflow = _history.Count - Capacity;
            _history.RemoveRange(0, overflow);
            _cursor -= overflow;
        }
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _history[--_cursor].Undo();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _history[_cursor++].Do();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _history.Clear();
        _cursor = 0;
        Changed?.Invoke();
    }
}
