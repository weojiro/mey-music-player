using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskbarPlayerProto;

// The equalizer (tray menu): 18 bands, a preamp, presets and a few effects, played through Equalizer APO.
// Every change is heard at once. Without Equalizer APO the page shows what it needs and can install it.
public partial class EqualizerWindow : Window
{
    const double Column = 40, GraphH = 230, GraphTop = 22, GraphBottom = 24;     // the band graph, in pixels
    static readonly Color Light = Color.FromRgb(0xE3, 0xE3, 0xE3);
    static readonly Color Ink = Color.FromRgb(0x1F, 0x1F, 0x1F);
    static readonly Color ChipBack = Color.FromRgb(0x2D, 0x2D, 0x2D);
    static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));
    static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    readonly MainWindow _main;
    readonly EqSettings _eq = Equalizer.Load();
    readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _installTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    CancellationTokenSource? _download;
    bool _installed, _loading;
    int _busyTries;

    // the graph
    readonly Ellipse[] _dots = new Ellipse[Equalizer.Frequencies.Length];
    readonly TextBlock[] _values = new TextBlock[Equalizer.Frequencies.Length];
    readonly Path _curve = new() { Stroke = new SolidColorBrush(Light), StrokeThickness = 2, IsHitTestVisible = false };
    readonly Path _area = new() { Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xE3, 0xE3, 0xE3)), IsHitTestVisible = false };
    int _dragBand = -1;

    public EqualizerWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        Title = T("eq");
        TitleText.Text = T("eq");
        SubText.Text = T("eqSub");
        OnBox.Content = T("eqOn");
        NeedText.Text = T("eqNeed");
        StepsText.Text = T("eqSteps");
        InstallButton.Content = Equalizer.SetupAvailable ? T("eqInstall") : T("eqGet");
        PresetsText.Text = T("eqPresets");
        ResetButton.Content = T("reset");
        BandHint.Text = T("eqBandHint");
        PreampText.Text = T("eqPreamp");
        EffectsText.Text = T("eqEffects");
        BassLabel.Text = T("eqBassBoost");
        WidthLabel.Text = T("eqWidth");
        BalanceLabel.Text = T("eqBalance");
        VocalsLabel.Text = T("eqVocals");
        EffectsHint.Text = T("eqVocalsHint");
        DevicesText.Text = T("eqDevices");
        DevicesHint.Text = T("eqDevicesHint");
        DevicesButton.Content = T("eqChoose");
        WriteText.Text = T("eqNoWrite");
        WriteButton.Content = T("eqAllow");
        RestartText.Text = T("eqRestart");
        RestartButton.Content = T("eqRestartNow");

        BuildGraph();
        _applyTimer.Tick += (_, _) => { _applyTimer.Stop(); Apply(); };
        // while the setup or the device picker may be open: the page follows what they did
        _installTimer.Tick += (_, _) =>
        {
            if (Equalizer.Installed != _installed || Equalizer.NeedsRestart != (RestartCard.Visibility == Visibility.Visible)) RefreshInstalled();
            else ShowDevices();
        };

        OnBox.Checked += (_, _) => { if (!_loading) { _eq.On = true; Changed(); } };
        OnBox.Unchecked += (_, _) => { if (!_loading) { _eq.On = false; Changed(); } };
        ResetButton.Click += (_, _) => Reset();
        InstallButton.Click += (_, _) => Install();
        DevicesButton.Click += (_, _) => { if (Equalizer.OpenDevicePicker()) _installTimer.Start(); };
        RestartButton.Click += (_, _) => Equalizer.Restart();
        WriteButton.Click += (_, _) => { if (Equalizer.AllowWriting()) Apply(); };

        PreampSlider.ValueChanged += (_, _) => { if (_loading) return; _eq.Preamp = PreampSlider.Value; Changed(); };
        BassSlider.ValueChanged += (_, _) => { if (_loading) return; _eq.Bass = BassSlider.Value; Changed(); };
        WidthSlider.ValueChanged += (_, _) => { if (_loading) return; _eq.Width = (int)WidthSlider.Value; Changed(); };
        BalanceSlider.ValueChanged += (_, _) => { if (_loading) return; _eq.Balance = (int)BalanceSlider.Value; Changed(); };
        VocalsSlider.ValueChanged += (_, _) => { if (_loading) return; _eq.Vocals = (int)VocalsSlider.Value; Changed(); };
        // right-click: back to the default
        PreampSlider.MouseRightButtonUp += (_, _) => PreampSlider.Value = 0;
        BassSlider.MouseRightButtonUp += (_, _) => BassSlider.Value = 0;
        WidthSlider.MouseRightButtonUp += (_, _) => WidthSlider.Value = 100;
        BalanceSlider.MouseRightButtonUp += (_, _) => BalanceSlider.Value = 0;
        VocalsSlider.MouseRightButtonUp += (_, _) => VocalsSlider.Value = 0;

        MaxHeight = SystemParameters.WorkArea.Height;
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        Closed += (_, _) =>
        {
            _installTimer.Stop();
            _download?.Cancel();
            if (_applyTimer.IsEnabled) { _applyTimer.Stop(); Apply(); }
        };

        RefreshAll();
        RefreshInstalled();
    }

    string T(string key) => _main.Text(key);

    static string Db(double v) => (v > 0 ? "+" : "") + v.ToString("0.#", CultureInfo.CurrentCulture);

    // ---------- Equalizer APO there or not ----------

    void RefreshInstalled()
    {
        _installed = Equalizer.Installed;
        InstallCard.Visibility = _installed ? Visibility.Collapsed : Visibility.Visible;
        Controls.IsEnabled = OnBox.IsEnabled = _installed;
        Dim();
        RestartCard.Visibility = _installed && Equalizer.NeedsRestart ? Visibility.Visible : Visibility.Collapsed;
        ShowDevices();
        if (_installed) Apply();
        else _installTimer.Start();      // the setup may be running: the page comes alive once it is done
    }

    // the devices it already works on, so it is clear whether the device picker is needed at all
    void ShowDevices()
    {
        var devices = Equalizer.Devices();
        DevicesNow.Text = string.Format(T("eqDevicesNow"), string.Join(", ", devices));
        DevicesNow.Visibility = devices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Install()
    {
        if (!Equalizer.SetupAvailable)
        {
            // another kind of PC (ARM): Equalizer APO's own download page
            try { Process.Start(new ProcessStartInfo(Equalizer.FilesPage) { UseShellExecute = true }); } catch { }
            return;
        }
        InstallButton.IsEnabled = false;
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(p => InstallStatus.Text = string.Format(T("eqDownloading"), (int)(p * 100)));
        InstallStatus.Foreground = DimBrush;
        var result = await Equalizer.DownloadAndRunAsync(progress, _download.Token);
        if (!IsLoaded) return;
        switch (result)
        {
            case Equalizer.SetupResult.Started:
                InstallStatus.Text = T("eqSetupOpen");
                InstallButton.IsEnabled = true;
                break;
            case Equalizer.SetupResult.Declined:
                InstallStatus.Text = "";
                InstallButton.IsEnabled = true;
                break;
            default:
                InstallStatus.Text = T("eqDlFailed");
                InstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82));
                InstallButton.IsEnabled = true;
                break;
        }
    }

    // ---------- settings ----------

    void Changed()
    {
        Equalizer.Save(_eq);
        RefreshValues();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    void Apply()
    {
        if (!_installed) return;
        var result = Equalizer.Apply(_eq);
        if (result == Equalizer.ApplyResult.Busy && ++_busyTries <= 20) { _applyTimer.Start(); return; }    // once more in a moment
        _busyTries = 0;
        WriteCard.Visibility = result == Equalizer.ApplyResult.Denied ? Visibility.Visible : Visibility.Collapsed;
    }

    void Reset()
    {
        var fresh = new EqSettings { On = _eq.On };
        _eq.Bands = fresh.Bands;
        _eq.Preamp = fresh.Preamp;
        _eq.Bass = fresh.Bass;
        _eq.Width = fresh.Width;
        _eq.Balance = fresh.Balance;
        _eq.Vocals = fresh.Vocals;
        _eq.Preset = fresh.Preset;
        RefreshAll();
        Changed();
    }

    void UsePreset(string key, double[] bands)
    {
        _eq.Bands = (double[])bands.Clone();
        _eq.Preset = key;
        RefreshAll();
        Changed();
    }

    void RefreshAll()
    {
        _loading = true;
        OnBox.IsChecked = _eq.On;
        PreampSlider.Value = _eq.Preamp;
        BassSlider.Value = _eq.Bass;
        WidthSlider.Value = _eq.Width;
        BalanceSlider.Value = _eq.Balance;
        VocalsSlider.Value = _eq.Vocals;
        _loading = false;
        RefreshValues();
    }

    // without Equalizer APO, or switched off: the controls fade, so it shows that nothing changes the sound
    void Dim() => Controls.Opacity = !_installed ? 0.4 : _eq.On ? 1 : 0.45;

    void RefreshValues()
    {
        Dim();
        PreampValue.Text = Db(_eq.Preamp) + " dB";
        BassValue.Text = Db(_eq.Bass) + " dB";
        WidthValue.Text = _eq.Width + " %";
        BalanceValue.Text = _eq.Balance == 0 ? T("center")
            : (_eq.Balance < 0 ? T("left") : T("right")) + " " + Math.Abs(_eq.Balance) + " %";
        VocalsValue.Text = _eq.Vocals + " %";
        RefreshChips();
        RefreshGraph();
    }

    void RefreshChips()
    {
        PresetChips.Children.Clear();
        foreach (var (key, bands) in Equalizer.Presets) AddChip(T("eqP_" + key), _eq.Preset == key, () => UsePreset(key, bands));
        if (_eq.Preset == "custom") AddChip(T("eqP_custom"), true, null);
    }

    void AddChip(string text, bool on, Action? click)
    {
        var b = new Button
        {
            Content = text, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6),
            Background = new SolidColorBrush(on ? Light : ChipBack), Foreground = new SolidColorBrush(on ? Ink : Light)
        };
        if (click != null) b.Click += (_, _) => click();
        PresetChips.Children.Add(b);
    }

    // ---------- the band graph ----------

    static double Y(double gain) => GraphTop + (Equalizer.MaxGain - gain) / (2 * Equalizer.MaxGain) * GraphH;
    static double X(int band) => band * Column + Column / 2;

    static string Freq(int f) => f >= 1000 ? (f / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : f.ToString(CultureInfo.InvariantCulture);

    void BuildGraph()
    {
        int n = Equalizer.Frequencies.Length;
        Graph.Width = n * Column;
        Graph.Height = GraphTop + GraphH + GraphBottom;
        // a line every 5 dB
        foreach (double g in new[] { 15.0, 10, 5, 0, -5, -10, -15 })
        {
            var line = new Line
            {
                X1 = 0, X2 = n * Column, Y1 = Y(g), Y2 = Y(g), Stroke = GridBrush, StrokeThickness = 1, IsHitTestVisible = false,
                StrokeDashArray = g == 0 ? null : new DoubleCollection { 3, 3 }
            };
            Graph.Children.Add(line);
        }
        for (int i = 0; i < n; i++)
        {
            Graph.Children.Add(new Line { X1 = X(i), X2 = X(i), Y1 = Y(Equalizer.MaxGain), Y2 = Y(-Equalizer.MaxGain), Stroke = GridBrush, StrokeThickness = 1, IsHitTestVisible = false });
            var label = new TextBlock { Text = Freq(Equalizer.Frequencies[i]), FontSize = 11, Foreground = DimBrush, Width = Column, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            Canvas.SetLeft(label, i * Column);
            Canvas.SetTop(label, GraphTop + GraphH + 6);
            Graph.Children.Add(label);
        }
        Graph.Children.Add(_area);
        Graph.Children.Add(_curve);
        for (int i = 0; i < n; i++)
        {
            _values[i] = new TextBlock { FontSize = 11, Width = Column, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
            Canvas.SetLeft(_values[i], i * Column);
            Canvas.SetTop(_values[i], 0);
            Graph.Children.Add(_values[i]);
            _dots[i] = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Light), Stroke = new SolidColorBrush(Ink), StrokeThickness = 2, IsHitTestVisible = false };
            Graph.Children.Add(_dots[i]);
        }

        Graph.MouseLeftButtonDown += (_, e) =>
        {
            _dragBand = BandAt(e.GetPosition(Graph).X);
            Graph.CaptureMouse();
            SetBand(_dragBand, e.GetPosition(Graph).Y);
        };
        Graph.MouseMove += (_, e) => { if (_dragBand >= 0 && e.LeftButton == MouseButtonState.Pressed) SetBand(_dragBand, e.GetPosition(Graph).Y); };
        Graph.MouseLeftButtonUp += (_, _) => { _dragBand = -1; Graph.ReleaseMouseCapture(); };
        Graph.LostMouseCapture += (_, _) => _dragBand = -1;
        Graph.MouseRightButtonUp += (_, e) => SetGain(BandAt(e.GetPosition(Graph).X), 0);
        Graph.MouseWheel += (_, e) =>
        {
            int band = BandAt(e.GetPosition(Graph).X);
            SetGain(band, _eq.Bands[band] + (e.Delta > 0 ? 0.5 : -0.5));
            e.Handled = true;
        };
    }

    int BandAt(double x) => Math.Clamp((int)(x / Column), 0, Equalizer.Frequencies.Length - 1);

    void SetBand(int band, double y)
    {
        double gain = Equalizer.MaxGain - (y - GraphTop) / GraphH * 2 * Equalizer.MaxGain;
        SetGain(band, gain);
    }

    void SetGain(int band, double gain)
    {
        gain = Math.Clamp(Math.Round(gain * 2) / 2, -Equalizer.MaxGain, Equalizer.MaxGain);     // half a dB steps
        if (_eq.Bands[band] == gain) return;
        _eq.Bands[band] = gain;
        _eq.Preset = Equalizer.Presets.FirstOrDefault(p => p.Bands.SequenceEqual(_eq.Bands)).Key ?? "custom";
        Changed();
    }

    void RefreshGraph()
    {
        int n = Equalizer.Frequencies.Length;
        var points = new Point[n];
        for (int i = 0; i < n; i++)
        {
            points[i] = new Point(X(i), Y(_eq.Bands[i]));
            Canvas.SetLeft(_dots[i], points[i].X - 6);
            Canvas.SetTop(_dots[i], points[i].Y - 6);
            _values[i].Text = Db(_eq.Bands[i]);
            _values[i].Foreground = _eq.Bands[i] == 0 ? DimBrush : new SolidColorBrush(Light);
        }
        // a smooth line through the dots (Catmull-Rom as Bézier curves), and the area between it and 0 dB
        var line = new PathFigure { StartPoint = points[0] };
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = points[Math.Max(i - 1, 0)], p1 = points[i], p2 = points[i + 1], p3 = points[Math.Min(i + 2, n - 1)];
            line.Segments.Add(new BezierSegment(
                new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6), p2, true));
        }
        _curve.Data = new PathGeometry(new[] { line });
        var area = line.Clone();
        area.Segments.Add(new LineSegment(new Point(points[n - 1].X, Y(0)), false));
        area.Segments.Add(new LineSegment(new Point(points[0].X, Y(0)), false));
        area.IsClosed = true;
        _area.Data = new PathGeometry(new[] { area });
    }
}
