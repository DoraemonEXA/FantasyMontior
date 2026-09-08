using static FantasyMontior.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace FantasyMontior;

public partial class App : Application
{
    private WidgetApplication? _widgetApplication;
    private const string ElevationAttempt = "--elevation-attempted";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = new ViewModels.SettingsViewModel();
        Localization.Apply(settings.Language);
        // Do not construct MainWindow (or its monitoring service) in the launcher process.
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (e.Args.Contains(ElevationAttempt, StringComparer.Ordinal))
                    throw new InvalidOperationException(T("Windows did not grant administrator access. CPU temperature monitoring needs this access; please check your account permissions."));

                // Target our apphost explicitly, including when started through dotnet/Rider.
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FantasyMontior.exe"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                };
                foreach (var argument in e.Args) start.ArgumentList.Add(argument);
                start.ArgumentList.Add(ElevationAttempt);
                using var child = Process.Start(start)
                    ?? throw new InvalidOperationException(T("Windows could not start the administrator instance."));
                Shutdown();
                return;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Cancelling UAC is a normal user decision; do not retry or open hardware.
            Shutdown();
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(F("FantasyMontior needs administrator access to read CPU temperatures through PawnIO.\n\n{0}", ex.Message),
                T("Could not start sensor monitoring"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _widgetApplication = new WidgetApplication(new MonitoringSession(settings));
        _widgetApplication.Start();
    }
}
