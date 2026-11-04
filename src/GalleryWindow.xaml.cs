using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarPlayerProto;

// The gallery: everyone's players (or equalizer sounds) as cards, six a page.
public partial class GalleryWindow : Window
{
    static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A));
    static readonly Brush DeveloperBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xA0, 0x8A));
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xE4, 0x57, 0x2E));
    static readonly Brush Pink = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x7A));
    static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    // the same colours as the design page; the first is the chameleon ring
    static readonly (string Key, string Text, string? Hex)[] ColorChoices =
    {
        ("chameleon", "galCChameleon", null), ("white", "galCWhite", "#FFFFFF"), ("gray", "galCGray", "#C8C8C8"), ("charcoal", "galCCharcoal", "#2B2B2B"),
        ("black", "galCBlack", "#111111"), ("red", "galCRed", "#E5484D"), ("orange", "galCOrange", "#F76B15"), ("yellow", "galCYellow", "#FFC53D"),
        ("green", "galCGreen", "#46A758"), ("teal", "galCTeal", "#12A594"), ("blue", "galCBlue", "#3E63DD"), ("purple", "galCPurple", "#8E4EC6"),
        ("pink", "galCPink", "#D6409F"),
    };

    readonly MainWindow _main;
    readonly string _kind;      // "player" or "eq": each has its own gallery, the account is the same
    readonly FontFamily _icons = new(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");
    readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "mmp-gallery-" + Guid.NewGuid().ToString("N"));

    readonly DispatcherTimer _flow = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly StickerAnimator _animator = new();
    readonly List<PlayerPreview> _previews = new();

    readonly PagedList _publish;
    readonly PagedList _mine;

    string _sort = "new";
    readonly HashSet<string> _show = new();
    readonly HashSet<string> _colors = new();
    readonly List<CheckBox> _sortBoxes = new();
    int _page, _generation;
    bool _isAdmin, _busy, _closed, _loadAfter;
    GalleryItem? _reporting;
    string? _reason;

    public GalleryWindow(MainWindow main, string kind)
    {
        InitializeComponent();
        _main = main;
        _kind = kind;
        // players large enough to see what they are; sounds are taller and fit three across
        if (kind == "eq") { Cards.Columns = 3; Cards.Rows = 2; }
        _publish = new PagedList(this, PublishList, PublishPager);
        _mine = new PagedList(this, MineList, MinePager);
        PublishPageButton.Content = T(kind == "eq" ? "galMineSounds" : "galMineDesigns");
        PublishPageButton.Click += (_, _) => Run(OpenPublishPage);
        BackIcon.FontFamily = _icons;
        BackText.Text = T("galTitle");
        BackButton.Click += (_, _) =>
        {
            PublishPage.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
            LoadSoon();
        };
        PublishTitle.Text = T("galPublishTitle");
        MineTitle.Text = T("galMine");
        FlagButton.Tag = _icons;
        HoverCard.Attach(FlagButton, T("galReports"));
        FlagButton.Click += (_, _) =>
        {
            HoverCard.Hide();
            OpenOver(new ReportsWindow(_main, _kind) { Owner = this, FlowDirection = FlowDirection }, () => LoadSoon());
        };

        Title = TitleText.Text = PageTitle.Text = T(kind == "eq" ? "galTitleEq" : "galTitlePlayer");
        WelcomeLogo.Child = new Viewbox { Child = PlayerPreview.Logo() };
        WelcomeTitle.Text = T("galWelcome");
        WelcomeSub.Text = T("galWelcomeSub");
        ChooseNameText.Text = T("galChooseName");
        NameHintText.Text = T("galNameHint");
        NameWarningText.Text = T("galNameWarning");
        RulesLinkText.Text = T("galRulesLink");
        AcceptTail.Text = T("galAcceptTail");
        OpenHint.Text = T("galOpenHint");
        RulesTitle.Text = T("galRulesTitle");
        RulesText.Text = T("galRulesText");
        RulesClose.Content = T("galRulesOk");
        CreateButton.Content = T("galEnter");
        ToastIcon.FontFamily = _icons;
        FilterIcon.FontFamily = _icons;
        FilterText.Text = T("galFilter");
        CodeHint.Text = T("galCodeHint");
        OpenCodeButton.Content = T("galOpen");
        ShowAllButton.Content = T("galShowAll");
        EmptyText.Text = T(kind == "eq" ? "galEmptyEq" : "galEmptyPlayer");
        ReportCancel.Content = T("cancel");
        ReportSend.Content = T("galSend");
        ReportNoteHint.Text = T("galReportNote");

        RulesLink.Click += (_, _) =>
        {
            RulesCard.Visibility = RulesCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            AcceptBox.IsEnabled = true;
            OpenHint.Visibility = Visibility.Collapsed;
        };
        RulesClose.Click += (_, _) => RulesCard.Visibility = Visibility.Collapsed;
        AcceptBox.Click += (_, _) =>
        {
            if (AcceptBox.IsChecked == true) RulesCard.Visibility = Visibility.Collapsed;
            RefreshCreate();
        };
        UserNameBox.TextChanged += (_, _) => RefreshCreate();
        ConfirmCancel.Content = T("cancel");
        ConfirmYes.Content = T("galStart");
        CreateButton.Click += (_, _) =>
        {
            ConfirmText.Text = string.Format(T("galNameSure"), UserNameBox.Text.Trim());
            ConfirmLayer.Visibility = Visibility.Visible;
        };
        ConfirmCancel.Click += (_, _) => ConfirmLayer.Visibility = Visibility.Collapsed;
        ConfirmYes.Click += (_, _) =>
        {
            ConfirmLayer.Visibility = Visibility.Collapsed;
            Run(CreateAccount);
        };
        FilterButton.Click += (_, _) => FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        OpenCodeButton.Click += (_, _) => Run(OpenCode);
        CodeBox.TextChanged += (_, _) => CodeHint.Visibility = CodeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CodeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Run(OpenCode); };
        ShowAllButton.Click += (_, _) => { CodeBox.Text = ""; LoadSoon(); };
        ReportCancel.Click += (_, _) => ReportPanel.Visibility = Visibility.Collapsed;
        ReportSend.Click += (_, _) => Run(SendReport);

        _flow.Tick += (_, _) => { foreach (var p in _previews) p.Tick(0.1); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = PublishToast.Visibility = Visibility.Collapsed; };
        StateChanged += (_, _) => { bool on = WindowState != WindowState.Minimized; _flow.IsEnabled = on; _animator.Run(on); };
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        Closing += (_, _) => { _closed = true; _generation++; };
        Closed += (_, _) =>
        {
            _flow.Stop();
            _animator.Clear();
            HoverCard.Hide();
            GalleryClient.RemoveFolder(_previewFolder);
        };
        MaxHeight = SystemParameters.WorkArea.Height;

        BuildFilters();
        BuildReasons();
        _flow.Start();
        if (ShowPanels()) Run(FirstLoad, quiet: true);
    }

    string T(string key) => _main.Text(key);

    // Like a dialog for this window only: the player and the other windows stay usable while it is open
    void OpenOver(Window dialog, Action closed)
    {
        IntPtr me = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableWindow(me, false);
        dialog.Closed += (_, _) =>
        {
            NativeMethods.EnableWindow(me, true);
            if (_closed) return;
            Activate();
            closed();
        };
        dialog.Show();
    }

    void RefreshCreate() => CreateButton.IsEnabled = !_busy && AcceptBox.IsChecked == true && UserNameBox.Text.Trim().Length > 0;

    bool ShowPanels()
    {
        bool signedIn = GalleryClient.HasAccount();
        SetupPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        MainPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInText.Text = signedIn ? string.Format(T("galSignedIn"), GalleryClient.UserName) : "";
        return signedIn;
    }

    void Say(string text, bool error = false)
    {
        MessageText.Text = text;
        MessageText.Foreground = error ? ErrorBrush : (Brush)FindResource("Dim");
        PublishMessage.Text = text;
        PublishMessage.Foreground = MessageText.Foreground;
        SetupMessage.Text = text.Length > 0 ? "\u2715 " + text : "";
        SetupMessage.Visibility = error && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // One thing at a time; every failure ends in a message on the page
    async void Run(Func<Task> work, bool quiet = false)
    {
        if (_busy) return;
        _busy = true;
        if (!quiet) Say("");
        RefreshCreate();
        try { await work(); }
        catch (GalleryError e)
        {
            Say(T(GalleryClient.ErrorKey(e.Code)), true);
            if (e.Code == "banned" && GalleryClient.HasAccount())
            {
                MainPanel.Visibility = Visibility.Visible;
                Cards.Children.Clear();
                FilterButton.IsEnabled = OpenCodeButton.IsEnabled = false;
            }
        }
        catch (LocalLimit e) { Say(e.Message, true); }
        catch (Exception e)
        {
            ErrorLog.Write("gallery", e);
            Say(T("galErrServer"), true);
        }
        _busy = false;
        RefreshCreate();
        if (_loadAfter && !_closed)
        {
            _loadAfter = false;
            if (MainPanel.Visibility == Visibility.Visible) Run(LoadPage, quiet: true);
        }
    }

    // The list again, now or as soon as what is running ends: a filter changed meanwhile isn't lost
    void LoadSoon(bool quiet = false)
    {
        if (_busy) _loadAfter = true;
        else Run(LoadPage, quiet);
    }

    async Task CreateAccount()
    {
        await GalleryClient.Register(UserNameBox.Text.Trim());
        if (ShowPanels()) await FirstLoad();
    }

    async Task FirstLoad()
    {
        await GalleryClient.RefreshName();      // the name on the server is the real one
        _main.ShowOwnerItems();
        SignedInText.Text = string.Format(T("galSignedIn"), GalleryClient.UserName);
        await _main.RemoveTakenDownDownloads();
        await LoadPage();
    }

    // ---------- filters ----------

    CheckBox Check(string text, Color? swatch, Action<bool> changed)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (swatch is { } c)
            content.Children.Add(new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(1),
            });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var box = new CheckBox { Content = content, Style = (Style)FindResource("Square"), Margin = new Thickness(0, 2, 12, 2) };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    TextBlock FilterHeading(string key) => new() { Text = T(key), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 0, 0, 4) };

    void BuildFilters()
    {
        SortList.Children.Add(FilterHeading("galSort"));
        foreach (var (key, text) in new[] { ("likes", "galMostLiked"), ("downloads", "galMostDownloaded"), ("new", "galNewest") })
        {
            CheckBox? box = null;
            box = Check(T(text), null, on =>
            {
                foreach (var other in _sortBoxes) if (other != box) other.IsChecked = false;
                _sort = on ? key : "new";
                FiltersChanged();
            });
            _sortBoxes.Add(box);
            SortList.Children.Add(box);
        }

        ShowList.Children.Add(FilterHeading("galShow"));
        foreach (var (key, text) in new[] { ("mine", _kind == "eq" ? "galMineSounds" : "galMineDesigns"), ("liked", "galLiked"), ("featured", "galFeatured") })
            ShowList.Children.Add(Check(T(text), null, on =>
            {
                if (on) _show.Add(key); else _show.Remove(key);
                FiltersChanged();
            }));

        if (_kind == "eq") return;      // a sound has no colours
        ColorColumn.Children.Add(FilterHeading("galColor"));
        var row = new WrapPanel();
        foreach (var (key, text, hex) in ColorChoices) row.Children.Add(ColorSwatch(key, T(text), hex));
        ColorColumn.Children.Add(row);
    }

    FrameworkElement ColorSwatch(string key, string name, string? hex)
    {
        var ring = new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3)), StrokeThickness = 2, Visibility = Visibility.Collapsed };
        var dot = new System.Windows.Shapes.Ellipse { Width = 22, Height = 22 };
        if (hex == null)
        {
            dot.Fill = (Brush)FindResource("Card");
            dot.Stroke = OptionsWindow.Rainbow;
            dot.StrokeThickness = 3;
        }
        else
        {
            var color = Designs.ParseColor(hex)!.Value;
            dot.Fill = new SolidColorBrush(color);
            if (color.R + color.G + color.B < 90)   // dark colours on the dark card
            {
                dot.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
                dot.StrokeThickness = 1;
            }
        }
        var swatch = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Children = { ring, dot } };
        HoverCard.Attach(swatch, name);
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            bool on = !_colors.Contains(key);
            if (on) _colors.Add(key); else _colors.Remove(key);
            ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            FiltersChanged();
        };
        return swatch;
    }

    void FiltersChanged()
    {
        int count = _show.Count + _colors.Count + (_sort == "new" ? 0 : 1);
        FilterCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterCountText.Text = count.ToString();
        _page = 0;
        LoadSoon();
    }

    // ---------- the cards ----------

    async Task LoadPage()
    {
        var page = await GalleryClient.Gallery(_kind, _sort, _show, _colors, _page);
        bool reviewed = page.ReportReviewed;
        if (page.Items.Count == 0 && _page > 0)     // the last page's last design went
        {
            _page = Math.Max(0, page.Pages - 1);
            page = await GalleryClient.Gallery(_kind, _sort, _show, _colors, _page);
            reviewed |= page.ReportReviewed;
        }
        _isAdmin = page.IsAdmin;
        ShowAllButton.Visibility = Visibility.Collapsed;
        ShowCards(page.Items);
        BuildPager(page.Pages);
        EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (reviewed) ShowToast(T("galReviewed"));
        if (_isAdmin)
        {
            FlagButton.Visibility = Visibility.Visible;
            var open = await GalleryClient.Reports(_kind);
            int count = _kind == "eq" ? open.Sounds : open.Players;
            FlagBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FlagCount.Text = count.ToString();
        }
    }

    void ShowToast(string text)
    {
        ToastText.Text = PublishToastText.Text = text;
        Toast.Visibility = PublishToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void AskDelete(FrameworkElement target, Func<Task> delete) =>
        GalleryUi.AskSure(this, target, T("galDeleteSure"), T("galYes"), T("galNo"), () => Run(delete));

    internal void Reload() { if (MainPanel.Visibility == Visibility.Visible) LoadSoon(quiet: true); }

    void BuildPager(int pages)
    {
        Pager.Children.Clear();
        if (pages < 2) return;
        for (int i = 0; i < pages; i++)
        {
            int page = i;
            bool on = page == _page;
            var button = new Button
            {
                Content = (page + 1).ToString(),
                Style = (Style)FindResource("Chip"),
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(11, 0, 11, 0),
                Background = new SolidColorBrush(on ? Color.FromRgb(0xE3, 0xE3, 0xE3) : Color.FromRgb(0x2D, 0x2D, 0x2D)),
                Foreground = new SolidColorBrush(on ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xE3, 0xE3, 0xE3)),
            };
            button.Click += (_, _) => { _page = page; LoadSoon(); };
            Pager.Children.Add(button);
        }
    }

    void ShowCards(List<GalleryItem> items)
    {
        _generation++;
        _animator.Clear();
        _previews.Clear();
        HoverCard.Hide();
        Cards.Children.Clear();
        foreach (var item in items) Cards.Children.Add(Card(item));
    }

    TextBlock Icon(string glyph, Brush brush, double size = 14) => new() { Text = glyph, FontFamily = _icons, FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };

    static Button Plain(UIElement content) => new()
    {
        Content = content,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(2),
        Margin = new Thickness(0, 0, 8, 0),
        Cursor = Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center,
        Template = PlainTemplate(),
    };

    static ControlTemplate PlainTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var back = new FrameworkElementFactory(typeof(Border));
        back.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        back.SetValue(Border.PaddingProperty, new Thickness(2));
        back.AppendChild(presenter);
        template.VisualTree = back;
        return template;
    }

    FrameworkElement Card(GalleryItem item)
    {
        var body = new StackPanel();
        body.Children.Add(item.Type == "eq" ? SoundPicture(item) : PlayerPicture(item));

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        title.Children.Add(new TextBlock { Text = item.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 400 });
        if (item.Visibility == "private")
        {
            var lockIcon = Icon("", Grey, 12);
            lockIcon.Margin = new Thickness(6, 0, 0, 0);
            HoverCard.Attach(lockIcon, T("galPrivateHint"));
            title.Children.Add(lockIcon);
        }
        body.Children.Add(title);
        body.Children.Add(new TextBlock
        {
            Text = item.MakerIsDeveloper ? $"{item.Maker} · {T("galDeveloper")}" : item.Maker,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 0),
            FontWeight = item.MakerIsDeveloper ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = item.MakerIsDeveloper ? DeveloperBrush : Grey,
        });
        if (item.Type == "eq") body.Children.Add(SoundTags(item));
        body.Children.Add(Meta(item));

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Margin = new Thickness(0, 0, 14, 14),
            BorderThickness = new Thickness(1),
            BorderBrush = item.Featured ? Orange : Brushes.Transparent,
            Child = body,
        };
        if (!item.Featured) return card;

        // the star at the top left of a featured design
        var star = new Border
        {
            Width = 28, Height = 28, Background = Orange, CornerRadius = new CornerRadius(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0),
            Child = new TextBlock { Text = "", FontFamily = _icons, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        HoverCard.Attach(star, T("galDevFeatured"));
        return new Grid { Children = { card, star } };
    }

    FrameworkElement PlayerPicture(GalleryItem item)
    {
        var preview = PlayerPreview.FromItem(item);
        _previews.Add(preview);
        _ = LoadPicture(item, preview, _generation);
        return preview;
    }

    // Only the small preview comes down until someone presses Save
    async Task LoadPicture(GalleryItem item, PlayerPreview preview, int generation)
    {
        if (_closed) return;
        try
        {
            var bytes = await GalleryClient.Preview(item.Id);
            if (bytes == null || generation != _generation) return;
            Directory.CreateDirectory(_previewFolder);
            string path = Path.Combine(_previewFolder, Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(path, bytes);
            var frames = await Stickers.LoadAsync(path, 700, 3_300_000);     // a 6 s preview at its full sharpness
            try { File.Delete(path); } catch { }
            if (_closed) GalleryClient.RemoveFolder(_previewFolder);
            if (frames == null || generation != _generation) return;
            preview.Picture.Source = frames.Frames[0];
            _animator.Add(preview.Picture, frames);
            _animator.Run(WindowState != WindowState.Minimized);
        }
        catch (IOException) { }
    }

    internal static double[] BandsOf(GalleryItem item)
    {
        if (!item.Spec.TryGetProperty("bands", out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
        return list.EnumerateArray().Select(b => b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0).ToArray();
    }

    FrameworkElement SoundPicture(GalleryItem item) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 9, 8, 9),
        Child = EqCurve.Make(BandsOf(item)),
    };

    FrameworkElement SoundTags(GalleryItem item)
    {
        double Num(string n) => item.Spec.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var panel = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        void Tag(string text) => panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 5, 0),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) },
        });
        if (Num("bass") > 0) Tag(string.Format(T("galTagBass"), Num("bass").ToString("0.#")));
        if (Num("reverb") > 0) Tag(string.Format(T("galTagReverb"), Num("reverb").ToString("0")));
        if (Num("enhancer") > 0) Tag(string.Format(T("galTagClarity"), Num("enhancer").ToString("0")));
        if (item.Spec.TryGetProperty("width", out _) && Num("width") != 100) Tag(string.Format(T("galTagWidth"), Num("width").ToString("0")));
        return panel;
    }

    FrameworkElement Meta(GalleryItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(LikeButton(item));
        var downloads = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        downloads.Children.Add(Icon("", Grey));
        downloads.Children.Add(new TextBlock { Text = item.Downloads.ToString(), FontSize = 12, Margin = new Thickness(5, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(downloads);
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);
        if (_isAdmin)
        {
            var star = Plain(Icon(item.Featured ? "" : "", DeveloperBrush));
            HoverCard.Attach(star, T("galFeature"));
            star.Click += (_, _) => Run(async () => { await GalleryClient.SetFeatured(item.Id, !item.Featured); await LoadPage(); });
            right.Children.Add(star);
        }
        if (_isAdmin && !item.Mine)
        {
            var hammer = Plain(GalleryUiParts.Hammer());
            HoverCard.Attach(hammer, T("galRemove"));
            hammer.Click += (_, _) => GalleryUi.AskModerate(this, hammer, T("galRemove"), T("galRemoveBan"), T("galDeleteSure"), T("galYes"), T("galNo"),
                action => Run(async () => { await GalleryClient.Moderate(item.Id, action); await LoadPage(); }));
            right.Children.Add(hammer);
        }
        if (item.Mine)
        {
            var copy = Plain(Icon("", Grey));
            HoverCard.Attach(copy, T("galCopy"));
            copy.Click += (_, _) => { try { Clipboard.SetText(item.Code); Say(T("galCopied")); } catch { } };
            right.Children.Add(copy);
            var delete = Plain(Icon("", ErrorBrush));
            HoverCard.Attach(delete, T("galDelete"));
            delete.Click += (_, _) => AskDelete(delete, async () => { await GalleryClient.Delete(item.Id); await LoadPage(); });
            right.Children.Add(delete);
        }
        else
        {
            var flag = Plain(Icon("", new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D))));
            HoverCard.Attach(flag, T("galReport"));
            flag.Click += (_, _) => OpenReport(item);
            right.Children.Add(flag);
        }
        right.Children.Add(SaveButton(item));
        grid.Children.Add(right);
        return grid;
    }

    Button LikeButton(GalleryItem item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var count = new TextBlock { FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var heart = new TextBlock { FontFamily = _icons, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(heart);
        panel.Children.Add(count);
        void Paint()
        {
            // the developer's like shows in its own colour; an own like fills the heart
            bool special = item.DevLiked;
            heart.Text = item.Liked || special ? "" : "";
            heart.Foreground = item.Liked || special ? Pink : Grey;
            count.Text = item.Likes.ToString();
            count.Foreground = special ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0xA3)) : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            count.FontWeight = special ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Paint();
        var button = Plain(panel);
        button.Margin = new Thickness(0, 0, 12, 0);
        if (item.DevLiked) HoverCard.Attach(button, T("galDevLiked"));
        button.Click += (_, _) => Run(async () =>
        {
            await GalleryClient.SetLike(item, !item.Liked);
            Paint();
        }, quiet: true);
        return button;
    }

    FrameworkElement SaveButton(GalleryItem item)
    {
        if (IsSaved(item)) return SavedMark();
        var host = new ContentControl();
        var button = new Button
        {
            Style = (Style)FindResource("Btn"),
            Background = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Padding = new Thickness(12, 0, 12, 0),
            MinHeight = 28,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "\uE896", FontFamily = _icons, FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = T("galSave"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) => Run(async () =>
        {
            await SaveItem(item);
            host.Content = SavedMark();
        });
        host.Content = button;
        return host;
    }

    FrameworkElement SavedMark()
    {
        var ok = new SolidColorBrush(Color.FromRgb(0x7F, 0xCE, 0x92));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Icon("", ok, 12));
        panel.Children.Add(new TextBlock { Text = T("galSaved"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ok, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    // ---------- with a code ----------

    async Task OpenCode()
    {
        string code = CodeBox.Text.Trim();
        if (code.Length == 0) return;
        var item = await GalleryClient.ByCode(code);
        if (item.Type != _kind)
        {
            Say(T(item.Type == "eq" ? "galCodeIsEq" : "galCodeIsPlayer"), true);
            return;
        }
        FilterPanel.Visibility = Visibility.Collapsed;
        ShowCards(new List<GalleryItem> { item });
        BuildPager(1);
        EmptyText.Visibility = Visibility.Collapsed;
        ShowAllButton.Visibility = Visibility.Visible;
    }

    // ---------- saving ----------

    bool IsSaved(GalleryItem item) => item.Type == "eq"
        ? Equalizer.Load().Saved.Any(s => s.GalleryId == item.Id)
        : _main.DesignStore.Players.Any(p => p.GalleryId == item.Id);

    async Task SaveItem(GalleryItem item)
    {
        if (IsSaved(item)) return;
        if (item.Type == "eq") SaveEq(item);
        else await SavePlayer(item);
        try { await GalleryClient.CountDownload(item.Id); } catch (GalleryError) { }
    }

    void SaveEq(GalleryItem item)
    {
        // an open equalizer window would write its own copy of the list over this one
        bool reopen = _main.CloseEqualizer();
        var settings = Equalizer.Load();
        if (settings.Saved.Count >= Equalizer.MaxSaved)
        {
            if (reopen) _main.OpenEqualizer();
            throw new LocalLimit(T("eqSavedFull"));
        }
        var s = item.Spec;
        double Num(string name, double fallback, double min, double max) =>
            s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), min, max) : fallback;
        var bands = BandsOf(item);
        if (bands.Length != Equalizer.Frequencies.Length)
        {
            if (reopen) _main.OpenEqualizer();
            throw new GalleryError("bad_spec");
        }

        string name = item.Name;
        for (int i = 2; settings.Saved.Any(x => x.Name == name); i++) name = $"{item.Name} {i}";
        settings.Saved.Add(new SavedEq
        {
            Name = name,
            GalleryId = item.Id,
            FromGallery = true,
            Bands = bands.Select(b => Math.Clamp(b, -15, 15)).ToArray(),
            Preamp = Num("preamp", 0, -15, 15),
            Bass = Num("bass", 0, 0, 15),
            Width = (int)Num("width", 100, 0, 200),
            Balance = (int)Num("balance", 0, -100, 100),
            Vocals = (int)Num("vocals", 0, 0, 100),
            Reverb = (int)Num("reverb", 0, 0, 100),
            Enhancer = (int)Num("enhancer", 0, 0, 100),
        });
        Equalizer.Save(settings);
        if (reopen) _main.OpenEqualizer();
        Activate();
    }

    async Task SavePlayer(GalleryItem item)
    {
        var designs = _main.DesignStore;
        if (designs.Players.Count >= Designs.MaxPlayers) throw new LocalLimit(T("maxPlayers"));
        var (folder, paths) = await GalleryClient.DownloadImages(item, p => Say(string.Format(T("galWorking"), p)));
        try
        {
            // nothing is awaited from here on, so the clean-up of unused pictures can't run in between
            var spec = PlayerFromServer(item, paths.Select(Designs.Adopt).ToList());
            string name = item.Name;
            for (int i = 2; designs.Players.Any(x => x.Name == name); i++) name = $"{item.Name} {i}";
            spec.Name = name;
            designs.Players.Add(spec);
            designs.Save();
            _main.DesignChanged();
            _main.PlayersChanged();
            Say("");
        }
        finally { GalleryClient.RemoveFolder(folder); }
    }

    static PlayerSpec PlayerFromServer(GalleryItem item, List<string> files)
    {
        var s = item.Spec;
        string? Colour(string name) =>
            s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Designs.ParseColor(v.GetString()) != null ? v.GetString() : null;
        int Whole(string name, int max) =>
            s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? Math.Clamp(n, 0, max) : 0;

        var spec = new PlayerSpec
        {
            GalleryId = item.Id,
            GalleryCode = item.Code,
            GalleryMaker = item.Maker,
            Bg = Colour("bg"),
            Buttons = Colour("buttons"),
            BgClear = Whole("bgClear", Designs.MaxClear),
            ButtonsClear = Whole("buttonsClear", Designs.MaxButtonsClear),
            CoverClear = Whole("coverClear", Designs.MaxClear),
            ClearApart = s.TryGetProperty("clearApart", out var apart) && apart.ValueKind == JsonValueKind.True,
        };
        if (!s.TryGetProperty("stickers", out var stickers) || stickers.ValueKind != JsonValueKind.Array) throw new GalleryError("bad_spec");
        foreach (var k in stickers.EnumerateArray().Take(Designs.MaxStickers))
        {
            if (!k.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Number || !image.TryGetInt32(out int index) || index < 0 || index >= files.Count)
                throw new GalleryError("bad_spec");
            double Pos(string name, double min, double max) => k.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), min, max) : 0;
            spec.Stickers.Add(new StickerSpec { File = files[index], X = Pos("x", -10000, 10000), Y = Pos("y", -10000, 10000), W = Pos("w", 0, 10000), H = Pos("h", 0, 10000), Angle = Pos("angle", -3600, 3600) });
        }
        return spec;
    }

    // ---------- publishing page ----------

    const double RowPicture = 46;      // the height of a player or a sound in the two lists

    async Task OpenPublishPage()
    {
        _generation++;
        _animator.Clear();
        _previews.Clear();
        FilterPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Collapsed;
        PublishPage.Visibility = Visibility.Visible;
        PageSigned.Text = SignedInText.Text;
        var mine = await RefreshMine();
        RefreshPublishList(mine);
    }

    // Something already published under the same name is updated instead of published again
    void RefreshPublishList(List<GalleryItem> mine)
    {
        GalleryItem? Published(string name) => mine.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        _publish.Rows.Clear();
        if (_kind == "eq")
            foreach (var s in Equalizer.Load().Saved.Where(s => !s.FromGallery))
                AddPublishRow(s.Name, EqCurve.Make(s.Bands, RowPicture), null, s, Published(s.Name));
        else
            foreach (var p in _main.DesignStore.Players.Where(p => p.GalleryId == null))
                AddPublishRow(p.Name, OwnPicture(p), p, null, Published(p.Name));
        _publish.Show();
    }

    FrameworkElement OwnPicture(PlayerSpec p)
    {
        var preview = new PlayerPreview(p.Bg, p.Buttons, p.BgClear, p.ButtonsClear, p.CoverClear) { HorizontalAlignment = HorizontalAlignment.Left, Height = RowPicture };
        _previews.Add(preview);
        _ = ShowOwnPicture(p, preview, _generation);
        return preview;
    }

    async Task ShowOwnPicture(PlayerSpec p, PlayerPreview preview, int generation)
    {
        try
        {
            var png = await GalleryPreview.MakeStill(p);
            if (generation == _generation) preview.Picture.Source = GalleryPreview.Read(png);
        }
        catch (Exception e) { ErrorLog.Write("gallery preview", e); }
    }

    TextBlock ListEmptyText() => new() { Text = T("galNone"), Foreground = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };

    void AddPublishRow(string name, FrameworkElement picture, PlayerSpec? player, SavedEq? sound, GalleryItem? published)
    {
        var button = new Button
        {
            Content = T(published != null ? "galUpdate" : "galPublish"),
            Style = (Style)FindResource("Btn"),
            Background = Orange,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
        };
        button.Click += (_, _) => AskPublish(player, sound, published);
        _publish.Rows.Add(Row(name, picture, button));
    }

    // a name, then public or private, in a small window; a private one shows its code there
    void AskPublish(PlayerSpec? player, SavedEq? sound, GalleryItem? published)
    {
        var dialog = new PublishWindow(_main, player, sound, published) { Owner = this, FlowDirection = FlowDirection };
        OpenOver(dialog, () =>
        {
            if (dialog.PublishedName == null) return;
            string notice = dialog.WasUpdate
                ? (_kind == "eq" ? "galUpdatedEq" : "galUpdatedPlayer")
                : (_kind == "eq" ? "galPublishedEq" : "galPublishedPlayer");
            ShowToast(string.Format(T(notice), dialog.PublishedName));
            Run(OpenPublishPage, quiet: true);
        });
    }

    // A row of the two lists: its name above its picture, its buttons on the right
    FrameworkElement Row(string title, FrameworkElement picture, params Button[] buttons)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3) });
        left.Children.Add(picture);
        grid.Children.Add(left);
        // two buttons go one above the other, so the picture keeps its width
        var row = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(row, 1);
        foreach (var b in buttons) { b.Margin = new Thickness(10, 2, 0, 2); row.Children.Add(b); }
        grid.Children.Add(row);
        return new Border { Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(10), Height = 86, Padding = new Thickness(12, 0, 8, 0), Margin = new Thickness(0, 0, 0, 6), Child = grid };
    }

    async Task<List<GalleryItem>> RefreshMine()
    {
        var items = (await GalleryClient.Mine()).Where(i => i.Type == _kind).ToList();
        _mine.Rows.Clear();
        foreach (var item in items)
        {
            var copy = new Button { Content = T("galCopy"), Style = (Style)FindResource("Btn") };
            copy.Click += (_, _) => { try { Clipboard.SetText(item.Code); Say(T("galCopied")); } catch { } };
            var delete = new Button { Content = T("galDelete"), Style = (Style)FindResource("Btn"), Foreground = ErrorBrush };
            delete.Click += (_, _) => AskDelete(delete, async () => { await GalleryClient.Delete(item.Id); await OpenPublishPage(); });
            FrameworkElement picture = item.Type == "eq" ? EqCurve.Make(BandsOf(item), RowPicture) : SmallPicture(item);
            string title = $"{item.Name}  ·  {item.Code}  ·  {string.Format(T("galDownloads"), item.Downloads)}";
            _mine.Rows.Add(Row(title, picture, copy, delete));
        }
        _mine.Show();
        return items;
    }

    FrameworkElement SmallPicture(GalleryItem item)
    {
        var picture = (PlayerPreview)PlayerPicture(item);
        picture.HorizontalAlignment = HorizontalAlignment.Left;
        picture.Height = RowPicture;
        return picture;
    }

    // Five rows a page, with page numbers under them when there is more than one
    sealed class PagedList(GalleryWindow window, StackPanel list, StackPanel pager)
    {
        const int PageSize = 5;
        int _page;
        public List<FrameworkElement> Rows { get; } = new();

        public void Show()
        {
            int pages = Math.Max(1, (Rows.Count + PageSize - 1) / PageSize);
            _page = Math.Min(_page, pages - 1);
            list.Children.Clear();
            foreach (var row in Rows.Skip(_page * PageSize).Take(PageSize)) list.Children.Add(row);
            if (Rows.Count == 0) list.Children.Add(window.ListEmptyText());
            pager.Children.Clear();
            if (pages < 2) return;
            for (int i = 0; i < pages; i++)
            {
                int page = i;
                bool on = page == _page;
                var button = new Button
                {
                    Content = (page + 1).ToString(),
                    Style = (Style)window.FindResource("Chip"),
                    Margin = new Thickness(3, 0, 3, 0),
                    Padding = new Thickness(11, 0, 11, 0),
                    Background = new SolidColorBrush(on ? Color.FromRgb(0xE3, 0xE3, 0xE3) : Color.FromRgb(0x2D, 0x2D, 0x2D)),
                    Foreground = new SolidColorBrush(on ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xE3, 0xE3, 0xE3)),
                };
                button.Click += (_, _) => { _page = page; Show(); };
                pager.Children.Add(button);
            }
        }
    }

    // ---------- reports ----------

    void BuildReasons()
    {
        foreach (var (key, text) in new[] { ("inappropriate", "galRInappropriate"), ("hate", "galRHate"), ("copyright", "galRCopyright"), ("spam", "galRSpam"), ("other", "galROther") })
        {
            var box = new RadioButton { Content = T(text), GroupName = "reason", Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += (_, _) => { _reason = key; ReportSend.IsEnabled = true; };
            ReasonList.Children.Add(box);
        }
    }

    void OpenReport(GalleryItem item)
    {
        _reporting = item;
        _reason = null;
        ReportSend.IsEnabled = false;
        foreach (RadioButton box in ReasonList.Children) box.IsChecked = false;
        ReportNote.Text = "";
        ReportTitle.Text = $"{T("galReport")}: {item.Name}";
        ReportPanel.Visibility = Visibility.Visible;
    }

    async Task SendReport()
    {
        if (_reporting == null || _reason == null) return;
        await GalleryClient.Report(_reporting.Id, _reason, ReportNote.Text.Trim());
        ReportPanel.Visibility = Visibility.Collapsed;
        Say(T("galReported"));
    }

    // A limit on this PC, with its text ready (no room for one more player or sound)
    sealed class LocalLimit(string text) : Exception(text);
}
