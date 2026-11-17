using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameMotionPlus.Sources.File.Interop;

/// <summary>
/// Loads the system's FFmpeg 9 shared libraries by soname (<c>libavutil.so.61</c>, <c>libavcodec.so.63</c>,
/// <c>libavformat.so.63</c>), or from <c>$FMP_FFMPEG_DIR</c> by absolute path, and binds the logical names the
/// <see cref="AvUtil"/>, <see cref="AvCodec"/> and <see cref="AvFormat"/> imports use to them. After loading it checks
/// each library's major version (the struct offsets in <c>AvTypes.cs</c> are those of that major) and refuses a
/// mismatch with a message naming what was found. The outcome is final for the process (libraries are never unloaded).
/// Nothing is loaded until something asks: an application without FFmpeg starts and runs, and only file playback reports
/// itself unavailable, with the reason.
/// <para>
/// FFmpeg's own log is captured instead of printed: lines at error level or worse go to a small ring
/// (<see cref="RecentErrors"/>) that error messages quote; nothing reaches stderr.
/// </para>
/// </summary>
internal static class FFmpegLibraries
{
    /// <summary>The environment variable naming a directory to load the three libraries from instead of the system's.</summary>
    public const string DirectoryVariable = "FMP_FFMPEG_DIR";

    private const int LogLineBytes = 512;
    private const int LogLines = 8;

    private static readonly Lock _gate = new();
    private static readonly Lock _logGate = new();
    private static readonly bool _resolverRegistered = Register();
    private static readonly byte[] _log = new byte[LogLines * LogLineBytes];
    private static int _logNext;
    private static int _logCount;
    private static bool _attempted;
    private static string? _failure;
    private static nint _avutil;
    private static nint _avcodec;
    private static nint _avformat;

    /// <summary>The FFmpeg version this build was checked against (struct offsets, constants, prototypes).</summary>
    public static string ExpectedVersion => $"FFmpeg 9 (libavcodec {Av.AvCodecMajor}, libavformat {Av.AvFormatMajor}, libavutil {Av.AvUtilMajor})";

    /// <summary>The library versions found, as "avcodec 63.1.102, ...", once loaded.</summary>
    public static string? LoadedVersions { get; private set; }

    /// <summary>The files loaded (sonames or paths).</summary>
    public static IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>Loads and checks the libraries once. False, with the reason, when FFmpeg 9 is not usable here.</summary>
    public static bool TryLoad(out string? reason)
    {
        _ = _resolverRegistered;
        lock (_gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                _failure = Load();
            }

            reason = _failure;
            return _failure is null;
        }
    }

    /// <summary>Loads the libraries or throws <see cref="FFmpegUnavailableException"/> with the reason.</summary>
    public static void EnsureLoaded()
    {
        if (!TryLoad(out var reason))
        {
            throw new FFmpegUnavailableException(reason!);
        }
    }

    /// <summary>The most recent FFmpeg error-level log lines, oldest first, joined (empty when there were none).</summary>
    public static string RecentErrors()
    {
        lock (_logGate)
        {
            var builder = new StringBuilder();
            var count = Math.Min(_logCount, LogLines);
            for (var i = 0; i < count; i++)
            {
                var index = (_logNext - count + i + LogLines) % LogLines;
                var span = _log.AsSpan(index * LogLineBytes, LogLineBytes);
                var length = span.IndexOf((byte)0);
                var line = Encoding.UTF8.GetString(span[..(length < 0 ? span.Length : length)]).Trim();
                if (line.Length > 0)
                {
                    builder.Append(builder.Length > 0 ? "; " : string.Empty).Append(line);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>Forgets the captured log lines (before an operation whose errors are to be quoted).</summary>
    public static void ClearRecentErrors()
    {
        lock (_logGate)
        {
            _logCount = 0;
        }
    }

    /// <summary>FFmpeg's description of an error code (<c>av_strerror</c>).</summary>
    public static unsafe string ErrorText(int error)
    {
        var buffer = stackalloc byte[256];
        if (AvUtil.av_strerror(error, buffer, 256) < 0)
        {
            return $"FFmpeg error {error}";
        }

        return Marshal.PtrToStringUTF8((nint)buffer) ?? $"FFmpeg error {error}";
    }

    /// <summary>Major version of an <c>AV_VERSION_INT</c>.</summary>
    internal static int Major(uint version) => (int)(version >> 16);

    internal static string Format(uint version) => $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    /// <summary>The refusal for a set of found major versions, or null when they are the ones this build was checked against.</summary>
    internal static string? CheckVersions(uint avutil, uint avcodec, uint avformat)
    {
        if (Major(avutil) == Av.AvUtilMajor && Major(avcodec) == Av.AvCodecMajor && Major(avformat) == Av.AvFormatMajor)
        {
            return null;
        }

        return $"found libavutil {Format(avutil)}, libavcodec {Format(avcodec)}, libavformat {Format(avformat)}; this build needs {ExpectedVersion} " +
               "(its structure layouts are those of that version)";
    }

    private static bool Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(FFmpegLibraries).Assembly, Resolve);
        return true;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) => libraryName switch
    {
        AvUtil.Library => _avutil,
        AvCodec.Library => _avcodec,
        AvFormat.Library => _avformat,
        _ => 0,
    };

    private static unsafe string? Load()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            // The views in AvTypes.cs are x86-64 (LP64) offsets, checked against the headers there; another 64-bit ABI
            // (arm64) may lay out the same structures differently.
            return $"file playback is verified on x86-64 Linux only (the FFmpeg structure layouts are checked there; this process is {RuntimeInformation.ProcessArchitecture} on {RuntimeInformation.OSDescription}; Windows is Phase 10)";
        }

        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        string Name(string soname) => string.IsNullOrEmpty(directory) ? soname : Path.Combine(directory, soname);
        var names = new[] { Name($"libavutil.so.{Av.AvUtilMajor}"), Name($"libavcodec.so.{Av.AvCodecMajor}"), Name($"libavformat.so.{Av.AvFormatMajor}") };
        var handles = new nint[3];
        for (var i = 0; i < names.Length; i++)
        {
            if (!NativeLibrary.TryLoad(names[i], out handles[i]))
            {
                var where = string.IsNullOrEmpty(directory) ? "the system library path" : $"{directory} (${DirectoryVariable})";
                return $"{Path.GetFileName(names[i])} not found in {where}; install {ExpectedVersion}";
            }
        }

        (_avutil, _avcodec, _avformat) = (handles[0], handles[1], handles[2]);
        Files = names;
        var versions = (avutil: AvUtil.avutil_version(), avcodec: AvCodec.avcodec_version(), avformat: AvFormat.avformat_version());
        LoadedVersions = $"libavcodec {Format(versions.avcodec)}, libavformat {Format(versions.avformat)}, libavutil {Format(versions.avutil)}";
        if (CheckVersions(versions.avutil, versions.avcodec, versions.avformat) is { } mismatch)
        {
            (_avutil, _avcodec, _avformat) = (0, 0, 0);
            return mismatch;
        }

        AvUtil.av_log_set_level(Av.LogError);
        AvUtil.av_log_set_callback(&OnLog);
        return null;
    }

    /// <summary>
    /// FFmpeg's log callback (any thread FFmpeg logs from): keeps error-level lines in a fixed ring of native-sized byte
    /// lines. Allocation-free; never throws back into FFmpeg.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLog(void* avcl, int level, byte* format, nint args)
    {
        if (level > Av.LogError)
        {
            return;
        }

        try
        {
            lock (_logGate)
            {
                var printPrefix = 1;
                fixed (byte* line = &_log[_logNext * LogLineBytes])
                {
                    _ = AvUtil.av_log_format_line2(avcl, level, format, args, line, LogLineBytes, &printPrefix);
                }

                _logNext = (_logNext + 1) % LogLines;
                _logCount++;
            }
        }
        catch (Exception)
        {
            // Nothing may escape into native code.
        }
    }
}
