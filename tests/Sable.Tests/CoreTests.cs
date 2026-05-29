using Sable.Core;
using Sable.Core.Undo;
using Xunit;

namespace Sable.Tests;

public class BlendModeTests
{
    // Integer values are the contract with composite.wgsl — must not drift.
    [Theory]
    [InlineData(BlendMode.Normal, 0)]
    [InlineData(BlendMode.Multiply, 1)]
    [InlineData(BlendMode.Screen, 2)]
    [InlineData(BlendMode.Overlay, 3)]
    [InlineData(BlendMode.Darken, 4)]
    [InlineData(BlendMode.Lighten, 5)]
    [InlineData(BlendMode.Add, 6)]
    public void BlendMode_HasStableIntValue(BlendMode mode, int expected)
        => Assert.Equal(expected, (int)mode);
}

public class UndoStackTests
{
    private sealed class DelegateCommand(Action doIt, Action undoIt) : IUndoableCommand
    {
        public string Name => "Test";
        public void Do() => doIt();
        public void Undo() => undoIt();
    }

    [Fact]
    public void Execute_RunsCommand_AndEnablesUndo()
    {
        var stack = new UndoStack();
        int value = 0;
        stack.Execute(new DelegateCommand(() => value = 1, () => value = 0));
        Assert.Equal(1, value);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Undo_Redo_RoundTrips()
    {
        var stack = new UndoStack();
        int value = 0;
        stack.Execute(new DelegateCommand(() => value = 5, () => value = 0));
        stack.Undo();
        Assert.Equal(0, value);
        Assert.True(stack.CanRedo);
        stack.Redo();
        Assert.Equal(5, value);
    }

    [Fact]
    public void Execute_AfterUndo_TruncatesRedoTail()
    {
        var stack = new UndoStack();
        int value = 0;
        stack.Execute(new DelegateCommand(() => value = 1, () => value = 0));
        stack.Execute(new DelegateCommand(() => value = 2, () => value = 1));
        stack.Undo();                       // value = 1, redo available
        stack.Execute(new DelegateCommand(() => value = 9, () => value = 1));
        Assert.False(stack.CanRedo);        // tail dropped
        Assert.Equal(9, value);
    }

    [Fact]
    public void Capacity_DropsOldestCommands()
    {
        var stack = new UndoStack { Capacity = 2 };
        int value = 0;
        for (int i = 1; i <= 5; i++)
        {
            int v = i;
            stack.Execute(new DelegateCommand(() => value = v, () => value = v - 1));
        }
        // only 2 retained
        Assert.True(stack.CanUndo);
        stack.Undo(); stack.Undo();
        Assert.False(stack.CanUndo);
    }
}
