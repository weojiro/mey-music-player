using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarPlayerProto;

// Synced lyrics from LRCLIB (lrclib.net). Spotify only; for now only shown in the debug window.
static class Lyrics
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MMP/{Updater.Current} (https://github.com/weojiro/mey-music-player)");
        return client;
    }

    public sealed record Result(List<(TimeSpan Time, string Text)>? Lines, string Status);

    // Exact lookup first, then a search. A result only counts when its length is within 2 s of the song:
    // otherwise the timings belong to another version (remix, live, radio edit).
    public static async Task<Result> FetchAsync(string title, string artist, string album, TimeSpan duration)
    {
        string reason = "not found";
        string get = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
            + "&artist_name=" + Uri.EscapeDataString(artist)
            + "&album_name=" + Uri.EscapeDataString(album)
            + "&duration=" + (int)Math.Round(duration.TotalSeconds);
        using (var response = await Http.GetAsync(get))
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var r = Pick(doc.RootElement, duration, "exact");
                if (r.Lines != null) return r;
                reason = r.Status;
            }
        }

        // the album name or a list of artists can make the exact lookup miss
        string firstArtist = artist.Split(',', '&')[0].Trim();
        string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
            + "&artist_name=" + Uri.EscapeDataString(firstArtist);
        using var results = JsonDocument.Parse(await Http.GetStringAsync(search));
        foreach (var item in results.RootElement.EnumerateArray())
        {
            var r = Pick(item, duration, "search");
            if (r.Lines != null) return r;
            if (reason == "not found") reason = r.Status;
        }
        return new(null, reason);
    }

    static Result Pick(JsonElement item, TimeSpan duration, string how)
    {
        double theirs = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        if (Math.Abs(theirs - duration.TotalSeconds) > 2)
            return new(null, $"length differs (lrclib {theirs:0} s, song {duration.TotalSeconds:0} s)");

        string synced = item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        if (synced.Length == 0)
        {
            bool instrumental = item.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
            bool plain = item.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String;
            return new(null, instrumental ? "instrumental" : plain ? "only unsynced lyrics" : "no lyrics");
        }
        var lines = Parse(synced);
        return lines.Count > 0 ? new(lines, $"found ({how}, {lines.Count} lines)") : new(null, "synced lyrics are empty");
    }

    // "[01:23.45] text"; one line can carry several timestamps
    static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:[.:]\d+)?)\]");

    static List<(TimeSpan Time, string Text)> Parse(string lrc)
    {
        var lines = new List<(TimeSpan Time, string Text)>();
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var stamps = Stamp.Matches(raw);
            if (stamps.Count == 0) continue;
            var last = stamps[stamps.Count - 1];
            string text = raw[(last.Index + last.Length)..].Trim();
            foreach (Match m in stamps)
            {
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add((TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(sec), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    // index of the line being sung at pos, -1 before the first line
    public static int LineAt(List<(TimeSpan Time, string Text)> lines, TimeSpan pos)
    {
        int i = -1;
        while (i + 1 < lines.Count && lines[i + 1].Time <= pos) i++;
        return i;
    }
}
