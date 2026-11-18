using System.Globalization;
using System.Text;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Capture;

/// <summary>Writes a capture's frame log (<c>fmp capture --frames-csv</c>): one row per dequeued buffer.</summary>
internal static class CaptureFrameLog
{
    public static void Write(string path, ReadOnlySpan<CaptureFrameRecord> log, FrameRate signal)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("sequence,timestamp_ns,dequeued_ns,judged_ns,interval_ms,usb_delivery_ms,metric,max_cell_mean,estimated_fps,unique,ambiguous,black,published,dropped_behind,stale,error,signal_hz");
        var last = long.MinValue;
        foreach (var r in log)
        {
            var f = r.Flags;
            sb.Append(ic, $"{r.Sequence},{r.TimestampNs},{r.DequeuedNs},{r.JudgedNs},");
            sb.Append(last == long.MinValue ? "" : ((r.TimestampNs - last) / 1e6).ToString("0.000", ic));
            sb.Append(ic, $",{(r.DequeuedNs - r.TimestampNs) / 1e6:0.000},{r.Metric:0.000000},{r.MaxCellMean:0.0},{r.EstimatedFps:0.000},");
            sb.Append(ic, $"{Bit(f, CaptureFrameEvents.Unique)},{Bit(f, CaptureFrameEvents.Ambiguous)},{Bit(f, CaptureFrameEvents.Black)},{Bit(f, CaptureFrameEvents.Published)},");
            sb.Append(ic, $"{Bit(f, CaptureFrameEvents.DroppedBehind)},{Bit(f, CaptureFrameEvents.Stale)},{Bit(f, CaptureFrameEvents.Error)},{signal.Hz:0.###}");
            sb.AppendLine();
            last = r.TimestampNs;
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static int Bit(CaptureFrameEvents flags, CaptureFrameEvents bit) => (flags & bit) != 0 ? 1 : 0;
}
