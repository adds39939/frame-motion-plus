using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameMotionPlus.Audio.PipeWire.Interop;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Audio.PipeWire;

/// <summary>
/// An audio node in the PipeWire graph, as the registry announced it. <see cref="AlsaCard"/> is the card part of an ALSA
/// node's object.path ("alsa:acp:X:0:capture": the card's id, X; some setups give its number): the registry's own
/// properties do not include api.alsa.pcm.card.
/// </summary>
internal sealed record PwNode(uint Id, ulong Serial, string Name, string? Description, string? MediaClass, string? AlsaCard)
{
    public bool IsSink => MediaClass is "Audio/Sink" or "Audio/Duplex";

    /// <summary>A node that plays sound out (a sink of any kind, a duplex device): what a stream may feed only when chosen.</summary>
    public static bool IsOutputDevice(string? mediaClass) =>
        mediaClass is not null && (mediaClass.StartsWith("Audio/Sink", StringComparison.Ordinal) || mediaClass == "Audio/Duplex");

    public bool IsSource => MediaClass is "Audio/Source" or "Audio/Source/Virtual" or "Audio/Duplex";
}

/// <summary>
/// A connection to the PipeWire server: a thread loop (PipeWire's own thread runs the main loop and every non-real-time
/// callback), a context, the core and the registry, which keeps a model of the audio nodes, the links between nodes and
/// the default sink and source (from the "default" metadata). The model is written on the loop thread and read with the
/// loop locked (<see cref="Lock"/>). A connection lost is noticed (<see cref="Lost"/>). Streams are made on it. Dispose
/// tears everything down in PipeWire's order; nothing of it outlives the connection.
/// </summary>
internal sealed unsafe class PwConnection : IDisposable
{
    private static readonly delegate* unmanaged<nint, uint, int, void> CoreDone = &OnCoreDone;
    private static readonly delegate* unmanaged<nint, uint, int, int, byte*, void> CoreError = &OnCoreError;
    private static readonly delegate* unmanaged<nint, uint, uint, byte*, uint, SpaDict*, void> RegistryGlobal = &OnGlobal;
    private static readonly delegate* unmanaged<nint, uint, void> RegistryGlobalRemove = &OnGlobalRemove;
    private static readonly delegate* unmanaged<nint, uint, byte*, byte*, byte*, int> MetadataProperty = &OnMetadataProperty;

    // The model the readers see (loop locked), and during a resync the one a fresh registry is filling (written instead).
    private Model _model = new();
    private Model? _staging;

    // Hooks of registry proxies replaced by a resync: the library may still touch them until the proxy is freed (at the
    // server's remove_id, or the disconnect), so they are freed only at Dispose.
    private readonly List<nint> _retiredHooks = [];
    private readonly RegistryLog? _log;
    private GCHandle _self;
    private nint _loop;
    private nint _context;
    private nint _core;
    private nint _registry;
    private nint _metadata;
    private uint _metadataId = PwDefs.IdAny;
    private SpaHook* _coreHook;
    private SpaHook* _registryHook;
    private SpaHook* _metadataHook;
    private PwCoreEvents* _coreEvents;
    private PwRegistryEvents* _registryEvents;
    private PwMetadataEvents* _metadataEvents;
    private int _doneSeq = -1;
    private int _lost;
    private int _graphVersion;
    private int _handlerErrors;
    private string? _firstHandlerError;
    private bool _hiddenOnce;
    private bool _disposed;

    private PwConnection(int registryLogCapacity)
    {
        _log = registryLogCapacity > 0 ? new RegistryLog(registryLogCapacity) : null;
    }

    /// <summary>
    /// Registry objects whose handling threw (each is missing from the model: a node the guard then does not know), and
    /// the metadata properties likewise. Counted on the loop thread; any thread may read.
    /// </summary>
    public int HandlerErrors => Volatile.Read(ref _handlerErrors);

    /// <summary>The first such failure ("Type: message"), or null.</summary>
    public string? FirstHandlerError => Volatile.Read(ref _firstHandlerError);

    /// <summary>Times the model was rebuilt from a fresh registry (<see cref="ResyncRegistryLocked"/>).</summary>
    public int Resyncs { get; private set; }

    /// <summary>
    /// Tests: the node of this name is left out of the model on its first announcement only, as if that announcement had
    /// been lost (a resync's fresh registry announces it). Set before the node appears.
    /// </summary>
    internal string? HideNodeOnceForTests { get; set; }

    /// <summary>Tests: the node of this name is never put in the model (it stays not announced, resync or not).</summary>
    internal string? HideNodeAlwaysForTests { get; set; }

    /// <summary>The loop's thread handle (for streams).</summary>
    internal nint Core => _core;

    /// <summary>The connection to the server was lost (the server stopped or restarted).</summary>
    public bool Lost => Volatile.Read(ref _lost) != 0;

    public string? LostReason { get; private set; }

    /// <summary>Bumped on every node, link or default change (read with the loop locked).</summary>
    public int GraphVersion => Volatile.Read(ref _graphVersion);

    /// <summary>The default sink's node name (WirePlumber's current choice, which can fall back on its own), or null; loop locked.</summary>
    public string? DefaultSink { get; private set; }

    /// <summary>The default sink the user configured (default.configured.audio.sink), or null; loop locked.</summary>
    public string? DefaultConfiguredSink { get; private set; }

    public string? DefaultSource { get; private set; }

    /// <summary>
    /// Connects as <paramref name="clientName"/> and waits (bounded) until the registry has announced what exists. False
    /// with one line when the library or the server is not there.
    /// </summary>
    /// <param name="registryLogCapacity">
    /// Diagnostics (tests): keep the last this many registry events (globals announced and removed, resyncs, handler
    /// errors) in memory, for <see cref="RegistryLogLocked"/>. 0: none (the app's connections).
    /// </param>
    public static bool TryConnect(string clientName, [NotNullWhen(true)] out PwConnection? connection, [NotNullWhen(false)] out string? reason, int timeoutMs = 3000, int registryLogCapacity = 0)
    {
        connection = null;
        if (!PwLibrary.IsAvailable(out reason))
        {
            return false;
        }

        var c = new PwConnection(registryLogCapacity);
        try
        {
            reason = c.Open(clientName, timeoutMs);
            if (reason is not null)
            {
                c.Dispose();
                return false;
            }

            connection = c;
            return true;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private string? Open(string clientName, int timeoutMs)
    {
        _self = GCHandle.Alloc(this);
        var name = NativeText.Alloc(clientName);
        try
        {
            _loop = PwNative.ThreadLoopNew(name, null);
        }
        finally
        {
            NativeMemory.Free(name);
        }

        if (_loop == 0)
        {
            return "cannot create a PipeWire thread loop";
        }

        _context = PwNative.ContextNew(PwNative.ThreadLoopGetLoop(_loop), 0, 0);
        if (_context == 0)
        {
            return $"cannot create a PipeWire context (errno {Marshal.GetLastSystemError()})";
        }

        if (PwNative.ThreadLoopStart(_loop) < 0)
        {
            return "cannot start the PipeWire thread loop";
        }

        using var scope = Lock();
        using (var props = new NativeDict([new(PwKeys.AppName, "fmp")]))
        {
            _core = PwNative.ContextConnect(_context, props.ToProperties(), 0);
        }

        if (_core == 0)
        {
            return "no PipeWire server to connect to (is PipeWire running for this user?)";
        }

        _coreHook = AllocZeroed<SpaHook>();
        _coreEvents = AllocZeroed<PwCoreEvents>();
        _coreEvents->Version = PwDefs.VersionCoreEvents;
        _coreEvents->Done = (nint)CoreDone;
        _coreEvents->Error = (nint)CoreError;
        _ = PwNative.CoreAddListener(_core, _coreHook, _coreEvents, GCHandle.ToIntPtr(_self));

        _registry = PwNative.CoreGetRegistry(_core, PwDefs.VersionRegistry, 0);
        if (_registry == 0)
        {
            return "the PipeWire server gave no registry";
        }

        _registryHook = AllocZeroed<SpaHook>();
        _registryEvents = AllocZeroed<PwRegistryEvents>();
        _registryEvents->Version = PwDefs.VersionRegistryEvents;
        _registryEvents->Global = (nint)RegistryGlobal;
        _registryEvents->GlobalRemove = (nint)RegistryGlobalRemove;
        _ = PwNative.RegistryAddListener(_registry, _registryHook, _registryEvents, GCHandle.ToIntPtr(_self));

        // Two round trips: the registry's globals, then what binding the metadata brought (the defaults).
        if (!RoundtripLocked(timeoutMs) || !RoundtripLocked(timeoutMs))
        {
            return Lost ? $"the PipeWire server closed the connection ({LostReason})" : "the PipeWire server did not answer within the time allowed";
        }

        return null;
    }

    /// <summary>Locks the thread loop (no callback of the main loop runs while held).</summary>
    public LoopLock Lock() => new(_loop);

    /// <summary>A round trip to the server (everything sent before it has been processed); with the loop locked.</summary>
    public bool RoundtripLocked(int timeoutMs)
    {
        var seq = PwNative.CoreSync(_core, PwDefs.IdCore, 0);
        var deadline = Monotonic.NowNs + (timeoutMs * 1_000_000L);
        while (Volatile.Read(ref _doneSeq) < seq && !Lost)
        {
            if (Monotonic.NowNs > deadline)
            {
                return false;
            }

            _ = PwNative.ThreadLoopTimedWait(_loop, 1);
        }

        return !Lost;
    }

    /// <summary>
    /// Rebuilds the model from a fresh registry proxy: the old one is destroyed, a new one announces every object that
    /// exists now, and a round trip (bounded by <paramref name="timeoutMs"/>; the loop runs while this waits) proves it has
    /// announced them all before the new model replaces the old one (readers see the old one until then). What a lost or
    /// mishandled announcement left out of the model is in it again. False when the server did not answer in time (the
    /// new model is used anyway and fills in as the late announcements arrive) or there is no server. Loop locked; never
    /// on the loop thread.
    /// </summary>
    public bool ResyncRegistryLocked(int timeoutMs)
    {
        if (_registry == 0 || _core == 0 || Lost)
        {
            return false;
        }

        _log?.Add("resync: a fresh registry proxy");
        PwNative.ProxyDestroy(_registry);
        _retiredHooks.Add((nint)_registryHook);
        _registryHook = AllocZeroed<SpaHook>();
        _staging = new Model();
        _registry = PwNative.CoreGetRegistry(_core, PwDefs.VersionRegistry, 0);
        var ok = _registry != 0;
        if (ok)
        {
            _ = PwNative.RegistryAddListener(_registry, _registryHook, _registryEvents, GCHandle.ToIntPtr(_self));
            ok = RoundtripLocked(timeoutMs);
        }

        // Even when it timed out the fresh registry is the only one now: its late announcements land in this model.
        _model = _staging;
        _staging = null;
        Resyncs++;
        _log?.Add(ok ? $"resync: done, {_model.AllNodes.Count} nodes, {_model.Links.Count} links" : "resync: NOT caught up in time");
        Changed();
        return ok;
    }

    /// <summary>The registry events kept (<see cref="TryConnect"/>'s registryLogCapacity), oldest first; loop locked.</summary>
    public List<string> RegistryLogLocked() => _log?.Lines() ?? [];

    /// <summary>The audio nodes now (loop locked).</summary>
    public List<PwNode> NodesLocked() => [.. _model.Nodes.Values.OrderBy(n => n.Id)];

    public PwNode? FindNodeLocked(string name) => _model.Nodes.Values.FirstOrDefault(n => n.Name == name);

    public PwNode? NodeLocked(uint id) => _model.Nodes.GetValueOrDefault(id);

    /// <summary>The nodes linked to <paramref name="node"/>'s inputs (sources feeding it), loop locked.</summary>
    public List<uint> PeersIntoLocked(uint node) => [.. _model.Links.Values.Where(l => l.Input == node).Select(l => l.Output).Distinct()];

    /// <summary>The nodes <paramref name="node"/>'s outputs are linked to (where its sound goes), loop locked.</summary>
    public List<uint> PeersOutOfLocked(uint node) => [.. _model.Links.Values.Where(l => l.Output == node).Select(l => l.Input).Distinct()];

    /// <summary>Any node the registry announced (a stream too): its name and media.class; false when none has that id. Loop locked.</summary>
    public bool TryNodeKindLocked(uint id, out string name, out string? mediaClass)
    {
        if (_model.AllNodes.TryGetValue(id, out var k))
        {
            (name, mediaClass) = k;
            return true;
        }

        (name, mediaClass) = ($"node {id}", null);
        return false;
    }

    /// <summary>Creates a node from a factory (tests: a null sink); owned by this connection. Loop locked.</summary>
    public nint CreateNodeLocked(string factory, IReadOnlyList<KeyValuePair<string, string>> properties)
    {
        var f = NativeText.Alloc(factory);
        var t = NativeText.Alloc(PwKeys.TypeNode);
        try
        {
            using var props = new NativeDict(properties);
            return PwNative.CoreCreateObject(_core, f, t, PwDefs.VersionNode, props.Dict, 0);
        }
        finally
        {
            NativeMemory.Free(f);
            NativeMemory.Free(t);
        }
    }

    public static void DestroyProxyLocked(nint proxy)
    {
        if (proxy != 0)
        {
            PwNative.ProxyDestroy(proxy);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_loop != 0)
        {
            PwNative.ThreadLoopLock(_loop);
            if (_metadata != 0)
            {
                PwNative.ProxyDestroy(_metadata);
                _metadata = 0;
            }

            if (_registry != 0)
            {
                PwNative.ProxyDestroy(_registry);
                _registry = 0;
            }

            if (_core != 0)
            {
                _ = PwNative.CoreDisconnect(_core);
                _core = 0;
            }

            PwNative.ThreadLoopUnlock(_loop);
            PwNative.ThreadLoopStop(_loop);
        }

        if (_context != 0)
        {
            PwNative.ContextDestroy(_context);
            _context = 0;
        }

        if (_loop != 0)
        {
            PwNative.ThreadLoopDestroy(_loop);
            _loop = 0;
        }

        Free(ref _coreHook);
        Free(ref _registryHook);
        Free(ref _metadataHook);
        Free(ref _coreEvents);
        Free(ref _registryEvents);
        Free(ref _metadataEvents);
        foreach (var hook in _retiredHooks)
        {
            NativeMemory.Free((void*)hook);
        }

        _retiredHooks.Clear();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    internal static T* AllocZeroed<T>()
        where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));

    internal static void Free<T>(ref T* p)
        where T : unmanaged
    {
        NativeMemory.Free(p);
        p = null;
    }

    private static PwConnection? From(nint data) => data == 0 ? null : GCHandle.FromIntPtr(data).Target as PwConnection;

    [UnmanagedCallersOnly]
    private static void OnCoreDone(nint data, uint id, int seq)
    {
        if (From(data) is { } c && id == PwDefs.IdCore)
        {
            Volatile.Write(ref c._doneSeq, Math.Max(c._doneSeq, seq));
            PwNative.ThreadLoopSignal(c._loop, false);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnCoreError(nint data, uint id, int seq, int res, byte* message)
    {
        _ = seq;
        if (From(data) is { } c && id == PwDefs.IdCore && res == -32)
        {
            // -EPIPE on the core: the connection is gone.
            c.LostReason = NativeText.ToManaged(message) ?? "connection closed";
            Volatile.Write(ref c._lost, 1);
            PwNative.ThreadLoopSignal(c._loop, false);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnGlobal(nint data, uint id, uint permissions, byte* type, uint version, SpaDict* props)
    {
        _ = permissions;
        _ = version;
        if (From(data) is not { } c)
        {
            return;
        }

        try
        {
            // While a resync runs the fresh registry fills the staging model (the readers keep the old one until it is done).
            var m = c._staging ?? c._model;
            if (NativeText.Equals(type, "PipeWire:Interface:Link"u8))
            {
                if (NativeText.TryParseUInt64(NativeText.Lookup(props, "link.output.node"u8), out var output)
                    && NativeText.TryParseUInt64(NativeText.Lookup(props, "link.input.node"u8), out var input))
                {
                    m.Links[id] = ((uint)output, (uint)input);
                    c._log?.Add($"+{id} Link {output}->{input}");
                    c.Changed();
                }
                else
                {
                    c._log?.Add($"+{id} Link without its nodes");
                }
            }
            else if (NativeText.Equals(type, "PipeWire:Interface:Node"u8))
            {
                var mediaClass = NativeText.ToManaged(NativeText.Lookup(props, "media.class"u8));
                var name = NativeText.ToManaged(NativeText.Lookup(props, "node.name"u8)) ?? $"node {id}";
                if (c.HiddenForTests(name))
                {
                    c._log?.Add($"+{id} Node {name} ({mediaClass}): HIDDEN by the test seam");
                    return;
                }

                m.AllNodes[id] = (name, mediaClass);
                c._log?.Add($"+{id} Node {name} ({mediaClass})");
                if (mediaClass is not null && mediaClass.StartsWith("Audio/", StringComparison.Ordinal))
                {
                    _ = NativeText.TryParseUInt64(NativeText.Lookup(props, "object.serial"u8), out var serial);
                    var card = AlsaCardOf(NativeText.ToManaged(NativeText.Lookup(props, "object.path"u8)));
                    m.Nodes[id] = new PwNode(
                        id,
                        serial,
                        name,
                        NativeText.ToManaged(NativeText.Lookup(props, "node.description"u8)),
                        mediaClass,
                        card);
                    c.Changed();
                }
            }
            else
            {
                if (c._log is { } log)
                {
                    var t = NativeText.ToManaged(type) ?? "?";
                    log.Add($"+{id} {(t.StartsWith("PipeWire:Interface:", StringComparison.Ordinal) ? t[19..] : t)}");
                }

                if (NativeText.Equals(type, "PipeWire:Interface:Metadata"u8) && c._metadata == 0
                    && NativeText.Equals(NativeText.Lookup(props, "metadata.name"u8), "default"u8))
                {
                    c.BindMetadata(id);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never let an exception cross into PipeWire's thread; the model misses this one object. Counted, and the first
            // is kept for the supervisor to say once (outside the loop lock); the guard looks again before cutting for a
            // node it does not know.
            c.HandlerFailed(e, id);
        }
    }

    private bool HiddenForTests(string name)
    {
        if (HideNodeAlwaysForTests == name)
        {
            return true;
        }

        if (_staging is null && !_hiddenOnce && HideNodeOnceForTests == name)
        {
            _hiddenOnce = true;
            return true;
        }

        return false;
    }

    private void HandlerFailed(Exception e, uint id)
    {
        try
        {
            _ = Interlocked.Increment(ref _handlerErrors);
            if (Volatile.Read(ref _firstHandlerError) is null)
            {
                Volatile.Write(ref _firstHandlerError, $"{e.GetType().Name}: {e.Message} (object {id})");
            }

            _log?.Add($"!{id} handler threw {e.GetType().Name}: {e.Message}");
        }
        catch (Exception again) when (again is not OutOfMemoryException)
        {
            // Counted at least; nothing may reach PipeWire's thread.
        }
    }

    /// <summary>The card part of an ALSA object path ("alsa:acp:X:0:capture" → "X", "alsa:pcm:3:..." → "3"), or null.</summary>
    internal static string? AlsaCardOf(string? objectPath)
    {
        if (objectPath is null || !objectPath.StartsWith("alsa:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = objectPath.Split(':');
        return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
    }

    private void BindMetadata(uint id)
    {
        var t = NativeText.Alloc(PwKeys.TypeMetadata);
        try
        {
            _metadata = PwNative.RegistryBind(_registry, id, t, PwDefs.VersionMetadata, 0);
        }
        finally
        {
            NativeMemory.Free(t);
        }

        if (_metadata == 0)
        {
            return;
        }

        _metadataId = id;
        _metadataHook = AllocZeroed<SpaHook>();
        _metadataEvents = AllocZeroed<PwMetadataEvents>();
        _metadataEvents->Version = PwDefs.VersionMetadataEvents;
        _metadataEvents->Property = (nint)MetadataProperty;

        // pw_metadata_add_listener is an inline in the extension header; on a client proxy it is this exported call.
        PwNative.ProxyAddObjectListener(_metadata, _metadataHook, _metadataEvents, GCHandle.ToIntPtr(_self));
    }

    [UnmanagedCallersOnly]
    private static void OnGlobalRemove(nint data, uint id)
    {
        if (From(data) is not { } c)
        {
            return;
        }

        var m = c._staging ?? c._model;
        var changed = m.Links.Remove(id) | m.Nodes.Remove(id) | m.AllNodes.Remove(id);
        try
        {
            c._log?.Add($"-{id}{(changed ? "" : " (not in the model)")}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Diagnostics only.
        }

        if (id == c._metadataId && c._metadata != 0)
        {
            PwNative.ProxyDestroy(c._metadata);
            c._metadata = 0;
            c._metadataId = PwDefs.IdAny;
        }

        if (changed)
        {
            c.Changed();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnMetadataProperty(nint data, uint subject, byte* key, byte* type, byte* value)
    {
        _ = type;
        if (From(data) is not { } c || subject != 0)
        {
            return 0;
        }

        try
        {
            if (key == null)
            {
                c.DefaultSink = null;
                c.DefaultConfiguredSink = null;
                c.DefaultSource = null;
                c.Changed();
            }
            else if (NativeText.Equals(key, "default.audio.sink"u8))
            {
                c.DefaultSink = NativeText.JsonName(NativeText.ToManaged(value));
                c.Changed();
            }
            else if (NativeText.Equals(key, "default.configured.audio.sink"u8))
            {
                c.DefaultConfiguredSink = NativeText.JsonName(NativeText.ToManaged(value));
                c.Changed();
            }
            else if (NativeText.Equals(key, "default.audio.source"u8))
            {
                c.DefaultSource = NativeText.JsonName(NativeText.ToManaged(value));
                c.Changed();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.HandlerFailed(e, subject);
        }

        return 0;
    }

    private void Changed() => Interlocked.Increment(ref _graphVersion);

    /// <summary>What the registry announced: audio nodes, every node (streams too) and the links, by id.</summary>
    private sealed class Model
    {
        public Dictionary<uint, PwNode> Nodes { get; } = [];

        // Every node the registry announced (streams too), by id: its name and media.class (null when it has none).
        public Dictionary<uint, (string Name, string? MediaClass)> AllNodes { get; } = [];

        public Dictionary<uint, (uint Output, uint Input)> Links { get; } = [];
    }

    /// <summary>A bounded ring of registry events (diagnostics, tests); written and read with the loop locked.</summary>
    private sealed class RegistryLog(int capacity)
    {
        private readonly string[] _lines = new string[capacity];
        private long _count;

        public void Add(string line)
        {
            _lines[_count % _lines.Length] = string.Create(CultureInfo.InvariantCulture, $"{Monotonic.NowNs / 1e6:0.0} ms {line}");
            _count++;
        }

        public List<string> Lines()
        {
            var first = Math.Max(0, _count - _lines.Length);
            var list = new List<string>((int)(_count - first));
            for (var i = first; i < _count; i++)
            {
                list.Add(_lines[i % _lines.Length]);
            }

            return list;
        }
    }

    /// <summary>The thread loop's lock, released on dispose.</summary>
    internal readonly struct LoopLock : IDisposable
    {
        private readonly nint _loop;

        public LoopLock(nint loop)
        {
            _loop = loop;
            if (loop != 0)
            {
                PwNative.ThreadLoopLock(loop);
            }
        }

        public void Dispose()
        {
            if (_loop != 0)
            {
                PwNative.ThreadLoopUnlock(_loop);
            }
        }
    }
}
