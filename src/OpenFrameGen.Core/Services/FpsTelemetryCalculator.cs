using System.Diagnostics;
using OpenFrameGen.Core.Models;

namespace OpenFrameGen.Core.Services;

/// <summary>
/// Tracks real captured frames and GPU-generated interpolated frames over a 1-second sliding window.
/// </summary>
public sealed class FpsTelemetryCalculator
{
    private readonly object _gate = new();
    private readonly Queue<long> _realTimestamps = new();
    private readonly Queue<long> _genTimestamps = new();
    private readonly Queue<double> _computeTimesMs = new();
    private readonly long _windowTicks;

    public FpsTelemetryCalculator(double windowSeconds = 1.0)
    {
        _windowTicks = (long)(Stopwatch.Frequency * Math.Clamp(windowSeconds, 0.25, 5.0));
    }

    public void RecordRealFrame(long? timestampTicks = null)
    {
        long now = timestampTicks ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _realTimestamps.Enqueue(now);
            Prune(now);
        }
    }

    public void RecordGeneratedFrame(double computeTimeMs, long? timestampTicks = null)
    {
        long now = timestampTicks ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _genTimestamps.Enqueue(now);
            _computeTimesMs.Enqueue(computeTimeMs);
            Prune(now);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _realTimestamps.Clear();
            _genTimestamps.Clear();
            _computeTimesMs.Clear();
        }
    }

    public FrameTelemetry GetSnapshot(int multiplier, bool isActive, string backendName, long? timestampTicks = null)
    {
        long now = timestampTicks ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Prune(now);

            double windowSec = (double)_windowTicks / Stopwatch.Frequency;
            double realFps = _realTimestamps.Count / windowSec;
            double genFps = _genTimestamps.Count / windowSec;
            double totalFps = realFps + genFps;
            double frameTimeMs = totalFps > 0.1 ? 1000.0 / totalFps : 0.0;
            double avgComputeMs = _computeTimesMs.Count > 0 ? _computeTimesMs.Average() : 0.0;

            return new FrameTelemetry(
                BaseFps: Math.Round(realFps, 1),
                TotalFps: Math.Round(totalFps, 1),
                RealFps: Math.Round(realFps, 1),
                GeneratedFps: Math.Round(genFps, 1),
                FrameTimeMs: Math.Round(frameTimeMs, 2),
                ComputeTimeMs: Math.Round(avgComputeMs, 2),
                Multiplier: multiplier,
                IsActive: isActive,
                BackendName: backendName);
        }
    }

    private void Prune(long now)
    {
        long cutoff = now - _windowTicks;
        while (_realTimestamps.Count > 0 && _realTimestamps.Peek() < cutoff)
            _realTimestamps.Dequeue();
        while (_genTimestamps.Count > 0 && _genTimestamps.Peek() < cutoff)
            _genTimestamps.Dequeue();
        while (_computeTimesMs.Count > _genTimestamps.Count && _computeTimesMs.Count > 0)
            _computeTimesMs.Dequeue();
    }
}
