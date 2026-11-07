using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using GlassLink.Protocol;

namespace GlassLink.Capture;

public sealed class CaptureReinitializeRequiredException : Exception
{
    public CaptureReinitializeRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record CaptureDeviceInfo(
    string AdapterName,
    string OutputName,
    CaptureBounds Bounds,
    string Format,
    bool HardwareDevice);

/// <summary>
/// DXGI Desktop Duplication capture. Frames stay on the GPU as an ID3D11Texture2D;
/// they are copied into a stable GPU texture and handed directly to the D3D11 video
/// processor / Media Foundation encoder. No Map/Unmap and no CPU readback happen.
/// </summary>
public sealed class DxgiScreenCapturer : IDisposable
{
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    private readonly IDXGIFactory1 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput1 _output;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Texture2D _gpuFrame;
    private bool _disposed;

    private DxgiScreenCapturer(
        IDXGIFactory1 factory,
        IDXGIAdapter1 adapter,
        IDXGIOutput1 output,
        IDXGIOutputDuplication duplication,
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11Texture2D gpuFrame,
        CaptureDeviceInfo info)
    {
        _factory = factory;
        _adapter = adapter;
        _output = output;
        _duplication = duplication;
        _device = device;
        _context = context;
        _gpuFrame = gpuFrame;
        Info = info;
    }

    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;
    public CaptureDeviceInfo Info { get; }
    public CaptureBounds Bounds => Info.Bounds;

    public static DxgiScreenCapturer Create(int outputIndex = 0)
    {
        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput1? output = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;

        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIOutput? chosenOutput = null;
            AdapterDescription1 adapterDesc = default;
            OutputDescription outputDesc = default;

            var allOutputs = new List<(IDXGIAdapter1 Adapter, IDXGIOutput Output, AdapterDescription1 AdapterDesc, OutputDescription OutputDesc)>();
            for (uint a = 0; ; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out var candidateAdapter);
                if (adapterResult.Failure || candidateAdapter is null) break;

                var desc = candidateAdapter.Description1;
                if ((desc.Flags & AdapterFlags.Software) != 0)
                {
                    candidateAdapter.Dispose();
                    continue;
                }

                for (uint o = 0; ; o++)
                {
                    var outputResult = candidateAdapter.EnumOutputs(o, out var candidateOutput);
                    if (outputResult.Failure || candidateOutput is null) break;
                    var od = candidateOutput.Description;
                    if (od.AttachedToDesktop && od.DesktopCoordinates.Right > od.DesktopCoordinates.Left)
                    {
                        allOutputs.Add((candidateAdapter, candidateOutput, desc, od));
                    }
                    else
                    {
                        candidateOutput.Dispose();
                    }
                }
            }

            if (allOutputs.Count == 0)
                throw new InvalidOperationException("没有找到连接到桌面的 DXGI 输出设备。请确认不是无头/远程桌面会话。");

            var selected = allOutputs.Count > outputIndex
                ? allOutputs[outputIndex]
                : allOutputs[0];
            chosenOutput = selected.Output;
            adapter = selected.Adapter;
            adapterDesc = selected.AdapterDesc;
            outputDesc = selected.OutputDesc;

            // Release all non-selected outputs and adapters.
            foreach (var entry in allOutputs)
            {
                if (!ReferenceEquals(entry.Output, chosenOutput)) entry.Output.Dispose();
                if (!ReferenceEquals(entry.Adapter, adapter)) entry.Adapter.Dispose();
            }

            output = chosenOutput.QueryInterface<IDXGIOutput1>();

            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            var create = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels,
                out device,
                out var featureLevel,
                out context);

            if (create.Failure || device is null || context is null)
            {
                // Retry without 11.1 (old Windows builds may reject the 11.1 request).
                create = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                    out device,
                    out featureLevel,
                    out context);
                create.CheckError();
            }

            if (device is null || context is null)
                throw new InvalidOperationException("创建 D3D11 设备失败。");

            // Media Foundation hardware encoders access the same D3D11 device from
            // their own MFT worker threads. Without multithread protection the
            // DXGI device manager initialization / type negotiation can fail with
            // MF_E_UNSUPPORTED_D3D_TYPE.
            using (var multithread = device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            duplication = output.DuplicateOutput(device);

            var rect = outputDesc.DesktopCoordinates;
            var bounds = new CaptureBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

            var copyDesc = new Texture2DDescription
            {
                Width = (uint)bounds.Width,
                Height = (uint)bounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            var gpuFrame = device.CreateTexture2D(copyDesc);
            var info = new CaptureDeviceInfo(
                adapterDesc.Description.TrimEnd('\0'),
                outputDesc.DeviceName.TrimEnd('\0'),
                bounds,
                "B8G8R8A8_UNorm",
                (device.FeatureLevel >= FeatureLevel.Level_11_0));

            return new DxgiScreenCapturer(factory, adapter, output, duplication, device, context, gpuFrame, info);
        }
        catch (SharpGenException ex)
        {
            DisposeAll(duplication, context, device, output, adapter, factory);
            throw new CaptureReinitializeRequiredException(
                $"DXGI Desktop Duplication 初始化失败：{ex.Message}。请检查是否处于受保护内容、远程桌面或不支持的显示配置；可尝试以管理员身份运行。",
                ex);
        }
        catch
        {
            DisposeAll(duplication, context, device, output, adapter, factory);
            throw;
        }
    }

    /// <summary>
    /// Acquire the next desktop frame and copy it to a stable GPU texture. If no new
    /// frame is available, returns false. The returned texture is valid until the
    /// next capture call; all processing happens on the GPU.
    /// </summary>
    public bool TryCaptureFrame(int timeoutMilliseconds, out ID3D11Texture2D? texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        texture = null;

        var result = _duplication.AcquireNextFrame((uint)Math.Max(1, timeoutMilliseconds), out _, out var resource);
        if (result.Code == DxgiErrorWaitTimeout) return false;

        if (result.Code == DxgiErrorAccessLost)
            throw new CaptureReinitializeRequiredException("DXGI Desktop Duplication 访问丢失（桌面切换、分辨率变化、UAC 安全桌面或显卡驱动重置）。");

        if (result.Failure)
            throw new CaptureReinitializeRequiredException($"AcquireNextFrame 失败：{result.Description} (0x{result.Code:X8})。");

        try
        {
            using var frameTexture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = frameTexture.Description;

            if (desc.Width != _gpuFrame.Description.Width || desc.Height != _gpuFrame.Description.Height)
                throw new CaptureReinitializeRequiredException("桌面分辨率发生变化，需要重建捕获与编码管线。");

            _context.CopyResource(_gpuFrame, frameTexture);
            texture = _gpuFrame;
            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private static void DisposeAll(params IDisposable?[] objects)
    {
        foreach (var obj in objects)
        {
            try { obj?.Dispose(); } catch { /* best-effort native teardown */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeAll(_gpuFrame, _duplication, _context, _device, _output, _adapter, _factory);
    }
}
