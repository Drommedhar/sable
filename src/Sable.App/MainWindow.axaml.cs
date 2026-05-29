using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sable.Engine;
using Sable.Engine.IO;
using Sable.Format;
using Sable.Tools;
using Sable.UI.ViewModels;

namespace Sable.App;

public partial class MainWindow : Window
{
    private bool _panning;
    private Point _lastPointer;
    private string? _currentPath;
    private AdjustmentWindow? _adjWindow;

    private LayerViewModel? _dragSource;
    private Point _dragStart;
    private bool _dragging;

    private static FilePickerFileType SableType => new("Sable document") { Patterns = new[] { "*.sable" } };

    public MainWindow()
    {
        InitializeComponent();

        // One shared Document drives both the GPU canvas and the layers panel.
        LoadDocument(Document.CreateDemo());

        // layer drag-drop (manual pointer DnD: reorder / move into group / auto-group)
        LayerList.AddHandler(PointerPressedEvent, OnLayerPointerPressed, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerMovedEvent, OnLayerPointerMoved, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerReleasedEvent, OnLayerPointerReleased, RoutingStrategies.Tunnel);

        // selection keys tunnel-first so a focused panel (e.g. the layers list) can't eat Delete
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        // gradient editor shares the canvas's gradient def; selecting a stop routes the colour wheel to it
        GradBar.Def = Canvas.Gradient;
        GradBar.StopSelected += _ => { if (_gradientTab) SyncWheelToStop(); };

        // font picker + on-canvas text editing
        FontCombo.ItemsSource = Sable.Imaging.TextRaster.Families();
        Canvas.TextEditStarted += t => Doc?.SelectModel(t);   // select the edited text layer

        // custom colour wheel → brush / selected shape-text / gradient stop
        BrushColorView.ColorChanged += OnBrushColorChanged;

        WireTools();
    }

    private DocumentViewModel? Doc => DataContext as DocumentViewModel;

    private static LayerViewModel? LayerOf(object? source)
        => (source as Control)?.DataContext as LayerViewModel;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (Canvas.TextEditing && !string.IsNullOrEmpty(e.Text))
        {
            Canvas.TextInsert(e.Text);
            Doc?.SelectedLayer?.RefreshThumbnail();
            e.Handled = true;
            return;
        }
        base.OnTextInput(e);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Canvas.TextEditing)   // typing on-canvas: only edit keys, let TextInput get the chars
        {
            switch (e.Key)
            {
                case Key.Back: Canvas.TextBackspace(); Doc?.SelectedLayer?.RefreshThumbnail(); e.Handled = true; break;
                case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    Canvas.TextInsert("\n"); Doc?.SelectedLayer?.RefreshThumbnail(); e.Handled = true; break;   // newline
                case Key.Enter or Key.Escape: Canvas.CommitTextEdit(); e.Handled = true; break;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Delete or Key.Back: Canvas.DeleteSelection(); e.Handled = true; break;
            case Key.Enter: Canvas.CommitCrop(); e.Handled = true; break;     // commit a pending crop
            case Key.Escape: Canvas.CancelCrop(); Canvas.Deselect(); e.Handled = true; break;
        }
    }

    private void OnLayerSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Doc?.SetSelection(LayerList.SelectedItems?.Cast<LayerViewModel>() ?? Enumerable.Empty<LayerViewModel>());

    private void OnToggleGroupExpand(object? sender, RoutedEventArgs e)
    {
        if (LayerOf(sender) is { } vm) Doc?.ToggleExpand(vm);
        e.Handled = true;   // don't let the click bubble into row selection
    }

    private void OnLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragSource = LayerOf(e.Source);
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void OnLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }

        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (System.Math.Abs(p.X - _dragStart.X) < 5 && System.Math.Abs(p.Y - _dragStart.Y) < 5) return;
            _dragging = true;                       // crossed threshold → show ghost
            DragGhostText.Text = _dragSource.Name;
            DragGhost.IsVisible = true;
        }
        var g = e.GetPosition(DragLayer);
        Avalonia.Controls.Canvas.SetLeft(DragGhost, g.X + 12);
        Avalonia.Controls.Canvas.SetTop(DragGhost, g.Y + 4);
    }

    private void OnLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // the list captures the pointer on press, so e.Source is the source row;
        // hit-test the row actually under the cursor at release instead
        if (_dragging && _dragSource is { } src && Doc is not null)
        {
            var hit = LayerList.InputHitTest(e.GetPosition(LayerList)) as Visual;
            var target = FindLayerVm(hit);
            if (target is not null) Doc.DropLayer(src.Model, target.Model);
        }
        EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragSource = null;
        DragGhost.IsVisible = false;
    }

    private static LayerViewModel? FindLayerVm(Visual? v)
    {
        while (v is not null)
        {
            if (v is Control { DataContext: LayerViewModel lvm }) return lvm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // --- viewport input -------------------------------------------------------
    // Mouse over the native GPU surface may be swallowed by the child window
    // (airspace); the transparent overlay handles it when it can, and keyboard
    // (+/-/0, arrows) is the guaranteed path.

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        Canvas.ZoomBy(e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1);
        e.Handled = true;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _panning = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(InputLayer);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(this);
        Canvas.PanBy(p.X - _lastPointer.X, p.Y - _lastPointer.Y);
        _lastPointer = p;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = false;
        e.Pointer.Capture(null);
    }

    private void OnCanvasDoubleTapped(object? sender, TappedEventArgs e) => Canvas.ResetView();

    private void OnBrushSizeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.Brush.Radius = (float)(e.NewValue / 2.0);   // slider = diameter
        if (BrushSizeLabel is not null) BrushSizeLabel.Text = $"{e.NewValue:0} px";
    }

    private void OnStrengthChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.Brush.Strength = (float)(e.NewValue / 100.0);
        if (StrengthLabel is not null) StrengthLabel.Text = $"{e.NewValue:0}%";
    }

    private void OnFeatherChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.SetSelectionFeather((float)e.NewValue);   // also re-feathers the current selection live
        if (FeatherLabel is not null) FeatherLabel.Text = $"{e.NewValue:0} px";
    }

    private bool _gradientTab;
    private Sable.Engine.Layers.ShapeLayer? _shapeTarget;   // selected shape the colour wheel recolours
    private Sable.Engine.Layers.TextLayer? _textTarget;     // selected text layer (wheel recolours + options edit)
    private bool _syncingType;

    private void OnTypeSizeChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (!float.TryParse(TypeSizeBox.Text, out var v)) return;
        Canvas.TypeFontSize = Math.Clamp(v, 4f, 512f);
        if (_syncingType || _textTarget is not { } t) return;
        t.FontSize = Canvas.TypeFontSize; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnFontChanged(object? sender, SelectionChangedEventArgs e)
    {
        var family = FontCombo.SelectedItem as string ?? "";
        Canvas.TypeFontFamily = family;
        if (_syncingType || _textTarget is not { } t) return;
        t.FontFamily = family; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnFontStyleChanged(object? sender, RoutedEventArgs e)
    {
        Canvas.TypeBold = BoldBtn.IsChecked == true;
        Canvas.TypeItalic = ItalicBtn.IsChecked == true;
        Canvas.TypeUnderline = UnderlineBtn.IsChecked == true;
        Canvas.TypeStrike = StrikeBtn.IsChecked == true;
        if (_syncingType || _textTarget is not { } t) return;
        t.Bold = Canvas.TypeBold; t.Italic = Canvas.TypeItalic;
        t.Underline = Canvas.TypeUnderline; t.Strikethrough = Canvas.TypeStrike; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnAlignChanged(object? sender, RoutedEventArgs e)
    {
        int a = int.TryParse((sender as Control)?.Tag as string, out var v) ? v : 0;
        Canvas.TypeAlign = a;
        _syncingType = true;   // enforce single selection
        AlignLeftBtn.IsChecked = a == 0; AlignCenterBtn.IsChecked = a == 1; AlignRightBtn.IsChecked = a == 2;
        _syncingType = false;
        if (_textTarget is not { } t) return;
        t.Align = (Sable.Engine.Layers.TextAlign)a; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnLineSpacingChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (!float.TryParse(LineSpacingBox.Text, out var v)) return;
        Canvas.TypeLineSpacing = Math.Clamp(v, 50f, 400f) / 100f;
        if (_syncingType || _textTarget is not { } t) return;
        t.LineSpacing = Canvas.TypeLineSpacing; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    /// <summary>Show a colour in the picker WITHOUT applying it to the brush/target.</summary>
    private void SetWheel(Avalonia.Media.Color c) => BrushColorView.SetColor(c);

    private void OnBrushColorChanged(Avalonia.Media.Color c)
    {
        if (_gradientTab)
        {
            GradBar.SetSelectedColor(c.R, c.G, c.B, c.A);   // colour wheel edits the selected stop
            return;
        }
        if (_shapeTarget is { } sh)
        {
            sh.R = c.R; sh.G = c.G; sh.B = c.B; sh.Dirty = true;   // recolour the selected shape (live)
            Doc?.SelectedLayer?.RefreshThumbnail();
            return;
        }
        if (_textTarget is { } tl)
        {
            tl.R = c.R; tl.G = c.G; tl.B = c.B; tl.Dirty = true;   // recolour the selected text (live)
            Doc?.SelectedLayer?.RefreshThumbnail();
            return;
        }
        Canvas.Brush.R = c.R;
        Canvas.Brush.G = c.G;
        Canvas.Brush.B = c.B;
    }

    private void OnSelectColorTab(object? sender, TappedEventArgs e)
        => SetColorTab((sender as Control)?.Tag as string == "grad");

    private void SetColorTab(bool grad)
    {
        if (GradientPanel is null) return;   // not initialized yet
        _gradientTab = grad;
        GradientPanel.IsVisible = grad;
        TabColor.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(grad ? "#FF666666" : "#FFAAAAAA"));
        TabGrad.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(grad ? "#FFAAAAAA" : "#FF666666"));
        if (grad) SyncWheelToStop();
        else SetWheel(Avalonia.Media.Color.FromRgb(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B));
    }

    private void OnGradAddStop(object? sender, RoutedEventArgs e) => GradBar.AddStop();
    private void OnGradDelStop(object? sender, RoutedEventArgs e) => GradBar.RemoveSelected();

    private void SyncWheelToStop()
    {
        var s = GradBar.SelectedStop;
        SetWheel(Avalonia.Media.Color.FromArgb(s.A, s.R, s.G, s.B));
    }

    // --- grouped tool strip (PLAN §14.5): flyout per group + hotkey cycle ----------
    // Icon = Lucide-style SVG path geometry (project icon system / no-emoji rule).
    private sealed record ToolDef(string Icon, string Name, Sable.Tools.ToolKind Kind);

    /// <summary>Build a fresh line-icon Path for a tool button (each button needs its own instance).</summary>
    private static Avalonia.Controls.Shapes.Path MakeIcon(string data, double size = 20) => new()
    {
        Classes = { "icon" },
        Width = size,
        Height = size,
        Data = Avalonia.Media.Geometry.Parse(data),
    };
    private sealed class ToolGroup
    {
        public string Letter = "";
        public ToolDef[] Tools = Array.Empty<ToolDef>();
        public int Current;
        public Button Button = null!;
        public Avalonia.Controls.Primitives.Popup? Popup;
        public Control? PopupPanel;
    }
    private readonly List<ToolGroup> _groups = new();
    private Avalonia.Controls.Primitives.Popup? _openPopup;
    private Control? _openPanel;
    private static readonly Avalonia.Media.IBrush ToolSelBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3A6EA5"));

    private void WireTools()
    {
        const string move   = "M12 2v20 M2 12h20 M5 9l-3 3 3 3 M9 5l3-3 3 3 M19 9l3 3-3 3 M9 19l3 3 3-3";
        const string xform  = "M22 6H2 M22 18H2 M6 2v20 M18 2v20";
        const string rect   = "M5 3a2 2 0 0 0-2 2 M19 3a2 2 0 0 1 2 2 M21 19a2 2 0 0 1-2 2 M5 21a2 2 0 0 1-2-2 M9 3h1 M9 21h1 M14 3h1 M14 21h1 M3 9v1 M21 9v1 M3 14v1 M21 14v1";
        const string ellip  = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z";
        const string lasso  = "M7 22a5 5 0 0 1-2-4 M3.3 14A6.8 6.8 0 0 1 2 10c0-4.4 4.5-8 10-8s10 3.6 10 8-4.5 8-10 8a12 12 0 0 1-5-1 M5 18a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
        const string wand   = "M15 4V2 M15 16v-2 M8 9h2 M20 9h2 M17.8 11.8 19 13 M17.8 6.2 19 5 M3 21l9-9 M12.2 6.2 11 5";
        const string brush  = "M9.06 11.9l8.07-8.06a2.85 2.85 0 1 1 4.03 4.03l-8.06 8.08 M7.07 14.94c-1.66 0-3 1.35-3 3.02 0 1.33-2.5 1.52-2 2.02 1.08 1.1 2.49 2.02 4 2.02 2.2 0 4-1.8 4-4.04a3.01 3.01 0 0 0-3-3.02z";
        const string eraser = "M7 21l-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21 M22 21H7 M5 11l9 9";
        const string fill   = "M19 11l-8-8-8.6 8.6a2 2 0 0 0 0 2.8l5.2 5.2c.8.8 2 .8 2.8 0L19 11z M5 2l5 5 M2 13h15 M22 20a2 2 0 1 1-4 0c0-1.6 1.7-2.4 2-4 .3 1.6 2 2.4 2 4z";
        const string grad   = "M4 4h16v16H4z M21 3 3 21";
        const string crop   = "M6 2v14a2 2 0 0 0 2 2h14 M18 22V8a2 2 0 0 0-2-2H2";
        const string shRect = "M3 5h18v14H3z";
        const string shEll  = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16z";
        const string shLine = "M4 20 20 4";
        const string clone  = "M5 22h14 M19 18v-3a2 2 0 0 0-2-2H7a2 2 0 0 0-2 2v3 M12 2a2 2 0 0 0-2 2c0 .8.5 1.4 1 1.7V9h2V5.7c.5-.3 1-.9 1-1.7a2 2 0 0 0-2-2z";
        const string type   = "M4 7V4h16v3 M9 20h6 M12 4v16";
        const string dodge  = "M12 8a4 4 0 1 0 0 8 4 4 0 1 0 0-8z M12 2v2 M12 20v2 M2 12h2 M20 12h2 M5 5l1.4 1.4 M17.6 17.6 19 19 M19 5l-1.4 1.4 M6.4 17.6 5 19";
        const string burn   = "M12 3c2 4 6 5 6 9a6 6 0 0 1-12 0c0-2 1-3 2-4 0 1 1 2 2 2 1-2 0-5 0-7z";
        const string sponge = "M5 11l6-6a4 4 0 0 1 6 0l1 1a4 4 0 0 1 0 6l-6 6 M5 11l7 7 M9 21H6a2 2 0 0 1-2-2v-3";
        const string blurB  = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16z M9 12h.01 M12 12h.01 M15 12h.01";
        const string sharpB = "M12 3 3 20h18z";
        const string smudge = "M3 14c4-7 14-7 18 0 M7 14a3 3 0 1 0 6 0";
        const string pipette= "M2 22l1-1h3l9-9 M3 21v-3l9-9 M15 6l3.4-3.4a2.1 2.1 0 1 1 3 3L21 9 M15 5l4 4";
        const string hand   = "M18 11V6a2 2 0 0 0-2-2 2 2 0 0 0-2 2 M14 10V4a2 2 0 0 0-2-2 2 2 0 0 0-2 2v2 M10 10.5V6a2 2 0 0 0-2-2 2 2 0 0 0-2 2v8 M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.9-6-2.3l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15";
        const string zoom   = "M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16z M21 21l-4.3-4.3 M11 8v6 M8 11h6";

        var defs = new (string letter, ToolDef[] tools)[]
        {
            ("V", new[] { new ToolDef(move, "Move", Sable.Tools.ToolKind.Move),
                          new ToolDef(xform, "Transform", Sable.Tools.ToolKind.Transform) }),
            ("M", new[] { new ToolDef(rect, "Rectangle Marquee", Sable.Tools.ToolKind.Marquee),
                          new ToolDef(ellip, "Elliptical Marquee", Sable.Tools.ToolKind.EllipseMarquee) }),
            ("L", new[] { new ToolDef(lasso, "Lasso", Sable.Tools.ToolKind.Lasso) }),
            ("W", new[] { new ToolDef(wand, "Magic Wand", Sable.Tools.ToolKind.MagicWand) }),
            ("B", new[] { new ToolDef(brush, "Brush", Sable.Tools.ToolKind.Brush),
                          new ToolDef(eraser, "Eraser", Sable.Tools.ToolKind.Eraser) }),
            ("G", new[] { new ToolDef(fill, "Fill", Sable.Tools.ToolKind.Fill),
                          new ToolDef(grad, "Gradient", Sable.Tools.ToolKind.Gradient) }),
            ("C", new[] { new ToolDef(crop, "Crop", Sable.Tools.ToolKind.Crop) }),
            ("U", new[] { new ToolDef(shRect, "Rectangle", Sable.Tools.ToolKind.ShapeRect),
                          new ToolDef(shEll, "Ellipse", Sable.Tools.ToolKind.ShapeEllipse),
                          new ToolDef(shLine, "Line", Sable.Tools.ToolKind.ShapeLine) }),
            ("S", new[] { new ToolDef(clone, "Clone Stamp", Sable.Tools.ToolKind.CloneStamp) }),
            ("O", new[] { new ToolDef(dodge, "Dodge", Sable.Tools.ToolKind.Dodge),
                          new ToolDef(burn, "Burn", Sable.Tools.ToolKind.Burn),
                          new ToolDef(sponge, "Sponge", Sable.Tools.ToolKind.Sponge),
                          new ToolDef(blurB, "Blur", Sable.Tools.ToolKind.BlurBrush),
                          new ToolDef(sharpB, "Sharpen", Sable.Tools.ToolKind.SharpenBrush),
                          new ToolDef(smudge, "Smudge", Sable.Tools.ToolKind.Smudge) }),
            ("T", new[] { new ToolDef(type, "Text", Sable.Tools.ToolKind.Type) }),
            ("I", new[] { new ToolDef(pipette, "Eyedropper", Sable.Tools.ToolKind.Eyedropper) }),
            ("H", new[] { new ToolDef(hand, "Hand", Sable.Tools.ToolKind.Hand) }),
            ("Z", new[] { new ToolDef(zoom, "Zoom", Sable.Tools.ToolKind.Zoom) }),
        };

        foreach (var (letter, tools) in defs)
        {
            var g = new ToolGroup { Letter = letter, Tools = tools };
            var btn = new Button { Classes = { "tool" }, Content = MakeIcon(tools[0].Icon), Tag = g };
            btn.Click += (_, _) => Canvas.ActiveTool = g.Tools[g.Current].Kind;

            var tip = $"{tools[0].Name} ({letter})";
            if (tools.Length > 1)
            {
                tip = string.Join(" / ", tools.Select(t => t.Name)) + $"  ({letter} cycles)";
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var t in tools)
                {
                    int mi = Array.IndexOf(tools, t);
                    var mb = new Button { Classes = { "tool" }, Content = MakeIcon(t.Icon) };
                    ToolTip.SetTip(mb, t.Name);
                    mb.Click += (_, _) => { SelectMember(g, mi); CloseToolFlyout(); };
                    sp.Children.Add(mb);
                }
                var border = new Border
                {
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF2A2A2A")),
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3A3A3A")),
                    BorderThickness = new Thickness(1),
                    Child = sp
                };
                var pop = new Avalonia.Controls.Primitives.Popup
                {
                    PlacementTarget = btn,
                    Placement = PlacementMode.Right,
                    IsLightDismissEnabled = false,   // grace-timer manages close
                    Child = border
                };
                g.Popup = pop;
                g.PopupPanel = border;
                ToolStrip.Children.Add(pop);         // host the popup in the tree
            }
            // hover any slot: close the previously-open flyout, open this slot's (if any)
            btn.PointerEntered += (_, _) => HoverGroup(g);
            ToolTip.SetTip(btn, tip);
            g.Button = btn;
            _groups.Add(g);
            ToolStrip.Children.Add(btn);
        }

        // grace close: shut the open flyout only when the pointer is over neither
        // the tool strip nor the flyout itself (so travelling button→flyout is fine)
        var graceTimer = new Avalonia.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(140) };
        graceTimer.Tick += (_, _) =>
        {
            if (_openPopup is null) return;
            bool overStrip = ToolStrip.IsPointerOver;
            bool overFlyout = _openPanel?.IsPointerOver ?? false;
            if (!overStrip && !overFlyout) CloseToolFlyout();
        };
        graceTimer.Start();

        Canvas.ToolChanged += OnToolChanged;
        OnToolChanged(Canvas.ActiveTool);
    }

    private void HoverGroup(ToolGroup g)
    {
        if (_openPopup is not null && _openPopup != g.Popup) CloseToolFlyout();   // swap
        if (g.Popup is { } pop && !pop.IsOpen)
        {
            pop.IsOpen = true;
            _openPopup = pop;
            _openPanel = g.PopupPanel;
        }
    }

    private void CloseToolFlyout()
    {
        if (_openPopup is not null) _openPopup.IsOpen = false;
        _openPopup = null;
        _openPanel = null;
    }

    private void SelectMember(ToolGroup g, int idx)
    {
        g.Current = Math.Clamp(idx, 0, g.Tools.Length - 1);
        Canvas.ActiveTool = g.Tools[g.Current].Kind;   // event → OnToolChanged updates icon/highlight
    }

    private void CycleGroup(string letter)
    {
        var g = _groups.FirstOrDefault(x => x.Letter == letter);
        if (g is null) return;
        bool active = g.Tools.Any(t => t.Kind == Canvas.ActiveTool);
        SelectMember(g, active ? (g.Current + 1) % g.Tools.Length : g.Current);
    }

    private void OnToolChanged(Sable.Tools.ToolKind kind)
    {
        foreach (var g in _groups)
        {
            int idx = Array.FindIndex(g.Tools, t => t.Kind == kind);
            bool sel = idx >= 0;
            if (sel)
            {
                g.Current = idx;
                g.Button.Content = MakeIcon(g.Tools[idx].Icon);
                ToolStatus.Text = g.Tools[idx].Name;
            }
            g.Button.Background = sel ? ToolSelBrush : Avalonia.Media.Brushes.Transparent;
        }
        // keep the colour panel in sync: Gradient tool → Gradients tab, everything else → Color
        SetColorTab(kind == Sable.Tools.ToolKind.Gradient);
        UpdateOptionsBar(kind);
    }

    // show only the options-bar controls relevant to the active tool
    private void UpdateOptionsBar(Sable.Tools.ToolKind k)
    {
        if (SizeOpts is null) return;   // not initialized yet
        SizeOpts.IsVisible = k is ToolKind.Brush or ToolKind.Eraser or ToolKind.CloneStamp or ToolKind.ShapeLine
                              or ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
                              or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge;
        StrengthOpts.IsVisible = k is ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
                                  or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge;
        SelectOpts.IsVisible = k is ToolKind.Marquee or ToolKind.EllipseMarquee or ToolKind.Lasso or ToolKind.MagicWand;
        TypeOpts.IsVisible = k == ToolKind.Type;
        MaskHint.IsVisible = k is ToolKind.Brush or ToolKind.Eraser;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Canvas.TextEditing) return;   // skip tool shortcuts; chars handled by OnTextInput
        const double step = 40;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: Canvas.ZoomBy(1.1); break;
            case Key.OemMinus or Key.Subtract: Canvas.ZoomBy(1.0 / 1.1); break;
            case Key.D0 or Key.NumPad0: Canvas.ResetView(); break;
            // tool shortcuts (PLAN §14) — re-press cycles within the group
            case Key.V: CycleGroup("V"); break;
            case Key.M: CycleGroup("M"); break;
            case Key.L: CycleGroup("L"); break;
            case Key.W: CycleGroup("W"); break;
            case Key.B: CycleGroup("B"); break;
            case Key.G: CycleGroup("G"); break;
            case Key.C: CycleGroup("C"); break;
            case Key.U: CycleGroup("U"); break;
            case Key.S when e.KeyModifiers == KeyModifiers.None: CycleGroup("S"); break;
            case Key.O: CycleGroup("O"); break;
            case Key.T: CycleGroup("T"); break;
            case Key.I: CycleGroup("I"); break;
            case Key.H: CycleGroup("H"); break;
            case Key.Z: CycleGroup("Z"); break;
            case Key.K: Canvas.PaintMask = !Canvas.PaintMask; break;   // edit layer mask
            case Key.Escape: Canvas.Deselect(); break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control: Canvas.Deselect(); break;
            case Key.Delete or Key.Back: Canvas.DeleteSelection(); break;
            case Key.Left: Canvas.PanBy(step, 0); break;
            case Key.Right: Canvas.PanBy(-step, 0); break;
            case Key.Up: Canvas.PanBy(0, step); break;
            case Key.Down: Canvas.PanBy(0, -step); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    private void LoadDocument(Document doc)
    {
        var vm = new DocumentViewModel(doc);
        Canvas.Document = doc;
        DataContext = vm;
        UpdateActiveLayer(vm);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DocumentViewModel.SelectedLayer)) return;
            UpdateActiveLayer(vm);
            if (vm.SelectedLayer?.IsEffect == true) ShowAdjustmentWindow();
            else _adjWindow?.Close();
        };
        if (_adjWindow is not null) _adjWindow.DataContext = vm;
        // brush strokes become undoable commands on the same stack as layer ops
        Canvas.CommandProduced = cmd =>
        {
            vm.Undo.Execute(cmd);
            vm.SelectedLayer?.RefreshThumbnail();   // live row thumb after paint/fill/erase/delete
        };
        // a drawn shape becomes its own new layer (so Move/V grabs just the shape)
        Canvas.LayerProduced = layer => vm.AddAndSelect(layer);
        // eyedropper (Alt+click) updates the color picker
        Canvas.ColorPicked = (r, g, b) => SetWheel(Avalonia.Media.Color.FromRgb(r, g, b));
    }

    private void UpdateActiveLayer(DocumentViewModel vm)
    {
        var m = vm.SelectedLayer?.Model;
        Canvas.ActiveLayer = m as Sable.Engine.Layers.PixelLayer;   // paint target (pixel only)
        Canvas.SelLayer = m;                                        // Move/bounds target (any type)
        _shapeTarget = m as Sable.Engine.Layers.ShapeLayer;
        _textTarget = m as Sable.Engine.Layers.TextLayer;
        // mirror a selected text layer into the options bar (font controls)
        if (_textTarget is { } txt)
        {
            _syncingType = true;
            TypeSizeBox.Text = ((int)txt.FontSize).ToString();
            BoldBtn.IsChecked = txt.Bold;
            ItalicBtn.IsChecked = txt.Italic;
            UnderlineBtn.IsChecked = txt.Underline;
            StrikeBtn.IsChecked = txt.Strikethrough;
            AlignLeftBtn.IsChecked = txt.Align == Sable.Engine.Layers.TextAlign.Left;
            AlignCenterBtn.IsChecked = txt.Align == Sable.Engine.Layers.TextAlign.Center;
            AlignRightBtn.IsChecked = txt.Align == Sable.Engine.Layers.TextAlign.Right;
            LineSpacingBox.Text = ((int)(txt.LineSpacing * 100)).ToString();
            if (!string.IsNullOrEmpty(txt.FontFamily)) FontCombo.SelectedItem = txt.FontFamily;
            _syncingType = false;
        }
        // point the colour wheel at the right target (unless the gradient tab owns it)
        if (!_gradientTab)
        {
            if (_shapeTarget is { } s) SetWheel(Avalonia.Media.Color.FromRgb(s.R, s.G, s.B));
            else if (_textTarget is { } t) SetWheel(Avalonia.Media.Color.FromRgb(t.R, t.G, t.B));
            else SetWheel(Avalonia.Media.Color.FromRgb(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B));
        }
    }

    private void ShowAdjustmentWindow()
    {
        if (_adjWindow is not null) { _adjWindow.Activate(); return; }

        var win = new AdjustmentWindow { DataContext = DataContext };
        win.Closed += (_, _) => _adjWindow = null;
        _adjWindow = win;
        win.Show(this);            // modeless, owned by main
        CenterOverCanvas(win);
    }

    // Position a tool window centered over the canvas surface.
    private void CenterOverCanvas(Window win)
    {
        var center = Canvas.PointToScreen(new Point(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 2));
        double scale = win.RenderScaling;
        win.Position = new PixelPoint(
            center.X - (int)(win.Width * scale / 2),
            center.Y - (int)(win.Height * scale / 2));
    }

    private async void OnResizeDocument(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc || Doc is not { } vm) return;
        var dlg = new ResizeDocumentWindow(doc.Width, doc.Height, doc.Dpi);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;
        vm.Undo.Execute(new Sable.Engine.Commands.ResizeCommand(doc, dlg.NewW, dlg.NewH, dlg.Dpi, dlg.Bilinear));
        Canvas.ResetView();
    }

    private async void OnResizeCanvas(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc || Doc is not { } vm) return;
        var dlg = new ResizeCanvasWindow(doc.Width, doc.Height);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;

        // anchor → crop origin (negative = grow, padding transparent). CropCommand handles both.
        int dx = dlg.NewW - doc.Width, dy = dlg.NewH - doc.Height;
        int leftPad = dlg.AnchorX == 0 ? 0 : dlg.AnchorX == 1 ? dx / 2 : dx;
        int topPad = dlg.AnchorY == 0 ? 0 : dlg.AnchorY == 1 ? dy / 2 : dy;
        vm.Undo.Execute(new Sable.Engine.Commands.CropCommand(doc, -leftPad, -topPad, dlg.NewW, dlg.NewH));
        Canvas.ResetView();
    }

    private void OnToggleAdjustments(object? sender, RoutedEventArgs e)
    {
        if (_adjWindow is not null) _adjWindow.Close();
        else ShowAdjustmentWindow();
    }

    private async void OnOpenImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        LoadDocument(DocumentIO.OpenImage(path));
    }

    private async void OnOpenSable(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Sable document",
            AllowMultiple = false,
            FileTypeFilter = new[] { SableType }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        LoadDocument(SableFile.Load(path));
        _currentPath = path;
    }

    private async void OnSaveSable(object? sender, RoutedEventArgs e)
    {
        if (_currentPath is { } p && Canvas.Document is { } doc) { SableFile.Save(doc, p); return; }
        await SaveAs();
    }

    private async void OnSaveAsSable(object? sender, RoutedEventArgs e) => await SaveAs();

    private async Task SaveAs()
    {
        if (Canvas.Document is not { } doc) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Sable document",
            SuggestedFileName = "untitled.sable",
            DefaultExtension = "sable",
            FileTypeChoices = new[] { SableType }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        SableFile.Save(doc, path);
        _currentPath = path;
    }

    private async void OnExportPng(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PNG",
            SuggestedFileName = "export.png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } }
            }
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var rgba = Canvas.ReadComposite();
        if (rgba is null) return;
        DocumentIO.ExportPng(path, doc.Width, doc.Height, rgba);
    }
}
