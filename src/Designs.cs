using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TaskbarPlayerProto;

// A picture on the player. Position (center) and size are in design units of the 338 x 40 player panel.
sealed class StickerSpec
{
    public string File { get; set; } = "";
    public string? Name { get; set; }       // the picked file's name, shown in the layer list
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Angle { get; set; }
}

// A saved player: two colours (null = chameleon, taken from the cover) and its pictures, bottom to top.
sealed class PlayerSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Bg { get; set; }
    public string? Buttons { get; set; }
    public List<StickerSpec> Stickers { get; set; } = new();

    // transparency in percent (0 = solid, 100 = cannot be seen); the background one also covers the pictures and the title
    // card, the buttons one the time, the lyrics and the song's name
    public int BgClear { get; set; }
    public int ButtonsClear { get; set; }
    public int CoverClear { get; set; }
    public bool ClearApart { get; set; }        // false: one slider sets all three

    public PlacementSpec? Placement { get; set; }  // where this player was left; null = not used yet
}

// Where a player sits and how it behaves; every player comes back the way it was left
sealed record PlacementSpec
{
    public string Side { get; init; } = "Left";
    public int? UserX { get; init; }            // spot on the taskbar
    public bool Free { get; init; }
    public double? FreeFx { get; init; }        // spot in free move, a share of the screen
    public double? FreeFy { get; init; }
    public string FreeMon { get; init; } = "";
    public bool OnTop { get; init; }            // show over games
    public int Size { get; init; } = 100;       // player size in free move, %
}

// "Design your player": the saved players live in %LOCALAPPDATA%\MMP\players, which updates never touch.
sealed class Designs
{
    public const int MaxPlayers = 5;
    public const int MaxStickers = 10;
    public const long MaxFileBytes = 25 * 1024 * 1024;
    // up to 100 % see-through; even then the player keeps a trace too faint to see, so it can still be clicked
    public const int MaxClear = 100;
    public const int MaxButtonsClear = 90;      // the buttons always stay a little visible

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP", "players");
    static readonly string ImagesDir = Path.Combine(Dir, "images");
    static readonly string FilePath = Path.Combine(Dir, "players.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? Active { get; set; }                 // id of the player in use; null = the default player
    public List<PlayerSpec> Players { get; set; } = new();
    public PlacementSpec? DefaultPlacement { get; set; }  // the default player's, like PlayerSpec.Placement

    [JsonIgnore] public PlayerSpec? Current => Players.FirstOrDefault(p => p.Id == Active);

    public static Designs Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Designs>(File.ReadAllText(FilePath), Json) ?? new Designs();
                d.Players = (d.Players ?? new()).Where(p => p != null).Take(MaxPlayers).ToList();
                foreach (var p in d.Players)
                {
                    p.Name ??= "";
                    p.BgClear = Math.Clamp(p.BgClear, 0, MaxClear);         // a hand-edited file can't hide the buttons
                    p.ButtonsClear = Math.Clamp(p.ButtonsClear, 0, MaxButtonsClear);
                    p.CoverClear = Math.Clamp(p.CoverClear, 0, MaxClear);
                    p.Stickers = (p.Stickers ?? new()).Where(s => s != null && File.Exists(ImagePath(s.File ?? ""))).Take(MaxStickers).ToList();
                }
                if (d.Current == null) d.Active = null;
                return d;
            }
        }
        catch (Exception ex)
        {
            // a broken file is kept aside and its pictures are not cleaned up, so nothing is lost for good
            ErrorLog.Write("players load", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            return new Designs { _keepImages = true };
        }
        return new Designs();
    }

    bool _keepImages;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static string ImagePath(string file) => Path.Combine(ImagesDir, file);

    // Copies a picked picture next to the players, so the original can be moved or deleted
    public static string Import(string source)
    {
        Directory.CreateDirectory(ImagesDir);
        string name = Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        File.Copy(source, ImagePath(name));
        return name;
    }

    // Deletes pictures that no player uses any more
    public void CleanImages()
    {
        try
        {
            if (_keepImages || !Directory.Exists(ImagesDir)) return;
            var used = Players.SelectMany(p => p.Stickers).Select(s => s.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(ImagesDir))
                // a picture copied a moment ago may still be loading before it joins a player
                if (!used.Contains(Path.GetFileName(f)) && DateTime.UtcNow - File.GetCreationTimeUtc(f) > TimeSpan.FromMinutes(1))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
