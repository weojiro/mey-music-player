using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarPlayerProto;

// The song's cover on Discord. Discord shows a picture only from a web address, and Windows hands MMP the cover as
// a picture, so the song is looked up: on SoundCloud itself for SoundCloud, then on Deezer and Apple Music. A cover is
// used only when it looks the same as the one the app shows: a single, its album and a remix often have different
// covers. No match: the app's logo. Only while "Show on Discord" is on. Found addresses are kept while MMP runs.
static class DiscordCover
{
    const int Grid = 12;                    // fingerprint: the cover as 12 x 12 average colours
    const double SameBelow = 20;            // average difference per colour (0-255) that still counts as the same cover
    const int MaxChecks = 4;                // small covers downloaded per service and song at most
    const int MaxCached = 300;

    static readonly HttpClient Http = CreateClient();
    // lookup (the song and its picture) -> address, null: nothing matched; and picture -> address, so the other
    // songs of an album need no lookup
    static readonly Dictionary<string, string?> Found = new();
    static readonly Queue<string> Order = new();
    static readonly Dictionary<string, string> ByPicture = new();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MMP/{Updater.Current} (https://github.com/weojiro/mey-music-player)");
        return client;
    }

    // a short name for a picture: its fingerprint rounded, so the same cover gives the same name
    public static string Hash(byte[] print)
    {
        var sb = new StringBuilder(print.Length);
        foreach (byte b in print) sb.Append((char)('a' + b / 32));
        return sb.ToString();
    }

    public static bool TryCached(string key, string picture, out string? url)
    {
        lock (Found)
        {
            if (ByPicture.TryGetValue(picture, out var same)) { url = same; return true; }
            return Found.TryGetValue(key, out url);
        }
    }

    static void Remember(string key, string picture, string? url)
    {
        lock (Found)
        {
            if (Found.ContainsKey(key)) return;
            Found[key] = url;
            Order.Enqueue(key);
            if (url != null) ByPicture[picture] = url;
            while (Order.Count > MaxCached) Found.Remove(Order.Dequeue());
            if (ByPicture.Count > MaxCached) ByPicture.Clear();
        }
    }

    // The address of a cover that matches print, or null. Never throws; a network error is not remembered,
    // so the song is asked again the next time it plays.
    // app: where the song plays. SoundCloud and YouTube Music show their own pictures, found by the song's address in
    // the browser; nothing found there is not remembered: the address can be read only while the tab is in front, so
    // it is asked again later.
    public static async Task<string?> FindAsync(string key, string title, string artist, byte[] print, MusicApp app)
    {
        string picture = Hash(print);
        if (TryCached(key, picture, out var known)) return known;
        bool soundCloud = app == MusicSources.SoundCloud, youTube = app == MusicSources.YouTubeMusic;
        try
        {
            string? url = soundCloud ? await FromSoundCloud(title, artist, print).ConfigureAwait(false) : null;
            url ??= await FromDeezer(title, artist, print).ConfigureAwait(false)
                 ?? await FromApple(title, artist, print).ConfigureAwait(false);
            // YouTube Music shows a video's picture for many songs (not the album's)
            if (url == null && youTube) url = await FromYouTube(title).ConfigureAwait(false);
            if (url != null || !(soundCloud || youTube)) Remember(key, picture, url);
            return url;
        }
        catch { return null; }
    }

    // The video's picture. No fingerprint check here: the id comes from the address of the YouTube Music window that
    // shows the song's name, which is always the song playing (and Windows gets a zoomed crop of the picture).
    static async Task<string?> FromYouTube(string title)
    {
        string? id = await Task.Run(() => MusicSources.YouTubeMusicId(title)).ConfigureAwait(false);
        if (id == null) return null;
        foreach (string size in new[] { "hq720", "mqdefault" })        // 1280 x 720 for HD videos, 320 x 180 for all
        {
            string url = $"https://i.ytimg.com/vi/{id}/{size}.jpg";
            using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url)).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return url;
        }
        return null;
    }

    // SoundCloud's own cover, by the song's address: first a guess from the names ("soundcloud.com/artist/title"),
    // then the link in the player bar of the SoundCloud tab. SoundCloud tells a song's cover by its address (oEmbed).
    static async Task<string?> FromSoundCloud(string title, string artist, byte[] print)
    {
        string user = Slug(artist);
        var guesses = new List<string> { $"https://soundcloud.com/{user}/{Slug(title)}" };
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) guesses.Add($"https://soundcloud.com/{user}/{Slug(title[(dash + 3)..])}");
        foreach (string page in guesses.Distinct().ToList())
            if (await SoundCloudCover(page, print).ConfigureAwait(false) is string found) return found;
        // the page open in the SoundCloud tab and the link in its player bar
        var pages = await Task.Run(() => MusicSources.SongAddresses(title, MusicSources.SoundCloud)).ConfigureAwait(false);
        foreach (string address in pages)
        {
            string page = address.Split('?', '#')[0].TrimEnd('/');
            if (page.Count(c => c == '/') != 4 || guesses.Contains(page)) continue;     // https://soundcloud.com/user/song
            guesses.Add(page);
            if (await SoundCloudCover(page, print).ConfigureAwait(false) is string found) return found;
        }
        return null;
    }

    static async Task<string?> SoundCloudCover(string page, byte[] print)
    {
        if (page.EndsWith('/')) return null;            // a name without letters
        using var response = await Http.GetAsync("https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(page)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;     // 404: no such song
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        string big = Text(doc.RootElement, "thumbnail_url");
        if (!big.Contains("-t500x500.")) return null;   // the uploader's picture instead of a cover
        byte[]? theirs;
        try { theirs = PrintOf(await Http.GetByteArrayAsync(big.Replace("-t500x500.", "-large.")).ConfigureAwait(false)); }
        catch (HttpRequestException) { return null; }
        return theirs != null && Difference(print, theirs) < SameBelow ? big : null;
    }

    // "Gece Yarısı (Remix)" -> "gece-yarisi-remix", like SoundCloud's addresses
    static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
            else if (c == 'ı') sb.Append('i');
            else if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    static async Task<string?> FromDeezer(string title, string artist, byte[] print)
    {
        // a plain search: the advanced one (artist:"..." track:"...") finds nothing for many songs
        string url = "https://api.deezer.com/search?limit=10&q=" + Uri.EscapeDataString(FirstArtist(artist) + " " + title);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        var picks = new List<(string Small, string Big)>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("album", out var album)) continue;
                string small = Text(album, "cover_small"), big = Text(album, "cover_xl");
                if (small != "" && big != "" && SameSong(title, artist, Text(item, "title"), ArtistName(item)))
                    picks.Add((small, big));
            }
        return await FirstMatch(picks, print).ConfigureAwait(false);
    }

    static async Task<string?> FromApple(string title, string artist, byte[] print)
    {
        string country = "US";
        try { country = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { }
        string url = "https://itunes.apple.com/search?media=music&entity=song&limit=15&country=" + Uri.EscapeDataString(country)
            + "&term=" + Uri.EscapeDataString(FirstArtist(artist) + " " + title);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        var picks = new List<(string Small, string Big)>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
            {
                string small = Text(item, "artworkUrl100");
                if (small.Contains("100x100bb") && SameSong(title, artist, Text(item, "trackName"), Text(item, "artistName")))
                    picks.Add((small, small.Replace("100x100bb", "600x600bb")));
            }
        return await FirstMatch(picks, print).ConfigureAwait(false);
    }

    static async Task<string?> FirstMatch(List<(string Small, string Big)> picks, byte[] print)
    {
        var seen = new HashSet<string>();
        int checks = 0;
        foreach (var (small, big) in picks)
        {
            if (!seen.Add(small)) continue;         // the songs of one album share a cover
            if (++checks > MaxChecks) break;
            byte[]? theirs;
            try { theirs = PrintOf(await Http.GetByteArrayAsync(small).ConfigureAwait(false)); }
            catch (HttpRequestException) { continue; }
            if (theirs != null && Difference(print, theirs) < SameBelow) return big;
        }
        return null;
    }

    // ---------- is it the same song (a cheap check before any picture is downloaded) ----------

    static bool SameSong(string title, string artist, string theirTitle, string theirArtist)
    {
        string a = Core(title), b = Core(theirTitle);
        if (a == "" || b == "" || !(a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) return false;
        string mine = Plain(FirstArtist(artist)), theirs = Plain(theirArtist);
        return mine == "" || theirs.Contains(mine) || mine.Contains(theirs);
    }

    // "Song - Remastered 2011", "Song (feat. X)" -> "song"
    static string Core(string title)
    {
        int cut = title.Length;
        foreach (string mark in new[] { " - ", " (", " [" })
        {
            int i = title.IndexOf(mark, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }
        return Plain(title[..cut]);
    }

    static string Plain(string s) => MusicSources.Plain(s);

    static string FirstArtist(string artist) => artist.Split(',', '&', ';')[0].Trim();

    static string ArtistName(JsonElement item) =>
        item.TryGetProperty("artist", out var a) ? Text(a, "name") : "";

    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    // ---------- fingerprints: is it the same picture ----------

    // the cover Spotify shows (in the player), on the UI thread
    public static byte[]? PrintOf(BitmapSource cover)
    {
        var src = cover.Format == PixelFormats.Bgra32 ? cover : new FormatConvertedBitmap(cover, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w < Grid || h < Grid) return null;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        return PrintOf(px, w, h, w * 4);
    }

    // a downloaded cover (JPEG), off the UI thread
    static byte[]? PrintOf(byte[] file)
    {
        try
        {
            using var ms = new MemoryStream(file);
            using var bmp = new Bitmap(ms);
            if (bmp.Width < Grid || bmp.Height < Grid) return null;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[data.Stride * bmp.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                return PrintOf(px, bmp.Width, bmp.Height, data.Stride);
            }
            finally { bmp.UnlockBits(data); }
        }
        catch { return null; }
    }

    // average colour of each cell of the centred square; see-through parts count as black
    static byte[] PrintOf(byte[] bgra, int w, int h, int stride)
    {
        int side = Math.Min(w, h), ox = (w - side) / 2, oy = (h - side) / 2;
        var sum = new long[Grid * Grid * 3];
        var count = new int[Grid * Grid];
        for (int y = 0; y < side; y++)
        {
            int row = (oy + y) * stride, gy = y * Grid / side;
            for (int x = 0; x < side; x++)
            {
                int i = row + (ox + x) * 4, cell = gy * Grid + x * Grid / side, a = bgra[i + 3];
                sum[cell * 3] += bgra[i + 2] * a / 255;
                sum[cell * 3 + 1] += bgra[i + 1] * a / 255;
                sum[cell * 3 + 2] += bgra[i] * a / 255;
                count[cell]++;
            }
        }
        var print = new byte[Grid * Grid * 3];
        for (int c = 0; c < print.Length; c++) print[c] = (byte)(sum[c] / Math.Max(1, count[c / 3]));
        return print;
    }

    internal static double Difference(byte[] a, byte[] b)
    {
        long total = 0;
        for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total / (double)a.Length;
    }
}
