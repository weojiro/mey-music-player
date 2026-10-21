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

        // clean up the exe that the last update moved aside
        try { File.Delete((Environment.ProcessPath ?? "") + ".old"); } catch { }
        RefreshLauncher();

        base.OnStartup(e);
    }

    // Launchers before v1.0.1 checked for the .NET 8 runtime before starting MMP.exe (desktop shortcut, Start with
    // Windows). MMP.exe has the runtime inside now: the installed .bat just starts it. Same text as launcher/MMP Launcher.bat.
    const string LauncherBat = """
        @echo off
        setlocal EnableExtensions

        if /i "%~1"=="/run" goto run

        rem Install / repair: everything is done by launcher.ps1 (English console text, colors, progress bars)
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1"
        exit /b

        :run
        rem Normal start (desktop shortcut / Start with Windows). MMP.exe has the .NET runtime inside.
        start "" "%~dp0MMP.exe"
        exit /b 0

        """;

    static void RefreshLauncher()
    {
        try
        {
            string bat = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "MMP Launcher.bat");
            if (File.Exists(bat) && File.ReadAllText(bat).Contains("WindowsDesktop.App"))
                File.WriteAllText(bat, LauncherBat.ReplaceLineEndings("\r\n"));
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
