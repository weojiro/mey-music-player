using System.IO;
using System.Windows;

namespace TaskbarPlayerProto;

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // unexpected errors are written to error.log; a UI error does not take the player off the taskbar
        DispatcherUnhandledException += (_, a) => { ErrorLog.Write("ui", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) ErrorLog.Write("crash", ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { ErrorLog.Write("task", a.Exception); a.SetObserved(); };

        // Only one copy at a time. After an update the old copy is still shutting down: wait for it.
        bool afterUpdate = e.Args.Contains("--after-update");
        _single = new Mutex(true, @"Local\MMP-single-instance", out bool createdNew);
        if (!createdNew)
        {
            bool got;
            try { got = afterUpdate && _single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { got = true; }
            if (!got)
            {
                Shutdown();
                return;
            }
        }

        Task.Run(DeleteOldExe);
        RefreshLauncher();

        base.OnStartup(e);
    }

    // The exe that the last update moved aside. Right after an update the old copy may still be closing: try again.
    static async Task DeleteOldExe()
    {
        string old = (Environment.ProcessPath ?? "") + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { await Task.Delay(3000); }
        }
    }

    // The launcher installed next to the app (MMP Launcher.bat + launcher.ps1, used by the desktop shortcut, Start with
    // Windows and for repairs) is brought up to the version inside this exe. Launchers before v1.0.1 installed .NET 8.
    static void RefreshLauncher()
    {
        try
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            if (!File.Exists(Path.Combine(dir, "MMP Launcher.bat"))) return;      // not installed by the launcher
            var asm = typeof(App).Assembly;
            foreach (string name in new[] { "MMP Launcher.bat", "launcher.ps1" })
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                byte[] mine = copy.ToArray();
                string path = Path.Combine(dir, name);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(mine))
                    File.WriteAllBytes(path, mine);
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
