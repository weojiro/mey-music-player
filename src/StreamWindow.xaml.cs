using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarPlayerProto;

// The stream window (tray menu): share it on Discord instead of the whole screen. It shows the song with bars or a
// wave that move with the sound MMP plays, and while it is open the chosen apps play through MMP, so the stream
// carries that sound (with the equalizer, when it is on).
public partial class StreamWindow : Window
{
    static readonly SolidColorBrush ChipOff = new(Color.FromArgb(0x60, 0x2D, 0x2D, 0x2D));
    static readonly SolidColorBrush ChipOn = new(Color.FromRgb(0xE3, 0xE3, 0xE3));

    readonly MainWindow _main;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    BitmapSource? _cover;

    public StreamWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        BarsButton.Content = main.Text("streamBars");
        WaveButton.Content = main.Text("streamWave");
        NeedCable.Text = main.Text("streamNeedCable");
        Viz.Wave = main.StreamWave;
        ShowMode();

        BarsButton.Click += (_, _) => SetWave(false);
        WaveButton.Click += (_, _) => SetWave(true);
        MouseEnter += (_, _) => Modes.Opacity = 1;
        MouseLeave += (_, _) => Modes.Opacity = 0;
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        _timer.Tick += (_, _) => RefreshSong();
        CompositionTarget.Rendering += OnFrame;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            _timer.Stop();
            EqEngine.Streaming = false;
        };

        EqEngine.Streaming = true;
        RefreshSong();
        _timer.Start();
    }

    void SetWave(bool wave)
    {
        Viz.Wave = wave;
        _main.StreamWave = wave;
        ShowMode();
    }

    void ShowMode()
    {
        BarsButton.Background = Viz.Wave ? ChipOff : ChipOn;
        BarsButton.Foreground = Viz.Wave ? ChipOn : Brushes.Black;
        WaveButton.Background = Viz.Wave ? ChipOn : ChipOff;
        WaveButton.Foreground = Viz.Wave ? Brushes.Black : ChipOn;
    }

    // on every screen frame (the drawing is light); a frame WPF hands over twice is skipped
    TimeSpan _lastRender;
    void OnFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r && r.RenderingTime == _lastRender) return;
        if (e is RenderingEventArgs r2) _lastRender = r2.RenderingTime;
        Viz.Step();
    }

    static BitmapSource Tiny(BitmapSource cover)
    {
        var small = new TransformedBitmap(cover, new ScaleTransform(16.0 / cover.PixelWidth, 16.0 / cover.PixelHeight));
        small.Freeze();
        return small;
    }

    void RefreshSong()
    {
        var (title, artist, cover, position, length) = _main.NowPlaying();
        SongText.Text = title;
        ArtistText.Text = artist;
        if (!ReferenceEquals(cover, _cover))
        {
            _cover = cover;
            Backdrop.Source = cover == null ? null : Tiny(cover);
            CoverBox.Background = cover == null
                ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A))
                : new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
        }
        double part = length.TotalSeconds > 1 ? Math.Clamp(position.TotalSeconds / length.TotalSeconds, 0, 1) : 0;
        Progress.Width = 620 * part;
        TimeText.Text = length.TotalSeconds > 1 ? $"{Clock(position)} / {Clock(length)}" : "";
        NeedCable.Visibility = EqEngine.CableInstalled ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

// Bars (how loud each pitch is, bass on the left) or a wave (the sound itself), from what MMP just played
sealed class Visualizer : FrameworkElement
{
    const int Size = 2048, Bands = 48;
    const double MinHz = 40, MaxHz = 16000;
    static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    static readonly Pen WavePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), 4.5) { LineJoin = PenLineJoin.Round });

    readonly float[] _samples = new float[Size];
    readonly double[] _levels = new double[Bands];
    readonly Complex[] _fft = new Complex[Size];
    readonly double[] _window = Enumerable.Range(0, Size).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1))).ToArray();
    double _waveGain;
    readonly double[] _wave = new double[40], _target = new double[40];
    public bool Wave { get; set; }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // once per screen frame
    public void Step()
    {
        if (!EqEngine.ReadScope(_samples)) Array.Clear(_samples);
        if (!Wave) Analyse();
        InvalidateVisual();
    }

    void Analyse()
    {
        for (int i = 0; i < Size; i++) _fft[i] = new Complex(_samples[i] * _window[i], 0);
        Fft(_fft);
        double hzPerBin = EqEngine.ScopeRate / (double)Size;
        for (int b = 0; b < Bands; b++)
        {
            // bands evenly spread over the octaves, like hearing
            double lo = MinHz * Math.Pow(MaxHz / MinHz, b / (double)Bands), hi = MinHz * Math.Pow(MaxHz / MinHz, (b + 1) / (double)Bands);
            int from = Math.Max(1, (int)(lo / hzPerBin)), to = Math.Max(from + 1, (int)(hi / hzPerBin));
            double sum = 0;
            for (int k = from; k < to && k < Size / 2; k++) sum += _fft[k].Magnitude;
            double db = 20 * Math.Log10(sum / (to - from) / Size * 4 + 1e-9);
            // music has less energy the higher it goes (about 3 dB less per octave): made up for, so the bars on the
            // right move too
            db += 3 * Math.Log2(Math.Sqrt(lo * hi) / 1000);
            double level = Math.Clamp((db + 62) / 56, 0, 1);
            // up at once, down slowly
            _levels[b] = level > _levels[b] ? _levels[b] + (level - _levels[b]) * 0.55 : _levels[b] - Math.Min(0.03, _levels[b] - level);
        }
    }

    static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                var wn = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * wn;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    wn *= w;
                }
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        if (Wave) DrawWave(dc, w, h);
        else
        {
            double bw = w / Bands;
            for (int b = 0; b < Bands; b++)
            {
                double bh = Math.Max(2, _levels[b] * h * 0.92);
                dc.DrawRoundedRectangle(BarBrush, null, new Rect(b * bw + bw * 0.18, h - bh, bw * 0.64, bh), 2, 2);
            }
        }
    }

    // The wave: the sound's shape, but calm. The samples are averaged into a few points (the fine detail is what made
    // it look jittery), each point eases toward its new place, and a smooth curve runs through them.
    void DrawWave(DrawingContext dc, double w, double h)
    {
        int n = _wave.Length, per = Size / n;
        double peak = 0;
        for (int p = 0; p < n; p++)
        {
            double sum = 0;
            for (int i = p * per; i < (p + 1) * per; i++) sum += _samples[i];
            _target[p] = sum / per;
            peak = Math.Max(peak, Math.Abs(_target[p]));
        }
        // quiet songs still show a wave, loud ones don't leave the window
        double gain = peak > 0.0005 ? Math.Min(12, 0.9 / peak) : 1;
        _waveGain = _waveGain == 0 ? gain : _waveGain + (gain - _waveGain) * 0.03;
        // each point drifts only a little toward the new sound, and takes some of its neighbours' place: slow and soft
        for (int p = 0; p < n; p++)
        {
            double next = (_target[Math.Max(p - 1, 0)] + 2 * _target[p] + _target[Math.Min(p + 1, n - 1)]) / 4;
            _wave[p] += (next * _waveGain - _wave[p]) * 0.06;
        }

        double mid = h * 0.55, amp = h * 0.38;
        var pts = new Point[n];
        for (int p = 0; p < n; p++) pts[p] = new Point(w * p / (n - 1), mid - Math.Clamp(_wave[p], -1, 1) * amp);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, n - 1)];
                g.BezierTo(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                    new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6), p2, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, WavePen, geo);
    }
}
