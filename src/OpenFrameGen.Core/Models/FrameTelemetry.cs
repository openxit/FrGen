namespace OpenFrameGen.Core.Models;

public sealed record FrameTelemetry(
    double BaseFps,
    double TotalFps,
    double RealFps,
    double GeneratedFps,
    double FrameTimeMs,
    double ComputeTimeMs,
    int Multiplier,
    bool IsActive,
    string BackendName)
{
    public static FrameTelemetry Idle(string backend = "D3D11 Compute") =>
        new(0, 0, 0, 0, 0, 0, 1, false, backend);
}
