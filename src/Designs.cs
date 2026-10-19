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
}

// "Design your player": the saved players live in %LOCALAPPDATA%\MMP\players, which updates never touch.
sealed class Designs
{
    public const int MaxPlayers = 5;
    public const int MaxStickers = 10;
    public const long MaxFileBytes = 10 * 1024 * 1024;

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

    [JsonIgnore] public PlayerSpec? Current => Players.FirstOrDefault(p => p.Id == Active);

    public static Designs Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Designs>(File.ReadAllText(FilePath), Json) ?? new Designs();
                d.Players = d.Players.Take(MaxPlayers).ToList();
                foreach (var p in d.Players)
                    p.Stickers = p.Stickers.Where(s => File.Exists(ImagePath(s.File))).Take(MaxStickers).ToList();
                if (d.Current == null) d.Active = null;
                return d;
            }
        }
        catch { }
        return new Designs();
    }

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
            if (!Directory.Exists(ImagesDir)) return;
            var used = Players.SelectMany(p => p.Stickers).Select(s => s.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(ImagesDir))
                if (!used.Contains(Path.GetFileName(f)))
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
