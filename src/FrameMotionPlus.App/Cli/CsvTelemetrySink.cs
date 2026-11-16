using FrameMotionPlus.Core.Telemetry;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// Telemetry path used by the simulator: records go through the lock-free telemetry ring and are drained to
/// CSV in batches. The live pipeline will drain the same ring from a background thread instead.
/// </summary>
internal sealed class CsvTelemetrySink : ITelemetrySink, IDisposable
{
    private readonly TelemetryRing _ring = new(4096);
    private readonly TelemetryCsvWriter _writer;

    public CsvTelemetrySink(string path)
    {
        _writer = new TelemetryCsvWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        _writer.WriteHeader();
    }

    public long RecordsWritten => _writer.RecordsWritten;

    public void Write(in FrameTimelineRecord record)
    {
        if (_ring.Count >= _ring.Capacity / 2)
        {
            _ring.DrainTo(_writer);
        }

        _ring.TryWrite(record);
    }

    public void Dispose()
    {
        _ring.DrainTo(_writer);
        _writer.Dispose();
    }
}
