using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using static TaskbarPlayerProto.NativeMethods;

namespace TaskbarPlayerProto;

// "Design your player": colours and pictures of the saved players, edited on a preview of the real player.
// Every change is saved on its own (300 ms after the last one) and shown on the taskbar player.
public partial class OptionsWindow : Window
{
    static readonly string[] Presets =
    {
        "#FFFFFF", "#C8C8C8", "#2B2B2B", "#111111", "#E5484D", "#F76B15",
        "#FFC53D", "#46A758", "#12A594", "#3E63DD", "#8E4EC6", "#D6409F"
    };
    static readonly Color Light = Color.FromRgb(0xE3, 0xE3, 0xE3);
    static readonly Color Ink = Color.FromRgb(0x1F, 0x1F, 0x1F);
    static readonly Color ChipBack = Color.FromRgb(0x2D, 0x2D, 0x2D);
    static readonly Color DefaultBg = Color.FromRgb(0x2B, 0x2B, 0x2B);

    readonly MainWindow _main;
    readonly Designs _designs;
    double _zoom = 3;
    int _selected = -1;                                 // selected picture of the player being edited
    int _previewBuild;
    readonly List<Image> _pvImages = new();
    readonly StickerAnimator _pvAnim = new();
    readonly Dictionary<string, StickerFrames> _pvCache = new();
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Action? _confirmAction;
    bool _loadingName;

    readonly FontFamily _icons = new(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");

    // a row of the layer list being dragged
    StickerSpec? _layerDrag;
    double _layerDragY;
    bool _layerMoved;

    enum DragKind { None, Move, Resize, Rotate }
    DragKind _drag;
    Point _dragFrom;
    double _fromX, _fromY, _fromW, _fromH;

    // selection frame and its handles over the preview
    readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
    readonly Rectangle _sizeHandle = new() { Fill = new SolidColorBrush(Light), Stroke = new SolidColorBrush(Ink), Cursor = Cursors.SizeNWSE };
    readonly Ellipse _turnHandle = new() { Fill = new SolidColorBrush(Light), Stroke = new SolidColorBrush(Ink), Cursor = Cursors.Hand };
    readonly Line _turnLine = new() { Stroke = Brushes.White, IsHitTestVisible = false };

    // custom colour picker
    Popup? _picker;
    bool _pickerForBg;
    double _hue, _sat = 1, _val = 1;
    Rectangle? _svHue;
    Ellipse? _svThumb, _hueThumb;
    TextBox? _hexBox;
    bool _pickerUpdating;

    public OptionsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _designs = main.DesignStore;

        Title = T("options");
        TitleText.Text = T("design");
        AutoSaveText.Text = T("autosave");
        PlayersText.Text = T("players");
        HintText.Text = T("editHint");
        NameText.Text = T("name");
        DeletePlayerButton.Content = T("deletePlayer");
        BgText.Text = T("background");
        ButtonsText.Text = T("buttons");
        ImagesText.Text = T("images");
        AddImageButton.Content = "+  " + T("addImage");
        RemoveButton.Content = T("remove");
        ImageHintText.Text = T("imageHint");
        ResetButton.Content = T("reset");
        ConfirmCancel.Content = T("cancel");

        LayerHintText.Text = T("layersHint");
        foreach (var t in new[] { PvPrev, PvPlay, PvNext, PvVolume, PvMix, PvLoop, PvGlyph }) t.FontFamily = _icons;

        foreach (int z in new[] { 1, 2, 3 })
        {
            var b = new Button { Content = $"{z}x", Tag = z, Style = (Style)FindResource("Chip"), Padding = new Thickness(12, 0, 12, 0) };
            b.Click += (_, _) => SetZoom(z);
            ZoomButtons.Children.Add(b);
        }
        // 3x by default, smaller when the window would not fit on the screen
        double room = SystemParameters.WorkArea.Width - 140;
        SetZoom(382 * 3 + 48 <= room ? 3 : 382 * 2 + 48 <= room ? 2 : 1);

        foreach (var el in new UIElement[] { _frame, _turnLine, _sizeHandle, _turnHandle }) PvOverlay.Children.Add(el);
        _sizeHandle.MouseLeftButtonDown += (_, e) => StartDrag(DragKind.Resize, e);
        _turnHandle.MouseLeftButtonDown += (_, e) => StartDrag(DragKind.Rotate, e);
        PvStickers.MouseLeftButtonDown += (_, _) => Select(-1);         // empty area
        PvStickers.MouseMove += OnDragMove;
        PvStickers.MouseLeftButtonUp += (_, _) => EndDrag();
        PvStickers.LostMouseCapture += (_, _) => _drag = DragKind.None;
        PvStickers.MouseWheel += OnPreviewWheel;
        LayerList.MouseMove += OnLayerMove;
        LayerList.MouseLeftButtonUp += (_, _) => EndLayerDrag();
        LayerList.LostMouseCapture += (_, _) => _layerDrag = null;

        NameBox.TextChanged += (_, _) => OnNameChanged();
        DeletePlayerButton.Click += (_, _) => Confirm(T("deleteAsk"), T("deletePlayer"), DeletePlayer);
        AddImageButton.Click += (_, _) => AddImage();
        RemoveButton.Click += (_, _) => RemoveSelected();
        ResetButton.Click += (_, _) => Confirm(T("resetAsk"), T("reset"), ResetPlayer);
        ConfirmCancel.Click += (_, _) => ConfirmLayer.Visibility = Visibility.Collapsed;
        ConfirmYes.Click += (_, _) => { ConfirmLayer.Visibility = Visibility.Collapsed; _confirmAction?.Invoke(); };

        _saveTimer.Tick += (_, _) => Flush();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageText.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += OnKey;        // before the scroll viewer takes the arrow keys
        PreviewMouseLeftButtonDown += OnPageClick;
        MaxHeight = SystemParameters.WorkArea.Height;   // taller pages scroll
        StateChanged += (_, _) => _pvAnim.Run(WindowState != WindowState.Minimized);
        SourceInitialized += (_, _) => DarkTitleBar();
        _main.CoverChanged += OnCoverChanged;
        Closed += (_, _) =>
        {
            _main.CoverChanged -= OnCoverChanged;
            if (_saveTimer.IsEnabled) Flush();
            _pvAnim.Clear();
            _pvCache.Clear();
        };

        SetupPlacement();
        SetupClear();
        RefreshAll();
    }

    string T(string key) => _main.Text(key);

    PlayerSpec? Current => _designs.Current;

    StickerSpec? SelectedSpec => Current is { } p && _selected >= 0 && _selected < p.Stickers.Count ? p.Stickers[_selected] : null;

    void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE - 1, ref on, sizeof(int));    // 19 on Windows 10 1809-1909
        if (Environment.OSVersion.Version.Build < 22000) return;
        int caption = 0x001F1F1F, corner = DWMWCP_ROUND;    // the title bar in the window's own colour
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    // ---------- whole page ----------

    void RefreshAll()
    {
        RefreshChips();
        RefreshNameRow();
        RefreshSwatches();
        RefreshColors();
        RefreshClear();
        RefreshButtons();
        RebuildPreviewStickers();
    }

    // ---------- transparency ----------

    bool _loadingClear;

    void SetupClear()
    {
        ClearText.Text = T("transparency");
        ClearAllBox.Content = T("clearAll");
        ClearBgLabel.Text = T("background");
        ClearButtonsLabel.Text = T("buttons");
        ClearCoverLabel.Text = T("cover");
        ClearAllBox.Click += (_, _) => SetClearApart(ClearAllBox.IsChecked != true);
        ClearAllSlider.ValueChanged += (_, e) => SetClear(0, e.NewValue);
        ClearBgSlider.ValueChanged += (_, e) => SetClear(1, e.NewValue);
        ClearButtonsSlider.ValueChanged += (_, e) => SetClear(2, e.NewValue);
        ClearCoverSlider.ValueChanged += (_, e) => SetClear(3, e.NewValue);
    }

    // the sliders of the player being edited; "all" shows one slider, else one each
    void RefreshClear()
    {
        _loadingClear = true;
        var p = Current;
        bool apart = p?.ClearApart ?? false;
        ClearAllBox.IsChecked = !apart;
        ClearAllSlider.Value = p?.BgClear ?? 0;
        ClearBgSlider.Value = p?.BgClear ?? 0;
        ClearButtonsSlider.Value = p?.ButtonsClear ?? 0;
        ClearCoverSlider.Value = p?.CoverClear ?? 0;
        foreach (UIElement el in ClearSliders.Children)
        {
            bool allRow = Grid.GetRow(el) == 0;
            el.Visibility = allRow != apart ? Visibility.Visible : Visibility.Collapsed;
        }
        ClearAllValue.Text = $"{(int)ClearAllSlider.Value} %";
        ClearBgValue.Text = $"{(int)ClearBgSlider.Value} %";
        ClearButtonsValue.Text = $"{(int)ClearButtonsSlider.Value} %";
        ClearCoverValue.Text = $"{(int)ClearCoverSlider.Value} %";
        _loadingClear = false;
    }

    // which: 0 all, 1 background, 2 buttons, 3 cover
    void SetClear(int which, double value)
    {
        if (_loadingClear) return;
        int v = (int)Math.Round(Math.Clamp(value, 0, Designs.MaxClear));
        if (Current == null && v == 0) return;          // the default player is solid already
        var p = EnsurePlayer();
        if (p == null) { RefreshClear(); return; }
        if (which == 0) p.BgClear = p.ButtonsClear = p.CoverClear = v;
        else if (which == 1) p.BgClear = v;
        else if (which == 2) p.ButtonsClear = v;
        else p.CoverClear = v;
        RefreshClear();
        RefreshColors();
        _main.ApplyDesignColors();
        QueueSave();
    }

    void SetClearApart(bool apart)
    {
        var p = Current;
        if (p == null && !apart) return;
        p ??= EnsurePlayer();
        if (p == null) { RefreshClear(); return; }
        p.ClearApart = apart;
        // back to one slider: all three take the background's value, so what is seen matches the slider
        if (!apart) p.ButtonsClear = p.CoverClear = p.BgClear;
        RefreshClear();
        RefreshColors();
        _main.ApplyDesignColors();
        QueueSave();
    }

    void RefreshButtons()
    {
        var p = Current;
        AddImageButton.IsEnabled = p == null ? _designs.Players.Count < Designs.MaxPlayers : p.Stickers.Count < Designs.MaxStickers;
        RemoveButton.IsEnabled = SelectedSpec != null;
        ResetButton.IsEnabled = p != null;
    }

    void SetZoom(double z)
    {
        _zoom = z;
        PreviewScale.ScaleX = PreviewScale.ScaleY = z;
        foreach (Button b in ZoomButtons.Children) Paint(b, (int)b.Tag == (int)z);
        UpdateOverlay();
    }

    static void Paint(Button b, bool on)
    {
        b.Background = new SolidColorBrush(on ? Light : Colors.Transparent);
        b.Foreground = new SolidColorBrush(on ? Ink : Light);
    }

    void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    void Confirm(string question, string action, Action yes)
    {
        ConfirmText.Text = question;
        ConfirmYes.Content = action;
        _confirmAction = yes;
        ConfirmLayer.Visibility = Visibility.Visible;
    }

    // ---------- saving ----------

    // Saves soon and shows the new pictures on the taskbar player; colours are shown at once by the callers
    void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Flush()
    {
        _saveTimer.Stop();
        _designs.Save();
        _designs.CleanImages();
        _main.DesignChanged();
    }

    // Editing the default player makes a new saved player first
    PlayerSpec? EnsurePlayer()
    {
        if (Current is { } p) return p;
        if (_designs.Players.Count >= Designs.MaxPlayers) { ShowMessage(T("maxPlayers")); return null; }
        p = new PlayerSpec { Name = UniqueName() };
        _designs.Players.Add(p);
        _designs.Active = p.Id;
        RefreshChips();
        RefreshNameRow();
        RefreshButtons();
        return p;
    }

    string UniqueName()
    {
        string name = T("customPlayer");
        if (_designs.Players.All(p => p.Name != name)) return name;
        for (int i = 2; ; i++)
            if (_designs.Players.All(p => p.Name != $"{name} {i}")) return $"{name} {i}";
    }

    // ---------- players ----------

    void RefreshChips()
    {
        PlayerChips.Children.Clear();
        AddChip(T("default"), _designs.Active == null, () => SwitchTo(null));
        foreach (var p in _designs.Players)
            AddChip(p.Name, p.Id == _designs.Active, () => SwitchTo(p.Id));
        var add = AddChip("+  " + T("newPlayer"), false, NewPlayer);
        add.IsEnabled = _designs.Players.Count < Designs.MaxPlayers;
    }

    Button AddChip(string text, bool on, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis },
            Style = (Style)FindResource("Chip"),
            Margin = new Thickness(0, 0, 8, 8)
        };
        b.Background = new SolidColorBrush(on ? Light : ChipBack);
        b.Foreground = new SolidColorBrush(on ? Ink : Light);
        b.Click += (_, _) => click();
        PlayerChips.Children.Add(b);
        return b;
    }

    void SwitchTo(string? id)
    {
        if (_designs.Active == id) return;
        _designs.Active = id;
        _selected = -1;
        Flush();
        RefreshAll();
    }

    void NewPlayer()
    {
        if (_designs.Players.Count >= Designs.MaxPlayers) { ShowMessage(T("maxPlayers")); return; }
        var p = new PlayerSpec { Name = UniqueName() };
        _designs.Players.Add(p);
        _designs.Active = p.Id;
        _selected = -1;
        Flush();
        RefreshAll();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void DeletePlayer()
    {
        if (Current is not { } p) return;
        _designs.Players.Remove(p);
        _designs.Active = null;
        _selected = -1;
        Flush();
        RefreshAll();
    }

    void ResetPlayer()
    {
        if (Current is not { } p) return;
        p.Bg = Designs.Hex(DefaultBg);              // the player's look when nothing plays
        p.Buttons = "#FFFFFF";
        p.Stickers.Clear();
        p.BgClear = p.ButtonsClear = p.CoverClear = 0;
        p.ClearApart = false;
        _selected = -1;
        Flush();
        RefreshAll();
    }

    void RefreshNameRow()
    {
        var p = Current;
        NameRow.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) return;
        _loadingName = true;
        NameBox.Text = p.Name;
        _loadingName = false;
    }

    void OnNameChanged()
    {
        if (_loadingName || Current is not { } p) return;
        string name = NameBox.Text.Trim();
        if (name.Length == 0) return;               // keep the old name while the box is empty
        p.Name = name;
        int i = _designs.Players.IndexOf(p) + 1;    // chip 0 is "Default"
        if (i < PlayerChips.Children.Count && PlayerChips.Children[i] is Button { Content: TextBlock tb }) tb.Text = name;
        QueueSave();
    }

    // ---------- colours ----------

    (Color Bg, Color Accent) EffectiveColors()
    {
        var (cb, ca) = _main.Chameleon;
        var p = Current;
        return (Designs.ParseColor(p?.Bg) ?? cb, Designs.ParseColor(p?.Buttons) ?? ca);
    }

    void OnCoverChanged()
    {
        RefreshColors();
        RefreshSwatches();
    }

    // the preview in the same colours as the player: transparent parts come from the button colour
    void RefreshColors()
    {
        var (bg, a) = EffectiveColors();
        // the design's transparency, as on the taskbar
        double bs = MainWindow.Solid(Current?.BgClear), ks = MainWindow.Solid(Current?.ButtonsClear);
        Brush Of(byte alpha) => new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * ks), a.R, a.G, a.B));
        PvPanel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(bg.A * bs), bg.R, bg.G, bg.B));
        PvStickers.Opacity = bs;
        PvCover.Opacity = MainWindow.Solid(Current?.CoverClear);
        foreach (var t in new[] { PvPrev, PvPlay, PvNext, PvVolume, PvLoop }) t.Foreground = Of(0xFF);
        PvMix.Foreground = Of(0x80);
        PvTime.Foreground = Of(0xCC);
        PvTrack.Background = Of(0x44);
        PvFill.Background = Of(0xFF);
        PvThumb.Fill = Of(0xFF);

        PvCover.Background = _main.CoverBackground;
        PvGlyph.Visibility = _main.CoverShowsGlyph ? Visibility.Visible : Visibility.Collapsed;
        bool right = _main.CoverOnRight;
        PvCover.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PvPanel.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    void RefreshSwatches()
    {
        var p = Current;
        var (cb, ca) = _main.Chameleon;
        FillSwatches(BgSwatches, true, p?.Bg, cb);
        FillSwatches(ButtonSwatches, false, p?.Buttons, ca);
    }

    void FillSwatches(WrapPanel panel, bool bg, string? value, Color chameleon)
    {
        panel.Children.Clear();
        panel.Children.Add(Swatch(value == null, chameleon, rainbow: true, plus: false, T("chameleon"), () => SetColor(bg, null)));
        foreach (var hex in Presets)
        {
            bool on = string.Equals(value, hex, StringComparison.OrdinalIgnoreCase);
            panel.Children.Add(Swatch(on, Designs.ParseColor(hex)!.Value, false, false, hex, () => SetColor(bg, hex)));
        }
        // the last one opens the picker; it shows the custom colour when one is chosen
        bool custom = value != null && !Presets.Contains(value, StringComparer.OrdinalIgnoreCase);
        var c = custom ? Designs.ParseColor(value) ?? Colors.Gray : Colors.Gray;
        FrameworkElement? swatch = null;
        swatch = Swatch(custom, c, rainbow: false, plus: !custom, T("customColor"), () => OpenPicker(bg, swatch!));
        panel.Children.Add(swatch);
    }

    static readonly Brush Rainbow = MakeRainbow();

    static Brush MakeRainbow()
    {
        var b = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0x4D, 0x4D), 0), new(Color.FromRgb(0xFF, 0xC5, 0x3D), 0.2), new(Color.FromRgb(0x46, 0xD3, 0x6B), 0.4),
            new(Color.FromRgb(0x3E, 0xB8, 0xFF), 0.6), new(Color.FromRgb(0x8E, 0x4E, 0xC6), 0.8), new(Color.FromRgb(0xFF, 0x4D, 0xC4), 1)
        }, 45);
        b.Freeze();
        return b;
    }

    FrameworkElement Swatch(bool on, Color fill, bool rainbow, bool plus, string tip, Action click)
    {
        var g = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = tip };
        if (on) g.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Light), StrokeThickness = 2 });
        var dot = new Ellipse { Width = 22, Height = 22, Fill = plus ? Rainbow : new SolidColorBrush(fill) as Brush };
        if (rainbow) { dot.Stroke = Rainbow; dot.StrokeThickness = 3; }
        else if (fill.R + fill.G + fill.B < 90) { dot.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)); dot.StrokeThickness = 1; }   // dark colours on the dark card
        g.Children.Add(dot);
        if (plus) g.Children.Add(new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        g.MouseLeftButtonUp += (_, _) => click();
        return g;
    }

    void SetColor(bool bg, string? hex)
    {
        if (Current == null && hex == null) return;  // the default player is chameleon already
        var p = EnsurePlayer();
        if (p == null) return;
        if (bg) p.Bg = hex; else p.Buttons = hex;
        if (!(_picker?.IsOpen ?? false)) RefreshSwatches();
        RefreshColors();
        _main.ApplyDesignColors();
        QueueSave();
    }

    // ---------- custom colour picker: saturation / brightness square, hue bar, hex ----------

    void OpenPicker(bool bg, FrameworkElement anchor)
    {
        _pickerForBg = bg;
        _picker ??= BuildPicker();
        var (cb, ca) = EffectiveColors();
        var start = bg ? cb : ca;
        (_hue, _sat, _val) = ToHsv(start);
        _picker.PlacementTarget = anchor;
        UpdatePicker(updateHex: true);
        _picker.IsOpen = true;
    }

    Popup BuildPicker()
    {
        const double W = 220, H = 150;
        var sv = new Grid { Width = W, Height = H, Cursor = Cursors.Cross, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 6, 6) };
        _svHue = new Rectangle();
        sv.Children.Add(_svHue);
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var svLayer = new Canvas { IsHitTestVisible = false };
        _svThumb = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2 };
        svLayer.Children.Add(_svThumb);
        sv.Children.Add(svLayer);

        var hue = new Grid { Width = W, Height = 14, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand };
        var hueStops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) hueStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        hue.Children.Add(new Rectangle { RadiusX = 7, RadiusY = 7, Fill = new LinearGradientBrush(hueStops, 0) });
        var hueLayer = new Canvas { IsHitTestVisible = false };
        _hueThumb = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent };
        hueLayer.Children.Add(_hueThumb);
        hue.Children.Add(hueLayer);

        _hexBox = new TextBox { Style = (Style)FindResource("Field"), Width = 110, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 7 };
        _hexBox.TextChanged += (_, _) =>
        {
            if (_pickerUpdating) return;
            string t = _hexBox.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && Designs.ParseColor(t) is Color c)
            {
                (_hue, _sat, _val) = ToHsv(c);
                UpdatePicker(updateHex: false);
                SetColor(_pickerForBg, Designs.Hex(c));
            }
        };

        void PickSv(Point p)
        {
            _sat = Math.Clamp(p.X / W, 0, 1);
            _val = 1 - Math.Clamp(p.Y / H, 0, 1);
            UpdatePicker(updateHex: true);
            SetColor(_pickerForBg, Designs.Hex(FromHsv(_hue, _sat, _val)));
        }
        void PickHue(Point p)
        {
            _hue = Math.Clamp(p.X / W, 0, 0.9999) * 360;
            UpdatePicker(updateHex: true);
            SetColor(_pickerForBg, Designs.Hex(FromHsv(_hue, _sat, _val)));
        }
        sv.MouseLeftButtonDown += (_, e) => { sv.CaptureMouse(); PickSv(e.GetPosition(sv)); };
        sv.MouseMove += (_, e) => { if (sv.IsMouseCaptured) PickSv(e.GetPosition(sv)); };
        sv.MouseLeftButtonUp += (_, _) => sv.ReleaseMouseCapture();
        hue.MouseLeftButtonDown += (_, e) => { hue.CaptureMouse(); PickHue(e.GetPosition(hue)); };
        hue.MouseMove += (_, e) => { if (hue.IsMouseCaptured) PickHue(e.GetPosition(hue)); };
        hue.MouseLeftButtonUp += (_, _) => hue.ReleaseMouseCapture();

        var body = new StackPanel();
        body.Children.Add(sv);
        body.Children.Add(hue);
        body.Children.Add(_hexBox);
        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 12, 12),
                FlowDirection = FlowDirection.LeftToRight,
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.45 },
                Child = body
            }
        };
        popup.Closed += (_, _) => RefreshSwatches();
        return popup;
    }

    void UpdatePicker(bool updateHex)
    {
        if (_svHue == null || _svThumb == null || _hueThumb == null || _hexBox == null) return;
        _svHue.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        Canvas.SetLeft(_svThumb, _sat * 220 - 7);
        Canvas.SetTop(_svThumb, (1 - _val) * 150 - 7);
        Canvas.SetLeft(_hueThumb, _hue / 360 * 220 - 8);
        Canvas.SetTop(_hueThumb, -1);
        if (updateHex)
        {
            _pickerUpdating = true;
            _hexBox.Text = Designs.Hex(FromHsv(_hue, _sat, _val));
            _pickerUpdating = false;
        }
    }

    static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ---------- pictures ----------

    async void AddImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = $"{T("images")} (PNG, JPG, GIF)|*.png;*.jpg;*.jpeg;*.gif" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > Designs.MaxFileBytes) { ShowMessage(T("badImage")); return; }
        }
        catch { ShowMessage(T("badImage")); return; }
        if (Current is { } existing && existing.Stickers.Count >= Designs.MaxStickers) { ShowMessage(T("maxImages")); return; }
        var p = EnsurePlayer();
        if (p == null) return;

        string file;
        try { file = Designs.Import(dialog.FileName); }
        catch { ShowMessage(T("badImage")); return; }
        var frames = await LoadPreview(file);
        if (frames == null)
        {
            try { File.Delete(Designs.ImagePath(file)); } catch { }
            ShowMessage(T("badImage"));
            return;
        }
        // as tall as the player, centered; wide pictures are made to fit its width
        double h = 40, w = h * frames.Aspect;
        if (w > 338) { w = 338; h = w / frames.Aspect; }
        p.Stickers.Add(new StickerSpec { File = file, Name = System.IO.Path.GetFileName(dialog.FileName), X = 169, Y = 20, W = w, H = h });
        _selected = p.Stickers.Count - 1;
        RebuildPreviewStickers();
        RefreshButtons();
        QueueSave();
    }

    async Task<StickerFrames?> LoadPreview(string file)
    {
        if (_pvCache.TryGetValue(file, out var f)) return f;
        f = await Stickers.LoadAsync(Designs.ImagePath(file), 1400, 3_000_000);
        if (f != null) _pvCache[file] = f;
        return f;
    }

    async void RebuildPreviewStickers()
    {
        int build = ++_previewBuild;
        var specs = Current?.Stickers.ToList() ?? new List<StickerSpec>();
        var loaded = new List<(StickerSpec spec, StickerFrames frames)>();
        foreach (var s in specs)
        {
            var f = await LoadPreview(s.File);
            if (build != _previewBuild) return;
            if (f != null) loaded.Add((s, f));
        }
        _pvAnim.Clear();
        _pvImages.Clear();
        PvStickers.Children.Clear();
        foreach (var (spec, frames) in loaded)
        {
            var img = new Image { Source = frames.Frames[0], Stretch = Stretch.Fill, Tag = spec, Cursor = Cursors.SizeAll };
            Panel.SetZIndex(img, specs.IndexOf(spec));
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Stickers.Place(img, spec);
            img.MouseLeftButtonDown += (_, e) =>
            {
                if (Current is not { } p) return;
                Select(p.Stickers.IndexOf(spec));
                StartDrag(DragKind.Move, e);
            };
            PvStickers.Children.Add(img);
            _pvImages.Add(img);
            _pvAnim.Add(img, frames);
        }
        _pvAnim.Run(WindowState != WindowState.Minimized);
        UpdateOverlay();
        RefreshLayers();
    }

    Image? SelectedImage => _pvImages.FirstOrDefault(i => ReferenceEquals(i.Tag, SelectedSpec));

    // a click anywhere but on a picture, its handles, the layer list or Remove drops the selection
    void OnPageClick(object sender, MouseButtonEventArgs e)
    {
        if (_selected < 0 || e.OriginalSource is not DependencyObject d) return;
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Image { Tag: StickerSpec } || d == _sizeHandle || d == _turnHandle || d == LayerCard || d == RemoveButton) return;
        Select(-1);
    }

    void Select(int index)
    {
        _selected = index;
        UpdateOverlay();
        RefreshButtons();
        RefreshLayers();
    }

    // ---------- layer list: one row per image, the image in front first ----------

    void RefreshLayers()
    {
        LayerList.Children.Clear();
        var specs = Current?.Stickers ?? new List<StickerSpec>();
        LayerCard.Visibility = LayerHintText.Visibility = specs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var dim = (Brush)FindResource("Dim");
        var picked = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        var hover = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
        for (int i = specs.Count - 1; i >= 0; i--)          // the spec list runs back to front
        {
            var s = specs[i];
            bool on = ReferenceEquals(s, SelectedSpec);
            var row = new Border
            {
                Height = 38, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 0, 12, 0),
                Background = on ? picked : Brushes.Transparent, Cursor = Cursors.SizeNS, Tag = s
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = "", FontFamily = _icons, FontSize = 12, Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var thumb = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            if (_pvCache.TryGetValue(s.File, out var f)) thumb.Source = f.Frames[0];
            RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
            var box = new Border { Width = 46, Height = 28, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), Margin = new Thickness(0, 0, 12, 0), Child = thumb };
            Grid.SetColumn(box, 1);
            g.Children.Add(box);
            var name = new TextBlock { Text = s.Name ?? string.Format(T("imageN"), i + 1), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280 };
            Grid.SetColumn(name, 2);
            g.Children.Add(name);
            row.Child = g;
            row.MouseEnter += (_, _) => { if (!ReferenceEquals(row.Tag, SelectedSpec)) row.Background = hover; };
            row.MouseLeave += (_, _) => { if (!ReferenceEquals(row.Tag, SelectedSpec)) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (Current is not { } p) return;
                _layerDragY = e.GetPosition(LayerList).Y;
                _layerMoved = false;
                Select(p.Stickers.IndexOf(s));
                _layerDrag = s;
                LayerList.CaptureMouse();
                e.Handled = true;
            };
            LayerList.Children.Add(row);
        }
    }

    // the dragged row goes where the mouse is; the preview changes its order at once
    void OnLayerMove(object sender, MouseEventArgs e)
    {
        if (_layerDrag is not { } s || Current is not { } p || !LayerList.IsMouseCaptured) return;
        double y = e.GetPosition(LayerList).Y;
        if (!_layerMoved && Math.Abs(y - _layerDragY) < 4) return;
        _layerMoved = true;
        int rows = LayerList.Children.Count;
        double rowH = rows > 0 && LayerList.ActualHeight > 0 ? LayerList.ActualHeight / rows : 38;
        int target = p.Stickers.Count - 1 - Math.Clamp((int)(y / rowH), 0, rows - 1);
        int now = p.Stickers.IndexOf(s);
        if (target == now || now < 0) return;
        p.Stickers.RemoveAt(now);
        p.Stickers.Insert(target, s);
        _selected = target;
        foreach (var img in _pvImages) Panel.SetZIndex(img, p.Stickers.IndexOf((StickerSpec)img.Tag));
        RefreshLayers();
        UpdateOverlay();
        QueueSave();
    }

    void EndLayerDrag()
    {
        _layerDrag = null;
        if (LayerList.IsMouseCaptured) LayerList.ReleaseMouseCapture();
    }

    void RemoveSelected()
    {
        if (Current is not { } p || SelectedSpec is not { } s) return;
        p.Stickers.Remove(s);
        _selected = -1;
        RebuildPreviewStickers();
        RefreshButtons();
        QueueSave();
    }

    // frame and handles of the selected picture; sizes are divided by the zoom so they stay the same on screen
    void UpdateOverlay()
    {
        var s = SelectedSpec;
        var vis = s == null ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement el in PvOverlay.Children) el.Visibility = vis;
        if (s == null) return;
        double k = 1 / _zoom;

        _frame.Width = Math.Max(1, s.W);
        _frame.Height = Math.Max(1, s.H);
        _frame.StrokeThickness = 1.5 * k;
        Canvas.SetLeft(_frame, s.X - s.W / 2);
        Canvas.SetTop(_frame, s.Y - s.H / 2);
        _frame.RenderTransformOrigin = new Point(0.5, 0.5);
        _frame.RenderTransform = new RotateTransform(s.Angle);

        double hs = 10 * k;
        var corner = Turn(s, s.W / 2, s.H / 2);
        _sizeHandle.Width = _sizeHandle.Height = hs;
        _sizeHandle.StrokeThickness = k;
        _sizeHandle.RadiusX = _sizeHandle.RadiusY = 2 * k;
        Canvas.SetLeft(_sizeHandle, corner.X - hs / 2);
        Canvas.SetTop(_sizeHandle, corner.Y - hs / 2);

        var top = Turn(s, 0, -s.H / 2);
        var knob = Turn(s, 0, -s.H / 2 - 18 * k);
        _turnHandle.Width = _turnHandle.Height = hs * 1.1;
        _turnHandle.StrokeThickness = k;
        Canvas.SetLeft(_turnHandle, knob.X - hs * 0.55);
        Canvas.SetTop(_turnHandle, knob.Y - hs * 0.55);
        _turnLine.X1 = top.X; _turnLine.Y1 = top.Y;
        _turnLine.X2 = knob.X; _turnLine.Y2 = knob.Y;
        _turnLine.StrokeThickness = k;
    }

    // a point given relative to the picture's center, turned with the picture
    static Point Turn(StickerSpec s, double lx, double ly)
    {
        double a = s.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        return new Point(s.X + lx * cos - ly * sin, s.Y + lx * sin + ly * cos);
    }

    void StartDrag(DragKind kind, MouseButtonEventArgs e)
    {
        if (SelectedSpec is not { } s) return;
        e.Handled = true;
        _drag = kind;
        _dragFrom = e.GetPosition(PvStickers);
        (_fromX, _fromY, _fromW, _fromH) = (s.X, s.Y, s.W, s.H);
        PvStickers.CaptureMouse();
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragKind.None || SelectedSpec is not { } s) return;
        var p = e.GetPosition(PvStickers);
        switch (_drag)
        {
            case DragKind.Move:
                s.X = _fromX + p.X - _dragFrom.X;
                s.Y = _fromY + p.Y - _dragFrom.Y;
                break;
            case DragKind.Resize:
            {
                // the corner follows the mouse along the diagonal, so the picture keeps its shape
                double a = -s.Angle * Math.PI / 180;
                double dx = p.X - s.X, dy = p.Y - s.Y;
                double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
                double hx = _fromW / 2, hy = _fromH / 2;
                double k = (lx * hx + ly * hy) / (hx * hx + hy * hy);
                k = Math.Clamp(k, 4 / Math.Min(_fromW, _fromH), 700 / Math.Max(_fromW, _fromH));
                s.W = _fromW * k;
                s.H = _fromH * k;
                break;
            }
            case DragKind.Rotate:
            {
                double angle = Math.Atan2(p.Y - s.Y, p.X - s.X) * 180 / Math.PI + 90;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) angle = Math.Round(angle / 15) * 15;
                else
                {
                    double nearest = Math.Round(angle / 90) * 90;     // snaps to straight angles
                    if (Math.Abs(angle - nearest) < 4) angle = nearest;
                }
                angle %= 360;
                if (angle > 180) angle -= 360;
                if (angle <= -180) angle += 360;
                s.Angle = Math.Round(angle, 1);
                break;
            }
        }
        if (SelectedImage is { } img) Stickers.Place(img, s);
        UpdateOverlay();
        QueueSave();
    }

    void EndDrag()
    {
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        PvStickers.ReleaseMouseCapture();
    }

    void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (Current is not { } p) return;
        var hit = _pvImages.LastOrDefault(i => i.IsMouseOver);
        if (hit?.Tag is StickerSpec spec) Select(p.Stickers.IndexOf(spec));
        if (SelectedSpec is not { } s) return;
        e.Handled = true;
        double k = e.Delta > 0 ? 1.08 : 1 / 1.08;
        if (s.W * k < 4 || s.H * k < 4 || s.W * k > 700 || s.H * k > 700) return;
        s.W *= k;
        s.H *= k;
        if (SelectedImage is { } img) Stickers.Place(img, s);
        UpdateOverlay();
        QueueSave();
    }

    // ---------- placement: free move, always on top, the two shortcuts ----------

    int _capturing = -1;            // the shortcut waiting for new keys (0 show / hide, 1 cursor), -1 none
    uint _capMods;                  // the most modifiers held at once while waiting
    bool _capKey;                   // a key other than a modifier was pressed while waiting

    void SetupPlacement()
    {
        PlacementText.Text = T("placement");
        FreeMoveBox.Content = T("freeMove");
        FreeMoveHint.Text = T("freeMoveHint");
        OnTopBox.Content = T("onTop");
        OnTopHint.Text = T("onTopHint");
        HotkeyText.Text = T("hotkey");
        CursorKeyText.Text = T("cursorKey");
        HotkeyHint.Text = T("hotkeyHint");
        FreeMoveBox.IsChecked = _main.FreeMove;
        OnTopBox.IsChecked = _main.AlwaysOnTop;
        FreeMoveBox.Click += (_, _) => _main.SetFree(FreeMoveBox.IsChecked == true);
        OnTopBox.Click += (_, _) => { _main.SetOnTop(OnTopBox.IsChecked == true); ShowCursorKeyState(); };
        CursorKeyNote.Text = T("needsOnTop");
        ShowCursorKeyState();
        ShowKeyButton.Click += (_, _) => ToggleCapture(0);
        CursorKeyButton.Click += (_, _) => ToggleCapture(1);
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => StopCapture();
        Closed += (_, _) => StopCapture();
        // another player picked: its own switches come back
        Action placement = () =>
        {
            FreeMoveBox.IsChecked = _main.FreeMove;
            OnTopBox.IsChecked = _main.AlwaysOnTop;
            ShowCursorKeyState();
        };
        _main.PlacementChanged += placement;
        Closed += (_, _) => _main.PlacementChanged -= placement;
        ShowShortcuts();
        if (_main.ShortcutBusy) ShowShortcutMessage("hotkeyBusy");      // another app took the keys before MMP started
    }

    // the mouse shortcut only works with "show over games": greyed out with a note while that is off
    void ShowCursorKeyState()
    {
        bool on = _main.AlwaysOnTop;
        if (!on && _capturing == 1) StopCapture();
        CursorKeyButton.IsEnabled = on;
        CursorKeyText.Opacity = on ? 1 : 0.45;
        CursorKeyNote.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowShortcuts()
    {
        foreach (var (b, i) in new[] { (ShowKeyButton, 0), (CursorKeyButton, 1) })
        {
            bool waiting = _capturing == i;
            b.Content = waiting ? T("hotkeyPress") : _main.ShortcutText(i);
            b.Background = new SolidColorBrush(waiting ? Light : ChipBack);
            b.Foreground = new SolidColorBrush(waiting ? Ink : Light);
        }
    }

    void ShowShortcutMessage(string? textKey)
    {
        HotkeyMessage.Text = textKey == null ? "" : T(textKey);
        HotkeyMessage.Visibility = textKey == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void ToggleCapture(int which)
    {
        bool same = _capturing == which;
        StopCapture();
        if (same) return;
        _capturing = which;
        _capMods = 0;
        _capKey = false;
        _main.PauseShortcuts();         // so pressing the old keys is seen here, not shows / hides the player
        ShowShortcutMessage(null);
        ShowShortcuts();
    }

    void StopCapture()
    {
        if (_capturing < 0) return;
        _capturing = -1;
        _main.ResumeShortcuts();
        ShowShortcuts();
    }

    void SetShortcut(uint mods, uint vk)
    {
        int which = _capturing;
        string? error = _main.TrySetShortcut(which, mods, vk);
        StopCapture();
        ShowShortcutMessage(error);
    }

    static uint ModsNow()
    {
        var m = Keyboard.Modifiers;
        return (m.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0) | (m.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
            | (m.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) | (m.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);
    }

    // New keys: Ctrl, Alt or Win with a key, or an F key on its own (Shift + a letter would stop the user typing
    // capitals). Two of Ctrl, Alt and Shift pressed and let go with nothing else is a shortcut of modifiers only.
    void CaptureKey(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Escape) { StopCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed)
        {
            _capMods |= ModsNow();
            return;
        }
        _capKey = true;
        uint mods = ModsNow();
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        bool fKey = key >= Key.F1 && key <= Key.F24;
        if (vk == 0 || (mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0 && !fKey) { ShowShortcutMessage("hotkeyBad"); return; }
        SetShortcut(mods, vk);
    }

    void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_capturing < 0) return;
        e.Handled = true;
        if (Keyboard.Modifiers != ModifierKeys.None) return;        // wait until all are let go
        uint mods = _capMods;
        bool key = _capKey;
        _capMods = 0;
        _capKey = false;
        if (key || mods == 0) return;
        int count = new[] { MOD_CONTROL, MOD_ALT, MOD_SHIFT }.Count(m => (mods & m) != 0);
        if (count >= 2 && (mods & MOD_WIN) == 0) SetShortcut(mods, 0);
        else ShowShortcutMessage("hotkeyBad");
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_capturing >= 0) { CaptureKey(e); return; }
        if (Keyboard.FocusedElement is TextBox || ConfirmLayer.Visibility == Visibility.Visible) return;
        if (e.Key == Key.Escape) { Select(-1); return; }
        if (SelectedSpec is not { } s) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        switch (e.Key)
        {
            case Key.Delete: case Key.Back: RemoveSelected(); e.Handled = true; return;
            case Key.Left: s.X -= step; break;
            case Key.Right: s.X += step; break;
            case Key.Up: s.Y -= step; break;
            case Key.Down: s.Y += step; break;
            default: return;
        }
        e.Handled = true;
        if (SelectedImage is { } img) Stickers.Place(img, s);
        UpdateOverlay();
        QueueSave();
    }
}
