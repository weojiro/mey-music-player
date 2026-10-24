using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TaskbarPlayerProto;

// Plays the chosen apps through the equalizer. Windows sends each chosen app to VB-CABLE, so it isn't heard directly;
// MMP takes that app's own sound, processes it and plays it on the default device. Discord, games and every other
// app are left alone. Switched off, or when MMP closes, the apps go back to the device they used before.
static class EqEngine
{
    const int PumpMs = 3;
    const string CableName = "VB-Audio Virtual Cable";

    static readonly object Gate = new();
    static volatile EqSettings _settings = new() { On = false };
    static Thread? _scanThread, _pumpThread;
    static volatile bool _running;
    static readonly AutoResetEvent Wake = new(false);

    // what MMP changed, so it can be put back even after a crash: program file -> the device it used before
    static readonly string RoutesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP", "eq-routes.json");
    static Dictionary<string, string> _routes = LoadRoutes();

    // the scan's state (under Gate); the pump and the output only see the snapshot
    static readonly Dictionary<int, Source> _sources = new();
    static volatile Source[] _snapshot = Array.Empty<Source>();
    static readonly Dictionary<int, string> _routed = new();    // pid -> program file
    static Source? _cableSource;                                // older Windows: VB-CABLE's own output, all apps at once
    static WasapiOut? _output;
    static volatile Mixer? _mixer;
    static readonly Dictionary<int, (string Name, string File)> _processes = new();     // pid -> name and program file
    static string? _outputId;
    static long _lastSound;
    static int _skipId;
    static volatile string? _cableId;

    // the stream window is open: the chosen apps go through MMP even with the equalizer off (unchanged), so a
    // Discord stream of MMP's window carries the sound
    static volatile bool _streaming;
    public static bool Streaming
    {
        get => _streaming;
        set { _streaming = value; Wake.Set(); }
    }

    // what was just played, for the stream window's bars and wave: the last samples (mono) and when they came
    static readonly float[] _scope = new float[8192];
    static int _scopePos;
    static long _scopeAt;
    public static int ScopeRate { get; private set; } = 48000;
    static volatile bool _heard;

    public static void Start(EqSettings settings)
    {
        Update(settings);
        if (_scanThread != null) return;
        _running = true;
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "MMP equalizer scan" };
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "MMP equalizer", Priority = ThreadPriority.AboveNormal };
        _scanThread.SetApartmentState(ApartmentState.MTA);
        _pumpThread.SetApartmentState(ApartmentState.MTA);
        _scanThread.Start();
        _pumpThread.Start();
    }

    public static void Update(EqSettings settings)
    {
        // no lock: the scan may be busy, and a slider being dragged must never wait for it
        var copy = JsonSerializer.Deserialize<EqSettings>(JsonSerializer.Serialize(settings))!;
        var old = _settings;
        _settings = copy;
        _mixer?.Dsp.Set(copy);
        if (old.On != copy.On || !old.Apps.SequenceEqual(copy.Apps)) Wake.Set();      // on, off or other apps: at once
    }

    // MMP is closing: every app goes back to where it was. An app that was on the cable and is closed now can only be
    // put back once it runs again (Windows takes a running program): helper (MMP --restore-routes, no window) waits
    // for it, unless MMP is closing because Windows is.
    static bool _stopped;
    public static void Stop(bool leaveHelper = true)
    {
        if (_stopped) return;           // Windows closing calls it, then the app's exit again
        _stopped = true;
        _running = false;
        Wake.Set();
        _scanThread?.Join(1500);
        _pumpThread?.Join(500);
        lock (Gate)
        {
            StopAll();
            if (leaveHelper && _routes.Count > 0 && Environment.ProcessPath is { } exe)
                try { Process.Start(new ProcessStartInfo(exe, "--restore-routes") { UseShellExecute = false }); } catch { }
        }
    }

    // MMP --restore-routes: puts each closed app back on its own device as soon as it runs; ends when all are back,
    // when MMP itself starts again (it does the same), or after a day
    public static void RestoreRoutes()
    {
        var until = DateTime.Now.AddDays(1);
        while (_routes.Count > 0 && DateTime.Now < until)
        {
            if (Mutex.TryOpenExisting(@"Local\MMP-single-instance", out var main)) { main.Dispose(); return; }
            foreach (var (file, before) in _routes.ToList())
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(file)))
                {
                    try
                    {
                        string path;
                        try { path = p.MainModule?.FileName ?? ""; } catch { path = ""; }
                        if (path.Length > 0 && !path.Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
                        if (AppRouting.Set(p.Id, before)) { _routes.Remove(file); SaveRoutes(); break; }
                    }
                    finally { p.Dispose(); }
                }
            }
            Thread.Sleep(1500);
        }
    }

    public static string? CableId()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(CableName) && d.FriendlyName.StartsWith("CABLE Input"))?.ID;
        }
        catch { return null; }
    }

    // Listing the sound devices takes a few hundred milliseconds: windows asking every few seconds would freeze for
    // that long (and stutter the stream window). The scan, once a second on its own thread, keeps the answer.
    static volatile bool _cableSeen;
    public static bool CableInstalled => _scanThread != null ? _cableSeen : CableId() != null;

    // ---------- the worker ----------

    static void ScanLoop()
    {
        while (_running)
        {
            try { lock (Gate) if (_running) Scan(); }
            catch (Exception ex) { ErrorLog.Write("eq", ex); }
            Wake.WaitOne(1000);
        }
    }

    // every few milliseconds: what the apps played since
    static void PumpLoop()
    {
        while (_running)
        {
            bool sound = false;
            foreach (var src in _snapshot) sound |= src.Pull();
            if (sound)
            {
                _lastSound = Environment.TickCount64;
                if (!_heard) { _heard = true; Wake.Set(); }     // playing again after a pause: the output starts
            }
            Thread.Sleep(PumpMs);
        }
    }

    // once a second: which apps are playing, where they are sent, and the output device
    static void Scan()
    {
        var s = _settings;
        using var en = new MMDeviceEnumerator();
        var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var cable = devices.FirstOrDefault(d => d.FriendlyName.Contains(CableName) && d.FriendlyName.StartsWith("CABLE Input"));
        _cableSeen = cable != null;
        _cableId = cable?.ID;
        var output = en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
        if (output != null && cable != null && output.ID == cable.ID) output = null;
        if (_outputId != null && output?.ID != _outputId) DropSources();       // another device: maybe another rate
        bool on = (s.On || Streaming) && cable != null && output != null && s.Apps.Count > 0 && AppRouting.Available;
        var apps = new HashSet<string>(s.Apps, StringComparer.OrdinalIgnoreCase);

        // every app that has sound on any device
        var playing = new Dictionary<int, (string Name, string File)>();
        foreach (var d in devices)
        {
            try
            {
                var sessions = d.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    int pid = (int)sessions[i].GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId || playing.ContainsKey(pid)) continue;
                    if (_processes.TryGetValue(pid, out var known)) { playing[pid] = known; continue; }
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        string file;
                        try { file = p.MainModule?.FileName ?? p.ProcessName; } catch { file = p.ProcessName; }
                        playing[pid] = _processes[pid] = (p.ProcessName.ToLowerInvariant(), file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        foreach (var pid in _processes.Keys.Where(p => !playing.ContainsKey(p)).ToList()) _processes.Remove(pid);

        foreach (var (pid, (name, file)) in playing)
        {
            bool want = on && apps.Contains(name);
            if (want && !_routed.ContainsKey(pid))
            {
                string? before = AppRouting.Get(pid);
                if (before == null) continue;
                if (!_routes.ContainsKey(file) && !AppRouting.Is(before, cable!.ID)) { _routes[file] = before; SaveRoutes(); }
                if (!AppRouting.Set(pid, cable!.ID)) continue;
                _routed[pid] = file;
            }
            else if (!want && _routes.ContainsKey(file) && !_routed.ContainsKey(pid) && cable != null && AppRouting.Is(AppRouting.Get(pid), cable.ID))
                Unroute(pid, file);     // left on the cable by MMP (a crash, or switched off while closed)
            if (want && !_sources.ContainsKey(pid) && ProcessLoopback.Supported)
            {
                var client = ProcessLoopback.Activate(pid);
                if (client != null && output != null) _sources[pid] = new Source(client, output.AudioClient.MixFormat.SampleRate);
            }
        }

        // apps that stopped, or aren't wanted any more
        foreach (var (pid, file) in _routed.ToList())
        {
            bool alive = playing.TryGetValue(pid, out var app);
            if (alive && on && apps.Contains(app.Name)) continue;
            _routed.Remove(pid);
            if (alive) Unroute(pid, file);
            if (_sources.Remove(pid, out var src))
            {
                _snapshot = _snapshot.Where(x => x != src).ToArray();
                src.Dispose();
            }
        }

        // older Windows can't take one app's sound: VB-CABLE's output carries every chosen app
        bool cableMode = on && !ProcessLoopback.Supported && _routed.Count > 0;
        if (cableMode && _cableSource == null && output != null)
        {
            var rec = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName.Contains(CableName));
            if (rec != null) _cableSource = new Source(rec.AudioClient, output.AudioClient.MixFormat.SampleRate, loopback: false);
        }
        else if (!cableMode && _cableSource != null) { _cableSource.Dispose(); _cableSource = null; }

        _snapshot = _sources.Values.Append(_cableSource).OfType<Source>().ToArray();

        // the output: only while there is something to play, on the device that is the default now
        bool any = _snapshot.Length > 0;
        bool quiet = Environment.TickCount64 - _lastSound > 15000;      // a paused app: let the computer sleep
        if (quiet) _heard = false;
        if (!any || quiet || output == null || output.ID != _outputId) StopOutput();
        if (any && !quiet && output != null && _output == null) StartOutput(output);
    }

    // MMP's own skip buttons: the song goes quiet first (a quarter of a second), then skips. If no new song comes,
    // the sound comes back by itself.
    public static async Task BeforeSkip()
    {
        var s = _settings;
        var mixer = _mixer;
        if (!s.On || !s.TrackFade || mixer == null) return;
        int id = Interlocked.Increment(ref _skipId);
        mixer.Dsp.Fade(0, 250);
        await Task.Delay(260);
        _ = Task.Delay(2500).ContinueWith(_ => { if (Volatile.Read(ref _skipId) == id) _mixer?.Dsp.Fade(1, 600); });
    }

    // a new song started (however it was skipped): a moment of silence, then it comes up over a second and a half
    public static void SongChanged()
    {
        var s = _settings;
        var mixer = _mixer;
        if (!s.On || !s.TrackFade || mixer == null) return;
        Interlocked.Increment(ref _skipId);
        mixer.Dsp.Fade(1, 1500, 250);
    }

    // the last dest.Length samples; false when nothing has played for a moment
    public static bool ReadScope(float[] dest)
    {
        if (Environment.TickCount64 - Volatile.Read(ref _scopeAt) > 250) return false;
        int pos = Volatile.Read(ref _scopePos);
        for (int i = 0; i < dest.Length; i++) dest[i] = _scope[(pos - dest.Length + i) & (_scope.Length - 1)];
        return true;
    }

    // back to the device it used before; the saved device is forgotten only when no other process of the same program
    // is still on the cable (a browser's second process would otherwise go to the default instead of the user's own)
    static void Unroute(int pid, string file)
    {
        string before = _routes.TryGetValue(file, out var b) ? b : "";
        if (!AppRouting.Set(pid, before)) return;
        bool others = _routed.Any(r => r.Key != pid && r.Value.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (!others && _routes.Remove(file)) SaveRoutes();
    }

    // the cable's device id, as the last scan saw it (the volume control looks there for the apps MMP plays)
    public static string? CableDeviceId => _cableId;

    static void DropSources()
    {
        _snapshot = Array.Empty<Source>();
        foreach (var src in _sources.Values) src.Dispose();
        _sources.Clear();
        _cableSource?.Dispose();
        _cableSource = null;
    }

    static void StartOutput(MMDevice device)
    {
        try
        {
            var format = device.AudioClient.MixFormat;
            _mixer = new Mixer(format.SampleRate, format.Channels);
            ScopeRate = format.SampleRate;
            _mixer.Dsp.Set(_settings);
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 10);
            _output.Init(_mixer);
            _output.Play();
            _outputId = device.ID;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("eq output", ex);
            StopOutput();
        }
    }

    static void StopOutput()
    {
        try { _output?.Stop(); _output?.Dispose(); } catch { }
        _output = null;
        _mixer = null;
        _outputId = null;
    }

    static void StopAll()
    {
        StopOutput();
        foreach (var (pid, file) in _routed.ToList())
        {
            _routed.Remove(pid);
            try { using var p = Process.GetProcessById(pid); Unroute(pid, file); } catch { }
        }
        DropSources();
    }

    static Dictionary<string, string> LoadRoutes()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RoutesPath)) ?? new(); }
        catch { return new(); }
    }

    static void SaveRoutes()
    {
        try
        {
            if (_routes.Count == 0) { File.Delete(RoutesPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(RoutesPath)!);
            File.WriteAllText(RoutesPath, JsonSerializer.Serialize(_routes));
        }
        catch { }
    }

    // ---------- one app's sound ----------

    sealed class Source : IDisposable
    {
        const int MaxMs = 40, KeepMs = 15;     // more than 40 ms waiting: the oldest goes, so the delay stays small
        readonly AudioClient _client;
        readonly AudioCaptureClient _capture;
        readonly float[] _ring;
        readonly int _max, _keep;
        int _read, _count;
        float[] _packet = new float[4096];
        readonly object _lock = new();

        public Source(AudioClient client, int rate, bool loopback = true)
        {
            _client = client;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY: Windows hands it over in the output's format
            var flags = (AudioClientStreamFlags)0x80000000 | (AudioClientStreamFlags)0x08000000;
            if (loopback) flags |= AudioClientStreamFlags.Loopback;
            _client.Initialize(AudioClientShareMode.Shared, flags, 1000000, 0, format, Guid.Empty);     // room for 100 ms; read every 3
            _capture = _client.AudioCaptureClient;
            _ring = new float[rate * 2];        // a second
            _max = rate * 2 * MaxMs / 1000;
            _keep = rate * 2 * KeepMs / 1000;
            _client.Start();
        }

        // true when something louder than silence came
        public bool Pull()
        {
            bool sound = false;
            try
            {
                while (_capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = _capture.GetBuffer(out int frames, out var flags);
                    int n = frames * 2;
                    if (_packet.Length < n) _packet = new float[n];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_packet, 0, n);
                    else Marshal.Copy(data, _packet, 0, n);
                    lock (_lock)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            float v = _packet[i];
                            if (v > 1e-4f || v < -1e-4f) sound = true;
                            _ring[(_read + _count) % _ring.Length] = v;
                            if (_count < _ring.Length) _count++; else _read = (_read + 1) % _ring.Length;
                        }
                        if (_count > _max) { _read = (_read + _count - _keep) % _ring.Length; _count = _keep; }
                    }
                    _capture.ReleaseBuffer(frames);
                }
            }
            catch { }
            return sound;
        }

        // adds up to `samples` into mix; missing ones stay as they are (silence)
        public void AddTo(float[] mix, int samples)
        {
            lock (_lock)
            {
                int n = Math.Min(samples, _count);
                for (int i = 0; i < n; i++) mix[i] += _ring[(_read + i) % _ring.Length];
                _read = (_read + n) % _ring.Length;
                _count -= n;
            }
        }

        public void Dispose()
        {
            try { _client.Stop(); _client.Dispose(); } catch { }
        }
    }

    // ---------- what the output plays ----------

    sealed class Mixer : IWaveProvider
    {
        readonly int _channels;
        float[] _mix = new float[4096];
        public readonly EqDsp Dsp;
        public WaveFormat WaveFormat { get; }

        public Mixer(int rate, int channels)
        {
            _channels = channels;
            Dsp = new EqDsp(rate);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / (4 * _channels);
            if (_mix.Length < frames * 2) _mix = new float[frames * 2];
            Array.Clear(_mix, 0, frames * 2);
            foreach (var s in _snapshot) s.AddTo(_mix, frames * 2);
            Dsp.Process(_mix, frames);
            int pos = _scopePos;
            for (int f = 0; f < frames; f++) _scope[pos++ & (_scope.Length - 1)] = (_mix[2 * f] + _mix[2 * f + 1]) / 2;
            Volatile.Write(ref _scopePos, pos & (_scope.Length - 1));
            Volatile.Write(ref _scopeAt, Environment.TickCount64);
            var output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * _channels));
            for (int f = 0; f < frames; f++)
            {
                if (_channels == 1) { output[f] = (_mix[2 * f] + _mix[2 * f + 1]) / 2; continue; }
                output[f * _channels] = _mix[2 * f];
                output[f * _channels + 1] = _mix[2 * f + 1];
                for (int c = 2; c < _channels; c++) output[f * _channels + c] = 0;
            }
            return frames * 4 * _channels;
        }
    }
}
