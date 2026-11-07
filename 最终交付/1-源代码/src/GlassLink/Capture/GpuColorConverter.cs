using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using GlassLink.Protocol;

namespace GlassLink.Capture;

/// <summary>
/// GPU BGRA -> NV12 conversion through the D3D11 Video Processor. This is the
/// path required by Media Foundation hardware encoders (NVENC / AMF / QSV) and
/// keeps every byte on the GPU. The intermediate textures are re-used.
/// </summary>
public sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _bgraTexture;
    private readonly ID3D11Texture2D _nv12Texture;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private bool _disposed;

    private GpuColorConverter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        ID3D11VideoProcessorEnumerator enumerator,
        ID3D11VideoProcessor processor,
        ID3D11Texture2D bgraTexture,
        ID3D11Texture2D nv12Texture,
        ID3D11VideoProcessorInputView inputView,
        ID3D11VideoProcessorOutputView outputView,
        int width,
        int height)
    {
        _device = device;
        _context = context;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _enumerator = enumerator;
        _processor = processor;
        _bgraTexture = bgraTexture;
        _nv12Texture = nv12Texture;
        _inputView = inputView;
        _outputView = outputView;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Nv12Texture => _nv12Texture;

    public static GpuColorConverter Create(ID3D11Device device, ID3D11DeviceContext context, CaptureBounds bounds, int fps)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));

        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        ID3D11VideoProcessorEnumerator? enumerator = null;
        ID3D11VideoProcessor? processor = null;
        ID3D11Texture2D? bgra = null;
        ID3D11Texture2D? nv12 = null;
        ID3D11VideoProcessorInputView? inputView = null;
        ID3D11VideoProcessorOutputView? outputView = null;

        try
        {
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();

            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.OptimalSpeed
            };

            var enumResult = videoDevice.CreateVideoProcessorEnumerator(content, out enumerator);
            enumResult.CheckError();
            if (enumerator is null) throw new InvalidOperationException("无法创建 D3D11 视频处理器枚举器。");

            var bgraSupport = enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out var bgraFlags);
            bgraSupport.CheckError();
            var nv12Support = enumerator.CheckVideoProcessorFormat(Format.NV12, out var nv12Flags);
            nv12Support.CheckError();

            if ((bgraFlags & VideoProcessorFormatSupport.Input) == 0)
                throw new InvalidOperationException("当前 GPU 的 D3D11 视频处理器不支持 BGRA 输入。");
            if ((nv12Flags & VideoProcessorFormatSupport.Output) == 0)
                throw new InvalidOperationException("当前 GPU 的 D3D11 视频处理器不支持 NV12 输出。");

            var processorResult = videoDevice.CreateVideoProcessor(enumerator, 0, out processor);
            processorResult.CheckError();
            if (processor is null) throw new InvalidOperationException("无法创建 D3D11 视频处理器。");

            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);

            var inputColor = new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 0,      // full-range RGB input
                YCbCr_Matrix = 1,   // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 2   // 0-255
            };
            var outputColor = new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 0,
                YCbCr_Matrix = 1,   // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 1   // 16-235, what H.264 encoders expect
            };
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0, inputColor);
            videoContext.VideoProcessorSetOutputColorSpace(processor, outputColor);

            var bgraDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            bgra = device.CreateTexture2D(bgraDesc);

            var nv12Desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            nv12 = device.CreateTexture2D(nv12Desc);

            var inputDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            };
            var inputResult = videoDevice.CreateVideoProcessorInputView(bgra, enumerator, inputDesc, out inputView);
            inputResult.CheckError();

            var outputDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            };
            var outputResult = videoDevice.CreateVideoProcessorOutputView(nv12, enumerator, outputDesc, out outputView);
            outputResult.CheckError();

            if (inputView is null || outputView is null)
                throw new InvalidOperationException("无法创建视频处理器输入/输出视图。");

            return new GpuColorConverter(device, context, videoDevice, videoContext, enumerator, processor,
                bgra, nv12, inputView, outputView, width, height);
        }
        catch
        {
            inputView?.Dispose();
            outputView?.Dispose();
            nv12?.Dispose();
            bgra?.Dispose();
            processor?.Dispose();
            enumerator?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            throw;
        }
    }

    /// <summary>GPU-only BGRA -> NV12 conversion. Returns the NV12 texture.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var desc = source.Description;
        if (desc.Width != (uint)Width || desc.Height != (uint)Height)
            throw new CaptureReinitializeRequiredException("捕获尺寸与颜色转换器不一致。");

        _context.CopyResource(_bgraTexture, source);

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = _inputView
        };

        var result = _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, new[] { stream });
        result.CheckError();

        // Submit the copy + blit before the Media Foundation encoder's worker
        // thread consumes the texture through the shared D3D11 device.
        _context.Flush();
        return _nv12Texture;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputView.Dispose();
        _outputView.Dispose();
        _nv12Texture.Dispose();
        _bgraTexture.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
