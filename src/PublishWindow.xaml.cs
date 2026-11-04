using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarPlayerProto;

// Publishing a player or an equalizer sound: its look, its name, and public or only with the code
public partial class PublishWindow : Window
{
    static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Unselected = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    readonly MainWindow _main;
    readonly PlayerSpec? _player;
    readonly SavedEq? _sound;
    bool _private, _busy, _done;

    string? _replaces;      // an update of this published item

    public string? PublishedName { get; private set; }
    public bool WasUpdate => _replaces != null;

    internal PublishWindow(MainWindow main, PlayerSpec? player, SavedEq? sound, GalleryItem? updating = null)
    {
        InitializeComponent();
        _main = main;
        _player = player;
        _sound = sound;
        Title = T("galPublishBtn");
        NameLabel.Text = T("name");
        PublicTitle.Text = T("galPublicTitle");
        PublicHint.Text = T("galPublicHint");
        PrivateTitle.Text = T("galPrivateTitle");
        PrivateHint.Text = T("galPrivateHint");
        CancelButton.Content = T("galNo");
        PublishButton.Content = T("galPublish");
        CopyButton.Content = T("galCopyShort");
        NameBox.Text = player?.Name ?? sound?.Name ?? "";
        if (updating != null)
        {
            _replaces = updating.Id;
            _private = updating.Visibility == "private";
            NameBox.Text = updating.Name;
            PublishButton.Content = T("galUpdate");
        }

        PublicOption.MouseLeftButtonUp += (_, _) => { if (!_done) { _private = false; PaintOptions(); } };
        PrivateOption.MouseLeftButtonUp += (_, _) => { if (!_done) { _private = true; PaintOptions(); } };
        CancelButton.Click += (_, _) => Close();
        CopyButton.Click += (_, _) => { try { Clipboard.SetText(CodeText.Text); Say(T("galCopied")); } catch { } };
        PublishButton.Click += async (_, _) => await Publish();
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        Closing += (_, e) => e.Cancel = _busy;

        PaintOptions();
    }

    string T(string key) => _main.Text(key);

    void PaintOptions()
    {
        PublicOption.BorderBrush = _private ? Brushes.Transparent : Selected;
        PrivateOption.BorderBrush = _private ? Selected : Brushes.Transparent;
        PublicDot.Stroke = _private ? Unselected : Selected;
        PrivateDot.Stroke = _private ? Selected : Unselected;
        PublicDot.StrokeThickness = _private ? 2 : 5;
        PrivateDot.StrokeThickness = _private ? 5 : 2;
    }

    void Say(string text, bool error = false)
    {
        MessageText.Text = text;
        MessageText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A)) : (Brush)FindResource("Dim");
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task Publish()
    {
        if (_busy || _done) return;
        if (!GalleryClient.HasAccount())
        {
            Say(T("galNeedAccount"), true);
            return;
        }
        _busy = true;
        PublishButton.IsEnabled = CancelButton.IsEnabled = false;
        Say(string.Format(T("galWorking"), 0));
        try
        {
            string name = NameBox.Text.Trim();
            string code;
            if (_player != null)
            {
                byte[] preview = await GalleryPreview.Make(_player);
                code = await GalleryClient.PublishPlayer(_player, name, preview, _private, n => Say(string.Format(T("galWorking"), n)), _replaces);
            }
            else code = await GalleryClient.PublishEq(_sound!, name, _private, _replaces);

            _done = true;
            PublishedName = name;
            _main.GalleriesChanged();
            if (!_private)      // a public one needs no code: the gallery tells it's published
            {
                _busy = false;
                Close();
                return;
            }
            NameBox.IsReadOnly = true;
            PublishButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = T("close");
            CodeText.Text = code;
            CodeRow.Visibility = Visibility.Visible;
            Say(T("galDonePrivate"));
        }
        catch (GalleryError e) when (e.Code == "use_update" && e.SimilarId != null)
        {
            // already published in nearly the same form: pressing again updates that one
            _replaces = e.SimilarId;
            PublishButton.Content = T("galUpdate");
            Say(string.Format(T("galUpdateSimilar"), e.SimilarName), true);
        }
        catch (GalleryError e) { Say(T(GalleryClient.ErrorKey(e.Code)), true); }
        catch (Exception e)
        {
            ErrorLog.Write("gallery publish", e);
            Say(T("galErrServer"), true);
        }
        _busy = false;
        PublishButton.IsEnabled = CancelButton.IsEnabled = true;
    }
}
