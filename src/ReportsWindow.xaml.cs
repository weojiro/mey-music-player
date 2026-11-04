using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TaskbarPlayerProto;

// The reports, only on the owner's PC: the server checks the role on every request here
public partial class ReportsWindow : Window
{
    static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A));

    readonly MainWindow _main;
    readonly System.Windows.Threading.DispatcherTimer _flow = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly List<PlayerPreview> _previews = new();
    string _kind;
    int _generation;
    bool _busy;

    public ReportsWindow(MainWindow main, string kind)
    {
        InitializeComponent();
        _main = main;
        _kind = kind;
        Title = TitleText.Text = T("galReports");
        SubText.Text = T("galReportsSub");
        PlayersChip.Click += (_, _) => { if (_busy) return; _kind = "player"; Run(Load); };
        SoundsChip.Click += (_, _) => { if (_busy) return; _kind = "eq"; Run(Load); };
        _flow.Tick += (_, _) => { foreach (var p in _previews) p.Tick(0.1); };
        _flow.Start();
        Closed += (_, _) => _flow.Stop();
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        Run(Load);
    }

    string T(string key) => _main.Text(key);

    async void Run(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        MessageText.Text = "";
        try { await work(); }
        catch (GalleryError e) { MessageText.Text = T(GalleryClient.ErrorKey(e.Code)); }
        catch (Exception e)
        {
            ErrorLog.Write("gallery reports", e);
            MessageText.Text = T("galErrServer");
        }
        _busy = false;
    }

    async Task Load()
    {
        var data = await GalleryClient.Reports(_kind);
        _generation++;
        _previews.Clear();
        Paint(PlayersChip, T("galPlayerLabel"), data.Players, _kind != "eq");
        Paint(SoundsChip, T("galEqLabel"), data.Sounds, _kind == "eq");
        List.Children.Clear();
        foreach (var report in data.Reports) List.Children.Add(Row(report));
    }

    static void Paint(Button chip, string text, int count, bool on)
    {
        chip.Content = $"{text} ({count})";
        chip.Background = new SolidColorBrush(on ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x2B, 0x2B, 0x2B));
        chip.Foreground = new SolidColorBrush(on ? Color.FromRgb(0x1D, 0x1D, 0x1F) : Colors.White);
    }

    string Ago(long ms)
    {
        var passed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (passed.TotalMinutes < 1) return T("galAgoNow");
        if (passed.TotalHours < 1) return string.Format(T("galAgoMin"), (int)passed.TotalMinutes);
        if (passed.TotalDays < 1) return string.Format(T("galAgoHour"), (int)passed.TotalHours);
        return string.Format(T("galAgoDay"), (int)passed.TotalDays);
    }

    string ReasonText(string reason) => T(reason switch
    {
        "inappropriate" => "galRInappropriate",
        "hate" => "galRHate",
        "copyright" => "galRCopyright",
        "spam" => "galRSpam",
        _ => "galROther",
    });

    FrameworkElement Row(GalleryReport report)
    {
        var item = report.Item;
        FrameworkElement picture;
        if (item.Type == "eq") picture = EqCurve.Make(GalleryWindow.BandsOf(item), 46);
        else
        {
            var preview = PlayerPreview.FromItem(item);
            _previews.Add(preview);
            _ = ShowRealFiles(item, preview, _generation);
            picture = preview;
        }
        picture.VerticalAlignment = VerticalAlignment.Center;

        var dim = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run(item.Name) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new Run("  ·  " + string.Format(T("galMadeBy"), item.Maker)) { FontSize = 12, Foreground = dim });
        var why = new TextBlock { FontSize = 12, Foreground = dim, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        why.Inlines.Add(new Run(ReasonText(report.Reason)) { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) });
        why.Inlines.Add(new Run($"  ·  {string.Format(T("galReportBy"), report.Reporter)}  ·  {Ago(report.At)}"));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Children = { title, why } };
        if (report.Note.Length > 0)
            text.Children.Add(new TextBlock { Text = report.Note, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        var red = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x25));
        var ok = new Button { Content = T("galNoProblem"), Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0) };
        var remove = new Button { Content = T("galRemove"), Style = (Style)FindResource("Btn"), Background = red, Foreground = ErrorBrush, Margin = new Thickness(0, 0, 6, 0) };
        var ban = new Button { Content = T("galRemoveBan"), Style = (Style)FindResource("Btn"), Background = red, Foreground = ErrorBrush };
        ok.Click += (_, _) => Run(() => Decide(report, "ok"));
        remove.Click += (_, _) => GalleryUi.AskSure(this, remove, T("galDeleteSure"), T("galYes"), T("galNo"), () => Run(() => Decide(report, "remove")));
        ban.Click += (_, _) => GalleryUi.AskSure(this, ban, T("galDeleteSure"), T("galYes"), T("galNo"), () => Run(() => Decide(report, "ban")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { ok, remove, ban } };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(picture);
        grid.Children.Add(text);
        grid.Children.Add(actions);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), CornerRadius = new CornerRadius(10), Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 10), Child = grid };
    }

    async Task Decide(GalleryReport report, string action)
    {
        await GalleryClient.Decide(report.Id, action);
        await Load();
    }

    // What was really uploaded, not the preview its maker sent: a preview could hide a different picture
    async Task ShowRealFiles(GalleryItem item, PlayerPreview preview, int generation)
    {
        try
        {
            var (folder, paths) = await GalleryClient.DownloadImages(item, _ => { });
            try
            {
                var png = await GalleryPreview.FromFiles(item, paths);
                if (generation == _generation) preview.Picture.Source = GalleryPreview.Read(png);
            }
            finally { GalleryClient.RemoveFolder(folder); }
        }
        catch (GalleryError) { }
        catch (Exception e) { ErrorLog.Write("gallery report files", e); }
    }
}
