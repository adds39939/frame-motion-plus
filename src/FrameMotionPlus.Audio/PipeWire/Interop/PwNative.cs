using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Audio.PipeWire.Interop;

/// <summary>
/// The libpipewire-0.3 entry points this binding calls: all real exported functions (recent PipeWire, 1.6 here, also
/// exports the core and registry methods that the headers declare inline, such as <c>pw_core_get_registry</c>), so no C
/// shim is needed. Bound to the SONAME <c>libpipewire-0.3.so.0</c> (no -dev symlink needed). Thread rules are PipeWire's: with a
/// thread loop running, everything except the stream's buffer and time calls is made with the loop locked or from the
/// loop's own thread. <see cref="PwLibrary"/> checks the library and every symbol before the first call.
/// </summary>
internal static unsafe partial class PwNative
{
    public const string Library = "pipewire-0.3";

    public const string FileName = "libpipewire-0.3.so.0";

    /// <summary>Every symbol called, for the availability check (a missing one makes audio unavailable, never a crash).</summary>
    public static readonly string[] Symbols =
    [
        "pw_init", "pw_get_library_version", "pw_thread_loop_new", "pw_thread_loop_destroy", "pw_thread_loop_start",
        "pw_thread_loop_stop", "pw_thread_loop_lock", "pw_thread_loop_unlock", "pw_thread_loop_signal", "pw_thread_loop_timed_wait",
        "pw_thread_loop_get_loop", "pw_context_new", "pw_context_destroy", "pw_context_connect", "pw_core_disconnect",
        "pw_core_add_listener", "pw_core_sync", "pw_core_get_registry", "pw_core_create_object", "pw_registry_add_listener",
        "pw_registry_bind", "pw_proxy_destroy", "pw_proxy_add_object_listener", "pw_properties_new_dict", "pw_stream_new",
        "pw_stream_add_listener", "pw_stream_connect", "pw_stream_disconnect", "pw_stream_destroy", "pw_stream_dequeue_buffer",
        "pw_stream_queue_buffer", "pw_stream_get_time_n", "pw_stream_get_node_id", "pw_stream_state_as_string",
    ];

    static PwNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(PwNative).Assembly, Resolve);
    }

    [LibraryImport(Library, EntryPoint = "pw_init")]
    public static partial void Init(int* argc, byte*** argv);

    [LibraryImport(Library, EntryPoint = "pw_get_library_version")]
    public static partial byte* GetLibraryVersion();

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_new")]
    public static partial nint ThreadLoopNew(byte* name, SpaDict* props);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_destroy")]
    public static partial void ThreadLoopDestroy(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_start")]
    public static partial int ThreadLoopStart(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_stop")]
    public static partial void ThreadLoopStop(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_lock")]
    public static partial void ThreadLoopLock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_unlock")]
    public static partial void ThreadLoopUnlock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_signal")]
    public static partial void ThreadLoopSignal(nint loop, [MarshalAs(UnmanagedType.U1)] bool waitForAccept);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_timed_wait")]
    public static partial int ThreadLoopTimedWait(nint loop, int waitMaxSeconds);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_get_loop")]
    public static partial nint ThreadLoopGetLoop(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_context_new")]
    public static partial nint ContextNew(nint mainLoop, nint properties, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_context_destroy")]
    public static partial void ContextDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "pw_context_connect")]
    public static partial nint ContextConnect(nint context, nint properties, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_core_disconnect")]
    public static partial int CoreDisconnect(nint core);

    [LibraryImport(Library, EntryPoint = "pw_core_add_listener")]
    public static partial int CoreAddListener(nint core, SpaHook* listener, PwCoreEvents* events, nint data);

    [LibraryImport(Library, EntryPoint = "pw_core_sync")]
    public static partial int CoreSync(nint core, uint id, int seq);

    [LibraryImport(Library, EntryPoint = "pw_core_get_registry")]
    public static partial nint CoreGetRegistry(nint core, uint version, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_core_create_object")]
    public static partial nint CoreCreateObject(nint core, byte* factoryName, byte* type, uint version, SpaDict* props, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_registry_add_listener")]
    public static partial int RegistryAddListener(nint registry, SpaHook* listener, PwRegistryEvents* events, nint data);

    [LibraryImport(Library, EntryPoint = "pw_registry_bind")]
    public static partial nint RegistryBind(nint registry, uint id, byte* type, uint version, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_proxy_destroy")]
    public static partial void ProxyDestroy(nint proxy);

    [LibraryImport(Library, EntryPoint = "pw_proxy_add_object_listener")]
    public static partial void ProxyAddObjectListener(nint proxy, SpaHook* listener, void* funcs, nint data);

    [LibraryImport(Library, EntryPoint = "pw_properties_new_dict")]
    public static partial nint PropertiesNewDict(SpaDict* dict);

    [LibraryImport(Library, EntryPoint = "pw_stream_new")]
    public static partial nint StreamNew(nint core, byte* name, nint properties);

    [LibraryImport(Library, EntryPoint = "pw_stream_add_listener")]
    public static partial void StreamAddListener(nint stream, SpaHook* listener, PwStreamEvents* events, nint data);

    [LibraryImport(Library, EntryPoint = "pw_stream_connect")]
    public static partial int StreamConnect(nint stream, int direction, uint targetId, int flags, byte** parameters, uint parameterCount);

    [LibraryImport(Library, EntryPoint = "pw_stream_disconnect")]
    public static partial int StreamDisconnect(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_destroy")]
    public static partial void StreamDestroy(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_dequeue_buffer")]
    public static partial PwBuffer* StreamDequeueBuffer(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_queue_buffer")]
    public static partial int StreamQueueBuffer(nint stream, PwBuffer* buffer);

    [LibraryImport(Library, EntryPoint = "pw_stream_get_time_n")]
    public static partial int StreamGetTimeN(nint stream, PwTime* time, nuint size);

    [LibraryImport(Library, EntryPoint = "pw_stream_get_node_id")]
    public static partial uint StreamGetNodeId(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_state_as_string")]
    public static partial byte* StreamStateAsString(int state);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && OperatingSystem.IsLinux() && NativeLibrary.TryLoad(FileName, out var handle) ? handle : 0;
}
