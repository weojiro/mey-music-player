using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Forms = System.Windows.Forms;
using static TaskbarPlayerProto.NativeMethods;

namespace TaskbarPlayerProto;

public partial class MainWindow : Window
{
    const int GapPx = 2;          // space above/below the cover inside the taskbar
    const int MarginPx = 8;       // space left/right of the cover
    const double DesignW = 382;   // full player width in design units (cover is 40 tall)
    const int AnimMs = 180;       // open/close animation length

    static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP");
    static readonly string DumpPath = Path.Combine(AppDataDir, "taskbar-dump.txt");
    static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.ini");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "mey music player";

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetClassName")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;

    enum Smtc { Props, Playback, Timeline }
    enum Side { Left, Right }   // which side the cover sits on; the player opens towards the other side

    // --- window / taskbar ---
    IntPtr _hwnd, _taskbar, _locHook, _fgHook;
    WinEventProc? _proc;          // kept in a field so the GC doesn't collect it
    DispatcherTimer? _poll;
    Window? _debug;
    readonly TextBlock _info = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Margin = new Thickness(12)
    };
    readonly DispatcherTimer _measureTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly List<(int L, int R)> _spans = new();   // taskbar items, screen pixels
    StructureChangedEventHandler? _structHandler;

    bool _shown, _usePoll, _measuring, _remeasure, _needMeasure = true;
    int _fullHeight, _gapX, _trayLeft, _retries, _screenW, _screenH;
    string _edge = "?";
    RECT _last;
    int _events, _zFixes, _enters, _leaves, _clicks, _hidWhileOver, _scans, _structEvents;
    string _scanInfo = "not scanned yet";
    bool _showDebug;

    // --- updates (GitHub releases) ---
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Window? _updateWin;
    Version? _dismissedVersion;
    string _updateStatus = "not checked yet";

    // --- position / drag / settings ---
    Side _side = Side.Left;
    int? _userX;                          // where the user dropped the cover (screen x); null = default spot
    string _lang = "en";
    bool _dragging, _dragMoved;
    int _dragStartCursorX, _dragStartCoverX, _dragX, _coverSize, _spansVersion, _posVersion, _pxVal;
    (int, int, int, int) _pxKey;
    readonly DispatcherTimer _zTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Window? _options;

    // --- tray menu ---
    readonly Forms.ToolStripMenuItem _miStartup = new(), _miOptions = new(), _miPosition = new(),
        _miRight = new(), _miLeft = new(), _miLanguage = new(), _miClose = new();
    readonly List<(string code, Forms.ToolStripMenuItem item)> _langItems = new();
    Forms.ContextMenuStrip? _menu;

    // --- mouse wheel over the tray icon ---
    IntPtr _wheelHook;
    LowLevelMouseProc? _wheelProc;
    POINT _trayPos;
    long _lastTrayHover;
    readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // --- colours: "chameleon" = taken from the cover ---
    static readonly Color DefaultBg = Color.FromRgb(0x2B, 0x2B, 0x2B);
    static readonly Color DefaultAccent = Color.FromRgb(0xFF, 0xFF, 0xFF);
    Brush _accentBrush = Brushes.White, _accentDimBrush = Brushes.Gray;
    string _paletteInfo = "default";

    // --- open/close animation ---
    bool _expanded, _animating;
    double _t, _animFrom, _animTo;      // _t: 0 = only the cover, 1 = full player
    readonly Stopwatch _animSw = new();

    // --- media ---
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    Brush? _defaultCoverBrush;
    bool _mediaDirty = true, _playing, _shuffleOn;
    Windows.Media.MediaPlaybackAutoRepeatMode _repeat = Windows.Media.MediaPlaybackAutoRepeatMode.None;
    TimeSpan _tlPos, _tlStart, _tlEnd;
    DateTimeOffset _tlUpdated;
    string _mediaStatus = "starting...", _title = "", _artist = "", _source = "";

    // --- seeking / scrubbing ---
    const int ThumbD = 10;                  // diameter of the circle on the seek bar (design units)
    bool _scrubbing;
    DateTime _seekHoldUntil;                // ignore stale timeline events right after a seek
    TimeSpan _pendingSeek;                  // offset from the start of the song
    readonly DispatcherTimer _seekCommitTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // --- volume of the app that is playing ---
    float _volume;                          // 0..1
    int _volTargets;                        // audio sessions found for the playing app
    string _volError = "";
    bool _muted;

    // --- tray icon ---
    System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        _usePoll = Environment.GetCommandLineArgs().Contains("--poll");
        _showDebug = Environment.GetCommandLineArgs().Contains("--debug");
        LoadSettings();
        ApplySideLayout();
        _zTimer.Tick += (_, _) => KeepOnTop();
        _wheelTimer.Tick += (_, _) => { if (Environment.TickCount64 - _lastTrayHover > 5000) StopWheelHook(); };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);   // first check 5 s after the start, then once a day
            CheckForUpdate();
        };
        _defaultCoverBrush = Cover.Background;

        Cover.MouseEnter += (_, _) => { _enters++; Refresh(); };
        Cover.MouseLeave += (_, _) => { _leaves++; Refresh(); };
        Cover.MouseLeftButtonDown += (_, e) =>
        {
            _clicks++;
            _dragMoved = false;
            _dragStartCursorX = (int)PointToScreen(e.GetPosition(this)).X;
            _dragStartCoverX = _gapX;
            Cover.CaptureMouse();      // a click opens/closes the player, a drag moves it along the taskbar
            Refresh();
        };
        Cover.MouseMove += (_, e) =>
        {
            if (!Cover.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            int dx = (int)PointToScreen(e.GetPosition(this)).X - _dragStartCursorX;
            if (!_dragMoved && Math.Abs(dx) < 5) return;       // small wobble = still a click
            _dragMoved = true;
            _dragging = true;
            _dragX = Math.Clamp(_dragStartCoverX + dx, _last.Left, Math.Max(_last.Left, TrayLeft - _coverSize));
            Update();
        };
        Cover.MouseLeftButtonUp += (_, _) =>
        {
            if (!Cover.IsMouseCaptured) return;
            Cover.ReleaseMouseCapture();
            if (_dragMoved) DropCover();
            else ToggleExpand();
        };
        Cover.LostMouseCapture += (_, _) => _dragging = false;
        _measureTimer.Tick += (_, _) => { _measureTimer.Stop(); RunMeasure(); };
        _progressTimer.Tick += (_, _) => UpdateProgressUi();
        _seekCommitTimer.Tick += (_, _) => { _seekCommitTimer.Stop(); CommitSeek(_pendingSeek); };
        RenderOptions.SetBitmapScalingMode(Cover, BitmapScalingMode.HighQuality);

        BtnPlay.Click += (_, _) => { _ = _session?.TryTogglePlayPauseAsync(); };
        BtnPrev.Click += (_, _) => { _ = _session?.TrySkipPreviousAsync(); };
        BtnNext.Click += (_, _) => { _ = _session?.TrySkipNextAsync(); };
        BtnMix.Click += (_, _) => { _ = _session?.TryChangeShuffleActiveAsync(!_shuffleOn); };
        BtnLoop.Click += (_, _) =>
        {
            var next = _repeat switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
            };
            _ = _session?.TryChangeAutoRepeatModeAsync(next);
        };
        SeekHit.MouseLeftButtonDown += (_, e) =>
        {
            if (_session == null || _tlEnd - _tlStart <= TimeSpan.Zero) return;
            _scrubbing = true;
            SeekHit.CaptureMouse();
            ScrubTo(e.GetPosition(SeekHit).X);
        };
        SeekHit.MouseMove += (_, e) => { if (_scrubbing) ScrubTo(e.GetPosition(SeekHit).X); };
        SeekHit.MouseLeftButtonUp += (_, e) =>
        {
            if (!_scrubbing) return;
            ScrubTo(e.GetPosition(SeekHit).X);
            _scrubbing = false;
            SeekHit.ReleaseMouseCapture();
            CommitSeek(_pendingSeek);
        };
        SeekHit.LostMouseCapture += (_, _) => _scrubbing = false;
        SeekHit.MouseWheel += (_, e) =>
        {
            SeekBy(TimeSpan.FromSeconds(e.Delta > 0 ? 5 : -5));
            e.Handled = true;
        };
        VolumeHit.MouseLeftButtonUp += (_, _) => ToggleMute();
        VolumeHit.MouseWheel += (_, e) =>
        {
            ChangeVolume(e.Delta > 0 ? 0.05f : -0.05f);
            e.Handled = true;
        };

        SetNoMedia();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Never take focus, and stay out of Alt+Tab.
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        _taskbar = FindWindow("Shell_TrayWnd", null);
        _proc = OnWinEvent;

        if (_taskbar != IntPtr.Zero)
        {
            GetWindowThreadProcessId(_taskbar, out uint pid);
            _locHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT);
            _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Re-measure the taskbar items whenever apps are opened/closed/pinned.
            _structHandler = (_, _) => Dispatcher.BeginInvoke(new Action(OnStructureChanged));
            Task.Run(() =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(_taskbar);
                    Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, _structHandler);
                }
                catch { /* UI Automation unavailable: the cover falls back to the taskbar's edge */ }
            });
        }

        if (_usePoll)
        {
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _poll.Tick += (_, _) => Update();
            _poll.Start();
        }

        if (_showDebug)
        {
            _debug = new Window
            {
                Title = "mey music player - debug (close me to quit)",
                Width = 540,
                Height = 640,
                Left = 20,
                Top = 120,
                Content = _info
            };
            _debug.Closed += (_, _) => Application.Current.Shutdown();
            _debug.Show();
        }

        CreateTray();
        InitMedia();
        _updateTimer.Start();
        Update();
    }

    // ------------------------------------------------------------------ taskbar

    bool IsFullScreenAppActive()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd || fg == _taskbar) return false;

            StringBuilder sb = new StringBuilder(256);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;

            // Normal pencerelerin hepsinde WS_CAPTION (Başlık çubuğu) bulunur.
            // Başlık çubuğu olan pencereler kesinlikle tam ekran oyun DEĞİLDİR.
            int style = GetWindowLong(fg, GWL_STYLE);
            if ((style & WS_CAPTION) == WS_CAPTION) return false;

            // Başlık çubuğu olmayan ve tüm ekranı kaplayan pencereler gerçek tam ekran oyun/videodur.
            if (GetWindowRect(fg, out RECT r) && _screenW > 0 && _screenH > 0)
            {
                if (r.Left <= 0 && r.Top <= 0 && r.Right >= _screenW && r.Bottom >= _screenH)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (hwnd != _taskbar || idObject != OBJID_WINDOW) return;
            _events++;
            Update();
        }
        else if (evt == EVENT_SYSTEM_FOREGROUND)
        {
            Update();
        }
    }

    void OnStructureChanged()
    {
        _structEvents++;
        if (!_shown) { _needMeasure = true; return; }  // measure when the taskbar comes back
        _measureTimer.Stop();
        _measureTimer.Start();                          // debounce bursts of changes
    }

    void Update()
    {
        if (_taskbar == IntPtr.Zero || !GetWindowRect(_taskbar, out RECT r)) return;
        _last = r;
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);

        bool wide = r.Width >= _screenW / 2;
        bool atBottom = wide && r.Top > _screenH / 2;
        bool atTop = wide && r.Bottom < _screenH / 2;
        _edge = atBottom ? "bottom" : atTop ? "top" : "side (not supported yet)";

        bool taskbarVisible = (atBottom && r.Top < _screenH - 3) || (atTop && r.Bottom > 3);
        bool visible = taskbarVisible && !IsFullScreenAppActive();

        // Remember the taskbar's full height from the moment it is completely on screen
        bool fullyOn = atBottom ? r.Bottom <= _screenH : atTop && r.Top >= 0;
        if (fullyOn) _fullHeight = r.Height;

        // Ensure player height never exceeds current taskbar height (fixes Win10 clipping)
        int currentTaskbarH = r.Height;
        int h = (_fullHeight > 0 && _fullHeight <= currentTaskbarH) ? _fullHeight : currentTaskbarH;
        int size = Math.Max(12, h - 2 * GapPx);

        if (visible && size >= 12)
        {
            if (!_shown)
            {
                if (_needMeasure) RunMeasure();
                if (_mediaDirty) { _mediaDirty = false; RefreshMedia(); }   // never show stale info
            }

            // The content is designed 40 units tall; scale it to the taskbar.
            double f = size / 40.0;
            double s = f / VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(Scale.ScaleX - s) > 0.001) { Scale.ScaleX = s; Scale.ScaleY = s; }
            int fullW = (int)Math.Round(DesignW * f);
            int width = size + (int)Math.Round((fullW - size) * _t);

            _coverSize = size;
            int restX = PlaceX(size, r.Left);
            int coverX = _dragging ? _dragX
                : _t > 0 ? restX + (int)Math.Round((OpenX(restX, size, r.Left) - restX) * _t)
                : restX;
            _gapX = coverX;
            int winX = _side == Side.Left ? coverX : coverX + size - width;
            int winY = r.Top + (r.Height - size) / 2;  // Vertical centering inside taskbar

            SetWindowPos(_hwnd, HWND_TOPMOST, winX, winY, width, size,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            bool was = _shown;
            _shown = true;
            if (!was) UpdateTimers();
        }
        else if (_shown)
        {
            if (Cover.IsMouseOver) _hidWhileOver++;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            _shown = false;
            UpdateTimers();   // nothing runs while the taskbar is hidden
        }

        Refresh();
    }

    int TrayLeft => _trayLeft > 0 ? _trayLeft : (int)(_screenW * 0.75);

    int FullWidth(int size) => (int)Math.Round(DesignW * size / 40.0);

    int PlaceX(int size, int taskbarLeft)
    {
        var key = (size, taskbarLeft, _spansVersion, _posVersion);
        if (key == _pxKey) return _pxVal;
        int wanted = _userX ?? (_side == Side.Left ? taskbarLeft - 100000 : _screenW + 100000);
        int x = SnapToFree(wanted, size, taskbarLeft, size);
        if (x < 0) x = _side == Side.Left ? taskbarLeft + MarginPx : TrayLeft - MarginPx - size;
        _pxKey = key;
        _pxVal = x;
        return x;
    }

    int OpenX(int restX, int size, int taskbarLeft)
    {
        int x = SnapToFree(restX, size, taskbarLeft, FullWidth(size));
        return x >= 0 ? x : restX;
    }

    List<(int a, int b)> FreeGaps(int taskbarLeft)
    {
        var free = new List<(int a, int b)>();
        int cursor = taskbarLeft;
        foreach (var (l, rr) in _spans.OrderBy(s => s.L))
        {
            if (l > cursor) free.Add((cursor, l));
            cursor = Math.Max(cursor, rr);
        }
        if (TrayLeft > cursor) free.Add((cursor, TrayLeft));
        return free;
    }

    int SnapToFree(int desiredX, int size, int taskbarLeft, int need)
    {
        int best = -1, bestDist = int.MaxValue;
        foreach (var (a, b) in FreeGaps(taskbarLeft))
        {
            int lo = _side == Side.Left ? a + MarginPx : a + MarginPx + need - size;
            int hi = _side == Side.Left ? b - MarginPx - need : b - MarginPx - size;
            if (hi < lo) continue;
            int x = Math.Clamp(desiredX, lo, hi);
            int dist = Math.Abs(x - desiredX);
            if (dist < bestDist) { best = x; bestDist = dist; }
        }
        return best;
    }

    void DropCover()
    {
        _dragging = false;
        int x = SnapToFree(_dragX, _coverSize, _last.Left, _coverSize);
        if (x >= 0) _userX = x;
        _posVersion++;
        SaveSettings();
        Update();
    }

    void ApplySideLayout()
    {
        bool left = _side == Side.Left;
        Content.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Content.RenderTransformOrigin = new Point(left ? 0 : 1, 0);
        Cover.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PlayerPanel.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PlayerPanel.Margin = left ? new Thickness(44, 0, 0, 0) : new Thickness(0, 0, 44, 0);
    }

    void KeepOnTop()
    {
        if (!_shown) return;
        if (IsFullScreenAppActive())
        {
            Update();
            return;
        }
        IntPtr w = NativeMethods.GetWindow(_hwnd, GW_HWNDPREV);
        for (int guard = 0; w != IntPtr.Zero && guard < 300; guard++)
        {
            if (w == _taskbar)
            {
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _zFixes++;
                Refresh();
                return;
            }
            w = NativeMethods.GetWindow(w, GW_HWNDPREV);
        }
    }

    void RunMeasure()
    {
        if (_taskbar == IntPtr.Zero) return;
        if (_measuring) { _remeasure = true; return; }
        _measuring = true;

        IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int trayLeft = tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr)
            ? tr.Left
            : (int)(GetSystemMetrics(SM_CXSCREEN) * 0.75);

        Task.Run(() => Scan(trayLeft)).ContinueWith(t =>
            Dispatcher.BeginInvoke(new Action(() => ApplyScan(t, trayLeft))));
    }

    static bool IsItemType(ControlType t) =>
        t == ControlType.Button || t == ControlType.SplitButton || t == ControlType.Edit ||
        t == ControlType.ListItem || t == ControlType.ComboBox;

    (List<(int L, int R)> spans, string dump) Scan(int trayLeft)
    {
        var spans = new List<(int L, int R)>();
        var sb = new StringBuilder();
        sb.AppendLine($"tray left: {trayLeft}   (lines starting with * count as taskbar items)");
        try
        {
            var root = AutomationElement.FromHandle(_taskbar);
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                try
                {
                    var info = el.Current;
                    var rc = info.BoundingRectangle;
                    if (rc.IsEmpty || rc.Width <= 0) continue;
                    int l = (int)rc.Left, rr = (int)Math.Ceiling(rc.Right);
                    bool counted = IsItemType(info.ControlType) && rr <= trayLeft;
                    sb.AppendLine($"{(counted ? "*" : " ")} {info.ControlType.ProgrammaticName,-24} L{l} R{rr} T{(int)rc.Top} B{(int)rc.Bottom}   id='{info.AutomationId}' class='{info.ClassName}' name='{info.Name}'");
                    if (counted) spans.Add((l, rr));
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (Exception ex) { sb.AppendLine("scan failed: " + ex.Message); }
        return (spans, sb.ToString());
    }

    void ApplyScan(Task<(List<(int L, int R)> spans, string dump)> t, int trayLeft)
    {
        _measuring = false;
        _scans++;
        _trayLeft = trayLeft;
        _spansVersion++;

        var (spans, dump) = t.IsCompletedSuccessfully
            ? t.Result
            : (new List<(int L, int R)>(), "scan task failed");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath)!);
            File.WriteAllText(DumpPath, dump);
        }
        catch { }

        if (spans.Count > 0)
        {
            _spans.Clear();
            _spans.AddRange(spans);
            _needMeasure = false;
            _retries = 0;
        }
        else
        {
            _needMeasure = true;
            if (_shown && _retries++ < 5) _measureTimer.Start();
        }

        _scanInfo = $"{spans.Count} items";
        if (_remeasure) { _remeasure = false; RunMeasure(); }
        Update();
    }

    // ------------------------------------------------------------ open / close

    void ToggleExpand()
    {
        if (_expanded && _gapX != _pxVal)
        {
            _userX = _gapX;
            _posVersion++;
            SaveSettings();
        }
        _expanded = !_expanded;
        _animFrom = _t;
        _animTo = _expanded ? 1 : 0;
        _animSw.Restart();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRender;
        }
        UpdateTimers();
        if (_expanded) { UpdateProgressUi(); ReadVolume(); }
    }

    void OnRender(object? sender, EventArgs e)
    {
        double p = Math.Min(1, _animSw.Elapsed.TotalMilliseconds / AnimMs);
        double ease = 1 - Math.Pow(1 - p, 3);
        _t = _animFrom + (_animTo - _animFrom) * ease;
        Update();
        if (p >= 1)
        {
            CompositionTarget.Rendering -= OnRender;
            _animating = false;
        }
    }

    void UpdateTimers()
    {
        bool need = _shown && _expanded && _playing;
        if (need && !_progressTimer.IsEnabled) _progressTimer.Start();
        else if (!need && _progressTimer.IsEnabled) _progressTimer.Stop();

        if (_shown && !_zTimer.IsEnabled) _zTimer.Start();
        else if (!_shown && _zTimer.IsEnabled) _zTimer.Stop();
    }

    // ------------------------------------------------------------------- media

    async void InitMedia()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(OnSessionChanged));
            OnSessionChanged();
        }
        catch (Exception ex)
        {
            _mediaStatus = "media API unavailable: " + ex.Message;
            Refresh();
        }
    }

    void OnSessionChanged()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProps;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = _mgr?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProps;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _mediaDirty = true;
        if (_shown) { _mediaDirty = false; RefreshMedia(); }
        else if (_session == null) SetNoMedia();
    }

    void OnMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) =>
        Dispatcher.BeginInvoke(new Action(() => OnSmtc(Smtc.Props)));
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) =>
        Dispatcher.BeginInvoke(new Action(() => OnSmtc(Smtc.Playback)));
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) =>
        Dispatcher.BeginInvoke(new Action(() => OnSmtc(Smtc.Timeline)));

    void OnSmtc(Smtc kind)
    {
        if (!_shown) { _mediaDirty = true; return; }
        var s = _session;
        if (s == null) return;
        try
        {
            switch (kind)
            {
                case Smtc.Props: RefreshMedia(); break;
                case Smtc.Playback:
                    ApplyPlayback(s.GetPlaybackInfo());
                    if (_playing && _volTargets == 0 && _expanded) ReadVolume();
                    break;
                case Smtc.Timeline: ApplyTimeline(s.GetTimelineProperties()); break;
            }
        }
        catch (Exception ex) { _mediaStatus = "media error: " + ex.Message; }
        Refresh();
    }

    async void RefreshMedia()
    {
        var s = _session;
        if (s == null) { SetNoMedia(); Refresh(); return; }
        try
        {
            ApplyPlayback(s.GetPlaybackInfo());
            ApplyTimeline(s.GetTimelineProperties());
            var props = await s.TryGetMediaPropertiesAsync();
            if (s != _session) return;
            _title = props.Title ?? "";
            _artist = props.Artist ?? "";
            _source = s.SourceAppUserModelId ?? "";
            ReadVolume();
            var cover = await LoadThumbAsync(props);
            if (s != _session) return;
            ApplyCover(cover);
            _mediaStatus = "ok";
        }
        catch (Exception ex) { _mediaStatus = "media error: " + ex.Message; }
        Refresh();
    }

    static async Task<BitmapImage?> LoadThumbAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        var thumb = props.Thumbnail;
        if (thumb == null) return null;
        using var ras = await thumb.OpenReadAsync();
        using var ms = new MemoryStream();
        await ras.AsStreamForRead().CopyToAsync(ms);
        if (ms.Length == 0) return null;
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 256;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    void ApplyCover(BitmapImage? cover)
    {
        if (cover != null)
        {
            Cover.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            Glyph.Visibility = Visibility.Collapsed;
            var (bg, accent) = ExtractPalette(cover);
            ApplyPalette(bg, accent);
        }
        else
        {
            Cover.Background = _defaultCoverBrush;
            Glyph.Visibility = Visibility.Visible;
            ApplyPalette(DefaultBg, DefaultAccent);
        }
    }

    void ApplyPalette(Color bg, Color accent)
    {
        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        _accentBrush = Frozen(accent);
        _accentDimBrush = Frozen(Color.FromArgb(0x80, accent.R, accent.G, accent.B));

        PlayerPanel.Background = Frozen(bg);
        BtnPrev.Foreground = BtnPlay.Foreground = BtnNext.Foreground = _accentBrush;
        VolumeGlyph.Foreground = _accentBrush;
        SeekFill.Background = _accentBrush;
        SeekThumb.Fill = _accentBrush;
        SeekTrack.Background = Frozen(Color.FromArgb(0x44, accent.R, accent.G, accent.B));
        TimeText.Foreground = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        UpdateToggleColors();
        _paletteInfo = $"background {Hex(bg)}   buttons {Hex(accent)}";
    }

    void UpdateToggleColors()
    {
        BtnMix.Foreground = _shuffleOn ? _accentBrush : _accentDimBrush;
        BtnLoop.Foreground = _repeat != Windows.Media.MediaPlaybackAutoRepeatMode.None ? _accentBrush : _accentDimBrush;
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static (Color bg, Color accent) ExtractPalette(BitmapSource src)
    {
        var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        fmt.CopyPixels(px, stride, 0);

        int step = Math.Max(1, Math.Min(w, h) / 48);
        double sumR = 0, sumG = 0, sumB = 0;
        int n = 0;
        var binW = new double[12];
        var binR = new double[12];
        var binG = new double[12];
        var binB = new double[12];
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                int i = y * stride + x * 4;
                if (px[i + 3] < 128) continue;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                n++;
                sumR += r; sumG += g; sumB += b;
                var (hue, sat, lig) = ToHsl(r, g, b);
                if (sat > 0.25 && lig > 0.15 && lig < 0.85)
                {
                    int bin = (int)(hue / 30) % 12;
                    double wt = sat * (1 - Math.Abs(2 * lig - 1));
                    binW[bin] += wt; binR[bin] += r * wt; binG[bin] += g * wt; binB[bin] += b * wt;
                }
            }
        }
        if (n == 0) return (DefaultBg, DefaultAccent);

        var (ah, asat, alig) = ToHsl((byte)(sumR / n), (byte)(sumG / n), (byte)(sumB / n));
        var bg = FromHsl(ah, Math.Min(asat, 0.45), Math.Clamp(alig * 0.45, 0.10, 0.22));

        int best = 0;
        for (int k = 1; k < 12; k++) if (binW[k] > binW[best]) best = k;

        Color accent;
        if (binW[best] < n * 0.02)
        {
            accent = Color.FromRgb(0xF0, 0xF0, 0xF0);
        }
        else
        {
            var (hh, ss, _) = ToHsl((byte)(binR[best] / binW[best]), (byte)(binG[best] / binW[best]), (byte)(binB[best] / binW[best]));
            accent = FromHsl(hh, Math.Clamp(ss, 0.5, 0.95), 0.68);
        }
        return (bg, accent);
    }

    static (double h, double s, double l) ToHsl(byte rb, byte gb, byte bb)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min, h = 0, s = 0;
        if (d > 1e-9)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    void ApplyPlayback(GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
    {
        var c = info.Controls;
        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _shuffleOn = info.IsShuffleActive == true;
        _repeat = info.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;

        BtnPlay.Content = _playing ? "\uE769" : "\uE768";
        BtnPlay.IsEnabled = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
        BtnPrev.IsEnabled = c.IsPreviousEnabled;
        BtnNext.IsEnabled = c.IsNextEnabled;
        BtnMix.IsEnabled = c.IsShuffleEnabled;
        BtnLoop.IsEnabled = c.IsRepeatEnabled;
        UpdateToggleColors();
        BtnLoop.Content = _repeat == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
        UpdateTimers();
    }

    void ApplyTimeline(GlobalSystemMediaTransportControlsSessionTimelineProperties tl)
    {
        _tlStart = tl.StartTime;
        _tlEnd = tl.EndTime;
        if (_scrubbing || DateTime.UtcNow < _seekHoldUntil)
        {
            UpdateProgressUi();
            return;
        }
        _tlPos = tl.Position;
        _tlUpdated = tl.LastUpdatedTime;
        UpdateProgressUi();
    }

    TimeSpan CurrentPosition()
    {
        var pos = _tlPos;
        if (_playing && !_scrubbing && _tlUpdated.Year > 2000) pos += DateTimeOffset.UtcNow - _tlUpdated;
        var dur = _tlEnd - _tlStart;
        if (pos > dur) pos = dur;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        return pos;
    }

    void UpdateProgressUi()
    {
        var dur = _tlEnd - _tlStart;
        var pos = CurrentPosition();

        TimeText.Text = pos.TotalHours >= 1 ? pos.ToString(@"h\:mm\:ss") : pos.ToString(@"m\:ss");
        bool has = dur.TotalSeconds > 0;
        double ratio = has ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        double x = ratio * (SeekHit.Width - ThumbD);
        SeekThumb.Margin = new Thickness(x, 0, 0, 0);
        SeekThumb.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SeekFill.Width = has ? x + ThumbD / 2.0 : 0;
    }

    void ScrubTo(double mouseX)
    {
        var dur = _tlEnd - _tlStart;
        double usable = SeekHit.Width - ThumbD;
        double ratio = Math.Clamp((mouseX - ThumbD / 2.0) / usable, 0, 1);
        _pendingSeek = dur * ratio;
        _tlPos = _pendingSeek;
        _tlUpdated = DateTimeOffset.UtcNow;
        UpdateProgressUi();
    }

    void SeekBy(TimeSpan delta)
    {
        var dur = _tlEnd - _tlStart;
        if (_session == null || dur <= TimeSpan.Zero) return;
        var target = CurrentPosition() + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > dur) target = dur;
        _pendingSeek = target;
        _tlPos = target;
        _tlUpdated = DateTimeOffset.UtcNow;
        _seekHoldUntil = DateTime.UtcNow.AddMilliseconds(900);
        UpdateProgressUi();
        _seekCommitTimer.Stop();
        _seekCommitTimer.Start();
    }

    void CommitSeek(TimeSpan offset)
    {
        _seekHoldUntil = DateTime.UtcNow.AddMilliseconds(900);
        _ = _session?.TryChangePlaybackPositionAsync((_tlStart + offset).Ticks);
    }

    // ------------------------------------------------------------------ volume

    static string ProcessKey(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        int bang = aumid.IndexOf('!');
        if (bang >= 0) aumid = aumid[(bang + 1)..];
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) aumid = aumid[..^4];
        return aumid;
    }

    T WithAppSessions<T>(Func<List<AudioSessionControl>, T> action, T fallback)
    {
        string key = ProcessKey(_source);
        if (key.Length == 0) { _volTargets = 0; return fallback; }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var found = new List<AudioSessionControl>();
            var names = new Dictionary<uint, string>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;
                if (pid == 0) continue;
                if (!names.TryGetValue(pid, out var name))
                {
                    try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
                    names[pid] = name;
                }
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) found.Add(session);
            }
            _volTargets = found.Count;
            _volError = "";
            return action(found);
        }
        catch (Exception ex)
        {
            _volTargets = 0;
            _volError = ex.Message;
            return fallback;
        }
    }

    void ReadVolume()
    {
        float? v = WithAppSessions<float?>(list =>
        {
            if (list.Count == 0) return null;
            _muted = list.Any(x => x.SimpleAudioVolume.Mute);
            return list.Max(x => x.SimpleAudioVolume.Volume);
        }, null);
        if (v is float f) _volume = f;
        ApplyVolumeUi(v != null);
    }

    void ChangeVolume(float delta)
    {
        if (_volTargets == 0)
        {
            ReadVolume();
            if (_volTargets == 0) return;
        }
        if (_muted && delta < 0) return;
        float v = Math.Clamp((float)(Math.Round((_volume + delta) * 20.0) / 20.0), 0f, 1f);
        bool ok = WithAppSessions(list =>
        {
            foreach (var x in list)
            {
                x.SimpleAudioVolume.Volume = v;
                if (_muted) x.SimpleAudioVolume.Mute = false;
            }
            return list.Count > 0;
        }, false);
        if (ok) { _volume = v; _muted = false; }
        ApplyVolumeUi(ok);
    }

    void ToggleMute()
    {
        if (_volTargets == 0)
        {
            ReadVolume();
            if (_volTargets == 0) return;
        }
        bool mute = !_muted;
        bool ok = WithAppSessions(list =>
        {
            foreach (var x in list) x.SimpleAudioVolume.Mute = mute;
            return list.Count > 0;
        }, false);
        if (ok) _muted = mute;
        ApplyVolumeUi(ok);
    }

    void ApplyVolumeUi(bool available)
    {
        VolumeHit.Opacity = available ? 1 : 0.3;
        VolumeGlyph.Text = !available ? "\uE995"
            : _muted || _volume <= 0.001f ? "\uE74F"
            : _volume < 0.34f ? "\uE993"
            : _volume < 0.67f ? "\uE994"
            : "\uE995";
    }

    // -------------------------------------------------------------------- tray

    void CreateTray()
    {
        try
        {
            Forms.NotifyIcon tray = new Forms.NotifyIcon();
            System.Drawing.Icon? icon = null;

            try
            {
                var asm = typeof(MainWindow).Assembly;
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

                if (resName != null)
                {
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                    }
                }
            }
            catch { }

            // Dosya yolu hatasında bile uygulamanın Tray ikonu mutlaka oluşsun:
            tray.Icon = icon ?? System.Drawing.SystemIcons.Application;

            _miStartup.Click += (_, _) =>
            {
                try { SetStartup(!IsStartupEnabled()); } catch { }
                UpdateTrayChecks();
            };
            _miOptions.Click += (_, _) => ShowOptions();
            _miRight.Click += (_, _) => SetSide(Side.Right);
            _miLeft.Click += (_, _) => SetSide(Side.Left);
            _miPosition.DropDownItems.AddRange(new Forms.ToolStripItem[] { _miRight, _miLeft });
            foreach (var (code, name) in Languages)
            {
                var item = new Forms.ToolStripMenuItem(name);
                item.Click += (_, _) => SetLanguage(code);
                _langItems.Add((code, item));
                _miLanguage.DropDownItems.Add(item);
            }
            _miClose.Click += (_, _) => Application.Current.Shutdown();

            _menu = new Forms.ContextMenuStrip();
            var menu = _menu;
            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                _miStartup, _miOptions, _miPosition, _miLanguage, new Forms.ToolStripSeparator(), _miClose
            });
            menu.Opening += (_, _) => UpdateTrayChecks();
            ApplyTrayTexts();

            tray.Text = "mey music player";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) _ = _session?.TryTogglePlayPauseAsync();
            };
            tray.MouseMove += (_, _) => OnTrayHover();

            _tray = tray;
            try { if (IsStartupEnabled()) SetStartup(true); } catch { }
        }
        catch (Exception ex) { _mediaStatus = "tray icon failed: " + ex.Message; }
    }

    static readonly (string code, string name)[] Languages =
    {
        ("en", "English"), ("tr", "Türkçe"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"),
        ("pt", "Português (Brasil)"), ("ru", "Русский"), ("zh", "简体中文"), ("ja", "日本語"),
        ("ar", "العربية"), ("hi", "हिन्दी")
    };

    static readonly string[] StrKeys =
    {
        "startup", "options", "position", "right", "left", "language", "close", "optionsBody",
        "updTitle", "updBody", "whatsNew", "updUpdate", "updLater", "updDownloading", "updFailed"
    };

    static readonly Dictionary<string, string[]> Strings = new()
    {
        ["en"] = new[] { "Start with Windows", "Options", "Position", "Right", "Left", "Language", "Close", "Settings will be added here.",
            "Update available", "Version {0} is available. You are using version {1}.", "What's new", "Update", "Later", "Downloading update...", "The update could not be installed. Please try again later." },
        ["tr"] = new[] { "Windows ile başlat", "Ayarlar", "Konum", "Sağ", "Sol", "Dil", "Kapat", "Ayarlar buraya eklenecek.",
            "Güncelleme mevcut", "{0} sürümü yayınlandı. Şu an {1} sürümünü kullanıyorsunuz.", "Yenilikler", "Güncelle", "Sonra", "Güncelleme indiriliyor...", "Güncelleme yüklenemedi. Lütfen daha sonra tekrar deneyin." },
        ["es"] = new[] { "Iniciar con Windows", "Opciones", "Posición", "Derecha", "Izquierda", "Idioma", "Cerrar", "Aquí se añadirán los ajustes.",
            "Actualización disponible", "La versión {0} ya está disponible. Estás usando la versión {1}.", "Novedades", "Actualizar", "Más tarde", "Descargando actualización...", "No se pudo instalar la actualización. Inténtalo de nuevo más tarde." },
        ["fr"] = new[] { "Démarrer avec Windows", "Options", "Position", "Droite", "Gauche", "Langue", "Fermer", "Les paramètres seront ajoutés ici.",
            "Mise à jour disponible", "La version {0} est disponible. Vous utilisez la version {1}.", "Nouveautés", "Mettre à jour", "Plus tard", "Téléchargement de la mise à jour...", "La mise à jour n'a pas pu être installée. Veuillez réessayer plus tard." },
        ["de"] = new[] { "Mit Windows starten", "Optionen", "Position", "Rechts", "Links", "Sprache", "Schließen", "Die Einstellungen werden hier hinzugefügt.",
            "Update verfügbar", "Version {0} ist verfügbar. Sie verwenden Version {1}.", "Neuigkeiten", "Aktualisieren", "Später", "Update wird heruntergeladen ...", "Das Update konnte nicht installiert werden. Bitte versuchen Sie es später erneut." },
        ["pt"] = new[] { "Iniciar com o Windows", "Opções", "Posição", "Direita", "Esquerda", "Idioma", "Fechar", "As configurações serão adicionadas aqui.",
            "Atualização disponível", "A versão {0} está disponível. Você está usando a versão {1}.", "Novidades", "Atualizar", "Mais tarde", "Baixando atualização...", "Não foi possível instalar a atualização. Tente novamente mais tarde." },
        ["ru"] = new[] { "Запускать вместе с Windows", "Параметры", "Положение", "Справа", "Слева", "Язык", "Закрыть", "Здесь появятся настройки.",
            "Доступно обновление", "Доступна версия {0}. Вы используете версию {1}.", "Что нового", "Обновить", "Позже", "Загрузка обновления...", "Не удалось установить обновление. Повторите попытку позже." },
        ["zh"] = new[] { "随 Windows 启动", "选项", "位置", "右", "左", "语言", "关闭", "设置将添加到此处。",
            "有可用更新", "版本 {0} 已发布。您当前使用的是版本 {1}。", "更新内容", "更新", "稍后", "正在下载更新…", "无法安装更新。请稍后重试。" },
        ["ja"] = new[] { "Windows と同時に起動", "オプション", "位置", "右", "左", "言語", "閉じる", "設定はここに追加されます。",
            "アップデートがあります", "バージョン {0} が利用可能です。現在のバージョンは {1} です。", "新機能", "更新", "後で", "アップデートをダウンロードしています...", "アップデートをインストールできませんでした。後でもう一度お試しください。" },
        ["ar"] = new[] { "التشغيل مع Windows", "خيارات", "الموضع", "يمين", "يسار", "اللغة", "إغلاق", "ستتم إضافة الإعدادات هنا.",
            "تحديث متاح", "الإصدار {0} متاح. أنت تستخدم الإصدار {1}.", "الجديد", "تحديث", "لاحقًا", "جارٍ تنزيل التحديث...", "تعذّر تثبيت التحديث. يُرجى المحاولة مرة أخرى لاحقًا." },
        ["hi"] = new[] { "Windows के साथ शुरू करें", "विकल्प", "स्थिति", "दाएँ", "बाएँ", "भाषा", "बंद करें", "सेटिंग्स यहाँ जोड़ी जाएँगी।",
            "अपडेट उपलब्ध है", "संस्करण {0} उपलब्ध है। आप संस्करण {1} का उपयोग कर रहे हैं।", "नया क्या है", "अपडेट करें", "बाद में", "अपडेट डाउनलोड हो रहा है...", "अपडेट इंस्टॉल नहीं हो सका। कृपया बाद में पुनः प्रयास करें।" }
    };

    string Tr(string key) =>
        (Strings.TryGetValue(_lang, out var t) ? t : Strings["en"])[Array.IndexOf(StrKeys, key)];

    void ApplyTrayTexts()
    {
        _miStartup.Text = Tr("startup");
        _miOptions.Text = Tr("options");
        _miPosition.Text = Tr("position");
        _miRight.Text = Tr("right");
        _miLeft.Text = Tr("left");
        _miLanguage.Text = Tr("language");
        _miClose.Text = Tr("close");
        if (_menu != null) _menu.RightToLeft = _lang == "ar" ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
    }

    void UpdateTrayChecks()
    {
        try { _miStartup.Checked = IsStartupEnabled(); } catch { }
        _miRight.Checked = _side == Side.Right;
        _miLeft.Checked = _side == Side.Left;
        foreach (var (code, item) in _langItems) item.Checked = code == _lang;
    }

    void SetSide(Side side)
    {
        _side = side;
        _userX = null;
        _posVersion++;
        ApplySideLayout();
        UpdateTrayChecks();
        SaveSettings();
        Update();
    }

    void SetLanguage(string lang)
    {
        _lang = lang;
        ApplyTrayTexts();
        UpdateTrayChecks();
        SaveSettings();
        if (_options != null) { _options.Close(); ShowOptions(); }
    }

    void ShowOptions()
    {
        if (_options != null) { _options.Activate(); return; }
        _options = new Window
        {
            Title = Tr("options"),
            FlowDirection = _lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Width = 360,
            Height = 220,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Content = new TextBlock
            {
                Text = Tr("optionsBody"),
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap
            }
        };
        _options.Closed += (_, _) => _options = null;
        _options.Show();
        _options.Activate();
    }

    // ------------------------------------------------------------------ updates

    async void CheckForUpdate()
    {
        try
        {
            var info = await Updater.CheckAsync();
            _updateStatus = info == null ? "up to date" : $"v{info.Version} is available";
            if (info != null && _updateWin == null && info.Version != _dismissedVersion) ShowUpdateWindow(info);
        }
        catch (Exception ex) { _updateStatus = "check failed: " + ex.Message; }
        Refresh();
    }

    void ShowUpdateWindow(UpdateInfo info)
    {
        var buttonStyle = (Style)FindResource("TextButton");
        var light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        var title = new TextBlock
        {
            Text = Tr("updTitle"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
        };
        var body = new TextBlock
        {
            Text = string.Format(Tr("updBody"), info.Version, Updater.Current),
            Foreground = light, Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap
        };
        var header = new TextBlock
        {
            Text = Tr("whatsNew"), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var notes = new TextBox
        {
            Text = Updater.PickNotes(info.Notes, _lang),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = Brushes.White,
            BorderThickness = new Thickness(0), Padding = new Thickness(10), Height = 150
        };
        var bar = new ProgressBar
        {
            Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed
        };
        var status = new TextBlock
        {
            Foreground = light, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var update = new Button
        {
            Content = Tr("updUpdate"), Style = buttonStyle,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x96, 0xDD)), Margin = new Thickness(0, 0, 8, 0)
        };
        var later = new Button { Content = Tr("updLater"), Style = buttonStyle };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(update);
        buttons.Children.Add(later);

        var panel = new StackPanel { Margin = new Thickness(24) };
        foreach (var child in new UIElement[] { title, body, header, notes, bar, status, buttons })
            panel.Children.Add(child);

        var win = new Window
        {
            Title = "mey music player",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            FlowDirection = _lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Content = panel
        };
        _updateWin = win;

        bool busy = false;
        win.Closing += (_, e) => { if (busy) e.Cancel = true; };
        win.Closed += (_, _) => { _dismissedVersion = info.Version; _updateWin = null; };
        later.Click += (_, _) => win.Close();
        update.Click += async (_, _) =>
        {
            busy = true;
            update.IsEnabled = later.IsEnabled = false;
            bar.Value = 0;
            bar.Visibility = status.Visibility = Visibility.Visible;
            status.Text = Tr("updDownloading");
            try
            {
                await Updater.DownloadAndInstallAsync(info, new Progress<double>(p => bar.Value = p));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                busy = false;
                _updateStatus = "update failed: " + ex.Message;
                status.Text = Tr("updFailed");
                update.IsEnabled = later.IsEnabled = true;
                Refresh();
            }
        };

        win.Show();
        win.Activate();
    }

    void LoadSettings()
    {
        try
        {
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line[..i].Trim(), v = line[(i + 1)..].Trim();
                if (k == "side") _side = v == "Right" ? Side.Right : Side.Left;
                else if (k == "lang" && Strings.ContainsKey(v)) _lang = v;
                else if (k == "userX" && int.TryParse(v, out int x)) _userX = x;
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllLines(SettingsPath, new[]
            {
                $"side={_side}",
                $"lang={_lang}",
                $"userX={(_userX is int x ? x.ToString() : "")}"
            });
        }
        catch { }
    }

    static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) != null;
    }

    static string StartupCommand()
    {
        string exe = Environment.ProcessPath ?? "";
        string bat = Path.Combine(Path.GetDirectoryName(exe) ?? "", "MMP Launcher.bat");
        return File.Exists(bat) ? $"cmd.exe /c start \"\" /min \"{bat}\" /run" : $"\"{exe}\"";
    }

    static void SetStartup(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue(RunName, StartupCommand());
        else key.DeleteValue(RunName, false);
    }

    void OnTrayHover()
    {
        GetCursorPos(out _trayPos);
        _lastTrayHover = Environment.TickCount64;
        if (_wheelHook != IntPtr.Zero) return;
        _wheelProc = WheelProc;
        string? module = Process.GetCurrentProcess().MainModule?.ModuleName;
        _wheelHook = SetWindowsHookEx(WH_MOUSE_LL, _wheelProc, GetModuleHandle(module), 0);
        if (_wheelHook != IntPtr.Zero) _wheelTimer.Start();
    }

    IntPtr WheelProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
        {
            var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (Math.Abs(d.pt.X - _trayPos.X) <= 6 && Math.Abs(d.pt.Y - _trayPos.Y) <= 6)
            {
                short delta = (short)((d.mouseData >> 16) & 0xFFFF);
                _lastTrayHover = Environment.TickCount64;
                Dispatcher.BeginInvoke(new Action(() => ChangeVolume(delta > 0 ? 0.05f : -0.05f)));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_wheelHook, nCode, wParam, lParam);
    }

    void StopWheelHook()
    {
        _wheelTimer.Stop();
        if (_wheelHook != IntPtr.Zero) UnhookWindowsHookEx(_wheelHook);
        _wheelHook = IntPtr.Zero;
    }

    void SetNoMedia()
    {
        _playing = false;
        _shuffleOn = false;
        _repeat = Windows.Media.MediaPlaybackAutoRepeatMode.None;
        _tlPos = _tlStart = _tlEnd = TimeSpan.Zero;
        _title = _artist = _source = "";
        foreach (var b in new[] { BtnPrev, BtnPlay, BtnNext, BtnMix, BtnLoop })
            b.IsEnabled = false;
        BtnPlay.Content = "\uE768";
        BtnLoop.Content = "\uE8EE";
        ApplyCover(null);
        UpdateProgressUi();
        ApplyVolumeUi(false);
        UpdateTimers();
        _mediaStatus = "no media session";
    }

    // ------------------------------------------------------------------- debug

    void Refresh()
    {
        if (_debug == null) return;
        _info.Text =
            $"mode                 : {(_usePoll ? "poll (16 ms)" : "events")}\n" +
            $"taskbar window      : {(_taskbar == IntPtr.Zero ? "NOT FOUND" : "found")}\n" +
            $"taskbar rect        : L{_last.Left} T{_last.Top} R{_last.Right} B{_last.Bottom} ({_last.Width}x{_last.Height})\n" +
            $"screen / edge       : {_screenW}x{_screenH} / {_edge}\n" +
            $"taskbar items       : {_scanInfo}  (scans {_scans}, ui events {_structEvents})\n" +
            $"tray left / cover x : {_trayLeft} / {_gapX}\n" +
            $"side / custom x     : {_side} / {(_userX?.ToString() ?? "none")}\n" +
            $"cover shown         : {_shown}\n" +
            $"player              : {(_expanded ? "open" : "closed")}  (t={_t:0.00})\n" +
            $"location events     : {_events}\n" +
            $"z-order fixes       : {_zFixes}\n" +
            $"mouse enter / leave : {_enters} / {_leaves}\n" +
            $"clicks on cover     : {_clicks}\n" +
            $"HID WHILE MOUSE OVER: {_hidWhileOver}\n" +
            $"\n" +
            $"media               : {_mediaStatus}\n" +
            $"now playing         : {_title} - {_artist}\n" +
            $"source app          : {_source}\n" +
            $"playing/shuffle/rep : {_playing} / {_shuffleOn} / {_repeat}\n" +
            $"volume              : {(_volTargets > 0 ? $"{_volume:P0}" : "n/a")}  ({_volTargets} audio session(s) for '{ProcessKey(_source)}') {_volError}\n" +
            $"muted               : {_muted}\n" +
            $"colors (chameleon)  : {_paletteInfo}\n" +
            $"version / update    : v{Updater.Current} / {_updateStatus}\n" +
            $"windows             : {Environment.OSVersion.Version}";
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _poll?.Stop();
        _measureTimer.Stop();
        _progressTimer.Stop();
        _zTimer.Stop();
        _updateTimer.Stop();
        StopWheelHook();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_locHook != IntPtr.Zero) UnhookWinEvent(_locHook);
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        try { Automation.RemoveAllEventHandlers(); } catch { }
    }
}
