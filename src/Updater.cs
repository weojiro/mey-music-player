using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarPlayerProto;

// What the update window needs to know about a newer release.
sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0, 0);
    public string Notes { get; init; } = "";
    public string ExeUrl { get; init; } = "";
    public string GzUrl { get; init; } = "";        // the same exe gzipped (from v1.0.1), "" if the release has none
    public string HashUrl { get; init; } = "";
}

// Looks for a newer release on GitHub and swaps the exe in place.
static class Updater
{
    const string Repo = "weojiro/mey-music-player";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MMP-updater");      // GitHub requires a user agent
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static Version Current =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    // The newest release if it is newer than this exe and its files are attached, otherwise null.
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var stream = await Http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Normalize(latest);
        if (latest <= Current) return null;

        string exeUrl = "", gzUrl = "", hashUrl = "";
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name == "MMP.exe") exeUrl = url;
                else if (name == "MMP.exe.gz") gzUrl = url;
                else if (name == "MMP.exe.sha256") hashUrl = url;
            }
        }
        if (exeUrl == "" || hashUrl == "") return null;     // the build is still being attached to the release

        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo { Version = latest, Notes = notes, ExeUrl = exeUrl, GzUrl = gzUrl, HashUrl = hashUrl };
    }

    // Downloads the new exe next to the running one (the gzipped copy if there is one: less than half the size),
    // checks its SHA-256, swaps it in and starts it.
    public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<double> progress)
    {
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("exe path unknown");
        string newPath = exePath + ".new";
        string oldPath = exePath + ".old";

        string expected = (await Http.GetStringAsync(info.HashUrl))
            .Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();

        bool gz = info.GzUrl != "";
        using (var response = await Http.GetAsync(gz ? info.GzUrl : info.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            using var download = await response.Content.ReadAsStreamAsync();
            using var counted = new CountingStream(download, done => { if (total > 0) progress.Report((double)done / total); });
            using var source = gz ? new GZipStream(counted, CompressionMode.Decompress) : (Stream)counted;
            using var target = File.Create(newPath);
            await source.CopyToAsync(target).ConfigureAwait(false);     // unzipping stays off the UI thread
        }

        string actual;
        using (var file = File.OpenRead(newPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(newPath);
            throw new InvalidDataException("SHA-256 mismatch");
        }

        // A running exe can be renamed but not overwritten: move it aside, put the new one in its place.
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(exePath, oldPath);
        try { File.Move(newPath, exePath); }
        catch
        {
            File.Move(oldPath, exePath);     // put the old one back, nothing is lost
            throw;
        }

        Process.Start(new ProcessStartInfo(exePath, "--after-update")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!
        });
    }

    // Release notes can have one section per language: "## en", "## tr", ... or "## English", "## Türkçe".
    // Picks the user's language, then English, then the whole text.
    public static string PickNotes(string body, string lang)
    {
        var lines = body.Replace("\r", "").Split('\n');
        var sections = new Dictionary<string, StringBuilder>();
        StringBuilder? current = null;
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^##\s*(\S.*?)\s*$");
            if (m.Success && LanguageCode(m.Groups[1].Value) is string code)
            {
                current = sections[code] = new StringBuilder();
                continue;
            }
            current?.AppendLine(Bullet(line));
        }

        string text = sections.TryGetValue(lang, out var mine) ? mine.ToString()
            : sections.TryGetValue("en", out var english) ? english.ToString()
            : string.Join("\n", lines.Select(Bullet));
        return text.Trim();
    }

    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en", ["Türkçe"] = "tr", ["Turkish"] = "tr", ["Español"] = "es", ["Français"] = "fr",
        ["Deutsch"] = "de", ["Português"] = "pt", ["Русский"] = "ru", ["中文"] = "zh", ["简体中文"] = "zh",
        ["日本語"] = "ja", ["العربية"] = "ar", ["हिन्दी"] = "hi"
    };

    static string? LanguageCode(string heading) =>
        Regex.IsMatch(heading, "^[a-z]{2}$") ? heading : LanguageNames.TryGetValue(heading, out var code) ? code : null;

    // Reports how many bytes were read from the download (the gzipped size, which is what the bar shows).
    sealed class CountingStream(Stream inner, Action<long> report) : Stream
    {
        long _done;

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            Count(await inner.ReadAsync(buffer, token));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        int Count(int n)
        {
            _done += n;
            report(_done);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static string Bullet(string line) => Regex.Replace(line, @"^(\s*)[-*]\s+", "$1• ");
}
