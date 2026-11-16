using System.Globalization;
using System.Text;

namespace FrameMotionPlus.Core.Telemetry;

/// <summary>
/// Writes timeline records as CSV (invariant culture, LF line endings) straight to a stream through a
/// reusable UTF-8 buffer, so a record costs no allocations.
/// </summary>
public sealed class TelemetryCsvWriter : IDisposable
{
    public const string Header =
        "slot,kind,reason,frame_a,frame_b,timestep,target_display_ns,actual_display_ns,ideal_content_ns,content_ns," +
        "pipeline_delay_ns,multiplier,capture_ns,dequeue_ns,upload_done_ns,convert_done_ns,arrival_ns,decision_ns," +
        "generation_start_ns,generation_end_ns,present_submit_ns,missed";

    private const int MaxRecordBytes = 512;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer;
    private int _used;
    private bool _disposed;

    public TelemetryCsvWriter(Stream stream, bool leaveOpen = false, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, MaxRecordBytes);
        _stream = stream;
        _leaveOpen = leaveOpen;
        _buffer = new byte[bufferSize];
    }

    public long RecordsWritten { get; private set; }

    public void WriteHeader()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "\n");
        Flush();
        _stream.Write(bytes);
    }

    public void Write(in FrameTimelineRecord r)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_buffer.Length - _used < MaxRecordBytes)
        {
            FlushBuffer();
        }

        var span = _buffer.AsSpan(_used);
        var pos = 0;
        Long(span, ref pos, r.SlotIndex);
        Text(span, ref pos, KindName(r.Kind));
        Text(span, ref pos, ReasonName(r.Reason));
        Long(span, ref pos, r.FrameA);
        Long(span, ref pos, r.FrameB);
        Double(span, ref pos, r.Timestep);
        Long(span, ref pos, r.TargetDisplayNs);
        Long(span, ref pos, r.ActualDisplayNs);
        Long(span, ref pos, r.IdealContentNs);
        Long(span, ref pos, r.ContentNs);
        Long(span, ref pos, r.PipelineDelayNs);
        Long(span, ref pos, r.Multiplier);
        Long(span, ref pos, r.CaptureNs);
        Long(span, ref pos, r.DequeueNs);
        Long(span, ref pos, r.UploadDoneNs);
        Long(span, ref pos, r.ConvertDoneNs);
        Long(span, ref pos, r.ArrivalNs);
        Long(span, ref pos, r.DecisionNs);
        Long(span, ref pos, r.GenerationStartNs);
        Long(span, ref pos, r.GenerationEndNs);
        Long(span, ref pos, r.PresentSubmitNs);
        span[pos++] = r.Missed ? (byte)'1' : (byte)'0';
        span[pos++] = (byte)'\n';
        _used += pos;
        RecordsWritten++;
    }

    public void Flush()
    {
        FlushBuffer();
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushBuffer();
        _stream.Flush();
        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private void FlushBuffer()
    {
        if (_used > 0)
        {
            _stream.Write(_buffer, 0, _used);
            _used = 0;
        }
    }

    private static void Long(Span<byte> span, ref int pos, long value)
    {
        value.TryFormat(span[pos..], out var written, default, CultureInfo.InvariantCulture);
        pos += written;
        span[pos++] = (byte)',';
    }

    private static void Double(Span<byte> span, ref int pos, double value)
    {
        value.TryFormat(span[pos..], out var written, "0.#########", CultureInfo.InvariantCulture);
        pos += written;
        span[pos++] = (byte)',';
    }

    private static void Text(Span<byte> span, ref int pos, ReadOnlySpan<byte> utf8)
    {
        utf8.CopyTo(span[pos..]);
        pos += utf8.Length;
        span[pos++] = (byte)',';
    }

    // Every defined value is listed; an unlisted one writes an empty token, which TelemetryTests catches.
    private static ReadOnlySpan<byte> KindName(Scheduling.SlotKind kind) => kind switch
    {
        Scheduling.SlotKind.Empty => "empty"u8,
        Scheduling.SlotKind.Real => "real"u8,
        Scheduling.SlotKind.Generated => "generated"u8,
        Scheduling.SlotKind.Repeat => "repeat"u8,
        _ => ""u8,
    };

    /// <summary>The CSV token for a reason; <see cref="Scheduling.SlotReason.None"/> is the empty token.</summary>
    private static ReadOnlySpan<byte> ReasonName(Scheduling.SlotReason reason) => reason switch
    {
        Scheduling.SlotReason.None => ""u8,
        Scheduling.SlotReason.Startup => "startup"u8,
        Scheduling.SlotReason.Starved => "starved"u8,
        Scheduling.SlotReason.Hold => "hold"u8,
        Scheduling.SlotReason.Discontinuity => "discontinuity"u8,
        Scheduling.SlotReason.Fallback => "fallback"u8,
        Scheduling.SlotReason.Late => "late"u8,
        _ => ""u8,
    };
}
