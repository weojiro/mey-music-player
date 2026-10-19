using System.IO;

namespace TaskbarPlayerProto;

// Unexpected errors go to %LOCALAPPDATA%\MMP\error.log. The file only appears once something went wrong,
// stays under ~256 KB (the oldest half is dropped) and skips a repeat of the same error within a minute.
static class ErrorLog
{
    const long MaxBytes = 256 * 1024;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP", "error.log");
    static readonly object Gate = new();
    static string _last = "";
    static DateTime _lastAt;

    public static void Write(string where, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                string key = where + "|" + ex.GetType().FullName + "|" + ex.Message;
                if (key == _last && DateTime.UtcNow - _lastAt < TimeSpan.FromMinutes(1)) return;
                _last = key;
                _lastAt = DateTime.UtcNow;

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    string old = File.ReadAllText(FilePath);
                    int cut = old.IndexOf("\n\n", old.Length / 2, StringComparison.Ordinal);    // keep whole entries
                    File.WriteAllText(FilePath, cut >= 0 ? old[(cut + 2)..] : "");
                }
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{Updater.Current}  Windows build {Environment.OSVersion.Version.Build}  [{where}]\n{ex}\n\n");
            }
        }
        catch { }       // logging must never break the app
    }
}
