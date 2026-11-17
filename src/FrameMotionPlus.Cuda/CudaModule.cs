using System.Diagnostics.CodeAnalysis;
using System.Text;
using FrameMotionPlus.Cuda.Interop;

namespace FrameMotionPlus.Cuda;

/// <summary>Grid or block dimensions for a kernel launch.</summary>
public readonly record struct CudaDim3(uint X, uint Y = 1, uint Z = 1);

/// <summary>A module loaded from PTX (JIT-compiled by the driver for the current GPU) or a cubin.</summary>
public sealed unsafe class CudaModule : CudaObject
{
    private const int LogBytes = 16 * 1024;

    private nint _handle;

    private CudaModule(CudaContext context, nint handle, string name)
        : base(context)
    {
        _handle = handle;
        Name = name;
    }

    public nint Handle => _handle;

    /// <summary>A label for messages (the embedded module name or "PTX").</summary>
    public string Name { get; }

    /// <summary>
    /// Loads <paramref name="image"/> (PTX text or a cubin) with <c>cuModuleLoadDataEx</c>. A NUL terminator is added
    /// to PTX that lacks one. On failure the exception carries the JIT compiler's error log.
    /// </summary>
    public static CudaModule Load(CudaContext context, ReadOnlySpan<byte> image, string name = "PTX")
    {
        ArgumentNullException.ThrowIfNull(context);
        if (image.IsEmpty)
        {
            throw new ArgumentException("The module image is empty.", nameof(image));
        }

        var terminated = image[^1] == 0 ? image.ToArray() : [.. image, (byte)0];
        var log = new byte[LogBytes];
        context.AddReference();
        try
        {
            context.EnsureCurrent();
            nint handle;
            CudaResult result;
            fixed (byte* data = terminated)
            fixed (byte* logBuffer = log)
            {
                var options = stackalloc CuJitOption[2] { CuJitOption.ErrorLogBuffer, CuJitOption.ErrorLogBufferSizeBytes };
                var values = stackalloc void*[2] { logBuffer, (void*)LogBytes };
                result = CuNative.cuModuleLoadDataEx(&handle, data, 2, options, values);
            }

            if (result != CudaResult.Success)
            {
                var text = Encoding.UTF8.GetString(log, 0, Math.Max(0, Array.IndexOf(log, (byte)0))).Trim();
                Cu.Throw(result, nameof(CuNative.cuModuleLoadDataEx), text.Length == 0 ? $"Module '{name}'." : $"Module '{name}', JIT log: {text}");
            }

            return new CudaModule(context, handle, name);
        }
        catch
        {
            context.Release();
            throw;
        }
    }

    /// <summary>The <c>extern "C"</c> kernel <paramref name="kernelName"/>.</summary>
    public CudaKernel GetKernel(string kernelName)
    {
        ArgumentException.ThrowIfNullOrEmpty(kernelName);
        ThrowIfDisposed();
        Context.EnsureCurrent();
        var bytes = Encoding.UTF8.GetBytes(kernelName + "\0");
        nint function;
        CudaResult result;
        fixed (byte* p = bytes)
        {
            result = CuNative.cuModuleGetFunction(&function, _handle, p);
        }

        if (result != CudaResult.Success)
        {
            Cu.Throw(result, nameof(CuNative.cuModuleGetFunction), $"No kernel '{kernelName}' in module '{Name}'.");
        }

        return new CudaKernel(this, function, kernelName);
    }

    internal void ThrowIfUnloaded() => ThrowIfDisposed();

    private protected override void ReleaseNative()
    {
        _ = CuNative.cuModuleUnload(_handle);
        _handle = 0;
    }
}

/// <summary>A kernel (<c>CUfunction</c>), valid while its <see cref="CudaModule"/> is.</summary>
public readonly unsafe struct CudaKernel : IEquatable<CudaKernel>
{
    private readonly CudaModule? _module;

    internal CudaKernel(CudaModule module, nint handle, string name)
    {
        _module = module;
        Handle = handle;
        Name = name;
    }

    public nint Handle { get; }

    public string? Name { get; }

    public bool IsValid => _module is { IsDisposed: false } && Handle != 0;

    public int MaxThreadsPerBlock => GetAttribute(CuFunctionAttribute.MaxThreadsPerBlock);

    public int RegisterCount => GetAttribute(CuFunctionAttribute.NumRegs);

    /// <summary>Virtual architecture the kernel's PTX targets, e.g. 75 for compute_75.</summary>
    public int PtxVersion => GetAttribute(CuFunctionAttribute.PtxVersion);

    /// <summary>
    /// Enqueues the kernel on <paramref name="stream"/>. <paramref name="arguments"/> holds one pointer per kernel
    /// parameter, each to a value of exactly the parameter's size; the driver copies the values before returning.
    /// </summary>
    public void Launch(CudaStream stream, CudaDim3 grid, CudaDim3 block, uint sharedMemoryBytes, void** arguments)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var streamHandle = stream.Handle; // throws for a disposed stream, whose zero handle would be the default stream
        var module = Module;
        module.ThrowIfUnloaded();
        module.Context.EnsureCurrent();
        var result = CuNative.cuLaunchKernel(
            Handle, grid.X, grid.Y, grid.Z, block.X, block.Y, block.Z, sharedMemoryBytes, streamHandle, arguments, null);
        if (result != CudaResult.Success)
        {
            ThrowLaunch(result, grid, block);
        }
    }

    public CudaModule Module => _module ?? throw new InvalidOperationException("Default CudaKernel.");

    public bool Equals(CudaKernel other) => Handle == other.Handle && ReferenceEquals(_module, other._module);

    public override bool Equals(object? obj) => obj is CudaKernel other && Equals(other);

    public override int GetHashCode() => Handle.GetHashCode();

    public static bool operator ==(CudaKernel left, CudaKernel right) => left.Equals(right);

    public static bool operator !=(CudaKernel left, CudaKernel right) => !left.Equals(right);

    private int GetAttribute(CuFunctionAttribute attribute)
    {
        var module = Module;
        module.ThrowIfUnloaded();
        module.Context.EnsureCurrent();
        int value;
        Cu.Check(CuNative.cuFuncGetAttribute(&value, attribute, Handle), nameof(CuNative.cuFuncGetAttribute));
        return value;
    }

    [DoesNotReturn]
    private void ThrowLaunch(CudaResult result, CudaDim3 grid, CudaDim3 block) =>
        Cu.Throw(result, nameof(CuNative.cuLaunchKernel), $"Kernel '{Name}', grid {grid.X}x{grid.Y}x{grid.Z}, block {block.X}x{block.Y}x{block.Z}.");
}
