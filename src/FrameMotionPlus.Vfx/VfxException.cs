using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FrameMotionPlus.Vfx.Interop;

namespace FrameMotionPlus.Vfx;

/// <summary>
/// A VFX SDK call failed. The message names the call, the status and what the SDK logged about it, e.g.
/// "NvVFX_Run: NVCV_ERR_PARAMETER (-7, An invalid parameter value has been supplied for this effect+selector.)".
/// </summary>
public sealed class VfxException : Exception
{
    public VfxException()
        : this("VFX SDK call failed.")
    {
    }

    public VfxException(string message)
        : base(message)
    {
        Status = NvCVStatus.ErrGeneral;
    }

    public VfxException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = NvCVStatus.ErrGeneral;
    }

    public VfxException(NvCVStatus status, string function, string? detail = null)
        : base(VfxErrors.Format(status, function, detail))
    {
        Status = status;
        Function = function;
    }

    public NvCVStatus Status { get; }

    /// <summary>The SDK entry point that failed, e.g. <c>NvVFX_Load</c>.</summary>
    public string? Function { get; }
}

/// <summary>The VFX SDK is not installed where it was looked for, or could not be loaded. The message is one line.</summary>
public sealed class VfxUnavailableException : Exception
{
    public VfxUnavailableException()
        : this("The NVIDIA VFX SDK is not available.")
    {
    }

    public VfxUnavailableException(string message)
        : base(message)
    {
    }

    public VfxUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Status checks: the success path is one compare; formatting happens only on failure.</summary>
internal static class Nv
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Check(NvCVStatus status, string function, long logMark)
    {
        if (status != NvCVStatus.Success)
        {
            Throw(status, function, logMark);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Check(NvCVStatus status, string function, long logMark, string detail)
    {
        if (status != NvCVStatus.Success)
        {
            Throw(status, function, logMark, detail);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Throw(NvCVStatus status, string function, long logMark, string? detail = null)
    {
        var logged = VfxLog.Since(logMark);
        var text = logged.Count == 0 ? detail : $"{(detail is null ? string.Empty : detail + "; ")}SDK log: {string.Join(" | ", logged)}";
        throw new VfxException(status, function, text);
    }
}

internal static unsafe class VfxErrors
{
    /// <summary>The header's name for <paramref name="status"/>: <c>ErrInitialization</c> is <c>NVCV_ERR_INITIALIZATION</c>.</summary>
    internal static string Name(NvCVStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            return $"NvCV_Status {(int)status}";
        }

        var name = status.ToString();
        var builder = new StringBuilder("NVCV_", name.Length + 12);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>The SDK's own description (<c>NvCV_GetErrorStringFromCode</c>), when the SDK is loaded.</summary>
    internal static string? Description(NvCVStatus status)
    {
        if (!VfxLibraries.IsLoaded)
        {
            return null;
        }

        var text = VfxNative.NvCV_GetErrorStringFromCode(status);
        return text == null ? null : Marshal.PtrToStringUTF8((nint)text);
    }

    internal static string Format(NvCVStatus status, string function, string? detail)
    {
        var description = Description(status);
        var text = description is null
            ? $"{function}: {Name(status)} ({(int)status})"
            : $"{function}: {Name(status)} ({(int)status}, {description})";
        return detail is null ? text : $"{text}; {detail}";
    }
}
