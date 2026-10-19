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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using static TaskbarPlayerProto.NativeMethods;

namespace TaskbarPlayerProto;

public partial class MainWindow : Window
{
    const int GapPx = 2;          // space above/below the cover inside the taskbar
    const int MarginPx = 8;       // space left/right of the cover
    const double DesignW = 382;   // full player width in design units (cover is 40 tall)
    const double CoverGap = 4;    // design units between the cover and the player, and between the cover and the title card
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
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize = 13,
        Margin = new Thickness(12),
        TextWrapping = TextWrapping.Wrap
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

    // --- hover info card ---
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // --- position / drag / settings ---
    Side _side = Side.Left;
    int? _userX;                          // where the user dropped the cover (screen x); null = default spot
    string _lang = "en";
    bool _firstRun;                       // no settings.ini yet
    bool _dragging, _dragMoved;
    int _dragStartCursorX, _dragStartCoverX, _dragX, _coverSize, _spansVersion, _posVersion, _pxVal;
    (int, int, int, int) _pxKey;
    readonly DispatcherTimer _zTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Window? _options;

    // --- tray menu ---
    readonly Forms.ToolStripMenuItem _miStartup = new(), _miOptions = new(), _miPosition = new(),
        _miRight = new(), _miLeft = new(), _miLanguage = new(), _miRestart = new(), _miClose = new(), _miLyrics = new();
    readonly Forms.ToolStripMenuItem _miVersion = new() { Enabled = false };   // greyed out, only shows the version
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
    System.Windows.Media.Brush _accentBrush = System.Windows.Media.Brushes.White, _accentDimBrush = System.Windows.Media.Brushes.Gray;
    string _paletteInfo = "default";
    Color _chamBg = DefaultBg, _chamAccent = DefaultAccent;     // chameleon colours of the current cover

    // --- Design your player: saved players, pictures on the player ---
    readonly Designs _designs = Designs.Load();
    readonly StickerAnimator _stickerAnim = new();
    readonly Dictionary<string, StickerFrames> _stickerCache = new();
    int _stickerBuild;

    // --- open/close animation ---
    bool _expanded, _animating;
    double _t, _animFrom, _animTo;      // _t: 0 = only the cover, 1 = full player
    readonly Stopwatch _animSw = new();

    // --- media ---
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    System.Windows.Media.Brush? _defaultCoverBrush;
    bool _mediaDirty = true, _playing, _shuffleOn;
    Windows.Media.MediaPlaybackAutoRepeatMode _repeat = Windows.Media.MediaPlaybackAutoRepeatMode.None;
    TimeSpan _tlPos, _tlStart, _tlEnd;
    DateTimeOffset _tlUpdated;
    string _mediaStatus = "starting...", _title = "", _artist = "", _source = "";
    long _lastWheelSkip;                    // mouse wheel on the cover: at most one skip per 300 ms
    string _album = "";
    int _refreshSeq;                        // newest RefreshMedia call; older ones drop their results

    // --- lyrics (Spotify only, from LRCLIB; the tray toggle is off by default) ---
    const int LyricMs = 300;                // line change animation
    const double LyricShift = 14;           // design units a line moves when it comes in or leaves
    bool _lyricsOn;
    List<(TimeSpan Time, string Text)>? _lyrics;
    string _lyricsStatus = "-", _lyricsKey = "", _lyricsRetryKey = "";
    int _lyricsRetries;
    (int Line, int Part) _lyricShown = (-2, 0);         // what is on screen; -2 = nothing yet (show without animation)
    TextBlock? _lyricFront;                             // LyricA or LyricB, whichever is on screen
    readonly DispatcherTimer _lyricsTimer = new();      // one shot, set to the time of the next line
    readonly DispatcherTimer _lyricsRetryTimer = new();
    readonly DispatcherTimer _debugTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    static readonly string LyricsLogPath = Path.Combine(AppDataDir, "lyrics-test.txt");

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

        // Windows 10 has no Segoe Fluent Icons
        if (Environment.OSVersion.Version.Build < 22000)
        {
            var fallbackFont = new FontFamily("Segoe MDL2 Assets, Segoe UI");
            BtnPrev.FontFamily = fallbackFont;
            BtnPlay.FontFamily = fallbackFont;
            BtnNext.FontFamily = fallbackFont;
            VolumeGlyph.FontFamily = fallbackFont;
            Glyph.FontFamily = fallbackFont;
        }

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

        TitlePopup.Placement = PlacementMode.Custom;
        TitlePopup.CustomPopupPlacementCallback = PlaceTitleCard;
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (Cover.IsMouseOver && !string.IsNullOrWhiteSpace(_title)) ShowTitleCard();
        };

        Cover.MouseEnter += (_, _) =>
        {
            _enters++;
            if (!_hoverTimer.IsEnabled && !string.IsNullOrWhiteSpace(_title))
                _hoverTimer.Start();
            Refresh();
        };
        Cover.MouseLeave += (_, _) =>
        {
            _leaves++;
            _hoverTimer.Stop();
            TitlePopup.IsOpen = false;
            Refresh();
        };
        Cover.MouseLeftButtonDown += (_, e) =>
        {
            _clicks++;
            _dragMoved = false;
            _dragStartCursorX = (int)PointToScreen(e.GetPosition(this)).X;
            _dragStartCoverX = _gapX;
            Cover.CaptureMouse();
            Refresh();
        };

        // middle click on the cover = play / pause.
        // WPF has no MouseMiddleButtonDown event: MouseDown + ChangedButton is the way to catch the wheel click
        Cover.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            _ = _session?.TryTogglePlayPauseAsync();
        };

        // wheel forward = next song, back = previous; touchpads send many small steps, so skip once per 300 ms
        Cover.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (Environment.TickCount64 - _lastWheelSkip < 300) return;
            _lastWheelSkip = Environment.TickCount64;
            _ = e.Delta > 0 ? _session?.TrySkipNextAsync() : _session?.TrySkipPreviousAsync();
        };

        Cover.MouseMove += (_, e) =>
        {
            if (!Cover.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            int dx = (int)PointToScreen(e.GetPosition(this)).X - _dragStartCursorX;
            if (!_dragMoved && Math.Abs(dx) < 5) return;       // small wobble = still a click
            if (!_dragMoved)
            {
                // the card stays open through a click, only a real drag hides it
                _hoverTimer.Stop();
                TitlePopup.IsOpen = false;
            }
            _dragMoved = true;
            _dragging = true;
            _dragX = Math.Clamp(_dragStartCoverX + dx, _last.Left, Math.Max(_last.Left, TrayLeft - _coverSize));
            Update();
        };
        Cover.MouseLeftButtonUp += (_, _) =>
        {
            if (!Cover.IsMouseCaptured) return;
            Cover.ReleaseMouseCapture();
            if (_dragMoved)
            {
                DropCover();
                if (Cover.IsMouseOver && !string.IsNullOrWhiteSpace(_title)) _hoverTimer.Start();   // card comes back after the drop
            }
            else ToggleExpand();
        };
        Cover.LostMouseCapture += (_, _) => _dragging = false;
        _measureTimer.Tick += (_, _) => { _measureTimer.Stop(); RunMeasure(); };
        _progressTimer.Tick += (_, _) => UpdateProgressUi();
        _seekCommitTimer.Tick += (_, _) => { _seekCommitTimer.Stop(); CommitSeek(_pendingSeek); };
        _lyricsTimer.Tick += (_, _) => { _lyricsTimer.Stop(); UpdateLyricsUi(); };
        _lyricsRetryTimer.Tick += (_, _) => { _lyricsRetryTimer.Stop(); _lyricsKey = ""; LoadLyrics(); };
        _lyricFront = LyricA;
        PlayerPanel.MouseEnter += (_, _) => ApplyLyricsLayer();     // hovering the player brings the controls back
        PlayerPanel.MouseLeave += (_, _) => ApplyLyricsLayer();
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
        BuildStickers();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => Update();      // WPF has shown the window now: hide or place it
        Closed += OnClosed;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

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

            _structHandler = (_, _) => Dispatcher.BeginInvoke(new Action(OnStructureChanged));
            Task.Run(() =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(_taskbar);
                    Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, _structHandler);
                }
                catch { }
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
                Title = "mey music player - debug",
                Width = 540,
                Height = 640,
                Left = 20,
                Top = 120,
                Content = _info
            };
            _debug.Closed += (_, _) => Application.Current.Shutdown();
            _debug.Show();
            _debugTimer.Tick += (_, _) => Refresh();     // live time and lyrics line
            _debugTimer.Start();
        }

        CreateTray();
        InitMedia();
        _updateTimer.Start();
        Update();
        if (Environment.GetCommandLineArgs().Contains("--options")) ShowOptions();     // for development and tests
    }

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

            int style = GetWindowLong(fg, GWL_STYLE);
            if ((style & WS_CAPTION) == WS_CAPTION) return false;

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
        if (!_shown) { _needMeasure = true; return; }
        _measureTimer.Stop();
        _measureTimer.Start();
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
        _edge = atBottom ? "bottom" : atTop ? "top" : "side";

        bool taskbarVisible = (atBottom && r.Top < _screenH - 3) || (atTop && r.Bottom > 3);
        bool visible = taskbarVisible && !IsFullScreenAppActive();

        bool fullyOn = atBottom ? r.Bottom <= _screenH : atTop && r.Top >= 0;
        if (fullyOn) _fullHeight = r.Height;

        int currentTaskbarH = r.Height;
        int h = (_fullHeight > 0 && _fullHeight <= currentTaskbarH) ? _fullHeight : currentTaskbarH;
        int size = Math.Max(12, h - 2 * GapPx);

        if (visible && size >= 12)
        {
            if (!_shown)
            {
                if (_needMeasure) RunMeasure();
                if (_mediaDirty) { _mediaDirty = false; RefreshMedia(); }
            }

            double f = size / 40.0;
            double s = f / VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(Scale.ScaleX - s) > 0.001) { Scale.ScaleX = s; Scale.ScaleY = s; }
            int fullW = (int)Math.Round(DesignW * f);

            _coverSize = size;
            int restX = PlaceX(size, r.Left);
            int coverX = _dragging ? _dragX
                : _t > 0 ? restX + (int)Math.Round((OpenX(restX, size, r.Left) - restX) * _t)
                : restX;
            _gapX = coverX;

            int winX;
            int width;

            if (_side == Side.Left)
            {
                width = size + (int)Math.Round((fullW - size) * _t);
                winX = coverX;
                PlayerPanel.Clip = null;
            }
            else
            {
                width = fullW;
                winX = coverX + size - fullW;
                double panelW = 338.0;
                double visibleW = panelW * _t;
                PlayerPanel.Clip = new RectangleGeometry(new Rect(panelW - visibleW, 0, visibleW, 40));
            }

            int winY = r.Top + (r.Height - size) / 2;

            SetWindowPos(_hwnd, HWND_TOPMOST, winX, winY, width, size,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            RepositionTitleCard();
            bool was = _shown;
            _shown = true;
            if (!was) UpdateTimers();
        }
        else if (_shown || IsWindowVisible(_hwnd))     // WPF shows the window at start even when the taskbar is hidden
        {
            if (Cover.IsMouseOver) _hidWhileOver++;
            _hoverTimer.Stop();
            TitlePopup.IsOpen = false;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            _shown = false;
            UpdateTimers();
        }

        UpdateStickerAnim();
        Refresh();
    }

    int TrayLeft => _trayLeft > 0 ? _trayLeft : (int)(_screenW * 0.75);

    int FullWidth(int size) => (int)Math.Round(DesignW * size / 40.0);

    int PlaceX(int size, int taskbarLeft)
    {
        var key = (size, taskbarLeft, _spansVersion, _posVersion);
        if (key == _pxKey) return _pxVal;
        int wanted = _userX ?? (_side == Side.Left ? taskbarLeft - 100000 : _screenW + 100000);
        int need = _side == Side.Right ? FullWidth(size) : size;
        int x = SnapToFree(wanted, size, taskbarLeft, need);
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
        PlayerPanel.Margin = left ? new Thickness(40 + CoverGap, 0, 0, 0) : new Thickness(0, 0, 40 + CoverGap, 0);
    }

    // The title card is centered on the cover, above it (below it when the taskbar is at the top).
    // WPF passes the real sizes, already scaled to the taskbar, so the gap is scaled by the cover size too.
    CustomPopupPlacement[] PlaceTitleCard(Size popup, Size target, Point offset)
    {
        double gap = CoverGap * target.Width / 40;
        double y = _edge == "top" ? target.Height + gap : -popup.Height - gap;
        return new[] { new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, y), PopupPrimaryAxis.None) };
    }

    void ShowTitleCard()
    {
        TitlePopupText.Text = _title;
        if (!TitlePopup.IsOpen) TitlePopup.IsOpen = true;
        else Dispatcher.BeginInvoke(new Action(RepositionTitleCard), DispatcherPriority.Loaded);   // after the new text is measured
    }

    // A Popup only places itself when it opens or an offset changes, not when the window under it moves
    void RepositionTitleCard()
    {
        if (!TitlePopup.IsOpen) return;
        TitlePopup.HorizontalOffset += 0.01;
        TitlePopup.HorizontalOffset -= 0.01;
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
        sb.AppendLine($"tray left: {trayLeft}");
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
                    sb.AppendLine($"{(counted ? "*" : " ")} {info.ControlType.ProgrammaticName,-24} L{l} R{rr} id='{info.AutomationId}'");
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

        var (spans, dump) = t.IsCompletedSuccessfully ? t.Result : (new List<(int L, int R)>(), "scan failed");

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

    void ToggleExpand()
    {
        if (_expanded && _gapX != _pxVal)
        {
            _userX = _gapX;
            _posVersion++;
            SaveSettings();
        }
        _expanded = !_expanded;
        if (_expanded) _lyricShown = (-2, 0);       // the line on screen appears with the player, without a line change animation
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

        if (_shown && _expanded) UpdateLyricsUi();      // also re-times the next line after play / pause
        else _lyricsTimer.Stop();
    }

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
            ErrorLog.Write("media init", ex);
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
        // the debug window keeps following the song while the taskbar is hidden (lyrics prototype)
        if (!_shown && !_showDebug) { _mediaDirty = true; return; }
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
                case Smtc.Timeline: ApplyTimeline(s.GetTimelineProperties()); LoadLyrics(); break;   // the length can come after the title
            }
        }
        catch (Exception ex) { _mediaStatus = "media error: " + ex.Message; ErrorLog.Write("media event", ex); }
        Refresh();
    }

    async void RefreshMedia()
    {
        var s = _session;
        int seq = ++_refreshSeq;        // quick song changes overlap: a slow cover of the last song must not win
        if (s == null) { SetNoMedia(); Refresh(); return; }
        try
        {
            ApplyPlayback(s.GetPlaybackInfo());
            ApplyTimeline(s.GetTimelineProperties());
            var props = await s.TryGetMediaPropertiesAsync();
            if (s != _session || seq != _refreshSeq) return;
            _title = props.Title ?? "";
            if (TitlePopup.IsOpen)          // song changed under the open card (e.g. wheel on the cover)
            {
                if (string.IsNullOrWhiteSpace(_title)) TitlePopup.IsOpen = false;
                else ShowTitleCard();
            }
            _artist = props.Artist ?? "";
            _album = props.AlbumTitle ?? "";
            _source = s.SourceAppUserModelId ?? "";
            LoadLyrics();
            ReadVolume();
            var cover = await LoadThumbAsync(props);
            if (s != _session || seq != _refreshSeq) return;
            ApplyCover(cover);
            _mediaStatus = "ok";
        }
        catch (Exception ex) { _mediaStatus = "media error: " + ex.Message; ErrorLog.Write("media refresh", ex); }
        Refresh();
    }

    static async Task<BitmapSource?> LoadThumbAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
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
        return CropToCover(bi);
    }

    // Spotify on Windows 10 hands over a 300x300 transparent PNG: the cover shrunk into the top and its logo
    // underneath. Keep only the first opaque block when it is a square cover; normal thumbnails come back as they are.
    static BitmapSource CropToCover(BitmapSource src)
    {
        var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        fmt.CopyPixels(px, stride, 0);

        bool RowOpaque(int y)
        {
            for (int x = 0; x < w; x++) if (px[y * stride + x * 4 + 3] >= 250) return true;
            return false;
        }

        int top = 0;
        while (top < h && !RowOpaque(top)) top++;
        if (top == h) return src;
        int bottom = top;
        while (bottom + 1 < h && RowOpaque(bottom + 1)) bottom++;

        int left = w, right = -1;
        for (int y = top; y <= bottom; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] >= 250) { if (x < left) left = x; if (x > right) right = x; }

        int cw = right - left + 1, ch = bottom - top + 1;
        if (cw == w && ch == h) return src;                                   // nothing around it
        if (cw < w / 3 || ch < h / 3) return src;                             // too small to be the cover
        if (Math.Abs(cw - ch) > Math.Max(cw, ch) / 10) return src;            // covers are square
        var crop = new CroppedBitmap(fmt, new Int32Rect(left, top, cw, ch));
        crop.Freeze();
        return crop;
    }

    BitmapSource? TryGetAppIcon(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        string key = ProcessKey(aumid).ToLower();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    if (p.ProcessName.ToLower().Contains(key) || key.Contains(p.ProcessName.ToLower()))
                    {
                        string path = p.MainModule?.FileName ?? "";
                        if (File.Exists(path))
                        {
                            using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                            if (sysIcon != null)
                            {
                                var bs = Imaging.CreateBitmapSourceFromHIcon(
                                    sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                bs.Freeze();
                                return bs;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    void ApplyCover(BitmapSource? cover)
    {
        if (cover != null)
        {
            Cover.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            Glyph.Visibility = Visibility.Collapsed;
            var (bg, accent) = ExtractPalette(cover);
            SetChameleon(bg, accent);
        }
        else
        {
            var appIcon = TryGetAppIcon(_source);
            if (appIcon != null)
            {
                Cover.Background = new ImageBrush(appIcon) { Stretch = Stretch.UniformToFill };
                Glyph.Visibility = Visibility.Collapsed;
                SetChameleon(DefaultBg, DefaultAccent);
            }
            else
            {
                Cover.Background = _defaultCoverBrush;
                Glyph.Text = "\uEC4F"; // music note
                Glyph.Visibility = Visibility.Visible;
                SetChameleon(DefaultBg, DefaultAccent);
            }
        }
    }

    void SetChameleon(Color bg, Color accent)
    {
        _chamBg = bg;
        _chamAccent = accent;
        ApplyDesignColors();
        CoverChanged?.Invoke();
    }

    // The player in use decides each colour; a colour it leaves empty comes from the cover (chameleon)
    internal void ApplyDesignColors()
    {
        var d = _designs.Current;
        ApplyPalette(Designs.ParseColor(d?.Bg) ?? _chamBg, Designs.ParseColor(d?.Buttons) ?? _chamAccent);
    }

    // --- for the options window ---
    internal Designs DesignStore => _designs;
    internal (Color Bg, Color Accent) Chameleon => (_chamBg, _chamAccent);
    internal System.Windows.Media.Brush CoverBackground => Cover.Background;
    internal bool CoverShowsGlyph => Glyph.Visibility == Visibility.Visible;
    internal bool CoverOnRight => _side == Side.Right;
    internal string Text(string key) => Tr(key);
    internal event Action? CoverChanged;

    // Called by the options window after the player in use or its pictures changed
    internal void DesignChanged()
    {
        ApplyDesignColors();
        BuildStickers();
    }

    // Decodes the pictures of the player in use (in the background, so they are ready before the player opens)
    async void BuildStickers()
    {
        int build = ++_stickerBuild;
        var specs = _designs.Current?.Stickers.ToList() ?? new List<StickerSpec>();
        var used = new Dictionary<string, StickerFrames>();
        var shown = new List<(StickerSpec spec, StickerFrames frames)>();
        foreach (var s in specs)
        {
            // about 2.5 px per design unit covers big taskbars and 200 % scaling; sizes are bucketed so small edits reuse it
            int side = (int)Math.Ceiling(Math.Max(s.W, s.H) * 2.5 / 32) * 32;
            string key = s.File + "|" + side;
            if (!used.TryGetValue(key, out var frames) && !_stickerCache.TryGetValue(key, out frames))
            {
                frames = await Stickers.LoadAsync(Designs.ImagePath(s.File), side, 1_500_000);
                if (build != _stickerBuild) return;
            }
            if (frames == null) continue;
            used[key] = frames;
            shown.Add((s, frames));
        }
        _stickerCache.Clear();
        foreach (var kv in used) _stickerCache[kv.Key] = kv.Value;

        _stickerAnim.Clear();
        StickerLayer.Children.Clear();
        foreach (var (spec, frames) in shown)
        {
            var img = new Image { Source = frames.Frames[0], Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Stickers.Place(img, spec);
            StickerLayer.Children.Add(img);
            _stickerAnim.Add(img, frames);
        }
        UpdateStickerAnim();
    }

    // GIFs only play while the player is open and on screen
    void UpdateStickerAnim() => _stickerAnim.Run(_shown && _t > 0);

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

        // chameleon colours for the player and the title card
        PlayerPanel.Background = Frozen(bg);
        TitlePopupBorder.Background = Frozen(bg);
        TitlePopupText.Foreground = _accentBrush;

        BtnPrev.Foreground = BtnPlay.Foreground = BtnNext.Foreground = _accentBrush;
        VolumeGlyph.Foreground = _accentBrush;
        SeekFill.Background = _accentBrush;
        SeekThumb.Fill = _accentBrush;
        SeekTrack.Background = Frozen(Color.FromArgb(0x44, accent.R, accent.G, accent.B));
        TimeText.Foreground = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        LyricA.Foreground = LyricB.Foreground = _accentBrush;
        UpdateToggleColors();
        _paletteInfo = $"bg {Hex(bg)} accent {Hex(accent)}";
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

        double trackW = SeekHit.ActualWidth;
        double usable = trackW - ThumbD;
        double x = (has && usable > 0) ? ratio * usable : 0;

        if (double.IsNaN(x) || x < 0) x = 0;

        SeekThumb.Margin = new Thickness(x, 0, 0, 0);
        SeekThumb.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SeekFill.Width = has ? Math.Max(0, x + ThumbD / 2.0) : 0;
        UpdateLyricsUi();       // seeking moves the lyrics too
    }

    void ScrubTo(double mouseX)
    {
        var dur = _tlEnd - _tlStart;
        double trackW = SeekHit.ActualWidth;
        double usable = trackW - ThumbD;
        if (usable <= 0) return;

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
                    .FirstOrDefault(n => n.EndsWith("tray.ico", StringComparison.OrdinalIgnoreCase));

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

            tray.Icon = icon ?? System.Drawing.SystemIcons.Application;

            _miStartup.Click += (_, _) =>
            {
                try { SetStartup(!IsStartupEnabled()); } catch { }
                UpdateTrayChecks();
            };
            _miOptions.Click += (_, _) => ShowOptions();
            _miLyrics.Click += (_, _) =>
            {
                _lyricsOn = !_lyricsOn;
                SaveSettings();
                UpdateTrayChecks();
                _lyricsKey = "";
                if (_lyricsOn) LoadLyrics();
                else SetLyrics(null, "off");
            };
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
            _miRestart.Click += (_, _) =>
            {
                // only one copy may run: --after-update makes the new copy wait until this one has exited
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "", "--after-update") { UseShellExecute = true }); }
                catch { return; }
                Application.Current.Shutdown();
            };
            _miClose.Click += (_, _) => Application.Current.Shutdown();
            _miVersion.Text = $"v{Updater.Current}";

            _menu = new Forms.ContextMenuStrip();
            var menu = _menu;
            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                // version, the window, then the on/off settings, then the submenus, Close on its own at the bottom
                _miVersion, new Forms.ToolStripSeparator(),
                _miOptions, new Forms.ToolStripSeparator(), _miLyrics, _miStartup, new Forms.ToolStripSeparator(),
                _miPosition, _miLanguage, new Forms.ToolStripSeparator(), _miRestart, _miClose
            });
            menu.Opening += (_, _) => UpdateTrayChecks();
            TrayMenu.Style(menu);
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
            // A new install starts with Windows. The first start after an update only writes settings.ini,
            // so people who turned it off before keep their choice.
            if (_firstRun)
            {
                if (!Environment.GetCommandLineArgs().Contains("--after-update")) try { SetStartup(true); } catch { }
                SaveSettings();
            }
        }
        catch (Exception ex) { _mediaStatus = "tray icon failed: " + ex.Message; ErrorLog.Write("tray", ex); }
    }

    static readonly (string code, string name)[] Languages =
    {
        ("en", "English"), ("tr", "Türkçe"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"),
        ("pt", "Português (Brasil)"), ("ru", "Русский"), ("zh", "简体中文"), ("ja", "日本語"),
        ("ar", "العربية"), ("hi", "हिन्दी")
    };

    static readonly string[] StrKeys =
    {
        "startup", "options", "position", "right", "left", "language", "close", "updTitle", "updBody", "whatsNew",
        "updUpdate", "updLater", "updDownloading", "updFailed", "restart", "lyrics", "design", "autosave",
        "players", "default", "customPlayer", "newPlayer", "name", "deletePlayer", "background", "buttons",
        "chameleon", "customColor", "images", "addImage", "imageHint", "editHint", "remove",
        "reset", "resetAsk", "deleteAsk", "cancel", "maxImages", "maxPlayers", "badImage", "layersHint", "imageN"
    };

    static readonly Dictionary<string, string[]> Strings = new()
    {
        ["en"] = new[] { "Start with Windows", "Options", "Position", "Right", "Left", "Language", "Close", "Update available",
            "Version {0} is available. You are using version {1}.", "What's new", "Update", "Later", "Downloading update...",
            "The update could not be installed. Please try again later.", "Restart", "Lyrics", "Design your player",
            "Changes are saved automatically.", "Players", "Default", "Custom player", "New player", "Name", "Delete player", "Background", "Buttons",
            "Chameleon: colours from the cover", "Custom colour", "Images", "Add image", "PNG, JPG or GIF, up to 10 MB. Up to 10 images per player.",
            "Drag an image to move it, its corner to resize it and the dot above it to rotate it. Mouse wheel: size. Delete: remove.", "Remove", "Reset", "Reset this player to the default colours and remove its images?", "Delete this player?", "Cancel",
            "A player can have up to 10 images.", "You can save up to 5 players.", "This file can't be used. Choose a PNG, JPG or GIF up to 10 MB.",
            "Drag to reorder. The top image is in front.", "Image {0}" },
        ["tr"] = new[] { "Windows ile başlat", "Ayarlar", "Konum", "Sağ", "Sol", "Dil", "Kapat", "Güncelleme mevcut",
            "{0} sürümü yayınlandı. Şu an {1} sürümünü kullanıyorsunuz.", "Yenilikler", "Güncelle", "Sonra", "Güncelleme indiriliyor...",
            "Güncelleme yüklenemedi. Lütfen daha sonra tekrar deneyin.", "Yeniden başlat", "Şarkı sözleri", "Oynatıcını tasarla",
            "Değişiklikler otomatik kaydedilir.", "Oynatıcılar", "Varsayılan", "Özel oynatıcı", "Yeni oynatıcı", "Ad", "Oynatıcıyı sil", "Arka plan",
            "Butonlar", "Bukalemun: renkler kapaktan", "Özel renk", "Resimler", "Resim ekle",
            "PNG, JPG veya GIF, en fazla 10 MB. Bir oynatıcıda en fazla 10 resim.",
            "Taşımak için resmi, boyutlandırmak için köşesini, döndürmek için üstündeki noktayı sürükle. Fare tekerleği: boyut. Delete: kaldır.",
            "Kaldır", "Sıfırla", "Bu oynatıcı varsayılan renklere dönsün ve resimleri kaldırılsın mı?",
            "Bu oynatıcı silinsin mi?", "Vazgeç", "Bir oynatıcıda en fazla 10 resim olabilir.", "En fazla 5 oynatıcı kaydedebilirsin.",
            "Bu dosya kullanılamıyor. En fazla 10 MB'lık bir PNG, JPG veya GIF seç.",
            "Sıralamak için sürükle. En üstteki resim en öndedir.", "Resim {0}" },
        ["es"] = new[] { "Iniciar con Windows", "Opciones", "Posición", "Derecha", "Izquierda", "Idioma", "Cerrar", "Actualización disponible",
            "La versión {0} está disponible. Estás usando la versión {1}.", "Novedades", "Actualizar", "Más tarde", "Descargando actualización...",
            "No se pudo instalar la actualización. Inténtalo de nuevo más tarde.", "Reiniciar", "Letras", "Diseña tu reproductor",
            "Los cambios se guardan automáticamente.", "Reproductores", "Predeterminado", "Reproductor personalizado", "Nuevo reproductor", "Nombre",
            "Eliminar reproductor", "Fondo", "Botones", "Camaleón: colores de la portada", "Color personalizado", "Imágenes", "Añadir imagen",
            "PNG, JPG o GIF, hasta 10 MB. Hasta 10 imágenes por reproductor.",
            "Arrastra una imagen para moverla, su esquina para cambiar el tamaño y el punto de arriba para girarla. Rueda del ratón: tamaño. Supr: quitar.",
            "Quitar", "Restablecer", "¿Restablecer este reproductor a los colores predeterminados y quitar sus imágenes?",
            "¿Eliminar este reproductor?", "Cancelar", "Un reproductor puede tener hasta 10 imágenes.", "Puedes guardar hasta 5 reproductores.",
            "Este archivo no se puede usar. Elige un PNG, JPG o GIF de hasta 10 MB.",
            "Arrastra para ordenar. La imagen de arriba queda delante.", "Imagen {0}" },
        ["fr"] = new[] { "Démarrer avec Windows", "Options", "Position", "Droite", "Gauche", "Langue", "Fermer", "Mise à jour disponible",
            "La version {0} est disponible. Vous utilisez la version {1}.", "Nouveautés", "Mettre à jour", "Plus tard",
            "Téléchargement de la mise à jour...", "La mise à jour n'a pas pu être installée. Veuillez réessayer plus tard.", "Redémarrer", "Paroles",
            "Concevez votre lecteur", "Les modifications sont enregistrées automatiquement.", "Lecteurs", "Par défaut", "Lecteur personnalisé",
            "Nouveau lecteur", "Nom", "Supprimer le lecteur", "Arrière-plan", "Boutons", "Caméléon : couleurs de la pochette",
            "Couleur personnalisée", "Images", "Ajouter une image", "PNG, JPG ou GIF, jusqu'à 10 Mo. Jusqu'à 10 images par lecteur.",
            "Faites glisser une image pour la déplacer, son coin pour la redimensionner et le point au-dessus pour la faire pivoter. Molette : taille. Suppr : retirer.",
            "Retirer", "Réinitialiser",
            "Réinitialiser ce lecteur aux couleurs par défaut et retirer ses images ?", "Supprimer ce lecteur ?", "Annuler",
            "Un lecteur peut avoir jusqu'à 10 images.", "Vous pouvez enregistrer jusqu'à 5 lecteurs.",
            "Ce fichier ne peut pas être utilisé. Choisissez un PNG, JPG ou GIF de 10 Mo maximum.",
            "Faites glisser pour réordonner. L'image du haut est devant.", "Image {0}" },
        ["de"] = new[] { "Mit Windows starten", "Optionen", "Position", "Rechts", "Links", "Sprache", "Schließen", "Update verfügbar",
            "Version {0} ist verfügbar. Sie verwenden Version {1}.", "Was ist neu", "Aktualisieren", "Später", "Update wird heruntergeladen...",
            "Das Update konnte nicht installiert werden. Bitte versuchen Sie es später noch einmal.", "Neu starten", "Songtexte",
            "Gestalten Sie Ihren Player", "Änderungen werden automatisch gespeichert.", "Player", "Standard", "Eigener Player", "Neuer Player",
            "Name", "Player löschen", "Hintergrund", "Schaltflächen", "Chamäleon: Farben vom Cover", "Eigene Farbe", "Bilder", "Bild hinzufügen",
            "PNG, JPG oder GIF, bis 10 MB. Bis zu 10 Bilder pro Player.",
            "Ziehen Sie ein Bild, um es zu verschieben, seine Ecke für die Größe und den Punkt darüber zum Drehen. Mausrad: Größe. Entf: entfernen.",
            "Entfernen", "Zurücksetzen", "Diesen Player auf die Standardfarben zurücksetzen und seine Bilder entfernen?",
            "Diesen Player löschen?", "Abbrechen", "Ein Player kann bis zu 10 Bilder haben.", "Sie können bis zu 5 Player speichern.",
            "Diese Datei kann nicht verwendet werden. Wählen Sie ein PNG, JPG oder GIF bis 10 MB.",
            "Zum Sortieren ziehen. Das oberste Bild liegt vorne.", "Bild {0}" },
        ["pt"] = new[] { "Iniciar com o Windows", "Opções", "Posição", "Direita", "Esquerda", "Idioma", "Fechar", "Atualização disponível",
            "A versão {0} está disponível. Você está usando a versão {1}.", "O que há de novo", "Atualizar", "Mais tarde", "Baixando atualização...",
            "A atualização não pôde ser instalada. Tente novamente mais tarde.", "Reiniciar", "Letras", "Crie seu player",
            "As alterações são salvas automaticamente.", "Players", "Padrão", "Player personalizado", "Novo player", "Nome", "Excluir player",
            "Fundo", "Botões", "Camaleão: cores da capa", "Cor personalizada", "Imagens", "Adicionar imagem",
            "PNG, JPG ou GIF, até 10 MB. Até 10 imagens por player.",
            "Arraste uma imagem para movê-la, o canto para redimensionar e o ponto acima para girar. Roda do mouse: tamanho. Delete: remover.",
            "Remover", "Redefinir", "Redefinir este player para as cores padrão e remover as imagens dele?",
            "Excluir este player?", "Cancelar", "Um player pode ter até 10 imagens.", "Você pode salvar até 5 players.",
            "Este arquivo não pode ser usado. Escolha um PNG, JPG ou GIF de até 10 MB.",
            "Arraste para reordenar. A imagem de cima fica na frente.", "Imagem {0}" },
        ["ru"] = new[] { "Запуск с Windows", "Параметры", "Положение", "Справа", "Слева", "Язык", "Закрыть", "Доступно обновление",
            "Доступна версия {0}. Вы используете версию {1}.", "Что нового", "Обновить", "Позже", "Загрузка обновления...",
            "Не удалось установить обновление. Повторите попытку позже.", "Перезапустить", "Тексты песен", "Оформите свой плеер",
            "Изменения сохраняются автоматически.", "Плееры", "По умолчанию", "Свой плеер", "Новый плеер", "Название", "Удалить плеер", "Фон",
            "Кнопки", "Хамелеон: цвета обложки", "Свой цвет", "Изображения", "Добавить изображение",
            "PNG, JPG или GIF до 10 МБ. До 10 изображений на плеер.",
            "Перетащите изображение, чтобы переместить его; угол — изменить размер, точку над ним — повернуть. Колесо мыши: размер. Delete: убрать.",
            "Убрать", "Сбросить", "Сбросить этот плеер к стандартным цветам и убрать его изображения?",
            "Удалить этот плеер?", "Отмена", "У плеера может быть до 10 изображений.", "Можно сохранить до 5 плееров.",
            "Этот файл нельзя использовать. Выберите PNG, JPG или GIF до 10 МБ.",
            "Перетащите, чтобы изменить порядок. Верхнее изображение — впереди.", "Изображение {0}" },
        ["zh"] = new[] { "随 Windows 启动", "选项", "位置", "右", "左", "语言", "关闭", "有可用更新", "版本 {0} 可用。您当前使用的是版本 {1}。", "更新内容", "更新", "稍后", "正在下载更新...", "无法安装更新。请稍后重试。",
            "重新启动", "歌词", "设计你的播放器", "更改会自动保存。", "播放器", "默认", "自定义播放器", "新建播放器", "名称", "删除播放器", "背景", "按钮", "变色龙：取自封面的颜色", "自定义颜色", "图片", "添加图片",
            "PNG、JPG 或 GIF，最大 10 MB。每个播放器最多 10 张图片。", "拖动图片可移动，拖动角可调整大小，拖动上方的圆点可旋转。鼠标滚轮：大小。Delete：移除。", "移除", "重置",
            "将此播放器重置为默认颜色并移除其图片？", "删除此播放器？", "取消", "每个播放器最多可以有 10 张图片。", "最多可以保存 5 个播放器。", "无法使用此文件。请选择不超过 10 MB 的 PNG、JPG 或 GIF。",
            "拖动以排序。最上面的图片在最前面。", "图片 {0}" },
        ["ja"] = new[] { "Windowsと同時に起動", "オプション", "位置", "右", "左", "言語", "閉じる", "利用可能なアップデート", "バージョン {0} が利用可能です。現在バージョン {1} を使用しています。", "新機能", "アップデート", "後で",
            "アップデートをダウンロード中...", "アップデートをインストールできませんでした。後で再度お試しください。", "再起動", "歌詞", "プレーヤーをデザイン", "変更は自動的に保存されます。", "プレーヤー", "デフォルト", "カスタムプレーヤー",
            "新しいプレーヤー", "名前", "プレーヤーを削除", "背景", "ボタン", "カメレオン：カバーの色", "カスタムカラー", "画像", "画像を追加", "PNG、JPG、GIF（10 MB まで）。1 つのプレーヤーに画像は 10 枚まで。",
            "画像をドラッグして移動、角でサイズ変更、上の点で回転します。マウスホイール：サイズ。Delete：取り除く。", "取り除く", "リセット", "このプレーヤーをデフォルトの色に戻し、画像を取り除きますか？",
            "このプレーヤーを削除しますか？", "キャンセル", "1 つのプレーヤーに画像は 10 枚までです。", "プレーヤーは 5 つまで保存できます。", "このファイルは使用できません。10 MB までの PNG、JPG、GIF を選んでください。",
            "ドラッグで並べ替え。いちばん上の画像が最前面です。", "画像 {0}" },
        ["ar"] = new[] { "البدء مع ويندوز", "الخيارات", "الموضع", "يمين", "يسار", "اللغة", "إغلاق", "تحديث متاح", "الإصدار {0} متاح. أنت تستخدم الإصدار {1}.",
            "ما الجديد", "تحديث", "لاحقاً", "جاري تنزيل التحديث...", "تعذر تثبيت التحديث. يرجى المحاولة مرة أخرى لاحقاً.", "إعادة التشغيل",
            "كلمات الأغاني", "صمّم مشغّلك", "يتم حفظ التغييرات تلقائياً.", "المشغّلات", "الافتراضي", "مشغّل مخصّص", "مشغّل جديد", "الاسم",
            "حذف المشغّل", "الخلفية", "الأزرار", "الحرباء: ألوان من الغلاف", "لون مخصّص", "الصور", "إضافة صورة",
            "PNG أو JPG أو GIF حتى 10 ميغابايت. حتى 10 صور لكل مشغّل.",
            "اسحب الصورة لتحريكها، وزاويتها لتغيير حجمها، والنقطة فوقها لتدويرها. عجلة الماوس: الحجم. Delete: إزالة.", "إزالة", "إعادة تعيين", "هل تريد إعادة هذا المشغّل إلى الألوان الافتراضية وإزالة صوره؟", "هل تريد حذف هذا المشغّل؟", "إلغاء",
            "يمكن أن يحتوي المشغّل على 10 صور كحد أقصى.", "يمكنك حفظ 5 مشغّلات كحد أقصى.",
            "لا يمكن استخدام هذا الملف. اختر صورة PNG أو JPG أو GIF حتى 10 ميغابايت.",
            "اسحب لإعادة الترتيب. الصورة العليا تكون في المقدمة.", "صورة {0}" },
        ["hi"] = new[] { "Windows के साथ शुरू करें", "विकल्प", "स्थिति", "दाएं", "बाएं", "भाषा", "बंद करें", "अपडेट उपलब्ध है",
            "संस्करण {0} उपलब्ध है। आप संस्करण {1} का उपयोग कर रहे हैं।", "नया क्या है", "अपडेट करें", "बाद में", "अपडेट डाउनलोड किया जा रहा है...",
            "अपडेट इंस्टॉल नहीं किया जा सका। कृपया बाद में पुनः प्रयास करें।", "पुनः आरंभ करें", "गीत के बोल", "अपना प्लेयर डिज़ाइन करें",
            "बदलाव अपने आप सहेजे जाते हैं।", "प्लेयर", "डिफ़ॉल्ट", "कस्टम प्लेयर", "नया प्लेयर", "नाम", "प्लेयर हटाएं", "पृष्ठभूमि", "बटन",
            "गिरगिट: कवर से रंग", "कस्टम रंग", "चित्र", "चित्र जोड़ें", "PNG, JPG या GIF, 10 MB तक। हर प्लेयर में 10 चित्र तक।",
            "खिसकाने के लिए चित्र को, आकार बदलने के लिए उसके कोने को और घुमाने के लिए ऊपर के बिंदु को खींचें। माउस व्हील: आकार। Delete: निकालें।",
            "निकालें", "रीसेट करें", "इस प्लेयर को डिफ़ॉल्ट रंगों पर रीसेट करें और इसके चित्र निकालें?",
            "इस प्लेयर को हटाएं?", "रद्द करें", "एक प्लेयर में अधिकतम 10 चित्र हो सकते हैं।", "आप अधिकतम 5 प्लेयर सहेज सकते हैं।",
            "यह फ़ाइल इस्तेमाल नहीं की जा सकती। 10 MB तक का PNG, JPG या GIF चुनें।",
            "क्रम बदलने के लिए खींचें। सबसे ऊपर वाला चित्र सबसे आगे होता है।", "चित्र {0}" }
    };

    string Tr(string key) =>
        (Strings.TryGetValue(_lang, out var t) ? t : Strings["en"])[Array.IndexOf(StrKeys, key)];

    void ApplyTrayTexts()
    {
        _miLyrics.Text = Tr("lyrics");
        _miStartup.Text = Tr("startup");
        _miOptions.Text = Tr("options");
        _miPosition.Text = Tr("position");
        _miRight.Text = Tr("right");
        _miLeft.Text = Tr("left");
        _miLanguage.Text = Tr("language");
        _miRestart.Text = Tr("restart");
        _miClose.Text = Tr("close");
        if (_menu != null) _menu.RightToLeft = _lang == "ar" ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
    }

    void UpdateTrayChecks()
    {
        try { _miStartup.Checked = IsStartupEnabled(); } catch { }
        _miLyrics.Checked = _lyricsOn;
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
        CoverChanged?.Invoke();     // the options preview puts the cover on the same side
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
        _options = new OptionsWindow(this) { FlowDirection = _lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        _options.Closed += (_, _) => _options = null;
        _options.Show();
        _options.Activate();
    }

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
        // TextButton uses the icon font, which has no letters: text buttons need a text font
        var textFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var title = new TextBlock
        {
            Text = Tr("updTitle"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White
        };
        var body = new TextBlock
        {
            Text = string.Format(Tr("updBody"), info.Version, Updater.Current),
            Foreground = light, Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap
        };
        var header = new TextBlock
        {
            Text = Tr("whatsNew"), FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var notes = new TextBox
        {
            Text = Updater.PickNotes(info.Notes, _lang),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = System.Windows.Media.Brushes.White,
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
            Content = Tr("updUpdate"), Style = buttonStyle, FontFamily = textFont, Padding = new Thickness(16, 6, 16, 6),
            MinWidth = 90, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x96, 0xDD)), Margin = new Thickness(0, 0, 8, 0)
        };
        var later = new Button
        {
            Content = Tr("updLater"), Style = buttonStyle, FontFamily = textFont, Padding = new Thickness(16, 6, 16, 6),
            MinWidth = 90
        };
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
                ErrorLog.Write("update install", ex);
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
        _firstRun = !File.Exists(SettingsPath);
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
                else if (k == "lyrics") _lyricsOn = v == "1";
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
                $"userX={(_userX is int x ? x.ToString() : "")}",
                $"lyrics={(_lyricsOn ? 1 : 0)}"
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
        TitlePopup.IsOpen = false;
        foreach (var b in new[] { BtnPrev, BtnPlay, BtnNext, BtnMix, BtnLoop })
            b.IsEnabled = false;
        BtnPlay.Content = "\uE768";
        BtnLoop.Content = "\uE8EE";
        ApplyCover(null);
        UpdateProgressUi();
        ApplyVolumeUi(false);
        UpdateTimers();
        _lyricsKey = "";
        SetLyrics(null, "no media");        // otherwise the last line stays on screen instead of the controls
        _mediaStatus = "no media session";
    }

    void Refresh()
    {
        if (_debug == null) return;
        var pos = CurrentPosition();
        var sb = new StringBuilder();
        sb.AppendLine($"media  : {_mediaStatus} | source : {_source}");
        sb.AppendLine();
        sb.AppendLine($"song   : {_title}");
        sb.AppendLine($"artist : {_artist}");
        sb.AppendLine($"album  : {_album}");
        sb.AppendLine($"time   : {pos.ToString(@"m\:ss\.f")} / {(_tlEnd - _tlStart).ToString(@"m\:ss")}");
        sb.AppendLine($"lyrics : {_lyricsStatus}");
        if (_lyrics != null)
        {
            sb.AppendLine();
            int now = Lyrics.LineAt(_lyrics, pos);
            for (int i = Math.Max(0, now - 1); i <= Math.Min(_lyrics.Count - 1, now + 2); i++)
                sb.AppendLine($"{(i == now ? ">" : " ")} {_lyrics[i].Time.ToString(@"m\:ss\.ff")}  {_lyrics[i].Text}");
        }
        _info.Text = sb.ToString();
    }

    bool IsSpotify => string.Equals(ProcessKey(_source), "Spotify", StringComparison.OrdinalIgnoreCase);

    bool LyricsActive => _lyricsOn && _lyrics != null && IsSpotify;

    // Finds synced lyrics for the Spotify song: disk cache first, then LRCLIB. Nothing is asked while lyrics are off
    // (the debug window still looks them up).
    async void LoadLyrics()
    {
        if (!_lyricsOn && !_showDebug) return;
        var dur = _tlEnd - _tlStart;
        string song = $"{_title}|{_artist}|{(int)dur.TotalSeconds}";
        string key = _source + "|" + song;
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        if (key != _lyricsRetryKey) { _lyricsRetryKey = key; _lyricsRetries = 0; _lyricsRetryTimer.Stop(); }
        if (!IsSpotify) { SetLyrics(null, "not Spotify"); return; }
        if (string.IsNullOrWhiteSpace(_title) || dur.TotalSeconds < 1)
        {
            _lyricsKey = "";        // look again when the length arrives
            SetLyrics(null, "waiting for the song length");
            return;
        }

        var cached = Lyrics.FromCache(song);
        if (cached != null) { SetLyrics(cached, $"found (cache, {cached.Count} lines)"); return; }

        SetLyrics(null, "searching...");
        Lyrics.Result r;
        bool failed = false;
        try { r = await Lyrics.FetchAsync(_title, _artist, _album, dur); }
        catch (Exception ex) { r = new(null, "error: " + ex.Message); failed = true; }
        if (key != _lyricsKey) return;          // the song changed meanwhile
        if (r.Lines != null) Lyrics.ToCache(song, r.Lrc);
        if (failed && _lyricsRetries < 3)       // LRCLIB busy (503) or offline: again after 5, 15, 45 s
        {
            _lyricsRetryTimer.Interval = TimeSpan.FromSeconds(5 * Math.Pow(3, _lyricsRetries++));
            _lyricsRetryTimer.Start();
        }
        SetLyrics(r.Lines, r.Status);
        if (_showDebug)
            try { File.AppendAllText(LyricsLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{_title}\t{_artist}\t{dur.ToString(@"m\:ss")}\t{r.Status}\n"); } catch { }
    }

    void SetLyrics(List<(TimeSpan Time, string Text)>? lines, string status)
    {
        _lyrics = lines;
        _lyricsStatus = status;
        _lyricShown = (-2, 0);
        ApplyLyricsLayer();
        UpdateLyricsUi();
        Refresh();
    }

    // Lyrics stand in for the controls while the mouse is away from the player
    void ApplyLyricsLayer()
    {
        bool active = LyricsActive;
        bool lyrics = active && !PlayerPanel.IsMouseOver;
        if (active) LyricsLayer.Visibility = Visibility.Visible;
        var toLyrics = new DoubleAnimation(lyrics ? 1 : 0, TimeSpan.FromMilliseconds(150));
        toLyrics.Completed += (_, _) => { if (!LyricsActive) LyricsLayer.Visibility = Visibility.Collapsed; };
        LyricsLayer.BeginAnimation(OpacityProperty, toLyrics);
        ControlsGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(lyrics ? 0 : 1, TimeSpan.FromMilliseconds(150)));
    }

    // Shows the line being sung (♪ between lines) and sets the timer for the next change.
    // A line too long for the player is shown in two halves, each for half of the line's time.
    void UpdateLyricsUi()
    {
        _lyricsTimer.Stop();
        if (!LyricsActive || _lyrics == null) return;
        var pos = CurrentPosition();
        int i = Lyrics.LineAt(_lyrics, pos);
        string text = i >= 0 ? _lyrics[i].Text : "";
        var start = i >= 0 ? _lyrics[i].Time : TimeSpan.Zero;
        var next = i + 1 < _lyrics.Count ? _lyrics[i + 1].Time : _tlEnd - _tlStart;
        var due = next;
        int part = 0;
        if (text.Length > 0 && SplitToFit(text) is var (first, second))
        {
            var mid = start + (next - start) / 2;
            if (pos < mid) { text = first; due = mid; }
            else { text = second; part = 1; }
        }
        if (text.Length == 0) text = "♪";

        if ((i, part) != _lyricShown)
        {
            ShowLyric(text, animate: _lyricShown.Line != -2 && _shown && _expanded);
            _lyricShown = (i, part);
        }
        if (_playing && _shown && _expanded && due > pos)
        {
            _lyricsTimer.Interval = due - pos + TimeSpan.FromMilliseconds(15);
            _lyricsTimer.Start();
        }
    }

    // The new line comes up from below while the old one leaves upwards, both fading (like Spotify)
    void ShowLyric(string text, bool animate)
    {
        var old = _lyricFront ?? LyricA;
        var cur = old == LyricA ? LyricB : LyricA;
        _lyricFront = cur;
        cur.Text = text;
        if (animate)
        {
            SlideLyric(old, 0, -LyricShift, 0);
            SlideLyric(cur, LyricShift, 0, 1);
        }
        else
        {
            SlideLyric(old, 0, 0, 0, instant: true);
            SlideLyric(cur, 0, 0, 1, instant: true);
        }
    }

    static void SlideLyric(TextBlock line, double fromY, double toY, double toOpacity, bool instant = false)
    {
        var move = (TranslateTransform)line.RenderTransform;
        var length = TimeSpan.FromMilliseconds(instant ? 0 : LyricMs);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };     // starts and ends gently, like Spotify's scroll
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, length) { EasingFunction = ease });
        line.BeginAnimation(OpacityProperty, new DoubleAnimation(toOpacity, length) { EasingFunction = ease });
    }

    // null when the line fits, otherwise its two halves, split at the space nearest the middle
    (string, string)? SplitToFit(string text)
    {
        double room = LyricsLayer.ActualWidth > 0 ? LyricsLayer.ActualWidth : 338 - 24;
        var size = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(LyricA.FontFamily, LyricA.FontStyle, LyricA.FontWeight, LyricA.FontStretch),
            LyricA.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (size.WidthIncludingTrailingWhitespace <= room) return null;
        int mid = text.Length / 2;
        for (int d = 0; d < mid; d++)
        {
            if (text[mid - d] == ' ') return (text[..(mid - d)].Trim(), text[(mid - d + 1)..].Trim());
            if (mid + d < text.Length && text[mid + d] == ' ') return (text[..(mid + d)].Trim(), text[(mid + d + 1)..].Trim());
        }
        return null;        // one long word: the ellipsis has to do
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _poll?.Stop();
        _measureTimer.Stop();
        _progressTimer.Stop();
        _zTimer.Stop();
        _updateTimer.Stop();
        _hoverTimer.Stop();
        _debugTimer.Stop();
        _lyricsTimer.Stop();
        _lyricsRetryTimer.Stop();
        _stickerAnim.Clear();
        StopWheelHook();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_locHook != IntPtr.Zero) UnhookWinEvent(_locHook);
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        try { Automation.RemoveAllEventHandlers(); } catch { }
    }
}
