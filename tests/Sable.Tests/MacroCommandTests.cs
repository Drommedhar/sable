using System;
using System.Collections.Generic;
using Sable.Core.Undo;

namespace Sable.Tests;

/// <summary>Transaction undo: grouping commands into one undo entry (PLUGIN_SDK_PLAN §13).</summary>
public sealed class MacroCommandTests
{
    private sealed class Spy : IUndoableCommand
    {
        private readonly List<string> _log;
        private readonly string _tag;
        public Spy(List<string> log, string tag) { _log = log; _tag = tag; }
        public string Name => _tag;
        public void Do() => _log.Add("do:" + _tag);
        public void Undo() => _log.Add("undo:" + _tag);
    }

    private sealed class Throwing : IUndoableCommand
    {
        public string Name => "boom";
        public void Do() => throw new InvalidOperationException("boom");
        public void Undo() { }
    }

    [Fact]
    public void Do_applies_in_order_Undo_reverses()
    {
        var log = new List<string>();
        var macro = new MacroCommand("batch", new Spy(log, "a"), new Spy(log, "b"), new Spy(log, "c"));
        macro.Do();
        macro.Undo();
        Assert.Equal(new[] { "do:a", "do:b", "do:c", "undo:c", "undo:b", "undo:a" }, log);
    }

    [Fact]
    public void Failing_child_rolls_back_applied_children()
    {
        var log = new List<string>();
        var macro = new MacroCommand("batch", new Spy(log, "a"), new Spy(log, "b"), new Throwing(), new Spy(log, "d"));
        Assert.Throws<InvalidOperationException>(() => macro.Do());
        // a,b applied then rolled back in reverse; d never ran
        Assert.Equal(new[] { "do:a", "do:b", "undo:b", "undo:a" }, log);
    }

    [Fact]
    public void Stack_records_macro_as_single_entry()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.ExecuteMacro("batch", new IUndoableCommand[] { new Spy(log, "a"), new Spy(log, "b") });
        Assert.Equal(1, stack.Cursor);               // one undo entry, not two
        Assert.Single(stack.History);
        stack.Undo();
        Assert.Equal(0, stack.Cursor);
        Assert.Equal(new[] { "do:a", "do:b", "undo:b", "undo:a" }, log);
    }

    [Fact]
    public void ExecuteMacro_empty_is_noop_single_is_unwrapped()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.ExecuteMacro("empty", Array.Empty<IUndoableCommand>());
        Assert.Empty(stack.History);

        stack.ExecuteMacro("one", new IUndoableCommand[] { new Spy(log, "x") });
        Assert.IsNotType<MacroCommand>(stack.History[0]);   // single command recorded directly
    }
}
