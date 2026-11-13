using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace TaskbarPlayerProto;

// A small card like the song's name on the cover: centred over its target, or under it when there is no room above
static class HoverCard
{
    static readonly Popup Card = new()
    {
        AllowsTransparency = true,
        IsHitTestVisible = false,
        Placement = PlacementMode.Custom,
        PopupAnimation = PopupAnimation.Fade,
        CustomPopupPlacementCallback = (popup, target, _) => new[]
        {
            new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - 6), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, target.Height + 6), PopupPrimaryAxis.Horizontal),
        },
    };

    public static void Show(FrameworkElement target, string text, Color? back = null, Color? fore = null)
    {
        var b = back ?? Color.FromRgb(0x2B, 0x2B, 0x2B);
        TextOptions.SetTextFormattingMode(Card, TextFormattingMode.Display);
        Card.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(b.R, b.G, b.B)),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 260,
            MinWidth = 90,
            Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fore ?? Colors.White),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Card.PlacementTarget = target;
        Card.IsOpen = true;
    }

    public static void Hide() => Card.IsOpen = false;

    public static void Attach(FrameworkElement target, string text)
    {
        target.MouseEnter += (_, _) => Show(target, text);
        target.MouseLeave += (_, _) => Hide();
    }
}

static class GalleryUi
{
    // The hammer on a card: Remove, or Remove and ban, then "are you sure", like a report
    public static void AskModerate(Window window, FrameworkElement target, string remove, string removeBan, string question, string yes, string no, Action<string> confirmed)
    {
        HoverCard.Hide();
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true };
        TextOptions.SetTextFormattingMode(popup, TextFormattingMode.Display);
        var red = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x25));
        var pink = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A));
        Button Red(string text) => new() { Content = text, Style = (Style)window.FindResource("Btn"), Background = red, Foreground = pink };
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10, 12, 10),
        };
        void Ask(string action)
        {
            var yesButton = Red(yes);
            yesButton.Margin = new Thickness(0, 0, 6, 0);
            var noButton = new Button { Content = no, Style = (Style)window.FindResource("Btn") };
            yesButton.Click += (_, _) => { popup.IsOpen = false; confirmed(action); };
            noButton.Click += (_, _) => popup.IsOpen = false;
            frame.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = question, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { yesButton, noButton } },
                },
            };
        }
        var removeButton = Red(remove);
        removeButton.Margin = new Thickness(0, 0, 6, 0);
        var banButton = Red(removeBan);
        removeButton.Click += (_, _) => Ask("remove");
        banButton.Click += (_, _) => Ask("ban");
        frame.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { removeButton, banButton } };
        popup.Child = frame;
        popup.IsOpen = true;
    }

    // "Are you sure?" in a small frame under the button, with Yes and Cancel
    public static void AskSure(Window window, FrameworkElement target, string question, string yes, string no, Action confirmed)
    {
        HoverCard.Hide();
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true };
        TextOptions.SetTextFormattingMode(popup, TextFormattingMode.Display);
        var yesButton = new Button { Content = yes, Style = (Style)window.FindResource("Btn"), Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x25)), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A)), Margin = new Thickness(0, 0, 6, 0) };
        var noButton = new Button { Content = no, Style = (Style)window.FindResource("Btn") };
        yesButton.Click += (_, _) => { popup.IsOpen = false; confirmed(); };
        noButton.Click += (_, _) => popup.IsOpen = false;
        popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = question, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { yesButton, noButton } },
                },
            },
        };
        popup.IsOpen = true;
    }
}

// A player in the gallery, laid out like the real one: the song flows, the bar can be dragged, the pictures play.
// Nothing here touches real music.
sealed class PlayerPreview : Viewbox
{
    const double SongSeconds = 184;

    public Image Picture { get; } = new() { Stretch = Stretch.Fill, Width = 338, Height = 40, IsHitTestVisible = false };

    readonly TextBlock _time = new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _play;
    readonly Border _track = new() { CornerRadius = new CornerRadius(2), Height = 4, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _fill = new() { CornerRadius = new CornerRadius(2), Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse _thumb = new() { Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _bar = new() { Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    double _progress = 0.2 + Random.Shared.NextDouble() * 0.5;
    bool _playing = true, _dragging;

    public static PlayerPreview FromItem(GalleryItem item)
    {
        var s = item.Spec;
        string? Str(string n) => s.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int Int(string n) => s.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? x : 0;
        return new PlayerPreview(Str("bg"), Str("buttons"), Int("bgClear"), Int("buttonsClear"), Int("coverClear"));
    }

    public PlayerPreview(string? bg, string? buttons, int bgClear, int buttonsClear, int coverClear)
    {
        Stretch = Stretch.Uniform;
        FlowDirection = FlowDirection.LeftToRight;
        // scaled shapes and letters stay sharp: no snapping to whole pixels, the best picture scaling
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.HighQuality);
        var back = Designs.ParseColor(bg) ?? Color.FromRgb(0x2B, 0x2B, 0x2B);
        var accent = Designs.ParseColor(buttons) ?? Colors.White;
        var brush = new SolidColorBrush(accent);
        var icons = new FontFamily(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");

        TextBlock Icon(string glyph, double size, double width, Thickness margin) => new()
        {
            Text = glyph, FontFamily = icons, FontSize = size, Width = width, Margin = margin, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Foreground = brush,
        };

        _play = Icon("", 15, 32, new Thickness(4, 0, 4, 0));
        _play.Background = Brushes.Transparent;
        _play.Cursor = Cursors.Hand;
        _play.MouseLeftButtonUp += (_, _) => { _playing = !_playing; _play.Text = _playing ? "" : ""; };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Icon("", 13, 28, default));
        left.Children.Add(_play);
        left.Children.Add(Icon("", 13, 28, default));

        _time.Foreground = brush;
        _track.Background = new SolidColorBrush(Color.FromArgb(0x45, accent.R, accent.G, accent.B));
        _fill.Background = brush;
        _thumb.Fill = brush;
        _bar.Children.Add(_track);
        _bar.Children.Add(_fill);
        _bar.Children.Add(_thumb);
        _bar.SizeChanged += (_, _) => Draw();
        _bar.MouseLeftButtonDown += (_, e) => { _dragging = true; _bar.CaptureMouse(); Seek(e); };
        _bar.MouseMove += (_, e) => { if (_dragging) Seek(e); };
        _bar.MouseLeftButtonUp += (_, _) => { _dragging = false; _bar.ReleaseMouseCapture(); };
        _bar.LostMouseCapture += (_, _) => _dragging = false;

        var controls = new Grid { Margin = new Thickness(10, 0, 10, 0), Opacity = (100 - Math.Clamp(buttonsClear, 0, 90)) / 100.0 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Grid.SetColumn(_time, 1);
        Grid.SetColumn(_bar, 2);
        foreach (UIElement el in new UIElement[] { left, _time, _bar }) controls.Children.Add(el);

        var pictureHost = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, 338, 40), 8, 8), Children = { Picture } };
        var panel = new Border
        {
            Width = 338, Height = 40, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb((byte)(255 * (100 - Math.Clamp(bgClear, 0, 100)) / 100), back.R, back.G, back.B)),
            Child = new Grid { Children = { pictureHost, controls } },
        };
        _cover = new Border
        {
            Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Opacity = (100 - Math.Clamp(coverClear, 0, 100)) / 100.0, Child = Logo(),
        };
        Child = new Grid { Width = 382, Height = 40, Children = { panel, _cover } };
        Draw();
    }

    readonly Border _cover;

    // the design's own cover picture in place of MMP's logo
    public void SetCover(ImageSource picture)
    {
        _cover.Child = null;
        _cover.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
    }

    // the image number of the design's cover picture, if it has one
    public static int? CoverIndex(GalleryItem item) =>
        item.Spec.TryGetProperty("cover", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            && n >= 0 && n < item.Images.Count ? n : null;

    // MMP's logo as the cover: a cream square, a black "m" and orange sound waves
    internal static FrameworkElement Logo()
    {
        var orange = new SolidColorBrush(Color.FromRgb(0xE4, 0x57, 0x2E));
        var canvas = new Canvas { Width = 40, Height = 40, Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xE1)), Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 8, 8) };
        var letter = new TextBlock { Text = "m", FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.ExtraBold, FontSize = 23, Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)) };
        Canvas.SetLeft(letter, 3);
        Canvas.SetTop(letter, 4);
        canvas.Children.Add(letter);
        foreach (string arc in new[] { "M28,16 A6,6 0 0 1 28,24", "M32,12 A11,11 0 0 1 32,28" })
            canvas.Children.Add(new Path { Data = Geometry.Parse(arc), Stroke = orange, StrokeThickness = 2.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return canvas;
    }

    void Seek(MouseEventArgs e)
    {
        if (_bar.ActualWidth <= 0) return;
        _progress = Math.Clamp(e.GetPosition(_bar).X / _bar.ActualWidth, 0, 1);
        Draw();
    }

    public void Tick(double seconds)
    {
        if (!_playing || _dragging) return;
        _progress += seconds / SongSeconds;
        if (_progress >= 1) _progress = 0;
        Draw();
    }

    void Draw()
    {
        int at = (int)(_progress * SongSeconds);
        _time.Text = $"{at / 60}:{at % 60:00}";
        double width = _bar.ActualWidth;
        _fill.Width = Math.Max(0, _progress * width);
        _thumb.Margin = new Thickness(Math.Max(0, _progress * width - 5), 0, 0, 0);
    }
}

// An equalizer sound drawn as its bands, like the bands in the equalizer window
static class EqCurve
{
    public static FrameworkElement Make(double[] bands, double height = 64)
    {
        const double W = 300, H = 64;
        var grid = new Grid { Height = height, ClipToBounds = true };
        if (bands.Length < 2) return grid;
        Point At(int i) => new(i * W / (bands.Length - 1), H / 2 - Math.Clamp(bands[i], -15, 15) * (H / 2 - 3) / 15);

        var line = new PathFigure { StartPoint = At(0) };
        for (int i = 1; i < bands.Length; i++)
        {
            Point a = At(i - 1), b = At(i);
            double mid = (a.X + b.X) / 2;
            line.Segments.Add(new BezierSegment(new Point(mid, a.Y), new Point(mid, b.Y), b, true));
        }
        var area = line.Clone();
        area.Segments.Add(new LineSegment(new Point(W, H / 2), false));
        area.Segments.Add(new LineSegment(new Point(0, H / 2), false));
        area.IsClosed = true;

        var light = Color.FromRgb(0xE6, 0xE6, 0xE6);
        var canvas = new Canvas { Width = W, Height = H };
        canvas.Children.Add(new Line { X1 = 0, X2 = W, Y1 = H / 2, Y2 = H / 2, Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), StrokeThickness = 1 });
        canvas.Children.Add(new Path { Data = new PathGeometry(new[] { area }), Fill = new SolidColorBrush(Color.FromArgb(0x33, light.R, light.G, light.B)) });
        canvas.Children.Add(new Path { Data = new PathGeometry(new[] { line }), Stroke = new SolidColorBrush(light), StrokeThickness = 2 });
        grid.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = canvas });
        return grid;
    }
}

// The picture a player shows in the gallery, made when it is published: only this travels until someone saves it
static class GalleryPreview
{
    const int W = 338, H = 40;
    const int MaxBytes = 5 * 1024 * 1024;
    const int MaxMs = 6000, StepMs = 100;

    static async Task<List<(StickerSpec Spec, StickerFrames Frames)>> Load(PlayerSpec p)
    {
        var loaded = new List<(StickerSpec Spec, StickerFrames Frames)>();
        foreach (var s in p.Stickers.Where(s => !s.Hidden))       // hidden pictures are not published
            if (await Stickers.LoadAsync(Designs.ImagePath(s.File), 700, 3_000_000) is { } frames)
                loaded.Add((s, frames));
        return loaded;
    }

    public static async Task<byte[]> MakeStill(PlayerSpec p) => Encode(new PngBitmapEncoder(), Render(await Load(p), 0, 2, null));

    // A player's look from its real, downloaded files, for the owner's report page
    public static async Task<byte[]> FromFiles(GalleryItem item, List<string> paths)
    {
        var loaded = new List<(StickerSpec Spec, StickerFrames Frames)>();
        if (item.Spec.TryGetProperty("stickers", out var stickers) && stickers.ValueKind == JsonValueKind.Array)
            foreach (var k in stickers.EnumerateArray())
            {
                if (!k.TryGetProperty("image", out var image) || !image.TryGetInt32(out int index) || index < 0 || index >= paths.Count) continue;
                double Num(string name) => k.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                if (await Stickers.LoadAsync(paths[index], 700, 3_000_000) is { } frames)
                    loaded.Add((new StickerSpec { X = Num("x"), Y = Num("y"), W = Num("w"), H = Num("h"), Angle = Num("angle") }, frames));
            }
        return Encode(new PngBitmapEncoder(), Render(loaded, 0, 2, null));
    }

    public static BitmapSource? Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.StreamSource = stream;
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.EndInit();
        picture.Freeze();
        return picture;
    }

    public static async Task<byte[]> Make(PlayerSpec p)
    {
        var loaded = await Load(p);
        if (loaded.Any(x => x.Frames.Animated))
        {
            var back = Designs.ParseColor(p.Bg) ?? Color.FromRgb(0x2B, 0x2B, 0x2B);
            int length = Math.Min(MaxMs, loaded.Max(x => x.Frames.Animated ? x.Frames.Ends[^1] : 0));
            foreach (double scale in new[] { 2.0, 1.0 })
            {
                var gif = await Animated(loaded, length, scale, Color.FromRgb(back.R, back.G, back.B));
                if (gif.Length <= MaxBytes) return gif;
            }
        }
        return Encode(new PngBitmapEncoder(), Render(loaded, 0, 2, null));
    }

    static async Task<byte[]> Animated(List<(StickerSpec Spec, StickerFrames Frames)> loaded, int length, double scale, Color back)
    {
        var encoder = new GifBitmapEncoder();
        for (int ms = 0; ms < length; ms += StepMs)
        {
            var frame = Render(loaded, ms, scale, back);
            var meta = new BitmapMetadata("gif");
            meta.SetQuery("/grctlext/Delay", (ushort)(StepMs / 10));
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(frame, PixelFormats.Indexed8, new BitmapPalette(frame, 256), 0), null, meta, null));
            await Task.Yield();       // the window stays alive while a long GIF is made
        }
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static BitmapSource Render(List<(StickerSpec Spec, StickerFrames Frames)> loaded, long ms, double scale, Color? back)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));
            if (back is { } fill) dc.DrawRectangle(new SolidColorBrush(fill), null, new Rect(0, 0, W, H));
            foreach (var (s, frames) in loaded)
            {
                dc.PushTransform(new RotateTransform(s.Angle, s.X, s.Y));
                dc.DrawImage(frames.FrameAt(ms), new Rect(s.X - s.W / 2, s.Y - s.H / 2, Math.Max(1, s.W), Math.Max(1, s.H)));
                dc.Pop();
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)(W * scale), (int)(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

static class GalleryUiParts
{
    // The four squares of the gallery buttons, drawn so they stay sharp
    public static UIElement GridLabel(string text)
    {
        var canvas = new Canvas { Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (x, y) in new[] { (1.0, 1.0), (9.5, 1.0), (1.0, 9.5), (9.5, 9.5) })
        {
            var square = new Rectangle { Width = 5.5, Height = 5.5, RadiusX = 1.3, RadiusY = 1.3, Stroke = Brushes.White, StrokeThickness = 1.6 };
            Canvas.SetLeft(square, x);
            Canvas.SetTop(square, y);
            canvas.Children.Add(square);
        }
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { canvas, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    // The owner's hammer on a card, in MMP's orange
    public static UIElement Hammer()
    {
        var orange = new SolidColorBrush(Color.FromRgb(0xE4, 0x57, 0x2E));
        var canvas = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M8.6,7.4 L2,14"), Stroke = orange, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M8.5,1.2 L14.8,7.5 L11.6,10.7 L5.3,4.4 Z"), Fill = orange });
        return canvas;
    }
}
