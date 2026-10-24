using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace TaskbarPlayerProto;

// The equalizer's settings, kept in %LOCALAPPDATA%\MMP\equalizer.json. Gains are in dB.
sealed class EqSettings
{
    public bool On { get; set; } = true;
    public double[] Bands { get; set; } = new double[Equalizer.Frequencies.Length];
    public double Preamp { get; set; }
    public double Bass { get; set; }            // bass boost, 0..15 dB
    public int Width { get; set; } = 100;       // stereo width, 0 (mono) .. 200 %
    public int Balance { get; set; }            // -100 (left) .. 100 (right)
    public int Vocals { get; set; }             // reduce vocals, 0..100 %
    public int Reverb { get; set; }             // 0..100 %
    public int Enhancer { get; set; }           // brighter, clearer highs (like a car stereo's sound retriever), 0..100 %
    public string Preset { get; set; } = "flat";    // a preset's key, "saved:<name>" or "custom"
    public List<string> Apps { get; set; } = new() { "spotify" };      // process names that go through it
    public List<SavedEq> Saved { get; set; } = new();
}

// A sound the user saved under a name (up to Equalizer.MaxSaved)
sealed class SavedEq
{
    public string Name { get; set; } = "";
    public double[] Bands { get; set; } = Array.Empty<double>();
    public double Preamp { get; set; }
    public double Bass { get; set; }
    public int Width { get; set; } = 100;
    public int Balance { get; set; }
    public int Vocals { get; set; }
    public int Reverb { get; set; }
    public int Enhancer { get; set; }

    public static SavedEq From(EqSettings s, string name) => new()
    {
        Name = name, Bands = (double[])s.Bands.Clone(), Preamp = s.Preamp, Bass = s.Bass, Width = s.Width,
        Balance = s.Balance, Vocals = s.Vocals, Reverb = s.Reverb, Enhancer = s.Enhancer
    };

    public void ApplyTo(EqSettings s)
    {
        s.Bands = (double[])Bands.Clone();
        s.Preamp = Preamp; s.Bass = Bass; s.Width = Width; s.Balance = Balance; s.Vocals = Vocals; s.Reverb = Reverb;
        s.Enhancer = Enhancer;
        s.Preset = "saved:" + Name;
    }
}

// Settings, presets and the apps it can work on. The sound itself: EqEngine (and EqDsp), through VB-CABLE, a free
// virtual audio cable by VB-Audio (donationware, vb-cable.com) that MMP installs once.
static class Equalizer
{
    public static readonly int[] Frequencies =
        { 31, 43, 63, 87, 125, 175, 250, 350, 500, 700, 1000, 1400, 2000, 2800, 4000, 5600, 8000, 11200, 16000, 22000 };
    public const double MaxGain = 15;
    public const int MaxSaved = 5;
    // the bands of v1.3.0's first build, for settings saved with it
    static readonly int[] OldFrequencies = { 31, 63, 87, 125, 175, 250, 350, 500, 700, 1000, 1400, 2000, 2800, 4000, 5600, 8000, 11200, 16000 };

    public static readonly (string Key, double[] Bands)[] Presets =
    {
        ("flat",   new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("bass",   new double[] { 7, 6.5, 6, 5.5, 4.5, 3.5, 2.5, 1.5, 0.5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("vocal",  new double[] { -2, -2, -2, -1.5, -1, 0, 1, 2, 3, 3.5, 3.5, 3, 2.5, 2, 1, 0, -0.5, -1, -1.5, -1.5 }),
        ("rock",   new double[] { 5, 5, 4.5, 4, 3, 1.5, 0, -1, -1.5, -1, 0, 1, 2, 3, 3.5, 4, 4, 4, 4, 4 }),
        ("pop",    new double[] { -1, -1, -0.5, 0, 1, 2, 3, 3.5, 3.5, 3, 2, 1, 0, -0.5, -0.5, 0, 0.5, 1, 1, 1 }),
        ("treble", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5, 1, 1.5, 2.5, 3.5, 4.5, 5.5, 6, 6.5, 7, 7 }),
    };

    // the music apps MMP works with (process name, shown name); the page lists the ones that are open
    public static readonly (string Process, string Name)[] Apps =
    {
        ("spotify", "Spotify"),
        ("brave", "Brave"), ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("opera", "Opera"),
        ("vivaldi", "Vivaldi"), ("arc", "Arc"), ("zen", "Zen"), ("librewolf", "LibreWolf"), ("waterfox", "Waterfox"),
        ("floorp", "Floorp"), ("thorium", "Thorium"),
        ("applemusic", "Apple Music"), ("itunes", "iTunes"), ("deezer", "Deezer"), ("tidal", "TIDAL"),
        ("amazon music", "Amazon Music"), ("youtube music", "YouTube Music"), ("cider", "Cider"), ("qobuz", "Qobuz"),
        ("microsoft.media.player", "Media Player"), ("wmplayer", "Windows Media Player"), ("aimp", "AIMP"),
        ("foobar2000", "foobar2000"), ("musicbee", "MusicBee"), ("winamp", "Winamp"), ("mediamonkey", "MediaMonkey"),
        ("vlc", "VLC"), ("potplayermini64", "PotPlayer"), ("mpv", "mpv"),
    };

    public static List<(string Process, string Name)> OpenApps()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            running.Add(p.ProcessName);
            p.Dispose();
        }
        return Apps.Where(a => running.Contains(a.Process)).ToList();
    }

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP", "equalizer.json");

    // ---------- settings ----------

    public static EqSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<EqSettings>(File.ReadAllText(SettingsPath));
            if (s != null) return Clean(s);
        }
        catch { }
        return new EqSettings();
    }

    static double[] CleanBands(double[]? old)
    {
        var bands = new double[Frequencies.Length];
        old ??= Array.Empty<double>();
        if (old.Length == OldFrequencies.Length)
        {
            // saved with the 18 bands: each band keeps its value, a new band takes its lower neighbour's
            for (int i = 0, j = 0; i < bands.Length; i++)
            {
                while (j + 1 < OldFrequencies.Length && OldFrequencies[j + 1] <= Frequencies[i]) j++;
                bands[i] = Math.Clamp(old[j], -MaxGain, MaxGain);
            }
        }
        else
            for (int i = 0; i < bands.Length && i < old.Length; i++) bands[i] = Math.Clamp(old[i], -MaxGain, MaxGain);
        return bands;
    }

    static EqSettings Clean(EqSettings s)
    {
        s.Bands = CleanBands(s.Bands);
        s.Preamp = Math.Clamp(s.Preamp, -MaxGain, MaxGain);
        s.Bass = Math.Clamp(s.Bass, 0, MaxGain);
        s.Width = Math.Clamp(s.Width, 0, 200);
        s.Balance = Math.Clamp(s.Balance, -100, 100);
        s.Vocals = Math.Clamp(s.Vocals, 0, 100);
        s.Reverb = Math.Clamp(s.Reverb, 0, 100);
        s.Enhancer = Math.Clamp(s.Enhancer, 0, 100);
        s.Apps ??= new();
        s.Saved = (s.Saved ?? new()).Where(x => x.Name.Trim().Length > 0).Take(MaxSaved).ToList();
        foreach (var x in s.Saved) x.Bands = CleanBands(x.Bands);
        s.Preset ??= "custom";
        return s;
    }

    // ---------- a sound in a file (export / import) ----------

    public const string SoundExtension = ".mmpeq";

    // readable by people too: letters like "ö" stay as they are, not ö
    public static void ExportSound(SavedEq sound, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sound, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

    // null when the file isn't a sound MMP can use
    public static SavedEq? ImportSound(string path)
    {
        try
        {
            var x = JsonSerializer.Deserialize<SavedEq>(File.ReadAllText(path));
            if (x?.Bands is not { Length: > 0 }) return null;
            x.Name = (x.Name ?? "").Trim();
            if (x.Name.Length == 0) x.Name = Path.GetFileNameWithoutExtension(path);
            if (x.Name.Length > 24) x.Name = x.Name[..24];
            x.Bands = CleanBands(x.Bands);
            x.Preamp = Math.Clamp(x.Preamp, -MaxGain, MaxGain);
            x.Bass = Math.Clamp(x.Bass, 0, MaxGain);
            x.Width = Math.Clamp(x.Width, 0, 200);
            x.Balance = Math.Clamp(x.Balance, -100, 100);
            x.Vocals = Math.Clamp(x.Vocals, 0, 100);
            x.Reverb = Math.Clamp(x.Reverb, 0, 100);
            x.Enhancer = Math.Clamp(x.Enhancer, 0, 100);
            return x;
        }
        catch { return null; }
    }

    public static void Save(EqSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    // ---------- VB-CABLE ----------

    // the official driver pack from vb-audio.com; checked before it runs
    public const string CableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string CableSha256 = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    public const long CableBytes = 1318877;
    public const string CableSite = "https://vb-audio.com/Cable/";

    public enum SetupResult { Done, NeedsRestart, Failed, Declined }

    // Downloads (progress 0..1), checks and installs VB-CABLE; Windows asks for administrator rights. Its setup makes the
    // cable the default sound device: the user's own default devices are put back.
    public static async Task<SetupResult> InstallCableAsync(IProgress<double> progress, CancellationToken cancel)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MMP");
        string zip = Path.Combine(dir, "VBCABLE_Driver_Pack45.zip"), files = Path.Combine(dir, "vbcable");
        try
        {
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MMP");
            using var response = await http.GetAsync(CableUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var to = File.Create(zip))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    done += n;
                    if (done > CableBytes * 2) throw new InvalidDataException("too big");
                    progress.Report(Math.Min(1, done / (double)CableBytes));
                }
            }
            string hash;
            await using (var file = File.OpenRead(zip))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false));
            if (!hash.Equals(CableSha256, StringComparison.OrdinalIgnoreCase)) return SetupResult.Failed;
            if (Directory.Exists(files)) Directory.Delete(files, true);
            ZipFile.ExtractToDirectory(zip, files);
        }
        catch { return SetupResult.Failed; }
        finally { try { File.Delete(zip); } catch { } }

        var defaults = DefaultDevices.Save();
        string setup = Path.Combine(files, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        try
        {
            // -i -h: install without its window
            using var p = Process.Start(new ProcessStartInfo(setup, "-i -h") { UseShellExecute = true, Verb = "runas", WorkingDirectory = files });
            if (p == null) return SetupResult.Failed;
            await p.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception) { return SetupResult.Declined; }     // no to Windows' question
        catch { return SetupResult.Failed; }

        // the cable shows up within a few seconds (it may make itself the default a moment later too)
        bool found = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            found |= EqEngine.CableInstalled;
            DefaultDevices.Restore(defaults);
        }
        try { Directory.Delete(files, true); } catch { }
        return found ? SetupResult.Done : SetupResult.NeedsRestart;
    }

    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    }

    // ---------- Equalizer APO (v1.3) ----------

    const string ApoNote = "# mey music player (MMP) no longer uses Equalizer APO. This file does nothing.\r\n";

    static string? ApoConfigDir
    {
        get
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\EqualizerAPO");
                return key?.GetValue("ConfigPath") is string c && Directory.Exists(c) ? c : null;
            }
            catch { return null; }
        }
    }

    // v1.3 played the equalizer through Equalizer APO, on everything the device played. MMP's file there is emptied,
    // so Equalizer APO stops changing the sound for MMP. True when Equalizer APO is still installed.
    public static bool ApoCleanup()
    {
        if (ApoConfigDir is not { } dir) return false;
        try
        {
            string own = Path.Combine(dir, "mmp.txt");
            if (File.Exists(own) && File.ReadAllText(own) != ApoNote) File.WriteAllText(own, ApoNote);
            foreach (var f in Directory.GetFiles(dir, "mmp-reverb*.wav")) File.Delete(f);
        }
        catch { }
        return true;
    }
}
