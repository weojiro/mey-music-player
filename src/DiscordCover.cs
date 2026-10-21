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
// a picture, so the song is looked up on Deezer, then on Apple Music. A cover is used only when it looks the same as
// the one Spotify shows: a single, its album and a remix often have different covers. No match: the MMP logo.
// Spotify songs only, while "Show on Discord" is on. Found addresses are kept while MMP runs.
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
    public static async Task<string?> FindAsync(string key, string title, string artist, byte[] print)
    {
        string picture = Hash(print);
        if (TryCached(key, picture, out var known)) return known;
        try
        {
            string? url = await FromDeezer(title, artist, print).ConfigureAwait(false)
                       ?? await FromApple(title, artist, print).ConfigureAwait(false);
            Remember(key, picture, url);
            return url;
        }
        catch { return null; }
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

    static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace('ı', 'i');
    }

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
