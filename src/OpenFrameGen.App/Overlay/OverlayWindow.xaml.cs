using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenFrameGen.Core.Models;

namespace OpenFrameGen.App.Overlay;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public void ApplyPlacement(OverlayCorner corner, double opacity)
    {
        HudRoot.Opacity = Math.Clamp(opacity, 0.25, 1.0);
        UpdateLayout();

        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 260;
        double h = ActualHeight > 0 ? ActualHeight : 120;

        switch (corner)
        {
            case OverlayCorner.TopLeft:
                Left = area.Left;
                Top = area.Top;
                break;
            case OverlayCorner.TopRight:
                Left = area.Right - w;
                Top = area.Top;
                break;
            case OverlayCorner.BottomLeft:
                Left = area.Left;
                Top = area.Bottom - h;
                break;
            case OverlayCorner.BottomRight:
                Left = area.Right - w;
                Top = area.Bottom - h;
                break;
        }
    }

    public void UpdateTelemetry(FrameTelemetry t)
    {
        TotalFpsText.Text = $"{t.TotalFps:0}";
        BaseFpsText.Text = $"{t.BaseFps:0} FPS";
        SplitText.Text = $"{t.RealFps:0} Real · {t.GeneratedFps:0} Gen ({t.Multiplier}x)";
        TimingText.Text = t.ComputeTimeMs > 0
            ? $"{t.FrameTimeMs:0.0} ms · OF {t.ComputeTimeMs:0.0} ms"
            : $"{t.FrameTimeMs:0.0} ms";

        if (t.IsActive)
        {
            StatusText.Text = $"FG {t.Multiplier}X";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            StatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94));
            StatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 34, 197, 94));
        }
        else
        {
            StatusText.Text = "PASSTHROUGH";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));
            StatusBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 245, 158, 11));
        }
    }
}
