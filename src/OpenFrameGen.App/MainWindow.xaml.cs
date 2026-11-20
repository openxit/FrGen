using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OpenFrameGen.App.Engine;
using OpenFrameGen.App.Overlay;
using OpenFrameGen.Core.Models;
using OpenFrameGen.Core.Services;

namespace OpenFrameGen.App;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_FG = 9001;
    private const int HOTKEY_TOGGLE_OVERLAY = 9002;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_F = 0x46;
    private const uint VK_O = 0x4F;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private readonly D3D11FrameGenEngine _engine = new();
    private readonly DriverManager _driverManager = new();
    private readonly OverlayWindow _overlay = new();
    private readonly FrameGenSettings _settings = new();
    private bool _isUiReady;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        RegisterHotKey(hwnd, HOTKEY_TOGGLE_FG, MOD_CONTROL | MOD_ALT, VK_F);
        RegisterHotKey(hwnd, HOTKEY_TOGGLE_OVERLAY, MOD_CONTROL | MOD_ALT, VK_O);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_TOGGLE_FG)
            {
                ToggleFrameGen();
                handled = true;
            }
            else if (id == HOTKEY_TOGGLE_OVERLAY)
            {
                OverlayCheck.IsChecked = !(OverlayCheck.IsChecked ?? false);
                handled = true;
            }
        }
        return nint.Zero;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _engine.TelemetryUpdated += OnTelemetryUpdated;
        _engine.EngineError += msg => Dispatcher.Invoke(() => FooterStatusText.Text = $"Note: {msg}");
        _engine.Initialize();

        GpuInfoText.Text = $"GPU: {_engine.AdapterName}";
        FooterStatusText.Text = _engine.ShadersCompiled
            ? "Ready · HLSL Optical-Flow Compute Shaders compiled"
            : "Ready · Software fallback";

        RefreshWindows();
        RefreshDriverStatus();

        _isUiReady = true;
        ApplyOverlayState();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, HOTKEY_TOGGLE_FG);
        UnregisterHotKey(hwnd, HOTKEY_TOGGLE_OVERLAY);

        _overlay.Close();
        _engine.Dispose();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var windows = WindowEnumerator.GetCaptureableWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.DisplayMemberPath = nameof(TargetWindowInfo.DisplayName);
        if (windows.Count > 0 && WindowCombo.SelectedIndex < 0)
            WindowCombo.SelectedIndex = 0;
    }

    private void RefreshDriverStatus()
    {
        var status = _driverManager.GetStatus();
        DriverPathText.Text = status.InstallDirectory;
        if (status.IsInstalled)
        {
            string reg = status.IsVulkanLayerRegistered ? "Vulkan Layer active" : "Not registered";
            DriverStatusText.Text = $"Installed ({status.InstalledVersion}) · {reg}";
            UninstallDriverBtn.IsEnabled = true;
        }
        else
        {
            DriverStatusText.Text = "Status: Not installed (D3D11 Capture active)";
            UninstallDriverBtn.IsEnabled = false;
        }
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e) => ToggleFrameGen();

    private void ToggleFrameGen()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            ToggleBtn.Content = "Start Frame-Gen (Ctrl+Alt+F)";
            FooterStatusText.Text = "Stopped · Passthrough";
        }
        else
        {
            nint hwnd = (WindowCombo.SelectedItem as TargetWindowInfo)?.Handle ?? nint.Zero;
            ReadSettingsFromUi();
            _engine.Start(hwnd, _settings);
            ToggleBtn.Content = "Stop Frame-Gen (Ctrl+Alt+F)";
            FooterStatusText.Text = $"Active · {(int)_settings.Multiplier}x Optical Flow";
        }
    }

    private void SettingsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady)
            return;
        ReadSettingsFromUi();
        _engine.UpdateSettings(_settings);
    }

    private void OverlayConfigChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady)
            return;
        ReadSettingsFromUi();
        ApplyOverlayState();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityLabel is not null)
            OpacityLabel.Text = $"Opacity ({Math.Round(e.NewValue * 100):0}%)";
        if (!_isUiReady)
            return;
        ReadSettingsFromUi();
        ApplyOverlayState();
    }

    private void ReadSettingsFromUi()
    {
        if (MultiplierCombo.SelectedItem is ComboBoxItem multItem &&
            int.TryParse(multItem.Tag?.ToString(), out int mult))
        {
            _settings.Multiplier = (FrameGenMultiplier)mult;
        }

        if (QualityCombo.SelectedItem is ComboBoxItem qualItem &&
            Enum.TryParse<OpticalFlowQuality>(qualItem.Tag?.ToString(), out var qual))
        {
            _settings.Quality = qual;
        }

        if (UpscaleCombo.SelectedItem is ComboBoxItem upItem &&
            Enum.TryParse<UpscaleMode>(upItem.Tag?.ToString(), out var up))
        {
            _settings.Upscaling = up;
        }

        if (OverlayCornerCombo.SelectedItem is ComboBoxItem cornerItem &&
            Enum.TryParse<OverlayCorner>(cornerItem.Tag?.ToString(), out var corner))
        {
            _settings.OverlayPosition = corner;
        }

        _settings.OverlayOpacity = OpacitySlider.Value;
        _settings.ShowOverlay = OverlayCheck.IsChecked ?? true;
    }

    private void ApplyOverlayState()
    {
        if (_settings.ShowOverlay)
        {
            if (!_overlay.IsVisible)
                _overlay.Show();
            _overlay.ApplyPlacement(_settings.OverlayPosition, _settings.OverlayOpacity);
        }
        else if (_overlay.IsVisible)
        {
            _overlay.Hide();
        }
    }

    private void OnTelemetryUpdated(FrameTelemetry t)
    {
        Dispatcher.InvokeAsync(() =>
        {
            BaseFpsLabel.Text = $"{t.BaseFps:0.0} FPS";
            TotalFpsLabel.Text = $"{t.TotalFps:0.0} FPS";
            RealGenLabel.Text = $"{t.RealFps:0.0} Real · {t.GeneratedFps:0.0} Gen";
            TimingLabel.Text = $"{t.FrameTimeMs:0.0} ms · OF {t.ComputeTimeMs:0.0} ms";

            if (_overlay.IsVisible)
                _overlay.UpdateTelemetry(t);
        });
    }

    private async void InstallDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallDriverBtn.IsEnabled = false;
        UninstallDriverBtn.IsEnabled = false;
        DriverProgress.Visibility = Visibility.Visible;
        DriverProgress.Value = 0;
        DriverStatusText.Text = "Downloading driver package from GitHub...";

        try
        {
            var progress = new Progress<double>(p => DriverProgress.Value = p);
            await _driverManager.InstallOrUpdateAsync(progress);
            FooterStatusText.Text = "Vulkan driver layer installed successfully.";
        }
        catch (Exception ex)
        {
            DriverStatusText.Text = $"Error: {ex.Message}";
            FooterStatusText.Text = "Driver download failed.";
        }
        finally
        {
            DriverProgress.Visibility = Visibility.Collapsed;
            InstallDriverBtn.IsEnabled = true;
            RefreshDriverStatus();
        }
    }

    private void UninstallDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _driverManager.Uninstall();
            FooterStatusText.Text = "Vulkan driver layer uninstalled.";
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = $"Uninstall error: {ex.Message}";
        }
        RefreshDriverStatus();
    }
}
