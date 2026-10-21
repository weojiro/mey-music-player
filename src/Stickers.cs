using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarPlayerProto;

// A decoded picture: one frame for PNG / JPG, every frame (already composited) for an animated GIF.
sealed class StickerFrames
{
    public BitmapSource[] Frames = Array.Empty<BitmapSource>();
    public int[] Ends = Array.Empty<int>();     // ms at which each frame ends
    public double Aspect = 1;                   // width / height

    public bool Animated => Frames.Length > 1;

    public BitmapSource FrameAt(long ms)
    {
        if (!Animated) return Frames[0];
        long t = ms % Ends[^1];
        int i = 0;
        while (Ends[i] <= t) i++;
        return Frames[i];
    }
}

static class Stickers
{
    const int MaxGifFrames = 300;

    // Decodes on a background thread. maxSide caps the longer side in pixels, budget caps the pixels of all
    // frames together, so even a big GIF stays small in memory.
    public static Task<StickerFrames?> LoadAsync(string path, int maxSide, long budget)
    {
        var done = new TaskCompletionSource<StickerFrames?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Load(path, maxSide, budget)); }
            catch { done.SetResult(null); }
            // rendering gave this thread a WPF dispatcher; shut it down or every load leaves one behind
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);       // WPF imaging and RenderTargetBitmap want STA
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    static StickerFrames? Load(string path, int maxSide, long budget)
    {
        var bytes = new MemoryStream(File.ReadAllBytes(path));
        var dec = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (dec.Frames.Count == 0) return null;
        bool gif = dec is GifBitmapDecoder && dec.Frames.Count > 1;
        int n = gif ? Math.Min(dec.Frames.Count, MaxGifFrames) : 1;

        var first = dec.Frames[0];
        int fw = first.PixelWidth, fh = first.PixelHeight;
        if (gif)
        {
            // the logical screen of the GIF; frames may be smaller and placed inside it
            fw = Query(dec.Metadata as BitmapMetadata, "/logscrdesc/Width", 0);
            fh = Query(dec.Metadata as BitmapMetadata, "/logscrdesc/Height", 0);
            if (fw <= 0 || fh <= 0)
                for (int i = 0; i < n; i++)
                {
                    var m = dec.Frames[i].Metadata as BitmapMetadata;
                    fw = Math.Max(fw, Query(m, "/imgdesc/Left", 0) + dec.Frames[i].PixelWidth);
                    fh = Math.Max(fh, Query(m, "/imgdesc/Top", 0) + dec.Frames[i].PixelHeight);
                }
        }
        if (fw <= 0 || fh <= 0) return null;

        double s = Math.Min(1, maxSide / (double)Math.Max(fw, fh));
        s = Math.Min(s, Math.Sqrt(budget / ((double)n * fw * fh)));
        int tw = Math.Max(1, (int)Math.Round(fw * s)), th = Math.Max(1, (int)Math.Round(fh * s));
        var result = new StickerFrames { Aspect = fw / (double)fh };

        if (!gif)
        {
            var one = Render(tw, th, dc => dc.DrawImage(first, new Rect(0, 0, tw, th)));
            result.Frames = new BitmapSource[] { one };
            result.Ends = new[] { 1 };
            return result;
        }

        // GIF frames are drawn over what came before; disposal 2 clears the frame's area, 3 restores the old picture
        var frames = new BitmapSource[n];
        var ends = new int[n];
        var full = new Rect(0, 0, fw, fh);
        BitmapSource canvas = Render(fw, fh, _ => { });
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            var f = dec.Frames[i];
            var m = f.Metadata as BitmapMetadata;
            var area = new Rect(Query(m, "/imgdesc/Left", 0), Query(m, "/imgdesc/Top", 0), f.PixelWidth, f.PixelHeight);
            int delay = Query(m, "/grctlext/Delay", 10) * 10;
            if (delay < 20) delay = 100;                    // what browsers do with 0 / 10 ms delays
            int disposal = Query(m, "/grctlext/Disposal", 0);

            var before = canvas;
            canvas = Render(fw, fh, dc => { dc.DrawImage(before, full); dc.DrawImage(f, area); });
            var drawn = canvas;
            frames[i] = Render(tw, th, dc => dc.DrawImage(drawn, new Rect(0, 0, tw, th)));
            total += delay;
            ends[i] = total;

            if (disposal == 2)
                canvas = Render(fw, fh, dc =>
                {
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(area)));
                    dc.DrawImage(drawn, full);
                    dc.Pop();
                });
            else if (disposal == 3)
                canvas = before;
        }
        result.Frames = frames;
        result.Ends = ends;
        return result;
    }

    static int Query(BitmapMetadata? m, string query, int fallback)
    {
        try { return m?.GetQuery(query) is object v ? Convert.ToInt32(v) : fallback; }
        catch { return fallback; }
    }

    static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // Puts a picture where its spec says: size, center and angle in design units
    public static void Place(FrameworkElement el, StickerSpec s)
    {
        el.Width = Math.Max(1, s.W);
        el.Height = Math.Max(1, s.H);
        Canvas.SetLeft(el, s.X - s.W / 2);
        Canvas.SetTop(el, s.Y - s.H / 2);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new RotateTransform(s.Angle);
    }
}

// Plays the GIFs of one picture layer with a single timer; the owner starts it only while the pictures can be seen.
sealed class StickerAnimator
{
    readonly List<(Image img, StickerFrames frames)> _items = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public StickerAnimator() => _timer.Tick += (_, _) => Tick();

    public bool HasAnimated => _items.Any(x => x.frames.Animated);

    public void Add(Image img, StickerFrames frames) => _items.Add((img, frames));

    public void Clear()
    {
        _items.Clear();
        _timer.Stop();
    }

    public void Run(bool on)
    {
        if (on && HasAnimated) { if (!_timer.IsEnabled) { Tick(); _timer.Start(); } }
        else if (_timer.IsEnabled) _timer.Stop();
    }

    void Tick()
    {
        long ms = _clock.ElapsedMilliseconds;
        foreach (var (img, frames) in _items)
        {
            if (!frames.Animated) continue;
            var f = frames.FrameAt(ms);
            if (!ReferenceEquals(img.Source, f)) img.Source = f;
        }
    }
}
