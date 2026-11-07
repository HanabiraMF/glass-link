using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Microsoft.Extensions.Logging;

namespace GlassLink.Encoding;

/// <summary>
/// Low-latency H.264 hardware encoder using Media Foundation IMFTransform +
/// IMFDXGIDeviceManager. NVENC / AMF / QSV are selected automatically by
/// Media Foundation. Input is a D3D11 NV12 texture; no CPU readback is made
/// before encoding. Encoded NAL units are Annex-B for WebCodecs.
/// </summary>
public sealed class MediaFoundationEncoder : IDisposable
{
    private const int MfVersion = 0x00020070;
    private const int MfErrorTransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfErrorTransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfErrorNoEventsAvailable = unchecked((int)0xC00D3E80);
    private const uint MfEnumFlagSyncMft = 0x00000001;
    private const uint MfEnumFlagAsyncMft = 0x00000002;
    private const uint MfEnumFlagHardware = 0x00000004;
    private const uint MfEnumFlagSortAndFilter = 0x00000040;
    private const uint MfNominalRange16To235 = 2;

    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MfSampleExtensionCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    private readonly ILogger _logger;
    private readonly VideoEncoderOptions _options;
    private readonly ID3D11Device _device;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private IMFDXGIDeviceManager? _deviceManager;
    private readonly bool _isAsync;
    private bool _streaming;
    private bool _disposed;
    private bool _needInput;
    private bool _forceKeyframe = true;
    private byte[] _sequenceHeader = Array.Empty<byte>();
    private string _codecString = "avc1.4D002A";
    private readonly long _frameDurationTicks;
    private readonly int _outputStreamFlags;
    private readonly int _outputBufferSize;
    private long _lastTimestampTicks = -1;
    private int _pendingBitrate;

    private MediaFoundationEncoder(
        ILogger logger,
        VideoEncoderOptions options,
        ID3D11Device device,
        IMFTransform transform,
        IMFMediaEventGenerator? events,
        IMFDXGIDeviceManager deviceManager,
        bool isAsync,
        bool hardware,
        string encoderName,
        int outputStreamFlags,
        int outputBufferSize)
    {
        _outputStreamFlags = outputStreamFlags;
        _outputBufferSize = outputBufferSize;
        _logger = logger;
        _options = options;
        _device = device;
        _transform = transform;
        _events = events;
        _deviceManager = deviceManager;
        _isAsync = isAsync;
        _frameDurationTicks = 10_000_000L / Math.Max(1, options.Fps);
        HardwareAccelerated = hardware;
        EncoderName = encoderName;
    }

    public string EncoderName { get; }
    public bool HardwareAccelerated { get; }
    public string CodecString => _codecString;
    public byte[] SequenceHeader => _sequenceHeader;
    public bool IsAsync => _isAsync;

    public static void MFStartup()
    {
        MediaFactory.MFStartup(true);
    }

    public static void MFShutdown()
    {
        try { MediaFactory.MFShutdown(); } catch { }
    }

    public static MediaFoundationEncoder Create(
        ILogger logger,
        VideoEncoderOptions options,
        ID3D11Device device)
    {
        var inputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12
        };
        var outputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        var failures = new List<string>();
        var candidates = new List<IMFActivate>();
        IMFActivateCollection? hardwareCollection = null;
        IMFActivateCollection? softwareCollection = null;

        try
        {
            hardwareCollection = CollectCandidates(logger, options, inputInfo, outputInfo,
                MfEnumFlagHardware | MfEnumFlagAsyncMft | MfEnumFlagSortAndFilter, candidates);
            var hardwareCount = candidates.Count;

            if (candidates.Count == 0 && options.AllowSoftwareFallback)
            {
                logger.LogWarning("No hardware H.264 encoder was enumerated; trying software Media Foundation fallback. This is not the preferred GPU path.");
                softwareCollection = CollectCandidates(logger, options, inputInfo, outputInfo,
                    MfEnumFlagAsyncMft | MfEnumFlagSyncMft | MfEnumFlagSortAndFilter, candidates);
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException("未找到可用的 H.264 Media Foundation 编码器（NVENC/AMF/QSV 均未枚举到）。请更新显卡驱动。");

            for (var index = 0; index < candidates.Count; index++)
            {
                var activate = candidates[index];
                var isHardwareCandidate = index < hardwareCount;
                var name = $"Media Foundation H.264 #{index}";
                try
                {
                    name = activate.FriendlyName ?? name;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        try { name = activate.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = $"Media Foundation H.264 #{index}";
                    logger.LogDebug("Encoder candidate {Index}: {Name}, hardware={Hardware}.", index, name, isHardwareCandidate);
                }
                catch
                {
                    // Some MFTs do not expose a friendly name.
                }

                try
                {
                    var transform = ActivateTransform(activate);
                    var encoder = TryCreateFromTransform(logger, options, device, transform, isHardwareCandidate, name!, failures);
                    if (encoder is not null)
                        return encoder;
                    transform.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {ex.Message}");
                    logger.LogDebug(ex, "Encoder candidate failed: {Name}", name);
                }
            }
        }
        finally
        {
            // Keep the activation collections (and therefore each IMFActivate)
            // alive until activation is complete. Disposing the collection
            // invalidates the enumerated wrappers.
            try { hardwareCollection?.Dispose(); } catch { }
            try { softwareCollection?.Dispose(); } catch { }
        }

        throw new InvalidOperationException(
            "Media Foundation H.264 编码器初始化失败。\n" +
            string.Join("\n", failures.Take(10)));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateObjectDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

    private static IMFTransform ActivateTransform(IMFActivate activate)
    {
        // Vortice's generic ActivateObject<T>() can throw a NullReferenceException
        // for MFT activation objects depending on the runtime. Call the COM
        // IMFActivate::ActivateObject vtable slot directly. IMFActivate inherits
        // IMFAttributes: slots 3..32 are IMFAttributes methods, so ActivateObject
        // is vtable slot 33 (3 IUnknown + 30 IMFAttributes + 0).
        var vtable = Marshal.ReadIntPtr(activate.NativePointer);
        var method = Marshal.ReadIntPtr(vtable, 33 * IntPtr.Size);
        var activateObject = Marshal.GetDelegateForFunctionPointer<ActivateObjectDelegate>(method);
        var iid = typeof(IMFTransform).GUID;
        var hr = activateObject(activate.NativePointer, ref iid, out var transformPointer);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (transformPointer == IntPtr.Zero) throw new InvalidOperationException("ActivateObject returned a null transform.");
        return new IMFTransform(transformPointer);
    }

    private static IMFActivateCollection? CollectCandidates(
        ILogger logger,
        VideoEncoderOptions options,
        RegisterTypeInfo inputInfo,
        RegisterTypeInfo outputInfo,
        uint flags,
        List<IMFActivate> candidates)
    {
        IMFActivateCollection? activates = null;
        try
        {
            activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, inputInfo, outputInfo);
            foreach (var activate in activates)
            {
                candidates.Add(activate);
                if (candidates.Count >= 12) break;
            }
            return activates;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MFTEnumEx returned no encoder list for flags 0x{Flags:X}", flags);
            try { activates?.Dispose(); } catch { }
            return null;
        }
    }

    private static MediaFoundationEncoder? TryCreateFromTransform(
        ILogger logger,
        VideoEncoderOptions options,
        ID3D11Device device,
        IMFTransform transform,
        bool hardware,
        string name,
        List<string> failures)
    {
        IMFMediaEventGenerator? events = null;
        IMFDXGIDeviceManager? manager = null;
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        var success = false;

        try
        {
            var isAsync = false;
            try
            {
                using var attributes = transform.Attributes;
                try
                {
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);
                }
                catch
                {
                    // Synchronous MFTs do not need the async unlock.
                }

                try
                {
                    attributes.Set(MfLowLatency, true);
                }
                catch
                {
                    logger.LogDebug("Encoder does not expose MF_LOW_LATENCY.");
                }

                isAsync = TryGetUInt32(attributes, TransformAttributeKeys.TransformAsync, out var asyncValue) && asyncValue != 0;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read MFT attributes.");
            }

            if (isAsync)
                events = transform.QueryInterface<IMFMediaEventGenerator>();

            // Hardware MFTs require the DXGI device manager before any media type
            // negotiation; otherwise SetOutputType returns MF_E_UNSUPPORTED_D3D_TYPE.
            manager = MediaFactory.MFCreateDXGIDeviceManager();
            var resetResult = manager.ResetDevice(device);
            resetResult.CheckError();
            logger.LogDebug("DXGI device manager reset: token={Token}, hr={Hr}.", manager.ResetToken, resetResult.Code);
            transform.ProcessMessage(TMessageType.MessageSetD3DManager, unchecked((nuint)manager.NativePointer));

            outputType = BuildOutputType(options);
            try
            {
                transform.SetOutputType(0, outputType, 0);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Output type with codec extensions rejected; retrying minimal type.");
                DumpOutputTypes(logger, transform, options);
                outputType.Dispose();
                outputType = BuildMinimalOutputType(options);
                transform.SetOutputType(0, outputType, 0);
            }

            inputType = BuildInputType(options);
            try
            {
                transform.SetInputType(0, inputType, 0);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Full input type rejected; retrying minimal input type.");
                inputType.Dispose();
                inputType = BuildMinimalInputType(options);
                transform.SetInputType(0, inputType, 0);
            }

            var outputInfo = transform.GetOutputStreamInfo(0);
            logger.LogDebug("Encoder output stream: flags={Flags}, size={Size}, alignment={Alignment}.",
                outputInfo.Flags, outputInfo.Size, outputInfo.Alignment);

            ConfigureCodecApi(logger, transform, options);

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            var encoder = new MediaFoundationEncoder(logger, options, device, transform, events, manager, isAsync, hardware, name,
                outputInfo.Flags, outputInfo.Size)
            {
                _streaming = true
            };
            encoder.CaptureSequenceHeader();

            logger.LogInformation("Media Foundation encoder created: {Name} ({Kind}), async={Async}.", name,
                hardware ? "hardware" : "software", isAsync);
            success = true;
            events = null;
            manager = null;
            return encoder;
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex}");
            logger.LogDebug(ex, "Encoder configuration failed for {Name}.", name);
            return null;
        }
        finally
        {
            if (!success)
            {
                events?.Dispose();
                manager?.Dispose();
                outputType?.Dispose();
                inputType?.Dispose();
            }
        }
    }

    private static void DumpOutputTypes(ILogger logger, IMFTransform transform, VideoEncoderOptions options)
    {
        for (var i = 0; i < 24; i++)
        {
            try
            {
                using var type = transform.GetOutputAvailableType(0, i);
                if (type is null) break;
                var major = type.MajorType;
                var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                string size = "?";
                string rate = "?";
                string profile = "?";
                try
                {
                    var packed = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    size = $"{packed >> 32}x{packed & 0xFFFFFFFF}";
                }
                catch { }
                try
                {
                    var packed = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                    rate = $"{packed >> 32}/{packed & 0xFFFFFFFF}";
                }
                catch { }
                try { profile = type.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile).ToString(); } catch { }
                logger.LogDebug("  Available output[{Index}]: major={Major}, subtype={Subtype}, size={Size}, rate={Rate}, profile={Profile}",
                    i, major, subtype, size, rate, profile);
            }
            catch
            {
                break;
            }
        }
    }

    private void CaptureSequenceHeader()
    {
        if (_transform is null) return;
        try
        {
            using var currentOutput = _transform.GetOutputCurrentType(0);
            var header = currentOutput.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (header.Length > 0)
            {
                _sequenceHeader = H264Utilities.NormalizeAnnexB(header);
                _codecString = H264Utilities.BuildCodecString(_sequenceHeader);
            }
        }
        catch
        {
            // The first keyframe may carry SPS/PPS in-band; handled in ConvertOutputSample.
        }
    }

    private static IMFMediaType BuildOutputType(VideoEncoderOptions options)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)options.Bitrate);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)options.Width, (uint)options.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)options.Fps, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2); // MFVideoInterlace_Progressive
        return type;
    }

    private static IMFMediaType BuildMinimalOutputType(VideoEncoderOptions options)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)options.Bitrate);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)options.Width, (uint)options.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)options.Fps, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        return type;
    }

    private static IMFMediaType BuildInputType(VideoEncoderOptions options)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)options.Width, (uint)options.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)options.Fps, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        return type;
    }

    private static IMFMediaType BuildMinimalInputType(VideoEncoderOptions options)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)options.Width, (uint)options.Height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)options.Fps, 1));
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
        return type;
    }

    private static void ConfigureCodecApi(ILogger logger, IMFTransform transform, VideoEncoderOptions options)
    {
        SetCodec(logger, transform, CodecApiInterop.AvEncCommonRateControlMode, 0, "rate-control CBR", required: false);
        SetCodec(logger, transform, CodecApiInterop.AvEncCommonMeanBitRate, (uint)options.Bitrate, "mean bitrate", required: false);
        SetCodec(logger, transform, CodecApiInterop.AvEncCommonMaxBitRate, (uint)options.Bitrate, "max bitrate", required: false);
        SetCodec(logger, transform, CodecApiInterop.AvEncCommonBufferSize, (uint)Math.Max(100_000, options.Bitrate / 2), "VBV buffer", required: false);
        SetCodec(logger, transform, CodecApiInterop.AvEncMpvGopSize, (uint)options.Gop, "GOP", required: false);
        SetCodec(logger, transform, CodecApiInterop.AvEncMpvDefaultBPictureCount, 0, "B-frames=0", required: false);
        var lowLatencyHr = CodecApiInterop.SetBool(transform.NativePointer, CodecApiInterop.AvEncCommonLowLatency, true);
        if (lowLatencyHr < 0) logger.LogDebug("Encoder does not support CODECAPI_AVEncCommonLowLatency (0x{Hr:X8}).", lowLatencyHr);
    }

    private static void SetCodec(ILogger logger, IMFTransform transform, Guid api, uint value, string name, bool required)
    {
        var hr = CodecApiInterop.SetUInt32(transform.NativePointer, api, value);
        if (hr >= 0) return;
        if (required) throw new InvalidOperationException($"编码器不接受 {name} 设置 (0x{hr:X8})。");
        logger.LogDebug("Encoder rejected {Setting} (0x{Hr:X8}); using Media Foundation default.", name, hr);
    }

    private static bool TryGetUInt32(IMFAttributes attributes, Guid key, out uint value)
    {
        try
        {
            value = attributes.GetUInt32(key);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    public void RequestKeyframe()
    {
        lock (this) _forceKeyframe = true;
    }

    /// <summary>Dynamically changes the target bitrate through ICodecAPI.</summary>
    public void UpdateBitrate(int bitrate)
    {
        Interlocked.Exchange(ref _pendingBitrate, Math.Clamp(bitrate, 1_000_000, 80_000_000));
    }

    /// <summary>Feed one NV12 GPU texture and return all encoded NAL units produced.</summary>
    public IReadOnlyList<EncodedVideoFrame> Encode(ID3D11Texture2D nv12Texture, long timestampMicroseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var transform = _transform ?? throw new ObjectDisposedException(nameof(MediaFoundationEncoder));

        var outputs = new List<EncodedVideoFrame>(2);
        var newBitrate = Interlocked.Exchange(ref _pendingBitrate, 0);
        if (newBitrate > 0)
        {
            var meanHr = CodecApiInterop.SetUInt32(transform.NativePointer, CodecApiInterop.AvEncCommonMeanBitRate, (uint)newBitrate);
            var maxHr = CodecApiInterop.SetUInt32(transform.NativePointer, CodecApiInterop.AvEncCommonMaxBitRate, (uint)newBitrate);
            if (meanHr < 0 || maxHr < 0)
                _logger.LogDebug("Dynamic bitrate update partially rejected (mean=0x{Mean:X8}, max=0x{Max:X8}).", meanHr, maxHr);
            else
                _logger.LogInformation("Encoder bitrate updated to {Bitrate} bps.", newBitrate);
        }

        if (_isAsync)
            WaitForNeedInput(5_000);

        if (_forceKeyframe)
        {
            _forceKeyframe = false;
            var hr = CodecApiInterop.SetUInt32(transform.NativePointer, CodecApiInterop.AvEncVideoForceKeyFrame, 1);
            if (hr < 0) _logger.LogDebug("Force-keyframe request not supported (0x{Hr:X8}).", hr);
        }

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12Texture, 0, false);
        buffer.CurrentLength = buffer.MaxLength;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        var timestampTicks = Math.Max(0, timestampMicroseconds) * 10;
        if (timestampTicks <= _lastTimestampTicks) timestampTicks = _lastTimestampTicks + 1;
        _lastTimestampTicks = timestampTicks;
        sample.SampleTime = timestampTicks;
        sample.SampleDuration = _frameDurationTicks;

        transform.ProcessInput(0, sample, 0);
        if (_isAsync)
        {
            _needInput = false;
            WaitForOutputEvent(5_000, outputs);
        }
        else
        {
            DrainProcessOutput(outputs, drainAll: true);
        }

        return outputs;
    }

    private void WaitForNeedInput(int timeoutMilliseconds)
    {
        if (_events is null) throw new InvalidOperationException("Async encoder event generator missing.");
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!_needInput)
        {
            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            if (remaining <= 1) throw new TimeoutException("等待编码器 NeedInput 超时。");
            ReadEvent(remaining, null);
        }
    }

    private void WaitForOutputEvent(int timeoutMilliseconds, List<EncodedVideoFrame> outputs)
    {
        if (_events is null) throw new InvalidOperationException("Async encoder event generator missing.");
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        var gotOutput = false;

        while (!gotOutput)
        {
            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            if (remaining <= 1) throw new TimeoutException("等待编码器输出超时。");
            ReadEvent(remaining, outputs);
            gotOutput = outputs.Count > 0;
        }
    }

    private void ReadEvent(int timeoutMilliseconds, List<EncodedVideoFrame>? outputs)
    {
        if (_events is null) return;

        IMFMediaEvent ev;
        try
        {
            ev = _events.GetEvent(timeoutMilliseconds);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MfErrorNoEventsAvailable)
        {
            throw new TimeoutException("Media Foundation 事件队列等待超时。", ex);
        }

        using (ev)
        {
            switch (ev.EventType)
            {
                case MediaEventTypes.Error:
                    throw new InvalidOperationException($"Media Foundation 编码器错误：{ev.Status.Description} (0x{ev.Status.Code:X8})。");
                case MediaEventTypes.TransformNeedInput:
                    _needInput = true;
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    // One HaveOutput event corresponds to one output sample for
                    // async hardware MFTs. Calling ProcessOutput repeatedly after
                    // that can return E_UNEXPECTED/0x8000FFFF on NVIDIA's MFT.
                    DrainProcessOutput(outputs ?? new List<EncodedVideoFrame>(), drainAll: false);
                    break;
                case MediaEventTypes.TransformDrainComplete:
                    break;
            }
        }
    }

    private void DrainProcessOutput(List<EncodedVideoFrame> outputs, bool drainAll)
    {
        if (_transform is null) return;

        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0 };
            var status = default(ProcessOutputStatus);
            IMFSample? callerAllocatedSample = null;
            IMFMediaBuffer? callerAllocatedBuffer = null;

            if ((_outputStreamFlags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
            {
                var bufferSize = Math.Max(_outputBufferSize, 4 * 1024 * 1024);
                callerAllocatedBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
                callerAllocatedSample = MediaFactory.MFCreateSample();
                callerAllocatedSample.AddBuffer(callerAllocatedBuffer);
                output.Sample = callerAllocatedSample;
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out status);

            if (result.Code == MfErrorTransformNeedMoreInput || result.Code == MfErrorNoEventsAvailable)
            {
                callerAllocatedSample?.Dispose();
                callerAllocatedBuffer?.Dispose();
                break;
            }

            if (result.Code == MfErrorTransformStreamChange)
            {
                callerAllocatedSample?.Dispose();
                callerAllocatedBuffer?.Dispose();
                throw new InvalidOperationException("编码器请求动态输出格式变化，当前固定 H.264 管线不支持。");
            }

            if (result.Failure)
            {
                callerAllocatedSample?.Dispose();
                callerAllocatedBuffer?.Dispose();
                throw new InvalidOperationException($"编码器 ProcessOutput 失败：{result.Description} (0x{result.Code:X8})。");
            }

            if (output.Sample is null)
            {
                callerAllocatedSample?.Dispose();
                callerAllocatedBuffer?.Dispose();
                continue;
            }

            try
            {
                outputs.Add(ConvertOutputSample(output.Sample));
            }
            finally
            {
                output.Sample.Dispose();
                callerAllocatedBuffer?.Dispose();
            }

            if (!drainAll) break;
        }
    }

    private EncodedVideoFrame ConvertOutputSample(IMFSample sample)
    {
        var timestamp = sample.SampleTime;
        var duration = sample.SampleDuration;
        var sampleIsKeyframe = IsKeyframe(sample);

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var currentLength);
        byte[] bytes;
        try
        {
            bytes = new byte[currentLength];
            if (currentLength > 0) Marshal.Copy(pointer, bytes, 0, currentLength);
        }
        finally
        {
            buffer.Unlock();
        }

        var annexB = H264Utilities.NormalizeAnnexB(bytes);
        if (annexB.Length == 0) annexB = bytes;

        var isKeyframe = sampleIsKeyframe || H264Utilities.ContainsIdr(annexB);
        if (isKeyframe)
        {
            if (_sequenceHeader.Length == 0)
            {
                _sequenceHeader = H264Utilities.ExtractParameterSets(annexB);
                if (_sequenceHeader.Length > 0)
                    _codecString = H264Utilities.BuildCodecString(_sequenceHeader);
            }
            annexB = H264Utilities.EnsureParameterSets(annexB, _sequenceHeader);
        }

        return new EncodedVideoFrame(
            annexB,
            isKeyframe,
            timestamp / 10,
            (uint)Math.Max(1, duration / 10));
    }

    private static bool IsKeyframe(IMFSample sample)
    {
        try
        {
            return sample.GetUInt32(MfSampleExtensionCleanPoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_transform is not null && _streaming)
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch { }

        try { _transform?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }

        _events?.Dispose();
        _events = null;
        try { _deviceManager?.Dispose(); } catch { }
        _deviceManager = null;
        try { _transform?.Dispose(); } catch { }
        _transform = null;
        _streaming = false;
    }
}
