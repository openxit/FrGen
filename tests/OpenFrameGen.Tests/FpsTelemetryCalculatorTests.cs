using System.Diagnostics;
using OpenFrameGen.Core.Services;
using Xunit;

namespace OpenFrameGen.Tests;

public sealed class FpsTelemetryCalculatorTests
{
    [Fact]
    public void CalculatesBaseTotalAndSplitAccurately()
    {
        var calc = new FpsTelemetryCalculator(1.0);
        long t0 = 10_000_000;

        for (int i = 0; i < 60; i++)
        {
            long ts = t0 + i * (Stopwatch.Frequency / 60);
            calc.RecordRealFrame(ts);
            calc.RecordGeneratedFrame(0.8, ts + 100);
        }

        long snapshotTime = t0 + Stopwatch.Frequency - 10;
        var snap = calc.GetSnapshot(2, isActive: true, "DXGI + D3D11 Compute", snapshotTime);

        Assert.Equal(60.0, snap.BaseFps);
        Assert.Equal(60.0, snap.RealFps);
        Assert.Equal(60.0, snap.GeneratedFps);
        Assert.Equal(120.0, snap.TotalFps);
        Assert.True(snap.IsActive);
        Assert.Equal(0.8, snap.ComputeTimeMs);
    }

    [Fact]
    public void PrunesOldFramesOutsideWindow()
    {
        var calc = new FpsTelemetryCalculator(1.0);
        long t0 = 10_000_000;
        calc.RecordRealFrame(t0);
        calc.RecordGeneratedFrame(1.2, t0);

        long afterWindow = t0 + Stopwatch.Frequency * 2;
        var snap = calc.GetSnapshot(2, isActive: true, "Test", afterWindow);

        Assert.Equal(0.0, snap.BaseFps);
        Assert.Equal(0.0, snap.TotalFps);
    }
}
