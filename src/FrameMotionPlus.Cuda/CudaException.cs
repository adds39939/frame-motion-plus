using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>A CUDA driver call failed. The message names the call and the CUDA error, e.g. "cuMemAlloc_v2: CUDA_ERROR_OUT_OF_MEMORY (out of memory)".</summary>
public sealed class CudaException : Exception
{
    public CudaException()
        : this("CUDA driver call failed.")
    {
    }

    public CudaException(string message)
        : base(message)
    {
        Result = CudaResult.ErrorUnknown;
        ErrorName = CudaErrors.Name(Result);
    }

    public CudaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Result = CudaResult.ErrorUnknown;
        ErrorName = CudaErrors.Name(Result);
    }

    public CudaException(CudaResult result, string function, string? detail = null)
        : base(CudaErrors.Format(result, function, detail))
    {
        Result = result;
        Function = function;
        ErrorName = CudaErrors.Name(result);
    }

    public CudaResult Result { get; }

    /// <summary>The driver entry point that failed, e.g. <c>cuModuleLoadDataEx</c>.</summary>
    public string? Function { get; }

    /// <summary>The CUDA name of <see cref="Result"/>, e.g. <c>CUDA_ERROR_INVALID_PTX</c>.</summary>
    public string ErrorName { get; }
}

/// <summary>Result checks for driver calls. The success path is one compare; formatting happens only on failure.</summary>
internal static class Cu
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Check(CudaResult result, string function)
    {
        if (result != CudaResult.Success)
        {
            Throw(result, function);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Throw(CudaResult result, string function, string? detail = null) =>
        throw new CudaException(result, function, detail);
}

internal static unsafe class CudaErrors
{
    /// <summary>The driver's name for <paramref name="result"/>, or one derived from the enum when the driver cannot say.</summary>
    internal static string Name(CudaResult result)
    {
        if (CudaLibrary.TryLoad(out _))
        {
            byte* name = null;
            if (CuNative.cuGetErrorName(result, &name) == CudaResult.Success && name != null)
            {
                return Marshal.PtrToStringUTF8((nint)name) ?? FallbackName(result);
            }
        }

        return FallbackName(result);
    }

    internal static string Format(CudaResult result, string function, string? detail)
    {
        var description = Description(result);
        var text = description is null
            ? $"{function}: {Name(result)} ({(int)result})"
            : $"{function}: {Name(result)} ({description})";
        return detail is null ? text : $"{text}. {detail}";
    }

    private static string? Description(CudaResult result)
    {
        if (!CudaLibrary.TryLoad(out _))
        {
            return null;
        }

        byte* text = null;
        return CuNative.cuGetErrorString(result, &text) == CudaResult.Success && text != null
            ? Marshal.PtrToStringUTF8((nint)text)
            : null;
    }

    private static string FallbackName(CudaResult result)
    {
        if (!Enum.IsDefined(result))
        {
            return $"CUresult {(int)result}";
        }

        var name = result.ToString();
        var builder = new System.Text.StringBuilder("CUDA_", name.Length + 16);
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
}
