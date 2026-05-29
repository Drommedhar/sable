using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sable.Engine;
using Sable.Engine.IO;
using Sable.Format;
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

        WireTools();
    }

    private DocumentViewModel? Doc => DataContext as DocumentViewModel;

    private static LayerViewModel? LayerOf(object? source)
        => (source as Control)?.DataContext as LayerViewModel;

    private void OnLayerSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Doc?.SetSelection(LayerList.SelectedItems?.Cast<LayerViewModel>() ?? Enumerable.Empty<LayerViewModel>());

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

    private void OnBrushColorChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        var c = e.NewColor;
        Canvas.Brush.R = c.R;
        Canvas.Brush.G = c.G;
        Canvas.Brush.B = c.B;
        if (BrushColorSwatch is not null)
            BrushColorSwatch.Background = new Avalonia.Media.SolidColorBrush(c);
    }

    // --- grouped tool strip (PLAN §14.5): flyout per group + hotkey cycle ----------
    private sealed record ToolDef(string Icon, string Name, Sable.Tools.ToolKind Kind);
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
        var defs = new (string letter, ToolDef[] tools)[]
        {
            ("V", new[] { new ToolDef("↹", "Move", Sable.Tools.ToolKind.Move),
                          new ToolDef("⤢", "Transform", Sable.Tools.ToolKind.Transform) }),
            ("M", new[] { new ToolDef("▭", "Rectangle Marquee", Sable.Tools.ToolKind.Marquee),
                          new ToolDef("◯", "Elliptical Marquee", Sable.Tools.ToolKind.EllipseMarquee) }),
            ("L", new[] { new ToolDef("⫯", "Lasso", Sable.Tools.ToolKind.Lasso) }),
            ("W", new[] { new ToolDef("✦", "Magic Wand", Sable.Tools.ToolKind.MagicWand) }),
            ("B", new[] { new ToolDef("🖌", "Brush", Sable.Tools.ToolKind.Brush),
                          new ToolDef("⬚", "Eraser", Sable.Tools.ToolKind.Eraser) }),
            ("G", new[] { new ToolDef("🪣", "Fill", Sable.Tools.ToolKind.Fill) }),
            ("I", new[] { new ToolDef("⛶", "Eyedropper", Sable.Tools.ToolKind.Eyedropper) }),
            ("H", new[] { new ToolDef("✋", "Hand", Sable.Tools.ToolKind.Hand) }),
            ("Z", new[] { new ToolDef("🔍", "Zoom", Sable.Tools.ToolKind.Zoom) }),
        };

        foreach (var (letter, tools) in defs)
        {
            var g = new ToolGroup { Letter = letter, Tools = tools };
            var btn = new Button { Classes = { "tool" }, Content = tools[0].Icon, Tag = g };
            btn.Click += (_, _) => Canvas.ActiveTool = g.Tools[g.Current].Kind;

            var tip = $"{tools[0].Name} ({letter})";
            if (tools.Length > 1)
            {
                tip = string.Join(" / ", tools.Select(t => t.Name)) + $"  ({letter} cycles)";
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var t in tools)
                {
                    int mi = Array.IndexOf(tools, t);
                    var mb = new Button { Classes = { "tool" }, Content = t.Icon };
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
                g.Button.Content = g.Tools[idx].Icon;
                ToolStatus.Text = g.Tools[idx].Name;
            }
            g.Button.Background = sel ? ToolSelBrush : Avalonia.Media.Brushes.Transparent;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
        Canvas.CommandProduced = cmd => vm.Undo.Execute(cmd);
        // eyedropper (Alt+click) updates the color picker + swatch
        Canvas.ColorPicked = (r, g, b) =>
        {
            var c = Avalonia.Media.Color.FromRgb(r, g, b);
            BrushColorView.Color = c;
            BrushColorSwatch.Background = new Avalonia.Media.SolidColorBrush(c);
        };
    }

    private void UpdateActiveLayer(DocumentViewModel vm)
        => Canvas.ActiveLayer = vm.SelectedLayer?.Model as Sable.Engine.Layers.PixelLayer;

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
