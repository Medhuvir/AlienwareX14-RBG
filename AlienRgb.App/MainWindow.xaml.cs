using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AlienRgb.Core;
using Microsoft.Win32;

namespace AlienRgb.App;

public sealed class ZoneVm : INotifyPropertyChanged
{
    private byte _r, _g, _b;

    public int Id { get; init; }
    public string Name { get; init; } = "";

    public double R { get => _r; set { _r = (byte)value; OnColorChanged(); } }
    public double G { get => _g; set { _g = (byte)value; OnColorChanged(); } }
    public double B { get => _b; set { _b = (byte)value; OnColorChanged(); } }

    public (byte r, byte g, byte b) Rgb => (_r, _g, _b);

    public string Hex
    {
        get => $"#{_r:X2}{_g:X2}{_b:X2}";
        set
        {
            try
            {
                (_r, _g, _b) = ProfileStore.ParseHex(value);
                OnColorChanged();
            }
            catch (FormatException)
            {
                OnPropertyChanged(nameof(Hex)); // revert display
            }
        }
    }

    public System.Windows.Media.Brush PreviewBrush => new SolidColorBrush(Color.FromRgb(_r, _g, _b));

    public event Action? ColorChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRgb(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        OnColorChanged();
    }

    private void OnColorChanged()
    {
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(PreviewBrush));
        ColorChanged?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly List<ZoneVm> _zones = new();
    private readonly DispatcherTimer _applyDebounce;
    private AlienFxDevice? _device;
    private bool _loadingUi;

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;

    public MainWindow()
    {
        InitializeComponent();

        _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _applyDebounce.Tick += (_, _) => { _applyDebounce.Stop(); ApplyToDevice(); };

        foreach (var zone in ZoneMap.Load())
        {
            var vm = new ZoneVm { Id = zone.Id, Name = zone.Name };
            vm.SetRgb(0x31, 0xD2, 0xE0); // default accent cyan
            vm.ColorChanged += OnZoneColorChanged;
            _zones.Add(vm);
        }
        ZoneList.ItemsSource = _zones;

        TryOpenDevice();
        LoadProfilesUi(selectLast: true);
        InitRunAtLoginCheckbox();
        InitTrayIcon();

        // Push the loaded state to the hardware immediately — otherwise a startup launch
        // (e.g. the all-users autostart entry) opens the device but leaves lights untouched
        // until the user manually clicks Apply or nudges a slider.
        if (_device is not null)
            ApplyToDevice();

        DiagLog.Write("MainWindow constructed");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _device?.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    // ----- Sleep/resume -----
    // The AW-ELC controller re-enumerates as a fresh USB HID device on resume and its
    // firmware forgets whatever colors were set, so the app must actively push them back.
    //
    // Microsoft.Win32.SystemEvents.PowerModeChanged is known to be unreliable on laptops
    // using Modern Standby (S0 low-power idle) — Windows can signal resume via
    // PBT_APMRESUMEAUTOMATIC, which SystemEvents doesn't always surface. So this also
    // hooks the raw WM_POWERBROADCAST window message directly as a backup trigger.

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMECRITICAL = 0x0006;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST)
        {
            int code = wParam.ToInt32();
            DiagLog.Write($"WM_POWERBROADCAST wParam=0x{code:X}");
            if (code is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMECRITICAL)
                Dispatcher.BeginInvoke(new Action(() => _ = ReapplyAfterResumeAsync("WM_POWERBROADCAST")));
        }
        return IntPtr.Zero;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        DiagLog.Write($"SystemEvents.PowerModeChanged: {e.Mode}");
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(new Action(() => _ = ReapplyAfterResumeAsync("SystemEvents")));
    }

    private bool _reapplyRunning;

    private async Task ReapplyAfterResumeAsync(string source)
    {
        if (_reapplyRunning)
        {
            DiagLog.Write($"Reapply already in progress, ignoring trigger from {source}");
            return;
        }
        _reapplyRunning = true;
        try
        {
            DiagLog.Write($"Reapply triggered by {source}");

            // The stale handle from before sleep is no longer valid; force a fresh open.
            _device?.Dispose();
            _device = null;

            StatusLabel.Text = "Resumed — reconnecting to AlienFX controller...";

            // Embedded controllers can take a while to re-enumerate after a modern-standby
            // resume; back off gradually rather than giving up after a few seconds.
            int[] delaysMs = { 500, 500, 1000, 1000, 1000, 2000, 2000, 2000, 3000, 3000, 3000, 5000, 5000, 5000, 5000 };
            foreach (var delay in delaysMs)
            {
                await Task.Delay(delay);
                TryOpenDevice();
                if (_device is not null)
                {
                    ApplyToDevice();
                    DiagLog.Write("Reapply succeeded.");
                    return;
                }
            }
            StatusLabel.Text = "Resume: could not reconnect to the AlienFX controller.";
            DiagLog.Write("Reapply gave up after all retries.");
        }
        finally
        {
            _reapplyRunning = false;
        }
    }

    // ----- Minimize to tray -----

    private void InitTrayIcon()
    {
        System.Drawing.Icon icon;
        var resourceInfo = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/alienhead.ico"));
        if (resourceInfo is not null)
        {
            using var stream = resourceInfo.Stream;
            icon = new System.Drawing.Icon(stream);
        }
        else
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open AlienRgb", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _trayIcon!.Visible = false; System.Windows.Application.Current.Shutdown(); });

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "AlienRgb",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            MinimizeToTray(announce: true);
    }

    private void MinimizeToTray(bool announce)
    {
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            if (announce && !_trayBalloonShown)
            {
                _trayIcon.ShowBalloonTip(2000, "AlienRgb", "Still running here — double-click to reopen.",
                    System.Windows.Forms.ToolTipIcon.None);
                _trayBalloonShown = true;
            }
        }
    }

    /// <summary>Used by --minimized/first-run startup: go straight to the tray without ever flashing a window.</summary>
    public void StartMinimizedToTray(bool announce = false)
    {
        WindowState = WindowState.Minimized;
        Show();
        MinimizeToTray(announce);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void TryOpenDevice()
    {
        try
        {
            _device = AlienFxDevice.Open();
            DeviceLabel.Text = $"  —  {_device.Description} (PID {_device.ProductId:X4}, APIv4)";
            DiagLog.Write($"Device opened: {_device.Description} (PID {_device.ProductId:X4})");
        }
        catch (Exception ex)
        {
            _device = null;
            DeviceLabel.Text = "  —  device not available";
            StatusLabel.Text = ex.Message;
            DiagLog.Write($"Device open failed: {ex.Message}");
        }
    }

    private void OnZoneColorChanged()
    {
        if (_loadingUi || LiveApply.IsChecked != true)
            return;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void ApplyToDevice()
    {
        if (_device is null)
        {
            TryOpenDevice();
            if (_device is null)
                return;
        }
        try
        {
            foreach (var group in _zones.GroupBy(z => z.Rgb))
                _device.StageColor(group.Select(z => z.Id).ToList(), group.Key.r, group.Key.g, group.Key.b);
            _device.Update();
            ProfileStore.SaveCurrentState(_zones.ToDictionary(z => z.Id, z => z.Hex.TrimStart('#')));
            StatusLabel.Text = $"Applied {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _device.Dispose();
            _device = null;
            StatusLabel.Text = $"Apply failed: {ex.Message}";
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => ApplyToDevice();

    private void OnSyncAllClick(object sender, RoutedEventArgs e)
    {
        var keyboard = _zones.FirstOrDefault(z => z.Name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase))
                       ?? _zones.FirstOrDefault();
        if (keyboard is null)
            return;
        _loadingUi = true;
        foreach (var z in _zones)
            z.SetRgb(keyboard.Rgb.r, keyboard.Rgb.g, keyboard.Rgb.b);
        _loadingUi = false;
        if (LiveApply.IsChecked == true)
            ApplyToDevice();
    }

    // ----- Profiles -----

    private void LoadProfilesUi(bool selectLast)
    {
        var file = ProfileStore.Load();
        _loadingUi = true;
        ProfileCombo.ItemsSource = file.Profiles.Select(p => p.Name).ToList();
        if (selectLast && !string.IsNullOrEmpty(file.LastApplied))
        {
            ProfileCombo.SelectedItem = file.Profiles
                .FirstOrDefault(p => p.Name.Equals(file.LastApplied, StringComparison.OrdinalIgnoreCase))?.Name;
            if (ProfileCombo.SelectedItem is string name)
                LoadProfileIntoUi(file, name);
        }
        _loadingUi = false;
    }

    private void LoadProfileIntoUi(ProfileFile file, string name)
    {
        var profile = file.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return;
        foreach (var z in _zones)
        {
            if (profile.ZoneColors.TryGetValue(z.Id, out var hex))
            {
                var (r, g, b) = ProfileStore.ParseHex(hex);
                z.SetRgb(r, g, b);
            }
        }
    }

    private void OnProfileSelected(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || ProfileCombo.SelectedItem is not string name)
            return;
        var file = ProfileStore.Load();
        _loadingUi = true;
        LoadProfileIntoUi(file, name);
        _loadingUi = false;
        ProfileNameBox.Text = name;
        file.LastApplied = name;
        ProfileStore.Save(file);
        ApplyToDevice();
    }

    private void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusLabel.Text = "Enter a profile name first.";
            return;
        }
        var file = ProfileStore.Load();
        var profile = file.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new RgbProfile { Name = name };
            file.Profiles.Add(profile);
        }
        profile.ZoneColors = _zones.ToDictionary(z => z.Id, z => z.Hex.TrimStart('#'));
        file.LastApplied = name;
        ProfileStore.Save(file);
        LoadProfilesUi(selectLast: false);
        ProfileCombo.SelectedItem = name;
        StatusLabel.Text = $"Saved profile '{name}'.";
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not string name)
            return;
        var file = ProfileStore.Load();
        file.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(file.LastApplied, name, StringComparison.OrdinalIgnoreCase))
            file.LastApplied = null;
        ProfileStore.Save(file);
        LoadProfilesUi(selectLast: false);
        StatusLabel.Text = $"Deleted profile '{name}'.";
    }

    // ----- Start with Windows (this account) -----
    // Per-user on purpose: an HKCU Run entry plus a per-account wake-restore scheduled
    // task, both registered by StartupInstaller without elevation. The old all-users HKLM
    // approach broke on secondary accounts — it pointed into another user's profile folder
    // (unreadable), and the wake task only ran for the account that created it.

    private void InitRunAtLoginCheckbox()
    {
        _loadingUi = true;
        RunAtLogin.IsChecked = StartupInstaller.IsInstalled();
        _loadingUi = false;
    }

    private void OnRunAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;

        bool enable = RunAtLogin.IsChecked == true;
        try
        {
            if (enable)
            {
                StartupInstaller.Install();
                StatusLabel.Text = "Will start minimized at login for this account (with sleep/wake restore).";
            }
            else
            {
                StartupInstaller.Uninstall();
                StatusLabel.Text = "Startup registration removed for this account.";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Startup setting failed: {ex.Message}";
            _loadingUi = true; RunAtLogin.IsChecked = !enable; _loadingUi = false;
        }
    }
}
