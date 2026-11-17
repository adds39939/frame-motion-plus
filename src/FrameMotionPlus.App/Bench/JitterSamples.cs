namespace FrameMotionPlus.App.Bench;

/// <summary>
/// Preallocated per-slot samples of one loop kind (timer only, or full), written only by the pacing thread. The arrays
/// are touched at construction so the measured loop takes no page faults on them.
/// </summary>
internal sealed class JitterSamples
{
    public JitterSamples(string name, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Name = name;
        Deadline = new long[capacity];
        Wake = new long[capacity];
        End = new long[capacity];
        Target = new long[capacity];
        Kind = new byte[capacity];
        Reason = new byte[capacity];
        Measured = new byte[capacity];
        Skipped = new int[capacity];
        Block = new int[capacity];
        Array.Fill(Deadline, -1);
        Array.Fill(Wake, -1);
        Array.Fill(End, -1);
        Array.Fill(Target, -1);
        Array.Fill(Kind, (byte)0xFF);
        Array.Fill(Reason, (byte)0xFF);
        Array.Fill(Measured, (byte)0xFF);
        Array.Fill(Skipped, -1);
        Array.Fill(Block, -1);
    }

    public string Name { get; }

    public long[] Deadline { get; }

    public long[] Wake { get; }

    public long[] End { get; }

    /// <summary>Target display time V of the slot served (timer-only loop: the grid instant the deadline leads).</summary>
    public long[] Target { get; }

    public byte[] Kind { get; }

    public byte[] Reason { get; }

    /// <summary>1 when the slot counts in the statistics; 0 for warm-up and the settling time after a block switch.</summary>
    public byte[] Measured { get; }

    /// <summary>Slots passed over before this one was served because the loop came too late for them.</summary>
    public int[] Skipped { get; }

    /// <summary>Index of the block in the run's interleaved block plan.</summary>
    public int[] Block { get; }

    public int Count { get; private set; }

    /// <summary>Slots that did not fit (the capacity was sized wrongly); must be 0.</summary>
    public long Overflow { get; private set; }

    public int Capacity => Deadline.Length;

    public void Add(long deadline, long wake, long end, long target, byte kind, byte reason, long skipped, bool measured, int block)
    {
        var i = Count;
        if (i >= Deadline.Length)
        {
            Overflow++;
            return;
        }

        Deadline[i] = deadline;
        Wake[i] = wake;
        End[i] = end;
        Target[i] = target;
        Kind[i] = kind;
        Reason[i] = reason;
        Skipped[i] = (int)Math.Min(skipped, int.MaxValue);
        Measured[i] = measured ? (byte)1 : (byte)0;
        Block[i] = block;
        Count = i + 1;
    }
}

/// <summary>One stretch of the run given to one loop kind. Slots with a deadline before <see cref="MeasuredFromNs"/>
/// are warm-up (the first block of each kind) or settling after the switch, and are not measured.</summary>
internal readonly record struct BenchBlock(bool Full, long StartNs, long MeasuredFromNs, long StopNs);
