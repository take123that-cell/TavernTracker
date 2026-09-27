using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TavernTracker.Core;
using TavernTracker.Core.Hearthstone;

namespace TavernTracker.App;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        // Only one copy at a time: two would fight over the same data files.
        using var single = new Mutex(true, "TavernTracker.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Tavern Tracker is already running.", "Tavern Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Background task error", e.Exception); e.SetObserved(); };

        var settings = AppSettings.Load();
        // Memory reading (rating, lobby names, tribes) is optional and can be turned off in Settings.
        IGameMemory? memory = settings.MemoryReading ? new TavernTracker.Memory.HearthstoneMemory() : null;
        using var engine = new TrackerEngine(settings, memory, new TavernTracker.App.Combat.V8CombatSimulator());
        engine.Start();
        Log.Info("Tavern Tracker started");

        var overlay = new OverlayWindow(engine);
        var main = new MainWindow(engine, overlay);
        main.Closed += (_, _) => overlay.Close();
        return app.Run(main);
    }

    private static void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        // Keep running: a broken redraw shouldn't take the whole app down.
        e.Handled = true;
    }
}
