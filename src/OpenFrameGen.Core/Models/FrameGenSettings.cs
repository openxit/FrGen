namespace OpenFrameGen.Core.Models;

public enum FrameGenMultiplier
{
    Off = 1,
    X2 = 2,
    X3 = 3
}

public enum OpticalFlowQuality
{
    Fast,
    Balanced,
    High
}

public enum UpscaleMode
{
    Off,
    SharpCas
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public sealed class FrameGenSettings
{
    public FrameGenMultiplier Multiplier { get; set; } = FrameGenMultiplier.X2;
    public OpticalFlowQuality Quality { get; set; } = OpticalFlowQuality.Balanced;
    public UpscaleMode Upscaling { get; set; } = UpscaleMode.SharpCas;
    public OverlayCorner OverlayPosition { get; set; } = OverlayCorner.TopLeft;
    public double OverlayOpacity { get; set; } = 0.86;
    public bool ShowOverlay { get; set; } = true;
    public bool CaptureCursor { get; set; } = false;
}
