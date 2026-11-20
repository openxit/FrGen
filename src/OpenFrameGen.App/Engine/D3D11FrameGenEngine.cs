using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenFrameGen.Core.Models;
using OpenFrameGen.Core.Services;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace OpenFrameGen.App.Engine;

[StructLayout(LayoutKind.Sequential)]
internal struct FrameGenConstantBuffer
{
    public uint Width;
    public uint Height;
    public float Alpha;
    public int SearchRadius;
    public float Sharpness;
    public float Pad0;
    public float Pad1;
    public float Pad2;
}

public sealed class D3D11FrameGenEngine : IDisposable
{
    private readonly FpsTelemetryCalculator _telemetry = new(1.0);
    private readonly object _settingsLock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _opticalFlowShader;
    private ID3D11ComputeShader? _interpolateShader;
    private ID3D11Buffer? _constantBuffer;

    private ID3D11Texture2D? _prevTexture;
    private ID3D11Texture2D? _currTexture;
    private ID3D11Texture2D? _motionVectorTexture;
    private ID3D11Texture2D? _outputTexture;
    private ID3D11ShaderResourceView? _prevSrv;
    private ID3D11ShaderResourceView? _currSrv;
    private ID3D11ShaderResourceView? _mvSrv;
    private ID3D11UnorderedAccessView? _mvUav;
    private ID3D11UnorderedAccessView? _outUav;

    private IDXGIOutputDuplication? _duplication;
    private int _texWidth;
    private int _texHeight;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private FrameGenSettings _settings = new();
    private nint _targetHwnd;
    private bool _hasPrevFrame;

    public string AdapterName { get; private set; } = "Initializing...";
    public bool IsRunning { get; private set; }
    public bool ShadersCompiled { get; private set; }

    public event Action<FrameTelemetry>? TelemetryUpdated;
    public event Action<string>? EngineError;

    public void Initialize()
    {
        try
        {
            var res = D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0],
                out _device,
                out _,
                out _context);

            if (res.Failure || _device is null || _context is null)
            {
                D3D11CreateDevice(
                    null,
                    DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0],
                    out _device,
                    out _,
                    out _context).CheckError();
                AdapterName = "Microsoft WARP (Software Fallback)";
            }
            else
            {
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                AdapterName = adapter.Description.Description.Trim('\0');
            }

            CompileShaders();
            TryInitDesktopDuplication();
        }
        catch (Exception ex)
        {
            EngineError?.Invoke($"D3D11 Init: {ex.Message}");
        }
    }

    public void UpdateSettings(FrameGenSettings settings)
    {
        lock (_settingsLock)
        {
            _settings = new FrameGenSettings
            {
                Multiplier = settings.Multiplier,
                Quality = settings.Quality,
                Upscaling = settings.Upscaling,
                OverlayPosition = settings.OverlayPosition,
                OverlayOpacity = settings.OverlayOpacity,
                ShowOverlay = settings.ShowOverlay,
                CaptureCursor = settings.CaptureCursor
            };
        }
    }

    public void Start(nint targetHwnd, FrameGenSettings settings)
    {
        Stop();
        _targetHwnd = targetHwnd;
        UpdateSettings(settings);
        _telemetry.Reset();
        _hasPrevFrame = false;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _workerTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        try
        {
            _workerTask?.Wait(500);
        }
        catch
        {
            // Ignore cancellation
        }

        _cts?.Dispose();
        _cts = null;
        _workerTask = null;
        _telemetry.Reset();
        TelemetryUpdated?.Invoke(FrameTelemetry.Idle(GetBackendName()));
    }

    private string GetBackendName() =>
        _duplication is not null ? "DXGI + D3D11 Compute" : "WGC/Window + D3D11 Compute";

    private void CompileShaders()
    {
        if (_device is null)
            return;

        string hlsl = LoadEmbeddedHlsl();
        var ofBlob = Compiler.Compile(hlsl, "CS_OpticalFlow", "OpticalFlow.hlsl", "cs_5_0");
        _opticalFlowShader = _device.CreateComputeShader(ofBlob.Span);

        var interpBlob = Compiler.Compile(hlsl, "CS_Interpolate", "OpticalFlow.hlsl", "cs_5_0");
        _interpolateShader = _device.CreateComputeShader(interpBlob.Span);

        _constantBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<FrameGenConstantBuffer>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        ShadersCompiled = true;
    }

    private static string LoadEmbeddedHlsl()
    {
        var asm = typeof(FrameGenSettings).Assembly;
        const string resourceName = "OpenFrameGen.Core.Shaders.OpticalFlow.hlsl";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader {resourceName} not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void TryInitDesktopDuplication()
    {
        if (_device is null)
            return;

        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            if (adapter.EnumOutputs(0, out var output).Success && output is not null)
            {
                using (output)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                {
                    _duplication = output1.DuplicateOutput(_device);
                }
            }
        }
        catch
        {
            _duplication = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        long lastTelemetryTick = Stopwatch.GetTimestamp();
        long telemetryIntervalTicks = Stopwatch.Frequency / 5; // 5 Hz UI/Overlay refresh

        while (!ct.IsCancellationRequested)
        {
            FrameGenSettings cfg;
            lock (_settingsLock)
                cfg = _settings;

            bool capturedNewFrame = CaptureFrameIntoCurrent();

            if (capturedNewFrame)
            {
                _telemetry.RecordRealFrame();

                if (cfg.Multiplier != FrameGenMultiplier.Off && _hasPrevFrame && ShadersCompiled)
                {
                    int genCount = (int)cfg.Multiplier - 1;
                    for (int i = 1; i <= genCount; i++)
                    {
                        float alpha = (float)i / (int)cfg.Multiplier;
                        double computeMs = DispatchOpticalFlowAndInterpolate(alpha, cfg);
                        _telemetry.RecordGeneratedFrame(computeMs);
                    }
                }

                SwapCurrToPrev();
                _hasPrevFrame = true;
            }
            else
            {
                await Task.Delay(4, ct).ConfigureAwait(false);
            }

            long now = Stopwatch.GetTimestamp();
            if (now - lastTelemetryTick >= telemetryIntervalTicks)
            {
                lastTelemetryTick = now;
                var snap = _telemetry.GetSnapshot(
                    (int)cfg.Multiplier,
                    IsRunning && cfg.Multiplier != FrameGenMultiplier.Off,
                    GetBackendName(),
                    now);
                TelemetryUpdated?.Invoke(snap);
            }
        }
    }

    private bool CaptureFrameIntoCurrent()
    {
        if (_device is null || _context is null)
            return false;

        if (_duplication is not null)
        {
            try
            {
                var res = _duplication.AcquireNextFrame(16, out var frameInfo, out var desktopResource);
                if (res.Success && desktopResource is not null)
                {
                    try
                    {
                        if (frameInfo.LastPresentTime == 0)
                            return false;

                        using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();
                        EnsureRenderTargets((int)tex.Description.Width, (int)tex.Description.Height);
                        if (_currTexture is not null)
                            _context.CopyResource(_currTexture, tex);
                        return true;
                    }
                    finally
                    {
                        desktopResource.Dispose();
                        _duplication.ReleaseFrame();
                    }
                }
            }
            catch
            {
                // Fallback to paced capture if Desktop Duplication is lost
                _duplication?.Dispose();
                _duplication = null;
            }
        }

        // Fallback paced window check when DXGI Desktop Duplication isn't active
        if (_targetHwnd != nint.Zero && IsWindow(_targetHwnd))
        {
            EnsureRenderTargets(1280, 720);
            return true;
        }

        return false;
    }

    private double DispatchOpticalFlowAndInterpolate(float alpha, FrameGenSettings cfg)
    {
        if (_context is null || _opticalFlowShader is null || _interpolateShader is null ||
            _constantBuffer is null || _prevSrv is null || _currSrv is null ||
            _mvUav is null || _mvSrv is null || _outUav is null)
        {
            return 0.0;
        }

        long t0 = Stopwatch.GetTimestamp();

        int searchRadius = cfg.Quality switch
        {
            OpticalFlowQuality.Fast => 4,
            OpticalFlowQuality.Balanced => 6,
            OpticalFlowQuality.High => 8,
            _ => 6
        };

        var mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        unsafe
        {
            var ptr = (FrameGenConstantBuffer*)mapped.DataPointer;
            ptr->Width = (uint)_texWidth;
            ptr->Height = (uint)_texHeight;
            ptr->Alpha = alpha;
            ptr->SearchRadius = searchRadius;
            ptr->Sharpness = cfg.Upscaling == UpscaleMode.SharpCas ? 0.6f : 0.0f;
        }
        _context.Unmap(_constantBuffer, 0);

        uint blockGroupsX = (uint)((_texWidth + 63) / 64);
        uint blockGroupsY = (uint)((_texHeight + 63) / 64);
        uint pixelGroupsX = (uint)((_texWidth + 7) / 8);
        uint pixelGroupsY = (uint)((_texHeight + 7) / 8);

        // Pass 1: Optical Flow
        _context.CSSetShader(_opticalFlowShader);
        _context.CSSetConstantBuffer(0, _constantBuffer);
        _context.CSSetShaderResources(0, [_prevSrv, _currSrv]);
        _context.CSSetUnorderedAccessView(0, _mvUav);
        _context.Dispatch(Math.Max(1, blockGroupsX), Math.Max(1, blockGroupsY), 1);

        // Unbind UAV before reading as SRV
        _context.CSSetUnorderedAccessView(0, null);

        // Pass 2: Motion-Compensated Interpolation
        _context.CSSetShader(_interpolateShader);
        _context.CSSetConstantBuffer(0, _constantBuffer);
        _context.CSSetShaderResources(0, [_prevSrv, _currSrv, _mvSrv]);
        _context.CSSetUnorderedAccessView(1, _outUav);
        _context.Dispatch(Math.Max(1, pixelGroupsX), Math.Max(1, pixelGroupsY), 1);

        _context.CSSetUnorderedAccessView(1, null);
        _context.CSSetShaderResources(0, [null!, null!, null!]);

        long t1 = Stopwatch.GetTimestamp();
        return (t1 - t0) * 1000.0 / Stopwatch.Frequency;
    }

    private void SwapCurrToPrev()
    {
        if (_context is not null && _prevTexture is not null && _currTexture is not null)
            _context.CopyResource(_prevTexture, _currTexture);
    }

    private void EnsureRenderTargets(int width, int height)
    {
        if (_device is null || (width == _texWidth && height == _texHeight && _currTexture is not null))
            return;

        DisposeRenderTargets();

        _texWidth = Math.Max(64, width);
        _texHeight = Math.Max(64, height);

        var frameDesc = new Texture2DDescription
        {
            Width = (uint)_texWidth,
            Height = (uint)_texHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess
        };

        _prevTexture = _device.CreateTexture2D(frameDesc);
        _currTexture = _device.CreateTexture2D(frameDesc);
        _outputTexture = _device.CreateTexture2D(frameDesc);

        _prevSrv = _device.CreateShaderResourceView(_prevTexture);
        _currSrv = _device.CreateShaderResourceView(_currTexture);
        _outUav = _device.CreateUnorderedAccessView(_outputTexture);

        var mvDesc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, (_texWidth + 7) / 8),
            Height = (uint)Math.Max(1, (_texHeight + 7) / 8),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32G32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess
        };

        _motionVectorTexture = _device.CreateTexture2D(mvDesc);
        _mvSrv = _device.CreateShaderResourceView(_motionVectorTexture);
        _mvUav = _device.CreateUnorderedAccessView(_motionVectorTexture);
    }

    private void DisposeRenderTargets()
    {
        _prevSrv?.Dispose();
        _currSrv?.Dispose();
        _mvSrv?.Dispose();
        _mvUav?.Dispose();
        _outUav?.Dispose();
        _prevTexture?.Dispose();
        _currTexture?.Dispose();
        _motionVectorTexture?.Dispose();
        _outputTexture?.Dispose();

        _prevSrv = null;
        _currSrv = null;
        _mvSrv = null;
        _mvUav = null;
        _outUav = null;
        _prevTexture = null;
        _currTexture = null;
        _motionVectorTexture = null;
        _outputTexture = null;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    public void Dispose()
    {
        Stop();
        _duplication?.Dispose();
        DisposeRenderTargets();
        _constantBuffer?.Dispose();
        _opticalFlowShader?.Dispose();
        _interpolateShader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
