using System.Windows;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Services.Camera;
using Fotobox.Services.Display;
using Fotobox.Services.Layout;
using Fotobox.Services.Printing;
using Fotobox.Services.Storage;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Microsoft.Extensions.DependencyInjection;

namespace Fotobox;

/// <summary>Composition Root: Single-Instance, DI-Container, globale Exception-Handler.</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Fotobox-SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "Die Fotobox läuft bereits.",
                "Fotobox",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppLog.Initialize();
        AppLog.Info("Fotobox startet.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _services = BuildServices();

            var adminLauncher = _services.GetRequiredService<IAdminLauncher>();
            if (!adminLauncher.EnsurePinConfigured(null))
            {
                AppLog.Info("PIN-Einrichtung bzw. Laden der Einstellungen abgebrochen – Fotobox wird beendet.");
                Shutdown();
                return;
            }

            var boothWindow = _services.GetRequiredService<BoothWindow>();
            MainWindow = boothWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            boothWindow.Show();
        }
        catch (Exception ex)
        {
            // Ohne diesen Pfad bliebe ein fensterloser Prozess (OnExplicitShutdown) samt Mutex am Leben.
            AppLog.Error("Die Fotobox konnte nicht gestartet werden.", ex);
            try
            {
                MessageBox.Show(
                    "Die Fotobox konnte nicht gestartet werden." + Environment.NewLine + Environment.NewLine +
                    "Einzelheiten stehen im Protokoll im Ordner %LOCALAPPDATA%\\Fotobox\\logs." + Environment.NewLine +
                    "Prüfen Sie dort bzw. im Ordner %LOCALAPPDATA%, ob Dateien oder Rechte den Start blockieren.",
                    "Fotobox",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception msgEx)
            {
                AppLog.Error("Fehlermeldung konnte nicht angezeigt werden.", msgEx);
            }
            Shutdown(1);
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore());
        services.AddSingleton<IPinService, PinService>();
        services.AddSingleton<ICameraService, WinRtCameraService>();
        services.AddSingleton<ILayoutComposer, LayoutComposer>();
        services.AddSingleton<IPrintService, PrintService>();
        services.AddSingleton<IPhotoStorage, PhotoStorage>();
        services.AddSingleton<IDisplayService, Win32DisplayService>();
        services.AddSingleton<IAdminLauncher, AdminLauncher>();

        services.AddSingleton<BoothController>();
        services.AddSingleton<BoothViewModel>();
        services.AddSingleton<BoothWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                var camera = _services.GetService<ICameraService>();
                if (camera is not null)
                {
                    try
                    {
                        // Auf dem Threadpool ausführen, damit kein UI-Thread-Deadlock entsteht.
                        if (!Task.Run(async () => await camera.DisposeAsync()).Wait(TimeSpan.FromSeconds(5)))
                            AppLog.Warn("Kamera konnte nicht rechtzeitig freigegeben werden.");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Fehler beim Freigeben der Kamera.", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Teilweise initialisierter Zustand (z. B. Dienst nicht erzeugbar): Beenden muss trotzdem durchlaufen.
            AppLog.Error("Fehler beim Beenden.", ex);
        }
        finally
        {
            AppLog.Info("Fotobox beendet.");
            AppLog.Flush();

            if (_singleInstanceMutex is not null)
            {
                try
                {
                    if (_ownsMutex)
                        _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex gehört nicht diesem Thread – beim Prozessende ohnehin freigegeben.
                }
                _singleInstanceMutex.Dispose();
            }
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unbehandelte Ausnahme (Dispatcher).", e.Exception);
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error($"Unbehandelte Ausnahme (AppDomain, Terminating={e.IsTerminating}).", e.ExceptionObject as Exception);
        AppLog.Flush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Nicht beobachtete Task-Ausnahme.", e.Exception);
        e.SetObserved();
    }
}
