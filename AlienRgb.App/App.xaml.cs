using System.IO;
using System.Threading;
using System.Windows;
using AlienRgb.Core;

namespace AlienRgb.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;

    // Lives in the per-copy data folder, so a freshly extracted copy (e.g. on another
    // account) always gets the first-run self-install experience.
    private static string FirstRunMarker => Path.Combine(AppContext.BaseDirectory, "data", "firstrun.done");

    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        DiagLog.Write($"App startup, args=[{string.Join(" ", e.Args)}]");

        // Headless mode kept for any old "--apply-last" registrations still out there.
        if (e.Args.Contains("--apply-last"))
        {
            try
            {
                var file = ProfileStore.Load();
                var profile = file.Profiles.FirstOrDefault(p =>
                    p.Name.Equals(file.LastApplied, StringComparison.OrdinalIgnoreCase));
                if (profile is not null)
                {
                    using var dev = AlienFxDevice.Open();
                    ProfileStore.Apply(dev, profile);
                }
            }
            catch
            {
                // best-effort at login; nothing to show
            }
            Shutdown();
            return;
        }

        // One GUI instance per session — an HKCU autostart plus a manual launch (or any
        // leftover HKLM entry) must not produce two tray icons fighting over the device.
        _singleInstanceMutex = new Mutex(true, "AlienRgb.App.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            DiagLog.Write("Another instance is already running in this session; exiting.");
            Shutdown();
            return;
        }

        // First run of this copy: embed into startup for the current account (login entry
        // + sleep/wake restore task, both per-user, no admin). Later runs re-register only
        // if already installed, which self-heals the paths if the folder was moved.
        bool firstRun = !File.Exists(FirstRunMarker);
        try
        {
            if (firstRun || StartupInstaller.IsInstalled())
                StartupInstaller.Install();
            if (firstRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FirstRunMarker)!);
                File.WriteAllText(FirstRunMarker, DateTime.Now.ToString("o"));
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Startup self-registration failed: {ex.Message}");
        }

        var window = new MainWindow();
        if (firstRun || e.Args.Contains("--minimized"))
            window.StartMinimizedToTray(announce: firstRun);
        else
            window.Show();
    }
}
