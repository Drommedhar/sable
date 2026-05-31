using System;
using System.Linq;
using Sable.Core;
using Sable.Core.Settings;
using Sable.Core.Undo;
using Xunit;

namespace Sable.Tests;

public class SettingsTests
{
    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var s = new SableSettings { Theme = AppTheme.Gray, DefaultDpi = 144, AutoCheckUpdates = false, WinW = 1000, WinMaximized = true };
        s.OpenTabs.Add(@"C:\a.sable");
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"set_{System.Guid.NewGuid():N}.json");
        try
        {
            SettingsService.Save(s, path);
            var l = SettingsService.Load(path);
            Assert.Equal(AppTheme.Gray, l.Theme);
            Assert.Equal(144, l.DefaultDpi);
            Assert.False(l.AutoCheckUpdates);
            Assert.Equal(1000, l.WinW);
            Assert.True(l.WinMaximized);
            Assert.Single(l.OpenTabs);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void AddRecent_DedupesNewestFirstAndCaps()
    {
        var s = new SableSettings();
        for (int i = 0; i < SableSettings.MaxRecent + 5; i++) s.AddRecent($"f{i}.sable");
        s.AddRecent("f3.sable");   // re-add an existing → moves to front, no dup
        Assert.Equal("f3.sable", s.RecentFiles[0]);
        Assert.Equal(SableSettings.MaxRecent, s.RecentFiles.Count);
        Assert.Equal(s.RecentFiles.Count, s.RecentFiles.Distinct().Count());
    }

    [Fact]
    public void GestureFor_UsesDefault_Override_AndExplicitUnbind()
    {
        var s = new SableSettings();
        Assert.Equal("Ctrl+Z", s.GestureFor("edit.undo"));    // catalog default
        s.KeyBindings["edit.undo"] = "Ctrl+Shift+U";
        Assert.Equal("Ctrl+Shift+U", s.GestureFor("edit.undo"));   // override wins
        s.KeyBindings["file.save"] = "";
        Assert.Equal("", s.GestureFor("file.save"));          // explicit unbind
        Assert.Equal("", s.GestureFor("does.not.exist"));     // unknown id
    }

    [Fact]
    public void KeyBindings_RoundTripThroughJson()
    {
        var s = new SableSettings();
        s.KeyBindings["edit.copy"] = "Ctrl+Shift+K";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keys_{System.Guid.NewGuid():N}.json");
        try
        {
            SettingsService.Save(s, path);
            var l = SettingsService.Load(path);
            Assert.Equal("Ctrl+Shift+K", l.GestureFor("edit.copy"));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Theory]
    [InlineData("#00A0E6", 0x00, 0xA0, 0xE6)]
    [InlineData("808080", 0x80, 0x80, 0x80)]
    [InlineData("#FF112233", 0x11, 0x22, 0x33)]   // tolerate #AARRGGBB
    public void ParseHex_ReadsColour(string hex, byte r, byte g, byte b)
    {
        var (pr, pg, pb) = SableSettings.ParseHex(hex, (1, 2, 3));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("#12")]
    public void ParseHex_FallsBackOnGarbage(string hex)
        => Assert.Equal(((byte)9, (byte)9, (byte)9), SableSettings.ParseHex(hex, (9, 9, 9)));
}

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
    public void JumpTo_MovesToAnyHistoryState()
    {
        var stack = new UndoStack();
        int value = 0;
        stack.Execute(new DelegateCommand(() => value = 1, () => value = 0));
        stack.Execute(new DelegateCommand(() => value = 2, () => value = 1));
        stack.Execute(new DelegateCommand(() => value = 3, () => value = 2));
        Assert.Equal(3, stack.Cursor);
        Assert.Equal(3, stack.History.Count);

        stack.JumpTo(1);          // back to after the first command
        Assert.Equal(1, value);
        Assert.Equal(1, stack.Cursor);

        stack.JumpTo(3);          // forward to the end
        Assert.Equal(3, value);

        stack.JumpTo(0);          // initial state
        Assert.Equal(0, value);
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

public class ColorConvertTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(10, 200, 120)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(64, 128, 192)]
    public void Hsl_RoundTrips(byte r, byte g, byte b)
    {
        var (h, s, l) = Sable.Core.ColorConvert.RgbToHsl(r, g, b);
        var (r2, g2, b2) = Sable.Core.ColorConvert.HslToRgb(h, s, l);
        Assert.InRange(Math.Abs(r2 - r), 0, 2);
        Assert.InRange(Math.Abs(g2 - g), 0, 2);
        Assert.InRange(Math.Abs(b2 - b), 0, 2);
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(10, 200, 120)]
    [InlineData(64, 128, 192)]
    public void Cmyk_RoundTrips(byte r, byte g, byte b)
    {
        var (c, m, y, k) = Sable.Core.ColorConvert.RgbToCmyk(r, g, b);
        var (r2, g2, b2) = Sable.Core.ColorConvert.CmykToRgb(c, m, y, k);
        Assert.InRange(Math.Abs(r2 - r), 0, 2);
        Assert.InRange(Math.Abs(g2 - g), 0, 2);
        Assert.InRange(Math.Abs(b2 - b), 0, 2);
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(10, 200, 120)]
    [InlineData(64, 128, 192)]
    public void Lab_RoundTrips(byte r, byte g, byte b)
    {
        var (L, a, bb) = Sable.Core.ColorConvert.RgbToLab(r, g, b);
        var (r2, g2, b2) = Sable.Core.ColorConvert.LabToRgb(L, a, bb);
        Assert.InRange(Math.Abs(r2 - r), 0, 3);
        Assert.InRange(Math.Abs(g2 - g), 0, 3);
        Assert.InRange(Math.Abs(b2 - b), 0, 3);
    }
}
