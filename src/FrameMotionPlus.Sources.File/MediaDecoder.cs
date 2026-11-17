using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Sources.File.Interop;

namespace FrameMotionPlus.Sources.File;

/// <summary>A file that cannot be played: not openable, no video stream, an unsupported codec or format, a decode error.</summary>
public sealed class FileSourceException : Exception
{
    public FileSourceException()
    {
    }

    public FileSourceException(string message)
        : base(message)
    {
    }

    public FileSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// FFmpeg demux and NVDEC decode of one video stream into CUDA memory, in the application's CUDA context.
/// <para>
/// <b>The CUDA context.</b> The device context is created with <c>AV_CUDA_USE_CURRENT_CONTEXT</c> while the
/// application's primary context is current, so FFmpeg takes that context (it checks: the device context's
/// <c>cuda_ctx</c> must equal <see cref="CudaContext.Handle"/>) and never destroys or releases it. FFmpeg 9.0.2's other
/// option, <c>AV_CUDA_USE_PRIMARY_CONTEXT</c>, insists on <c>CU_CTX_SCHED_BLOCKING_SYNC</c>: it fails with ENOTSUP when
/// the primary context is already active with other flags (ours is) and silently changes the flags when it is not.
/// </para>
/// <para>
/// <b>Streams.</b> <c>AVCUDADeviceContext.stream</c> is set to <paramref name="copyStream"/> before the decoder exists,
/// so NVDEC's post-processing (<c>cuvidMapVideoFrame</c>'s output stream) and FFmpeg's device-to-device copy of each
/// decoded picture into its frame pool run on it. See docs/benchmarks/phase4a-decode.md for what that does to other
/// threads.
/// </para>
/// <para>
/// <b>Frame pool.</b> FFmpeg's NVDEC path copies each decoded picture into a frame of a CUDA frame pool that starts
/// empty and grows on demand (<c>cuMemAlloc</c>) and is freed only when the decoder is (<c>cuMemFree</c>). The first
/// decoded frame grows it to <see cref="PoolFrames"/> frames at once (start-up); afterwards every frame handed out is
/// checked against those buffers, and <see cref="PoolGrowth"/> counts any new one (an allocation during playback).
/// With frames released before the next is decoded, as the source does, it stays 0.
/// </para>
/// <para>
/// <b>Seeking.</b> <see cref="Seek"/> restarts at the keyframe at or before a time, or, where the demuxer has no time
/// index (an elementary stream has no timestamps), at the first byte of the file; it reports rather than throws when
/// neither works, and does not try on an input that cannot seek (a pipe: FFmpeg's byte seek returns success there
/// without moving). It drops every frame the decoder still holds from before: <c>avcodec_flush_buffers</c> alone does not
/// (FFmpeg 9.0.2's HEVC decoder keeps the frames its drain at the end of the stream had already queued for output, and
/// would return them after the seek), so the seek receives and discards until the decoder asks for input, and any frame
/// that still came out before the first packet from the new position would be dropped too (<see cref="StaleFrames"/>).
/// </para>
/// One thread at a time.
/// </summary>
internal sealed unsafe class MediaDecoder : IDisposable
{
    /// <summary>Frame-pool buffers made at start-up: two are ever in use at once (one held, one being copied into).</summary>
    public const int PoolFrames = 4;

    private const int TrackedBuffers = 16;

    private readonly CudaContext _context;
    private readonly ulong[] _poolBuffers = new ulong[TrackedBuffers];
    private GCHandle _self;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _scratch;
    private AVStream* _stream;
    private int _poolCount;
    private bool _sentSinceSeek = true;
    private bool _restartsByByte;
    private bool _flushSent;
    private bool _packetPending;
    private bool _hardwareRefused;
    private bool _disposed;

    public MediaDecoder(string path, CudaContext context, CudaStream copyStream)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(copyStream);
        FFmpegLibraries.EnsureLoaded();
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException($"No such file: {path}", path);
        }

        _context = context;
        _self = GCHandle.Alloc(this);
        try
        {
            Open(path, copyStream);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public FileStreamInfo Info { get; private set; } = null!;

    public int StreamIndex { get; private set; }

    /// <summary>Frame-pool buffers that appeared after start-up (each one a <c>cuMemAlloc</c> during playback).</summary>
    public long PoolGrowth { get; private set; }

    /// <summary>Packets the decoder refused as invalid data (corrupt input skipped).</summary>
    public long CorruptPackets { get; private set; }

    /// <summary>Frames the decoder flagged corrupt (decoded with concealment).</summary>
    public long CorruptFrames { get; private set; }

    /// <summary>True once the end of the stream was reached and every frame was output.</summary>
    public bool AtEnd { get; private set; }

    /// <summary>Why demuxing ended early, if it ended with an error rather than at the end of the file.</summary>
    public string? DemuxError { get; private set; }

    /// <summary>Frames from before a seek that the decoder still returned after it, and were dropped.</summary>
    public long StaleFrames { get; private set; }

    /// <summary>Why the last seek that failed could not be made.</summary>
    public string? SeekError { get; private set; }

    /// <summary>The pool buffers made at start-up (tests).</summary>
    internal int PoolBuffers => _poolCount;

    /// <summary>The <c>CUcontext</c> FFmpeg's device context holds (tests: it must be the application's).</summary>
    internal nint DeviceContext { get; private set; }

    /// <summary>The <c>CUstream</c> FFmpeg's device context works on (tests: it must be the copy stream).</summary>
    internal nint DeviceStream { get; private set; }

    /// <summary>
    /// The next decoded frame in presentation order, into <paramref name="frame"/> (unreferenced first); false at the
    /// end of the stream. The frame is an <c>AV_PIX_FMT_CUDA</c> frame whose planes are in CUDA memory, written by work
    /// on the copy stream; unreference it before asking for the next. Allocation-free once started.
    /// </summary>
    public bool Receive(AVFrame* frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AvUtil.av_frame_unref(frame);
        while (true)
        {
            var result = AvCodec.avcodec_receive_frame(_codec, frame);
            if (result == 0)
            {
                if (!_sentSinceSeek)
                {
                    // Nothing from the new position has reached the decoder yet: this frame is from before the seek.
                    StaleFrames++;
                    AvUtil.av_frame_unref(frame);
                    continue;
                }

                Accept(frame);
                return true;
            }

            if (result == Av.ErrorEof)
            {
                AtEnd = true;
                return false;
            }

            if (result == Av.ErrorInvalidData)
            {
                CorruptFrames++;
                continue;
            }

            if (result != Av.ErrorEagain)
            {
                ThrowDecode(result, "avcodec_receive_frame");
            }

            Feed();
        }
    }

    /// <summary>
    /// Restarts from a keyframe at or before <paramref name="ticks"/> (stream time base), or from the first byte of the
    /// file, and drops the decoder's state and every frame it still holds. Frames before the target are still output;
    /// the caller skips them. <see cref="SeekOutcome.Failed"/> (with <see cref="SeekError"/>) when the input cannot be
    /// seeked: nothing changed, decoding goes on from where it was. Allocation-free when it succeeds.
    /// <para>
    /// A time seek is checked: the first packet read afterwards must be a keyframe at or before the target. FFmpeg's
    /// MPEG-TS and MPEG-PS seeks land on whatever packet is near the target's DTS, keyframe or not (their timestamp search
    /// takes every packet for a keyframe), so frames up to the next keyframe would be lost, and with a single keyframe all
    /// of them. When the check fails the seek goes back further (1 s, 2 s, 4 s, ... before the target). A target at or
    /// before the start, and a search that reaches it, restarts at the first byte where the format can (MPEG-TS/PS and
    /// elementary streams: <c>AVFMT_TS_DISCONT</c> or <c>AVFMT_NOTIMESTAMPS</c>, byte seeks allowed; FFmpeg's byte seek
    /// starts at the first data byte), else at the start by time. An elementary stream, which has no time index at all,
    /// always restarts at its first byte.
    /// </para>
    /// </summary>
    public SeekOutcome Seek(long ticks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_format->Pb == null || (_format->Pb->Seekable & Av.AvioSeekableNormal) == 0)
        {
            // A pipe or a stream: FFmpeg's byte seek would report success and stay where it is.
            SeekError = $"cannot seek in '{Info.Path}': the input is not seekable (a pipe or a stream)";
            return SeekOutcome.Failed;
        }

        if (_packetPending)
        {
            AvCodec.av_packet_unref(_packet);
            _packetPending = false;
        }

        var start = Info.StartTicks == long.MinValue ? 0 : Info.StartTicks;
        var second = Math.Max(1, (long)Info.TimeBaseDen / Math.Max(1, Info.TimeBaseNum));
        var outcome = SeekOutcome.Failed;
        var back = 0L;
        for (var attempt = 0; attempt < 64 && outcome == SeekOutcome.Failed; attempt++)
        {
            var at = ticks - back;
            if (at <= start)
            {
                // The start: by byte where that is the first frame, else by time (an indexed container lands on its first
                // keyframe), unchecked.
                if (_restartsByByte && AvFormat.av_seek_frame(_format, -1, 0, Av.SeekFlagByte) >= 0)
                {
                    outcome = SeekOutcome.Start;
                    break;
                }

                var startError = AvFormat.av_seek_frame(_format, StreamIndex, start, Av.SeekFlagBackward);
                if (startError < 0)
                {
                    SeekError = $"cannot seek in '{Info.Path}' to its start: {FFmpegLibraries.ErrorText(startError)}";
                    return SeekOutcome.Failed;
                }

                outcome = SeekOutcome.Time;
                break;
            }

            var timeError = AvFormat.av_seek_frame(_format, StreamIndex, at, Av.SeekFlagBackward);
            if (timeError < 0)
            {
                // No time index (an elementary stream has no timestamps, so FFmpeg's generic index stays empty): the start.
                if (_restartsByByte && AvFormat.av_seek_frame(_format, -1, 0, Av.SeekFlagByte) >= 0)
                {
                    outcome = SeekOutcome.Start;
                    break;
                }

                SeekError = $"cannot seek in '{Info.Path}': {FFmpegLibraries.ErrorText(timeError)}";
                return SeekOutcome.Failed;
            }

            if (LandedOnKeyframe(ticks))
            {
                outcome = SeekOutcome.Time;
                break;
            }

            back = back == 0 ? second : back * 2;
        }

        if (outcome == SeekOutcome.Failed)
        {
            SeekError = $"cannot seek in '{Info.Path}': no keyframe found before the target";
            return SeekOutcome.Failed;
        }

        AvCodec.avcodec_flush_buffers(_codec);

        // What the decoder had already queued for output survives the flush in some decoders (HEVC's output FIFO after
        // a drain): receive it now, before any packet from the new position, and drop it.
        while (AvCodec.avcodec_receive_frame(_codec, _scratch) == 0)
        {
            StaleFrames++;
            AvUtil.av_frame_unref(_scratch);
        }

        _sentSinceSeek = false;
        _flushSent = false;
        AtEnd = false;
        DemuxError = null;
        return outcome;
    }

    /// <summary>
    /// After a time seek: reads the stream's first packet and keeps it for the decoder. True when it is a keyframe at or
    /// before <paramref name="target"/> (or has no timestamp), so decoding from it reaches the target; otherwise the
    /// packet is dropped. False also at the end of the file (the seek landed past it).
    /// </summary>
    private bool LandedOnKeyframe(long target)
    {
        while (AvFormat.av_read_frame(_format, _packet) >= 0)
        {
            if (_packet->StreamIndex != StreamIndex)
            {
                AvCodec.av_packet_unref(_packet);
                continue;
            }

            var pts = _packet->Pts;
            if ((_packet->Flags & Av.PacketFlagKey) != 0 && (pts == Av.NoPtsValue || pts <= target))
            {
                _packetPending = true;
                return true;
            }

            AvCodec.av_packet_unref(_packet);
            return false;
        }

        return false;
    }

    /// <summary>The decoded planes of <paramref name="frame"/> (a frame <see cref="Receive"/> returned), cropped to the visible size.</summary>
    public YuvPlanes Planes(AVFrame* frame)
    {
        var bytes = Info.SampleFormat == YuvSampleFormat.P010 ? 2 : 1;
        var left = (long)frame->CropLeft;
        var top = (long)frame->CropTop;
        return new YuvPlanes
        {
            Luma = frame->Data[0] + (ulong)((top * frame->LineSize[0]) + (left * bytes)),
            LumaPitch = frame->LineSize[0],
            Chroma = frame->Data[1] + (ulong)((top / 2 * frame->LineSize[1]) + (left / 2 * 2 * bytes)),
            ChromaPitch = frame->LineSize[1],
            Width = Info.Width,
            Height = Info.Height,
            Format = Info.SampleFormat,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_packet != null)
        {
            var packet = _packet;
            AvCodec.av_packet_free(&packet);
            _packet = null;
        }

        if (_scratch != null)
        {
            var scratch = _scratch;
            AvUtil.av_frame_free(&scratch);
            _scratch = null;
        }

        if (_codec != null)
        {
            // Frees the device context reference and, once no frame refers to it, the frame pool (cuMemFree): a drained
            // point only.
            _context.EnsureCurrent();
            var codec = _codec;
            AvCodec.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format != null)
        {
            var format = _format;
            AvFormat.avformat_close_input(&format);
            _format = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Open(string path, CudaStream copyStream)
    {
        FFmpegLibraries.ClearRecentErrors();
        var utf8 = Encoding.UTF8.GetBytes(path + "\0");
        AVFormatContext* format = null;
        int result;
        fixed (byte* url = utf8)
        {
            result = AvFormat.avformat_open_input(&format, url, null, null);
        }

        if (result < 0)
        {
            throw Failure($"cannot open '{path}'", result);
        }

        _format = format;
        result = AvFormat.avformat_find_stream_info(_format, null);
        if (result < 0)
        {
            throw Failure($"cannot read the streams of '{path}'", result);
        }

        void* decoder = null;
        result = AvFormat.av_find_best_stream(_format, Av.MediaTypeVideo, -1, -1, &decoder, 0);
        if (result < 0)
        {
            throw result == Av.ErrorDecoderNotFound
                ? Failure($"'{path}': FFmpeg has no decoder for its video stream", result)
                : Failure($"'{path}' has no video stream", result);
        }

        StreamIndex = result;
        for (var i = 0; i < (int)_format->NbStreams; i++)
        {
            if (i != StreamIndex)
            {
                _format->Streams[i]->Discard = Av.DiscardAll; // the demuxer drops their packets
            }
        }

        _stream = _format->Streams[StreamIndex];
        var formatFlags = _format->IFormat == null ? 0 : _format->IFormat->Flags;
        _restartsByByte = (formatFlags & (Av.FormatTsDiscont | Av.FormatNoTimestamps)) != 0 && (formatFlags & Av.FormatNoByteSeek) == 0;
        var parameters = _stream->CodecPar;
        var codecName = Utf8(AvCodec.avcodec_get_name(parameters->CodecId));
        if (parameters->CodecId is not (Av.CodecIdH264 or Av.CodecIdHevc or Av.CodecIdAv1 or Av.CodecIdVp9))
        {
            throw new FileSourceException(
                $"'{path}': the video codec is {codecName}; file playback decodes H.264, HEVC, AV1 and VP9 with NVDEC.");
        }

        if (!HasCudaHwaccel(decoder))
        {
            // The first decoder registered for the codec may be a software library (libdav1d for AV1): FFmpeg's own
            // decoder, named like the codec, is the one with the NVDEC hwaccel.
            var native = Encoding.ASCII.GetBytes(codecName + "\0");
            fixed (byte* name = native)
            {
                decoder = AvCodec.avcodec_find_decoder_by_name(name);
            }

            if (decoder == null || !HasCudaHwaccel(decoder))
            {
                throw new FileSourceException($"'{path}': this FFmpeg's {codecName} decoder has no CUDA (NVDEC) hardware acceleration.");
            }
        }

        var (bitDepth, sampleFormat) = parameters->Format switch
        {
            Av.PixFmtYuv420P or Av.PixFmtYuvJ420P or Av.PixFmtNv12 => (8, YuvSampleFormat.Nv12),
            Av.PixFmtYuv420P10Le or Av.PixFmtP010Le => (10, YuvSampleFormat.P010),
            _ => (0, YuvSampleFormat.Unknown),
        };
        if (bitDepth == 0)
        {
            var name = parameters->Format < 0 ? "unknown" : Utf8(AvUtil.av_get_pix_fmt_name(parameters->Format));
            throw new FileSourceException(
                $"'{path}': the video is {name}; file playback takes 4:2:0 at 8 or 10 bits (NV12, P010), not 4:2:2, 4:4:4 or 12-bit.");
        }

        var raw = new RawColorDescription(parameters->ColorPrimaries, parameters->ColorTrc, parameters->ColorSpace, parameters->ColorRange, parameters->ChromaLocation);
        var matrix = ColorMapping.Matrix(raw, parameters->Height)
            ?? throw new FileSourceException($"'{path}': colour matrix {ColorMapping.MatrixName(raw.Matrix)} is not supported (BT.601, BT.709 and BT.2020 non-constant are).");
        var output = bitDepth > 8 ? PixelFormat.Rgb10A2 : PixelFormat.Rgba8;
        var mastering = ColorMapping.Mastering((AVMasteringDisplayMetadata*)CodedSideData(parameters, Av.PacketDataMasteringDisplayMetadata));
        var lightLevel = ColorMapping.LightLevel((AVContentLightMetadata*)CodedSideData(parameters, Av.PacketDataContentLightLevel));
        var rate = Rate(_stream->AvgFrameRate) is { IsValid: true } avg ? avg : Rate(_stream->RFrameRate);
        var timeBase = _stream->TimeBase;
        Info = new FileStreamInfo
        {
            Path = path,
            Container = Utf8(_format->IFormat == null ? null : _format->IFormat->Name),
            Codec = codecName,
            Profile = Utf8(AvCodec.avcodec_profile_name(parameters->CodecId, parameters->Profile)),
            StreamIndex = StreamIndex,
            Width = parameters->Width,
            Height = parameters->Height,
            BitDepth = bitDepth,
            SampleFormat = sampleFormat,
            OutputFormat = output,
            NominalRate = rate,
            TimeBaseNum = timeBase.Num,
            TimeBaseDen = timeBase.Den,
            StartTicks = _stream->StartTime == Av.NoPtsValue ? long.MinValue : _stream->StartTime,
            DiscontinuousTimestamps = (formatFlags & Av.FormatTsDiscont) != 0,
            DurationNs = _stream->Duration > 0 && timeBase.Den > 0
                ? (long)((Int128)_stream->Duration * timeBase.Num * 1_000_000_000 / timeBase.Den)
                : _format->Duration > 0 ? _format->Duration * 1000 : 0,
            DeclaredFrames = Math.Max(0, _stream->NbFrames),
            RawColor = raw,
            Color = ColorMapping.ToColorMetadata(raw, matrix, mastering, lightLevel),
            Conversion = YuvConversion.Create(matrix, ColorMapping.Range(raw), bitDepth, ColorMapping.Siting(raw), output),
        };

        _codec = AvCodec.avcodec_alloc_context3(decoder);
        if (_codec == null)
        {
            throw new FileSourceException("avcodec_alloc_context3 failed." + " (FFmpeg is out of memory)");
        }

        result = AvCodec.avcodec_parameters_to_context(_codec, parameters);
        if (result < 0)
        {
            throw Failure("avcodec_parameters_to_context", result);
        }

        _codec->PktTimebase = timeBase;
        _codec->ThreadCount = 1; // hwaccel decode on the calling thread only: no frame threads, no extra delay
        _codec->Opaque = (void*)GCHandle.ToIntPtr(_self);
        _codec->GetFormat = &GetFormat;
        _codec->HwDeviceCtx = CreateDevice(copyStream);
        result = AvCodec.avcodec_open2(_codec, decoder, null);
        if (result < 0)
        {
            throw Failure($"cannot open the {codecName} decoder", result);
        }

        _packet = AvCodec.av_packet_alloc();
        _scratch = AvUtil.av_frame_alloc();
        if (_packet == null || _scratch == null)
        {
            throw new FileSourceException("av_packet_alloc or av_frame_alloc failed." + " (FFmpeg is out of memory)");
        }
    }

    /// <summary>
    /// A CUDA device context on the application's context (current on this thread), with every CUDA call of FFmpeg's
    /// on <paramref name="copyStream"/>.
    /// </summary>
    private AVBufferRef* CreateDevice(CudaStream copyStream)
    {
        _context.MakeCurrent();
        AVBufferRef* device = null;
        var ordinal = Encoding.ASCII.GetBytes(_context.Device.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        int result;
        fixed (byte* name = ordinal)
        {
            result = AvUtil.av_hwdevice_ctx_create(&device, Av.HwDeviceTypeCuda, name, null, Av.CudaUseCurrentContext);
        }

        if (result < 0)
        {
            throw Failure("cannot create FFmpeg's CUDA device context", result);
        }

        var cuda = (AVCUDADeviceContext*)((AVHWDeviceContext*)device->Data)->HwCtx;
        if (cuda->CudaCtx != _context.Handle)
        {
            AvUtil.av_buffer_unref(&device);
            throw new FileSourceException("FFmpeg's CUDA device context did not take the application's CUDA context.");
        }

        cuda->Stream = copyStream.Handle;
        DeviceContext = cuda->CudaCtx;
        DeviceStream = cuda->Stream;
        return device;
    }

    /// <summary>Reads packets of the stream into the decoder until it has one (or the end of the stream is signalled).</summary>
    private void Feed()
    {
        if (_flushSent)
        {
            // Draining: receive will report the end. Nothing else to feed.
            ThrowDecode(Av.ErrorEof, "avcodec_receive_frame (no frame after the end of the stream was signalled)");
        }

        while (true)
        {
            if (!_packetPending)
            {
                var read = AvFormat.av_read_frame(_format, _packet);
                if (read < 0)
                {
                    if (read != Av.ErrorEof)
                    {
                        // A truncated or damaged file: decode what was read, then end.
                        DemuxError = FFmpegLibraries.ErrorText(read);
                    }

                    _flushSent = true;
                    _sentSinceSeek = true;
                    var flush = AvCodec.avcodec_send_packet(_codec, null);
                    if (flush < 0 && flush != Av.ErrorEof)
                    {
                        ThrowDecode(flush, "avcodec_send_packet (end of stream)");
                    }

                    return;
                }

                if (_packet->StreamIndex != StreamIndex)
                {
                    AvCodec.av_packet_unref(_packet);
                    continue;
                }

                _packetPending = true;
            }

            var sent = AvCodec.avcodec_send_packet(_codec, _packet);
            if (sent == Av.ErrorEagain)
            {
                return; // keep the packet; a frame is waiting to be received first
            }

            AvCodec.av_packet_unref(_packet);
            _packetPending = false;
            if (sent == Av.ErrorInvalidData)
            {
                CorruptPackets++;
                continue;
            }

            if (sent < 0)
            {
                ThrowDecode(sent, "avcodec_send_packet");
            }

            _sentSinceSeek = true;
            return;
        }
    }

    /// <summary>Checks a received frame: NVDEC output in CUDA memory; tracks the frame pool (start-up growth, then any new buffer).</summary>
    private void Accept(AVFrame* frame)
    {
        if (frame->Format != Av.PixFmtCuda || frame->HwFramesCtx == null)
        {
            throw new FileSourceException(
                $"'{Info.Path}': FFmpeg decoded a frame in software ({FormatName(frame->Format)}) instead of with NVDEC; the GPU cannot decode this stream.");
        }

        var frames = (AVHWFramesContext*)frame->HwFramesCtx->Data;
        var expected = Info.SampleFormat == YuvSampleFormat.P010 ? Av.PixFmtP010Le : Av.PixFmtNv12;
        if (frames->SwFormat != expected)
        {
            throw new FileSourceException(
                $"'{Info.Path}': NVDEC delivered {FormatName(frames->SwFormat)} where {FormatName(expected)} was expected (a format change mid-stream is not supported).");
        }

        if (frame->Width - (int)frame->CropLeft < Info.Width || frame->Height - (int)frame->CropTop < Info.Height ||
            (frame->CropLeft & 1) != 0 || (frame->CropTop & 1) != 0)
        {
            throw new FileSourceException(
                $"'{Info.Path}': a decoded frame is {frame->Width}x{frame->Height} (crop {frame->CropLeft},{frame->CropTop}) for a {Info.Width}x{Info.Height} stream; a size change mid-stream is not supported.");
        }

        if ((frame->Flags & Av.FrameFlagCorrupt) != 0)
        {
            CorruptFrames++;
        }

        var buffer = frame->Data[0];
        if (_poolCount == 0)
        {
            Grow(frame, buffer);
            return;
        }

        for (var i = 0; i < _poolCount; i++)
        {
            if (_poolBuffers[i] == buffer)
            {
                return;
            }
        }

        PoolGrowth++;
        if (_poolCount < TrackedBuffers)
        {
            _poolBuffers[_poolCount++] = buffer;
        }
    }

    /// <summary>
    /// Start-up, with the first frame: takes <see cref="PoolFrames"/> − 1 more frames from the decoder's frame pool at
    /// once and gives them back, so the pool holds <see cref="PoolFrames"/> buffers before playback, and remembers them.
    /// </summary>
    private void Grow(AVFrame* frame, ulong first)
    {
        _poolBuffers[_poolCount++] = first;
        var extra = stackalloc nint[PoolFrames - 1];
        var taken = 0;
        try
        {
            for (; taken < PoolFrames - 1; taken++)
            {
                var spare = AvUtil.av_frame_alloc();
                if (spare == null)
                {
                    throw new FileSourceException("av_frame_alloc failed." + " (FFmpeg is out of memory)");
                }

                extra[taken] = (nint)spare;
                var result = AvUtil.av_hwframe_get_buffer(frame->HwFramesCtx, spare, 0);
                if (result < 0)
                {
                    taken++;
                    throw Failure("cannot grow the decoder's CUDA frame pool", result);
                }

                _poolBuffers[_poolCount++] = spare->Data[0];
            }
        }
        finally
        {
            for (var i = 0; i < taken; i++)
            {
                var spare = (AVFrame*)extra[i];
                AvUtil.av_frame_free(&spare);
            }
        }
    }

    private static bool HasCudaHwaccel(void* decoder)
    {
        for (var i = 0; ; i++)
        {
            var config = AvCodec.avcodec_get_hw_config(decoder, i);
            if (config == null)
            {
                return false;
            }

            if (config->DeviceType == Av.HwDeviceTypeCuda && (config->Methods & Av.CodecHwConfigMethodHwDeviceCtx) != 0)
            {
                return true;
            }
        }
    }

    private static void* CodedSideData(AVCodecParameters* parameters, int type)
    {
        var entry = AvCodec.av_packet_side_data_get(parameters->CodedSideData, parameters->NbCodedSideData, type);
        return entry == null ? null : entry->Data;
    }

    private static FrameRate Rate(AVRational r) => r.Num > 0 && r.Den > 0 ? new FrameRate(r.Num, r.Den) : default;

    private static string Utf8(byte* text) => text == null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    private static string FormatName(int format) => format < 0 ? "none" : Utf8(AvUtil.av_get_pix_fmt_name(format));

    /// <summary>The decoder's pixel format choice: NVDEC's CUDA frames, or nothing (no silent software fallback).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetFormat(AVCodecContext* context, int* formats)
    {
        try
        {
            for (var p = formats; *p != Av.PixFmtNone; p++)
            {
                if (*p == Av.PixFmtCuda)
                {
                    return Av.PixFmtCuda;
                }
            }

            if (context->Opaque != null && GCHandle.FromIntPtr((nint)context->Opaque).Target is MediaDecoder self)
            {
                self._hardwareRefused = true;
            }
        }
        catch (Exception)
        {
            // Nothing may escape into native code.
        }

        return Av.PixFmtNone;
    }

    private static FileSourceException Failure(string what, int error)
    {
        var log = FFmpegLibraries.RecentErrors();
        return new FileSourceException($"{what}: {FFmpegLibraries.ErrorText(error)}{(log.Length > 0 ? $" (FFmpeg: {log})" : string.Empty)}");
    }

    private void ThrowDecode(int error, string call)
    {
        if (_hardwareRefused)
        {
            var log = FFmpegLibraries.RecentErrors();
            throw new FileSourceException(
                $"'{Info.Path}': NVDEC cannot decode this {Info.Codec} stream ({(Info.Profile.Length > 0 ? Info.Profile + ", " : string.Empty)}{Info.BitDepth}-bit " +
                $"{Info.Width}x{Info.Height}) on this GPU{(log.Length > 0 ? $" (FFmpeg: {log})" : string.Empty)}");
        }

        throw Failure($"'{Info.Path}': decoding failed in {call}", error);
    }
}

/// <summary>How <see cref="MediaDecoder.Seek"/> went.</summary>
internal enum SeekOutcome
{
    /// <summary>At the keyframe at or before the time asked for.</summary>
    Time,

    /// <summary>At the first byte of the file (an elementary stream, or MPEG-TS/PS): the target is reached by decoding forward from there.</summary>
    Start,

    /// <summary>Neither was possible; nothing changed.</summary>
    Failed,
}
