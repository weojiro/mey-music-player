using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    const float VolumeStep = 0.01f;   // one wheel notch on the volume icon or the tray icon; two while the wheel spins fast

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
    volatile int _startLeft = -1;         // the Start button's left edge (taskbar scan); -1 = not seen yet
    bool _center;                         // "Center": the cover in the middle of the taskbar (its side is Left)
    bool _above;                          // "Center, above": in the middle just above the taskbar (with _center)
    const double AboveGap = 6;            // between the taskbar and the cover, at 100 % scaling
    string _lang = "en";
    bool _firstRun;                       // no settings.ini yet
    bool _dragging, _dragMoved;
    int _dragStartCursorX, _dragStartCoverX, _dragX, _coverSize, _spansVersion, _posVersion, _pxVal;
    (int, int, int, int) _pxKey;
    readonly DispatcherTimer _zTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Window? _options;
    Window? _equalizer;

    // --- free move (anywhere on the screen), always on top, the two shortcuts ---
    bool _free, _onTop, _hidden, _clickThrough, _cursorHidden;
    double? _freeFx, _freeFy;             // where the closed cover rests in free mode: 0..1 across its screen
    int _size = 100;                      // player size in free move, % of the taskbar size
    string _freeMon = "";                 // that screen's name (\\.\DISPLAY1)
    int? _oldFreeX, _oldFreeY;            // v0.5.0 kept pixels: turned into the above at the first start
    int _gapY, _restX, _winW, _winX;      // cover top, where the cover rests now, window width and left now
    int _dragStartCursorY, _dragStartCoverY, _dragY;
    readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    // Shortcut 0 shows / hides the player, shortcut 1 gives a cursor in the player. One with a key goes through
    // RegisterHotKey; one of modifiers only (Ctrl + Alt) is polled: a short press with nothing else counts.
    const int TapMs = 500;
    readonly Shortcut[] _keys =
    {
        new() { Mods = MOD_CONTROL | MOD_ALT },
        new() { Mods = MOD_CONTROL | MOD_ALT, Vk = 0x58 }
    };
    bool _keysPaused;
    long _lastHotkeyAt = -100000;
    readonly DispatcherTimer _keyTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    // mouse out of the game (shortcut 1)
    bool _mouseOut, _playerFocused;
    IntPtr _dropsWin;                     // a game seen dropping to the desktop without the focus: the drawn cursor next time
    // games (process names) that drop to the desktop without the focus, kept in settings.ini; GTA San Andreas (MTA,
    // SA-MP) runs in exclusive full screen, which Windows 11 often doesn't report
    readonly HashSet<string> _focusGames = new(StringComparer.OrdinalIgnoreCase) { "gta_sa" };

    // ... or, in a game that changed the screen's resolution, a cursor drawn in the player
    bool _cursorMode, _cursorAnchored, _vDown, _hadClip;
    FrameworkElement? _vPress;            // what the drawn cursor pressed (cover, seek bar, speaker, a button)
    RECT _savedClip;
    double _vx, _vy;                      // the drawn cursor, screen pixels
    long _lastCursorInput;
    IntPtr _cursorHook;
    LowLevelMouseProc? _cursorProc;
    readonly DispatcherTimer _cursorIdleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    const int CursorIdleMs = 15000;       // the mouse goes back to the game after this long without moving
    Window? _focusWin;                    // tiny MMP window that holds the focus meanwhile
    IntPtr _gameWin;                      // the app that had the focus: it gets it back

    // --- tray menu ---
    readonly Forms.ToolStripMenuItem _miStartup = new(), _miOptions = new(), _miEq = new(), _miPosition = new(),
        _miRight = new(), _miLeft = new(), _miCenter = new(), _miAbove = new(), _miLanguage = new(), _miRestart = new(), _miClose = new(), _miLyrics = new(),
        _miHide = new(), _miDiscord = new();
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

    // --- lyrics (Spotify only, from LRCLIB; the tray toggle is on by default) ---
    const int LyricMs = 300;                // line change animation
    const double LyricShift = 14;           // design units a line moves when it comes in or leaves
    bool _lyricsOn = true;                  // a settings.ini without "lyrics=" (first run) keeps it on
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
        _placementOwner = _designs.Active;          // settings.ini holds the placement of the player in use
        ApplySideLayout();
        _zTimer.Tick += (_, _) => { KeepOnTop(); if (_above && !_free) Update(); };
        _cursorTimer.Tick += (_, _) => UpdateClickThrough();
        Deactivated += (_, _) => { if (_mouseOut && _playerFocused) EndMouseOut(); };     // the game or another app was clicked
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Update));                    // free mode: moved to a screen with another scale
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;              // resolution or screens changed
        _wheelTimer.Tick += (_, _) => { if (Environment.TickCount64 - _lastTrayHover > 5000) StopWheelHook(); };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);   // first check 5 s after the start, then once a day
            CheckForUpdate();
        };
        _defaultCoverBrush = Cover.Background;

        TitlePopup.Placement = PlacementMode.Custom;
        TitlePopup.CustomPopupPlacementCallback = PlaceTitleCard;
        VolumePopup.Placement = PlacementMode.Custom;
        VolumePopup.CustomPopupPlacementCallback = PlaceVolumeCard;
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
            CoverPress(PointToScreen(e.GetPosition(this)));
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

        Cover.MouseWheel += (_, e) => { e.Handled = true; CoverWheel(e.Delta); };

        Cover.MouseMove += (_, e) =>
        {
            if (Cover.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) CoverDragTo(PointToScreen(e.GetPosition(this)));
        };
        Cover.MouseLeftButtonUp += (_, _) =>
        {
            if (!Cover.IsMouseCaptured) return;
            Cover.ReleaseMouseCapture();
            CoverRelease();
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
        VolumeHit.MouseEnter += (_, _) => ShowVolumeCard();
        VolumeHit.MouseLeave += (_, _) => VolumePopup.IsOpen = false;
        VolumeHit.MouseWheel += (_, e) =>
        {
            ChangeVolume(WheelVolumeStep(e.Delta));
            e.Handled = true;
        };

        SetNoMedia();
        BuildStickers();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => Update();      // WPF has shown the window now: hide or place it
        Closed += OnClosed;
    }

    // ---------- the cover: click = open / close, drag = move (also used by the drawn cursor) ----------

    bool _dragBlocked;                  // a drag on "Center": nothing moves, and it is no click either

    void CoverPress(Point at)
    {
        _clicks++;
        _dragMoved = _dragBlocked = false;
        _dragStartCursorX = (int)at.X;
        _dragStartCursorY = (int)at.Y;
        _dragStartCoverX = _gapX;
        _dragStartCoverY = _gapY;
    }

    void CoverDragTo(Point at)
    {
        int dx = (int)at.X - _dragStartCursorX, dy = (int)at.Y - _dragStartCursorY;
        if (!_free) dy = 0;
        if (!_dragMoved && Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;       // small wobble = still a click
        // "Center" and "Center, above" stay in the middle: a drag moves nothing (and is no click either)
        if (_center && !_free) { _dragMoved = _dragBlocked = true; return; }
        if (!_dragMoved)
        {
            // the card stays open through a click, only a real drag hides it
            _hoverTimer.Stop();
            TitlePopup.IsOpen = false;
        }
        _dragMoved = true;
        _dragging = true;
        if (_free)
        {
            // the whole window (the open player too) stays on the screen under the cursor
            var mon = MonitorAt((int)at.X, (int)at.Y);
            int extra = _winW - _coverSize;                 // the open part of the player
            int lo = _side == Side.Left ? mon.Left : mon.Left + extra;
            int hi = _side == Side.Left ? mon.Right - _winW : mon.Right - _coverSize;
            _dragX = Math.Clamp(_dragStartCoverX + dx, lo, Math.Max(lo, hi));
            _dragY = Math.Clamp(_dragStartCoverY + dy, mon.Top, Math.Max(mon.Top, mon.Bottom - _coverSize));
        }
        else _dragX = Math.Clamp(_dragStartCoverX + dx, _last.Left, Math.Max(_last.Left, TrayLeft - _coverSize));
        Update();
    }

    void CoverRelease()
    {
        if (_dragMoved)
        {
            if (_dragBlocked) return;       // a drag that moved nothing ("Center")
            DropCover();
            if (Cover.IsMouseOver && !string.IsNullOrWhiteSpace(_title)) _hoverTimer.Start();   // card comes back after the drop
        }
        else ToggleExpand();
    }

    // wheel forward = next song, back = previous; touchpads send many small steps, so skip once per 300 ms
    void CoverWheel(int delta)
    {
        if (Environment.TickCount64 - _lastWheelSkip < 300) return;
        _lastWheelSkip = Environment.TickCount64;
        _ = delta > 0 ? _session?.TrySkipNextAsync() : _session?.TrySkipPreviousAsync();
    }

    // Finds the taskbar and follows it: its moves (a hidden taskbar sliding in) and its icons. Again after Explorer
    // restarted (a crash, an update, a changed taskbar setting): the old taskbar is gone and a new one comes.
    void AttachTaskbar()
    {
        if (_locHook != IntPtr.Zero) { UnhookWinEvent(_locHook); _locHook = IntPtr.Zero; }
        _taskbar = FindWindow("Shell_TrayWnd", null);
        _fullHeight = 0;
        _needMeasure = true;
        _pxKey = default;
        if (_taskbar == IntPtr.Zero) return;
        GetWindowThreadProcessId(_taskbar, out uint pid);
        _locHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _proc!, pid, 0, WINEVENT_OUTOFCONTEXT);
        var taskbar = _taskbar;
        Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(taskbar);
                Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, _structHandler);
            }
            catch { }
        });
    }

    // sent to every window when Explorer has made a new taskbar
    static readonly int WM_TASKBARCREATED = (int)RegisterWindowMessage("TaskbarCreated");

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        _proc = OnWinEvent;
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _structHandler = (_, _) => Dispatcher.BeginInvoke(new Action(OnStructureChanged));
        AttachTaskbar();

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

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _keyTimer.Tick += (_, _) => PollShortcuts();
        if (DiscordAppId.Length > 0)
        {
            _discord = new DiscordPresence(DiscordAppId);
            _discordTimer.Tick += (_, _) => _discord.Set(DiscordActivity());
            _discordTimer.Start();
        }
        _cursorIdleTimer.Tick += (_, _) => { if (Environment.TickCount64 - _lastCursorInput > CursorIdleMs) SetCursorMode(false); };
        RegisterShortcuts();

        CreateTray();
        InitMedia();
        Task.Run(Lyrics.TrimCache);
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
            // Windows' own full-screen views (Alt + Tab, Task View) belong to the taskbar's process (explorer):
            // not a game, or "show over games" would put the cover on a hidden taskbar for a moment
            GetWindowThreadProcessId(fg, out uint fgPid);
            GetWindowThreadProcessId(_taskbar, out uint shellPid);
            if (fgPid != 0 && fgPid == shellPid) return false;
            if (cls is "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "TaskSwitcherWnd" or "ForegroundStaging")
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
            SetCursorMode(false);       // another app took the screen: the mouse goes back
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
        if (_free) { UpdateFree(); return; }
        if (_taskbar == IntPtr.Zero || !GetWindowRect(_taskbar, out RECT r)) return;
        _last = r;
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);

        bool wide = r.Width >= _screenW / 2;
        bool atBottom = wide && r.Top > _screenH / 2;
        bool atTop = wide && r.Bottom < _screenH / 2;
        _edge = atBottom ? "bottom" : atTop ? "top" : "side";

        bool taskbarVisible = (atBottom && r.Top < _screenH - 3) || (atTop && r.Bottom > 3);
        bool fullScreen = IsFullScreenAppActive();
        // "show over games": over a full-screen game the cover stays in its place on the taskbar,
        // also when the taskbar hides itself under the game
        bool overGame = _onTop && fullScreen && (atBottom || atTop);
        // "Center, above" shows with the taskbar, open or not; a taskbar that hides itself while the mouse moves up onto
        // the player leaves it where it is until the mouse goes
        bool keepAbove = _above && _shown && (atBottom || atTop) && (_dragging || CursorOnPlayer());
        // the taskbar really in front: on the screen, and over a full-screen window only when it came up over it
        bool taskbarInFront = taskbarVisible && (!fullScreen || TaskbarOverForeground());
        bool sinking = _above && _shown && !taskbarVisible && !overGame && !keepAbove && !_hidden;
        bool visible = (taskbarVisible || overGame || keepAbove || sinking) && !_hidden && (_onTop || !fullScreen);

        bool fullyOn = atBottom ? r.Bottom <= _screenH : atTop && r.Top >= 0;
        if (fullyOn) _fullHeight = r.Height;
        // the icons may have moved while the taskbar was away (apps opened, closed): look again once it is up
        if (fullyOn && !_wasFullyOn && _scans > 0) RunMeasure();
        _wasFullyOn = fullyOn;
        // on the taskbar, the cover waits for the first look at the icons, so it never shows up on them
        if (!_above && _scans == 0)
        {
            if (visible && !_measuring) RunMeasure();
            visible = false;
        }

        int currentTaskbarH = r.Height;
        int h = (_fullHeight > 0 && _fullHeight <= currentTaskbarH) ? _fullHeight : currentTaskbarH;
        int size = Math.Max(12, h - 2 * GapPx);

        if (visible && size >= 12)
        {
            if (!_shown && _needMeasure) RunMeasure();
            int fullW = PrepareShow(size);
            // "Center, above" is off the taskbar: the middle, without moving away from the icons
            int middle = r.Left + (r.Right - r.Left) / 2;
            int restX = _above ? middle - size / 2 : PlaceX(size, r.Left);
            int openX = _above ? Math.Clamp(middle - fullW / 2, r.Left, Math.Max(r.Left, r.Right - fullW))
                               : _t > 0 ? OpenX(restX, size, r.Left) : restX;
            _restX = restX;
            int coverX = _dragging ? _dragX : restX + (int)Math.Round((openX - restX) * _t);
            // a hidden taskbar is moved off the screen: the cover goes where the taskbar is when it shows
            int barTop = taskbarVisible ? r.Top : atBottom ? _screenH - h : 0;
            int coverY = barTop + (h - size) / 2;
            if (_above)
            {
                // above the taskbar (below one at the top), going up and down with it; with no taskbar in front (a
                // full-screen game) where the taskbar would be, like "Center". Under the mouse it stays put: a hiding
                // taskbar would pull it away from under the mouse. Without the taskbar it sinks off the screen.
                int gap = (int)Math.Round(AboveGap * VisualTreeHelper.GetDpi(this).DpiScaleX);
                int state = overGame && !taskbarInFront ? YSpot : sinking ? YSink : YAbove;
                if (state == YAbove)
                    // a taskbar all the way up always takes it back above it (it may have stopped half way down,
                    // under the mouse, as the taskbar was hiding)
                    coverY = taskbarVisible && (!keepAbove || fullyOn) ? (atBottom ? r.Top - gap - size : r.Bottom + gap) : _gapY;
                else if (state == YSink)
                    coverY = atBottom ? _screenH : -size;
                coverY = SmoothY(coverY, state);
                if (state == YSink && !_yAnimating) { HideWindow(); UpdateStickerAnim(); Refresh(); return; }
            }
            Place(coverX, coverY, size, fullW);
        }
        else HideWindow();

        UpdateStickerAnim();
        Refresh();
    }

    // Free move: the cover rests where the user dropped it, anywhere on a screen. Opening pushes it away from the
    // edge of the screen, the way it is pushed away from the taskbar icons, so the player never leaves the screen.
    void UpdateFree()
    {
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);
        int size = FreeSize();
        bool visible = !_hidden && (_onTop || !IsFullScreenAppActive());
        // a big player never gets wider or taller than its screen
        var screen = MonitorByName(_freeMon);
        size = Math.Max(12, Math.Min(size, Math.Min((int)(screen.Width * 40 / DesignW), screen.Height)));
        if (visible)
        {
            int fullW = PrepareShow(size);
            if (_freeFx == null || _freeFy == null)
            {
                // the first time: the spot saved by v0.5.0, where it is on the taskbar now,
                // or (never shown yet) above the bottom right corner of the main screen's work area
                if (_oldFreeX is int ox && _oldFreeY is int oy) SetFreeSpot(ox, oy, size);
                else if (_gapY > 0) SetFreeSpot(_gapX, _gapY, size);
                else
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    GetMonitorInfo(MonitorFromPoint(default, MONITOR_DEFAULTTONEAREST), ref info);
                    SetFreeSpot(info.rcWork.Right - fullW - 16, info.rcWork.Bottom - size - 16, size);
                }
                SaveSettings();
            }
            // the same place on the screen in proportion, also after the resolution changed (a game at 1080p on a 1440p screen)
            var mon = MonitorByName(_freeMon);
            int restX = mon.Left + (int)Math.Round(_freeFx!.Value * Math.Max(0, mon.Width - size));
            int restY = mon.Top + (int)Math.Round(_freeFy!.Value * Math.Max(0, mon.Height - size));
            _restX = restX;
            int openX = _side == Side.Left
                ? Math.Max(mon.Left, Math.Min(restX, mon.Right - fullW))
                : Math.Min(mon.Right - size, Math.Max(restX, mon.Left + fullW - size));
            int coverX = _dragging ? _dragX : restX + (int)Math.Round((openX - restX) * _t);
            int coverY = _dragging ? _dragY : restY;
            // the title card goes below the cover when there is no room above it
            var at = MonitorAt(coverX + size / 2, coverY + size / 2);
            _edge = coverY - at.Top < size * 2 ? "top" : "bottom";
            Place(coverX, coverY, size, fullW);
        }
        else HideWindow();

        UpdateStickerAnim();
        Refresh();
    }

    // The cover size in free mode: the same as on the taskbar, or the size of a standard taskbar when the taskbar
    // is on the side of the screen or can't be found
    int FreeSize()
    {
        int h = 0;
        if (_taskbar != IntPtr.Zero && GetWindowRect(_taskbar, out RECT r) && r.Width >= _screenW / 2)
            h = _fullHeight > 0 ? _fullHeight : r.Height;
        if (h <= 0 || h > 200) h = (int)Math.Round(48 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        return Math.Max(12, (int)Math.Round((h - 2 * GapPx) * _size / 100.0));
    }

    // Scales the player to the cover size; returns the width of the open player in pixels
    int PrepareShow(int size)
    {
        if (!_shown && _mediaDirty) { _mediaDirty = false; RefreshMedia(); }
        double f = size / 40.0;
        double s = f / VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(Scale.ScaleX - s) > 0.001) { Scale.ScaleX = s; Scale.ScaleY = s; }
        _coverSize = size;
        return (int)Math.Round(DesignW * f);
    }

    // Puts the window on the screen with the cover at (coverX, coverY), open by _t
    void Place(int coverX, int coverY, int size, int fullW)
    {
        _gapX = coverX;
        _gapY = coverY;
        int winX, width;
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
        _winW = size + (int)Math.Round((fullW - size) * _t);    // what is drawn, for both sides
        _winX = winX;
        if (_cursorMode) MoveDrawnCursor();         // the player moved or opened under the drawn cursor

        SetWindowPos(_hwnd, HWND_TOPMOST, winX, coverY, width, size,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        RepositionTitleCard();
        bool was = _shown;
        _shown = true;
        if (!was) UpdateTimers();
    }

    void HideWindow()
    {
        if (!_shown && !IsWindowVisible(_hwnd)) return;     // WPF shows the window at start even when the taskbar is hidden
        SetCursorMode(false);
        if (Cover.IsMouseOver) _hidWhileOver++;
        _hoverTimer.Stop();
        TitlePopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        _shown = false;
        UpdateTimers();
    }

    static RECT MonitorAt(int x, int y)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref info)) return info.rcMonitor;
        return new RECT { Right = GetSystemMetrics(SM_CXSCREEN), Bottom = GetSystemMetrics(SM_CYSCREEN) };
    }

    // Free move keeps the cover's place as a share of its screen (0 = left / top edge, 1 = right / bottom edge)
    void SetFreeSpot(int x, int y, int size)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr mon = MonitorFromPoint(new POINT { X = x + size / 2, Y = y + size / 2 }, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero || !GetMonitorInfoEx(mon, ref info)) return;
        var r = info.rcMonitor;
        _freeMon = info.szDevice ?? "";
        _freeFx = r.Width > size ? Math.Clamp((x - r.Left) / (double)(r.Width - size), 0, 1) : 0;
        _freeFy = r.Height > size ? Math.Clamp((y - r.Top) / (double)(r.Height - size), 0, 1) : 0;
    }

    // the screen with that name, or the main screen when it is gone
    static RECT MonitorByName(string name)
    {
        RECT? found = null;
        if (name.Length > 0)
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoEx(mon, ref info) && info.szDevice == name) { found = info.rcMonitor; return false; }
                return true;
            }, IntPtr.Zero);
        return found ?? MonitorAt(0, 0);
    }

    void OnDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => { _pxKey = default; Update(); }));

    int TrayLeft => _trayLeft > 0 ? _trayLeft : (int)(_screenW * 0.75);
    bool _wasFullyOn;

    int FullWidth(int size) => (int)Math.Round(DesignW * size / 40.0);

    int PlaceX(int size, int taskbarLeft)
    {
        var key = (size, taskbarLeft, _spansVersion, _posVersion);
        if (key == _pxKey) return _pxVal;
        int wanted = _userX ?? (_center ? _last.Left + (_last.Right - _last.Left - size) / 2
                              : _side == Side.Left ? taskbarLeft - 100000 : _screenW + 100000);
        int need = _side == Side.Right ? FullWidth(size) : size;
        int x = SnapToFree(wanted, size, taskbarLeft, need);
        if (x < 0) x = _side == Side.Left ? taskbarLeft + MarginPx : TrayLeft - MarginPx - size;
        _pxKey = key;
        _pxVal = x;
        return x;
    }

    int OpenX(int restX, int size, int taskbarLeft)
    {
        // "Center": the whole open player sits in the middle of the taskbar, not only the cover
        int wanted = _center && _userX == null ? _last.Left + (_last.Right - _last.Left - FullWidth(size)) / 2 : restX;
        int x = SnapToFree(wanted, size, taskbarLeft, FullWidth(size));
        if (x >= 0) return x;
        // no free room is wide enough: open from the widest one, so as few icons as possible are covered
        var widest = FreeGaps(taskbarLeft).OrderByDescending(g => g.b - g.a).FirstOrDefault();
        if (widest.b <= widest.a) return restX;
        return _side == Side.Left ? widest.a + MarginPx : widest.b - MarginPx - size;
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
        if (_free)
        {
            SetFreeSpot(_dragX, _dragY, _coverSize);
            SaveSettings();
            Update();
            return;
        }
        int x = SnapToFree(_dragX, _coverSize, _last.Left, _coverSize);
        if (x >= 0)
        {
            _userX = x;
            _center = _above = false;       // a spot of the user's own: no longer the middle
        }
        _posVersion++;
        RunMeasure();       // the icons may have moved since the last look: dropped on them, it moves off them
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
        bool fullScreen = IsFullScreenAppActive();
        if (fullScreen && !_onTop)
        {
            Update();
            return;
        }
        // the taskbar, or with "always on top" a full-screen game, must not cover the player
        IntPtr fg = fullScreen ? GetForegroundWindow() : IntPtr.Zero;
        IntPtr w = NativeMethods.GetWindow(_hwnd, GW_HWNDPREV);
        for (int guard = 0; w != IntPtr.Zero && guard < 300; guard++)
        {
            if (w == _taskbar || (fg != IntPtr.Zero && w == fg))
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
                    // hidden or off-screen items (a button Windows keeps but doesn't show) would block free room
                    if (rc.IsEmpty || rc.Width < 4 || rc.Height < 4 || info.IsOffscreen) continue;
                    int l = (int)rc.Left, rr = (int)Math.Ceiling(rc.Right);
                    bool counted = IsItemType(info.ControlType) && rr <= trayLeft;
                    if (info.AutomationId == "StartButton") _startLeft = l;
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

        if (_showDebug)     // only for the debug window: normal use doesn't write a file on every taskbar change
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DumpPath)!);
                File.WriteAllText(DumpPath, dump);
            }
            catch { }
        }

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
        // closing: the cover stays where opening pushed it ("Center" goes back to the middle)
        if (_expanded && _gapX != _restX && !_center)
        {
            if (_free) SetFreeSpot(_gapX, _gapY, _coverSize);
            else { _userX = _gapX; _posVersion++; }
            SaveSettings();
        }
        _expanded = !_expanded;
        if (!_expanded) VolumePopup.IsOpen = false;
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

    // "Center, above" glides between its places: above the taskbar, the taskbar's place over a game, off the screen
    const int YAbove = 0, YSpot = 1, YSink = 2;
    int _yState;
    bool _yAnimating;
    int _yFrom;
    readonly Stopwatch _ySw = new();

    int SmoothY(int target, int state)
    {
        if (!_shown) { _yState = state; StopYAnim(); return target; }      // appears right where it belongs
        if (state != _yState)
        {
            _yState = state;
            _yFrom = _gapY;
            _ySw.Restart();
            if (!_yAnimating) { _yAnimating = true; CompositionTarget.Rendering += OnYRender; }
        }
        if (!_yAnimating) return target;
        double p = Math.Min(1, _ySw.Elapsed.TotalMilliseconds / AnimMs);
        if (p >= 1) { StopYAnim(); return target; }
        double ease = 1 - Math.Pow(1 - p, 3);
        return _yFrom + (int)Math.Round((target - _yFrom) * ease);
    }

    void StopYAnim()
    {
        if (!_yAnimating) return;
        _yAnimating = false;
        CompositionTarget.Rendering -= OnYRender;
    }

    void OnYRender(object? sender, EventArgs e) => Update();

    // Is the taskbar above the foreground window? Under a full-screen game it is behind it; it comes in front when it
    // slides up over the game.
    bool TaskbarOverForeground()
    {
        IntPtr fg = GetForegroundWindow();
        IntPtr w = NativeMethods.GetWindow(_hwnd, GW_HWNDFIRST);       // the top of the z-order
        for (int guard = 0; w != IntPtr.Zero && guard < 1000; guard++)
        {
            if (w == _taskbar) return true;
            if (w == fg) return false;
            w = NativeMethods.GetWindow(w, GW_HWNDNEXT);
        }
        return false;
    }

    // the mouse on the player, or just left it: a small slip off it doesn't drop it
    long _lastOnPlayer;
    const int PlayerSlack = 16, PlayerGraceMs = 700;

    bool CursorOnPlayer()
    {
        if (!GetCursorPos(out var c)) return false;
        bool on = c.X >= _winX - PlayerSlack && c.X <= _winX + _winW + PlayerSlack
               && c.Y >= _gapY - PlayerSlack && c.Y <= _gapY + _coverSize + PlayerSlack;
        if (on) _lastOnPlayer = Environment.TickCount64;
        return on || Environment.TickCount64 - _lastOnPlayer < PlayerGraceMs;
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

        if (_shown && !_cursorTimer.IsEnabled) _cursorTimer.Start();
        else if (!_shown && _cursorTimer.IsEnabled) _cursorTimer.Stop();

        if (_shown && _expanded) UpdateLyricsUi();      // also re-times the next line after play / pause
        else _lyricsTimer.Stop();
    }

    // Right after sign-in (Start with Windows) Windows' media service may not be ready yet: try again a few times
    async void InitMedia()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(OnSessionChanged));
                OnSessionChanged();
                return;
            }
            catch (Exception ex)
            {
                _mediaStatus = "media API unavailable: " + ex.Message;
                Refresh();
                if (attempt >= 6) { ErrorLog.Write("media init", ex); return; }
            }
            await Task.Delay(5000);
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
        else RefreshHiddenInfo();
    }

    // While the player is hidden (a hidden taskbar, a full-screen game) Discord still shows what is playing:
    // only the play state and the names are read; the rest waits until the player shows (_mediaDirty)
    int _hiddenSeq;
    async void RefreshHiddenInfo()
    {
        var s = _session;
        if (s == null || _discord == null || !_discordOn) return;
        int seq = ++_hiddenSeq;
        try
        {
            var info = s.GetPlaybackInfo();
            _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            SetMediaType(info);
            ApplyTimeline(s.GetTimelineProperties());       // Discord's progress bar
            var props = await s.TryGetMediaPropertiesAsync();
            if (s != _session || seq != _hiddenSeq || _shown) return;
            _title = props.Title ?? "";
            _artist = props.Artist ?? "";
            _album = props.AlbumTitle ?? "";
            _source = s.SourceAppUserModelId ?? "";
            var thumb = await LoadThumbAsync(props);     // Discord shows the cover too
            if (s != _session || seq != _hiddenSeq || _shown) return;
            _thumb = thumb;
            FindDiscordCover();
        }
        catch (Exception ex) { ErrorLog.Write("media hidden", ex); }
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
        if (!_shown && !_showDebug)
        {
            _mediaDirty = true;
            if (kind != Smtc.Timeline) RefreshHiddenInfo();
            else if (_discordOn) try { if (_session is { } hs) ApplyTimeline(hs.GetTimelineProperties()); } catch { }
            return;
        }
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
            _thumb = cover;
            ApplyCover(cover);
            FindDiscordCover();
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
            // no cover (a video, a file without one): the music note, like with nothing playing; an app's own icon
            // looked blurry and out of place there
            Cover.Background = _defaultCoverBrush;
            Glyph.Text = "\uEC4F"; // music note
            Glyph.Visibility = Visibility.Visible;
            SetChameleon(DefaultBg, DefaultAccent);
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
        double bg = Solid(d?.BgClear), buttons = Solid(Math.Min(d?.ButtonsClear ?? 0, Designs.MaxButtonsClear));
        ApplyPalette(Designs.ParseColor(d?.Bg) ?? _chamBg, Designs.ParseColor(d?.Buttons) ?? _chamAccent, bg, buttons);
        StickerLayer.Opacity = bg;                  // the pictures sit on the background and fade with it
        Cover.Opacity = Solid(d?.CoverClear);
    }

    // how solid a part is, from its transparency in percent
    // At 100 % it is 1 / 255, not 0: a fully clear pixel lets clicks through to what is behind, a nearly clear one
    // cannot be seen but still takes them (an invisible cover still opens the player)
    internal static double Solid(int? clear) => Math.Max(1 / 255.0, 1 - Math.Clamp(clear ?? 0, 0, Designs.MaxClear) / 100.0);

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
        if (_designs.Active != _placementOwner) SwitchPlacement();
        ApplyDesignColors();
        BuildStickers();
    }

    // ---------- Discord: "Listening via MMP" on the user's profile (on by default, tray menu) ----------

    // the "mey music player" application on discord.com/developers: its name is what the profile shows
    const string DiscordAppId = "1562239814949150850";
    const string RepoUrl = "https://github.com/weojiro/mey-music-player";
    const string DiscordLogo = "https://weojiro.github.io/mey-music-player/img/mmp-logo.png?v=2";     // ?v: Discord keeps a copy per address
    DiscordPresence? _discord;
    bool _discordOn = true;
    readonly DispatcherTimer _discordTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Sent as a game, because Discord puts games before Spotify, with "Listening via MMP" (in the app's language) as the
    // text under the user's name. The song is shown only from music apps and music websites (MusicSources); anything
    // else stays private ("Something is playing"), and with nothing playing it says "Paused". The picture is the song's
    // cover, or the app's logo when its cover can't be found. Sent only when it changed (DiscordPresence compares).
    object? DiscordActivity()
    {
        if (!_discordOn) return _lastActivity = null;     // turned on again later: no old song while the cover loads
        var app = _title.Trim().Length > 0 ? MusicNow() : null;
        bool song = app != null;
        if (song)
        {
            FindDiscordCover();             // a website can be recognised a moment after the song started
            RetryBrowserCover(app!);
            // a new song waits a moment for its cover (the picture from Windows, then the lookup), so the name and
            // the cover change together on the profile
            string key = SongKey();
            if (key != _waitKey) { _waitKey = key; _waitSince = Environment.TickCount64; }
            bool pending = _coverSong != key || _coverAsked != 0;
            if (pending && Environment.TickCount64 - _waitSince < CoverWaitMs && _lastActivity != null) return _lastActivity;
        }
        static string Fit(string t) { t = t.Trim(); return t.Length < 2 ? t + "  " : t.Length > 128 ? t[..127] + "…" : t; }
        var buttons = new[] { new Dictionary<string, object> { ["label"] = Tr("dcButton"), ["url"] = RepoUrl } };
        if (!song)
        {
            // something private, or nothing at all: a small card that names MMP
            return _lastActivity = new Dictionary<string, object>
            {
                ["type"] = 2,                                       // listening
                ["status_display_type"] = 2,                        // the member list shows "Paused" / "Something is playing"
                ["details"] = Fit(Tr(_playing ? "dcSome" : "dcPaused")),
                ["state"] = Fit(_playing ? "mey music player" : Tr("dcIdle")),
                ["assets"] = new Dictionary<string, object>
                {
                    ["large_image"] = DiscordLogo, ["large_text"] = "mey music player · " + Tr("dcLine2"), ["large_url"] = RepoUrl
                },
                ["buttons"] = buttons
            };
        }
        // a song: laid out like a music app's own card (title, artist, album, a progress bar, the cover with the app's
        // logo in the corner); the member list shows the artist. Paused: "Paused" in place of the album, no bar
        string? cover = _coverSong == SongKey() ? _coverUrl : null;
        string large = cover ?? app!.LogoUrl ?? DiscordLogo;
        var assets = new Dictionary<string, object>
        {
            ["large_image"] = large,
            ["large_text"] = Fit(!_playing ? Tr("dcPaused") : _album.Trim().Length > 0 ? _album : _title)
        };
        if (cover != null && app!.LogoUrl != null) { assets["small_image"] = app.LogoUrl; assets["small_text"] = app.Name; }
        else if (large != DiscordLogo) { assets["small_image"] = DiscordLogo; assets["small_text"] = "mey music player"; assets["small_url"] = RepoUrl; }
        var activity = new Dictionary<string, object>
        {
            ["type"] = 2,                                           // listening
            ["status_display_type"] = 1,                            // the member list shows the state: the artist
            ["details"] = Fit(_title),
            ["state"] = Fit(_artist.Trim().Length > 0 ? _artist : app!.Name),
            ["assets"] = assets,
            ["buttons"] = buttons
        };
        if (_playing && SongTimes() is (long start, long end))
            activity["timestamps"] = new Dictionary<string, object> { ["start"] = start, ["end"] = end };
        return _lastActivity = activity;
    }

    // The progress bar: when the song started and when it ends, in Unix seconds. Kept while the song plays on as
    // expected (each new value would send the card again); moved when it was paused or skipped through.
    string _timesSong = "";
    long _timesStart;

    (long, long)? SongTimes()
    {
        var length = _tlEnd - _tlStart;
        if (length.TotalSeconds < 2) return null;
        long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)CurrentPosition().TotalSeconds;
        string song = SongKey();
        if (song != _timesSong || Math.Abs(start - _timesStart) > 2) { _timesSong = song; _timesStart = start; }
        return (_timesStart, _timesStart + (long)length.TotalSeconds);
    }

    // ---------- where the song plays (MusicSources): asked again until it is recognised, then kept for the song ----------

    string _appKey = "";
    MusicApp? _app;
    long _appCheckedAt;
    bool _appSure;                          // the answer for this song won't change (no more asking)
    bool _isMusic, _isVideo;                // what Windows says the media is

    MusicApp? MusicNow()
    {
        string key = _source + "\n" + SongKey();
        if (key != _appKey) { _appKey = key; _app = null; _appSure = false; _appCheckedAt = 0; }
        if (_app == null && !_appSure && Environment.TickCount64 - _appCheckedAt > 1500)
        {
            _appCheckedAt = Environment.TickCount64;
            _app = _isVideo ? null : MusicSources.Identify(_source, _isMusic, _title, out _appSure);
        }
        return _app;
    }

    // ---------- the song's cover on Discord (DiscordCover finds its web address) ----------

    const int CoverWaitMs = 1500;
    string _coverSong = "";                 // the song the cover is for
    string _coverLookup = "";               // that song and the picture Windows gave for it
    string? _coverUrl;                      // its cover's address; null: the app's logo
    long _coverAsked;                       // when the lookup started, 0 when it is done
    string _waitKey = "";                   // the song DiscordActivity last saw, and since when
    long _waitSince;
    object? _lastActivity;
    BitmapSource? _thumb;                   // the cover Windows gave for the song now (null: none)
    int _coverRetries;                      // SoundCloud: asked again while its tab may come to the front
    long _coverRetryAt;

    string SongKey() => $"{_title}\n{_artist}\n{_album}";

    // Looks the cover up when the song or its picture changes (Spotify can send the new name before the new
    // picture), when Discord is turned on and when a website is recognised
    void FindDiscordCover()
    {
        if (_discord == null || !_discordOn || !_playing || MusicNow() is not MusicApp app) return;
        string song = SongKey();
        var print = _thumb == null ? null : DiscordCover.PrintOf(_thumb);
        string lookup = song + "\n" + (print == null ? "" : DiscordCover.Hash(print));
        if (lookup == _coverLookup) return;
        if (song != _coverSong) _coverRetries = 0;
        _coverLookup = lookup;
        _coverSong = song;
        _coverUrl = null;
        _coverAsked = 0;
        if (print == null) return;          // no cover from the app: its logo
        if (DiscordCover.TryCached(lookup, DiscordCover.Hash(print), out var known)) { _coverUrl = known; return; }
        _coverAsked = Environment.TickCount64;
        _coverRetryAt = _coverAsked + 10000;
        _ = Ask(lookup, _title, _artist, print, app);

        async Task Ask(string lookup, string title, string artist, byte[] print, MusicApp app)
        {
            string? url = await DiscordCover.FindAsync(lookup, title, artist, print, app);
            if (lookup != _coverLookup) return;     // the song or its picture changed meanwhile
            _coverUrl = url;
            _coverAsked = 0;
            _discord?.Set(DiscordActivity());
        }
    }

    // SoundCloud's and YouTube Music's addresses can be read only while their tab is in front: a few more tries, 10 s apart
    void RetryBrowserCover(MusicApp app)
    {
        if (app != MusicSources.SoundCloud && app != MusicSources.YouTubeMusic || _coverUrl != null || _coverAsked != 0 || _thumb == null) return;
        if (_coverRetries >= 6 || Environment.TickCount64 < _coverRetryAt) return;
        _coverRetries++;
        _coverLookup = "";
        FindDiscordCover();
    }

    // ---------- every player comes back where and how it was left ----------

    string? _placementOwner;                // the player whose placement is in use now
    internal event Action? PlacementChanged;

    PlacementSpec PlacementNow() => new()
    {
        Side = SideName(), UserX = _userX, Free = _free,
        FreeFx = _freeFx, FreeFy = _freeFy, FreeMon = _freeMon, OnTop = _onTop, Size = _size
    };

    // keeps the placement in use with its player; players.json is only written when it changed
    void StorePlacement()
    {
        var now = PlacementNow();
        if (_placementOwner == null)
        {
            if (_designs.DefaultPlacement == now) return;
            _designs.DefaultPlacement = now;
        }
        else if (_designs.Players.FirstOrDefault(p => p.Id == _placementOwner) is { } p)
        {
            if (p.Placement == now) return;
            p.Placement = now;
        }
        else return;                        // that player was deleted
        _designs.Save();
    }

    // Another player was picked: the old one keeps the placement it had, the new one gets back its own
    // (a new player starts with the placement in use)
    void SwitchPlacement()
    {
        StorePlacement();
        _placementOwner = _designs.Active;
        var next = _placementOwner == null ? _designs.DefaultPlacement : _designs.Current?.Placement;
        if (next == null || next == PlacementNow()) { StorePlacement(); return; }

        SetMouseOut(false);
        SetCursorMode(false);
        _dragging = false;
        _side = next.Side == "Right" ? Side.Right : Side.Left;
        _center = next.Side is "Center" or "CenterAbove";
        _above = next.Side == "CenterAbove";
        ApplySideLayout();
        _userX = next.UserX;
        _free = next.Free;
        _freeFx = next.FreeFx;
        _freeFy = next.FreeFy;
        _freeMon = next.FreeMon ?? "";
        _onTop = next.OnTop;
        _size = Math.Clamp(next.Size, MinSize, MaxSize);
        _pxKey = default;
        _posVersion++;
        UpdateTrayChecks();
        SaveSettings();
        Update();
        CoverChanged?.Invoke();             // the options preview puts the cover on the same side
        PlacementChanged?.Invoke();         // and its switches follow
    }

    // Decodes the pictures of the player in use (in the background, so they are ready before the player opens)
    async void BuildStickers()
    {
        int build = ++_stickerBuild;
        var specs = _designs.Current?.Stickers.ToList() ?? new List<StickerSpec>();
        var used = new Dictionary<string, StickerFrames>();
        var shown = new List<(StickerSpec spec, StickerFrames frames)>();
        double ppu = Math.Max(2.5, _free ? FreeSize() / 40.0 : 0);
        long budget = (long)(1_500_000 * Math.Min(4, Math.Pow(ppu / 2.5, 2)));
        foreach (var s in specs)
        {
            // about 2.5 px per design unit covers big taskbars and 200 % scaling, more for a bigger player in free move;
            // sizes are bucketed so small edits reuse it
            int side = (int)Math.Ceiling(Math.Max(s.W, s.H) * ppu / 32) * 32;
            string key = s.File + "|" + side;
            if (!used.TryGetValue(key, out var frames) && !_stickerCache.TryGetValue(key, out frames))
            {
                frames = await Stickers.LoadAsync(Designs.ImagePath(s.File), side, budget);
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

    // bgSolid / accentSolid: 1 = solid, less = see-through (the design's transparency)
    void ApplyPalette(Color bg, Color accent, double bgSolid = 1, double accentSolid = 1)
    {
        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        // never rounded down to 0 (see Solid): a dim part (shuffle off, the seek track) stays clickable at 100 %
        Color A(Color c, int alpha, double solid) =>
            Color.FromArgb((byte)(alpha == 0 ? 0 : Math.Max(1, Math.Round(alpha * solid))), c.R, c.G, c.B);

        _accentBrush = Frozen(A(accent, 0xFF, accentSolid));
        _accentDimBrush = Frozen(A(accent, 0x80, accentSolid));

        // chameleon colours for the player and the title card
        PlayerPanel.Background = Frozen(A(bg, bg.A, bgSolid));
        TitlePopupBorder.Background = Frozen(A(bg, bg.A, bgSolid));
        TitlePopupText.Foreground = _accentBrush;
        VolumePopupBorder.Background = TitlePopupBorder.Background;
        VolumePopupText.Foreground = _accentBrush;

        BtnPrev.Foreground = BtnPlay.Foreground = BtnNext.Foreground = _accentBrush;
        VolumeGlyph.Foreground = _accentBrush;
        SeekFill.Background = _accentBrush;
        SeekThumb.Fill = _accentBrush;
        SeekTrack.Background = Frozen(A(accent, 0x44, accentSolid));
        TimeText.Foreground = Frozen(A(accent, 0xCC, accentSolid));
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

    void SetMediaType(GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
    {
        _isMusic = info.PlaybackType == Windows.Media.MediaPlaybackType.Music;
        _isVideo = info.PlaybackType == Windows.Media.MediaPlaybackType.Video;
    }

    void ApplyPlayback(GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
    {
        var c = info.Controls;
        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        SetMediaType(info);
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

    // apps whose media id is not their process name
    static readonly Dictionary<string, string> AudioProcess = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ZuneMusic"] = "Microsoft.Media.Player",     // Windows Media Player (the new one)
    };

    T WithAppSessions<T>(Func<List<AudioSessionControl>, T> action, T fallback)
    {
        string key = ProcessKey(_source);
        if (AudioProcess.TryGetValue(key, out var process)) key = process;
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
                    try { using var proc = Process.GetProcessById((int)pid); name = proc.ProcessName; } catch { name = ""; }
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

    long _lastVolumeWheel;

    float WheelVolumeStep(int delta)
    {
        long now = Environment.TickCount64;
        float step = now - _lastVolumeWheel < 60 ? 2 * VolumeStep : VolumeStep;
        _lastVolumeWheel = now;
        return delta > 0 ? step : -step;
    }

    void ChangeVolume(float delta)
    {
        if (_volTargets == 0)
        {
            ReadVolume();
            if (_volTargets == 0) return;
        }
        if (_muted && delta < 0) return;
        float v = Math.Clamp((float)(Math.Round((_volume + delta) / VolumeStep) * VolumeStep), 0f, 1f);
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
        if (!available) VolumePopup.IsOpen = false;
        else if (VolumePopup.IsOpen) ShowVolumeCard();
    }

    // The volume card, over the volume icon with the same gap to the player as the title card has to the cover
    void ShowVolumeCard()
    {
        if (_volTargets == 0 || !_expanded) return;
        int pct = (int)Math.Round((_muted ? 0 : _volume) * 100);
        VolumePopupText.Text = _lang == "tr" ? $"%{pct}" : $"{pct}%";
        if (!VolumePopup.IsOpen) VolumePopup.IsOpen = true;
        else Dispatcher.BeginInvoke(new Action(() => { VolumePopup.HorizontalOffset += 0.01; VolumePopup.HorizontalOffset -= 0.01; }), DispatcherPriority.Loaded);
    }

    CustomPopupPlacement[] PlaceVolumeCard(Size popup, Size target, Point offset)
    {
        double k = target.Height / VolumeHit.Height;                // the scale of the player
        double gap = ((40 - VolumeHit.Height) / 2 + CoverGap) * k;  // from the icon to the player's edge, then the gap
        double y = _edge == "top" ? target.Height + gap : -popup.Height - gap;
        return new[] { new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, y), PopupPrimaryAxis.None) };
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
            _miEq.Click += (_, _) => ShowEqualizer();
            _miHide.Click += (_, _) => SetHidden(!_hidden);
            _miDiscord.Click += (_, _) =>
            {
                _discordOn = !_discordOn;
                SaveSettings();
                UpdateTrayChecks();
                FindDiscordCover();
                _discord?.Set(DiscordActivity());
            };
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
            _miCenter.Click += (_, _) => { if (!IconsCentered()) SetSide(Side.Left, center: true); };
            _miAbove.Click += (_, _) => { if (IconsCentered()) SetSide(Side.Left, center: true, above: true); };
            _miPosition.DropDownItems.AddRange(new Forms.ToolStripItem[] { _miRight, _miCenter, _miAbove, _miLeft });
            _miPosition.DropDown.ShowItemToolTips = true;       // "Center" tells which Windows setting it needs
            // picking a side keeps the menu open: the cover moves at once and the user can try the other side
            bool keepOpen = false;
            _miPosition.DropDown.ItemClicked += (_, e) =>
                keepOpen = e.ClickedItem == _miRight || e.ClickedItem == _miLeft || e.ClickedItem == _miCenter || e.ClickedItem == _miAbove;
            _miPosition.DropDown.Closing += (_, e) =>
            {
                if (keepOpen && e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
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
                _miOptions, _miEq, new Forms.ToolStripSeparator(), _miHide, _miLyrics, _miDiscord, _miStartup, new Forms.ToolStripSeparator(),
                _miPosition, _miLanguage, new Forms.ToolStripSeparator(), _miRestart, _miClose
            });
            menu.Opening += (_, _) => UpdateTrayChecks();
            menu.Closing += (_, e) =>
            {
                if (keepOpen && e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
                keepOpen = false;       // the main menu closes last: anything else closes both as usual
            };
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
        "reset", "resetAsk", "deleteAsk", "cancel", "maxImages", "maxPlayers", "badImage", "layersHint", "imageN",
        "hide", "placement", "freeMove", "freeMoveHint", "onTop", "onTopHint", "hotkey", "hotkeyHint",
        "cursorKey", "hotkeyPress", "hotkeyBusy", "hotkeyBad", "hotkeySame", "needsOnTop", "transparency", "clearAll", "cover", "discord", "dcLine1", "dcLine2", "dcVia", "dcButton", "dcSome", "dcPaused", "playerSize", "sizeReset", "needsFreeMove", "dcIdle", "center", "centerHint", "centerAbove", "centerAboveHint", "eq", "eqSub", "eqOn", "eqNeed", "eqSteps", "eqInstall", "eqGet", "eqPresets", "eqBandHint", "eqPreamp", "eqEffects", "eqBassBoost", "eqWidth", "eqBalance", "eqVocals", "eqVocalsHint", "eqDevices", "eqDevicesHint", "eqChoose", "eqNoWrite", "eqAllow", "eqDownloading", "eqSetupOpen", "eqDlFailed", "eqP_flat", "eqP_bass", "eqP_vocal", "eqP_rock", "eqP_pop", "eqP_treble", "eqP_custom", "eqRestart", "eqRestartNow", "eqDevicesNow"
    };

    static readonly Dictionary<string, string[]> Strings = new()
    {
        ["en"] = new[] { "Start with Windows", "Options", "Position", "Right", "Left", "Language", "Close", "Update available",
            "Version {0} is available. You are using version {1}.", "What's new", "Update", "Later", "Downloading update...",
            "The update could not be installed. Please try again later.", "Restart", "Lyrics", "Design your player",
            "Changes are saved automatically.", "Players", "Default", "Custom player", "New player", "Name", "Delete player", "Background", "Buttons",
            "Chameleon: colours from the cover", "Custom colour", "Images", "Add image", "PNG, JPG or GIF, up to 25 MB. Up to 10 images per player.",
            "Drag an image to move it, its corner to resize it and the dot above it to rotate it. Mouse wheel: size. Delete: remove.", "Remove", "Reset", "Reset this player to the default colours and remove its images?", "Delete this player?", "Cancel",
            "A player can have up to 10 images.", "You can save up to 5 players.", "This file can't be used. Choose a PNG, JPG or GIF up to 25 MB.",
            "Drag to reorder. The top image is in front.", "Image {0}",
            "Hide player", "Placement", "Free move", "Drag the cover anywhere on the screen. When it opens, the player always stays on the screen.", "Show over games", "Also stays visible over full-screen games and apps. While a game hides the cursor, clicks go to the game. If it doesn't show in a game, choose borderless or windowed full screen in the game's settings.", "Show / hide", "Click a shortcut to change it. While a game hides the cursor, clicks go to the game. The second shortcut takes the mouse out of a full-screen game, so you can use the player or anything else; press it again or click the game to go back. It works when Show over games is on.", "Mouse out of the game", "Press a shortcut...", "Another app uses this shortcut. Choose another one.", "Use Ctrl, Alt or Win with a key, two of Ctrl, Alt and Shift on their own, or an F key.", "The other shortcut already uses these keys.", "Turn on Show over games to use this.", "Transparency", "All together", "Cover", "Show on Discord", "Music, right on the Windows taskbar", "Free and open source", "Listening via MMP", "Get mey music player", "Something is playing", "Paused", "Player size", "Back to default", "Works when Free move is on.", "Nothing is playing right now", "Center", "Needs the taskbar icons on the left: Settings → Personalization → Taskbar → Taskbar behaviors → Taskbar alignment: Left", "Center, above", "Needs the taskbar icons in the middle (Windows 11): Settings → Personalization → Taskbar → Taskbar behaviors → Taskbar alignment: Center", "Equalizer", "Changes are heard at once, in every app.", "On", "The equalizer works through Equalizer APO, a free and open-source sound tool for Windows. It isn't installed yet.", "MMP downloads its official setup (about 12 MB) and starts it. Windows asks for permission, you choose your sound devices, then restart the computer.", "Download and install", "Open the download page", "Presets", "Drag a dot up or down. Mouse wheel: half a dB. Right-click: back to 0.", "Preamp", "Effects", "Bass boost", "Stereo width", "Balance", "Reduce vocals", "Reduce vocals lowers what is in the middle of the song, mostly the voice; it works best on stereo songs. Right-click a slider: back to default.", "Sound devices", "Equalizer APO works on the sound devices you chose in its setup. To add another one (headphones, speakers), choose it here. Windows asks for permission, then restart the computer.", "Choose devices", "MMP can't save into Equalizer APO's settings folder.", "Allow", "Downloading... {0}%", "The setup is open. Finish it, then restart the computer.", "The download didn't work. Check your connection and try again.", "Flat", "Bass", "Vocal", "Rock", "Pop", "Treble", "Custom", "Restart the computer to turn on the equalizer. Until then, changes aren't heard.", "Restart now", "Working on: {0}" },
        ["tr"] = new[] { "Windows ile başlat", "Ayarlar", "Konum", "Sağ", "Sol", "Dil", "Kapat", "Güncelleme mevcut",
            "{0} sürümü yayınlandı. Şu an {1} sürümünü kullanıyorsun.", "Yenilikler", "Güncelle", "Sonra", "Güncelleme indiriliyor...",
            "Güncelleme yüklenemedi. Daha sonra tekrar dene.", "Yeniden başlat", "Şarkı sözleri", "Oynatıcını tasarla",
            "Değişiklikler otomatik kaydedilir.", "Oynatıcılar", "Varsayılan", "Özel oynatıcı", "Yeni oynatıcı", "Ad", "Oynatıcıyı sil", "Arka plan",
            "Butonlar", "Bukalemun: renkler kapaktan", "Özel renk", "Resimler", "Resim ekle",
            "PNG, JPG veya GIF, en fazla 25 MB. Bir oynatıcıda en fazla 10 resim.",
            "Taşımak için resmi, boyutlandırmak için köşesini, döndürmek için üstündeki noktayı sürükle. Fare tekerleği: boyut. Delete: kaldır.",
            "Kaldır", "Sıfırla", "Bu oynatıcı varsayılan renklere dönsün ve resimleri kaldırılsın mı?",
            "Bu oynatıcı silinsin mi?", "Vazgeç", "Bir oynatıcıda en fazla 10 resim olabilir.", "En fazla 5 oynatıcı kaydedebilirsin.",
            "Bu dosya kullanılamıyor. En fazla 25 MB'lık bir PNG, JPG veya GIF seç.",
            "Sıralamak için sürükle. En üstteki resim en öndedir.", "Resim {0}",
            "Oynatıcıyı gizle", "Yerleşim", "Serbest hareket", "Kapağı ekranda istediğin yere sürükle. Açıldığında oynatıcı hep ekranın içinde kalır.", "Oyunların üstünde göster", "Tam ekran oyunların ve uygulamaların üstünde de görünür. Oyun imleci gizlerken tıklamalar oyuna gider. Bir oyunda görünmüyorsa oyunun ayarlarından çerçevesiz ya da pencereli tam ekranı seç.", "Göster / gizle", "Değiştirmek için bir kısayola tıkla. Oyun imleci gizlerken tıklamalar oyuna gider. İkinci kısayol fareyi tam ekran oyundan alır; oynatıcıyı ya da başka bir şeyi kullanabilirsin. Oyuna dönmek için tekrar bas ya da oyuna tıkla. Oyunların üstünde göster açıkken çalışır.", "Fareyi oyundan al", "Bir kısayola bas...", "Bu kısayolu başka bir uygulama kullanıyor. Başka bir tane seç.", "Bir tuşla birlikte Ctrl, Alt veya Win, tek başına Ctrl, Alt ve Shift'ten ikisi ya da bir F tuşu kullan.", "Bu tuşları diğer kısayol kullanıyor.", "Bunun için Oyunların üstünde göster'i aç.", "Saydamlık", "Hepsi birlikte", "Kapak", "Discord'da göster", "Müzik, tam Windows görev çubuğunda", "Ücretsiz ve açık kaynak", "MMP üzerinden dinliyor", "mey music player'ı indir", "Bir şeyler çalıyor", "Duraklatıldı", "Oynatıcı boyutu", "Varsayılana döndür", "Serbest hareket açıkken çalışır.", "Şu an bir şey çalmıyor", "Orta", "Görev çubuğu simgeleri solda olmalı: Ayarlar → Kişiselleştirme → Görev çubuğu → Görev çubuğu davranışları → Görev çubuğu hizalaması: Sol", "Orta üst", "Görev çubuğu simgeleri ortada olmalı (Windows 11): Ayarlar → Kişiselleştirme → Görev çubuğu → Görev çubuğu davranışları → Görev çubuğu hizalaması: Orta", "Ekolayzer", "Değişiklikler anında, her uygulamada duyulur.", "Açık", "Ekolayzer, Windows için ücretsiz ve açık kaynak bir ses aracı olan Equalizer APO ile çalışır. Henüz kurulu değil.", "MMP resmi kurulumunu indirir (yaklaşık 12 MB) ve başlatır. Windows izin ister, ses cihazlarını seçersin, sonra bilgisayarı yeniden başlatırsın.", "İndir ve kur", "İndirme sayfasını aç", "Hazır ayarlar", "Bir noktayı yukarı ya da aşağı sürükle. Fare tekerleği: yarım dB. Sağ tık: 0'a döner.", "Ön kazanç", "Efektler", "Bas güçlendirme", "Stereo genişlik", "Denge", "Vokali azalt", "Vokali azalt, şarkının ortasındakini (çoğunlukla sesi) kısar; en iyi stereo şarkılarda çalışır. Bir kaydırıcıya sağ tık: varsayılana döner.", "Ses cihazları", "Equalizer APO, kurulumda seçtiğin ses cihazlarında çalışır. Başka bir cihaz eklemek için (kulaklık, hoparlör) buradan seç. Windows izin ister, sonra bilgisayarı yeniden başlat.", "Cihazları seç", "MMP, Equalizer APO'nun ayar klasörüne kaydedemiyor.", "İzin ver", "İndiriliyor... %{0}", "Kurulum açık. Bitir, sonra bilgisayarı yeniden başlat.", "İndirme olmadı. Bağlantını kontrol edip tekrar dene.", "Düz", "Bas", "Vokal", "Rock", "Pop", "Tiz", "Özel", "Ekolayzerin çalışması için bilgisayarı yeniden başlat. O zamana kadar değişiklikler duyulmaz.", "Şimdi yeniden başlat", "Şu an çalıştığı cihazlar: {0}" },
        ["es"] = new[] { "Iniciar con Windows", "Opciones", "Posición", "Derecha", "Izquierda", "Idioma", "Cerrar", "Actualización disponible",
            "La versión {0} está disponible. Estás usando la versión {1}.", "Novedades", "Actualizar", "Más tarde", "Descargando actualización...",
            "No se pudo instalar la actualización. Inténtalo de nuevo más tarde.", "Reiniciar", "Letras", "Diseña tu reproductor",
            "Los cambios se guardan automáticamente.", "Reproductores", "Predeterminado", "Reproductor personalizado", "Nuevo reproductor", "Nombre",
            "Eliminar reproductor", "Fondo", "Botones", "Camaleón: colores de la portada", "Color personalizado", "Imágenes", "Añadir imagen",
            "PNG, JPG o GIF, hasta 25 MB. Hasta 10 imágenes por reproductor.",
            "Arrastra una imagen para moverla, su esquina para cambiar el tamaño y el punto de arriba para girarla. Rueda del ratón: tamaño. Supr: quitar.",
            "Quitar", "Restablecer", "¿Restablecer este reproductor a los colores predeterminados y quitar sus imágenes?",
            "¿Eliminar este reproductor?", "Cancelar", "Un reproductor puede tener hasta 10 imágenes.", "Puedes guardar hasta 5 reproductores.",
            "Este archivo no se puede usar. Elige un PNG, JPG o GIF de hasta 25 MB.",
            "Arrastra para ordenar. La imagen de arriba queda delante.", "Imagen {0}",
            "Ocultar reproductor", "Ubicación", "Movimiento libre", "Arrastra la portada a cualquier parte de la pantalla. Al abrirse, el reproductor siempre queda dentro de la pantalla.", "Mostrar sobre los juegos", "También se ve encima de juegos y aplicaciones a pantalla completa. Mientras un juego oculta el cursor, los clics van al juego. Si no aparece en un juego, elige pantalla completa sin bordes o en ventana en los ajustes del juego.", "Mostrar / ocultar", "Haz clic en un atajo para cambiarlo. Mientras un juego oculta el cursor, los clics van al juego. El segundo atajo saca el ratón de un juego a pantalla completa para que puedas usar el reproductor o cualquier otra cosa; vuelve a pulsarlo o haz clic en el juego para volver. Funciona con Mostrar sobre los juegos activado.", "Sacar el ratón del juego", "Pulsa un atajo...", "Otra aplicación usa este atajo. Elige otro.", "Usa Ctrl, Alt o Win con una tecla, dos de Ctrl, Alt y Mayús solas, o una tecla F.", "El otro atajo ya usa estas teclas.", "Activa Mostrar sobre los juegos para usarlo.", "Transparencia", "Todo junto", "Portada", "Mostrar en Discord", "Música en la barra de tareas de Windows", "Gratis y de código abierto", "Escuchando con MMP", "Descargar mey music player", "Algo está sonando", "En pausa", "Tamaño del reproductor", "Volver al predeterminado", "Funciona con Movimiento libre activado.", "Ahora mismo no suena nada", "Centro", "Necesita los iconos de la barra de tareas a la izquierda: Configuración → Personalización → Barra de tareas → Comportamientos de la barra de tareas → Alineación de la barra de tareas: Izquierda", "Centro, arriba", "Necesita los iconos de la barra de tareas en el centro (Windows 11): Configuración → Personalización → Barra de tareas → Comportamientos de la barra de tareas → Alineación de la barra de tareas: Centro", "Ecualizador", "Los cambios se oyen al instante, en todas las apps.", "Activado", "El ecualizador funciona con Equalizer APO, una herramienta de sonido gratuita y de código abierto para Windows. Aún no está instalada.", "MMP descarga su instalador oficial (unos 12 MB) y lo inicia. Windows pide permiso, eliges tus dispositivos de sonido y luego reinicias el equipo.", "Descargar e instalar", "Abrir la página de descarga", "Ajustes predefinidos", "Arrastra un punto hacia arriba o abajo. Rueda del ratón: medio dB. Clic derecho: vuelve a 0.", "Preamplificador", "Efectos", "Refuerzo de graves", "Amplitud estéreo", "Balance", "Reducir voces", "Reducir voces baja lo que está en el centro de la canción, sobre todo la voz; funciona mejor con canciones estéreo. Clic derecho en un control: valor predeterminado.", "Dispositivos de sonido", "Equalizer APO funciona en los dispositivos de sonido que elegiste al instalarlo. Para añadir otro (auriculares, altavoces), elígelo aquí. Windows pide permiso; luego reinicia el equipo.", "Elegir dispositivos", "MMP no puede guardar en la carpeta de ajustes de Equalizer APO.", "Permitir", "Descargando... {0}%", "El instalador está abierto. Termínalo y luego reinicia el equipo.", "La descarga no funcionó. Revisa tu conexión e inténtalo de nuevo.", "Plano", "Graves", "Voz", "Rock", "Pop", "Agudos", "Personalizado", "Reinicia el equipo para activar el ecualizador. Hasta entonces, los cambios no se oyen.", "Reiniciar ahora", "Funciona en: {0}" },
        ["fr"] = new[] { "Démarrer avec Windows", "Options", "Position", "Droite", "Gauche", "Langue", "Fermer", "Mise à jour disponible",
            "La version {0} est disponible. Vous utilisez la version {1}.", "Nouveautés", "Mettre à jour", "Plus tard",
            "Téléchargement de la mise à jour...", "La mise à jour n'a pas pu être installée. Veuillez réessayer plus tard.", "Redémarrer", "Paroles",
            "Concevez votre lecteur", "Les modifications sont enregistrées automatiquement.", "Lecteurs", "Par défaut", "Lecteur personnalisé",
            "Nouveau lecteur", "Nom", "Supprimer le lecteur", "Arrière-plan", "Boutons", "Caméléon : couleurs de la pochette",
            "Couleur personnalisée", "Images", "Ajouter une image", "PNG, JPG ou GIF, jusqu'à 25 Mo. Jusqu'à 10 images par lecteur.",
            "Faites glisser une image pour la déplacer, son coin pour la redimensionner et le point au-dessus pour la faire pivoter. Molette : taille. Suppr : retirer.",
            "Retirer", "Réinitialiser",
            "Réinitialiser ce lecteur aux couleurs par défaut et retirer ses images ?", "Supprimer ce lecteur ?", "Annuler",
            "Un lecteur peut avoir jusqu'à 10 images.", "Vous pouvez enregistrer jusqu'à 5 lecteurs.",
            "Ce fichier ne peut pas être utilisé. Choisissez un PNG, JPG ou GIF de 25 Mo maximum.",
            "Faites glisser pour réordonner. L'image du haut est devant.", "Image {0}",
            "Masquer le lecteur", "Emplacement", "Déplacement libre", "Faites glisser la pochette n'importe où sur l'écran. À l'ouverture, le lecteur reste toujours à l'écran.", "Afficher par-dessus les jeux", "Reste aussi visible par-dessus les jeux et applications en plein écran. Quand un jeu masque le curseur, les clics vont au jeu. S'il n'apparaît pas dans un jeu, choisissez le plein écran sans bordure ou fenêtré dans les paramètres du jeu.", "Afficher / masquer", "Cliquez sur un raccourci pour le changer. Quand un jeu masque le curseur, les clics vont au jeu. Le second raccourci sort la souris d'un jeu en plein écran pour utiliser le lecteur ou autre chose ; appuyez de nouveau ou cliquez sur le jeu pour y revenir. Fonctionne quand Afficher par-dessus les jeux est activé.", "Sortir la souris du jeu", "Appuyez sur un raccourci...", "Une autre application utilise ce raccourci. Choisissez-en un autre.", "Utilisez Ctrl, Alt ou Win avec une touche, deux touches parmi Ctrl, Alt et Maj seules, ou une touche F.", "L'autre raccourci utilise déjà ces touches.", "Activez Afficher par-dessus les jeux pour l'utiliser.", "Transparence", "Tout ensemble", "Pochette", "Afficher sur Discord", "La musique dans la barre des tâches", "Gratuit et open source", "Écoute avec MMP", "Obtenir mey music player", "Quelque chose joue", "En pause", "Taille du lecteur", "Revenir par défaut", "Fonctionne avec Déplacement libre activé.", "Rien ne joue pour le moment", "Centre", "Nécessite les icônes de la barre des tâches à gauche : Paramètres → Personnalisation → Barre des tâches → Comportements de la barre des tâches → Alignement de la barre des tâches : Gauche", "Centre, au-dessus", "Nécessite les icônes de la barre des tâches au centre (Windows 11) : Paramètres → Personnalisation → Barre des tâches → Comportements de la barre des tâches → Alignement de la barre des tâches : Centre", "Égaliseur", "Les changements s'entendent tout de suite, dans toutes les applis.", "Activé", "L'égaliseur fonctionne avec Equalizer APO, un outil audio gratuit et open source pour Windows. Il n'est pas encore installé.", "MMP télécharge son installateur officiel (environ 12 Mo) et le lance. Windows demande l'autorisation, vous choisissez vos périphériques audio, puis vous redémarrez l'ordinateur.", "Télécharger et installer", "Ouvrir la page de téléchargement", "Préréglages", "Faites glisser un point vers le haut ou le bas. Molette : un demi-dB. Clic droit : retour à 0.", "Préampli", "Effets", "Renfort des basses", "Largeur stéréo", "Balance", "Réduire la voix", "Réduire la voix baisse ce qui est au centre du morceau, surtout la voix ; marche mieux sur les morceaux stéréo. Clic droit sur un curseur : valeur par défaut.", "Périphériques audio", "Equalizer APO fonctionne sur les périphériques audio choisis lors de son installation. Pour en ajouter un autre (casque, enceintes), choisissez-le ici. Windows demande l'autorisation, puis redémarrez l'ordinateur.", "Choisir les périphériques", "MMP ne peut pas enregistrer dans le dossier de réglages d'Equalizer APO.", "Autoriser", "Téléchargement... {0} %", "L'installateur est ouvert. Terminez-le, puis redémarrez l'ordinateur.", "Le téléchargement a échoué. Vérifiez votre connexion et réessayez.", "Neutre", "Basses", "Voix", "Rock", "Pop", "Aigus", "Personnalisé", "Redémarrez l'ordinateur pour activer l'égaliseur. D'ici là, les changements ne s'entendent pas.", "Redémarrer maintenant", "Actif sur : {0}" },
        ["de"] = new[] { "Mit Windows starten", "Optionen", "Position", "Rechts", "Links", "Sprache", "Schließen", "Update verfügbar",
            "Version {0} ist verfügbar. Sie verwenden Version {1}.", "Was ist neu", "Aktualisieren", "Später", "Update wird heruntergeladen...",
            "Das Update konnte nicht installiert werden. Bitte versuchen Sie es später noch einmal.", "Neu starten", "Songtexte",
            "Gestalten Sie Ihren Player", "Änderungen werden automatisch gespeichert.", "Player", "Standard", "Eigener Player", "Neuer Player",
            "Name", "Player löschen", "Hintergrund", "Schaltflächen", "Chamäleon: Farben vom Cover", "Eigene Farbe", "Bilder", "Bild hinzufügen",
            "PNG, JPG oder GIF, bis 25 MB. Bis zu 10 Bilder pro Player.",
            "Ziehen Sie ein Bild, um es zu verschieben, seine Ecke für die Größe und den Punkt darüber zum Drehen. Mausrad: Größe. Entf: entfernen.",
            "Entfernen", "Zurücksetzen", "Diesen Player auf die Standardfarben zurücksetzen und seine Bilder entfernen?",
            "Diesen Player löschen?", "Abbrechen", "Ein Player kann bis zu 10 Bilder haben.", "Sie können bis zu 5 Player speichern.",
            "Diese Datei kann nicht verwendet werden. Wählen Sie ein PNG, JPG oder GIF bis 25 MB.",
            "Zum Sortieren ziehen. Das oberste Bild liegt vorne.", "Bild {0}",
            "Player ausblenden", "Platzierung", "Frei bewegen", "Ziehen Sie das Cover an eine beliebige Stelle auf dem Bildschirm. Beim Öffnen bleibt der Player immer auf dem Bildschirm.", "Über Spielen anzeigen", "Bleibt auch über Vollbildspielen und -apps sichtbar. Solange ein Spiel den Mauszeiger ausblendet, gehen Klicks an das Spiel. Wenn er in einem Spiel nicht erscheint, wählen Sie in den Spieleinstellungen randloses Vollbild oder den Fenstermodus.", "Ein- / ausblenden", "Klicken Sie auf ein Tastenkürzel, um es zu ändern. Solange ein Spiel den Mauszeiger ausblendet, gehen Klicks an das Spiel. Das zweite Kürzel holt die Maus aus einem Vollbildspiel, damit Sie den Player oder etwas anderes bedienen können; drücken Sie es erneut oder klicken Sie ins Spiel, um zurückzukehren. Funktioniert, wenn Über Spielen anzeigen an ist.", "Maus aus dem Spiel holen", "Tastenkürzel drücken...", "Eine andere App verwendet dieses Tastenkürzel. Wählen Sie ein anderes.", "Verwenden Sie Strg, Alt oder Win mit einer Taste, zwei von Strg, Alt und Umschalt allein oder eine F-Taste.", "Das andere Tastenkürzel verwendet diese Tasten bereits.", "Schalten Sie Über Spielen anzeigen ein, um dies zu nutzen.", "Transparenz", "Alles zusammen", "Cover", "Auf Discord zeigen", "Musik direkt in der Windows-Taskleiste", "Kostenlos und Open Source", "Hört über MMP", "mey music player holen", "Etwas läuft", "Pausiert", "Player-Größe", "Auf Standard zurücksetzen", "Funktioniert, wenn Frei bewegen aktiviert ist.", "Gerade läuft nichts", "Mitte", "Die Taskleistensymbole müssen links sein: Einstellungen → Personalisierung → Taskleiste → Verhalten der Taskleiste → Taskleistenausrichtung: Links", "Mitte, darüber", "Die Taskleistensymbole müssen in der Mitte sein (Windows 11): Einstellungen → Personalisierung → Taskleiste → Verhalten der Taskleiste → Taskleistenausrichtung: Zentriert", "Equalizer", "Änderungen sind sofort zu hören, in jeder App.", "Ein", "Der Equalizer arbeitet mit Equalizer APO, einem kostenlosen Open-Source-Audiotool für Windows. Es ist noch nicht installiert.", "MMP lädt das offizielle Setup herunter (etwa 12 MB) und startet es. Windows fragt nach der Berechtigung, du wählst deine Audiogeräte und startest dann den Computer neu.", "Herunterladen und installieren", "Downloadseite öffnen", "Voreinstellungen", "Ziehe einen Punkt nach oben oder unten. Mausrad: ein halbes dB. Rechtsklick: zurück auf 0.", "Vorverstärkung", "Effekte", "Bassverstärkung", "Stereobreite", "Balance", "Gesang reduzieren", "Gesang reduzieren senkt, was in der Mitte des Songs liegt, meist die Stimme; am besten bei Stereo-Songs. Rechtsklick auf einen Regler: Standardwert.", "Audiogeräte", "Equalizer APO arbeitet auf den Audiogeräten, die du im Setup gewählt hast. Um ein weiteres hinzuzufügen (Kopfhörer, Lautsprecher), wähle es hier. Windows fragt nach der Berechtigung, danach den Computer neu starten.", "Geräte wählen", "MMP kann nicht im Einstellungsordner von Equalizer APO speichern.", "Erlauben", "Wird heruntergeladen... {0} %", "Das Setup ist geöffnet. Schließe es ab und starte dann den Computer neu.", "Der Download hat nicht geklappt. Prüfe deine Verbindung und versuche es erneut.", "Neutral", "Bass", "Gesang", "Rock", "Pop", "Höhen", "Eigene", "Starte den Computer neu, um den Equalizer einzuschalten. Bis dahin sind Änderungen nicht zu hören.", "Jetzt neu starten", "Aktiv auf: {0}" },
        ["pt"] = new[] { "Iniciar com o Windows", "Opções", "Posição", "Direita", "Esquerda", "Idioma", "Fechar", "Atualização disponível",
            "A versão {0} está disponível. Você está usando a versão {1}.", "O que há de novo", "Atualizar", "Mais tarde", "Baixando atualização...",
            "A atualização não pôde ser instalada. Tente novamente mais tarde.", "Reiniciar", "Letras", "Crie seu player",
            "As alterações são salvas automaticamente.", "Players", "Padrão", "Player personalizado", "Novo player", "Nome", "Excluir player",
            "Fundo", "Botões", "Camaleão: cores da capa", "Cor personalizada", "Imagens", "Adicionar imagem",
            "PNG, JPG ou GIF, até 25 MB. Até 10 imagens por player.",
            "Arraste uma imagem para movê-la, o canto para redimensionar e o ponto acima para girar. Roda do mouse: tamanho. Delete: remover.",
            "Remover", "Redefinir", "Redefinir este player para as cores padrão e remover as imagens dele?",
            "Excluir este player?", "Cancelar", "Um player pode ter até 10 imagens.", "Você pode salvar até 5 players.",
            "Este arquivo não pode ser usado. Escolha um PNG, JPG ou GIF de até 25 MB.",
            "Arraste para reordenar. A imagem de cima fica na frente.", "Imagem {0}",
            "Ocultar player", "Posicionamento", "Movimento livre", "Arraste a capa para qualquer lugar da tela. Ao abrir, o player sempre fica dentro da tela.", "Mostrar sobre os jogos", "Também fica visível sobre jogos e apps em tela cheia. Enquanto um jogo esconde o cursor, os cliques vão para o jogo. Se não aparecer em um jogo, escolha tela cheia sem bordas ou em janela nas configurações do jogo.", "Mostrar / ocultar", "Clique em um atalho para mudá-lo. Enquanto um jogo esconde o cursor, os cliques vão para o jogo. O segundo atalho tira o mouse de um jogo em tela cheia para você usar o player ou qualquer outra coisa; pressione de novo ou clique no jogo para voltar. Funciona com Mostrar sobre os jogos ativado.", "Tirar o mouse do jogo", "Pressione um atalho...", "Outro app usa este atalho. Escolha outro.", "Use Ctrl, Alt ou Win com uma tecla, duas entre Ctrl, Alt e Shift sozinhas, ou uma tecla F.", "O outro atalho já usa estas teclas.", "Ative Mostrar sobre os jogos para usar isto.", "Transparência", "Tudo junto", "Capa", "Mostrar no Discord", "Música na barra de tarefas do Windows", "Grátis e de código aberto", "Ouvindo pelo MMP", "Baixar mey music player", "Algo está tocando", "Pausado", "Tamanho do player", "Voltar ao padrão", "Funciona com Movimento livre ativado.", "Nada tocando agora", "Centro", "Requer os ícones da barra de tarefas à esquerda: Configurações → Personalização → Barra de Tarefas → Comportamentos da barra de tarefas → Alinhamento da barra de tarefas: Esquerda", "Centro, acima", "Requer os ícones da barra de tarefas no centro (Windows 11): Configurações → Personalização → Barra de Tarefas → Comportamentos da barra de tarefas → Alinhamento da barra de tarefas: Centro", "Equalizador", "As mudanças são ouvidas na hora, em todos os apps.", "Ligado", "O equalizador funciona com o Equalizer APO, uma ferramenta de som gratuita e de código aberto para Windows. Ele ainda não está instalado.", "O MMP baixa o instalador oficial (cerca de 12 MB) e o inicia. O Windows pede permissão, você escolhe seus dispositivos de som e depois reinicia o computador.", "Baixar e instalar", "Abrir a página de download", "Predefinições", "Arraste um ponto para cima ou para baixo. Roda do mouse: meio dB. Clique direito: volta para 0.", "Pré-amplificador", "Efeitos", "Reforço de graves", "Largura estéreo", "Balanço", "Reduzir vocais", "Reduzir vocais abaixa o que está no centro da música, principalmente a voz; funciona melhor em músicas estéreo. Clique direito em um controle: valor padrão.", "Dispositivos de som", "O Equalizer APO funciona nos dispositivos de som escolhidos na instalação. Para adicionar outro (fones, caixas de som), escolha aqui. O Windows pede permissão; depois reinicie o computador.", "Escolher dispositivos", "O MMP não consegue salvar na pasta de configurações do Equalizer APO.", "Permitir", "Baixando... {0}%", "O instalador está aberto. Conclua-o e depois reinicie o computador.", "O download não funcionou. Verifique sua conexão e tente novamente.", "Plano", "Graves", "Vocal", "Rock", "Pop", "Agudos", "Personalizado", "Reinicie o computador para ativar o equalizador. Até lá, as mudanças não são ouvidas.", "Reiniciar agora", "Ativo em: {0}" },
        ["ru"] = new[] { "Запуск с Windows", "Параметры", "Положение", "Справа", "Слева", "Язык", "Закрыть", "Доступно обновление",
            "Доступна версия {0}. Вы используете версию {1}.", "Что нового", "Обновить", "Позже", "Загрузка обновления...",
            "Не удалось установить обновление. Повторите попытку позже.", "Перезапустить", "Тексты песен", "Оформите свой плеер",
            "Изменения сохраняются автоматически.", "Плееры", "По умолчанию", "Свой плеер", "Новый плеер", "Название", "Удалить плеер", "Фон",
            "Кнопки", "Хамелеон: цвета обложки", "Свой цвет", "Изображения", "Добавить изображение",
            "PNG, JPG или GIF до 25 МБ. До 10 изображений на плеер.",
            "Перетащите изображение, чтобы переместить его; угол — изменить размер, точку над ним — повернуть. Колесо мыши: размер. Delete: убрать.",
            "Убрать", "Сбросить", "Сбросить этот плеер к стандартным цветам и убрать его изображения?",
            "Удалить этот плеер?", "Отмена", "У плеера может быть до 10 изображений.", "Можно сохранить до 5 плееров.",
            "Этот файл нельзя использовать. Выберите PNG, JPG или GIF до 25 МБ.",
            "Перетащите, чтобы изменить порядок. Верхнее изображение — впереди.", "Изображение {0}",
            "Скрыть плеер", "Размещение", "Свободное перемещение", "Перетащите обложку в любое место экрана. При открытии плеер всегда остаётся на экране.", "Показывать поверх игр", "Виден и поверх полноэкранных игр и приложений. Пока игра скрывает курсор, щелчки идут в игру. Если в игре его не видно, выберите в настройках игры полноэкранный режим без рамки или оконный режим.", "Показать / скрыть", "Нажмите на сочетание, чтобы изменить его. Пока игра скрывает курсор, щелчки идут в игру. Второе сочетание выводит мышь из полноэкранной игры, чтобы пользоваться плеером или чем-то ещё; нажмите его снова или щёлкните по игре, чтобы вернуться. Работает, когда включено «Показывать поверх игр».", "Вывести мышь из игры", "Нажмите сочетание клавиш...", "Это сочетание использует другое приложение. Выберите другое.", "Используйте Ctrl, Alt или Win с клавишей, две из Ctrl, Alt и Shift без других клавиш или клавишу F.", "Эти клавиши уже заняты другим сочетанием.", "Включите «Показывать поверх игр», чтобы пользоваться этим.", "Прозрачность", "Всё вместе", "Обложка", "Показывать в Discord", "Музыка прямо на панели задач Windows", "Бесплатно и с открытым кодом", "Слушает через MMP", "Скачать mey music player", "Что-то играет", "На паузе", "Размер плеера", "По умолчанию", "Работает при включённом «Свободном перемещении».", "Сейчас ничего не играет", "По центру", "Нужны значки панели задач слева: Параметры → Персонализация → Панель задач → Поведение панели задач → Выравнивание панели задач: по левому краю", "По центру, над панелью", "Нужны значки панели задач по центру (Windows 11): Параметры → Персонализация → Панель задач → Поведение панели задач → Выравнивание панели задач: по центру", "Эквалайзер", "Изменения слышны сразу, во всех приложениях.", "Вкл.", "Эквалайзер работает через Equalizer APO — бесплатную программу для звука в Windows с открытым кодом. Она ещё не установлена.", "MMP скачает официальный установщик (около 12 МБ) и запустит его. Windows попросит разрешение, вы выберете звуковые устройства, затем перезагрузите компьютер.", "Скачать и установить", "Открыть страницу загрузки", "Пресеты", "Перетащите точку вверх или вниз. Колесо мыши: полдецибела. Правый клик: обратно на 0.", "Предусиление", "Эффекты", "Усиление басов", "Ширина стерео", "Баланс", "Приглушить вокал", "«Приглушить вокал» снижает то, что звучит в центре песни, в основном голос; лучше всего работает со стерео. Правый клик по ползунку: значение по умолчанию.", "Звуковые устройства", "Equalizer APO работает на звуковых устройствах, выбранных при установке. Чтобы добавить другое (наушники, колонки), выберите его здесь. Windows попросит разрешение, затем перезагрузите компьютер.", "Выбрать устройства", "MMP не может сохранить файл в папку настроек Equalizer APO.", "Разрешить", "Загрузка... {0}%", "Установщик открыт. Завершите установку и перезагрузите компьютер.", "Не удалось скачать. Проверьте подключение и попробуйте снова.", "Ровный", "Басы", "Вокал", "Рок", "Поп", "Высокие", "Свой", "Перезагрузите компьютер, чтобы включить эквалайзер. До этого изменения не слышны.", "Перезагрузить сейчас", "Работает на: {0}" },
        ["zh"] = new[] { "随 Windows 启动", "选项", "位置", "右", "左", "语言", "关闭", "有可用更新", "版本 {0} 可用。您当前使用的是版本 {1}。", "更新内容", "更新", "稍后", "正在下载更新...", "无法安装更新。请稍后重试。",
            "重新启动", "歌词", "设计你的播放器", "更改会自动保存。", "播放器", "默认", "自定义播放器", "新建播放器", "名称", "删除播放器", "背景", "按钮", "变色龙：取自封面的颜色", "自定义颜色", "图片", "添加图片",
            "PNG、JPG 或 GIF，最大 25 MB。每个播放器最多 10 张图片。", "拖动图片可移动，拖动角可调整大小，拖动上方的圆点可旋转。鼠标滚轮：大小。Delete：移除。", "移除", "重置",
            "将此播放器重置为默认颜色并移除其图片？", "删除此播放器？", "取消", "每个播放器最多可以有 10 张图片。", "最多可以保存 5 个播放器。", "无法使用此文件。请选择不超过 25 MB 的 PNG、JPG 或 GIF。",
            "拖动以排序。最上面的图片在最前面。", "图片 {0}",
            "隐藏播放器", "位置", "自由移动", "将封面拖到屏幕上的任意位置。打开时，播放器始终保持在屏幕内。", "在游戏上方显示", "在全屏游戏和应用上方也保持可见。游戏隐藏光标时，点击会传给游戏。如果在游戏中不显示，请在游戏设置中选择无边框或窗口化全屏。", "显示 / 隐藏", "点击快捷键即可更改。游戏隐藏光标时，点击会传给游戏。第二个快捷键让鼠标离开全屏游戏，以便使用播放器或其他内容；再按一次或点击游戏即可返回。开启“在游戏上方显示”时可用。", "让鼠标离开游戏", "请按下快捷键...", "其他应用正在使用此快捷键。请选择其他快捷键。", "请使用 Ctrl、Alt 或 Win 加一个键、单独使用 Ctrl、Alt、Shift 中的两个，或使用 F 键。", "另一个快捷键已在使用这些键。", "请开启“在游戏上方显示”以使用此功能。", "透明度", "全部一起", "封面", "在 Discord 上显示", "音乐，就在 Windows 任务栏上", "免费且开源", "正在通过 MMP 收听", "获取 mey music player", "正在播放", "已暂停", "播放器大小", "恢复默认", "需开启自由移动。", "当前没有播放内容", "居中", "需要任务栏图标靠左：设置 → 个性化 → 任务栏 → 任务栏行为 → 任务栏对齐方式：左", "居中（上方）", "需要任务栏图标居中（Windows 11）：设置 → 个性化 → 任务栏 → 任务栏行为 → 任务栏对齐方式：居中", "均衡器", "更改会立即生效，适用于所有应用。", "开启", "均衡器通过 Equalizer APO 工作，这是一款免费、开源的 Windows 音频工具。它尚未安装。", "MMP 会下载其官方安装程序（约 12 MB）并启动。Windows 会请求权限，你选择音频设备，然后重启电脑。", "下载并安装", "打开下载页面", "预设", "上下拖动圆点。鼠标滚轮：半个 dB。右键：恢复为 0。", "前置放大", "效果", "低音增强", "立体声宽度", "平衡", "减弱人声", "减弱人声会降低歌曲中间的声音（主要是人声），立体声歌曲效果最好。右键单击滑块：恢复默认值。", "音频设备", "Equalizer APO 只作用于安装时选择的音频设备。要添加其他设备（耳机、音箱），请在这里选择。Windows 会请求权限，然后重启电脑。", "选择设备", "MMP 无法保存到 Equalizer APO 的设置文件夹。", "允许", "正在下载... {0}%", "安装程序已打开。完成后请重启电脑。", "下载失败。请检查网络连接后重试。", "平坦", "低音", "人声", "摇滚", "流行", "高音", "自定义", "重启电脑以启用均衡器。在此之前，更改不会生效。", "立即重启", "正在使用的设备：{0}" },
        ["ja"] = new[] { "Windowsと同時に起動", "オプション", "位置", "右", "左", "言語", "閉じる", "利用可能なアップデート", "バージョン {0} が利用可能です。現在バージョン {1} を使用しています。", "新機能", "アップデート", "後で",
            "アップデートをダウンロード中...", "アップデートをインストールできませんでした。後で再度お試しください。", "再起動", "歌詞", "プレーヤーをデザイン", "変更は自動的に保存されます。", "プレーヤー", "デフォルト", "カスタムプレーヤー",
            "新しいプレーヤー", "名前", "プレーヤーを削除", "背景", "ボタン", "カメレオン：カバーの色", "カスタムカラー", "画像", "画像を追加", "PNG、JPG、GIF（25 MB まで）。1 つのプレーヤーに画像は 10 枚まで。",
            "画像をドラッグして移動、角でサイズ変更、上の点で回転します。マウスホイール：サイズ。Delete：取り除く。", "取り除く", "リセット", "このプレーヤーをデフォルトの色に戻し、画像を取り除きますか？",
            "このプレーヤーを削除しますか？", "キャンセル", "1 つのプレーヤーに画像は 10 枚までです。", "プレーヤーは 5 つまで保存できます。", "このファイルは使用できません。25 MB までの PNG、JPG、GIF を選んでください。",
            "ドラッグで並べ替え。いちばん上の画像が最前面です。", "画像 {0}",
            "プレーヤーを隠す", "配置", "自由に移動", "カバーを画面上の好きな場所にドラッグします。開いたときもプレーヤーは常に画面内に収まります。", "ゲームの上に表示", "全画面のゲームやアプリの上にも表示されます。ゲームがカーソルを隠している間はクリックがゲームに届きます。ゲームで表示されない場合は、ゲームの設定でボーダーレスまたはウィンドウ化された全画面を選んでください。", "表示 / 非表示", "ショートカットをクリックすると変更できます。ゲームがカーソルを隠している間はクリックがゲームに届きます。2 つ目のショートカットは全画面ゲームからマウスを出し、プレーヤーやほかのものを使えるようにします。戻るにはもう一度押すか、ゲームをクリックしてください。「ゲームの上に表示」がオンのときに使えます。", "マウスをゲームから出す", "ショートカットを押してください...", "このショートカットは別のアプリが使用しています。別のものを選んでください。", "Ctrl、Alt、Win とキーの組み合わせ、Ctrl・Alt・Shift のうち 2 つだけ、または F キーを使ってください。", "もう一方のショートカットがこのキーを使っています。", "使うには「ゲームの上に表示」をオンにしてください。", "透明度", "すべてまとめて", "カバー", "Discord に表示", "音楽を Windows のタスクバーに", "無料でオープンソース", "MMP で再生中", "mey music player を入手", "何かを再生中", "一時停止中", "プレーヤーのサイズ", "デフォルトに戻す", "「自由に移動」がオンのときに使えます。", "今は何も再生していません", "中央", "タスク バーのアイコンを左揃えにする必要があります: 設定 → 個人用設定 → タスク バー → タスク バーの動作 → タスク バーの配置: 左揃え", "中央（上）", "タスク バーのアイコンを中央揃えにする必要があります (Windows 11): 設定 → 個人用設定 → タスク バー → タスク バーの動作 → タスク バーの配置: 中央揃え", "イコライザー", "変更はすぐに、すべてのアプリに反映されます。", "オン", "イコライザーは Equalizer APO（Windows 用の無料・オープンソースのサウンドツール）で動作します。まだインストールされていません。", "MMP が公式セットアップ（約 12 MB）をダウンロードして起動します。Windows の許可を求められたら、サウンド デバイスを選び、PC を再起動してください。", "ダウンロードしてインストール", "ダウンロード ページを開く", "プリセット", "点を上下にドラッグします。マウス ホイール: 0.5 dB。右クリック: 0 に戻す。", "プリアンプ", "エフェクト", "低音ブースト", "ステレオの広がり", "バランス", "ボーカルを抑える", "「ボーカルを抑える」は曲の中央にある音（主に声）を下げます。ステレオの曲で最もよく効きます。スライダーを右クリック: 既定値に戻す。", "サウンド デバイス", "Equalizer APO はセットアップで選んだサウンド デバイスで動作します。別のデバイス（ヘッドホン、スピーカー）を追加するには、ここで選んでください。Windows の許可のあと、PC を再起動してください。", "デバイスを選択", "MMP は Equalizer APO の設定フォルダーに保存できません。", "許可", "ダウンロード中... {0}%", "セットアップが開いています。完了したら PC を再起動してください。", "ダウンロードできませんでした。接続を確認してもう一度お試しください。", "フラット", "低音", "ボーカル", "ロック", "ポップ", "高音", "カスタム", "イコライザーを有効にするには PC を再起動してください。それまでは変更が反映されません。", "今すぐ再起動", "使用中のデバイス：{0}" },
        ["ar"] = new[] { "البدء مع ويندوز", "الخيارات", "الموضع", "يمين", "يسار", "اللغة", "إغلاق", "تحديث متاح", "الإصدار {0} متاح. أنت تستخدم الإصدار {1}.",
            "ما الجديد", "تحديث", "لاحقاً", "جاري تنزيل التحديث...", "تعذر تثبيت التحديث. يرجى المحاولة مرة أخرى لاحقاً.", "إعادة التشغيل",
            "كلمات الأغاني", "صمّم مشغّلك", "يتم حفظ التغييرات تلقائياً.", "المشغّلات", "الافتراضي", "مشغّل مخصّص", "مشغّل جديد", "الاسم",
            "حذف المشغّل", "الخلفية", "الأزرار", "الحرباء: ألوان من الغلاف", "لون مخصّص", "الصور", "إضافة صورة",
            "PNG أو JPG أو GIF حتى 25 ميغابايت. حتى 10 صور لكل مشغّل.",
            "اسحب الصورة لتحريكها، وزاويتها لتغيير حجمها، والنقطة فوقها لتدويرها. عجلة الماوس: الحجم. Delete: إزالة.", "إزالة", "إعادة تعيين", "هل تريد إعادة هذا المشغّل إلى الألوان الافتراضية وإزالة صوره؟", "هل تريد حذف هذا المشغّل؟", "إلغاء",
            "يمكن أن يحتوي المشغّل على 10 صور كحد أقصى.", "يمكنك حفظ 5 مشغّلات كحد أقصى.",
            "لا يمكن استخدام هذا الملف. اختر صورة PNG أو JPG أو GIF حتى 25 ميغابايت.",
            "اسحب لإعادة الترتيب. الصورة العليا تكون في المقدمة.", "صورة {0}",
            "إخفاء المشغّل", "الموقع", "تحريك حر", "اسحب الغلاف إلى أي مكان على الشاشة. عند الفتح يبقى المشغّل دائماً داخل الشاشة.", "العرض فوق الألعاب", "يبقى ظاهراً أيضاً فوق الألعاب والتطبيقات بملء الشاشة. عندما تُخفي اللعبة المؤشر تذهب النقرات إلى اللعبة. إذا لم يظهر في لعبة، فاختر ملء الشاشة بلا حدود أو في نافذة من إعدادات اللعبة.", "إظهار / إخفاء", "انقر على اختصار لتغييره. عندما تُخفي اللعبة المؤشر تذهب النقرات إلى اللعبة. الاختصار الثاني يُخرج الماوس من لعبة بملء الشاشة لتستخدم المشغّل أو أي شيء آخر؛ اضغطه مرة أخرى أو انقر على اللعبة للعودة. يعمل عند تفعيل «العرض فوق الألعاب».", "إخراج الماوس من اللعبة", "اضغط اختصاراً...", "يستخدم تطبيق آخر هذا الاختصار. اختر اختصاراً آخر.", "استخدم Ctrl أو Alt أو Win مع مفتاح، أو اثنين من Ctrl وAlt وShift وحدهما، أو مفتاح F.", "الاختصار الآخر يستخدم هذه المفاتيح بالفعل.", "فعّل «العرض فوق الألعاب» لاستخدام هذا.", "الشفافية", "الكل معاً", "الغلاف", "العرض على Discord", "الموسيقى على شريط مهام ويندوز", "مجاني ومفتوح المصدر", "يستمع عبر MMP", "احصل على mey music player", "شيء ما قيد التشغيل", "متوقف مؤقتًا", "حجم المشغل", "العودة إلى الافتراضي", "يعمل عند تشغيل «تحريك حر».", "لا يتم تشغيل شيء الآن", "الوسط", "يتطلب أن تكون أيقونات شريط المهام على اليسار: الإعدادات ← التخصيص ← شريط المهام ← سلوكيات شريط المهام ← محاذاة شريط المهام: يسار", "الوسط، فوق", "يتطلب أن تكون أيقونات شريط المهام في الوسط (Windows 11): الإعدادات ← التخصيص ← شريط المهام ← سلوكيات شريط المهام ← محاذاة شريط المهام: توسيط", "المعادل الصوتي", "تُسمع التغييرات فورًا في كل التطبيقات.", "تشغيل", "يعمل المعادل الصوتي عبر Equalizer APO، وهي أداة صوت مجانية ومفتوحة المصدر لنظام Windows. لم يتم تثبيتها بعد.", "يقوم MMP بتنزيل برنامج الإعداد الرسمي (حوالي 12 ميغابايت) وتشغيله. يطلب Windows الإذن، فتختار أجهزة الصوت، ثم تعيد تشغيل الكمبيوتر.", "تنزيل وتثبيت", "فتح صفحة التنزيل", "إعدادات جاهزة", "اسحب نقطة لأعلى أو لأسفل. عجلة الماوس: نصف ديسيبل. النقر بزر الماوس الأيمن: العودة إلى 0.", "التضخيم المسبق", "المؤثرات", "تعزيز الجهير", "عرض الستيريو", "التوازن", "تخفيف الصوت البشري", "يخفض «تخفيف الصوت البشري» ما في وسط الأغنية، وهو غالبًا الصوت؛ ويعمل بشكل أفضل مع الأغاني الستيريو. النقر بزر الماوس الأيمن على شريط التمرير: القيمة الافتراضية.", "أجهزة الصوت", "يعمل Equalizer APO على أجهزة الصوت التي اخترتها أثناء الإعداد. لإضافة جهاز آخر (سماعات رأس، مكبرات صوت) اختره من هنا. يطلب Windows الإذن، ثم أعد تشغيل الكمبيوتر.", "اختيار الأجهزة", "لا يستطيع MMP الحفظ في مجلد إعدادات Equalizer APO.", "سماح", "جارٍ التنزيل... {0}%", "برنامج الإعداد مفتوح. أكمله ثم أعد تشغيل الكمبيوتر.", "لم ينجح التنزيل. تحقق من اتصالك وحاول مرة أخرى.", "مسطح", "جهير", "صوت بشري", "روك", "بوب", "حاد", "مخصص", "أعد تشغيل الكمبيوتر لتفعيل المعادل الصوتي. حتى ذلك الحين، لن تُسمع التغييرات.", "إعادة التشغيل الآن", "يعمل على: {0}" },
        ["hi"] = new[] { "Windows के साथ शुरू करें", "विकल्प", "स्थिति", "दाएं", "बाएं", "भाषा", "बंद करें", "अपडेट उपलब्ध है",
            "संस्करण {0} उपलब्ध है। आप संस्करण {1} का उपयोग कर रहे हैं।", "नया क्या है", "अपडेट करें", "बाद में", "अपडेट डाउनलोड किया जा रहा है...",
            "अपडेट इंस्टॉल नहीं किया जा सका। कृपया बाद में पुनः प्रयास करें।", "पुनः आरंभ करें", "गीत के बोल", "अपना प्लेयर डिज़ाइन करें",
            "बदलाव अपने आप सहेजे जाते हैं।", "प्लेयर", "डिफ़ॉल्ट", "कस्टम प्लेयर", "नया प्लेयर", "नाम", "प्लेयर हटाएं", "पृष्ठभूमि", "बटन",
            "गिरगिट: कवर से रंग", "कस्टम रंग", "चित्र", "चित्र जोड़ें", "PNG, JPG या GIF, 25 MB तक। हर प्लेयर में 10 चित्र तक।",
            "खिसकाने के लिए चित्र को, आकार बदलने के लिए उसके कोने को और घुमाने के लिए ऊपर के बिंदु को खींचें। माउस व्हील: आकार। Delete: निकालें।",
            "निकालें", "रीसेट करें", "इस प्लेयर को डिफ़ॉल्ट रंगों पर रीसेट करें और इसके चित्र निकालें?",
            "इस प्लेयर को हटाएं?", "रद्द करें", "एक प्लेयर में अधिकतम 10 चित्र हो सकते हैं।", "आप अधिकतम 5 प्लेयर सहेज सकते हैं।",
            "यह फ़ाइल इस्तेमाल नहीं की जा सकती। 25 MB तक का PNG, JPG या GIF चुनें।",
            "क्रम बदलने के लिए खींचें। सबसे ऊपर वाला चित्र सबसे आगे होता है।", "चित्र {0}",
            "प्लेयर छिपाएं", "जगह", "मुक्त रूप से हिलाएं", "कवर को स्क्रीन पर कहीं भी खींचें। खुलने पर प्लेयर हमेशा स्क्रीन के अंदर रहता है।", "गेम के ऊपर दिखाएं", "फ़ुल-स्क्रीन गेम और ऐप्स के ऊपर भी दिखता रहता है। जब गेम कर्सर छिपाता है, तो क्लिक गेम में जाते हैं। अगर किसी गेम में न दिखे, तो गेम की सेटिंग में बॉर्डरलेस या विंडो वाली फ़ुल स्क्रीन चुनें।", "दिखाएं / छिपाएं", "बदलने के लिए किसी शॉर्टकट पर क्लिक करें। जब गेम कर्सर छिपाता है, तो क्लिक गेम में जाते हैं। दूसरा शॉर्टकट माउस को फ़ुल-स्क्रीन गेम से बाहर लाता है, ताकि आप प्लेयर या कुछ और इस्तेमाल कर सकें; वापस जाने के लिए इसे फिर से दबाएं या गेम पर क्लिक करें। यह तब काम करता है जब गेम के ऊपर दिखाएं चालू हो।", "माउस को गेम से बाहर लाएं", "कोई शॉर्टकट दबाएं...", "यह शॉर्टकट कोई दूसरा ऐप इस्तेमाल कर रहा है। कोई दूसरा चुनें।", "किसी कुंजी के साथ Ctrl, Alt या Win, अकेले Ctrl, Alt और Shift में से दो, या कोई F कुंजी इस्तेमाल करें।", "दूसरा शॉर्टकट पहले से ये कुंजियां इस्तेमाल कर रहा है।", "इसे इस्तेमाल करने के लिए गेम के ऊपर दिखाएं चालू करें।", "पारदर्शिता", "सब एक साथ", "कवर", "Discord पर दिखाएं", "संगीत, सीधे Windows टास्कबार पर", "मुफ़्त और ओपन सोर्स", "MMP पर सुन रहे हैं", "mey music player पाएं", "कुछ चल रहा है", "रुका हुआ", "प्लेयर का आकार", "डिफ़ॉल्ट पर लौटें", "मुक्त रूप से हिलाएं चालू होने पर काम करता है।", "अभी कुछ नहीं चल रहा है", "बीच में", "टास्कबार आइकन बाईं ओर होने चाहिए: सेटिंग्स → वैयक्तिकरण → टास्कबार → टास्कबार व्यवहार → टास्कबार संरेखण: बाएँ", "बीच में, ऊपर", "टास्कबार आइकन बीच में होने चाहिए (Windows 11): सेटिंग्स → वैयक्तिकरण → टास्कबार → टास्कबार व्यवहार → टास्कबार संरेखण: केंद्र", "इक्वलाइज़र", "बदलाव तुरंत, हर ऐप में सुनाई देते हैं।", "चालू", "इक्वलाइज़र Equalizer APO के ज़रिए काम करता है, जो Windows के लिए एक मुफ़्त और ओपन सोर्स साउंड टूल है। यह अभी इंस्टॉल नहीं है।", "MMP इसका आधिकारिक सेटअप (लगभग 12 MB) डाउनलोड करके शुरू करता है। Windows अनुमति मांगता है, आप अपने साउंड डिवाइस चुनते हैं, फिर कंप्यूटर रीस्टार्ट करें।", "डाउनलोड करें और इंस्टॉल करें", "डाउनलोड पेज खोलें", "प्रीसेट", "किसी बिंदु को ऊपर या नीचे खींचें। माउस व्हील: आधा dB। राइट-क्लिक: वापस 0 पर।", "प्रीएम्प", "इफ़ेक्ट", "बेस बूस्ट", "स्टीरियो चौड़ाई", "बैलेंस", "आवाज़ कम करें", "आवाज़ कम करें गाने के बीच वाली ध्वनि (ज़्यादातर गायक की आवाज़) को कम करता है; स्टीरियो गानों पर सबसे अच्छा काम करता है। किसी स्लाइडर पर राइट-क्लिक: डिफ़ॉल्ट मान।", "साउंड डिवाइस", "Equalizer APO उन साउंड डिवाइस पर काम करता है जिन्हें आपने सेटअप में चुना था। कोई और डिवाइस (हेडफ़ोन, स्पीकर) जोड़ने के लिए उसे यहां चुनें। Windows अनुमति मांगता है, फिर कंप्यूटर रीस्टार्ट करें।", "डिवाइस चुनें", "MMP, Equalizer APO के सेटिंग फ़ोल्डर में सेव नहीं कर पा रहा है।", "अनुमति दें", "डाउनलोड हो रहा है... {0}%", "सेटअप खुला है। इसे पूरा करें, फिर कंप्यूटर रीस्टार्ट करें।", "डाउनलोड नहीं हो पाया। अपना कनेक्शन जांचें और फिर से कोशिश करें।", "फ्लैट", "बेस", "वोकल", "रॉक", "पॉप", "ट्रेबल", "कस्टम", "इक्वलाइज़र चालू करने के लिए कंप्यूटर रीस्टार्ट करें। तब तक बदलाव सुनाई नहीं देंगे।", "अभी रीस्टार्ट करें", "इन पर चल रहा है: {0}" }
    };

    string Tr(string key) =>
        (Strings.TryGetValue(_lang, out var t) ? t : Strings["en"])[Array.IndexOf(StrKeys, key)];

    void ApplyTrayTexts()
    {
        _miLyrics.Text = Tr("lyrics");
        _miHide.Text = Tr("hide");
        _miDiscord.Text = Tr("discord");
        _miStartup.Text = Tr("startup");
        _miOptions.Text = Tr("options");
        _miEq.Text = Tr("eq");
        _miPosition.Text = Tr("position");
        _miRight.Text = Tr("right");
        _miLeft.Text = Tr("left");
        _miCenter.Text = Tr("center");
        _miAbove.Text = Tr("centerAbove");
        _miLanguage.Text = Tr("language");
        _miRestart.Text = Tr("restart");
        _miClose.Text = Tr("close");
        if (_menu != null) _menu.RightToLeft = _lang == "ar" ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
    }

    void UpdateTrayChecks()
    {
        try { _miStartup.Checked = IsStartupEnabled(); } catch { }
        _miLyrics.Checked = _lyricsOn;
        _miHide.Checked = _hidden;
        _miDiscord.Checked = _discordOn;
        _miDiscord.Visible = DiscordAppId.Length > 0;     // hidden until the Discord application id is set
        _miRight.Checked = _side == Side.Right;
        _miLeft.Checked = _side == Side.Left && !_center;
        _miCenter.Checked = _center && !_above;
        _miAbove.Checked = _above;
        // with the taskbar icons in the middle (Windows 11's default) there is no room there: dimmed, and the
        // tooltip names the Windows setting. Read each time the menu opens: the setting can change any time.
        // "Center" is for taskbar icons on the left; "Center, above" is for icons in the middle (no room for "Center")
        bool centered = IconsCentered();
        _miCenter.Tag = centered ? TrayMenu.Dimmed : null;
        _miCenter.ToolTipText = centered ? Tr("centerHint") : "";
        _miAbove.Tag = centered ? null : TrayMenu.Dimmed;
        _miAbove.ToolTipText = centered ? "" : Tr("centerAboveHint");
        foreach (var (code, item) in _langItems) item.Checked = code == _lang;
    }

    string SideName() => _above ? "CenterAbove" : _center ? "Center" : _side.ToString();

    // Windows 11 puts the taskbar icons in the middle unless "Taskbar alignment" is "Left"; Windows 10 keeps them left.
    // The Start button's place tells it best (newer Windows 11 builds don't keep the setting where it used to be).
    bool IconsCentered()
    {
        if (Environment.OSVersion.Version.Build < 22000) return false;
        int width = _last.Right - _last.Left;
        if (_startLeft >= 0 && width > 0) return _startLeft - _last.Left > width / 4;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is not int al || al != 0;
        }
        catch { return true; }
    }

    void SetSide(Side side, bool center = false, bool above = false)
    {
        _side = side;
        _center = center;
        _above = above;
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
        if (_equalizer != null) { _equalizer.Close(); ShowEqualizer(); }
    }

    void ShowOptions()
    {
        if (_options != null) { _options.Activate(); return; }
        _options = new OptionsWindow(this) { FlowDirection = _lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        _options.Closed += (_, _) => _options = null;
        _options.Show();
        _options.Activate();
    }

    void ShowEqualizer()
    {
        if (_equalizer != null) { _equalizer.Activate(); return; }
        _equalizer = new EqualizerWindow(this) { FlowDirection = _lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        _equalizer.Closed += (_, _) => _equalizer = null;
        _equalizer.Show();
        _equalizer.Activate();
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
                if (k == "side") { _side = v == "Right" ? Side.Right : Side.Left; _center = v is "Center" or "CenterAbove"; _above = v == "CenterAbove"; }
                else if (k == "lang" && Strings.ContainsKey(v)) _lang = v;
                else if (k == "userX" && int.TryParse(v, out int x)) _userX = x;
                else if (k == "lyrics") _lyricsOn = v == "1";
                else if (k == "discord") _discordOn = v == "1";
                else if (k == "free") _free = v == "1";
                else if (k == "freeX" && int.TryParse(v, out int fx)) _oldFreeX = fx;
                else if (k == "freeY" && int.TryParse(v, out int fy)) _oldFreeY = fy;
                else if (k == "freeFx" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double ffx)) _freeFx = Math.Clamp(ffx, 0, 1);
                else if (k == "freeFy" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double ffy)) _freeFy = Math.Clamp(ffy, 0, 1);
                else if (k == "freeMon") _freeMon = v;
                else if (k is "showKey" or "cursorKey" && v.Split(',') is [var m, var c]
                         && uint.TryParse(m, out uint mods) && uint.TryParse(c, out uint vk))
                {
                    // only what Options can make: a key with Ctrl / Alt / Win (or an F key), or two modifiers on their own
                    mods &= MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;
                    int count = new[] { MOD_CONTROL, MOD_ALT, MOD_SHIFT }.Count(x => (mods & x) != 0);
                    bool valid = vk == 0 ? count >= 2 && (mods & MOD_WIN) == 0
                        : vk < 0xFF && ((mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) != 0 || vk is >= 0x70 and <= 0x87);
                    if (valid) (_keys[k == "showKey" ? 0 : 1].Mods, _keys[k == "showKey" ? 0 : 1].Vk) = (mods, vk);
                }
                else if (k == "onTop") _onTop = v == "1";
                else if (k == "focusGames")
                    foreach (string g in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) _focusGames.Add(g);
                else if (k == "size" && int.TryParse(v, out int sz)) _size = Math.Clamp(sz, MinSize, MaxSize);
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        StorePlacement();
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllLines(SettingsPath, new[]
            {
                $"side={SideName()}",
                $"lang={_lang}",
                $"userX={(_userX is int x ? x.ToString() : "")}",
                $"lyrics={(_lyricsOn ? 1 : 0)}",
                $"discord={(_discordOn ? 1 : 0)}",
                $"free={(_free ? 1 : 0)}",
                $"freeFx={(_freeFx is double fx ? fx.ToString("0.#####", CultureInfo.InvariantCulture) : "")}",
                $"freeFy={(_freeFy is double fy ? fy.ToString("0.#####", CultureInfo.InvariantCulture) : "")}",
                $"freeMon={_freeMon}",
                $"showKey={_keys[0].Mods},{_keys[0].Vk}",
                $"cursorKey={_keys[1].Mods},{_keys[1].Vk}",
                $"onTop={(_onTop ? 1 : 0)}",
                $"focusGames={string.Join(",", _focusGames)}",
                $"size={_size}"
            });
        }
        catch { }
    }

    static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) != null;
    }

    // MMP.exe itself: it has the .NET runtime inside, so nothing needs checking first (it used to go through the
    // launcher, two command windows flashing at sign-in). An old entry is rewritten when MMP starts.
    static string StartupCommand() => $"\"{Environment.ProcessPath}\"";

    static void SetStartup(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue(RunName, StartupCommand());
        else key.DeleteValue(RunName, false);
    }

    // ---------- free move, always on top, show / hide shortcut ----------

    internal bool FreeMove => _free;
    internal bool AlwaysOnTop => _onTop;

    internal void SetFree(bool on)
    {
        if (on == _free) return;
        _free = on;
        _dragging = false;
        if (Cover.IsMouseCaptured) Cover.ReleaseMouseCapture();
        _pxKey = default;
        SaveSettings();
        Update();
        if (_size != DefaultSize) BuildStickers();
    }

    internal const int MinSize = 50, MaxSize = 300, DefaultSize = 100;
    internal int PlayerSize => _size;

    // the player size in free move (%); the player is drawn bigger, not stretched, so it stays sharp
    internal void SetPlayerSize(int pct)
    {
        pct = Math.Clamp(pct, MinSize, MaxSize);
        if (pct == _size) return;
        _size = pct;
        _dragging = false;
        SaveSettings();
        Update();
        BuildStickers();                    // pictures decoded for the new size
    }

    internal void SetOnTop(bool on)
    {
        _onTop = on;
        if (!on) { SetMouseOut(false); SetCursorMode(false); }
        SaveSettings();
        Update();
    }

    void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden) SetCursorMode(false);
        UpdateTrayChecks();
        Update();
    }

    // ---------- the two shortcuts: 0 shows / hides the player, 1 gives a cursor in the player ----------

    internal sealed class Shortcut
    {
        public uint Mods, Vk;           // Vk 0: modifiers only (Ctrl + Alt), polled
        public bool Down, Spoiled;
        public bool Busy;               // another app has these keys: RegisterHotKey failed
        public long At;
    }

    internal string ShortcutText(int which) => KeyText(_keys[which].Mods, _keys[which].Vk);
    internal bool ShortcutBusy => _keys.Any(k => k.Busy);

    void ShortcutPressed(int which)
    {
        if (which == 0) SetHidden(!_hidden);
        else ToggleMouse();
    }

    void RegisterShortcuts()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int i = 0; i < _keys.Length; i++)
        {
            UnregisterHotKey(_hwnd, i + 1);
            _keys[i].Down = false;
            if (!_keysPaused && _keys[i].Vk != 0)
                _keys[i].Busy = !RegisterHotKey(_hwnd, i + 1, _keys[i].Mods | MOD_NOREPEAT, _keys[i].Vk);
            else if (_keys[i].Vk == 0) _keys[i].Busy = false;
        }
        bool poll = !_keysPaused && _keys.Any(k => k.Vk == 0);
        if (poll && !_keyTimer.IsEnabled) _keyTimer.Start();
        else if (!poll) _keyTimer.Stop();
    }

    // the options window lets go of the shortcuts while it waits for new keys, so the old ones are seen there
    internal void PauseShortcuts() { _keysPaused = true; RegisterShortcuts(); }
    internal void ResumeShortcuts() { _keysPaused = false; RegisterShortcuts(); }

    // null when it worked, else the text key of the reason; the old shortcut stays then
    internal string? TrySetShortcut(int which, uint mods, uint vk)
    {
        var other = _keys[1 - which];
        if (other.Mods == mods && other.Vk == vk) return "hotkeySame";
        if (vk != 0)
        {
            UnregisterHotKey(_hwnd, which + 1);
            bool free = RegisterHotKey(_hwnd, which + 1, mods | MOD_NOREPEAT, vk);
            UnregisterHotKey(_hwnd, which + 1);
            if (!free) { RegisterShortcuts(); return "hotkeyBusy"; }
        }
        _keys[which].Mods = mods;
        _keys[which].Vk = vk;
        SaveSettings();
        RegisterShortcuts();
        return null;
    }

    internal static string KeyText(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        if (vk != 0) parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    static string KeyName(uint vk)
    {
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        if (vk >= 0x30 && vk <= 0x39 || vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        // the name on the user's keyboard layout ("." , "Ö", "Page Up"...)
        uint sc = MapVirtualKey(vk, 0);
        bool extended = vk is >= 0x21 and <= 0x2E or 0x6F or 0x90;
        var sb = new StringBuilder(64);
        if (sc != 0 && GetKeyNameText((int)(sc << 16) | (extended ? 1 << 24 : 0), sb, sb.Capacity) > 0)
        {
            string s = sb.ToString();
            return s.Length == 1 ? s.ToUpperInvariant() : s;
        }
        return KeyInterop.KeyFromVirtualKey((int)vk).ToString();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() is 1 or 2)
        {
            handled = true;
            _lastHotkeyAt = Environment.TickCount64;
            ShortcutPressed(wParam.ToInt32() - 1);
        }
        // a game switched the screen to another resolution (or back): free move puts the cover in its corner again
        else if (msg == WM_DISPLAYCHANGE) OnDisplayChanged(null, EventArgs.Empty);
        else if (msg == WM_TASKBARCREATED && msg != 0)
        {
            AttachTaskbar();
            Update();
        }
        return IntPtr.Zero;
    }

    // A shortcut of modifiers only (Ctrl + Alt) counts when let go soon, with no other key or click while it was down
    // (AltGr + a key, Ctrl + Alt + Del or Ctrl + Alt + X are not it). Polled, so nothing is hooked into the keyboard.
    void PollShortcuts()
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            var k = _keys[i];
            if (k.Vk != 0) continue;
            bool down = ModsDown(k.Mods);
            if (down && !k.Down)
            {
                k.Down = true;
                k.Spoiled = false;
                k.At = Environment.TickCount64;
                OtherKeyPressed(k.Mods);        // forgets keys pressed before
            }
            else if (down)
            {
                if (!k.Spoiled && OtherKeyPressed(k.Mods)) k.Spoiled = true;
            }
            else if (k.Down)
            {
                k.Down = false;
                // the system takes the key of a shortcut like Ctrl + Alt + X away, so it is not seen above:
                // Ctrl + Alt let go right after that shortcut is not a press of its own
                bool afterHotkey = Environment.TickCount64 - _lastHotkeyAt < TapMs + 200;
                if (!k.Spoiled && !afterHotkey && !OtherKeyPressed(k.Mods) && Environment.TickCount64 - k.At < TapMs) ShortcutPressed(i);
            }
        }
    }

    static readonly (uint Mod, int[] Vks)[] ModKeys =
    {
        (MOD_CONTROL, new[] { 0x11, 0xA2, 0xA3 }), (MOD_ALT, new[] { 0x12, 0xA4, 0xA5 }),
        (MOD_SHIFT, new[] { 0x10, 0xA0, 0xA1 }), (MOD_WIN, new[] { 0x5B, 0x5C })
    };

    static bool ModsDown(uint mods) =>
        ModKeys.All(m => (mods & m.Mod) == 0 || m.Vks.Any(v => GetAsyncKeyState(v) < 0));

    // any key or mouse button other than the shortcut's own modifiers down now, or pressed since the last call
    static bool OtherKeyPressed(uint mods)
    {
        bool any = false;
        for (int vk = 1; vk < 255; vk++)
        {
            if (ModKeys.Any(m => (mods & m.Mod) != 0 && m.Vks.Contains(vk))) continue;
            if ((GetAsyncKeyState(vk) & 0x8001) != 0) any = true;      // no early return: every key's "pressed since" is read
        }
        return any;
    }

    // ---------- mouse out of the game ----------
    // A full-screen game holds the mouse while it has the focus. The shortcut gives the focus to MMP, like Alt + Tab
    // without leaving the game: games let go of the mouse then, so the player or anything else can be used.
    // The player itself takes the focus when it is on the screen (a click on it then keeps the focus; with a separate
    // window Windows handed the focus back to the game on that click), else a tiny window does.
    // The shortcut again, or a click on the game, gives the focus back. Only with "always on top" on.
    void SetMouseOut(bool on)
    {
        if (on == _mouseOut) return;
        if (on)
        {
            if (!_onTop) return;
            _gameWin = GetForegroundWindow();
            _mouseOut = true;
            if (_shown)
            {
                _playerFocused = true;
                SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
                SetForegroundWindow(_hwnd);
            }
            else
            {
                _focusWin ??= CreateFocusWindow();
                _focusWin.Show();
                SetForegroundWindow(new WindowInteropHelper(_focusWin).Handle);
            }
            ClipCursor(IntPtr.Zero);                    // in case the game keeps the mouse in its box
            // a game in exclusive full screen that was not recognised drops to the desktop: bring it back at once
            // and use the drawn cursor instead
            IntPtr game = _gameWin;
            var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            long until = Environment.TickCount64 + 3000;      // an old game takes a while to drop
            check.Tick += (_, _) =>
            {
                if (!_mouseOut || _gameWin != game || !IsWindow(game)) { check.Stop(); return; }
                if (!IsIconic(game))
                {
                    if (Environment.TickCount64 > until) check.Stop();
                    return;
                }
                check.Stop();
                _dropsWin = game;
                if (ProcessNameOf(game) is string name && _focusGames.Add(name)) SaveSettings();
                EndMouseOut();
                ShowWindow(game, SW_RESTORE);
                SetForegroundWindow(game);
                SetCursorMode(true);
            };
            check.Start();
        }
        else
        {
            _mouseOut = false;
            IntPtr fg = GetForegroundWindow();
            bool ours = fg == _hwnd || (_focusWin != null && fg == new WindowInteropHelper(_focusWin).Handle);
            if (ours && _gameWin != IntPtr.Zero && IsWindow(_gameWin)) SetForegroundWindow(_gameWin);
            EndMouseOut();
        }
    }

    // the focus went back (the shortcut, a click on the game or another app): the player stops taking it
    void EndMouseOut()
    {
        _mouseOut = false;
        if (_playerFocused) SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        _playerFocused = false;
        _focusWin?.Hide();
        _gameWin = IntPtr.Zero;
    }

    Window CreateFocusWindow()
    {
        var w = new Window
        {
            Title = "mey music player", Width = 1, Height = 1, Left = -100, Top = -100, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);     // not in Alt + Tab
        };
        // the game or another app was clicked: the mouse went back by itself
        w.Deactivated += (_, _) => { if (_mouseOut && !_playerFocused) EndMouseOut(); };
        return w;
    }

    // ---------- cursor drawn in the player (a game that changed the screen's resolution) ----------

    void ToggleMouse()
    {
        if (_cursorMode) { SetCursorMode(false); return; }
        if (_mouseOut) { SetMouseOut(false); return; }
        if (!_onTop) return;
        if (GameNeedsFocus()) SetCursorMode(true);
        else SetMouseOut(true);
    }

    static string? ProcessNameOf(IntPtr hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return null; }
    }

    // A game in exclusive full screen drops to the desktop as soon as it loses the focus, so the focus must stay with
    // it: Windows says so for Direct3D games, a game at another resolution than the screen's own (1080p on a 1440p
    // screen) is one, and so is a game already seen dropping
    bool GameNeedsFocus()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == _dropsWin) return true;
        if (fg != IntPtr.Zero && ProcessNameOf(fg) is string name && _focusGames.Contains(name)) return true;
        if (SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN) return true;
        if (!IsFullScreenAppActive()) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr mon = MonitorFromPoint(new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 }, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero || !GetMonitorInfoEx(mon, ref info)) return false;
        var now = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        var own = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref now) ||
            !EnumDisplaySettings(info.szDevice, ENUM_REGISTRY_SETTINGS, ref own)) return false;
        return now.dmPelsWidth != own.dmPelsWidth || now.dmPelsHeight != own.dmPelsHeight;
    }

    // The game keeps the focus and gets none of the mouse: moves and clicks stop at a low-level hook and drive a cursor
    // drawn in the player instead. The shortcut again, hiding the player, another app taking the screen or 15 s without
    // touching the mouse gives the mouse back to the game.
    void SetCursorMode(bool on)
    {
        if (on == _cursorMode) return;
        if (on)
        {
            if (_hidden) SetHidden(false);
            if (!_shown) return;
            _cursorProc ??= CursorProc;
            _cursorHook = SetWindowsHookEx(WH_MOUSE_LL, _cursorProc, GetModuleHandle(null), 0);
            if (_cursorHook == IntPtr.Zero) return;
            _cursorMode = true;
            _vDown = _cursorAnchored = false;
            _vPress = null;
            _lastCursorInput = Environment.TickCount64;
            // a game can hold the cursor in a tiny box; let go of it so the mouse steps can be seen
            _hadClip = GetClipCursor(out _savedClip);
            ClipCursor(IntPtr.Zero);
            SetClickThrough(false);
            PlayerCursor.Visibility = Visibility.Visible;
            MoveDrawnCursor();
            ApplyLyricsLayer();                         // the buttons, not the lyrics, while the cursor is there
            _cursorIdleTimer.Start();
        }
        else
        {
            _cursorMode = false;
            _cursorIdleTimer.Stop();
            if (_cursorHook != IntPtr.Zero) UnhookWindowsHookEx(_cursorHook);
            _cursorHook = IntPtr.Zero;
            if (_vPress == Cover && _dragMoved) DropCover();           // a drag that was going on ends where it is
            if (_scrubbing && _vPress == SeekHit) { _scrubbing = false; CommitSeek(_pendingSeek); }
            _vDown = false;
            _vPress = null;
            if (_hadClip) ClipCursor(ref _savedClip);
            PlayerCursor.Visibility = Visibility.Collapsed;
            ApplyLyricsLayer();
            Mouse.Synchronize();                        // the real mouse is not over the player: hover effects end
        }
    }

    IntPtr CursorProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_cursorMode) return CallNextHookEx(_cursorHook, nCode, wParam, lParam);
        var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        int msg = wParam.ToInt32();
        _lastCursorInput = Environment.TickCount64;
        if (msg == WM_MOUSEMOVE)
        {
            // the real cursor stays where it is (the move is dropped); the step it would have made moves the drawn one
            GetCursorPos(out POINT cur);
            _vx += d.pt.X - cur.X;
            _vy += d.pt.Y - cur.Y;
            _cursorAnchored = true;
            MoveDrawnCursor();
        }
        if (msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MBUTTONDOWN or WM_MOUSEWHEEL)
        {
            short delta = (short)((d.mouseData >> 16) & 0xFFFF);
            Dispatcher.BeginInvoke(new Action(() => VirtualMouse(msg, delta)));     // out of the hook quickly
        }
        return (IntPtr)1;       // the game (and every other app) gets nothing while the cursor is in the player
    }

    // What the player would do with a real mouse at the drawn cursor. WPF itself drops mouse messages while the real
    // cursor is somewhere else, so the drawn cursor finds what is under it and does it.
    void VirtualMouse(int msg, short delta)
    {
        if (!_cursorMode) return;
        double f = _coverSize / 40.0;
        var pt = new Point((_vx - _winX) / f, (_vy - _gapY) / f);      // in the player's design units
        var at = new Point(_vx, _vy);
        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (_vPress == Cover) CoverDragTo(at);
                else if (_vPress == SeekHit && _scrubbing) ScrubTo(Content.TranslatePoint(pt, SeekHit).X);
                break;
            case WM_LBUTTONDOWN:
                _vDown = true;
                _vPress = TargetAt(pt);
                if (_vPress == Cover) CoverPress(at);
                else if (_vPress == SeekHit && _session != null && _tlEnd - _tlStart > TimeSpan.Zero)
                {
                    _scrubbing = true;
                    ScrubTo(Content.TranslatePoint(pt, SeekHit).X);
                }
                break;
            case WM_LBUTTONUP:
                _vDown = false;
                var pressed = _vPress;
                _vPress = null;
                if (pressed == Cover) CoverRelease();
                else if (pressed == SeekHit && _scrubbing)
                {
                    ScrubTo(Content.TranslatePoint(pt, SeekHit).X);
                    _scrubbing = false;
                    CommitSeek(_pendingSeek);
                }
                else if (pressed != null && TargetAt(pt) == pressed)
                {
                    if (pressed == VolumeHit) ToggleMute();
                    else if (pressed is Button b) b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
                }
                break;
            case WM_MBUTTONDOWN:
                if (TargetAt(pt) == Cover) _ = _session?.TryTogglePlayPauseAsync();
                break;
            case WM_MOUSEWHEEL:
                var over = TargetAt(pt);
                if (over == Cover) CoverWheel(delta);
                else if (over == SeekHit) SeekBy(TimeSpan.FromSeconds(delta > 0 ? 5 : -5));
                else if (over == VolumeHit) ChangeVolume(WheelVolumeStep(delta));
                break;
        }
    }

    // the cover, the seek bar, the speaker or a button under the drawn cursor
    FrameworkElement? TargetAt(Point pt)
    {
        for (var d = Content.InputHitTest(pt) as DependencyObject; d != null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == Cover || d == SeekHit || d == VolumeHit) return (FrameworkElement)d;
            if (d is Button b) return b;
            if (d == Content) break;
        }
        return null;
    }

    // keeps the drawn cursor on the player (on the screen while the cover is dragged) and draws it there
    void MoveDrawnCursor()
    {
        double f = _coverSize / 40.0;
        if (!_cursorAnchored)
        {
            // until the mouse moves: on the cover
            _vx = _gapX + _coverSize / 2.0;
            _vy = _gapY + _coverSize / 2.0;
        }
        RECT r;
        if (_vDown && _vPress == Cover) r = MonitorAt((int)_vx, (int)_vy);
        else
        {
            int left = _side == Side.Left ? _gapX : _gapX + _coverSize - _winW;
            r = new RECT { Left = left, Top = _gapY, Right = left + _winW, Bottom = _gapY + _coverSize };
        }
        _vx = Math.Clamp(_vx, r.Left, Math.Max(r.Left, r.Right - 1));
        _vy = Math.Clamp(_vy, r.Top, Math.Max(r.Top, r.Bottom - 1));
        Canvas.SetLeft(PlayerCursor, (_vx - _winX) / f);
        Canvas.SetTop(PlayerCursor, (_vy - _gapY) / f);
    }

    // While a game hides the cursor (driving, aiming), clicks and the mouse go through the player to the game.
    // When the cursor is shown (the game's menu, the desktop), the player can be used and dragged as usual.
    // Over the player Windows shows the player's own cursor, so the game's cursor is only seen while the mouse is
    // somewhere else, or while clicks already pass through (then the game is under the mouse).
    void UpdateClickThrough()
    {
        if (_cursorMode) return;        // the drawn cursor uses the player, whatever the game does with its cursor
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return;
        // what is drawn: the cover and the open part of the player (with the cover on the right, the window is wider)
        int left = _side == Side.Left ? _gapX : _gapX + _coverSize - _winW;
        var at = ci.ptScreenPos;
        bool overPlayer = at.X >= left && at.X < left + _winW && at.Y >= _gapY && at.Y < _gapY + _coverSize;
        if (_clickThrough || !overPlayer)
            _cursorHidden = (ci.flags & (CURSOR_SHOWING | CURSOR_SUPPRESSED)) == 0
                || (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor == IntPtr.Zero;
        SetClickThrough(_cursorHidden && !Cover.IsMouseCaptured && !_scrubbing);
    }

    void SetClickThrough(bool through)
    {
        if (through == _clickThrough) return;
        _clickThrough = through;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        if (through)
        {
            _hoverTimer.Stop();
            TitlePopup.IsOpen = false;
        }
        Mouse.Synchronize();
        ApplyLyricsLayer();
        Refresh();
    }

    void OnTrayHover()
    {
        GetCursorPos(out _trayPos);
        _lastTrayHover = Environment.TickCount64;
        if (_wheelHook != IntPtr.Zero) return;
        _wheelProc = WheelProc;
        _wheelHook = SetWindowsHookEx(WH_MOUSE_LL, _wheelProc, GetModuleHandle(null), 0);     // this exe
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
                Dispatcher.BeginInvoke(new Action(() => ChangeVolume(WheelVolumeStep(delta))));
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
        _title = _artist = _album = _source = "";
        _thumb = null;
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
        sb.AppendLine($"discord: {_discord?.Status} | app: {_app?.Name ?? "-"} | {(_isMusic ? "music" : _isVideo ? "video" : "other")}");
        sb.AppendLine($"cover  : {_coverUrl ?? (_coverAsked != 0 ? "looking..." : "-")}");
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
        bool lyrics = active && (!PlayerPanel.IsMouseOver || _clickThrough) && !_cursorMode;
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
        _cursorTimer.Stop();
        _keyTimer.Stop();
        _discordTimer.Stop();
        _discord?.Dispose();                // clears the line on the profile
        SetMouseOut(false);
        SetCursorMode(false);
        if (_hwnd != IntPtr.Zero) { UnregisterHotKey(_hwnd, 1); UnregisterHotKey(_hwnd, 2); }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
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
