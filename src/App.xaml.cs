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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
