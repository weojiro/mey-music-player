using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskbarPlayerProto;

// The equalizer's settings, kept in %LOCALAPPDATA%\MMP\equalizer.json. Gains are in dB.
sealed class EqSettings
{
    public bool On { get; set; } = true;
    public double[] Bands { get; set; } = new double[Equalizer.Frequencies.Length];
    public double Preamp { get; set; }
    public double Bass { get; set; }            // bass boost, 0..12 dB
    public int Width { get; set; } = 100;       // stereo width, 0 (mono) .. 200 %
    public int Balance { get; set; }            // -100 (left) .. 100 (right)
    public int Vocals { get; set; }             // reduce vocals, 0..100 %
    public string Preset { get; set; } = "flat";
}

// The sound goes through Equalizer APO (free, open source, works on the whole sound device). MMP writes its own file
// (mmp.txt) into Equalizer APO's config folder and includes it once from config.txt, so the user's own Equalizer APO
// settings stay as they are. Equalizer APO reads the files again as soon as they change.
static class Equalizer
{
    public static readonly int[] Frequencies =
        { 31, 43, 63, 87, 125, 175, 250, 350, 500, 700, 1000, 1400, 2000, 2800, 4000, 5600, 8000, 11200, 16000, 22000 };
    public const double MaxGain = 15;
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

    // the official setup from SourceForge; checked before it runs
    public const string SetupVersion = "1.4.2";
    public const string SetupUrl = "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";
    public const string SetupSha256 = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
    public const long SetupBytes = 11980366;
    public const string FilesPage = "https://sourceforge.net/projects/equalizerapo/files/";
    const string OwnFile = "mmp.txt";
    const string IncludeLine = "Include: " + OwnFile;

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP", "equalizer.json");

    // ---------- Equalizer APO on this computer ----------

    static string? ReadKey(string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\EqualizerAPO");
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    public static string? InstallDir => ReadKey("InstallPath") is { Length: > 0 } p && Directory.Exists(p) ? p : null;

    public static string? ConfigDir
    {
        get
        {
            if (ReadKey("ConfigPath") is { Length: > 0 } c && Directory.Exists(c)) return c;
            return InstallDir is { } i && Directory.Exists(Path.Combine(i, "config")) ? Path.Combine(i, "config") : null;
        }
    }

    public static bool Installed => ConfigDir != null;

    // Equalizer APO starts working after a restart: true while it was installed, or a device was added, after the
    // computer was last started (the setup writes Uninstall.exe, the device picker a backup_*.reg file per device)
    public static bool NeedsRestart
    {
        get
        {
            if (InstallDir is not { } dir) return false;
            try
            {
                var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
                var files = Directory.GetFiles(dir, "backup_*.reg").Append(Path.Combine(dir, "Uninstall.exe")).Where(File.Exists);
                return files.Any(f => File.GetLastWriteTime(f) > boot.AddSeconds(30));
            }
            catch { return false; }
        }
    }

    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    }

    // Equalizer APO's own device picker (it needs administrator rights; Windows asks)
    public static bool OpenDevicePicker()
    {
        if (InstallDir is not { } dir) return false;
        foreach (var name in new[] { "DeviceSelector.exe", "Configurator.exe" })
        {
            string exe = Path.Combine(dir, name);
            if (!File.Exists(exe)) continue;
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); return true; }
            catch { return false; }     // the user said no to Windows' question
        }
        return false;
    }

    // Lets every user change the config folder (Windows asks once). Only needed when the folder was made read-only.
    public static bool AllowWriting()
    {
        if (ConfigDir is not { } dir) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

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

    static EqSettings Clean(EqSettings s)
    {
        var bands = new double[Frequencies.Length];
        var old = s.Bands ?? Array.Empty<double>();
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
        s.Bands = bands;
        s.Preamp = Math.Clamp(s.Preamp, -MaxGain, MaxGain);
        s.Bass = Math.Clamp(s.Bass, 0, MaxGain);
        s.Width = Math.Clamp(s.Width, 0, 200);
        s.Balance = Math.Clamp(s.Balance, -100, 100);
        s.Vocals = Math.Clamp(s.Vocals, 0, 100);
        return s;
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

    // ---------- the Equalizer APO file ----------

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // What MMP's file says. Off: only a comment, so the sound is left as it is.
    public static string ConfigText(EqSettings s)
    {
        var sb = new StringBuilder("# Written by mey music player (MMP). Change it in MMP: tray menu, Equalizer.\r\n");
        if (!s.On) return sb.Append("# Equalizer off\r\n").ToString();
        if (s.Preamp != 0) sb.Append($"Preamp: {F(s.Preamp)} dB\r\n");
        if (s.Bands.Any(b => b != 0))
            sb.Append("GraphicEQ: ").Append(string.Join("; ", Frequencies.Select((f, i) => $"{f} {F(s.Bands[i])}"))).Append("\r\n");
        if (s.Bass > 0) sb.Append($"Filter: ON LS Fc 105 Hz Gain {F(s.Bass)} dB\r\n");

        // width, vocals and balance in one mix of the left and right channels: the middle (what both have, mostly
        // the voice) and the sides (what differs) are turned up or down, then each side gets its share of the balance
        double mid = 1 - s.Vocals / 100.0, side = s.Width / 100.0;
        double left = s.Balance > 0 ? 1 - s.Balance / 100.0 : 1, right = s.Balance < 0 ? 1 + s.Balance / 100.0 : 1;
        if (mid != 1 || side != 1 || left != 1 || right != 1)
        {
            double same = (mid + side) / 2, other = (mid - side) / 2;
            sb.Append($"Copy: L={F(same * left)}*L+{F(other * left)}*R R={F(other * right)}*L+{F(same * right)}*R\r\n");
        }
        return sb.ToString();
    }

    public enum ApplyResult { Done, Busy, Denied }

    // Writes MMP's file and makes sure config.txt includes it. Busy: Equalizer APO was reading a file just then (try
    // again in a moment); Denied: the folder can't be written at all.
    public static ApplyResult Apply(EqSettings s)
    {
        if (ConfigDir is not { } dir) return ApplyResult.Denied;
        try { File.WriteAllText(Path.Combine(dir, OwnFile + ".tmp"), ""); }
        catch { return ApplyResult.Denied; }
        try
        {
            WriteAtomic(Path.Combine(dir, OwnFile), ConfigText(s));
            AddInclude(Path.Combine(dir, "config.txt"));
            return ApplyResult.Done;
        }
        catch (UnauthorizedAccessException) { return ApplyResult.Busy; }   // replacing a file that is open fails this way
        catch (IOException) { return ApplyResult.Busy; }
    }

    // a new file moved over the old one: Equalizer APO never reads a half-written file
    static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    static void AddInclude(string configPath)
    {
        string text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        bool IsOurs(string l) => string.Equals(l.Replace(" ", ""), IncludeLine.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        // Equalizer APO's own first config (a -6 dB preamp, its example bass filters and a flat GraphicEQ) would colour
        // the sound: it is put aside (kept as config-before-mmp.txt) and only MMP's file is used
        var others = lines.Where(l => !IsOurs(l)).ToList();
        bool fresh = others.All(l => Regex.IsMatch(l, @"^Preamp:\s*-6\s*dB$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(l, @"^Include:\s*example\.txt$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(l, @"^GraphicEQ:(\s*\d+(\.\d+)?\s+0(\.0+)?\s*;?)*$", RegexOptions.IgnoreCase));
        if (fresh && (others.Count > 0 || lines.Count == 0))
        {
            if (others.Count > 0) File.Copy(configPath, Path.Combine(Path.GetDirectoryName(configPath)!, "config-before-mmp.txt"), true);
            WriteAtomic(configPath, IncludeLine + "\r\n");
            return;
        }
        if (lines.Any(IsOurs)) return;
        // the user's own settings stay; MMP's file comes after them
        string add = (text.Length > 0 && !text.EndsWith('\n') ? "\r\n" : "") + "# mey music player\r\n" + IncludeLine + "\r\n";
        File.AppendAllText(configPath, add, Encoding.ASCII);
    }

    // ---------- setup ----------

    public static bool SetupAvailable => RuntimeInformation.OSArchitecture == Architecture.X64 && SetupSha256.Length == 64;

    public enum SetupResult { Started, Failed, Declined }

    // Downloads the setup (progress 0..1), checks it and starts it; Windows asks for administrator rights
    public static async Task<SetupResult> DownloadAndRunAsync(IProgress<double> progress, CancellationToken cancel)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MMP");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"EqualizerAPO-x64-{SetupVersion}.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MMP");
            using var response = await http.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var to = File.Create(path))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await from.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    done += n;
                    if (done > SetupBytes * 2) throw new InvalidDataException("too big");
                    progress.Report(Math.Min(1, done / (double)SetupBytes));
                }
            }
            string hash;
            await using (var file = File.OpenRead(path))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false));
            if (!hash.Equals(SetupSha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(path); return SetupResult.Failed; }
        }
        catch
        {
            try { File.Delete(path); } catch { }
            return SetupResult.Failed;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return SetupResult.Started; }
        catch (System.ComponentModel.Win32Exception) { return SetupResult.Declined; }     // no to Windows' question
        catch { return SetupResult.Failed; }
    }
}
