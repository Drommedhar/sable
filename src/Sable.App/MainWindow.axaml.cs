using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sable.App.Localization;
using Sable.Engine;
using Sable.Engine.Clipboard;
using Sable.Engine.IO;
using Sable.Engine.Layers;
using Sable.Format;
using Sable.Imaging;
using Sable.Tools;
using Sable.UI.ViewModels;

namespace Sable.App;

public partial class MainWindow : Window
{
    private bool _panning;
    private Point _lastPointer;
    private string? _currentPath;
    private AdjustmentWindow? _adjWindow;
    private EffectsWindow? _fxWindow;
    private TransformWindow? _transformWindow;
    private HistoryWindow? _historyWindow;
    private readonly System.Collections.ObjectModel.ObservableCollection<DocumentTab> _tabs = new();
    private DocumentTab? _activeTab;
    private int _untitledCounter = 1;

    private LayerViewModel? _dragSource;
    private System.Collections.Generic.List<Sable.Engine.Layers.Layer>? _dragModels;   // whole multi-selection being dragged
    private Point _dragStart;
    private bool _dragging;
    private LayerViewModel? _dropTarget;
    private bool _dropAbove;
    private bool _dropInto;        // dropping ONTO the target row (nest / into-group / auto-group)
    private LayerViewModel? _pendingCollapse;   // row to collapse the selection to on release-without-drag

    // arrow-key nudge coalescing: while an arrow is held (autorepeat), move the layer live and
    // commit ONE undo entry on KeyUp — avoids a flood of undo entries + Resync per keypress.
    private Sable.Engine.Layers.Layer? _nudgeLayer;
    private int _nudgeStartX, _nudgeStartY;
    private Key _nudgeKey = Key.None;

    private static FilePickerFileType SableType => new(Loc.T("mainWindow.sableDocumentType")) { Patterns = new[] { "*.sable" } };

    private readonly string[] _launchArgs;

    public MainWindow() : this(null) { }

    public MainWindow(string[]? args)
    {
        _launchArgs = args ?? System.Array.Empty<string>();

        // custom title bar (menu lives in the header): Avalonia 12 WindowDecorations — BorderOnly
        // keeps the OS resize border + shadow but drops the native caption/title bar.
        WindowDecorations = Avalonia.Controls.WindowDecorations.BorderOnly;

        InitializeComponent();

        // Start with no document (welcome / empty state) — New/Open/paste/drop creates the first tab.
        TabStrip.ItemsSource = _tabs;
        UpdateEmptyState();

        // the embedded adjustment/filter editor needs the composite for its Curves/Levels histogram
        AdjPanel.CompositeProvider = () => Canvas.ReadComposite();
        AdjPanel.CompareStarted += BeginPeek;
        AdjPanel.CompareEnded += EndPeek;

        _brushPresets = Sable.Tools.BrushPresetStore.Load();
        RebuildBrushPresetCombo();

        // layer drag-drop (manual pointer DnD: reorder / move into group / auto-group)
        LayerList.AddHandler(PointerPressedEvent, OnLayerPointerPressed, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerMovedEvent, OnLayerPointerMoved, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerReleasedEvent, OnLayerPointerReleased, RoutingStrategies.Tunnel);
        // open the row context flyout ourselves so a multi-selection survives. ContextRequested is a
        // BUBBLE event — registering it as Tunnel meant the handler never ran. Bubble + handledToo so
        // we still get it after the ListBox's own handling.
        LayerList.AddHandler(ContextRequestedEvent, OnLayerContextRequested,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // selection keys tunnel-first so a focused panel (e.g. the layers list) can't eat Delete
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        // caption buttons act on PointerReleased (the Click gesture is unreliable in the custom title bar)
        WireCaptionButtons();

        // drop image / .sable files onto the window chrome → open each as a new tab.
        // Works on Windows/macOS via Avalonia DragDrop. On Linux/X11, Avalonia 12.0 doesn't fire
        // these events (no XDND support), so the X11InputSource handles XDND directly and raises
        // Canvas.FilesDropped — both paths call the same OnFilesDropPaths handler.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFilesDragOver);
        AddHandler(DragDrop.DropEvent, OnFilesDrop);
        Canvas.FilesDropped += paths => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnFilesDropPaths(paths));

        // gradient editor shares the canvas's gradient def; selecting a stop routes the colour wheel to it
        GradBar.Def = Canvas.Gradient;
        GradBar.StopSelected += _ => { if (_gradientTab) SyncWheelToStop(); };

        // font picker + on-canvas text editing
        FontCombo.ItemsSource = Sable.Imaging.TextRaster.Families();
        Canvas.TextEditStarted += t => Doc?.SelectModel(t);   // select the edited text layer

        // custom colour wheel → brush / selected shape-text / gradient stop
        BrushColorView.ColorChanged += OnBrushColorChanged;

        WireTools();
        WireBrushDynamics();

        // settings: restore window placement + theme + recent menu + last session (PLAN §17.1)
        ApplySettings();
        RebuildKeyGestures();   // load the rebindable keymap (defaults + user overrides)
        Opened += (_, _) =>
        {
            OfferCrashRecovery();           // restore autosaved docs from a previous unclean exit
            RestoreSession();
            OpenLaunchArgs();               // files passed on the command line (file associations)
            StartAutosave();
            if (_settings.AutoCheckUpdates) _ = CheckForUpdatesAsync(manual: false);   // silent launch check
            if (!_settings.OnboardingShown)
            {
                _settings.OnboardingShown = true;
                Sable.Core.Settings.SettingsService.Save(_settings);
                new TipsWindow().Show(this);   // first-run orientation card (once)
            }
        };
        Closing += OnWindowClosing;

        // live status bar (zoom + cursor position) + rulers
        Canvas.ViewChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { UpdateZoomLabel(); UpdateRulers(); });
        Canvas.CursorDocMoved += (x, y) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateCursorLabel(x, y);
            RulerH.SetCursor(x); RulerV.SetCursor(y);
        });
        // canvas can change scale on window resize without a ViewChanged → refresh rulers on layout
        Canvas.LayoutUpdated += (_, _) => UpdateRulers();

        // live histogram of the composite (low-frequency; readback is not free)
        var histTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        histTimer.Tick += (_, _) =>
        {
            if (HistView is not null && HistView.IsEffectivelyVisible && Canvas.Document is not null
                && Canvas.ReadComposite() is { } rgba)
                HistView.SetBins(Histogram.Compute(rgba));
            RefreshNavigator();
        };
        histTimer.Start();

        // navigator: click/drag re-centres; view changes move the viewport rect immediately
        NavView.ViewCenterRequested += (dx, dy) => Canvas.CenterOnDoc(dx, dy);
        Canvas.ViewChanged += () =>
        {
            if (NavigatorTabPanel.IsVisible) UpdateNavRect();
            UpdateTaskBar();   // keep the contextual pill glued to the selection during pan/zoom (no 250ms lag)
        };

        // status-bar memory readout (working set + VRAM in use), low frequency
        var memTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        memTimer.Tick += (_, _) => UpdateMemLabel();
        memTimer.Start();
        UpdateMemLabel();

        // contextual task bar follows the selection (position + show/hide)
        var tbTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        tbTimer.Tick += (_, _) => { UpdateTaskBar(); UpdateToasts(); };
        tbTimer.Start();

        // click a ruler to drop a guide: top ruler → vertical guide (X), left ruler → horizontal guide (Y)
        RulerH.GuideRequested += d => AddGuide(vertical: true, d);
        RulerV.GuideRequested += d => AddGuide(vertical: false, d);
    }

    /// <summary>Status-bar RAM/VRAM readout: app working set + used/total VRAM (when the probe knows it).</summary>
    private void UpdateMemLabel()
    {
        static string Gb(ulong b) => b >= 10UL * 1024 * 1024 * 1024
            ? $"{b / (1024.0 * 1024 * 1024):0} GB"
            : $"{b / (1024.0 * 1024 * 1024):0.0} GB";
        var ram = (ulong)System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var total = _gpuProbe.TotalVramBytes();
        var free = _gpuProbe.FreeVramBytes();
        var text = $"RAM {Gb(ram)}";
        if (total > 0 && free > 0 && free <= total)
            text += $"  ·  VRAM {Gb(total - free)}/{Gb(total)}";
        MemLabel.Text = text;
    }

    private void AddGuide(bool vertical, double docPos)
    {
        if (_activeTab?.Doc is not { } doc) return;
        int p = (int)System.Math.Round(docPos);
        if (vertical) { if (p < 0 || p > doc.Width) return; if (!doc.GuidesX.Contains(p)) doc.GuidesX.Add(p); }
        else { if (p < 0 || p > doc.Height) return; if (!doc.GuidesY.Contains(p)) doc.GuidesY.Add(p); }
    }

    private void UpdateRulers()
    {
        if (RulerH is null || RulerV is null) return;
        var (ox, oy, scale) = Canvas.ViewportDip;
        RulerH.SetView(ox, scale);
        RulerV.SetView(oy, scale);
        if (_activeTab?.Doc is { } d)
        {
            RulerH.SetGuides(d.GuidesX.ToArray());   // top ruler = X axis = vertical guides
            RulerV.SetGuides(d.GuidesY.ToArray());
        }
    }

    private readonly Sable.Core.Settings.SableSettings _settings = Sable.Core.Settings.SettingsService.Load();

    private void ApplySettings()
    {
        Width = _settings.WinW;
        Height = _settings.WinH;
        if (_settings.WinX is { } wx && _settings.WinY is { } wy)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)wx, (int)wy);
        }
        if (_settings.WinMaximized) WindowState = WindowState.Maximized;
        // migrate legacy presets that still reference an external workflow file → copy into Sable's storage
        if (Sable.Core.Ai.WorkflowStore.Migrate(_settings)) Sable.Core.Settings.SettingsService.Save(_settings);
        ApplyTheme(_settings.Theme);
        ApplyOverlayColors();
        ApplyGridAndSnap();
        ApplyAiVisibility();
        ApplyPanelVisibility();
        RebuildRecentMenu();
    }

    /// <summary>Show/hide the tool strip and the right-side Colour/Layers panels (Window menu toggles).</summary>
    private void ApplyPanelVisibility()
    {
        ToolStripHost.IsVisible = _settings.ShowToolsPanel;
        MainSplit.ColumnDefinitions[0].Width = new GridLength(_settings.ShowToolsPanel ? 40 : 0);

        ColorPanelRoot.IsVisible = _settings.ShowColorPanel;
        LayersPanelRoot.IsVisible = _settings.ShowLayersPanel;
        PanelSplitter.IsVisible = _settings.ShowColorPanel && _settings.ShowLayersPanel;
        bool right = _settings.ShowColorPanel || _settings.ShowLayersPanel;
        RightPanelRoot.IsVisible = right;
        MainSplit.ColumnDefinitions[2].Width = new GridLength(right ? 300 : 0);

        PanelToolsItem.IsChecked = _settings.ShowToolsPanel;
        PanelColorItem.IsChecked = _settings.ShowColorPanel;
        PanelLayersItem.IsChecked = _settings.ShowLayersPanel;
        ContextBarItem.IsChecked = _settings.ShowContextBar;
    }

    /// <summary>Checkbox on the welcome screen itself: unticking hides the welcome content
    /// from then on (re-enable in Preferences ▸ User Interface).</summary>
    private void OnToggleWelcome(object? sender, RoutedEventArgs e)
    {
        _settings.ShowWelcomeScreen = WelcomeShowCheck.IsChecked == true;
        Sable.Core.Settings.SettingsService.Save(_settings);
        UpdateEmptyState();
    }

    private void OnToggleContextBar(object? sender, RoutedEventArgs e)
    {
        _settings.ShowContextBar = !_settings.ShowContextBar;
        ContextBarItem.IsChecked = _settings.ShowContextBar;
        UpdateTaskBar();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    // ===== contextual task bar: floating pill under the active selection (airspace-safe top-level) =====
    private TaskBarWindow? _taskBar;

    private void UpdateTaskBar()
    {
        // NOTE: no IsActive gate — canvas clicks go to the native HWND with MA_NOACTIVATE, so the
        // Avalonia window often isn't "active" while the user works; gating on it made the pill
        // vanish for good after its first click. Window state covers the minimized case.
        bool show = _settings.ShowContextBar && WindowState != WindowState.Minimized
            && _activeTab is not null && Canvas.IsVisible
            && TopLevel.GetTopLevel(Canvas) is not null   // detached during dock rebuilds (Reset Layout)
            && Canvas.Document is { Selection: { W: > 2, H: > 2 } };
        if (!show)
        {
            if (_taskBar?.IsVisible == true) _taskBar.Hide();
            return;
        }
        var r = Canvas.Document!.Selection!.Value;
        var (ox, oy, s) = Canvas.ViewportDip;
        double cx = ox + (r.X + r.W / 2.0) * s;
        double by = oy + r.Bottom * s + 12;
        // clamp into the canvas area so the pill stays reachable for off-screen selections
        cx = System.Math.Clamp(cx, 60, System.Math.Max(60, Canvas.Bounds.Width - 60));
        by = System.Math.Clamp(by, 0, System.Math.Max(0, Canvas.Bounds.Height - 40));
        var screen = Canvas.PointToScreen(new Avalonia.Point(cx, by));

        _taskBar ??= CreateTaskBar();
        _taskBar.SetGenFillVisible(AiGenFillItem.IsVisible && AiGenFillItem.IsEnabled);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int halfW = (int)((_taskBar.Bounds.Width > 0 ? _taskBar.Bounds.Width : 240) * scale / 2);
        _taskBar.Position = new PixelPoint(screen.X - halfW, screen.Y);
        if (!_taskBar.IsVisible) _taskBar.Show(this);
    }

    // ===== notification toasts: Affinity-style cards in the canvas's top-right corner =====
    private ToastWindow? _toasts;

    /// <summary>Show a dismissable notification card (missing fonts, import notes, …).</summary>
    private void ShowToast(string title, string body, string? actionLabel = null, System.Action? action = null)
    {
        _toasts ??= new ToastWindow();
        _toasts.Push(title, body, actionLabel, action);
        UpdateToasts();
    }

    /// <summary>Keep the toast stack glued to the canvas's top-right corner (window move/resize/minimise).</summary>
    private void UpdateToasts()
    {
        if (_toasts is null || _toasts.Items.Count == 0) return;
        // dock rebuilds (Reset Layout) detach the canvas momentarily — PointToScreen throws then
        bool canvasAttached = TopLevel.GetTopLevel(Canvas) is not null;
        if (WindowState == WindowState.Minimized || !Canvas.IsVisible || !canvasAttached)
        {
            if (_toasts.IsVisible) _toasts.Hide();
            return;
        }
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var corner = Canvas.PointToScreen(new Avalonia.Point(Canvas.Bounds.Width, 0));
        _toasts.Position = new PixelPoint(corner.X - (int)(_toasts.Width * scale) - (int)(8 * scale), corner.Y + (int)(8 * scale));
        if (!_toasts.IsVisible) _toasts.Show(this);
    }

    private TaskBarWindow CreateTaskBar()
    {
        var w = new TaskBarWindow();
        // clicking the pill activates IT — bring the main window back first so dialogs the
        // action opens (AmountDialog, gen-fill) get a live, focused owner
        void Run(System.Action act) { Activate(); act(); }
        w.GenFillClicked += () => Run(() => OnAiGenerativeFill(null, _e));
        w.MaskClicked += () => Run(() => Doc?.ToggleMaskCommand.Execute(null));
        w.InvertClicked += () => Run(() => OnInvertSelection(null, _e));
        w.DeselectClicked += () => Run(() => OnDeselect(null, _e));
        return w;
    }

    private void OnTogglePanelTools(object? sender, RoutedEventArgs e)
    {
        _settings.ShowToolsPanel = !_settings.ShowToolsPanel;
        ApplyPanelVisibility();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    private void OnTogglePanelColor(object? sender, RoutedEventArgs e)
    {
        _settings.ShowColorPanel = !_settings.ShowColorPanel;
        ApplyPanelVisibility();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    private void OnTogglePanelLayers(object? sender, RoutedEventArgs e)
    {
        _settings.ShowLayersPanel = !_settings.ShowLayersPanel;
        ApplyPanelVisibility();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    /// <summary>Push the customisable canvas-overlay colours from settings to the GPU canvas.</summary>
    private void ApplyOverlayColors()
    {
        Canvas.SetOverlayColors(
            _settings.GuideRgb(), _settings.SmartGuideRgb(),
            _settings.GridRgb(), _settings.QuickMaskRgb());
        Canvas.SetCheckerPrefs(_settings.CheckerSize, _settings.CheckerARgb(), _settings.CheckerBRgb());
        Canvas.SetCursorPrefs(_settings.PreciseCursor);
    }

    private void ApplyTheme(Sable.Core.Settings.AppTheme theme)
    {
        // Chrome surfaces bound via {DynamicResource Chrome…} swap with the variant (Theme.axaml
        // ThemeDictionaries). Gray = custom variant inheriting Dark (Fluent controls stay dark).
        var variant = theme switch
        {
            Sable.Core.Settings.AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
            Sable.Core.Settings.AppTheme.Gray => Themes.Gray,
            _ => Avalonia.Styling.ThemeVariant.Dark,
        };
        RequestedThemeVariant = variant;

        // user accent colour: a window-level resource override beats the theme-dictionary default,
        // and every {DynamicResource ChromeAccent} consumer updates live
        var (ar, ag, ab) = _settings.AccentRgb();
        Resources["ChromeAccent"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(ar, ag, ab));

        // keep the GPU pasteboard (canvas surround) in sync with the ChromeCanvas token
        // (resolved directly — variant resources aren't reliably available at ctor time)
        switch (theme)
        {
            case Sable.Core.Settings.AppTheme.Light: Canvas.SetPasteboardColor(0xC4, 0xC4, 0xC4); break;
            case Sable.Core.Settings.AppTheme.Gray:  Canvas.SetPasteboardColor(0x2A, 0x2A, 0x2A); break;
            default:                                 Canvas.SetPasteboardColor(0x1B, 0x1B, 0x1B); break;
        }
    }

    private void RestoreSession()
    {
        if (!_settings.ReopenOnStartup) return;
        foreach (var path in _settings.OpenTabs.ToList())
        {
            try
            {
                if (System.IO.File.Exists(path) && path.EndsWith(".sable", System.StringComparison.OrdinalIgnoreCase))
                {
                    var tab = OpenInNewTab(SableFile.Load(path), path, System.IO.Path.GetFileName(path));
                    tab.IsDirty = false;
                }
            }
            catch { /* skip missing/corrupt */ }
        }
    }

    private void OnWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        _settings.WinMaximized = WindowState == WindowState.Maximized;
        if (!_settings.WinMaximized)
        {
            _settings.WinW = Width; _settings.WinH = Height;
            _settings.WinX = Position.X; _settings.WinY = Position.Y;
        }
        _settings.OpenTabs = _tabs.Where(t => t.Path is not null).Select(t => t.Path!).ToList();
        Sable.Core.Settings.SettingsService.Save(_settings);
        RecoveryService.Clear();   // clean exit → discard autosaved recovery copies
        try { _comfy?.Dispose(); } catch { }     // stop the ComfyUI process
    }

    /// <summary>Record a file in the recent list + persist + rebuild the menu.</summary>
    private void NoteRecent(string path)
    {
        _settings.AddRecent(path);
        Sable.Core.Settings.SettingsService.Save(_settings);
        RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        if (RecentMenu is null) return;
        RecentMenu.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = Loc.T("mainWindow.recentNone"), IsEnabled = false });
            return;
        }
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = System.IO.Path.GetFileName(path), Tag = path };
            item.Click += OnOpenRecent;
            RecentMenu.Items.Add(item);
        }
    }

    private void OnOpenRecent(object? sender, RoutedEventArgs e)
    {
        MainMenu.Close();   // dynamically-added submenu items don't auto-close the menu
        if (sender is MenuItem { Tag: string path }) OpenPath(path);
    }

    /// <summary>Welcome-screen recent-file cards (thumbnail + name; click opens). Thumbnails come from
    /// the .sable embedded preview.png or a downscaled decode of the image file, loaded off-thread.</summary>
    private void RebuildWelcome()
    {
        if (WelcomeRecents is null) return;
        WelcomeRecents.Children.Clear();
        var recents = _settings.RecentFiles.Where(System.IO.File.Exists).Take(6).ToList();
        WelcomeRecentHeader.IsVisible = recents.Count > 0;
        foreach (var path in recents)
        {
            var img = new Avalonia.Controls.Image
            {
                Width = 120, Height = 84,
                Stretch = Avalonia.Media.Stretch.Uniform,
            };
            var thumbHost = new Border
            {
                Width = 124, Height = 88,
                CornerRadius = new Avalonia.CornerRadius(3),
                Child = img,
            };
            thumbHost.Bind(Border.BackgroundProperty, this.GetResourceObservable("ChromePanel2"));
            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                FontSize = 11, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxWidth = 124, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeTextDim"));
            var card = new Button
            {
                Padding = new Thickness(6),
                Margin = new Thickness(5),
                Background = Avalonia.Media.Brushes.Transparent,
                Tag = path,
                Content = new StackPanel { Spacing = 5, Children = { thumbHost, name } },
            };
            ToolTip.SetTip(card, path);
            card.Click += (_, _) => OpenPath(path);
            WelcomeRecents.Children.Add(card);
            LoadWelcomeThumb(path, img);
        }
    }

    private void LoadWelcomeThumb(string path, Avalonia.Controls.Image img)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Avalonia.Media.Imaging.Bitmap? bmp = null;
                if (path.EndsWith(".sable", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (SableFile.TryReadPreviewPng(path) is { } png)
                        bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(new System.IO.MemoryStream(png), 240);
                }
                else
                {
                    using var fs = System.IO.File.OpenRead(path);
                    bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 240);
                }
                if (bmp is not null)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => img.Source = bmp);
            }
            catch { /* thumbnail is best-effort */ }
        });
    }

    /// <summary>Snapshot the (still-active) composite as the tab's hover-preview bitmap.</summary>
    private void CaptureTabPreview(DocumentTab tab)
    {
        try
        {
            if (!ReferenceEquals(Canvas.Document, tab.Doc)) return;   // only the mounted doc can be read back
            if (MakeSavePreviewPng() is { } png)
                tab.Preview = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(png));
        }
        catch { /* preview is best-effort */ }
    }

    /// <summary>Downscaled composite encoded as PNG, embedded in .sable saves for fast previews.</summary>
    private byte[]? MakeSavePreviewPng()
    {
        try
        {
            if (Canvas.Document is not { } doc || Canvas.ReadComposite() is not { } px) return null;
            const int maxSide = 256;
            int w = doc.Width, h = doc.Height;
            double s = System.Math.Min(1.0, (double)maxSide / System.Math.Max(w, h));
            int tw = System.Math.Max(1, (int)(w * s)), th = System.Math.Max(1, (int)(h * s));
            var thumb = new byte[tw * th * 4];
            for (int y = 0; y < th; y++)
            {
                int sy = System.Math.Min(h - 1, (int)((y + 0.5) / s));
                for (int x = 0; x < tw; x++)
                {
                    int sx = System.Math.Min(w - 1, (int)((x + 0.5) / s));
                    System.Array.Copy(px, (sy * w + sx) * 4, thumb, (y * tw + x) * 4, 4);
                }
            }
            return Sable.Imaging.ImageCodec.EncodePngBytes(tw, th, thumb);
        }
        catch { return null; }
    }

    /// <summary>Open a .sable or image file as a new tab (recent menu, file associations, drag-drop).</summary>
    private DocumentTab? OpenPath(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            DocumentTab tab;
            if (path.EndsWith(".sable", System.StringComparison.OrdinalIgnoreCase))
            {
                tab = OpenInNewTab(SableFile.Load(path), path, System.IO.Path.GetFileName(path));
                tab.IsDirty = false;
            }
            else if (path.EndsWith(".psd", System.StringComparison.OrdinalIgnoreCase))
                tab = OpenPsdTab(path);
            else tab = OpenInNewTab(DocumentIO.OpenImage(path), null, System.IO.Path.GetFileName(path), path);
            NoteRecent(path);
            return tab;
        }
        catch { return null; }
    }

    /// <summary>Import a .psd as a new (untitled) tab. Missing fonts and lossy-mapping
    /// notes are shown as Affinity-style notification toasts, not a modal.</summary>
    private DocumentTab OpenPsdTab(string path)
    {
        var doc = PsdReader.Load(path, out var warnings, out var fonts);
        var tab = OpenInNewTab(doc, null, System.IO.Path.GetFileName(path), path);

        var missing = fonts.Where(f => !FontInstalled(f)).ToList();
        if (missing.Count > 0)
            ShowToast(Loc.T("toast.missingFontsTitle"), string.Join("\n", missing));

        if (warnings.Count > 0)
        {
            var list = string.Join("\n", warnings.Take(12));
            if (warnings.Count > 12) list += "\n" + Loc.T("psd.moreWarnings", warnings.Count - 12);
            ShowToast(Loc.T("psd.importTitle"), list);
        }
        return tab;
    }

    /// <summary>Loose match of a PSD PostScript font name ("OpenSans-Bold") against installed
    /// family names ("Open Sans") — alphanumeric-normalised family must prefix the PS name.</summary>
    private static bool FontInstalled(string psName)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        var n = Norm(psName).ToLowerInvariant();
        if (n.Length == 0) return true;   // unparseable → don't cry wolf
        foreach (var fam in Sable.Imaging.TextRaster.Families())
        {
            var nf = Norm(fam).ToLowerInvariant();
            if (nf.Length >= 3 && n.StartsWith(nf)) return true;
        }
        return false;
    }

    private void OpenLaunchArgs()
    {
        foreach (var a in _launchArgs)
            if (!string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')) OpenPath(a);
    }

    private static string GpuName => Loc.T("mainWindow.gpuName");

    private async void OnPreferences(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, GpuName, ModelReg);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (ok)
        {
            ApplyTheme(_settings.Theme);
            ApplyOverlayColors();
            ApplyPanelVisibility();
            UpdateEmptyState();     // welcome-screen toggle may have changed
            RebuildKeyGestures();   // pick up any rebound hotkeys
            foreach (var tab in _tabs) tab.Vm.Undo.Capacity = _settings.UndoLimit;   // apply undo limit live
        }
        // AI enable + model installs (+ GPU runtime + ComfyUI sources) happen LIVE inside the ML panel. Drop
        // any built backend so the next AI op rebuilds it — picking up a newly installed model / source.
        _aiService = null;
        PersistModelSources();   // sources were edited (live) in the ML panel's registry → persist them
        ApplyAiVisibility();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow(GpuName).ShowDialog(this);

    // --- AI (Phase 8 §8.0): menu present, pre-flight readiness explains why an op is unavailable.
    // The actual ops (segment/matte/upscale/inpaint) + their ONNX backend land in slices 8.1+.
    private Sable.Ai.AiService? _aiService;
    // Accessing Ai builds the ONNX backend, which loads the ORT native (a process singleton). On Linux
    // the GPU runtime must be activated BEFORE that first load, so we defer Ai until an actual AI op —
    // visibility + VRAM + the Settings/Models dialogs use the backend-free ModelReg/GpuProbe instead.
    private Sable.Ai.AiService Ai => _aiService ??= CreateAi();

    // One shared GPU probe: the first query shells out to nvidia-smi (slow); caching it here means
    // the model manager / Smart Select reuse the warm result instead of each spawning nvidia-smi anew.
    private readonly Sable.Ai.Gpu.GpuProbe _gpuProbe = new();

    private Sable.Ai.Models.ModelRegistry? _modelReg;
    /// <summary>The model registry, independent of the (ORT-loading) AI backend — safe to touch any time.</summary>
    private Sable.Ai.Models.ModelRegistry ModelReg
    {
        get
        {
            if (_modelReg is null)
            {
                _modelReg = new Sable.Ai.Models.ModelRegistry(_settings.EffectiveModelsFolder());
                // fast native-only load so the UI thread never blocks on a big/remote ComfyUI tree;
                // the external sources are scanned on a background thread, then AI visibility re-applies.
                try { _modelReg.SetSources(_settings.ModelSources, scan: false); } catch { /* no models yet */ }
                if (_settings.ModelSources.Count > 0) _ = ScanExternalSourcesAsync(_modelReg);
            }
            return _modelReg;
        }
    }

    /// <summary>Scan external (ComfyUI) sources off the UI thread, then refresh AI-menu visibility.</summary>
    private async System.Threading.Tasks.Task ScanExternalSourcesAsync(Sable.Ai.Models.ModelRegistry reg)
    {
        try { await System.Threading.Tasks.Task.Run(() => reg.Load()); }
        catch { return; }
        Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAiVisibility);
    }

    private Sable.Ai.AiService CreateAi()
    {
        var svc = new Sable.Ai.AiService(ModelReg);
        try { svc.AddBackend(new Sable.Ai.Backends.OnnxBackend()); } catch { /* ORT/EP unavailable → readiness reports NoGpu */ }
        return svc;
    }

    private async void RunAiTask(Sable.Core.Ai.AiTaskKind task, string title)
    {
        var r = Ai.CheckReadiness(task);
        if (!r.CanRun) { await ConfirmWindow.Ask(this, title, r.Message); return; }
        // model + GPU ready but no backend wired yet in 8.0
        await ConfirmWindow.Ask(this, title, Loc.T("ai.modelReady", title));
    }

    /// <summary>Show the AI menu only when enabled, and each feature only when its model is installed
    /// (so the user never sees an action they can't actually run).</summary>
    private void ApplyAiVisibility()
    {
        AiMenu.IsVisible = _settings.AiEnabled;
        if (!_settings.AiEnabled) return;
        bool Has(Sable.Core.Ai.AiTaskKind t) => ModelReg.DefaultFor(t) is not null;   // registry only, no ORT load
        AiRemoveBgItem.IsVisible = Has(Sable.Core.Ai.AiTaskKind.Matte);
        AiSelectItem.IsVisible = Has(Sable.Core.Ai.AiTaskKind.Segment);
        AiSmartSelectItem.IsVisible = Has(Sable.Core.Ai.AiTaskKind.Segment);
        AiUpscaleItem.IsVisible = Has(Sable.Core.Ai.AiTaskKind.Upscale);
        // Inpaint splits by tier: LaMa (light) = Remove Object; a generative checkpoint = Generative Fill.
        bool HasTier(Sable.Core.Ai.AiTaskKind t, Sable.Core.Ai.AiTier tier) =>
            ModelReg.Catalog.ForTask(t).Any(m => m.Tier == tier);
        AiRemoveObjItem.IsVisible = HasTier(Sable.Core.Ai.AiTaskKind.Inpaint, Sable.Core.Ai.AiTier.Light);
        // Generative Fill / Generate Image need the opt-in tier ON + a matching configured preset.
        bool genOn = _settings.GenerativeAiEnabled;
        AiGenFillItem.IsVisible = genOn && _settings.GenerativePresets.Any(p => !p.IsTextToImage);
        AiGenImageItem.IsVisible = genOn && _settings.GenerativePresets.Any(p => p.IsTextToImage);
        AiGenSep.IsVisible = AiGenFillItem.IsVisible || AiGenImageItem.IsVisible;

        // All AI ops act on the active document — disable them when nothing is open (only Models makes
        // sense with no doc). Generate Image (text-to-image) creates its own canvas, so it stays enabled.
        bool hasDoc = _activeTab is not null;
        AiRemoveBgItem.IsEnabled = hasDoc;
        AiSelectItem.IsEnabled = hasDoc;
        AiSmartSelectItem.IsEnabled = hasDoc;
        AiUpscaleItem.IsEnabled = hasDoc;
        AiRemoveObjItem.IsEnabled = hasDoc;
        AiGenFillItem.IsEnabled = hasDoc;
    }

    private async void OnAiRemoveBackground(object? sender, RoutedEventArgs e)
    {
        string title = Loc.T("mainWindow.removeBackground");
        if (Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectPixelLayer")); return; }

        var r = Ai.CheckReadiness(Sable.Core.Ai.AiTaskKind.Matte);
        if (!r.CanRun) { await ConfirmWindow.Ask(this, title, r.Message); return; }

        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Loc.T("ai.removingBackground"), cts);
        System.Exception? error = null;
        try
        {
            var cmd = await Ai.RemoveBackgroundAsync(px, cts.Token);
            Doc!.Undo.Execute(cmd);
            Doc.SelectedLayer?.RefreshThumbnail();
        }
        catch (System.OperationCanceledException) { /* user cancelled */ }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", error.Message));
    }
    private async void OnAiSelectSubject(object? sender, RoutedEventArgs e)
    {
        string title = Loc.T("mainWindow.selectSubject");
        if (_activeTab?.Doc is not { } doc || Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectPixelLayer")); return; }

        var r = Ai.CheckReadiness(Sable.Core.Ai.AiTaskKind.Segment);
        if (!r.CanRun) { await ConfirmWindow.Ask(this, title, r.Message); return; }

        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Loc.T("ai.selectingSubject"), cts);
        System.Exception? error = null;
        try
        {
            var mask = await Ai.SelectSubjectAsync(px, prompts: null, cts.Token);   // one-click = centre point
            // SAM2 produces a soft probability mask; binarise so the selection is hard-edged
            // and copy/paste preserves the layer's original alpha.
            var cov = mask.Coverage;
            for (int i = 0; i < cov.Length; i++)
                cov[i] = cov[i] >= 128 ? (byte)255 : (byte)0;
            doc.SetMaskSelection(cov);
        }
        catch (System.OperationCanceledException) { /* user cancelled */ }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", error.Message));
    }
    // AI menu entry just activates the tool; OnToolChanged runs the precompute.
    private void OnAiSmartSelect(object? sender, RoutedEventArgs e) => Canvas.ActiveTool = Sable.Tools.ToolKind.SmartSelect;

    private bool _smartBusy;   // a SAM2 analysis is in flight — block re-entry (concurrent DML Run can crash)

    /// <summary>True when a pixel layer is fully transparent (no alpha) — nothing for SAM2 to segment.</summary>
    private static bool IsLayerEmpty(Sable.Engine.Layers.PixelLayer px) => IsBufferEmpty(px.Pixels);

    /// <summary>True when an RGBA8 buffer is fully transparent (every alpha byte 0).</summary>
    private static bool IsBufferEmpty(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 0) return false;
        return true;
    }

    /// <summary>Precompute the active layer's objects (SAM2 AMG, 32×32) for hover-to-select.</summary>
    private async System.Threading.Tasks.Task StartSmartSelect()
    {
        string title = Loc.T("ai.smartSelectTitle");
        if (_smartBusy) return;   // one SAM2 run at a time — a second concurrent DML Run can hard-crash (AV)
        if (Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectPixelLayer")); return; }
        // a blank/transparent layer (e.g. a just-added layer) has nothing to segment — and feeding an empty
        // image to the SAM2 DML decoder hard-crashes the process (ScatterND "parameter incorrect"). Bail.
        if (IsLayerEmpty(px)) { Canvas.SetSmartObjects(null); _smartLayer = px; return; }

        _smartBusy = true;
        using var cts = new System.Threading.CancellationTokenSource();
        try
        {
            // show the dialog FIRST: the first AI op builds the ONNX backend (loads the ORT native, seconds),
            // which would otherwise freeze the UI thread before any feedback and look like a crash. Build it
            // + check readiness off the UI thread while the busy dialog is already up.
            var busy = BusyWindow.Begin(this, Loc.T("ai.preparingAi"), cts);
            Sable.Ai.AiReadiness r;
            try { r = await System.Threading.Tasks.Task.Run(() => Ai.CheckReadiness(Sable.Core.Ai.AiTaskKind.Segment), cts.Token); }
            catch (System.OperationCanceledException) { busy.Done(); return; }
            catch (System.Exception ex) { busy.Done(); await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", ex.Message)); return; }
            if (!r.CanRun) { busy.Done(); await ConfirmWindow.Ask(this, title, r.Message); return; }

            // SAM2 density is configurable (Settings ▸ Machine Learning); Auto scales by detected VRAM so a
            // weak/low-VRAM laptop GPU doesn't hit a driver timeout (TDR) running 1024 decoder passes.
            int grid = Sable.Core.Settings.SableSettings.SmartSelectGrid(
                _settings.SmartSelectQuality, _gpuProbe.TotalVramBytes());
            bool forceCpu = _settings.SmartSelectForceCpu;   // this GPU previously couldn't run SAM2 → CPU only

            // a layer with a non-translation transform (scale/rotate/shear/perspective) can't be mapped back
            // by offset alone, so render it THROUGH its transform into doc space and segment THAT — objects
            // then come back in doc space (overlay/selection line up). Offset-only/content-sized layers stay
            // on the raw buffer (better mask resolution) and map via the rect.
            var samTarget = px;
            bool docSpace = false;
            var docM = Canvas.Document;
            if (docM is not null && HasNonTranslationTransform(px))
            {
                var rendered = Canvas.RenderLayersToPixels(new System.Collections.Generic.List<Sable.Engine.Layers.Layer> { px });
                if (rendered is null || IsBufferEmpty(rendered))
                { busy.Done(); Canvas.SetSmartObjects(null); _smartLayer = px; return; }   // off-canvas / blank → nothing
                samTarget = new Sable.Engine.Layers.PixelLayer(docM.Width, docM.Height, px.Name);
                samTarget.SetBuffer(docM.Width, docM.Height, rendered);
                docSpace = true;
            }

            busy.SetMessage(forceCpu ? Loc.T("ai.analysingObjectsCpu") : Loc.T("ai.analysingObjects"));
            System.Collections.Generic.IReadOnlyList<Sable.Core.Ai.ObjectMask>? objs = null;
            System.Exception? error = null;
            bool fellBack = false;
            try
            {
                objs = await Ai.SegmentEverythingAsync(samTarget, grid, busy.Progress, cts.Token, forceCpu,
                    onCpuFallback: () => fellBack = true);
            }
            catch (System.OperationCanceledException) { busy.Done(); return; }
            catch (System.Exception ex) { error = ex; }
            finally { busy.Done(); }
            if (error is not null) { await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", error.Message)); return; }

            if (fellBack)   // GPU couldn't run SAM2 (TDR) → remember to use CPU next time, tell the user once
            {
                _settings.SmartSelectForceCpu = true;
                Sable.Core.Settings.SettingsService.Save(_settings);
                await ConfirmWindow.Ask(this, title, Loc.T("ai.cpuFallback"));
            }

            if (docSpace) Canvas.SetSmartObjects(objs);   // transform baked into doc space → identity overlay
            else Canvas.SetSmartObjects(objs, px.OffsetX, px.OffsetY, px.Width, px.Height);   // raw buffer → offset rect
            _smartLayer = px;
            if (objs is not null) _smartCache[px] = (objs, ContentKey(px), docSpace);   // cache for instant reload while unchanged
            if (objs is { Count: 0 }) await ConfirmWindow.Ask(this, title, Loc.T("ai.noObjectsFound"));
        }
        finally { _smartBusy = false; }
    }

    private async void OnAiUpscale(object? sender, RoutedEventArgs e)
    {
        string title = Loc.T("ai.upscaleTitle");
        if (_activeTab?.Doc is not { } doc || Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectPixelLayer")); return; }

        var r = Ai.CheckReadiness(Sable.Core.Ai.AiTaskKind.Upscale);
        if (!r.CanRun) { await ConfirmWindow.Ask(this, title, r.Message); return; }

        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Loc.T("ai.upscaling"), cts);
        System.Exception? error = null;
        try
        {
            var cmd = await Ai.UpscaleAsync(doc, px, busy.Progress, cts.Token);
            Doc!.Undo.Execute(cmd);
        }
        catch (System.OperationCanceledException) { /* user cancelled */ }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", error.Message));
    }
    private async void OnAiRemoveObject(object? sender, RoutedEventArgs e)
    {
        string title = Loc.T("mainWindow.removeObject");
        if (_activeTab?.Doc is not { } doc || Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectPixelLayer")); return; }
        // SnapshotSelectionMask rasterizes a plain rect selection → mask, so a rectangular marquee
        // (which only sets doc.Selection, not SelectionMask) also works here.
        var selMask = doc.SnapshotSelectionMask();
        if (selMask is null)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.selectObjectFirst")); return; }
        if (px.Width != doc.Width || px.Height != doc.Height)
        { await ConfirmWindow.Ask(this, title, Loc.T("ai.fullCanvasOnly")); return; }

        var r = Ai.CheckReadiness(Sable.Core.Ai.AiTaskKind.Inpaint);
        if (!r.CanRun) { await ConfirmWindow.Ask(this, title, r.Message); return; }

        var mask = new Sable.Core.Ai.AiMask(selMask, doc.Width, doc.Height);
        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Loc.T("ai.removingObject"), cts);
        byte[]? result = null; System.Exception? error = null;
        try { result = await Ai.RemoveObjectAsync(px, mask, cts.Token); }
        catch (System.OperationCanceledException) { busy.Done(); return; }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) { await ConfirmWindow.Ask(this, title, Loc.T("ai.failed", error.Message)); return; }

        var before = Sable.Tools.RasterState.Capture(px);
        px.SetBuffer(px.Width, px.Height, result!);
        px.Dirty = true;
        var cmd = new Sable.Tools.RasterStateCommand(px, before, Sable.Tools.RasterState.Capture(px), () => px.Dirty = true);
        Doc!.Undo.Execute(cmd);
        Doc.SelectedLayer?.RefreshThumbnail();
        doc.ClearSelection();
    }
    private Sable.Ai.Comfy.ComfyBackend? _comfy;
    private GenerativePanel? _genPanel;
    private Sable.Core.Ai.GenerativePreset? _curPreset;   // the preset for the in-flight gen run (read by the Comfy resolver)

    /// <summary>Open the modeless Generative Fill panel (selection-driven). Generate → <see cref="OnGenerateFill"/>.</summary>
    private void OnAiGenerativeFill(object? sender, RoutedEventArgs e) => OpenGenPanel(textToImage: false);

    /// <summary>Open the Generate Image panel (text-to-image, no selection → new document).</summary>
    private void OnAiGenerateImage(object? sender, RoutedEventArgs e) => OpenGenPanel(textToImage: true);

    private void OpenGenPanel(bool textToImage)
    {
        if (_genPanel is not null && _genPanel.TextToImage != textToImage) _genPanel.Close();   // wrong mode → reopen
        if (_genPanel is null)
        {
            _genPanel = new GenerativePanel(ModelReg, _settings.GenerativePresets, textToImage) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            _genPanel.GenerateRequested += req => _ = OnGenerateFill(req);
            _genPanel.Closed += (_, _) => { _genPanel = null; if (_comfy is not null) _ = _comfy.FreeMemoryAsync(); };   // free comfy VRAM when done
        }
        _genPanel.Show(this);
        _genPanel.Activate();
    }

    /// <summary>
    /// Start the chosen generative backend (ComfyUI or Diffusers) and wire it into <see cref="AiService"/>.
    /// Provisioning is done up-front in Preferences (no install on first use); this only resolves + starts an
    /// already-installed runtime. Cheap no-op once running.
    /// </summary>
    private System.Threading.Tasks.Task<(bool Ok, string Message)> EnsureGenerativeAsync(System.Threading.CancellationToken ct)
    {
        if (!_settings.GenerativeAiEnabled)
            return System.Threading.Tasks.Task.FromResult((false, Sable.App.Localization.Loc.T("generative.sidecarOff")));
        return EnsureComfyAsync(ct);   // ComfyUI is the only generative backend
    }

    private async System.Threading.Tasks.Task<(bool Ok, string Message)> EnsureComfyAsync(System.Threading.CancellationToken ct)
    {
        if (_comfy is { IsAvailable: true }) { Ai.Generative = _comfy; return (true, ""); }

        var host = Sable.Ai.Sidecar.Provisioning.ComfyEnvLocator.CurrentHost();
        var comfyPath = ModelReg.Sources.FirstOrDefault(s => s.Kind == Sable.Core.Ai.ModelSourceKind.ComfyUI)?.Path ?? "";
        var install = comfyPath.Length > 0 ? Sable.Ai.Comfy.Provisioning.ComfyLocator.LocateSameOs(comfyPath, host) : null;
        if (install is null)
        {
            var prov = new Sable.Ai.Comfy.Provisioning.ComfyProvisioner();
            if (prov.IsProvisioned) install = new Sable.Ai.Comfy.Provisioning.ComfyInstall(prov.ComfyDir, prov.PythonExe);
        }
        if (install is null) return (false, Sable.App.Localization.Loc.T("generative.runtimeNotInstalled"));

        _comfy ??= new Sable.Ai.Comfy.ComfyBackend();
        WireComfy(_comfy);
        bool started = await _comfy.StartAsync(install.PythonExe, install.ComfyDir, ct: ct);
        if (!started)
        {
            var tail = _comfy.LastOutputTail;
            var lastLines = string.IsNullOrWhiteSpace(tail) ? "" :
                "\n" + string.Join("\n", tail.Split('\n').TakeLast(8));
            return (false, Loc.T("generative.sidecarFailedStart")
                + lastLines + Loc.T("generative.logSuffix", Sable.Ai.Comfy.ComfyBackend.LogPath));
        }
        Ai.Generative = _comfy;
        return (true, Sable.App.Localization.Loc.T("generative.usingEnv", "ComfyUI"));
    }


    /// <summary>Wire the ComfyUI backend's App-side glue: manifest→graph model ref, LoRA names, PNG codec.</summary>
    private void WireComfy(Sable.Ai.Comfy.ComfyBackend comfy)
    {
        comfy.ResolveModel = id => BuildComfyModelRef(ModelReg.Catalog.ById(id),
            _curPreset?.EncoderIds ?? (System.Collections.Generic.IReadOnlyList<string>)System.Array.Empty<string>(), _curPreset?.VaeId, ModelReg);
        comfy.LoraName = id =>
        {
            var f = ModelReg.Catalog.ById(id)?.Files?.FirstOrDefault();
            return f is null ? "" : Sable.Ai.Comfy.Workflow.WorkflowBuilder.ComfyName(f);
        };
        comfy.DecodePng = png =>
        {
            var d = Sable.Imaging.ImageCodec.DecodeRgbaBytes(png)
                ?? throw new System.InvalidOperationException("ComfyUI returned an undecodable image.");
            return (d.rgba, d.width, d.height);
        };
        comfy.EncodePng = (rgba, w, h) => Sable.Imaging.ImageCodec.EncodePngBytes(w, h, rgba);
    }

    /// <summary>Map a model manifest to how ComfyUI loads it (checkpoint vs standalone transformer + the
    /// user-picked encoder/VAE for the assembled case).</summary>
    private static Sable.Ai.Comfy.Workflow.ComfyModelRef? BuildComfyModelRef(
        Sable.Core.Ai.ModelManifest? m, System.Collections.Generic.IReadOnlyList<string> encoderIds, string? vaeId,
        Sable.Ai.Models.ModelRegistry reg)
    {
        var file = m?.Files?.FirstOrDefault();
        if (m is null || file is null) return null;
        if (Sable.Ai.Comfy.Workflow.WorkflowBuilder.KindForPath(file) == Sable.Ai.Comfy.Workflow.ComfyModelKind.Checkpoint)
            return new Sable.Ai.Comfy.Workflow.ComfyModelRef(m.Family, Sable.Ai.Comfy.Workflow.ComfyModelKind.Checkpoint, Sable.Ai.Comfy.Workflow.WorkflowBuilder.ComfyName(file));

        // assembled: resolve the picked encoder/VAE component ids → the names ComfyUI lists (relative to the type folder)
        string? NameOf(string? id) => id is null ? null : reg.Catalog.ById(id)?.Files?.FirstOrDefault() is { } f ? Sable.Ai.Comfy.Workflow.WorkflowBuilder.ComfyName(f) : null;
        var clipNames = encoderIds.Select(NameOf).Where(n => n is not null).Select(n => n!).ToList();
        return new Sable.Ai.Comfy.Workflow.ComfyModelRef(m.Family, Sable.Ai.Comfy.Workflow.ComfyModelKind.Unet,
            Sable.Ai.Comfy.Workflow.WorkflowBuilder.ComfyName(file), ClipNames: clipNames, VaeName: NameOf(vaeId));
    }

    /// <summary>Run a Generative Fill from the panel: validate layer + selection, start the sidecar, generate,
    /// deposit the result as an undoable layer (§4.2).</summary>
    private async System.Threading.Tasks.Task OnGenerateFill(GenFillRequest req)
    {
        if (req.Preset.IsTextToImage) { await OnGenerateTxt2Img(req); return; }   // no selection — new document

        if (_activeTab?.Doc is not { } doc || Doc?.SelectedLayer?.Model is not Sable.Engine.Layers.PixelLayer px)
        { _genPanel?.SetStatus(Sable.App.Localization.Loc.T("generative.selectPixelLayer")); return; }

        var docSel = doc.SnapshotSelectionMask();
        if (docSel is null) { _genPanel?.SetStatus(Sable.App.Localization.Loc.T("generative.makeSelection")); return; }
        var region = LayerMaskFromDocSelection(px, docSel, doc.Width, doc.Height);

        _curPreset = req.Preset;   // read by the Comfy ResolveModel closure when building the graph

        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Sable.App.Localization.Loc.T("generative.startingBackend"), cts);
        System.Exception? error = null;
        try
        {
            var (ok, msg) = await EnsureGenerativeAsync(cts.Token);
            if (!ok) { busy.Done(); _genPanel?.SetStatus(msg); return; }
            _genPanel?.SetStatus(msg);
            busy.SetMessage(Sable.App.Localization.Loc.T("generative.fillBusy"));
            if (_comfy is not null)
            {
                _comfy.Progress = busy.Progress;          // ComfyUI WS step progress → the bar
                _comfy.Denoise = req.Denoise;
                _comfy.WorkflowJsonPath = req.Preset.WorkflowFile;   // run the user's exported workflow
            }

            var spec = new Sable.Core.Ai.GenRequest(req.Preset.BaseModelId, Sable.Core.Ai.AiTaskKind.Inpaint, req.Prompt,
                req.Negative, req.Steps, req.Cfg, req.Seed, Loras: req.Loras, Offload: req.Offload);
            var cmd = await Ai.GenerativeFillAsync(doc, px, region, spec, busy.Progress, cts.Token);
            Doc!.Undo.Execute(cmd);
        }
        catch (System.OperationCanceledException) { /* cancelled */ }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) _genPanel?.SetStatus(Sable.App.Localization.Loc.T("generative.failed", error.Message));
    }

    /// <summary>Text-to-image: run the preset's workflow with no input image, open the result as a new document.</summary>
    private async System.Threading.Tasks.Task OnGenerateTxt2Img(GenFillRequest req)
    {
        _curPreset = req.Preset;
        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Sable.App.Localization.Loc.T("generative.startingBackend"), cts);
        System.Exception? error = null;
        try
        {
            var (ok, msg) = await EnsureGenerativeAsync(cts.Token);
            if (!ok) { busy.Done(); _genPanel?.SetStatus(msg); return; }
            _genPanel?.SetStatus(msg);
            busy.SetMessage(Sable.App.Localization.Loc.T("generative.fillBusy"));
            if (_comfy is not null)
            {
                _comfy.Progress = busy.Progress;
                _comfy.Denoise = req.Denoise;
                _comfy.WorkflowJsonPath = req.Preset.WorkflowFile;
            }
            var spec = new Sable.Core.Ai.GenRequest(req.Preset.BaseModelId, Sable.Core.Ai.AiTaskKind.Txt2Img, req.Prompt,
                req.Negative, req.Steps, req.Cfg, req.Seed, Loras: req.Loras, Offload: req.Offload);
            var img = await Ai.GenerateImageAsync(spec, cts.Token);

            var doc = new Sable.Engine.Document(img.Width, img.Height);
            var layer = new Sable.Engine.Layers.PixelLayer(img.Width, img.Height, Loc.T("mainWindow.layerGenerated"));
            layer.SetBuffer(img.Width, img.Height, img.Rgba);
            doc.Layers.Add(layer);
            OpenInNewTab(doc, null, Loc.T("mainWindow.layerGenerated"));
        }
        catch (System.OperationCanceledException) { /* cancelled */ }
        catch (System.Exception ex) { error = ex; }
        finally { busy.Done(); }
        if (error is not null) _genPanel?.SetStatus(Sable.App.Localization.Loc.T("generative.failed", error.Message));
    }

    /// <summary>Sample a doc-sized selection coverage into a layer-aligned <see cref="Sable.Core.Ai.AiMask"/>
    /// (offset-aware), so the fill region lines up with the target layer's buffer.</summary>
    private static Sable.Core.Ai.AiMask LayerMaskFromDocSelection(Sable.Engine.Layers.PixelLayer px, byte[] docSel, int docW, int docH)
    {
        var cov = new byte[px.Width * px.Height];
        for (int y = 0; y < px.Height; y++)
        {
            int dy = y + px.OffsetY;
            if (dy < 0 || dy >= docH) continue;
            for (int x = 0; x < px.Width; x++)
            {
                int dx = x + px.OffsetX;
                if (dx < 0 || dx >= docW) continue;
                cov[y * px.Width + x] = docSel[dy * docW + dx];
            }
        }
        return new Sable.Core.Ai.AiMask(cov, px.Width, px.Height);
    }

    private async void OnAiModels(object? sender, RoutedEventArgs e)
    {
        var win = new ModelsWindow(ModelReg, _gpuProbe) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.DefaultsChanged += ApplyAiVisibility;   // default/install changes alter which model serves each op
        win.PresetsChanged += () => { Sable.Core.Settings.SettingsService.Save(_settings); _genPanel?.Close(); };
        win.OpenComfyRequested += () => _ = OpenBundledComfyAsync();
        win.ModelsFolderChanged += newPath =>      // registry already moved; persist the choice + rebuild the AI backend
        {
            _settings.ModelsFolder = newPath;
            Sable.Core.Settings.SettingsService.Save(_settings);
            _aiService = null;   // next AI op rebuilds the backend against the moved registry
        };
        win.InitGenerative(_settings);   // build the Generative preset tab
        await win.ShowDialog(this);
        ApplyAiVisibility();   // installs/removals in the manager change which features are available
    }

    /// <summary>Start Sable's bundled ComfyUI (if needed) and open its web UI in the browser — so the user
    /// can load a workflow and "Save (API Format)" for a preset (PHASE8_AI_COMFY).</summary>
    private async System.Threading.Tasks.Task OpenBundledComfyAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        var busy = BusyWindow.Begin(this, Sable.App.Localization.Loc.T("generative.startingBackend"), cts);
        (bool Ok, string Message) r;
        try { r = await EnsureComfyAsync(cts.Token); } catch (System.Exception ex) { busy.Done(); await ConfirmWindow.Ask(this, "ComfyUI", ex.Message); return; }
        busy.Done();
        if (!r.Ok || _comfy?.BaseUri is not { } uri) { await ConfirmWindow.Ask(this, "ComfyUI", r.Message); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (System.Exception ex) { await ConfirmWindow.Ask(this, "ComfyUI", Loc.T("mainWindow.openInBrowser", uri, ex.Message)); }
    }

    /// <summary>Save the registry's external (non-native) sources to settings (§2.1).</summary>
    private void PersistModelSources()
    {
        _settings.ModelSources = ModelReg.Sources
            .Where(s => s.Kind != Sable.Core.Ai.ModelSourceKind.Native)
            .ToList();
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    // --- custom title bar (menu-in-header, client-side decorations) ---
    private void OnMinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestoreWindow(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    // Caption buttons (min/max/close) act on PointerReleased, not Click: in the custom BorderOnly title
    // bar the Avalonia Click gesture is unreliable for the top row (press+release reach the button but
    // Click only fires when the window later deactivates — see diag.log). PointerPressed/Released are
    // 100% reliable, so we run the action on release over the button. Tunnel on the strip so we act
    // before (and instead of) the button's own gesture handling.
    private void WireCaptionButtons()
        => CaptionButtons?.AddHandler(PointerReleasedEvent, OnCaptionReleased, RoutingStrategies.Tunnel);

    private void OnCaptionReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() is not { Tag: string tag }) return;
        e.Handled = true;
        switch (tag)
        {
            case "min": OnMinimizeWindow(this, e); break;
            case "max": OnMaxRestoreWindow(this, e); break;
            case "close": OnCloseWindow(this, e); break;
        }
    }

    private void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void OnTitleBarDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // --- Select menu (PLAN §3 / §16.2) ---
    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is { } d) d.SetMaskSelection(Sable.Engine.Selections.Full(d.Width, d.Height));
    }
    private void OnDeselect(object? sender, RoutedEventArgs e) => Canvas.Deselect();
    private void OnInvertSelection(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } d) return;
        var m = d.SnapshotSelectionMask() ?? Sable.Engine.Selections.Full(d.Width, d.Height);
        d.SetMaskSelection(Sable.Engine.Selections.Invert(m));
    }
    // each op remembers its last radius for the session (PS-style amount prompt)
    private static int _lastGrowPx = 4, _lastShrinkPx = 4, _lastSmoothPx = 4, _lastBorderPx = 4, _lastFeatherPx = 4;

    private async void OnGrowSelection(object? sender, RoutedEventArgs e)
    {
        if (await PromptSelectionAmount("mainWindow.growSel", _lastGrowPx) is not { } px) return;
        _lastGrowPx = px;
        MorphSelection((m, w, h) => Sable.Engine.Selections.Grow(m, w, h, px));
    }
    private async void OnShrinkSelection(object? sender, RoutedEventArgs e)
    {
        if (await PromptSelectionAmount("mainWindow.shrinkSel", _lastShrinkPx) is not { } px) return;
        _lastShrinkPx = px;
        MorphSelection((m, w, h) => Sable.Engine.Selections.Shrink(m, w, h, px));
    }
    private async void OnSmoothSelection(object? sender, RoutedEventArgs e)
    {
        if (await PromptSelectionAmount("mainWindow.smoothSel", _lastSmoothPx) is not { } px) return;
        _lastSmoothPx = px;
        MorphSelection((m, w, h) => Sable.Engine.Selections.Smooth(m, w, h, px));
    }
    private async void OnBorderSelection(object? sender, RoutedEventArgs e)
    {
        if (await PromptSelectionAmount("mainWindow.borderSel", _lastBorderPx) is not { } px) return;
        _lastBorderPx = px;
        MorphSelection((m, w, h) => Sable.Engine.Selections.Border(m, w, h, px));
    }
    private async void OnFeatherSelection(object? sender, RoutedEventArgs e)
    {
        if (await PromptSelectionAmount("mainWindow.featherSel", _lastFeatherPx) is not { } px) return;
        _lastFeatherPx = px;
        MorphSelection((m, w, h) => Sable.Engine.Selections.Feather(m, w, h, px));
    }

    /// <summary>Amount prompt for the Select morphology ops; null when there is no selection or on cancel.</summary>
    private async System.Threading.Tasks.Task<int?> PromptSelectionAmount(string titleKey, int initial)
    {
        if (Canvas.Document is not { } d || (d.Selection is null && d.SelectionMask is null)) return null;
        var title = Loc.T(titleKey).TrimEnd('…', '.', ' ');
        return await AmountDialog.Ask(this, title, initial, 1, 250);
    }

    private void MorphSelection(System.Func<byte[], int, int, byte[]> op)
    {
        if (Canvas.Document is { } d && d.SnapshotSelectionMask() is { } m)
            d.SetMaskSelection(op(m, d.Width, d.Height));
    }

    private void OnSaveSelection(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is { } d && d.SnapshotSelectionMask() is { } m) d.SavedSelection = m;
    }
    private void OnLoadSelection(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is { } d && d.SavedSelection is { } m) d.SetMaskSelection((byte[])m.Clone());
    }

    // --- autosave + crash recovery (PLAN §2.6) ---
    private Avalonia.Threading.DispatcherTimer? _autosaveTimer;

    private void StartAutosave()
    {
        if (!_settings.AutosaveEnabled) return;
        int mins = System.Math.Clamp(_settings.AutosaveMinutes, 1, 120);
        _autosaveTimer = new Avalonia.Threading.DispatcherTimer
            { Interval = System.TimeSpan.FromMinutes(mins) };
        _autosaveTimer.Tick += (_, _) => AutosaveNow();
        _autosaveTimer.Start();
    }

    private bool _autosaving;

    private void AutosaveNow()
    {
        if (!_settings.AutosaveEnabled || _autosaving) return;
        // Snapshot the dirty docs on the UI thread (a fast buffer memcpy), then run the
        // zip+deflate serialization on a background thread — serializing the live doc on the
        // Tick handler froze the UI for seconds on a large document (audit C10).
        var snap = _tabs.Where(t => t.IsDirty)
            .Select(t => (t.RecoveryId, t.Path, t.Title, Doc: SnapshotDoc(t.Doc)))
            .ToList();
        if (snap.Count == 0) return;
        _autosaving = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { RecoveryService.Save(snap.Select(s => (s.RecoveryId, s.Path, s.Title, s.Doc))); }
            finally { _autosaving = false; }
        });
    }

    /// <summary>Deep-copy a document (layer buffers + params) so it can be serialized off the UI thread.</summary>
    private static Sable.Engine.Document SnapshotDoc(Sable.Engine.Document d)
    {
        var c = new Sable.Engine.Document(d.Width, d.Height);
        foreach (var l in d.Layers) c.Layers.Add(l.Clone());
        c.GuidesX.AddRange(d.GuidesX);
        c.GuidesY.AddRange(d.GuidesY);
        if (d.SavedSelection is { } s) c.SavedSelection = (byte[])s.Clone();
        return c;
    }

    private async void OfferCrashRecovery()
    {
        var pending = RecoveryService.GetPending();
        if (pending.Count == 0) return;
        bool restore = await ConfirmWindow.Ask(this, Loc.T("mainWindow.recoverTitle"),
            Loc.T("mainWindow.recoverBody", pending.Count));
        if (restore)
            foreach (var p in pending)
            {
                try
                {
                    var tab = OpenInNewTab(SableFile.Load(p.RecoveryPath), p.OrigPath, p.Title);
                    tab.IsDirty = true;   // recovered work isn't saved to its real location yet
                }
                catch { /* skip a corrupt recovery file */ }
            }
        RecoveryService.Clear();   // handled either way → don't prompt again
    }

    // --- status bar: zoom UI + document info + cursor position (PLAN §2.5) ---
    private void OnZoomFit(object? sender, RoutedEventArgs e) { Canvas.FitView(false); UpdateZoomLabel(); }
    private void OnZoomActual(object? sender, RoutedEventArgs e) { Canvas.ZoomActualPixels(); UpdateZoomLabel(); }
    private void OnZoomInMenu(object? sender, RoutedEventArgs e) { Canvas.ZoomBy(1.25); UpdateZoomLabel(); }
    private void OnZoomOutMenu(object? sender, RoutedEventArgs e) { Canvas.ZoomBy(0.8); UpdateZoomLabel(); }

    private void OnToggleRulers(object? sender, RoutedEventArgs e)
    {
        bool on = RulersMenuItem.IsChecked;
        CanvasGrid.RowDefinitions[0].Height = new GridLength(on ? 18 : 0);
        CanvasGrid.ColumnDefinitions[0].Width = new GridLength(on ? 18 : 0);
        RulerH.IsVisible = on; RulerV.IsVisible = on;
    }

    private void OnToggleSnap(object? sender, RoutedEventArgs e)
    {
        Canvas.SnapEnabled = SnapMenuItem.IsChecked;
        _settings.SnapEnabled = SnapMenuItem.IsChecked;
        Sable.Core.Settings.SettingsService.Save(_settings);
    }
    private void OnClearGuides(object? sender, RoutedEventArgs e)
    {
        if (_activeTab?.Doc is { } d) { d.GuidesX.Clear(); d.GuidesY.Clear(); }
    }

    private void OnToggleGrid(object? sender, RoutedEventArgs e)
    {
        Canvas.ShowGrid = GridMenuItem.IsChecked;
        _settings.ShowGrid = GridMenuItem.IsChecked;
        Sable.Core.Settings.SettingsService.Save(_settings);
    }

    /// <summary>Push the grid + snapping settings to the canvas and sync the View-menu quick toggles.</summary>
    private void ApplyGridAndSnap()
    {
        Canvas.ShowGrid = _settings.ShowGrid;
        Canvas.GridSpacing = (float)_settings.GridSpacing;
        Canvas.GridSubdivisions = _settings.GridSubdivisions;
        Canvas.SnapEnabled = _settings.SnapEnabled;
        Canvas.SnapTolerance = _settings.SnapTolerance;
        Canvas.SnapToGrid = _settings.SnapToGrid;
        Canvas.SnapToGuides = _settings.SnapToGuides;
        Canvas.SnapToCanvas = _settings.SnapToCanvas;
        Canvas.SnapToObjects = _settings.SnapToObjects;
        Canvas.SnapVisibleOnly = _settings.SnapVisibleOnly;
        if (GridMenuItem is not null) GridMenuItem.IsChecked = _settings.ShowGrid;
        if (SnapMenuItem is not null) SnapMenuItem.IsChecked = _settings.SnapEnabled;
    }

    private void OnGridSettings(object? sender, RoutedEventArgs e)
    {
        var win = new GridSettingsWindow(_settings, () =>
        {
            ApplyGridAndSnap();
            ApplyOverlayColors();   // grid colour lives in the overlay-colour push
            Sable.Core.Settings.SettingsService.Save(_settings);
        }) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.ShowDialog(this);
    }

    private void OnSnapping(object? sender, RoutedEventArgs e)
    {
        var win = new SnappingWindow(_settings, () =>
        {
            ApplyGridAndSnap();
            Sable.Core.Settings.SettingsService.Save(_settings);
        }) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.ShowDialog(this);
    }
    private void OnTogglePixelGrid(object? sender, RoutedEventArgs e) => Canvas.ShowPixelGrid = PixelGridMenuItem.IsChecked;

    private void OnZoomBoxKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var s = (ZoomBox.Text ?? "").Replace("%", "").Trim();
        if (double.TryParse(s, out var pct)) { Canvas.SetZoomPercent(System.Math.Clamp(pct, 5, 6400)); }
        UpdateZoomLabel();
        e.Handled = true;
    }

    private void UpdateZoomLabel()
    {
        if (ZoomBox is null) return;
        ZoomBox.Text = Loc.T("mainWindow.zoomPercent", $"{Canvas.EffectiveScale * 100:0}");
    }

    private void UpdateCursorLabel(double docX, double docY)
    {
        if (CursorLabel is not null) CursorLabel.Text = Loc.T("mainWindow.cursorPx", (int)System.Math.Floor(docX), (int)System.Math.Floor(docY));

        // Info: colour under the cursor (active layer pixel) + selection size
        if (InfoLabel is not null)
        {
            int ix = (int)System.Math.Floor(docX), iy = (int)System.Math.Floor(docY);
            if (Canvas.ActiveLayer is { } al)
            {
                int lx = ix - al.OffsetX, ly = iy - al.OffsetY;
                if (lx >= 0 && ly >= 0 && lx < al.Width && ly < al.Height)
                {
                    int j = (ly * al.Width + lx) * 4;
                    byte r = al.Pixels[j], g = al.Pixels[j + 1], b = al.Pixels[j + 2], a = al.Pixels[j + 3];
                    InfoLabel.Text = $"R{r} G{g} B{b} A{a}";
                    InfoSwatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b));
                }
                else { InfoLabel.Text = ""; InfoSwatch.Background = Avalonia.Media.Brushes.Transparent; }
            }
            else { InfoLabel.Text = ""; InfoSwatch.Background = Avalonia.Media.Brushes.Transparent; }
        }
        if (SelSizeLabel is not null)
            SelSizeLabel.Text = Canvas.Document?.Selection is { } s ? Loc.T("mainWindow.selSize", s.W, s.H) : "";
    }

    private void UpdateDocInfo()
    {
        if (DocInfoLabel is null) return;
        if (_activeTab?.Doc is { } d) DocInfoLabel.Text = Loc.T("mainWindow.docInfo", d.Width, d.Height, $"{d.Dpi:0}");
        else DocInfoLabel.Text = Loc.T("mainWindow.colorSpaceNone");
        // colour mode + working depth of the active document (RGB only for now; 8/16/32-bit via Image ▸ Mode)
        if (ColorSpaceLabel is not null)
            ColorSpaceLabel.Text = _activeTab?.Doc is { } cd ? Loc.T("mainWindow.colorSpace", (int)cd.Depth) : Loc.T("mainWindow.colorSpaceNone");
        SyncDepthMenu();
    }

    /// <summary>Tick the Image ▸ Mode radio matching the active document's bit depth.</summary>
    private void SyncDepthMenu()
    {
        var depth = _activeTab?.Doc.Depth;
        if (Depth8Item is not null) Depth8Item.IsChecked = depth == Sable.Core.BitDepth.Eight;
        if (Depth16Item is not null) Depth16Item.IsChecked = depth == Sable.Core.BitDepth.Sixteen;
        if (Depth32Item is not null) Depth32Item.IsChecked = depth == Sable.Core.BitDepth.ThirtyTwo;
    }

    private void OnSetDepth8(object? sender, RoutedEventArgs e) => SetDepth(Sable.Core.BitDepth.Eight);
    private void OnSetDepth16(object? sender, RoutedEventArgs e) => SetDepth(Sable.Core.BitDepth.Sixteen);
    private void OnSetDepth32(object? sender, RoutedEventArgs e) => SetDepth(Sable.Core.BitDepth.ThirtyTwo);

    private void SetDepth(Sable.Core.BitDepth depth)
    {
        if (_activeTab is not { } tab || tab.Doc.Depth == depth) return;
        tab.Doc.Depth = depth;
        tab.IsDirty = true;       // a document-metadata change to save
        UpdateDocInfo();
    }

    private void OnCheckUpdatesMenu(object? sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(manual: true);

    // Launch + manual update check (PLAN §2.4). On an available update, shows UpdateWindow
    // (download + install + restart). Non-blocking; failures are silent on the launch check.
    private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool manual)
    {
        var service = new Sable.Core.Services.UpdateService();
        try
        {
            var info = await service.CheckForUpdateAsync();
            if (info is null)
            {
                if (manual) await ConfirmWindow.Ask(this, Loc.T("mainWindow.upToDate"), Loc.T("mainWindow.upToDateBody", Sable.Core.VersionInfo.Version));
                return;
            }
            await new UpdateWindow(info, service).ShowDialog(this);
        }
        catch
        {
            if (manual) await ConfirmWindow.Ask(this, Loc.T("mainWindow.updateCheckFailed"), Loc.T("mainWindow.updateCheckFailedBody"));
        }
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
        bool typing = IsTypingInTextField();   // a chrome text field has focus (rename box, numeric option fields)
        if (e.Key == Key.F2)   // rename the selected layer inline
        {
            if (!typing && Doc?.SelectedLayer is { } rv) { rv.BeginRename(); FocusRenameBox(rv); e.Handled = true; }
            return;
        }
        // rebindable command hotkeys (PLAN §17.1): match the keymap before the fixed tool/nav keys. These
        // are all modifier/F-key gestures, so they stay active (e.g. Ctrl+Z) even while a field has focus.
        foreach (var (g, id) in _keyGestures)
        {
            if (g.Matches(e)) { RunKeyCommand(id); e.Handled = true; return; }
        }
        if (typing) return;   // below: bare Delete/Enter/Escape canvas actions — must not steal typed keys
        switch (e.Key)
        {
            case Key.Delete or Key.Back: Canvas.DeleteSelection(); e.Handled = true; break;
            case Key.Enter:
                if (Canvas.QuickMask) Canvas.ToggleQuickMask();       // commit quick mask
                else if (Canvas.PenActive) Canvas.CommitPen();        // finish pen path (open)
                else if (Canvas.MeshActive) Canvas.CommitMeshWarp();  // apply mesh warp
                else if (Canvas.PolyLassoActive) Canvas.CommitPolyLasso();
                else Canvas.CommitCrop();
                e.Handled = true; break;
            case Key.Escape:
                if (_radial is not null) CloseRadialMenu();            // dismiss the radial quick menu first
                else if (Canvas.QuickMask) Canvas.CancelQuickMask();   // cancel quick mask (restore prior selection)
                else if (Canvas.PenActive) Canvas.CancelPen();        // discard pen path
                else if (Canvas.MeshActive) Canvas.CancelMeshWarp();
                else if (Canvas.PolyLassoActive) Canvas.CancelPolyLasso();
                else { Canvas.CancelCrop(); Canvas.Deselect(); }
                e.Handled = true; break;
        }
    }

    private void OnLayerSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Doc?.SetSelection(LayerList.SelectedItems?.Cast<LayerViewModel>() ?? Enumerable.Empty<LayerViewModel>());

    // --- inline layer rename (double-click name / F2 / context menu) ---
    private void OnLayerNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LayerOf(sender) is { } vm) { vm.BeginRename(); FocusRenameBox(vm); e.Handled = true; }
    }

    private void OnRenameLayer(object? sender, RoutedEventArgs e)
    {
        var vm = LayerOf(sender) ?? Doc?.SelectedLayer;
        if (vm is null) return;
        vm.BeginRename();
        FocusRenameBox(vm);
    }

    private void OnLayerNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (LayerOf(sender) is not { } vm) return;
        if (e.Key == Key.Enter) { vm.CommitRename(); LayerList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.CancelRename(); LayerList.Focus(); e.Handled = true; }
    }

    private void OnLayerNameCommit(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        if (LayerOf(sender) is { IsEditing: true } vm) vm.CommitRename();
    }

    /// <summary>Focus + select-all the inline rename TextBox for a row (after it's been made visible).</summary>
    private void FocusRenameBox(LayerViewModel vm)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var box = LayerList.ContainerFromItem(vm)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (box is not null) { box.Focus(); box.SelectAll(); }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>True when a text field (rename box, numeric option boxes, …) has focus — so the global
    /// key handlers don't steal Enter/Esc/Backspace/letter keys while the user is typing.</summary>
    private bool IsTypingInTextField()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

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
        _dragModels = null;
        _pendingCollapse = null;
        if (_dragSource is null || Doc is not { } d) return;
        // Right-press: only fix the selection (select the row when it's OUTSIDE a multi-selection;
        // keep the multi-selection intact when inside). The flyout itself is opened from the
        // ContextRequested handler — handling the raw right-press here suppressed it.
        if (e.GetCurrentPoint(LayerList).Properties.IsRightButtonPressed)
        {
            if (!d.SelectionModels.Contains(_dragSource.Model)) LayerList.SelectedItem = _dragSource;
            return;
        }
        if (!e.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed) return;

        // capture the drag set BEFORE the ListBox mutates the selection on this press: if the
        // grabbed row is part of a live multi-selection, drag the whole selection.
        var sel = d.SelectionModels;
        bool inMulti = sel.Count > 1 && sel.Contains(_dragSource.Model);
        _dragModels = inMulti ? sel.ToList()
            : new System.Collections.Generic.List<Sable.Engine.Layers.Layer> { _dragSource.Model };

        // Delayed-deselect (Explorer/Photoshop): pressing WITHOUT a modifier on a row that is part
        // of a multi-selection makes the ListBox collapse the selection to just that row on press —
        // which kills a drag of the whole selection. Suppress the ListBox's press handling + capture
        // the pointer so the multi-selection survives the press; on release WITHOUT a drag we collapse
        // to the clicked row. Skip when the press is on a child control (eye/chevron/tag) so they work.
        bool plain = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) == 0;
        if (inMulti && plain && !PressOnInteractive(e.Source))
        {
            _pendingCollapse = _dragSource;
            e.Pointer.Capture(LayerList);
            e.Handled = true;
        }
    }

    // true when the press landed on an interactive child of a layer row (visibility eye, disclosure
    // chevron, colour-tag button, …) — those must keep working, so we never suppress their press.
    private static bool PressOnInteractive(object? source)
    {
        var v = source as Visual;
        while (v is not null and not ListBoxItem)
        {
            if (v is Button or Avalonia.Controls.Primitives.ToggleButton or Slider or ComboBox or TextBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void OnLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { e.Pointer.Capture(null); EndDrag(); return; }

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

        // resolve the row under the cursor → drop position. Top/bottom band = reorder above/below;
        // middle band = drop ONTO (nest a filter/adjustment, drop into a group, or auto-group).
        var hitRow = FindLayerRow(LayerList.InputHitTest(e.GetPosition(LayerList)) as Visual);
        if (hitRow is { DataContext: LayerViewModel vm } && (_dragModels is null || !_dragModels.Contains(vm.Model)))
        {
            double rh = hitRow.Bounds.Height;
            double cy = e.GetPosition(hitRow).Y;
            _dropTarget = vm;
            _dropAbove = cy < rh * 0.5;
            _dropInto = cy > rh * 0.3 && cy < rh * 0.7;
            var top = hitRow.TranslatePoint(new Point(0, 0), DragLayer) ?? default;
            if (_dropInto)
            {
                DropIndicator.IsVisible = false;
                Avalonia.Controls.Canvas.SetLeft(DropIntoBox, top.X);
                Avalonia.Controls.Canvas.SetTop(DropIntoBox, top.Y);
                DropIntoBox.Width = hitRow.Bounds.Width;
                DropIntoBox.Height = rh;
                DropIntoBox.IsVisible = true;
            }
            else
            {
                DropIntoBox.IsVisible = false;
                double iy = _dropAbove ? top.Y : top.Y + rh;
                Avalonia.Controls.Canvas.SetLeft(DropIndicator, top.X);
                Avalonia.Controls.Canvas.SetTop(DropIndicator, iy - 1);
                DropIndicator.Width = hitRow.Bounds.Width;
                DropIndicator.IsVisible = true;
            }
        }
        else { _dropTarget = null; DropIndicator.IsVisible = false; DropIntoBox.IsVisible = false; }
    }

    // Open the layer row's ContextFlyout ourselves: the default path collapsed a multi-selection to
    // the clicked row, so Group/Merge acted on one layer. We keep the selection (fixed on right-press)
    // and show the flyout on the row under the cursor. Hit-test by position — e.Source on a bubbled
    // ContextRequested is often the ListBox, not the row.
    private void OnLayerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        Visual? hit = e.TryGetPosition(LayerList, out var p)
            ? LayerList.InputHitTest(p) as Visual
            : e.Source as Visual;
        for (var v = hit; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { ContextFlyout: { } flyout } c &&
                c.DataContext is Sable.UI.ViewModels.LayerViewModel)
            {
                flyout.ShowAt(c);
                e.Handled = true;
                return;
            }
        }
    }

    private void OnLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragModels is { Count: > 0 } items && Doc is { } doc && _dropTarget is { } t)
        {
            if (_dropInto) doc.DropOnto(items, t.Model);                       // nest / into-group / auto-group
            else doc.DropMultipleRelative(items, t.Model, _dropAbove);        // between-row reorder
        }
        else if (!_dragging && _pendingCollapse is { } row)
        {
            // plain click on a selected row without a drag → NOW collapse to just that row
            LayerList.SelectedItems!.Clear();
            LayerList.SelectedItems.Add(row);
        }
        // OnLayerPointerPressed captures the pointer to LayerList for the delayed-deselect/drag gesture.
        // Avalonia mouse capture is explicit — if we don't release it, the next click anywhere routes to
        // the (now stale) LayerList and is silently swallowed app-wide. Mirror OnCanvasPointerReleased.
        // BUT only release a capture WE took: this is a TUNNEL handler, so it runs before an interactive
        // child (visibility eye, lock, chevron) processes its own release. That child captured the pointer
        // to ITSELF to detect its click — clearing it here would swallow the toggle. Leave it alone.
        if (ReferenceEquals(e.Pointer.Captured, LayerList)) e.Pointer.Capture(null);
        EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragSource = null;
        _dragModels = null;
        _dropTarget = null;
        _pendingCollapse = null;
        DragGhost.IsVisible = false;
        DropIndicator.IsVisible = false;
        DropIntoBox.IsVisible = false;
    }

    /// <summary>Walk up to the row container (Control whose DataContext is a LayerViewModel).</summary>
    private static Control? FindLayerRow(Visual? v)
    {
        while (v is not null)
        {
            if (v is Control { DataContext: LayerViewModel } c) return c;
            v = v.GetVisualParent();
        }
        return null;
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
        if (BrushSizeLabel is not null) BrushSizeLabel.Text = Loc.T("mainWindow.pxValue", $"{e.NewValue:0}");
    }

    private void OnStrengthChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.Brush.Strength = (float)(e.NewValue / 100.0);
        Canvas.LiquifyStrength = (float)(e.NewValue / 100.0);
        if (StrengthLabel is not null) StrengthLabel.Text = Loc.T("mainWindow.percentValue", $"{e.NewValue:0}");
    }

    private void OnLiquifyModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LiquifyModeCombo is not null)
            Canvas.LiquifyMode = (Sable.Tools.LiquifyMode)Math.Max(0, LiquifyModeCombo.SelectedIndex);
    }

    private void OnBrushHardnessChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.Brush.Hardness = (float)(e.NewValue / 100.0);
        if (BrushHardnessLabel is not null) BrushHardnessLabel.Text = Loc.T("mainWindow.percentValue", $"{e.NewValue:0}");
    }

    private void OnBrushFlowChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.Brush.Flow = (float)(e.NewValue / 100.0);
        if (BrushFlowLabel is not null) BrushFlowLabel.Text = Loc.T("mainWindow.percentValue", $"{e.NewValue:0}");
    }

    private void OnBrushSmoothChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Canvas.Stabilizer = (float)(e.NewValue / 100.0);

    private void OnBrushSpacingChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Canvas.Brush.Spacing = (float)(e.NewValue / 100.0);

    private void OnPressureToggles(object? sender, RoutedEventArgs e)
    {
        Canvas.Brush.PressureSize = PressureSizeCheck.IsChecked == true;
        Canvas.Brush.PressureFlow = PressureFlowCheck.IsChecked == true;
    }

    private void OnGradShapeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GradShapeCombo is null) return;   // fires during InitializeComponent
        Canvas.GradientShape = (Sable.Tools.GradientShape)System.Math.Clamp(GradShapeCombo.SelectedIndex, 0, 4);
    }

    private void OnCropAspectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CropAspectCombo is null) return;   // fires during InitializeComponent
        Canvas.CropAspect = CropAspectCombo.SelectedIndex switch
        {
            1 => 1.0,
            2 => 4.0 / 3.0,
            3 => 3.0 / 2.0,
            4 => 16.0 / 9.0,
            _ => 0,   // free
        };
    }

    private void OnCropDeleteChanged(object? sender, RoutedEventArgs e)
        => Canvas.CropDeletePixels = CropDeleteCheck.IsChecked == true;

    private void OnZoom50(object? sender, RoutedEventArgs e) => Canvas.SetZoomPercent(50);
    private void OnZoom200(object? sender, RoutedEventArgs e) => Canvas.SetZoomPercent(200);

    private void OnFillForeground(object? sender, RoutedEventArgs e)
        => Canvas.FillSelection(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B);

    private void OnFillBackground(object? sender, RoutedEventArgs e)
        => Canvas.FillSelection(Canvas.BgR, Canvas.BgG, Canvas.BgB);

    // ===== brush presets (options-bar picker; persisted to %AppData%/Sable/brushes.json) =====
    private List<Sable.Tools.BrushPreset> _brushPresets = new();
    private bool _presetSync;

    private void RebuildBrushPresetCombo(string? select = null)
    {
        if (BrushPresetCombo is null) return;
        _presetSync = true;
        BrushPresetCombo.Items.Clear();
        foreach (var p in _brushPresets)
            BrushPresetCombo.Items.Add(new ComboBoxItem { Content = p.Name });
        BrushPresetCombo.SelectedIndex = select is null ? -1 : _brushPresets.FindIndex(p => p.Name == select);
        _presetSync = false;
    }

    private void OnBrushPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_presetSync || BrushPresetCombo is null) return;
        int i = BrushPresetCombo.SelectedIndex;
        if (i < 0 || i >= _brushPresets.Count) return;
        _brushPresets[i].ApplyTo(Canvas.Brush);
        SyncBrushSliders();
        // reflect the non-HUD params too
        BrushFlowSlider.Value = Canvas.Brush.Flow * 100;
        BrushSpacingSlider.Value = Canvas.Brush.Spacing * 100;
        PressureSizeCheck.IsChecked = Canvas.Brush.PressureSize;
        PressureFlowCheck.IsChecked = Canvas.Brush.PressureFlow;
        SyncBrushDynamics();
    }

    private async void OnSaveBrushPreset(object? sender, RoutedEventArgs e)
    {
        var name = await TextPromptWindow.Ask(this, Loc.T("mainWindow.savePreset"),
            Loc.T("mainWindow.presetDefaultName", _brushPresets.Count + 1));
        if (name is null) return;
        _brushPresets.RemoveAll(p => p.Name == name);   // same name = overwrite
        _brushPresets.Add(Sable.Tools.BrushPreset.From(name, Canvas.Brush));
        Sable.Tools.BrushPresetStore.Save(_brushPresets);
        RebuildBrushPresetCombo(name);
    }

    private void OnDeleteBrushPreset(object? sender, RoutedEventArgs e)
    {
        int i = BrushPresetCombo.SelectedIndex;
        if (i < 0 || i >= _brushPresets.Count) return;
        _brushPresets.RemoveAt(i);
        Sable.Tools.BrushPresetStore.Save(_brushPresets);
        RebuildBrushPresetCombo();
    }

    /// <summary>Reflect brush size/hardness back into the options-bar sliders (after HUD adjust).</summary>
    public void SyncBrushSliders()
    {
        if (BrushSizeSlider is null) return;
        BrushSizeSlider.Value = Canvas.Brush.Radius * 2.0;
        BrushHardnessSlider.Value = Canvas.Brush.Hardness * 100.0;
    }

    // ===== brush dynamics flyout: dab shape + jitter + paint blend + .abr import (plan §2) =====
    private bool _dynSync;

    private void WireBrushDynamics()
    {
        BrushBlendCombo.ItemsSource = System.Enum.GetValues<Sable.Core.BlendMode>();
        BrushBlendCombo.SelectedIndex = 0;
        void Hook(LabeledSlider s) => s.PropertyChanged += (_, e) =>
        {
            if (e.Property == LabeledSlider.ValueProperty && !_dynSync) ApplyBrushDynamics();
        };
        Hook(DynAngle); Hook(DynRoundness); Hook(DynSizeJitter);
        Hook(DynFlowJitter); Hook(DynScatter); Hook(DynAngleJitter);
    }

    private void ApplyBrushDynamics()
    {
        var b = Canvas.Brush;
        b.Angle = (float)DynAngle.Value;
        b.Roundness = (float)(DynRoundness.Value / 100.0);
        b.SizeJitter = (float)(DynSizeJitter.Value / 100.0);
        b.FlowJitter = (float)(DynFlowJitter.Value / 100.0);
        b.ScatterJitter = (float)(DynScatter.Value / 100.0);
        b.AngleJitter = (float)(DynAngleJitter.Value / 100.0);
    }

    /// <summary>Reflect the brush's shape/jitter/blend/tip state into the dynamics flyout.</summary>
    private void SyncBrushDynamics()
    {
        if (DynAngle is null) return;
        _dynSync = true;
        var b = Canvas.Brush;
        DynAngle.Value = b.Angle;
        DynRoundness.Value = b.Roundness * 100;
        DynSizeJitter.Value = b.SizeJitter * 100;
        DynFlowJitter.Value = b.FlowJitter * 100;
        DynScatter.Value = b.ScatterJitter * 100;
        DynAngleJitter.Value = b.AngleJitter * 100;
        BrushBlendCombo.SelectedItem = b.PaintBlend;
        BrushTipLabel.Text = b.Tip is null
            ? Loc.T("mainWindow.tipRound")
            : Loc.T("mainWindow.tipSampled", b.TipW, b.TipH);
        ClearTipBtn.IsVisible = b.Tip is not null;
        _dynSync = false;
    }

    private void OnBrushBlendChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dynSync || BrushBlendCombo?.SelectedItem is not Sable.Core.BlendMode m) return;
        Canvas.Brush.PaintBlend = m;
    }

    private void OnClearBrushTip(object? sender, RoutedEventArgs e)
    {
        Canvas.Brush.Tip = null;
        Canvas.Brush.TipW = Canvas.Brush.TipH = 0;
        SyncBrushDynamics();
    }

    private async void OnImportAbr(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("mainWindow.importAbr"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("mainWindow.abrFilter")) { Patterns = new[] { "*.abr" } }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var presets = Sable.Tools.AbrReader.Load(path, out var notes);
            foreach (var p in presets)
            {
                _brushPresets.RemoveAll(x => x.Name == p.Name);   // same name = overwrite
                _brushPresets.Add(p);
            }
            Sable.Tools.BrushPresetStore.Save(_brushPresets);
            RebuildBrushPresetCombo(presets.Count > 0 ? presets[0].Name : null);
            if (presets.Count > 0)
            {
                presets[0].ApplyTo(Canvas.Brush);
                SyncBrushSliders();
                SyncBrushDynamics();
            }
            var body = Loc.T("mainWindow.abrImported", presets.Count);
            if (notes.Count > 0) body += "\n" + string.Join("\n", notes.Take(8));
            ShowToast(Loc.T("mainWindow.importAbr"), body);
        }
        catch (System.Exception ex)
        {
            ShowToast(Loc.T("mainWindow.importAbr"), ex.Message);
        }
    }

    private void OnEyedropperSampleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EyedropperSampleCombo is not null) Canvas.EyedropperRadius = EyedropperSampleCombo.SelectedIndex; // 0/1/2 → point/3×3/5×5
    }

    private void OnEyedropperAllLayersChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb) Canvas.EyedropperAllLayers = cb.IsChecked == true;
    }

    private void OnFeatherChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Canvas.SetSelectionFeather((float)e.NewValue);   // also re-feathers the current selection live
        if (FeatherLabel is not null) FeatherLabel.Text = Loc.T("mainWindow.pxValue", $"{e.NewValue:0}");
    }

    // --- shape tool options (draw-time defaults baked into each new ShapeLayer) ---
    private void OnShapeOptChanged(object? sender, RoutedEventArgs e) => SyncShapeStyle();
    private void OnShapeWidthChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => SyncShapeStyle();
    private void OnShapeNumChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => SyncShapeStyle();

    private void SyncShapeStyle()
    {
        if (ShapeFillChk is null) return;   // not initialized yet
        var s = Canvas.Shape;
        s.Filled = ShapeFillChk.IsChecked == true;
        s.StrokeOn = ShapeStrokeChk.IsChecked == true;
        s.StrokeWidth = (float)ShapeStrokeWidth.Value;
        s.DashOn = ShapeDashChk.IsChecked == true;
        if (ShapeStrokeWidthLabel is not null) ShapeStrokeWidthLabel.Text = Loc.T("mainWindow.pxValue", $"{ShapeStrokeWidth.Value:0}");
        if (int.TryParse(ShapeSidesBox.Text, out var sides)) s.Sides = Math.Clamp(sides, 3, 60);
        if (float.TryParse(ShapeInnerBox.Text, out var inner)) s.InnerRatio = Math.Clamp(inner / 100f, 0.05f, 0.95f);
        if (float.TryParse(ShapeCornerBox.Text, out var corner)) s.CornerRadius = Math.Max(0, corner);
    }

    private bool _gradientTab;
    private Sable.Engine.Layers.ShapeLayer? _shapeTarget;   // selected shape the colour wheel recolours
    private Sable.Engine.Layers.TextLayer? _textTarget;     // selected text layer (wheel recolours + options edit)
    private bool _syncingType;

    private void OnTypeSizeChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (Canvas is null || TypeSizeBox is null) return;   // fires during InitializeComponent (markup Text)
        if (!float.TryParse(TypeSizeBox.Text, out var v)) return;
        Canvas.TypeFontSize = Math.Clamp(v, 4f, 512f);
        if (_syncingType || _textTarget is not { } t) return;
        t.FontSize = Canvas.TypeFontSize; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnBoxWidthChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (Canvas is null || BoxWidthBox is null) return;   // fires during InitializeComponent
        if (!float.TryParse(BoxWidthBox.Text, out var v)) return;
        Canvas.TypeBoxWidth = Math.Max(0f, v);
        if (_syncingType || _textTarget is not { } t) return;
        t.BoxWidth = Canvas.TypeBoxWidth; t.Dirty = true;
        Doc?.SelectedLayer?.RefreshThumbnail();
    }

    private void OnTrackingChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (Canvas is null || TrackingBox is null) return;   // fires during InitializeComponent
        if (!float.TryParse(TrackingBox.Text, out var v)) return;
        Canvas.TypeTracking = v;
        if (_syncingType || _textTarget is not { } t) return;
        t.Tracking = Canvas.TypeTracking; t.Dirty = true;
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
        if (!_syncingColor) SyncColorEditor(c.R, c.G, c.B);
        else UpdateSwatchFills();
    }

    // ===== colour editor: fg/bg swatches, alpha, RGB/HSL/CMYK/LAB sliders, palette (PLAN §16.11) =====
    private int _colorMode;            // 0=RGB 1=HSL 2=CMYK 3=LAB
    private bool _syncingColor;

    private void OnPickFg(object? sender, TappedEventArgs e)
        => SetWheel(Avalonia.Media.Color.FromRgb(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B));
    private void OnPickBg(object? sender, TappedEventArgs e)
        => SetWheel(Avalonia.Media.Color.FromRgb(Canvas.BgR, Canvas.BgG, Canvas.BgB));

    private void OnSwapColors(object? sender, RoutedEventArgs e) { Canvas.SwapColors(); UpdateSwatchFills(); }
    private void OnResetColors(object? sender, RoutedEventArgs e) { Canvas.ResetColors(); UpdateSwatchFills(); }

    private void OnAlphaChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Canvas.Brush.Alpha = (float)(e.NewValue / 100.0);

    private void UpdateSwatchFills()
    {
        if (FgSwatch is null) return;
        FgSwatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B));
        BgSwatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(Canvas.BgR, Canvas.BgG, Canvas.BgB));
    }

    private void OnColorModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ColorModeCombo is null || ColorSliderRows is null || BrushColorView is null) return;   // init-time event
        int idx = Math.Max(0, ColorModeCombo.SelectedIndex);
        bool wheel = idx == 0;                       // 0 = Wheel; 1..4 = RGB/HSL/CMYK/LAB sliders
        BrushColorView.IsVisible = wheel;
        ColorSliderRows.IsVisible = !wheel;
        if (!wheel)
        {
            _colorMode = idx - 1;
            SyncColorEditor(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B);
        }
    }

    // refresh the slider rows + boxes + swatches to show colour (r,g,b) in the current mode
    private void SyncColorEditor(byte r, byte g, byte b)
    {
        if (CSlider0 is null) return;
        _syncingColor = true;
        UpdateSwatchFills();
        (string l0, string l1, string l2, string l3) labels;
        double[] mins = { 0, 0, 0, 0 }, maxs = { 255, 255, 255, 255 }, vals = { r, g, b, 0 };
        bool row3 = false;
        switch (_colorMode)
        {
            case 1:   // HSL
            {
                var (h, s, l) = Sable.Core.ColorConvert.RgbToHsl(r, g, b);
                labels = ("H", "S", "L", ""); maxs = new double[] { 360, 100, 100, 100 };
                vals = new double[] { h, s * 100, l * 100, 0 }; break;
            }
            case 2:   // CMYK
            {
                var (c, m, y, k) = Sable.Core.ColorConvert.RgbToCmyk(r, g, b);
                labels = ("C", "M", "Y", "K"); maxs = new double[] { 100, 100, 100, 100 };
                vals = new double[] { c * 100, m * 100, y * 100, k * 100 }; row3 = true; break;
            }
            case 3:   // LAB
            {
                var (L, a, bb) = Sable.Core.ColorConvert.RgbToLab(r, g, b);
                labels = ("L", "a", "b", ""); mins = new double[] { 0, -128, -128, 0 }; maxs = new double[] { 100, 127, 127, 100 };
                vals = new double[] { L, a, bb, 0 }; break;
            }
            default:  // RGB
                labels = ("R", "G", "B", ""); break;
        }
        CLbl0.Text = labels.l0; CLbl1.Text = labels.l1; CLbl2.Text = labels.l2; CLbl3.Text = labels.l3;
        var sl = new[] { CSlider0, CSlider1, CSlider2, CSlider3 };
        var bx = new[] { CVal0, CVal1, CVal2, CVal3 };
        for (int i = 0; i < 4; i++) { sl[i].Minimum = mins[i]; sl[i].Maximum = maxs[i]; sl[i].Value = vals[i]; bx[i].Text = $"{vals[i]:0}"; }
        CRow3.IsVisible = row3;
        _syncingColor = false;
    }

    private void OnColorSlider(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingColor) return;
        _syncingColor = true;
        CVal0.Text = $"{CSlider0.Value:0}"; CVal1.Text = $"{CSlider1.Value:0}"; CVal2.Text = $"{CSlider2.Value:0}"; CVal3.Text = $"{CSlider3.Value:0}";
        _syncingColor = false;
        ApplyColorFromEditor();
    }

    private void OnColorValBox(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_syncingColor) return;
        _syncingColor = true;
        if (double.TryParse(CVal0.Text, out var v0)) CSlider0.Value = v0;
        if (double.TryParse(CVal1.Text, out var v1)) CSlider1.Value = v1;
        if (double.TryParse(CVal2.Text, out var v2)) CSlider2.Value = v2;
        if (double.TryParse(CVal3.Text, out var v3)) CSlider3.Value = v3;
        _syncingColor = false;
        ApplyColorFromEditor();
    }

    private void ApplyColorFromEditor()
    {
        double a = CSlider0.Value, b = CSlider1.Value, c = CSlider2.Value, d = CSlider3.Value;
        var (r, g, bl) = _colorMode switch
        {
            1 => Sable.Core.ColorConvert.HslToRgb(a, b / 100, c / 100),
            2 => Sable.Core.ColorConvert.CmykToRgb(a / 100, b / 100, c / 100, d / 100),
            3 => Sable.Core.ColorConvert.LabToRgb(a, b, c),
            _ => ((byte)Math.Clamp(a, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(c, 0, 255)),
        };
        _syncingColor = true;
        SetWheel(Avalonia.Media.Color.FromRgb(r, g, bl));   // updates wheel + Brush via OnBrushColorChanged
        UpdateSwatchFills();
        _syncingColor = false;
    }

    private void OnAddSwatch(object? sender, RoutedEventArgs e)
        => AddSwatch(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B);

    private void AddSwatch(byte r, byte g, byte b)
    {
        if (SwatchList is null) return;
        var items = (SwatchList.Items as System.Collections.IList);
        var sw = new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(1),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF555555")), BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        sw.Tapped += (_, _) => SetWheel(Avalonia.Media.Color.FromRgb(r, g, b));
        items?.Add(sw);
    }

    // Tabs switch on PointerPressed (not Tapped): Tapped cancels on a few px of press->release drift, so
    // a slightly imperfect click on a tab did nothing. Pressing acts immediately, drift-proof.
    private void OnSelectColorTab(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        => SetColorTab((sender as Control)?.Tag as string ?? "color");

    private void SetColorTab(string tab)
    {
        if (ColorTabPanel is null) return;   // not initialized yet
        _gradientTab = tab == "grad";
        ColorTabPanel.IsVisible = tab == "color";
        GradientPanel.IsVisible = tab == "grad";
        SwatchesTabPanel.IsVisible = tab == "swatch";
        HistogramTabPanel.IsVisible = tab == "hist";
        NavigatorTabPanel.IsVisible = tab == "nav";

        static Avalonia.Media.IBrush B(bool on) => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(on ? "#FFAAAAAA" : "#FF666666"));
        TabColor.Foreground = B(tab == "color");
        TabGrad.Foreground = B(tab == "grad");
        TabSwatches.Foreground = B(tab == "swatch");
        TabHist.Foreground = B(tab == "hist");
        TabNav.Foreground = B(tab == "nav");

        if (tab == "grad") SyncWheelToStop();
        else if (tab == "color") SetWheel(Avalonia.Media.Color.FromRgb(Canvas.Brush.R, Canvas.Brush.G, Canvas.Brush.B));
        else if (tab == "nav") RefreshNavigator(force: true);
    }

    // --- Navigator panel: composite thumbnail + viewport rect; click/drag re-centres ---
    private int _navThumbVersion = -1;

    /// <summary>Refresh the navigator thumbnail (downscaled composite) + viewport rect.</summary>
    private void RefreshNavigator(bool force = false)
    {
        if (NavView is null || !NavigatorTabPanel.IsVisible) return;
        if (Canvas.Document is not { } doc) { NavView.SetThumbnail(null, 0, 0); return; }

        UpdateNavRect();
        // thumbnail readback is not free — only re-read when the composite may have changed
        int ver = doc.SelectionVersion + (Doc?.Undo.Cursor ?? 0);
        if (!force && ver == _navThumbVersion) return;
        _navThumbVersion = ver;

        if (Canvas.ReadComposite() is not { } px) return;
        const int maxSide = 220;
        int w = doc.Width, h = doc.Height;
        double s = System.Math.Min(1.0, (double)maxSide / System.Math.Max(w, h));
        int tw = System.Math.Max(1, (int)(w * s)), th = System.Math.Max(1, (int)(h * s));
        var thumb = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
        {
            int sy = System.Math.Min(h - 1, (int)((y + 0.5) / s));
            for (int x = 0; x < tw; x++)
            {
                int sx = System.Math.Min(w - 1, (int)((x + 0.5) / s));
                System.Array.Copy(px, (sy * w + sx) * 4, thumb, (y * tw + x) * 4, 4);
            }
        }
        var png = Sable.Imaging.ImageCodec.EncodePngBytes(tw, th, thumb);
        NavView.SetThumbnail(new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(png)), w, h);
    }

    private void UpdateNavRect()
    {
        if (NavView is null) return;
        var (x, y, w, h) = Canvas.VisibleDocRect;
        NavView.SetVisibleRect(x, y, w, h);
        if (NavZoomSlider is not null && !_navZoomSync)
        {
            _navZoomSync = true;
            NavZoomSlider.Value = System.Math.Clamp(Canvas.EffectiveScale * 100, NavZoomSlider.Minimum, NavZoomSlider.Maximum);
            _navZoomSync = false;
        }
    }

    private bool _navZoomSync;

    private void OnNavZoom(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_navZoomSync || Canvas.Document is null) return;
        _navZoomSync = true;
        Canvas.SetZoomPercent(e.NewValue);
        _navZoomSync = false;
    }

    // --- Channels panel (view + isolate) --------------------------------------------------
    // _channelView: 0 composite (normal), 1=R 2=G 3=B 4=A shown grayscale. _chanVis = RGB channel
    // visibility in the composite. Clicking a row views that channel; the eyes mask the composite;
    // right-click loads the channel as a pixel selection.
    private int _channelView;
    private readonly bool[] _chanVis = { true, true, true };   // R, G, B
    private bool _channelsBuilt;
    private readonly Border[] _chanRows = new Border[5];
    private readonly Avalonia.Controls.Primitives.ToggleButton?[] _chanEyes = new Avalonia.Controls.Primitives.ToggleButton?[5];

    private void OnSelectLayerTab(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        => SetLayerTab((sender as Control)?.Tag as string ?? "layers");

    private void SetLayerTab(string tab)
    {
        if (LayersBody is null) return;   // not initialized yet
        bool channels = tab == "channels";
        LayersBody.IsVisible = !channels;
        ChannelsBody.IsVisible = channels;
        static Avalonia.Media.IBrush B(bool on) => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(on ? "#FFAAAAAA" : "#FF666666"));
        TabLayers.Foreground = B(!channels);
        TabChannels.Foreground = B(channels);
        if (channels) { EnsureChannelsBuilt(); ApplyChannels(); }
        else { _channelView = 0; Canvas.SetChannelDisplay(0, 7); }   // restore the normal composite
    }

    private void EnsureChannelsBuilt()
    {
        if (_channelsBuilt || ChannelRows is null) return;
        _channelsBuilt = true;
        ChannelRows.Children.Add(BuildChannelRow(0, Loc.T("mainWindow.channelRgb"),   -1, "#FF888888"));
        ChannelRows.Children.Add(BuildChannelRow(1, Loc.T("mainWindow.channelRed"),    0, "#FFD24B4B"));
        ChannelRows.Children.Add(BuildChannelRow(2, Loc.T("mainWindow.channelGreen"),  1, "#FF4BB04B"));
        ChannelRows.Children.Add(BuildChannelRow(3, Loc.T("mainWindow.channelBlue"),   2, "#FF4B6BD2"));
        ChannelRows.Children.Add(BuildChannelRow(4, Loc.T("mainWindow.channelAlpha"), -1, "#FFBBBBBB"));
        RefreshChannelRows();
    }

    // view = 0 RGB / 1..4 R,G,B,A; visIdx = index into _chanVis (-1 = no composite-visibility eye)
    private Border BuildChannelRow(int view, string name, int visIdx, string chipHex)
    {
        var dock = new DockPanel { LastChildFill = true, Height = 26 };

        if (visIdx >= 0)
        {
            // mirror the layer rows: open eye when visible, struck-through eye (dimmed) when hidden
            var openEye = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon" }, Width = 14, Height = 14,
                Data = Avalonia.Media.Geometry.Parse("M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6z"),
            };
            var shutEye = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon" }, Width = 14, Height = 14, Opacity = 0.4,
                Data = Avalonia.Media.Geometry.Parse("M2 2l20 20 M10.7 5.1A10.4 10.4 0 0 1 12 5c7 0 10 7 10 7a13 13 0 0 1-1.7 2.7 M6.6 6.6A13.5 13.5 0 0 0 2 12s3 7 10 7a9.7 9.7 0 0 0 5.4-1.6"),
            };
            var eye = new Avalonia.Controls.Primitives.ToggleButton
            {
                Classes = { "eye" }, IsChecked = _chanVis[visIdx],
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new Panel { Children = { openEye, shutEye } },
            };
            void SyncEye() { bool on = eye.IsChecked == true; openEye.IsVisible = on; shutEye.IsVisible = !on; }
            SyncEye();
            ToolTip.SetTip(eye, Loc.T("mainWindow.channelVisibility"));
            eye.IsCheckedChanged += (_, _) => { _chanVis[visIdx] = eye.IsChecked == true; SyncEye(); ApplyChannels(); };
            DockPanel.SetDock(eye, Avalonia.Controls.Dock.Left);
            dock.Children.Add(eye);
            _chanEyes[view] = eye;
        }
        else
        {
            dock.Children.Add(new Border { Width = 20, [DockPanel.DockProperty] = Avalonia.Controls.Dock.Left });   // align with eye column
        }

        var chip = new Border
        {
            Width = 26, Height = 18, Margin = new Thickness(4, 0, 6, 0), CornerRadius = new Avalonia.CornerRadius(2),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(chipHex)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF454545")), BorderThickness = new Thickness(1),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, [DockPanel.DockProperty] = Avalonia.Controls.Dock.Left,
        };
        dock.Children.Add(chip);

        var label = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ChromeText"));
        dock.Children.Add(label);

        var row = new Border { Padding = new Thickness(6, 1), CornerRadius = new Avalonia.CornerRadius(3), Child = dock,
                               Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        ToolTip.SetTip(row, Loc.T("mainWindow.channelVisibility"));
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsRightButtonPressed) { LoadChannelAsSelection(view); return; }
            _channelView = view; RefreshChannelRows(); ApplyChannels();
        };
        _chanRows[view] = row;
        return row;
    }

    private void RefreshChannelRows()
    {
        var sel = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF2D4A6B"));
        for (int i = 0; i < _chanRows.Length; i++)
            if (_chanRows[i] is { } r) r.Background = _channelView == i ? sel : Avalonia.Media.Brushes.Transparent;
        for (int i = 0; i < _chanEyes.Length; i++)
            if (_chanEyes[i] is { } eye && i is >= 1 and <= 3) eye.IsChecked = _chanVis[i - 1];
    }

    private void ApplyChannels()
    {
        int mask = (_chanVis[0] ? 1 : 0) | (_chanVis[1] ? 2 : 0) | (_chanVis[2] ? 4 : 0);
        Canvas.SetChannelDisplay(_channelView, mask);
    }

    // Load a channel's values as a pixel selection (RGB row = luminosity).
    private void LoadChannelAsSelection(int view)
    {
        if (_activeTab?.Doc is not { } doc || Canvas.ReadComposite() is not { } px) return;
        int w = doc.Width, h = doc.Height, n = w * h;
        if (px.Length < n * 4) return;
        var cov = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            cov[i] = view switch
            {
                1 => px[o],          // R
                2 => px[o + 1],      // G
                3 => px[o + 2],      // B
                4 => px[o + 3],      // A
                _ => (byte)((px[o] * 77 + px[o + 1] * 150 + px[o + 2] * 29) >> 8),   // luminosity
            };
        }
        doc.SetMaskSelection(cov);
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
    private sealed class ToolGroup
    {
        public string Letter = "";
        public ToolDef[] Tools = Array.Empty<ToolDef>();
        public int Current;
        public ToolButton Button = null!;
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
        const string rect   = "M5 3a2 2 0 0 0-2 2 M19 3a2 2 0 0 1 2 2 M21 19a2 2 0 0 1-2 2 M5 21a2 2 0 0 1-2-2 M9 3h1 M9 21h1 M14 3h1 M14 21h1 M3 9v1 M21 9v1 M3 14v1 M21 14v1";
        const string ellip  = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z";
        const string lasso  = "M7 22a5 5 0 0 1-2-4 M3.3 14A6.8 6.8 0 0 1 2 10c0-4.4 4.5-8 10-8s10 3.6 10 8-4.5 8-10 8a12 12 0 0 1-5-1 M5 18a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
        const string wand   = "M15 4V2 M15 16v-2 M8 9h2 M20 9h2 M17.8 11.8 19 13 M17.8 6.2 19 5 M3 21l9-9 M12.2 6.2 11 5";
        const string colRng = "M9 3a6 6 0 1 0 0 12A6 6 0 1 0 9 3z M15 9a6 6 0 1 0 0 12 6 6 0 1 0 0-12z";
        const string smartS = "M13 3l2 6 6 2-6 2-2 6-2-6-6-2 6-2z M5 3v4 M3 5h4 M6 17v3 M4.5 18.5h3";
        const string brush  = "M9.06 11.9l8.07-8.06a2.85 2.85 0 1 1 4.03 4.03l-8.06 8.08 M7.07 14.94c-1.66 0-3 1.35-3 3.02 0 1.33-2.5 1.52-2 2.02 1.08 1.1 2.49 2.02 4 2.02 2.2 0 4-1.8 4-4.04a3.01 3.01 0 0 0-3-3.02z";
        const string eraser = "M7 21l-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21 M22 21H7 M5 11l9 9";
        const string pencil = "M12 20h9 M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z";
        const string fill   = "M19 11l-8-8-8.6 8.6a2 2 0 0 0 0 2.8l5.2 5.2c.8.8 2 .8 2.8 0L19 11z M5 2l5 5 M2 13h15 M22 20a2 2 0 1 1-4 0c0-1.6 1.7-2.4 2-4 .3 1.6 2 2.4 2 4z";
        const string grad   = "M4 4h16v16H4z M21 3 3 21";
        const string crop   = "M6 2v14a2 2 0 0 0 2 2h14 M18 22V8a2 2 0 0 0-2-2H2";
        const string shRect = "M3 5h18v14H3z";
        const string shRound= "M7 4h10a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3z";
        const string shEll  = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16z";
        const string shLine = "M4 20 20 4";
        const string shPoly = "M12 2 21 8.5 17.5 19h-11L3 8.5z";
        const string shStar = "M12 2 14.9 8.6 22 9.3l-5.3 4.7L18.2 21 12 17.3 5.8 21l1.5-7L2 9.3l7.1-.7z";
        const string shArrow= "M3 12h15 M13 7l6 5-6 5";
        const string clone  = "M5 22h14 M19 18v-3a2 2 0 0 0-2-2H7a2 2 0 0 0-2 2v3 M12 2a2 2 0 0 0-2 2c0 .8.5 1.4 1 1.7V9h2V5.7c.5-.3 1-.9 1-1.7a2 2 0 0 0-2-2z";
        const string heal   = "M8 4h8a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M12 9v6 M9 12h6";
        const string spot   = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 8v8 M8 12h8";
        const string patch  = "M4 4h16v16H4z M4 12h16 M12 4v16";
        const string liquify= "M12 3c3 4 6 6 6 10a6 6 0 0 1-12 0c0-4 3-6 6-10z M9 14a3 3 0 0 0 5 1";
        const string mesh   = "M3 3h18v18H3z M3 9h18 M3 15h18 M9 3v18 M15 3v18";
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
        const string pen    = "M15.7 21.3a1 1 0 0 1-1.4 0l-1.6-1.6a1 1 0 0 1 0-1.4l5.6-5.6a1 1 0 0 1 1.4 0l1.6 1.6a1 1 0 0 1 0 1.4z M18 13l-1.4-6.9a1 1 0 0 0-.7-.8L3.2 2a1 1 0 0 0-1.2 1.2l3.3 12.7a1 1 0 0 0 .8.7L13 18 M2.3 2.3l7.3 7.3 M11 11a2 2 0 1 1-4 0 2 2 0 0 1 4 0z";
        const string node   = "M5 5h4v4H5z M15 15h4v4h-4z M9 7h4a4 4 0 0 1 4 4v4 M7 9v6";

        var defs = new (string letter, ToolDef[] tools)[]
        {
            ("V", new[] { new ToolDef(move, LocToolName(Sable.Tools.ToolKind.Move), Sable.Tools.ToolKind.Move) }),   // unified Move + Transform
            ("M", new[] { new ToolDef(rect, LocToolName(Sable.Tools.ToolKind.Marquee), Sable.Tools.ToolKind.Marquee),
                          new ToolDef(ellip, LocToolName(Sable.Tools.ToolKind.EllipseMarquee), Sable.Tools.ToolKind.EllipseMarquee) }),
            ("L", new[] { new ToolDef(lasso, LocToolName(Sable.Tools.ToolKind.Lasso), Sable.Tools.ToolKind.Lasso),
                          new ToolDef(lasso, LocToolName(Sable.Tools.ToolKind.PolyLasso), Sable.Tools.ToolKind.PolyLasso) }),
            ("W", new[] { new ToolDef(wand, LocToolName(Sable.Tools.ToolKind.MagicWand), Sable.Tools.ToolKind.MagicWand),
                          new ToolDef(colRng, LocToolName(Sable.Tools.ToolKind.ColorRange), Sable.Tools.ToolKind.ColorRange),
                          new ToolDef(smartS, LocToolName(Sable.Tools.ToolKind.SmartSelect), Sable.Tools.ToolKind.SmartSelect) }),
            ("B", new[] { new ToolDef(brush, LocToolName(Sable.Tools.ToolKind.Brush), Sable.Tools.ToolKind.Brush),
                          new ToolDef(pencil, LocToolName(Sable.Tools.ToolKind.Pencil), Sable.Tools.ToolKind.Pencil) }),
            ("E", new[] { new ToolDef(eraser, LocToolName(Sable.Tools.ToolKind.Eraser), Sable.Tools.ToolKind.Eraser) }),
            ("G", new[] { new ToolDef(fill, LocToolName(Sable.Tools.ToolKind.Fill), Sable.Tools.ToolKind.Fill),
                          new ToolDef(grad, LocToolName(Sable.Tools.ToolKind.Gradient), Sable.Tools.ToolKind.Gradient) }),
            ("C", new[] { new ToolDef(crop, LocToolName(Sable.Tools.ToolKind.Crop), Sable.Tools.ToolKind.Crop) }),
            ("U", new[] { new ToolDef(shRect, LocToolName(Sable.Tools.ToolKind.ShapeRect), Sable.Tools.ToolKind.ShapeRect),
                          new ToolDef(shRound, LocToolName(Sable.Tools.ToolKind.ShapeRoundedRect), Sable.Tools.ToolKind.ShapeRoundedRect),
                          new ToolDef(shEll, LocToolName(Sable.Tools.ToolKind.ShapeEllipse), Sable.Tools.ToolKind.ShapeEllipse),
                          new ToolDef(shPoly, LocToolName(Sable.Tools.ToolKind.ShapePolygon), Sable.Tools.ToolKind.ShapePolygon),
                          new ToolDef(shStar, LocToolName(Sable.Tools.ToolKind.ShapeStar), Sable.Tools.ToolKind.ShapeStar),
                          new ToolDef(shLine, LocToolName(Sable.Tools.ToolKind.ShapeLine), Sable.Tools.ToolKind.ShapeLine),
                          new ToolDef(shArrow, LocToolName(Sable.Tools.ToolKind.ShapeArrow), Sable.Tools.ToolKind.ShapeArrow) }),
            ("S", new[] { new ToolDef(clone, LocToolName(Sable.Tools.ToolKind.CloneStamp), Sable.Tools.ToolKind.CloneStamp),
                          new ToolDef(heal, LocToolName(Sable.Tools.ToolKind.Heal), Sable.Tools.ToolKind.Heal),
                          new ToolDef(spot, LocToolName(Sable.Tools.ToolKind.SpotHeal), Sable.Tools.ToolKind.SpotHeal),
                          new ToolDef(patch, LocToolName(Sable.Tools.ToolKind.Patch), Sable.Tools.ToolKind.Patch) }),
            ("O", new[] { new ToolDef(dodge, LocToolName(Sable.Tools.ToolKind.Dodge), Sable.Tools.ToolKind.Dodge),
                          new ToolDef(burn, LocToolName(Sable.Tools.ToolKind.Burn), Sable.Tools.ToolKind.Burn),
                          new ToolDef(sponge, LocToolName(Sable.Tools.ToolKind.Sponge), Sable.Tools.ToolKind.Sponge),
                          new ToolDef(blurB, LocToolName(Sable.Tools.ToolKind.BlurBrush), Sable.Tools.ToolKind.BlurBrush),
                          new ToolDef(sharpB, LocToolName(Sable.Tools.ToolKind.SharpenBrush), Sable.Tools.ToolKind.SharpenBrush),
                          new ToolDef(smudge, LocToolName(Sable.Tools.ToolKind.Smudge), Sable.Tools.ToolKind.Smudge) }),
            ("Y", new[] { new ToolDef(liquify, LocToolName(Sable.Tools.ToolKind.Liquify), Sable.Tools.ToolKind.Liquify),
                          new ToolDef(mesh, LocToolName(Sable.Tools.ToolKind.MeshWarp), Sable.Tools.ToolKind.MeshWarp) }),
            ("T", new[] { new ToolDef(type, LocToolName(Sable.Tools.ToolKind.Type), Sable.Tools.ToolKind.Type) }),
            ("P", new[] { new ToolDef(pen, LocToolName(Sable.Tools.ToolKind.Pen), Sable.Tools.ToolKind.Pen),
                          new ToolDef(node, LocToolName(Sable.Tools.ToolKind.Node), Sable.Tools.ToolKind.Node) }),
            ("I", new[] { new ToolDef(pipette, LocToolName(Sable.Tools.ToolKind.Eyedropper), Sable.Tools.ToolKind.Eyedropper) }),
            ("H", new[] { new ToolDef(hand, LocToolName(Sable.Tools.ToolKind.Hand), Sable.Tools.ToolKind.Hand) }),
            ("Z", new[] { new ToolDef(zoom, LocToolName(Sable.Tools.ToolKind.Zoom), Sable.Tools.ToolKind.Zoom) }),
        };

        foreach (var (letter, tools) in defs)
        {
            var g = new ToolGroup { Letter = letter, Tools = tools };
            var btn = new ToolButton { Classes = { "tool" }, Icon = tools[0].Icon, Tag = g, HasMore = tools.Length > 1 };
            btn.Click += (_, _) => Canvas.ActiveTool = g.Tools[g.Current].Kind;

            var tip = Loc.T("tools.tipSingle", tools[0].Name, letter);
            if (tools.Length > 1)
            {
                tip = Loc.T("tools.tipCycle", string.Join(" / ", tools.Select(t => t.Name)), letter);
                var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var t in tools)
                {
                    int mi = Array.IndexOf(tools, t);
                    var mb = new ToolButton { Classes = { "tool" }, Icon = t.Icon };
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

    private RadialMenuWindow? _radial;

    /// <summary>Quick radial tool menu at the cursor (` key) — pen-friendly tool switching.
    /// Re-pressing ` (or Esc) closes it; the window never takes focus so the editor stays active.</summary>
    private void OpenRadialMenu()
    {
        if (_radial is not null) { _radial.Close(); _radial = null; return; }   // toggle
        if (_activeTab is null) return;
        var items = new (string, System.Action)[]
        {
            (Loc.T("tools.move"), () => SetToolDirect(Sable.Tools.ToolKind.Move)),
            (Loc.T("tools.rectangleMarquee"), () => SetToolDirect(Sable.Tools.ToolKind.Marquee)),
            (Loc.T("tools.brush"), () => SetToolDirect(Sable.Tools.ToolKind.Brush)),
            (Loc.T("tools.eraser"), () => SetToolDirect(Sable.Tools.ToolKind.Eraser)),
            (Loc.T("tools.fill"), () => SetToolDirect(Sable.Tools.ToolKind.Fill)),
            (Loc.T("tools.eyedropper"), () => SetToolDirect(Sable.Tools.ToolKind.Eyedropper)),
            (Loc.T("tools.hand"), () => SetToolDirect(Sable.Tools.ToolKind.Hand)),
            (Loc.T("tools.zoom"), () => SetToolDirect(Sable.Tools.ToolKind.Zoom)),
        };
        _radial = new RadialMenuWindow(items);
        _radial.Closed += (_, _) => _radial = null;
        _radial.ShowAtCursor(this);
    }

    private void CloseRadialMenu() { _radial?.Close(); _radial = null; }

    // ===== before/after peek: hold \ (or the panel's Compare button) to view the document
    // with every adjustment + live filter hidden; release restores them exactly =====
    private readonly List<Sable.Engine.Layers.Layer> _peekHidden = new();

    private void BeginPeek()
    {
        if (_peekHidden.Count > 0 || Canvas.Document is not { } d) return;
        void Walk(List<Sable.Engine.Layers.Layer> list)
        {
            foreach (var l in list)
            {
                if (l.Visible && l is Sable.Engine.Layers.AdjustmentLayer or Sable.Engine.Layers.FilterLayer)
                {
                    l.Visible = false;
                    l.Dirty = true;
                    _peekHidden.Add(l);
                }
                Walk(l.Children);
            }
        }
        Walk(d.Layers);
    }

    private void EndPeek()
    {
        foreach (var l in _peekHidden) { l.Visible = true; l.Dirty = true; }
        _peekHidden.Clear();
    }

    // ===== spring-loaded tools (hold a tool letter = temporary tool, release = restore) =====
    private Key _springKey = Key.None;
    private Sable.Tools.ToolKind _springPrev;
    private DateTime _springDownAt;

    private void SpringTool(KeyEventArgs e, string letter)
    {
        if (e.Key == _springKey) return;   // key autorepeat while held — don't keep cycling
        _springKey = e.Key;
        _springPrev = Canvas.ActiveTool;
        _springDownAt = DateTime.UtcNow;
        CycleGroup(letter);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Oem5 or Key.OemBackslash) EndPeek();   // before/after peek release
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) CommitNudge(e.Key);
        if (e.Key != _springKey) return;
        bool held = (DateTime.UtcNow - _springDownAt).TotalMilliseconds > 350;
        _springKey = Key.None;
        if (held && Canvas.ActiveTool != _springPrev) SetToolDirect(_springPrev);
    }

    /// <summary>Activate a tool by kind, keeping its group's flyout state in sync.</summary>
    private void SetToolDirect(Sable.Tools.ToolKind kind)
    {
        foreach (var g in _groups)
        {
            int i = System.Array.FindIndex(g.Tools, t => t.Kind == kind);
            if (i >= 0) { SelectMember(g, i); return; }
        }
        Canvas.ActiveTool = kind;
    }

    private object? _smartLayer;   // the layer whose SAM2 objects the canvas currently shows
    // per-layer SAM2 object cache: switching back to an unchanged layer reuses its masks instead of
    // re-running SAM2. Keyed by layer ref; the content key invalidates it when the layer's pixels or
    // transform change. docSpace = objects are in doc space (transform baked in) vs layer-buffer space.
    private readonly System.Collections.Generic.Dictionary<Sable.Engine.Layers.PixelLayer,
        (System.Collections.Generic.IReadOnlyList<Sable.Core.Ai.ObjectMask> objs, int key, bool docSpace)> _smartCache = new();

    /// <summary>Cheap sampled hash of a layer's pixels + its transform — changes when the layer is edited
    /// OR transformed, so a cached analysis is reused only while both are unchanged (the transform matters
    /// because we segment the layer as displayed). Mirrors Sam2Adapter's pixel key.</summary>
    private static int ContentKey(Sable.Engine.Layers.PixelLayer px)
    {
        var b = px.Pixels;
        int h = 17 ^ b.Length;
        int step = System.Math.Max(1, b.Length / 4096);
        for (int i = 0; i < b.Length; i += step) h = h * 31 + b[i];
        h = h * 31 + px.OffsetX; h = h * 31 + px.OffsetY;
        h = h * 31 + px.ScaleX.GetHashCode(); h = h * 31 + px.ScaleY.GetHashCode();
        h = h * 31 + px.Rotation.GetHashCode();
        h = h * 31 + px.ShearX.GetHashCode(); h = h * 31 + px.ShearY.GetHashCode();
        h = h * 31 + px.Perspective.GetHashCode();
        if (px.PerspCorners is { } pc) foreach (var v in pc) h = h * 31 + v.GetHashCode();
        return h;
    }

    /// <summary>True when a layer has a non-translation transform (scale/rotate/shear/perspective) — then
    /// SAM2 can't run on the raw buffer + offset mapping; the layer must be rendered to doc space first.</summary>
    private static bool HasNonTranslationTransform(Sable.Engine.Layers.PixelLayer px)
        => px.Perspective || px.ScaleX != 1f || px.ScaleY != 1f || px.Rotation != 0f || px.ShearX != 0f || px.ShearY != 0f;

    /// <summary>Show SAM2 objects for the current active layer: reuse the per-layer cache when the layer's
    /// pixels are unchanged, else run SAM2. No-op unless the Smart Select tool is active. Called when the
    /// analysed layer changes (layer/tab switch) or the tool is entered.</summary>
    private void EnsureSmartObjectsForActiveLayer()
    {
        if (Canvas.ActiveTool != Sable.Tools.ToolKind.SmartSelect) { Canvas.SetSmartObjects(null); _smartLayer = null; return; }
        var model = Doc?.SelectedLayer?.Model;
        if (model is not Sable.Engine.Layers.PixelLayer px || IsLayerEmpty(px))
        { Canvas.SetSmartObjects(null); _smartLayer = model; return; }   // non-pixel / blank → nothing to segment

        if (_smartCache.TryGetValue(px, out var hit) && hit.key == ContentKey(px))
        {
            // unchanged since last analysis → instant, no SAM2 run
            if (hit.docSpace) Canvas.SetSmartObjects(hit.objs);   // transform baked → doc-space identity overlay
            else Canvas.SetSmartObjects(hit.objs, px.OffsetX, px.OffsetY, px.Width, px.Height);   // raw buffer → offset rect
            _smartLayer = px;
            return;
        }
        Canvas.SetSmartObjects(null);   // stale/absent → drop the old masks, then analyse (caches on success)
        _smartLayer = null;
        _ = StartSmartSelect();
    }

    private void OnToolChanged(Sable.Tools.ToolKind kind)
    {
        // entering Smart Select → show the active layer's objects (cached if unchanged, else analyse)
        if (kind == Sable.Tools.ToolKind.SmartSelect) EnsureSmartObjectsForActiveLayer();
        foreach (var g in _groups)
        {
            int idx = Array.FindIndex(g.Tools, t => t.Kind == kind);
            bool sel = idx >= 0;
            if (sel)
            {
                g.Current = idx;
                g.Button.Icon = g.Tools[idx].Icon;
                ToolStatus.Text = g.Tools[idx].Name;
            }
            g.Button.Background = sel ? ToolSelBrush : Avalonia.Media.Brushes.Transparent;
        }
        // keep the colour panel in sync: Gradient tool → Gradients tab, everything else → Color
        SetColorTab(kind == Sable.Tools.ToolKind.Gradient ? "grad" : "color");
        UpdateOptionsBar(kind);
        if (ToolHint is not null) ToolHint.Text = ToolHintFor(kind);
        // Transform panel no longer auto-opens/closes with the tool (user preference) — it's a plain
        // modeless panel toggled from Window ▸ Transform, like History/Adjustments.
    }

    // Affinity-style status-bar hints: what drag/click/modifiers do for the active tool.
    private static string ToolHintFor(ToolKind k) => Loc.T(k switch
    {
        ToolKind.Move or ToolKind.Transform => "toolStatus.move",
        ToolKind.Marquee or ToolKind.EllipseMarquee => "toolStatus.marquee",
        ToolKind.Lasso => "toolStatus.lasso",
        ToolKind.PolyLasso => "toolStatus.polyLasso",
        ToolKind.MagicWand => "toolStatus.magicWand",
        ToolKind.ColorRange => "toolStatus.colorRange",
        ToolKind.Brush or ToolKind.Pencil => "toolStatus.brush",
        ToolKind.Eraser => "toolStatus.eraser",
        ToolKind.Fill => "toolStatus.fill",
        ToolKind.Gradient => "toolStatus.gradient",
        ToolKind.Crop => "toolStatus.crop",
        ToolKind.ShapeRect or ToolKind.ShapeRoundedRect or ToolKind.ShapeEllipse
            or ToolKind.ShapePolygon or ToolKind.ShapeStar => "toolStatus.shape",
        ToolKind.ShapeLine or ToolKind.ShapeArrow => "toolStatus.shapeLine",
        ToolKind.CloneStamp => "toolStatus.cloneStamp",
        ToolKind.Heal => "toolStatus.heal",
        ToolKind.SpotHeal => "toolStatus.spotHeal",
        ToolKind.Patch => "toolStatus.patch",
        ToolKind.Liquify => "toolStatus.liquify",
        ToolKind.MeshWarp => "toolStatus.meshWarp",
        ToolKind.Dodge => "toolStatus.dodge",
        ToolKind.Burn => "toolStatus.burn",
        ToolKind.Sponge => "toolStatus.sponge",
        ToolKind.BlurBrush => "toolStatus.blurBrush",
        ToolKind.SharpenBrush => "toolStatus.sharpenBrush",
        ToolKind.Smudge => "toolStatus.smudge",
        ToolKind.Type => "toolStatus.type",
        ToolKind.Pen => "toolStatus.pen",
        ToolKind.Node => "toolStatus.node",
        ToolKind.Eyedropper => "toolStatus.eyedropper",
        ToolKind.Hand => "toolStatus.hand",
        ToolKind.Zoom => "toolStatus.zoom",
        _ => "toolStatus.move",
    });

    // Localized display name for a tool (toolbox tooltips + command palette).
    internal static string LocToolName(Sable.Tools.ToolKind k) => Loc.T(k switch
    {
        ToolKind.Move or ToolKind.Transform => "tools.move",
        ToolKind.Marquee => "tools.rectangleMarquee",
        ToolKind.EllipseMarquee => "tools.ellipticalMarquee",
        ToolKind.Lasso => "tools.lasso",
        ToolKind.PolyLasso => "tools.polygonalLasso",
        ToolKind.MagicWand => "tools.magicWand",
        ToolKind.ColorRange => "tools.colourRange",
        ToolKind.SmartSelect => "tools.smartSelect",
        ToolKind.Brush => "tools.brush",
        ToolKind.Pencil => "tools.pencil",
        ToolKind.Eraser => "tools.eraser",
        ToolKind.Fill => "tools.fill",
        ToolKind.Gradient => "tools.gradient",
        ToolKind.Crop => "tools.crop",
        ToolKind.ShapeRect => "tools.rectangle",
        ToolKind.ShapeRoundedRect => "tools.roundedRectangle",
        ToolKind.ShapeEllipse => "tools.ellipse",
        ToolKind.ShapePolygon => "tools.polygon",
        ToolKind.ShapeStar => "tools.star",
        ToolKind.ShapeLine => "tools.line",
        ToolKind.ShapeArrow => "tools.arrow",
        ToolKind.CloneStamp => "tools.cloneStamp",
        ToolKind.Heal => "tools.healingBrush",
        ToolKind.SpotHeal => "tools.spotHeal",
        ToolKind.Patch => "tools.patch",
        ToolKind.Dodge => "tools.dodge",
        ToolKind.Burn => "tools.burn",
        ToolKind.Sponge => "tools.sponge",
        ToolKind.BlurBrush => "tools.blur",
        ToolKind.SharpenBrush => "tools.sharpen",
        ToolKind.Smudge => "tools.smudge",
        ToolKind.Liquify => "tools.liquify",
        ToolKind.MeshWarp => "tools.meshWarp",
        ToolKind.Type => "tools.text",
        ToolKind.Pen => "tools.pen",
        ToolKind.Node => "tools.node",
        ToolKind.Eyedropper => "tools.eyedropper",
        ToolKind.Hand => "tools.hand",
        ToolKind.Zoom => "tools.zoom",
        _ => "tools.move",
    });

    // show only the options-bar controls relevant to the active tool
    private void UpdateOptionsBar(Sable.Tools.ToolKind k)
    {
        if (SizeOpts is null) return;   // not initialized yet
        SizeOpts.IsVisible = k is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser or ToolKind.CloneStamp
                              or ToolKind.Heal or ToolKind.SpotHeal or ToolKind.Liquify
                              or ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
                              or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge;
        bool shapeTool = k is ToolKind.ShapeRect or ToolKind.ShapeRoundedRect or ToolKind.ShapeEllipse
                              or ToolKind.ShapeLine or ToolKind.ShapePolygon or ToolKind.ShapeStar or ToolKind.ShapeArrow;
        ShapeOpts.IsVisible = shapeTool;
        bool lineish = k is ToolKind.ShapeLine or ToolKind.ShapeArrow;
        ShapeFillChk.IsVisible = !lineish;
        ShapeStrokeChk.IsVisible = !lineish;   // line/arrow always stroke
        ShapeSidesOpts.IsVisible = k is ToolKind.ShapePolygon or ToolKind.ShapeStar;
        ShapeInnerOpts.IsVisible = k is ToolKind.ShapeStar;
        ShapeCornerOpts.IsVisible = k is ToolKind.ShapeRoundedRect;
        StrengthOpts.IsVisible = k is ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
                                  or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge or ToolKind.Liquify;
        LiquifyOpts.IsVisible = k == ToolKind.Liquify;
        SelectOpts.IsVisible = k is ToolKind.Marquee or ToolKind.EllipseMarquee or ToolKind.Lasso or ToolKind.PolyLasso or ToolKind.MagicWand or ToolKind.ColorRange;
        TypeOpts.IsVisible = k == ToolKind.Type;
        EyedropperOpts.IsVisible = k == ToolKind.Eyedropper;
        GradOpts.IsVisible = k == ToolKind.Gradient;
        CropOpts.IsVisible = k == ToolKind.Crop;
        // Flow only applies to the paint/clone brushes — not liquify/retouch (Smudge etc.) which ignore it
        if (FlowOpts is not null)
            FlowOpts.IsVisible = k is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser
                                  or ToolKind.CloneStamp or ToolKind.Heal or ToolKind.SpotHeal;
        MaskHint.IsVisible = k is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;            // the tunnel OnGlobalKeyDown already consumed it (keymap / Delete / Enter / Escape)
        if (Canvas.TextEditing) return;   // skip tool shortcuts; chars handled by OnTextInput
        if (IsTypingInTextField()) { base.OnKeyDown(e); return; }   // don't cycle tools while typing in a field
        const double step = 40;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: Canvas.ZoomBy(1.1); break;
            case Key.OemMinus or Key.Subtract: Canvas.ZoomBy(1.0 / 1.1); break;
            case Key.D0 or Key.NumPad0: Canvas.ResetView(); break;
            // tool shortcuts (PLAN §14) — re-press cycles within the group; HOLDING the key makes
            // the switch temporary (spring-loaded): release restores the previous tool.
            case Key.V: SpringTool(e, "V"); break;
            case Key.M: SpringTool(e, "M"); break;
            case Key.L: SpringTool(e, "L"); break;
            case Key.W: SpringTool(e, "W"); break;
            case Key.B: SpringTool(e, "B"); break;
            case Key.E: SpringTool(e, "E"); break;   // eraser — its own tool (Affinity-style)
            case Key.G: SpringTool(e, "G"); break;
            case Key.C: SpringTool(e, "C"); break;
            case Key.U: SpringTool(e, "U"); break;
            case Key.S when e.KeyModifiers == KeyModifiers.None: SpringTool(e, "S"); break;
            case Key.O: SpringTool(e, "O"); break;
            case Key.T: SpringTool(e, "T"); break;
            case Key.Y when e.KeyModifiers == KeyModifiers.None: SpringTool(e, "Y"); break;
            case Key.P: SpringTool(e, "P"); break;
            case Key.I: SpringTool(e, "I"); break;
            case Key.H: SpringTool(e, "H"); break;
            case Key.Z: SpringTool(e, "Z"); break;
            // Ctrl+K (palette) and Ctrl+D (deselect) are owned by the rebindable keymap (OnGlobalKeyDown)
            case Key.X when e.KeyModifiers == KeyModifiers.None: Canvas.SwapColors(); UpdateSwatchFills(); break;   // swap fg/bg
            case Key.D when e.KeyModifiers == KeyModifiers.None: Canvas.ResetColors(); UpdateSwatchFills(); break;  // reset fg/bg
            case Key.Oem5 or Key.OemBackslash: BeginPeek(); break;   // hold = before/after (hide adjustments)
            case Key.OemTilde: OpenRadialMenu(); break;              // ` = quick radial tool menu at the cursor
            case Key.Q: Canvas.ToggleQuickMask(); break;   // quick mask (paint the selection as rubylith)
            case Key.K: Canvas.PaintMask = !Canvas.PaintMask; break;   // edit layer mask
            // Delete / Enter / Escape are owned by the tunnel OnGlobalKeyDown (richer pen/mesh/quickmask logic)
            case Key.Left:
                if (Canvas.ActiveTool == Sable.Tools.ToolKind.Move && Canvas.SelLayer is { } nll)
                    NudgeLayer(nll, -1, 0, e);
                else Canvas.PanBy(step, 0);
                break;
            case Key.Right:
                if (Canvas.ActiveTool == Sable.Tools.ToolKind.Move && Canvas.SelLayer is { } nlr)
                    NudgeLayer(nlr, 1, 0, e);
                else Canvas.PanBy(-step, 0);
                break;
            case Key.Up:
                if (Canvas.ActiveTool == Sable.Tools.ToolKind.Move && Canvas.SelLayer is { } nlu)
                    NudgeLayer(nlu, 0, -1, e);
                else Canvas.PanBy(0, step);
                break;
            case Key.Down:
                if (Canvas.ActiveTool == Sable.Tools.ToolKind.Move && Canvas.SelLayer is { } nld)
                    NudgeLayer(nld, 0, 1, e);
                else Canvas.PanBy(0, -step);
                break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// <summary>Move the selected layer by dx/dy pixels (Move tool arrow-key nudge). Shift = 10x step.
    /// While the same arrow key is held (autorepeat), moves live and defers the undo entry until
    /// KeyUp — so a held-key drag is one undo step, not a flood, and skips the per-press Resync.</summary>
    private void NudgeLayer(Layer layer, int dx, int dy, KeyEventArgs e)
    {
        if (_activeTab?.Vm is not { } vm) return;
        int mult = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        // start a new nudge gesture on the first press of a different key (or a different layer)
        if (_nudgeKey != e.Key || !ReferenceEquals(_nudgeLayer, layer))
        {
            _nudgeKey = e.Key;
            _nudgeLayer = layer;
            _nudgeStartX = layer.OffsetX;
            _nudgeStartY = layer.OffsetY;
        }
        layer.OffsetX += dx * mult;
        layer.OffsetY += dy * mult;
        vm.Model.MarkStructureChanged();   // live recomposite — no undo push, no Resync
    }

    /// <summary>Commit the coalesced arrow-key nudge as a single undo entry on key release.</summary>
    private void CommitNudge(Key key)
    {
        if (_nudgeKey != key || _nudgeLayer is not { } layer || _activeTab?.Vm is not { } vm) { _nudgeKey = Key.None; return; }
        if (layer.OffsetX != _nudgeStartX || layer.OffsetY != _nudgeStartY)
            vm.Undo.Execute(new Sable.Engine.Commands.MoveOffsetCommand(vm.Model, layer, _nudgeStartX, _nudgeStartY, layer.OffsetX, layer.OffsetY));
        _nudgeKey = Key.None;
        _nudgeLayer = null;
    }

    /// <summary>Open a document in a new tab and make it active (PLAN Phase 2 multi-tab).</summary>
    private DocumentTab OpenInNewTab(Document doc, string? path, string title, string? sourcePath = null)
    {
        sourcePath ??= path;   // .sable: source = save path; image import: caller passes the image path
        // never open the same file twice — activate the existing tab instead (avoids save conflicts)
        if (sourcePath is not null &&
            _tabs.FirstOrDefault(t => string.Equals(t.SourcePath, sourcePath, System.StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            ActivateTab(existing);
            return existing;
        }

        var tab = new DocumentTab(doc, path, title) { SourcePath = sourcePath };
        tab.Vm.Undo.Capacity = _settings.UndoLimit;
        tab.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DocumentViewModel.SelectedLayer)) return;
            if (!ReferenceEquals(_activeTab, tab)) return;       // only the visible tab drives the UI
            UpdateActiveLayer(tab.Vm);
            // adjustment/filter params + shape properties now live in the docked right-panel
            // editors (AdjPanel / ShapePanelCtl), shown via IsVisible bindings — no floating window.
        };
        _tabs.Add(tab);
        ActivateTab(tab);
        NotifyMissingFonts(doc);
        return tab;
    }

    /// <summary>Affinity-style missing-font toast for any opened document: text layers whose
    /// family isn't installed render with a substitute until the font is added.</summary>
    private void NotifyMissingFonts(Document doc)
    {
        var missing = new System.Collections.Generic.SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var families = Sable.Imaging.TextRaster.Families();
        void Walk(System.Collections.Generic.List<Sable.Engine.Layers.Layer> layers)
        {
            foreach (var l in layers)
            {
                if (l is Sable.Engine.Layers.TextLayer { FontFamily: { Length: > 0 } fam } &&
                    !families.Any(f => string.Equals(f, fam, System.StringComparison.OrdinalIgnoreCase)))
                    missing.Add(fam);
                Walk(l.Children);
            }
        }
        Walk(doc.Layers);
        if (missing.Count > 0)
            ShowToast(Loc.T("toast.missingFontsTitle"), string.Join("\n", missing));
    }

    /// <summary>Make a tab active: swap the canvas + DataContext, rewire the canvas callbacks.</summary>
    private void ActivateTab(DocumentTab? tab)
    {
        if (_activeTab is { } prev)
        {
            prev.IsActive = false;
            CaptureTabPreview(prev);   // snapshot the composite for the tab's hover preview
        }
        _activeTab = tab;
        _smartLayer = null; _smartCache.Clear();   // smart-select masks/cache belong to the previous doc
        ApplyAiVisibility();                        // AI ops enable/disable with doc presence

        if (tab is null)
        {
            Canvas.Document = null;
            DataContext = null;
            ColorPanelRoot.DataContext = null;
            LayersPanelRoot.DataContext = null;
            _currentPath = null;
            UpdateEmptyState();
            UpdateDocInfo();
            return;
        }

        tab.IsActive = true;
        Canvas.Document = tab.Doc;
        DataContext = tab.Vm;
        // docking: set the panel roots' DataContext EXPLICITLY (not just window inheritance) —
        // a floated Tool re-parents into a Dock host window and would silently lose the
        // inherited DocumentViewModel otherwise (the parked-spike failure mode).
        ColorPanelRoot.DataContext = tab.Vm;
        LayersPanelRoot.DataContext = tab.Vm;
        _currentPath = tab.Path;
        WireCanvas(tab.Vm);
        UpdateActiveLayer(tab.Vm);
        if (_adjWindow is not null) _adjWindow.DataContext = tab.Vm;
        if (_fxWindow is not null) _fxWindow.DataContext = tab.Vm;
        if (_transformWindow is not null) _transformWindow.DataContext = tab.Vm;
        if (_historyWindow is not null) _historyWindow.DataContext = tab.Vm;
        Canvas.FitView(_settings.LimitInitialZoom);
        UpdateEmptyState();
        UpdateDocInfo();
        UpdateZoomLabel();
    }

    // wire the canvas callbacks to the active tab's view-model
    private void WireCanvas(DocumentViewModel vm)
    {
        Canvas.CommandProduced = cmd =>
        {
            vm.Undo.Execute(cmd);
            vm.SelectedLayer?.RefreshThumbnail();   // live row thumb after paint/fill/erase/delete
            if (Canvas.ActiveLayer is { } al) _smartCache.Remove(al);   // a pixel edit invalidates that layer's cached SAM2 masks
        };
        Canvas.LayerProduced = layer => vm.AddAndSelect(layer);
        Canvas.ColorPicked = (r, g, b) => SetWheel(Avalonia.Media.Color.FromRgb(r, g, b));
        Canvas.BrushAdjusted = SyncBrushSliders;
    }

    private void UpdateEmptyState()
    {
        bool empty = _tabs.Count == 0;
        if (EmptyState is not null)
        {
            EmptyState.IsVisible = empty && _settings.ShowWelcomeScreen;
            WelcomeShowCheck.IsChecked = _settings.ShowWelcomeScreen;
        }
        if (empty && _settings.ShowWelcomeScreen) RebuildWelcome();
        // the GPU canvas is a native HWND that paints OVER the Avalonia welcome overlay (airspace);
        // hide it when there's no document so the empty/welcome state is actually visible.
        if (Canvas is not null) Canvas.IsVisible = !empty;
        // no document → rulers have nothing to map; stop them taking pointer input (no stray guide
        // drags) and dim them so they read as inactive.
        foreach (var r in new[] { RulerH, RulerV })
            if (r is not null) { r.IsHitTestVisible = !empty; r.Opacity = empty ? 0.4 : 1.0; }
        SetDocMenusEnabled(!empty);   // grey out document-only menu actions (Save/edit/etc.) with no doc
    }

    /// <summary>Enable/disable the menu actions that only make sense with an open document
    /// (the whole editing menus + Save/Export + clipboard + zoom). New/Open/Preferences stay live.</summary>
    private void SetDocMenusEnabled(bool on)
    {
        foreach (var mi in new[]
        {
            FileSaveItem, FileSaveAsItem, FileExportItem,
            FilePlaceItem, FileRevertItem, FileCloseItem, FileCloseAllItem, FileCloseOthersItem,
            EditCutItem, EditCopyItem, EditCopyMergedItem, EditPasteItem, EditPasteIntoItem, EditPasteInPlaceItem, EditDuplicateItem,
            EditFillFgItem, EditFillBgItem,
            ImageMenu, LayerMenu, TypeMenu, SelectMenu, FilterMenu,
            ViewZoomInItem, ViewZoomOutItem, ViewFitItem, ViewActualItem, ViewHalfItem, ViewDoubleItem,
        })
            if (mi is not null) mi.IsEnabled = on;
    }

    private void UpdateActiveLayer(DocumentViewModel vm)
    {
        var m = vm.SelectedLayer?.Model;
        // analysed layer changed (layer/tab switch) → show the new layer's smart-select objects: reuse its
        // cache if the pixels are unchanged (instant), else re-run SAM2. No-op unless the tool is active.
        if (!ReferenceEquals(m, _smartLayer)) EnsureSmartObjectsForActiveLayer();
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
            BoxWidthBox.Text = ((int)txt.BoxWidth).ToString();
            TrackingBox.Text = ((int)txt.Tracking).ToString();
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
        win.CompositeProvider = () => Canvas.ReadComposite();   // backdrop histogram source
        win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        win.Closed += (_, _) => _adjWindow = null;
        _adjWindow = win;
        win.Show(this);            // modeless, owned + centered over main
    }

    private void OnToggleTransform(object? sender, RoutedEventArgs e)
    {
        if (_transformWindow is not null) { _transformWindow.Close(); return; }
        ShowTransformWindow();
    }

    private void ShowTransformWindow()
    {
        if (_transformWindow is not null) { _transformWindow.Activate(); return; }
        var win = new TransformWindow { DataContext = DataContext };
        win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        win.Closed += (_, _) => _transformWindow = null;
        _transformWindow = win;
        win.Show(this);
    }

    private void OnCommandPalette(object? sender, RoutedEventArgs e)
    {
        var actions = new List<(string, Action)>
        {
            (Loc.T("newDocumentWindow.newDocument"), () => OnNewMenu(null, _e)),
            (Loc.T("menu.file.open"), () => OnOpenSable(null, _e)),
            (Loc.T("menu.file.openImage"), () => OnOpenImage(null, _e)),
            (Loc.T("menu.file.save"), () => OnSaveSable(null, _e)),
            (Loc.T("menu.file.saveAs"), () => OnSaveAsSable(null, _e)),
            (Loc.T("menu.file.export"), () => OnExport(null, _e)),
            (Loc.T("menu.edit.undo"), () => Doc?.Undo.Undo()),
            (Loc.T("menu.edit.redo"), () => Doc?.Undo.Redo()),
            (Loc.T("menu.edit.copy"), () => OnCopy(null, _e)),
            (Loc.T("menu.edit.copyMerged"), () => OnCopyMerged(null, _e)),
            (Loc.T("menu.edit.cut"), () => OnCut(null, _e)),
            (Loc.T("menu.edit.paste"), () => OnPaste(null, _e)),
            (Loc.T("mainWindow.selectAll"), () => OnSelectAll(null, _e)),
            (Loc.T("mainWindow.deselect"), () => OnDeselect(null, _e)),
            (Loc.T("mainWindow.invertSelection"), () => OnInvertSelection(null, _e)),
            (Loc.T("mainWindow.newLayer"), () => Doc?.NewLayerCommand.Execute(null)),
            (Loc.T("menu.edit.duplicateLayer"), () => OnDuplicate(null, _e)),
            (Loc.T("mainWindow.mergeDown"), () => OnMergeDown(null, _e)),
            (Loc.T("mainWindow.mergeVisible"), () => OnMergeVisible(null, _e)),
            (Loc.T("mainWindow.flattenImage"), () => OnFlatten(null, _e)),
            (Loc.T("mainWindow.rasteriseLayer"), () => OnRasterise(null, _e)),
            (Loc.T("mainWindow.flipHorizontal"), () => OnFlipH(null, _e)),
            (Loc.T("mainWindow.flipVertical"), () => OnFlipV(null, _e)),
            (Loc.T("mainWindow.rotate90CW"), () => OnRotate90CW(null, _e)),
            (Loc.T("mainWindow.rotate90CCW"), () => OnRotate90CCW(null, _e)),
            (Loc.T("mainWindow.rotate180"), () => OnRotate180(null, _e)),
            (Loc.T("mainWindow.resetTransform"), () => OnResetTransform(null, _e)),
            (Loc.T("mainWindow.alignLeft"), () => OnAlignLeft(null, _e)),
            (Loc.T("mainWindow.alignCenter"), () => OnAlignCenterH(null, _e)),
            (Loc.T("mainWindow.alignRight"), () => OnAlignRight(null, _e)),
            (Loc.T("mainWindow.alignTop"), () => OnAlignTop(null, _e)),
            (Loc.T("mainWindow.alignMiddle"), () => OnAlignMiddle(null, _e)),
            (Loc.T("mainWindow.alignBottom"), () => OnAlignBottom(null, _e)),
            (Loc.T("mainWindow.distributeHorizontally"), () => OnDistributeH(null, _e)),
            (Loc.T("mainWindow.distributeVertically"), () => OnDistributeV(null, _e)),
            (Loc.T("mainWindow.textToCurves"), () => OnTextToCurves(null, _e)),
            (Loc.T("mainWindow.fitToWindow"), () => OnZoomFit(null, _e)),
            (Loc.T("mainWindow.zoom100"), () => OnZoomActual(null, _e)),
            (Loc.T("mainWindow.toggleGrid"), () => OnToggleGrid(null, _e)),
            (Loc.T("mainWindow.toggleRulers"), () => OnToggleRulers(null, _e)),
            (Loc.T("mainWindow.windowAdjustments"), () => OnToggleAdjustments(null, _e)),
            (Loc.T("mainWindow.windowLayerEffects"), () => OnToggleEffects(null, _e)),
            (Loc.T("mainWindow.windowHistory"), () => OnToggleHistory(null, _e)),
        };
        foreach (var t in _toolKinds) { var k = t; actions.Add((Loc.T("tools.cyclePrefix", ToolDisplayName(k)), () => Canvas.ActiveTool = k)); }
        var pal = new CommandPalette(actions);
        pal.Show(this);
    }

    private static readonly RoutedEventArgs _e = new();
    private static readonly Sable.Tools.ToolKind[] _toolKinds = (Sable.Tools.ToolKind[])Enum.GetValues(typeof(Sable.Tools.ToolKind));
    private static string ToolDisplayName(Sable.Tools.ToolKind k) => LocToolName(k);

    // ===== rebindable hotkeys (PLAN §17.1) =====
    // id → handler. Built once (lambdas read the live Doc/_activeTab, so a single map is enough).
    private Dictionary<string, Action>? _keyCommandRun;
    // current keymap as matchable gestures, rebuilt from settings on init + after a settings change.
    private readonly List<(Avalonia.Input.KeyGesture Gesture, string Id)> _keyGestures = new();

    private Dictionary<string, Action> KeyCommandRun => _keyCommandRun ??= new()
    {
        ["file.new"]        = () => OnNewMenu(null, _e),
        ["file.open"]       = () => OnOpenSable(null, _e),
        ["file.openImage"]  = () => OnOpenImage(null, _e),
        ["file.save"]       = () => OnSaveSable(null, _e),
        ["file.saveAs"]     = () => OnSaveAsSable(null, _e),
        ["file.export"]     = () => OnExport(null, _e),
        ["file.closeTab"]   = () => { if (_activeTab is { } t) _ = CloseTab(t); },
        ["edit.undo"]       = () => Doc?.Undo.Undo(),
        ["edit.redo"]       = () => Doc?.Undo.Redo(),
        ["edit.cut"]        = () => OnCut(null, _e),
        ["edit.copy"]       = () => OnCopy(null, _e),
        ["edit.copyMerged"] = () => OnCopyMerged(null, _e),
        ["edit.paste"]      = () => OnPaste(null, _e),
        ["edit.pasteInto"]  = () => OnPasteInto(null, _e),
        ["edit.pasteInPlace"] = () => OnPasteInPlace(null, _e),
        ["edit.fillFg"]     = () => OnFillForeground(null, _e),
        ["edit.fillBg"]     = () => OnFillBackground(null, _e),
        ["edit.duplicate"]  = () => OnDuplicate(null, _e),
        ["select.all"]      = () => OnSelectAll(null, _e),
        ["select.deselect"] = () => OnDeselect(null, _e),
        ["select.invert"]   = () => OnInvertSelection(null, _e),
        ["layer.new"]          = () => Doc?.NewLayerCommand.Execute(null),
        ["layer.group"]        = () => Doc?.GroupCommand.Execute(null),
        ["layer.ungroup"]      = () => Doc?.UngroupCommand.Execute(null),
        ["layer.mergeDown"]    = () => OnMergeDown(null, _e),
        ["layer.mergeVisible"] = () => OnMergeVisible(null, _e),
        ["layer.stamp"]        = () => OnStamp(null, _e),
        ["view.zoomIn"]     = () => OnZoomInMenu(null, _e),
        ["view.zoomOut"]    = () => OnZoomOutMenu(null, _e),
        ["view.fit"]        = () => OnZoomFit(null, _e),
        ["view.actual"]     = () => OnZoomActual(null, _e),
        ["window.palette"]     = () => OnCommandPalette(null, _e),
        ["window.history"]     = () => OnToggleHistory(null, _e),
        ["window.adjustments"] = () => OnToggleAdjustments(null, _e),
        ["window.effects"]     = () => OnToggleEffects(null, _e),
    };

    /// <summary>Re-parse the keymap (catalog defaults + user overrides) into matchable gestures.</summary>
    private void RebuildKeyGestures()
    {
        _keyGestures.Clear();
        foreach (var c in Sable.Core.Settings.KeyCommands.Catalog)
        {
            var g = _settings.GestureFor(c.Id);
            if (string.IsNullOrWhiteSpace(g)) continue;
            try { _keyGestures.Add((Avalonia.Input.KeyGesture.Parse(g), c.Id)); }
            catch { /* a malformed override just disables that binding */ }
        }
        RefreshMenuGestures();
    }

    /// <summary>Update menu accelerator labels to the EFFECTIVE keymap (menu items carry the
    /// command id in Tag; their static InputGesture is just the pre-init default).</summary>
    private void RefreshMenuGestures()
    {
        var byId = Sable.Core.Settings.KeyCommands.Catalog.ToDictionary(c => c.Id);
        foreach (var mi in EnumerateMenuItems(MainMenu.Items))
        {
            if (mi.Tag is not string id || !byId.ContainsKey(id)) continue;
            var g = _settings.GestureFor(id);
            if (string.IsNullOrWhiteSpace(g)) { mi.InputGesture = null; continue; }
            try { mi.InputGesture = Avalonia.Input.KeyGesture.Parse(g); }
            catch { /* malformed override → leave the label empty */ mi.InputGesture = null; }
        }
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(System.Collections.IEnumerable items)
    {
        foreach (var it in items)
        {
            if (it is not MenuItem mi) continue;
            yield return mi;
            foreach (var child in EnumerateMenuItems(mi.Items)) yield return child;
        }
    }

    private void OnShortcuts(object? sender, RoutedEventArgs e) => new ShortcutsWindow(_settings).Show(this);

    /// <summary>Run a command by id (keymap dispatch). Unknown ids are ignored.</summary>
    private void RunKeyCommand(string id)
    {
        if (KeyCommandRun.TryGetValue(id, out var run)) run();
    }

    private void OnToggleHistory(object? sender, RoutedEventArgs e)
    {
        if (_historyWindow is not null) { _historyWindow.Close(); return; }
        var win = new HistoryWindow { DataContext = DataContext };
        win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        win.Closed += (_, _) => _historyWindow = null;
        _historyWindow = win;
        win.Show(this);
    }

    // ===== clipboard (PLAN §16.2 / Phase 1 #5) =====

    private void OnCopy(object? sender, RoutedEventArgs e) => DoCopy(false);
    private void OnCopyMerged(object? sender, RoutedEventArgs e) => DoCopy(true);

    /// <summary>Where the copied region sits in the document (selection bounds, else the layer offset).</summary>
    private (int X, int Y) CopySourcePos()
    {
        if (Canvas.Document?.Selection is { } sel) return (sel.X, sel.Y);
        if (Doc?.SelectedLayer?.Model is { } ml) return (ml.OffsetX, ml.OffsetY);
        return (0, 0);
    }

    private void DoCopy(bool merged)
    {
        var r = merged ? Canvas.CopyMerged() : Canvas.CopyRegion();
        if (r is { } reg)
        {
            var (sx, sy) = CopySourcePos();
            SableClipboard.SetRegion(reg.px, reg.w, reg.h, sx, sy);
            _ = WriteOsImage(reg.px, reg.w, reg.h);
        }
        else if (Doc?.SelectedLayer is { } vm)
        {
            SableClipboard.SetLayer(vm.Model.Clone());   // whole-layer copy (no pixel region)
        }
    }

    private void OnCut(object? sender, RoutedEventArgs e)
    {
        var r = Canvas.CopyRegion();
        if (r is { } reg)
        {
            var (sx, sy) = CopySourcePos();
            SableClipboard.SetRegion(reg.px, reg.w, reg.h, sx, sy);
            _ = WriteOsImage(reg.px, reg.w, reg.h);
            Canvas.DeleteSelection();   // undoable clear of the copied region
        }
        else if (Doc is { } vm && vm.SelectedLayer is not null)
        {
            SableClipboard.SetLayer(vm.SelectedLayer.Model.Clone());
            vm.DeleteLayerCommand.Execute(null);
        }
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is null) return;
        if (SableClipboard.Layer is { } l) { vm.PasteLayer(l.Clone()); return; }
        if (SableClipboard.Pixels is { } px) { vm.PasteLayer(LayerFromRegion(px, SableClipboard.Width, SableClipboard.Height, null)); return; }
        var img = await ReadOsImage();
        if (img is { } i) vm.PasteLayer(LayerFromRegion(i.rgba, i.width, i.height, null));
    }

    private void OnPasteInto(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc) return;
        if (SableClipboard.Pixels is not { } px) return;
        var mask = doc.SnapshotSelectionMask();   // paste clipped to the current selection
        vm.PasteLayer(LayerFromRegion(px, SableClipboard.Width, SableClipboard.Height, mask));
    }

    /// <summary>Paste at the exact document position the region was copied from.</summary>
    private void OnPasteInPlace(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is null) return;
        if (SableClipboard.Pixels is not { } px) { OnPaste(sender, e); return; }
        var layer = LayerFromRegion(px, SableClipboard.Width, SableClipboard.Height, null);
        if (SableClipboard.SourcePos is { } sp) { layer.OffsetX = sp.X; layer.OffsetY = sp.Y; }
        vm.PasteLayer(layer);
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e) => Doc?.DuplicateLayerCommand.Execute(null);

    private void OnSetTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is int tag && Doc?.SelectedLayer is { } vm)
            vm.ColorTag = tag;
    }

    // ===== layer-FX clipboard (copy/paste/clear a layer's effect stack) =====
    private static List<Sable.Engine.Layers.LayerEffect>? _fxClipboard;
    private static float _fxClipboardFill = 1f;

    private void OnCopyEffects(object? sender, RoutedEventArgs e)
    {
        if (Doc?.SelectedLayer?.Model is not { } m) return;
        _fxClipboard = m.Effects.Select(fx => fx.Clone()).ToList();
        _fxClipboardFill = m.FillOpacity;
    }

    private void OnPasteEffects(object? sender, RoutedEventArgs e)
    {
        if (_fxClipboard is null || Doc is not { } doc) return;
        var targets = doc.SelectionModels.Count > 0
            ? doc.SelectionModels.ToList()
            : doc.SelectedLayer?.Model is { } one ? new List<Sable.Engine.Layers.Layer> { one } : new();
        foreach (var m in targets)
        {
            m.Effects.Clear();
            m.Effects.AddRange(_fxClipboard.Select(fx => fx.Clone()));
            m.FillOpacity = _fxClipboardFill;
            m.Dirty = true;
        }
    }

    private void OnClearEffects(object? sender, RoutedEventArgs e)
    {
        if (Doc?.SelectedLayer?.Model is not { } m || m.Effects.Count == 0) return;
        m.Effects.Clear();
        m.Dirty = true;
    }

    // ===== document tabs (Phase 2 #1) =====

    private void OnSelectTab(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: DocumentTab tab } && !ReferenceEquals(tab, _activeTab))
            ActivateTab(tab);
    }

    private async void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        if (sender is not Control { Tag: DocumentTab tab }) return;
        e.Handled = true;
        await CloseTab(tab);
    }

    private async void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: DocumentTab tab }) return;
        e.Handled = true;   // don't let the click select the tab
        await CloseTab(tab);
    }

    private async System.Threading.Tasks.Task CloseTab(DocumentTab tab)
    {
        if (tab.IsDirty)
        {
            var ok = await ConfirmWindow.Ask(this, Loc.T("mainWindow.closeTabTitle", tab.Title), Loc.T("mainWindow.closeTabBody"));
            if (!ok) return;
        }
        int i = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (ReferenceEquals(_activeTab, tab))
            ActivateTab(_tabs.Count == 0 ? null : _tabs[System.Math.Clamp(i, 0, _tabs.Count - 1)]);
    }

    private void OnCloseActive(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is { } t) _ = CloseTab(t);
    }

    private async void OnCloseAll(object? sender, RoutedEventArgs e)
    {
        foreach (var t in _tabs.ToList()) await CloseTab(t);   // dirty tabs prompt; cancel keeps that tab
    }

    private async void OnCloseOthers(object? sender, RoutedEventArgs e)
    {
        var keep = _activeTab;
        foreach (var t in _tabs.ToList())
            if (!ReferenceEquals(t, keep)) await CloseTab(t);
    }

    /// <summary>File ▸ Revert — discard changes and reload the active tab from its file.</summary>
    private async void OnRevert(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is not { } tab) return;
        var src = tab.Path ?? tab.SourcePath;
        if (string.IsNullOrEmpty(src) || !System.IO.File.Exists(src)) return;
        if (tab.IsDirty &&
            !await ConfirmWindow.Ask(this, Loc.T("menu.file.revert"), Loc.T("mainWindow.revertBody"))) return;
        try
        {
            var doc = src.EndsWith(".sable", System.StringComparison.OrdinalIgnoreCase)
                ? SableFile.Load(src)
                : DocumentIO.OpenImage(src);
            int i = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            var nt = OpenInNewTab(doc, tab.Path, tab.Title, tab.SourcePath);
            nt.IsDirty = false;
            if (_tabs.IndexOf(nt) != i && i >= 0 && i < _tabs.Count)
            {
                _tabs.Remove(nt);
                _tabs.Insert(i, nt);
            }
            ActivateTab(nt);
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("menu.file.revert"), ex.Message);
        }
    }

    /// <summary>File ▸ Place — import an image file as a new layer in the open document.</summary>
    private async void OnPlaceImage(object? sender, RoutedEventArgs e)
    {
        if (Doc is null || Canvas.Document is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("menu.file.place"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("mainWindow.imagesFilter"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (DocumentIO.OpenImage(path).Layers is [Sable.Engine.Layers.PixelLayer src2, ..])
            {
                var layer = LayerFromRegion(src2.Pixels, src2.Width, src2.Height, null);
                layer.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                Doc.PasteLayer(layer);   // undoable add to the current document
            }
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("menu.file.place"), ex.Message);
        }
    }

    private void OnNewTabButton(object? sender, RoutedEventArgs e) => _ = OnNewDocument();

    private void OnFilesDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnFilesDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        // drop target: over the canvas (with a doc open) → add image as a layer; elsewhere → new tab
        var p = e.GetPosition(Canvas);
        bool overCanvas = Canvas.Document is not null && Doc is not null &&
            p.X >= 0 && p.Y >= 0 && p.X < Canvas.Bounds.Width && p.Y < Canvas.Bounds.Height;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        OnFilesDropPaths(paths, overCanvas);
    }

    /// <summary>Shared drop handler for both Avalonia DragDrop (Windows/macOS) and X11 XDND (Linux).</summary>
    private void OnFilesDropPaths(System.Collections.Generic.IEnumerable<string> paths, bool overCanvas = false)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".sable")
                {
                    var tab = OpenInNewTab(SableFile.Load(path), path, System.IO.Path.GetFileName(path));
                    tab.IsDirty = false;
                }
                else if (ext == ".psd")
                {
                    OpenPsdTab(path);   // layered import always opens its own tab (placing would flatten)
                }
                else if (overCanvas && DocumentIO.OpenImage(path).Layers is [Sable.Engine.Layers.PixelLayer src, ..])
                {
                    var layer = LayerFromRegion(src.Pixels, src.Width, src.Height, null);   // centred, region-sized
                    layer.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                    Doc!.PasteLayer(layer);   // undoable add to the current document
                }
                else
                {
                    OpenInNewTab(DocumentIO.OpenImage(path), null, System.IO.Path.GetFileName(path), path);
                }
            }
            catch { /* skip files we can't decode */ }
        }
    }

    private async System.Threading.Tasks.Task OnNewDocument()
    {
        var dlg = new NewDocumentWindow(_settings.DefaultDpi);
        if (await dlg.ShowDialog<bool>(this))
        {
            var doc = new Document(dlg.DocWidth, dlg.DocHeight) { Dpi = dlg.Dpi, Depth = dlg.DocDepth };
            var bg = new PixelLayer(dlg.DocWidth, dlg.DocHeight, dlg.Transparent ? Loc.T("mainWindow.layerLayer1") : Loc.T("mainWindow.layerBackground"));
            if (!dlg.Transparent) bg.Pixels.AsSpan().Fill(0xFF);   // opaque white
            bg.Dirty = true;
            doc.Layers.Add(bg);
            OpenInNewTab(doc, null, Loc.T("mainWindow.layerUntitled", _untitledCounter++));
        }
    }

    private void OnNewMenu(object? sender, RoutedEventArgs e) => _ = OnNewDocument();

    private async void OnNewFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (await ReadOsImage() is not { } img) return;
        var doc = new Document(img.width, img.height);
        var layer = new PixelLayer(img.width, img.height, Loc.T("mainWindow.layerClipboard"));
        img.rgba.CopyTo(layer.Pixels.AsSpan());
        layer.Dirty = true;
        doc.Layers.Add(layer);
        OpenInNewTab(doc, null, Loc.T("mainWindow.layerClipboardN", _untitledCounter++));
    }

    // ===== layer collapse ops (PLAN §16.3 / Phase 1 #6): GPU-render to a flat pixel layer =====

    /// <summary>Composite a set of layers into a new doc-sized pixel layer.</summary>
    private PixelLayer? Collapse(System.Collections.Generic.List<Layer> layers, string name)
    {
        if (Canvas.Document is not { } doc || Canvas.RenderLayersToPixels(layers) is not { } bytes) return null;
        var pl = new PixelLayer(doc.Width, doc.Height, name);
        pl.SetBuffer(doc.Width, doc.Height, bytes);
        return pl;
    }

    private void OnMergeDown(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer is null) return;
        var sel = vm.SelectedLayer.Model;
        var parent = doc.FindParent(sel) ?? doc.Layers;
        int i = parent.IndexOf(sel);
        if (i <= 0) return;                          // nothing below to merge with
        var below = parent[i - 1];
        var set = new System.Collections.Generic.List<Layer> { below, sel };
        if (Collapse(set, below.Name) is not { } merged) return;
        vm.Undo.Execute(new Sable.Engine.Commands.ReplaceLayersCommand(doc, parent, set, i - 1, merged, Loc.T("mainWindow.mergeDown")));
        vm.SelectModel(merged);
    }

    private void OnMergeVisible(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc) return;
        var vis = doc.Layers.Where(l => l.Visible).ToList();
        if (vis.Count < 2) return;
        if (Collapse(vis, Loc.T("mainWindow.layerMerged")) is not { } merged) return;
        int idx = doc.Layers.IndexOf(vis[0]);
        vm.Undo.Execute(new Sable.Engine.Commands.ReplaceLayersCommand(doc, doc.Layers, vis, idx, merged, Loc.T("mainWindow.mergeVisible")));
        vm.SelectModel(merged);
    }

    private void OnStamp(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc) return;
        var vis = doc.Layers.Where(l => l.Visible).ToList();
        if (vis.Count == 0 || Collapse(vis, Loc.T("mainWindow.layerStamp")) is not { } stamp) return;
        vm.Undo.Execute(new Sable.Engine.Commands.AddLayerCommand(doc, doc.Layers, stamp, doc.Layers.Count));
        vm.SelectModel(stamp);
    }

    private void OnFlatten(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || doc.Layers.Count == 0) return;
        var all = doc.Layers.ToList();
        if (Collapse(all, Loc.T("mainWindow.layerFlattened")) is not { } flat) return;
        vm.Undo.Execute(new Sable.Engine.Commands.ReplaceLayersCommand(doc, doc.Layers, all, 0, flat, Loc.T("mainWindow.flattenImage")));
        vm.SelectModel(flat);
    }

    // ===== Type menu (PLAN §16.10) =====

    private void OnTextToCurves(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer?.Model is not Sable.Engine.Layers.TextLayer txt) return;
        var path = txt.ToPath();
        if (path.Nodes.Count == 0) return;   // empty / whitespace text
        var parent = doc.FindParent(txt) ?? doc.Layers;
        int i = parent.IndexOf(txt);
        if (i < 0) return;
        vm.Undo.Execute(new Sable.Engine.Commands.ReplaceLayersCommand(doc, parent,
            new System.Collections.Generic.List<Layer> { txt }, i, path, Loc.T("mainWindow.textToCurves")));
        vm.SelectModel(path);
    }

    private void OnTextToPath(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer?.Model is not Sable.Engine.Layers.TextLayer txt) return;
        if (FindPathSource(doc, txt) is not { } pts || pts.Count < 2) return;   // no vector path to fit to
        vm.Undo.Execute(new Sable.Engine.Commands.SetTextPathCommand(doc, txt, pts));
    }

    private void OnTextDetachPath(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer?.Model is not Sable.Engine.Layers.TextLayer txt) return;
        if (txt.PathPoints.Count == 0) return;
        vm.Undo.Execute(new Sable.Engine.Commands.SetTextPathCommand(doc, txt, new System.Collections.Generic.List<(float, float)>()));
    }

    /// <summary>Flattened doc-px polyline of the topmost vector path/shape (excluding the text), to fit text along.</summary>
    private static System.Collections.Generic.List<(float, float)>? FindPathSource(Sable.Engine.Document doc, Layer exclude)
    {
        Layer? src = null;
        void Walk(System.Collections.Generic.List<Layer> layers)
        {
            foreach (var l in layers)   // doc order is bottom→top; keep the last match = topmost
            {
                if (!ReferenceEquals(l, exclude) && l is Sable.Engine.Layers.PathLayer or Sable.Engine.Layers.ShapeLayer) src = l;
                if (l is Sable.Engine.Layers.GroupLayer g) Walk(g.Children);
            }
        }
        Walk(doc.Layers);
        switch (src)
        {
            case Sable.Engine.Layers.PathLayer p:
                return p.Flatten(24).Select(t => ((float)t.X + p.OffsetX, (float)t.Y + p.OffsetY)).ToList();
            case Sable.Engine.Layers.ShapeLayer s:
                var (outline, _) = s.BuildOutline();
                return outline.Select(t => ((float)t.X + s.OffsetX, (float)t.Y + s.OffsetY)).ToList();
            default:
                return null;
        }
    }

    // ===== Transform / align (PLAN §16.9) =====

    private void ApplyTransform(Action<Layer> mutate)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer?.Model is not { } l) return;
        var before = Sable.Engine.Commands.LayerXform.From(l);
        mutate(l);
        vm.Undo.Execute(new Sable.Engine.Commands.TransformLayerCommand(doc, l, before, Sable.Engine.Commands.LayerXform.From(l)));
    }

    private void OnFlipH(object? sender, RoutedEventArgs e) => ApplyTransform(l => l.ScaleX = -l.ScaleX);
    private void OnFlipV(object? sender, RoutedEventArgs e) => ApplyTransform(l => l.ScaleY = -l.ScaleY);
    private void OnRotate90CW(object? sender, RoutedEventArgs e) => ApplyTransform(l => l.Rotation += 90);
    private void OnRotate90CCW(object? sender, RoutedEventArgs e) => ApplyTransform(l => l.Rotation -= 90);
    private void OnRotate180(object? sender, RoutedEventArgs e) => ApplyTransform(l => l.Rotation += 180);
    private void OnResetTransform(object? sender, RoutedEventArgs e) => ApplyTransform(l =>
    {
        l.OffsetX = 0; l.OffsetY = 0; l.ScaleX = 1; l.ScaleY = 1; l.Rotation = 0; l.ShearX = 0; l.ShearY = 0;
        l.Perspective = false; l.PerspCorners = null;
    });

    private void OnAlignLeft(object? sender, RoutedEventArgs e) => AlignSelected(0);
    private void OnAlignCenterH(object? sender, RoutedEventArgs e) => AlignSelected(1);
    private void OnAlignRight(object? sender, RoutedEventArgs e) => AlignSelected(2);
    private void OnAlignTop(object? sender, RoutedEventArgs e) => AlignSelected(3);
    private void OnAlignMiddle(object? sender, RoutedEventArgs e) => AlignSelected(4);
    private void OnAlignBottom(object? sender, RoutedEventArgs e) => AlignSelected(5);
    private void OnDistributeH(object? sender, RoutedEventArgs e) => AlignSelected(6);
    private void OnDistributeV(object? sender, RoutedEventArgs e) => AlignSelected(7);

    // mode: 0=L 1=centreH 2=R 3=T 4=middle 5=B 6=distributeH 7=distributeV
    private void AlignSelected(int mode)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc) return;
        var layers = vm.SelectionModels.ToList();
        if (layers.Count < (mode >= 6 ? 3 : 2)) return;
        var rects = layers.Select(l =>
        {
            var cb = l.ContentBounds(doc.Width, doc.Height);
            return (l, x: cb.x + l.OffsetX, y: cb.y + l.OffsetY, w: cb.w, h: cb.h);
        }).ToList();
        var moves = new List<(Layer, int, int, int, int)>();

        if (mode < 6)
        {
            double gMinX = rects.Min(r => r.x), gMaxX = rects.Max(r => r.x + r.w);
            double gMinY = rects.Min(r => r.y), gMaxY = rects.Max(r => r.y + r.h);
            double gcx = (gMinX + gMaxX) / 2, gcy = (gMinY + gMaxY) / 2;
            foreach (var r in rects)
            {
                int nx = r.l.OffsetX, ny = r.l.OffsetY;
                switch (mode)
                {
                    case 0: nx += (int)Math.Round(gMinX - r.x); break;
                    case 1: nx += (int)Math.Round(gcx - (r.x + r.w / 2.0)); break;
                    case 2: nx += (int)Math.Round(gMaxX - (r.x + r.w)); break;
                    case 3: ny += (int)Math.Round(gMinY - r.y); break;
                    case 4: ny += (int)Math.Round(gcy - (r.y + r.h / 2.0)); break;
                    case 5: ny += (int)Math.Round(gMaxY - (r.y + r.h)); break;
                }
                moves.Add((r.l, r.l.OffsetX, r.l.OffsetY, nx, ny));
            }
        }
        else
        {
            bool horiz = mode == 6;
            var sorted = rects.OrderBy(r => horiz ? r.x + r.w / 2.0 : r.y + r.h / 2.0).ToList();
            int n = sorted.Count;
            double firstC = horiz ? sorted[0].x + sorted[0].w / 2.0 : sorted[0].y + sorted[0].h / 2.0;
            double lastC = horiz ? sorted[^1].x + sorted[^1].w / 2.0 : sorted[^1].y + sorted[^1].h / 2.0;
            for (int i = 0; i < n; i++)
            {
                var r = sorted[i];
                double target = firstC + (lastC - firstC) * i / (n - 1);
                int nx = r.l.OffsetX, ny = r.l.OffsetY;
                if (horiz) nx += (int)Math.Round(target - (r.x + r.w / 2.0));
                else ny += (int)Math.Round(target - (r.y + r.h / 2.0));
                moves.Add((r.l, r.l.OffsetX, r.l.OffsetY, nx, ny));
            }
        }
        vm.Undo.Execute(new Sable.Engine.Commands.AlignLayersCommand(doc, moves));
    }

    private void OnRasterise(object? sender, RoutedEventArgs e)
    {
        if (Doc is not { } vm || Canvas.Document is not { } doc || vm.SelectedLayer is null) return;
        var sel = vm.SelectedLayer.Model;
        if (sel is PixelLayer) return;               // already raster
        var parent = doc.FindParent(sel) ?? doc.Layers;
        int i = parent.IndexOf(sel);
        var set = new System.Collections.Generic.List<Layer> { sel };
        if (Collapse(set, sel.Name) is not { } px) return;
        vm.Undo.Execute(new Sable.Engine.Commands.ReplaceLayersCommand(doc, parent, set, i, px, Loc.T("mainWindow.rasterise")));
        vm.SelectModel(px);
    }

    /// <summary>
    /// Build a pixel layer holding the region. Plain paste = a region-sized layer placed at an
    /// offset (off-canvas pixels preserved, not clipped). Paste-into (with a doc-sized selection
    /// mask) = a doc-sized layer so the mask aligns.
    /// </summary>
    private PixelLayer LayerFromRegion(byte[] px, int w, int h, byte[]? maskFull)
    {
        var doc = Canvas.Document!;

        if (maskFull is null)
        {
            // region-sized layer, centred (or at the selection), keeps everything incl. off-canvas
            var layer = new PixelLayer(w, h, Loc.T("mainWindow.layerPasted"));
            px.CopyTo(layer.Pixels.AsSpan());
            layer.OffsetX = doc.Selection is { } s ? s.X : (doc.Width - w) / 2;
            layer.OffsetY = doc.Selection is { } s2 ? s2.Y : (doc.Height - h) / 2;
            layer.Dirty = true;
            return layer;
        }

        // paste-into: doc-sized so the selection mask lines up
        var full = new PixelLayer(doc.Width, doc.Height, Loc.T("mainWindow.layerPasted"));
        int ox = doc.Selection is { } sel ? sel.X : (doc.Width - w) / 2;
        int oy = doc.Selection is { } sel2 ? sel2.Y : (doc.Height - h) / 2;
        var dst = full.Pixels;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int tx = ox + x, ty = oy + y;
            if (tx < 0 || ty < 0 || tx >= doc.Width || ty >= doc.Height) continue;
            int si = (y * w + x) * 4, di = (ty * doc.Width + tx) * 4;
            dst[di] = px[si]; dst[di + 1] = px[si + 1]; dst[di + 2] = px[si + 2]; dst[di + 3] = px[si + 3];
        }
        full.Mask = maskFull; full.MaskDirty = true;
        full.Dirty = true;
        return full;
    }

    // OS clipboard image interop (Avalonia 12 ClipboardExtensions.Set/TryGetBitmapAsync).
    private async System.Threading.Tasks.Task WriteOsImage(byte[] px, int w, int h)
    {
        if (Clipboard is null) return;
        try
        {
            var png = ImageCodec.EncodePngBytes(w, h, px);
            using var ms = new System.IO.MemoryStream(png);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            await Clipboard.SetBitmapAsync(bmp);
        }
        catch { /* image clipboard is best-effort; internal SableClipboard still holds the copy */ }
    }

    private async System.Threading.Tasks.Task<(int width, int height, byte[] rgba)?> ReadOsImage()
    {
        if (Clipboard is null) return null;
        try
        {
            if (await Clipboard.TryGetBitmapAsync() is { } bmp)
            {
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms);   // PNG
                return ImageCodec.DecodeRgbaBytes(ms.ToArray());
            }
        }
        catch { /* unsupported on this platform */ }
        return null;
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

    // gate the Window-menu tool panels to the current selection (they're param panels for it)
    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e)
    {
        var sel = Doc?.SelectedLayer;
        AdjustmentsMenuItem.IsEnabled = sel?.IsEffect == true;        // adjustment/filter layer
        EffectsMenuItem.IsEnabled = sel is not null && sel.IsEffect == false;   // a content layer
    }

    private void OnToggleAdjustments(object? sender, RoutedEventArgs e)
    {
        if (_adjWindow is not null) _adjWindow.Close();
        else ShowAdjustmentWindow();
    }

    private void OnToggleEffects(object? sender, RoutedEventArgs e)
    {
        if (_fxWindow is not null) { _fxWindow.Close(); return; }
        ShowEffectsWindow();
    }

    // footer "fx" button: open + focus the dialog (don't toggle it closed)
    private void OnFxButton(object? sender, RoutedEventArgs e)
    {
        if (_fxWindow is not null) { _fxWindow.Activate(); return; }
        ShowEffectsWindow();
    }

    private void ShowEffectsWindow()
    {
        var win = new EffectsWindow { DataContext = DataContext };
        win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        win.Closed += (_, _) => _fxWindow = null;
        _fxWindow = win;
        win.Show(this);
    }

    private async void OnOpenImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("mainWindow.openImageTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("mainWindow.imagesFilter"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.psd" }
                }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (path.EndsWith(".psd", System.StringComparison.OrdinalIgnoreCase)) OpenPsdTab(path);
            else OpenInNewTab(DocumentIO.OpenImage(path), null, System.IO.Path.GetFileName(path), path);
            NoteRecent(path);
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("mainWindow.openImageTitle"), Loc.T("mainWindow.openImageError", ex.Message));
        }
    }

    private async void OnOpenSable(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("mainWindow.openSableTitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { SableType }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var tab = OpenInNewTab(SableFile.Load(path), path, System.IO.Path.GetFileName(path));
            tab.IsDirty = false;
            NoteRecent(path);
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("mainWindow.openSableTitle"), Loc.T("mainWindow.openSableError", ex.Message));
        }
    }

    private async void OnSaveSable(object? sender, RoutedEventArgs e)
    {
        if (_currentPath is { } p && Canvas.Document is { } doc)
        {
            try
            {
                SableFile.Save(doc, p, MakeSavePreviewPng());
                if (_activeTab is { } t) t.IsDirty = false;
            }
            catch (System.Exception ex)
            {
                await ConfirmWindow.Ask(this, Loc.T("mainWindow.saveErrorTitle"), Loc.T("mainWindow.saveError", ex.Message));
            }
            return;
        }
        // No .sable save path yet — but if the doc was opened from an image file (PNG/JPG/etc.),
        // overwrite the original image directly (like Photoshop's Save on an imported image).
        if (_activeTab?.SourcePath is { } src && Canvas.Document is { } imgDoc && IsImageExtension(src))
        {
            try
            {
                var rgba = Canvas.ReadComposite();
                if (rgba is not null)
                {
                    DocumentIO.Export(src, ImageCodec.FormatFromExtension(src),
                        imgDoc.Width, imgDoc.Height, rgba, imgDoc.Width, imgDoc.Height, 95, imgDoc.Dpi);
                    _activeTab.IsDirty = false;
                }
            }
            catch (System.Exception ex)
            {
                await ConfirmWindow.Ask(this, Loc.T("mainWindow.saveErrorTitle"), Loc.T("mainWindow.saveError", ex.Message));
            }
            return;
        }
        await SaveAs();
    }

    /// <summary>True if the path has a raster-image extension (not .sable / .psd).</summary>
    private static bool IsImageExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tif" or ".tiff";
    }

    private async void OnSaveAsSable(object? sender, RoutedEventArgs e) => await SaveAs();

    private async Task SaveAs()
    {
        if (Canvas.Document is not { } doc) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("mainWindow.saveSableTitle"),
            SuggestedFileName = Loc.T("mainWindow.untitledFile") + ".sable",
            DefaultExtension = "sable",
            FileTypeChoices = new[] { SableType }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            SableFile.Save(doc, path, MakeSavePreviewPng());
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("mainWindow.saveErrorTitle"), Loc.T("mainWindow.saveError", ex.Message));
            return;
        }
        _currentPath = path;
        if (_activeTab is { } t)
        {
            t.Path = path;
            t.SourcePath = path;   // saved → dedupe future opens of this .sable
            t.Title = System.IO.Path.GetFileName(path);
            t.IsDirty = false;
        }
        NoteRecent(path);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc || Canvas.ReadComposite() is not { } rgba) return;

        var dlg = new ExportDialog(doc.Width, doc.Height, rgba);
        if (!await dlg.ShowDialog<bool>(this)) return;

        string ext = ImageCodec.Extension(dlg.Format);
        string baseName = System.IO.Path.GetFileNameWithoutExtension(_activeTab?.Title ?? Loc.T("mainWindow.untitledFile"));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("mainWindow.exportTitle"),
            SuggestedFileName = $"{baseName}.{ext}",
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(dlg.Format.ToString()) { Patterns = new[] { "*." + ext } } }
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            DocumentIO.Export(path, dlg.Format, doc.Width, doc.Height, rgba, dlg.OutW, dlg.OutH, dlg.Quality, doc.Dpi);
        }
        catch (System.Exception ex)
        {
            await ConfirmWindow.Ask(this, Loc.T("mainWindow.exportErrorTitle"), Loc.T("mainWindow.exportError", ex.Message));
        }
    }
}
