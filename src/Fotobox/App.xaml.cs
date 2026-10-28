using System.Windows;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Audio;
using Fotobox.Services.Camera;
using Fotobox.Services.Detection;
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

        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            // Ohne Eintrag bliebe ein Absturz hier unbemerkt.
            AppLog.Initialize();
            AppLog.Error("Die Einzelinstanz-Sperre konnte nicht angelegt werden.", ex);
            try
            {
                MessageBox.Show(
                    AppInfo.DisplayName + " konnte nicht gestartet werden." + Environment.NewLine + Environment.NewLine +
                    "Einzelheiten stehen im Protokoll im Ordner %LOCALAPPDATA%\\Fotobox\\logs.",
                    AppInfo.DisplayName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception msgEx)
            {
                AppLog.Error("Fehlermeldung konnte nicht angezeigt werden.", msgEx);
            }
            Shutdown(1);
            return;
        }

        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                AppInfo.DisplayName + " läuft bereits.",
                AppInfo.DisplayName,
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
                    AppInfo.DisplayName + " konnte nicht gestartet werden." + Environment.NewLine + Environment.NewLine +
                    "Einzelheiten stehen im Protokoll im Ordner %LOCALAPPDATA%\\Fotobox\\logs." + Environment.NewLine +
                    "Prüfen Sie dort bzw. im Ordner %LOCALAPPDATA%, ob Dateien oder Rechte den Start blockieren.",
                    AppInfo.DisplayName,
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
        RegisterServices(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Alle Registrierungen (getrennt vom Container, damit ein Test sie mit Ersatzdiensten überschreiben kann).</summary>
    internal static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore());
        services.AddSingleton<IPinService, PinService>();
        services.AddSingleton<ICameraService, WinRtCameraService>();
        services.AddSingleton<ILayoutComposer, LayoutComposer>();
        services.AddSingleton<IPrintService, PrintService>();
        services.AddSingleton<IPhotoStorage, PhotoStorage>();
        services.AddSingleton<IVideoStorage>(_ => new VideoStorage());
        services.AddSingleton<IDisplayService, Win32DisplayService>();
        services.AddSingleton<IPersonDetectionService>(sp => new PersonDetectionService(
            sp.GetRequiredService<ICameraService>(),
            kind => kind == DetectionKind.Body ? new YoloxPersonDetector() : new WindowsFaceDetector(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IFunSoundPlayer, FunSoundPlayer>();
        // Transient: Gastansicht und Admin-Vorschau brauchen je eine eigene Zielerfassung.
        services.AddTransient<FunOverlayViewModel>();
        services.AddSingleton<IAdminLauncher, AdminLauncher>();

        services.AddSingleton<BoothController>();
        services.AddSingleton<BoothViewModel>();
        services.AddSingleton<BoothWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Zweite Instanz (Mutex belegt): nichts gestartet, nichts zu beenden und nichts zu loggen.
        if (!_ownsMutex)
        {
            ReleaseMutex();
            base.OnExit(e);
            return;
        }

        try
        {
            if (_services is { } services)
            {
                try
                {
                    // Gibt nur bereits erzeugte Dienste frei (die Kamera wird nicht neu erzeugt, z. B. nach abgebrochener
                    // PIN-Einrichtung); ihr Dispose stoppt die Kamera. Einmalig mit Frist, auf dem Threadpool, damit kein
                    // UI-Thread-Deadlock entsteht.
                    if (!Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(5)))
                        AppLog.Warn("Kamera konnte nicht rechtzeitig freigegeben werden.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Fehler beim Freigeben der Kamera.", ex);
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
            ReleaseMutex();
        }

        base.OnExit(e);
    }

    private void ReleaseMutex()
    {
        if (_singleInstanceMutex is null)
            return;

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
