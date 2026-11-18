using System.Runtime.InteropServices;
using FrameMotionPlus.Audio.PipeWire.Interop;

namespace FrameMotionPlus.Audio.PipeWire;

/// <summary>
/// One PipeWire stream (F32 stereo at 48 kHz) on a <see cref="PwConnection"/>. Its process callback runs on PipeWire's
/// data thread (<c>PW_STREAM_FLAG_RT_PROCESS</c>; real-time only where the system grants it, which it does not here
/// without RTKit): <see cref="OnProcess"/> must not allocate, lock or block. State changes come on the loop thread.
/// Creating, connecting and destroying are done with the loop locked; <see cref="Dispose"/> destroys the stream (its
/// callbacks have stopped when it returns) and frees the native listener memory.
/// </summary>
internal abstract unsafe class PwStream : IDisposable
{
    public const int Rate = 48_000;
    public const int Channels = 2;
    public const int FrameBytes = Channels * sizeof(float);

    private static readonly delegate* unmanaged<nint, int, int, byte*, void> StateChangedCallback = &OnStateChangedNative;
    private static readonly delegate* unmanaged<nint, void> ProcessCallback = &OnProcessNative;

    private readonly PwConnection _connection;
    private GCHandle _self;
    private SpaHook* _hook;
    private PwStreamEvents* _events;
    private nint _stream;
    private int _state;
    private int _errors;
    private int _faulted;

    /// <param name="properties">Stream properties (media type and category, node name, latency, target...).</param>
    protected PwStream(PwConnection connection, string name, IReadOnlyList<KeyValuePair<string, string>> properties)
    {
        _connection = connection;
        Name = name;
        _self = GCHandle.Alloc(this);
        _hook = PwConnection.AllocZeroed<SpaHook>();
        _events = PwConnection.AllocZeroed<PwStreamEvents>();
        _events->Version = PwDefs.VersionStreamEvents;
        _events->StateChanged = (nint)StateChangedCallback;
        _events->Process = (nint)ProcessCallback;
        var n = NativeText.Alloc(name);
        try
        {
            using var props = new NativeDict(properties);
            using var scope = connection.Lock();
            _stream = PwNative.StreamNew(connection.Core, n, props.ToProperties());
            if (_stream == 0)
            {
                PwConnection.Free(ref _hook);
                PwConnection.Free(ref _events);
                _self.Free();
                throw new InvalidOperationException($"pw_stream_new failed for {name}");
            }

            PwNative.StreamAddListener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
        }
        finally
        {
            NativeMemory.Free(n);
        }
    }

    public string Name { get; }

    protected nint Handle => _stream;

    /// <summary>The stream's state (pw_stream_state), as last reported.</summary>
    public int State => Volatile.Read(ref _state);

    /// <summary>The last error the stream reported (state "error"), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The exception that stopped the process callback, if one did (a bug: reported, never a crash).</summary>
    public Exception? Fault { get; private set; }

    /// <summary>How many times it went to the error state.</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>The stream's node id once connected (PW_ID_ANY before); loop locked.</summary>
    public uint NodeIdLocked() => _stream == 0 ? PwDefs.IdAny : PwNative.StreamGetNodeId(_stream);

    /// <summary>Connects with the F32 stereo 48 kHz format; <paramref name="capture"/> for an input stream. Loop locked inside.</summary>
    public int Connect(bool capture, int extraFlags = 0)
    {
        var pod = SpaFormatPod.AudioRawF32(Rate);
        fixed (byte* p = pod)
        {
            var parameters = stackalloc byte*[1];
            parameters[0] = p;
            using var scope = _connection.Lock();
            return PwNative.StreamConnect(
                _stream,
                capture ? PwDefs.DirectionInput : PwDefs.DirectionOutput,
                PwDefs.IdAny,
                PwDefs.StreamFlagAutoconnect | PwDefs.StreamFlagMapBuffers | PwDefs.StreamFlagRtProcess | extraFlags,
                parameters,
                1);
        }
    }

    /// <summary>The stream's timing now (data thread or loop locked); false when it is not streaming.</summary>
    protected bool TryGetTime(out PwTime time)
    {
        PwTime t;
        var ok = PwNative.StreamGetTimeN(_stream, &t, (nuint)sizeof(PwTime)) == 0;
        time = t;
        return ok;
    }

    protected PwBuffer* Dequeue() => PwNative.StreamDequeueBuffer(_stream);

    protected void Queue(PwBuffer* buffer) => _ = PwNative.StreamQueueBuffer(_stream, buffer);

    /// <summary>Data thread: one graph cycle.</summary>
    protected abstract void OnProcess();

    /// <summary>Loop thread: the state changed (see <see cref="State"/>).</summary>
    protected virtual void OnStateChanged(int previous, int state)
    {
    }

    public void Dispose()
    {
        if (_stream != 0)
        {
            using (_connection.Lock())
            {
                PwNative.StreamDestroy(_stream);
            }

            _stream = 0;
        }

        PwConnection.Free(ref _hook);
        PwConnection.Free(ref _events);
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        GC.SuppressFinalize(this);
    }

    private static PwStream? From(nint data) => data == 0 ? null : GCHandle.FromIntPtr(data).Target as PwStream;

    [UnmanagedCallersOnly]
    private static void OnStateChangedNative(nint data, int previous, int state, byte* error)
    {
        if (From(data) is not { } s)
        {
            return;
        }

        try
        {
            if (state == PwDefs.StreamStateError)
            {
                s.Error = NativeText.ToManaged(error) ?? "error";
                _ = Interlocked.Increment(ref s._errors);
            }

            Volatile.Write(ref s._state, state);
            s.OnStateChanged(previous, state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    [UnmanagedCallersOnly]
    private static void OnProcessNative(nint data)
    {
        // An exception must never cross into PipeWire's thread (it would end the process): the stream is marked faulted
        // and processes nothing more. A try block costs nothing until something throws.
        if (From(data) is not { } s || s._faulted != 0)
        {
            return;
        }

        try
        {
            s.OnProcess();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            s.Fault = e;
            Volatile.Write(ref s._faulted, 1);
        }
    }
}
