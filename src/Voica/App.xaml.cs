using System;
using System.Threading;
using System.Windows;
using Voica.UI;

namespace Voica;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIconController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a named mutex keeps only one Voica running per user session.
        _singleInstance = new Mutex(initiallyOwned: true, name: "Voica.SingleInstance.Mutex", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Paths.EnsureCreated();

        // Audio retention cleanup on launch (spec §8).
        Retention.RunOnLaunch();

        _tray = new TrayIconController();
        _tray.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
