using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TaskbarPlayerProto;

sealed class GalleryError(string code, string? similarId = null, string? similarName = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? SimilarId { get; } = similarId;       // use_update: the published item to update instead
    public string? SimilarName { get; } = similarName;
}

sealed class GalleryFile
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Ext { get; set; } = "";
}

sealed class GalleryItem
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Visibility { get; set; } = "";
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public bool Liked { get; set; }
    public bool Featured { get; set; }
    public bool DevLiked { get; set; }
    public bool MakerIsDeveloper { get; set; }
    public bool Mine { get; set; }
    public JsonElement Spec { get; set; }
    public List<GalleryFile> Images { get; set; } = new();
}

sealed class GalleryUser
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Banned { get; set; }
    public long CreatedAt { get; set; }
    public int Items { get; set; }
}

sealed class GalleryReport
{
    public string Id { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Note { get; set; } = "";
    public long At { get; set; }
    public string Reporter { get; set; } = "";
    public GalleryItem Item { get; set; } = new();
}

sealed class GalleryReports
{
    public int Players { get; set; }
    public int Sounds { get; set; }
    public List<GalleryReport> Reports { get; set; } = new();
}

sealed class GalleryPage
{
    public List<GalleryItem> Items { get; set; } = new();
    public int Pages { get; set; } = 1;
    public int Total { get; set; }
    public bool ReportReviewed { get; set; }
    public bool IsAdmin { get; set; }
}

// The app's side of the gallery server. The account's secret stays on this PC, encrypted for this Windows user.
// Nothing here trusts a name or path from the server: it gives numbers and hashes, and files get names made here.
static class GalleryClient
{
    const string Server = "https://mmp-gallery.mmp-gallery-server.workers.dev";
    const long MaxPreviewBytes = 5 * 1024 * 1024;
    static readonly TimeSpan AnswerTime = TimeSpan.FromSeconds(30);     // everything but the files, which take their time

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP");
    static readonly string AccountFile = Path.Combine(Dir, "gallery.dat");
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    static string? _token;
    public static string? UserName { get; private set; }
    public static bool IsAdmin { get; private set; }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };      // a 25 MB picture on a slow line
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MMP/{Updater.Current} (https://github.com/weojiro/mey-music-player)");
        return client;
    }

    // ---------- account ----------

    public static bool HasAccount()
    {
        if (_token != null) return true;
        try
        {
            if (!File.Exists(AccountFile)) return false;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(AccountFile), null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            _token = doc.RootElement.GetProperty("token").GetString();
            UserName = doc.RootElement.GetProperty("name").GetString();
            return _token != null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("gallery account", ex);
            return false;
        }
    }

    static void SaveAccount(string token, string name)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { token, name });
        Directory.CreateDirectory(Dir);
        string tmp = AccountFile + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        File.Move(tmp, AccountFile, true);
        _token = token;
        UserName = name;
    }

    // Stands for this PC without giving Windows' own id away
    static string PcId()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        string id = key?.GetValue("MachineGuid") as string ?? throw new GalleryError("server_error");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mmp-gallery|" + id)));
    }

    public static async Task Register(string name)
    {
        using var doc = await SendJson(HttpMethod.Post, "/v1/register", new { name, pcId = PcId() }, false);
        SaveAccount(doc.RootElement.GetProperty("token").GetString() ?? throw new GalleryError("server_error"),
            doc.RootElement.GetProperty("name").GetString() ?? name);
    }

    public static async Task RefreshName()
    {
        using var doc = await SendJson(HttpMethod.Get, "/v1/me");
        string? name = doc.RootElement.GetProperty("name").GetString();
        IsAdmin = doc.RootElement.TryGetProperty("role", out var role) && role.GetString() == "admin";
        if (name != null && name != UserName && _token != null) SaveAccount(_token, name);
    }

    // ---------- requests ----------

    static HttpRequestMessage Request(HttpMethod method, string path, bool signed = true)
    {
        var request = new HttpRequestMessage(method, Server + path);
        if (signed) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancel = default)
    {
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel); }
        catch (HttpRequestException) { throw new GalleryError("network"); }
        catch (OperationCanceledException) { throw new GalleryError("network"); }
        if (response.IsSuccessStatusCode) return response;
        string code = "server_error";
        string? similarId = null, similarName = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            code = doc.RootElement.GetProperty("error").GetString() ?? code;
            if (doc.RootElement.TryGetProperty("similarId", out var id)) similarId = id.GetString();
            if (doc.RootElement.TryGetProperty("similarName", out var name)) similarName = name.GetString();
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }       // an answer that isn't an object
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
        response.Dispose();
        throw new GalleryError(code, similarId, similarName);
    }

    static async Task<JsonDocument> SendJson(HttpMethod method, string path, object? body = null, bool signed = true)
    {
        using var limit = new CancellationTokenSource(AnswerTime);
        using var request = Request(method, path, signed);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await Send(request, limit.Token);
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token)); }
        catch (JsonException) { throw new GalleryError("server_error"); }
        catch (HttpRequestException) { throw new GalleryError("network"); }
        catch (OperationCanceledException) { throw new GalleryError("network"); }
    }

    public static async Task<List<GalleryItem>> Mine()
    {
        using var doc = await SendJson(HttpMethod.Get, "/v1/mine");
        return doc.RootElement.GetProperty("items").Deserialize<List<GalleryItem>>(Json) ?? new();
    }

    public static async Task<GalleryItem> ByCode(string code)
    {
        using var doc = await SendJson(HttpMethod.Get, "/v1/code/" + Uri.EscapeDataString(code));
        return doc.RootElement.Deserialize<GalleryItem>(Json) ?? throw new GalleryError("server_error");
    }

    public static async Task<GalleryPage> Gallery(string type, string sort, IEnumerable<string> show, IEnumerable<string> colors, int page)
    {
        string path = $"/v1/gallery?type={type}&sort={sort}&page={page}&show={string.Join(',', show)}&colors={string.Join(',', colors)}";
        using var doc = await SendJson(HttpMethod.Get, path);
        return doc.RootElement.Deserialize<GalleryPage>(Json) ?? new();
    }

    public static async Task SetLike(GalleryItem item, bool on)
    {
        using var doc = await SendJson(HttpMethod.Post, $"/v1/items/{item.Id}/like", new { on });
        item.Likes = doc.RootElement.GetProperty("likes").GetInt32();
        item.Liked = on;
        item.DevLiked = doc.RootElement.GetProperty("devLiked").GetBoolean();
    }

    public static async Task Moderate(string id, string action) =>
        (await SendJson(HttpMethod.Post, $"/v1/items/{id}/moderate", new { action })).Dispose();

    public static async Task SetFeatured(string id, bool on) => (await SendJson(HttpMethod.Post, $"/v1/items/{id}/feature", new { on })).Dispose();

    public static async Task Report(string id, string reason, string note) =>
        (await SendJson(HttpMethod.Post, $"/v1/items/{id}/report", new { reason, note })).Dispose();

    public static async Task<List<GalleryUser>> Users()
    {
        using var doc = await SendJson(HttpMethod.Get, "/v1/users");
        return doc.RootElement.GetProperty("users").Deserialize<List<GalleryUser>>(Json) ?? new();
    }

    public static async Task<GalleryReports> Reports(string type)
    {
        using var doc = await SendJson(HttpMethod.Get, "/v1/reports?type=" + type);
        return doc.RootElement.Deserialize<GalleryReports>(Json) ?? new();
    }

    public static async Task Decide(string reportId, string action) =>
        (await SendJson(HttpMethod.Post, "/v1/reports/" + reportId, new { action })).Dispose();

    // Which of these downloads the owner of MMP took down
    public static async Task<List<string>> TakenDown(List<string> ids)
    {
        using var doc = await SendJson(HttpMethod.Post, "/v1/removed", new { ids = ids.Take(30) });
        return doc.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
    }

    // The text for a code the server (or this client) gave
    public static string ErrorKey(string code) => code switch
    {
        "network" => "galErrNet",
        "name_length" or "name_invalid" or "item_name_length" or "item_name_invalid" => "galErrName",
        "name_not_allowed" => "galErrNameBad",
        "item_name_not_allowed" => "galErrItemBad",
        "name_taken" => "galErrNameTaken",
        "pc_has_account" or "too_many_accounts" => "galErrPc",
        "banned" => "galErrBanned",
        "not_found" => "galErrNotFound",
        "use_update" => "galErrSimilar",
        "copy_exists" => "galErrCopy",
        "item_limit" => "galErrLimit",
        "daily_limit" or "ip_daily_limit" => "galErrDaily",
        "storage_full" or "server_busy" => "galErrFull",
        "bad_image" or "wrong_size" or "wrong_file" or "bad_preview" or "too_many_images" or "bad_spec" => "galErrFile",
        _ => "galErrServer",
    };

    public static async Task Delete(string id) => (await SendJson(HttpMethod.Delete, "/v1/items/" + id)).Dispose();

    public static async Task CountDownload(string id) => (await SendJson(HttpMethod.Post, $"/v1/items/{id}/download")).Dispose();

    public static async Task<byte[]?> Preview(string id)
    {
        try
        {
            using var limit = new CancellationTokenSource(AnswerTime);
            using var request = Request(HttpMethod.Get, $"/v1/items/{id}/preview");
            using var response = await Send(request, limit.Token);
            if (response.Content.Headers.ContentLength > MaxPreviewBytes) return null;
            return await response.Content.ReadAsByteArrayAsync(limit.Token);
        }
        // a design without a picture still shows its name
        catch (GalleryError) { return null; }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    // ---------- publishing ----------

    static string? ExtOf(byte[] head)
    {
        if (head.Length < 4) return null;
        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return "png";
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return "gif";
        return null;
    }

    static async Task<GalleryFile> Describe(string path)
    {
        return await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4];
            stream.ReadExactly(head);
            stream.Position = 0;
            return new GalleryFile
            {
                Size = stream.Length,
                Ext = ExtOf(head) ?? throw new GalleryError("bad_image"),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
            };
        });
    }

    static async Task Put(string id, string part, Stream body, long size)
    {
        using var request = Request(HttpMethod.Put, $"/v1/items/{id}/{part}");
        request.Content = new StreamContent(body);
        request.Content.Headers.ContentLength = size;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        (await Send(request)).Dispose();
    }

    public static async Task<string> PublishPlayer(PlayerSpec p, string name, byte[] preview, bool isPrivate, Action<int> progress, string? replaces = null)
    {
        // each picture once, even when the player shows it twice; only these go to the server. Hidden pictures stay
        // on this PC: who saves the design never gets them
        var shown = p.Stickers.Where(s => !s.Hidden).ToList();
        var files = shown.Select(s => s.File).Distinct().ToList();
        var paths = files.Select(Designs.ImagePath).ToList();
        // the cover picture goes up as small as it is ever shown, so the gallery can show it before anyone saves
        string? coverPath = p.ShownCover is { } cover ? await Task.Run(() => SmallCover(cover)) : null;
        if (coverPath != null) paths.Add(coverPath);
        try
        {
            var described = new List<GalleryFile>();
            foreach (string path in paths) described.Add(await Describe(path));

            var body = new
            {
                type = "player",
                replaces,
                name,
                visibility = isPrivate ? "private" : "public",
                images = described,
                preview = new GalleryFile { Size = preview.Length, Ext = ExtOf(preview) ?? "png", Sha256 = Convert.ToHexStringLower(SHA256.HashData(preview)) },
                spec = new
                {
                    bg = p.Bg,
                    buttons = p.Buttons,
                    bgClear = p.BgClear,
                    buttonsClear = p.ButtonsClear,
                    coverClear = p.CoverClear,
                    clearApart = p.ClearApart,
                    stickers = shown.Select(s => new { image = files.IndexOf(s.File), x = s.X, y = s.Y, w = s.W, h = s.H, angle = s.Angle }),
                    cover = coverPath == null ? (int?)null : paths.Count - 1,
                },
            };
            string id = await Create(body);
            for (int i = 0; i < paths.Count; i++)
            {
                progress(100 * i / (paths.Count + 1));
                await using var stream = File.OpenRead(paths[i]);
                await Put(id, "images/" + i, stream, described[i].Size);
            }
            progress(100 * paths.Count / (paths.Count + 1));
            await using (var stream = new MemoryStream(preview)) await Put(id, "preview", stream, preview.Length);
            return await Finish(id);
        }
        finally
        {
            if (coverPath != null) try { File.Delete(coverPath); } catch { }
        }
    }

    public const long MaxCoverUpload = 1024 * 1024;

    // The cover picture at the size MMP shows it, as PNG (JPG for photos), in a temporary file
    static string SmallCover(string file)
    {
        var picture = Designs.LoadCover(file) ?? throw new GalleryError("bad_image");
        bool photo = Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg";
        BitmapEncoder encoder = photo ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        string path = Path.Combine(Path.GetTempPath(), "mmp-cover-" + Guid.NewGuid().ToString("N") + (photo ? ".jpg" : ".png"));
        using (var stream = File.Create(path)) encoder.Save(stream);
        if (new FileInfo(path).Length > MaxCoverUpload) { File.Delete(path); throw new GalleryError("bad_image"); }
        return path;
    }

    // A design's cover picture for its gallery card: small, checked against the hash the server listed
    public static async Task<BitmapSource?> CoverPicture(GalleryItem item, int index)
    {
        try
        {
            var file = item.Images[index];
            if (file.Ext is not ("png" or "jpg") || file.Size <= 0 || file.Size > MaxCoverUpload) return null;
            using var limit = new CancellationTokenSource(AnswerTime);
            using var request = Request(HttpMethod.Get, $"/v1/items/{item.Id}/images/{index}");
            using var response = await Send(request, limit.Token);
            if (response.Content.Headers.ContentLength > file.Size) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token);
            if (bytes.Length != file.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256) return null;
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.DecodePixelWidth = Designs.CoverSide;
                picture.StreamSource = stream;
                picture.EndInit();
                picture.Freeze();
                return (BitmapSource)picture;
            });
        }
        catch { return null; }
    }

    public static async Task<string> PublishEq(SavedEq s, string name, bool isPrivate, string? replaces = null)
    {
        string id = await Create(new
        {
            type = "eq",
            replaces,
            name,
            visibility = isPrivate ? "private" : "public",
            spec = new { bands = s.Bands, preamp = s.Preamp, bass = s.Bass, width = s.Width, balance = s.Balance, vocals = s.Vocals, reverb = s.Reverb, enhancer = s.Enhancer },
        });
        return await Finish(id);
    }

    static async Task<string> Create(object body)
    {
        using var doc = await SendJson(HttpMethod.Post, "/v1/items", body);
        return doc.RootElement.GetProperty("id").GetString() ?? throw new GalleryError("server_error");
    }

    // The code stays the same when an item is updated
    static async Task<string> Finish(string id)
    {
        using var doc = await SendJson(HttpMethod.Post, $"/v1/items/{id}/publish");
        return doc.RootElement.GetProperty("code").GetString() ?? throw new GalleryError("server_error");
    }

    // ---------- downloading ----------

    // A player's pictures into a folder of their own, checked against the hashes the server listed, each one opened
    // once so a broken or oversized picture never reaches a player. The caller moves them in and removes the folder.
    public static async Task<(string Folder, List<string> Paths)> DownloadImages(GalleryItem item, Action<int> progress)
    {
        string folder = Path.Combine(Dir, "gallery-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        // a line that stops answering ends in a message, not in a gallery waiting for ever
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            for (int i = 0; i < item.Images.Count; i++)
            {
                progress(100 * i / Math.Max(1, item.Images.Count));
                var file = item.Images[i];
                string ext = file.Ext is "png" or "jpg" or "gif" ? file.Ext : throw new GalleryError("bad_image");
                if (file.Size <= 0 || file.Size > Designs.MaxFileBytes) throw new GalleryError("bad_image");
                string path = Path.Combine(folder, i + "." + ext);

                using var request = Request(HttpMethod.Get, $"/v1/items/{item.Id}/images/{i}");
                using var response = await Send(request, limit.Token);
                await using (var source = await response.Content.ReadAsStreamAsync(limit.Token))
                await using (var target = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
                    {
                        total += read;
                        if (total > file.Size) throw new GalleryError("bad_image");
                        await target.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
                string hash = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Convert.ToHexStringLower(SHA256.HashData(stream));
                });
                if (hash != file.Sha256 || await Stickers.LoadAsync(path, 64, 3_000_000) == null) throw new GalleryError("bad_image");
                paths.Add(path);
            }
            return (folder, paths);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or HttpIOException)
        {
            RemoveFolder(folder);
            throw new GalleryError("network");
        }
        catch
        {
            RemoveFolder(folder);
            throw;
        }
    }

    public static void RemoveFolder(string folder)
    {
        try { Directory.Delete(folder, true); } catch { }
    }
}
