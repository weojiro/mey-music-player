using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace TaskbarPlayerProto;

// A music app or website the song can be shown from on Discord, with its logo (shown when its cover can't be found)
sealed record MusicApp(string Name, string Logo)
{
    // "" for an app without a logo of its own: the MMP logo is used
    public string? LogoUrl => Logo.Length > 0 ? "https://weojiro.github.io/mey-music-player/img/apps/" + Logo + "?v=1" : null;
}

// Which app or website a song plays in. The song is shown only from music apps and music websites: Windows names
// desktop apps, but for a browser it only says "Brave", so the browser window whose title shows the song is found and
// the address in its address bar tells the website. Anything else stays "Something is playing".
static class MusicSources
{
    public static readonly MusicApp SoundCloud = new("SoundCloud", "soundcloud.png");
    public static readonly MusicApp YouTubeMusic = new("YouTube Music", "youtube-music.png");
    static readonly MusicApp Spotify = new("Spotify", "spotify.png"), AppleMusic = new("Apple Music", "apple-music.png"),
        Deezer = new("Deezer", "deezer.png"), Tidal = new("TIDAL", "tidal.png"), AmazonMusic = new("Amazon Music", "amazon-music.png");

    // desktop apps, found in the media id Windows gives (e.g. "Spotify.exe", "AppleInc.AppleMusicWin_...!App")
    static readonly (string Id, MusicApp App)[] Apps =
    {
        ("Spotify", Spotify), ("AppleMusic", AppleMusic), ("iTunes", new("iTunes", "apple-music.png")), ("Deezer", Deezer),
        ("TIDAL", Tidal), ("AmazonMusic", AmazonMusic), ("Amazon Music", AmazonMusic),
        ("ZuneMusic", new("Media Player", "media-player.png")),
        ("youtube-music", YouTubeMusic),        // the YouTube Music desktop app
    };

    // websites, by their address ("music.amazon." for every country)
    static readonly (string Host, MusicApp App)[] Sites =
    {
        ("music.youtube.com", YouTubeMusic), ("soundcloud.com", SoundCloud), ("open.spotify.com", Spotify),
        ("deezer.com", Deezer), ("music.apple.com", AppleMusic), ("listen.tidal.com", Tidal), ("music.amazon.", AmazonMusic),
    };

    // websites installed as an app from the browser (no address bar): by the name in the window title
    static readonly (string Mark, MusicApp App)[] InstalledSites =
    {
        ("YouTube Music", YouTubeMusic), ("SoundCloud", SoundCloud), ("Spotify", Spotify), ("Deezer", Deezer),
        ("Apple Music", AppleMusic), ("TIDAL", Tidal), ("Amazon Music", AmazonMusic),
    };

    static readonly string[] Browsers =
    {
        "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "arc", "browser", "librewolf", "waterfox",
        "zen", "floorp", "thorium", "chromium", "iexplore"
    };

    // The app or website of what plays now, or null when the song must stay private (a video is never asked).
    // aumid: Windows' media id; music: Windows says it is music (browsers don't always); title: the song's name.
    // sure: the answer won't change for this song (false while the browser window with the song isn't found).
    public static MusicApp? Identify(string aumid, bool music, string title, out bool sure)
    {
        sure = true;
        if (aumid.Length == 0 || Plain(title).Length < 2) return null;
        string key = Key(aumid);
        // an app installed from a browser ("Brave._crx_..."): its window title
        int crx = aumid.IndexOf("._crx_", StringComparison.OrdinalIgnoreCase);
        if (crx > 0) return InstalledSite(title, aumid[..crx], out sure);
        if (Browsers.Contains(key, StringComparer.OrdinalIgnoreCase)) return Site(title, key, out sure);
        foreach (var (id, app) in Apps)
            if (aumid.Contains(id, StringComparison.OrdinalIgnoreCase)) return app;
        // another desktop app that says it plays music: shown with its own name, the MMP logo for a picture
        string? name = AppName(key);
        if (name != null) return music ? new MusicApp(name, "") : null;
        // a media id that is no app (Firefox gives a number): treated as a browser
        return Site(title, null, out sure);
    }

    // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "Microsoft.ZuneMusic", "Spotify.exe" -> "Spotify"
    static string Key(string aumid)
    {
        int bang = aumid.IndexOf('!');
        if (bang >= 0) aumid = aumid[(bang + 1)..];
        return aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? aumid[..^4] : aumid;
    }

    // the name of a running desktop app (its description, like "foobar2000"); null when no such app runs.
    // Remembered per media id: it is asked every few seconds while a song can't be shown.
    static readonly Dictionary<string, string?> AppNames = new(StringComparer.OrdinalIgnoreCase);

    static string? AppName(string key)
    {
        if (key.Length == 0) return null;
        if (AppNames.TryGetValue(key, out var known)) return known;
        string? name = null;
        var procs = Array.Empty<Process>();
        try
        {
            procs = Process.GetProcessesByName(key);
            if (procs.Length > 0)
            {
                try
                {
                    string desc = procs[0].MainModule?.FileVersionInfo.FileDescription?.Trim() ?? "";
                    name = desc.Length > 1 ? desc : key;
                }
                catch { name = key; }
            }
        }
        catch { }
        finally { foreach (var p in procs) p.Dispose(); }
        if (AppNames.Count > 100) AppNames.Clear();
        AppNames[key] = name;
        return name;
    }

    // The music website in the browser window that shows the song's name (browser: the process name, null for any
    // browser). Only that window's tab in front shows the song in the title, so its address is the song's website.
    static MusicApp? Site(string title, string? browser, out bool sure)
    {
        sure = false;
        foreach (var (hwnd, _) in SongWindows(title, browser))
        {
            string? address = Address(hwnd);
            if (address == null) continue;
            sure = true;                    // a music website, or a website that stays private
            return SiteOf(address);
        }
        return null;
    }

    static MusicApp? InstalledSite(string title, string browser, out bool sure)
    {
        sure = false;
        foreach (var (_, text) in SongWindows(title, browser))
            foreach (var (mark, app) in InstalledSites)
                if (text.Contains(mark, StringComparison.OrdinalIgnoreCase)) { sure = true; return app; }
        return null;
    }

    static MusicApp? SiteOf(string address)
    {
        string host = Regex.Replace(address, "^[a-z]+://", "", RegexOptions.IgnoreCase);
        int slash = host.IndexOfAny(new[] { '/', '?', '#' });
        if (slash >= 0) host = host[..slash];
        host = host.ToLowerInvariant();
        foreach (var (h, app) in Sites)
            if (h.EndsWith('.') ? host.StartsWith(h, StringComparison.Ordinal)
                                : host == h || host.EndsWith("." + h, StringComparison.Ordinal))
                return app;
        return null;
    }

    // browser windows whose title shows the song's name
    static IEnumerable<(IntPtr Hwnd, string Title)> SongWindows(string title, string? browser)
    {
        string song = Plain(title);
        return BrowserWindows(browser).Where(w => Plain(w.Title).Contains(song));
    }

    // visible top-level windows of the browsers (or of one browser) with their titles
    static List<(IntPtr Hwnd, string Title)> BrowserWindows(string? browser)
    {
        var found = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out uint pid) != 0)
            {
                string name = ProcessName(pid);
                if (browser != null ? name.Equals(browser, StringComparison.OrdinalIgnoreCase)
                                    : Browsers.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    var sb = new StringBuilder(512);
                    if (GetWindowText(h, sb, sb.Capacity) > 0) found.Add((h, sb.ToString()));
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // process names by id, kept: the windows are listed every few seconds (cheap after the first time)
    static readonly Dictionary<uint, string> Names = new();

    static string ProcessName(uint pid)
    {
        lock (Names)
        {
            if (Names.TryGetValue(pid, out var name)) return name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { name = ""; }
            if (Names.Count > 500) Names.Clear();
            return Names[pid] = name;
        }
    }

    // The address in a browser window's address bar (Windows' accessibility interface): "OmniboxViewViews" in Chrome
    // and Edge, "BraveOmniboxViewViews" in Brave, "AddressTextfieldView" in Opera and Opera GX; "urlbar-input" in
    // Firefox. Null if it can't be read.
    static string? Address(IntPtr hwnd)
    {
        try
        {
            var bar = AutomationElement.FromHandle(hwnd)
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .FirstOrDefault(e => e.Current.ClassName.EndsWith("OmniboxViewViews", StringComparison.Ordinal)
                                  || e.Current.ClassName == "AddressTextfieldView"
                                  || e.Current.AutomationId == "urlbar-input");
            if (bar?.TryGetCurrentPattern(ValuePattern.Pattern, out object? p) == true && p is ValuePattern v)
            {
                string address = v.Current.Value.Trim();
                return address.Length > 0 ? address : null;
            }
        }
        catch { }
        return null;
    }

    // The address of the song playing on a website (app: SoundCloud or YouTube Music), from the window that shows
    // the song: its address bar, or for SoundCloud the link in the player bar (another page may be open). Null if
    // not found.
    public static List<string> SongAddresses(string title, MusicApp app)
    {
        var found = new List<string>();
        foreach (var (hwnd, _) in SongWindows(title, null))
        {
            string? address = Address(hwnd);
            if (address == null || SiteOf(address) != app) continue;
            found.Add("https://" + Regex.Replace(address, "^[a-z]+://", "", RegexOptions.IgnoreCase));
            if (app != SoundCloud) continue;
            try
            {
                var link = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.NameProperty, title)));
                if (link?.TryGetCurrentPattern(ValuePattern.Pattern, out object? p) == true && p is ValuePattern v)
                    found.Add(v.Current.Value);
            }
            catch { }
        }
        return found;
    }

    // The video id of the song playing on YouTube Music (the address follows the song: music.youtube.com/watch?v=...)
    public static string? YouTubeMusicId(string title)
    {
        foreach (string address in SongAddresses(title, YouTubeMusic))
        {
            var m = Regex.Match(address, @"music\.youtube\.com/watch\?(?:.*&)?v=([\w-]{11})");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // letters and digits only, lower case, without accents: for comparing names
    public static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace('ı', 'i');
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
