using System.Globalization;
using System.Text;
using FrameMotionPlus.Cuda.Interop;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>
/// Proves every interop struct layout and constant against cuda.h: a C program compiled with gcc against the header
/// prints sizeof/offsetof/values, and each must equal the C# declaration. Skips where gcc or the header is absent
/// (CI); on the dev machine it uses the CUDA 12.8 headers bundled with the VFX SDK.
/// </summary>
[Collection(SerialGpuDefinition.Name)]
public class StructLayoutTests
{
    [Fact]
    public void Interop_structs_and_constants_match_cuda_h()
    {
        var toolkit = Repo.CudaToolkit;
        var gcc = Tools.Which("gcc");
        if (toolkit is null || gcc is null || OperatingSystem.IsWindows())
        {
            Assert.Skip($"needs gcc and cuda.h (gcc: {gcc ?? "missing"}, toolkit: {toolkit ?? "missing"})");
        }

        var checks = Checks();
        var dir = Tools.TempDirectory("layout");
        try
        {
            var source = new StringBuilder();
            source.AppendLine("#include <stdio.h>\n#include <stddef.h>\n#include <cuda.h>\nint main(void)\n{");
            foreach (var (label, expression, _) in checks)
            {
                source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%lld\\n\", \"{label}\", (long long)({expression}));\n");
            }

            source.AppendLine("    return 0;\n}");
            File.WriteAllText(Path.Combine(dir, "layout.c"), source.ToString());
            var (compileExit, compileOutput) = Tools.Run(gcc, ["-std=c11", "-Wall", "-I", Path.Combine(toolkit, "include"), "-o", "layout", "layout.c"], dir);
            Assert.True(compileExit == 0, compileOutput);
            var (runExit, output) = Tools.Run(Path.Combine(dir, "layout"), [], dir);
            Assert.Equal(0, runExit);

            var fromC = new Dictionary<string, long>();
            foreach (var parts in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('=', 2)))
            {
                fromC.TryAdd(parts[0], long.Parse(parts[1], CultureInfo.InvariantCulture));
            }

            var mismatches = checks
                .Where(c => fromC[c.Label] != c.CSharp)
                .Select(c => $"{c.Label}: C {fromC[c.Label]}, C# {c.CSharp}")
                .ToList();
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            Assert.True(checks.Count > 200, $"only {checks.Count} checks");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static unsafe List<(string Label, string Expression, long CSharp)> Checks()
    {
        var list = new List<(string, string, long)>();
        void Size(string cType, int csharp) => list.Add(($"sizeof({cType})", $"sizeof({cType})", csharp));
        void Offset(string cType, string member, long csharp) => list.Add(($"offsetof({cType},{member})", $"offsetof({cType}, {member})", csharp));
        void Value(string cName, long csharp) => list.Add((cName, cName, csharp));

        Size("CUdeviceptr", sizeof(ulong));
        Size("CUdevice", sizeof(int));
        Size("CUresult", sizeof(CudaResult));
        Size("CUmemorytype", sizeof(CuMemoryType));
        Size("CUjit_option", sizeof(CuJitOption));
        Size("CUdevice_attribute", sizeof(CudaDeviceAttribute));
        Size("size_t", sizeof(nuint));
        Size("CUuuid", sizeof(CuUuid));

        CuMemcpy2D m = default;
        var mb = (byte*)&m;
        Size("CUDA_MEMCPY2D", sizeof(CuMemcpy2D));
        Offset("CUDA_MEMCPY2D", "srcXInBytes", (byte*)&m.SrcXInBytes - mb);
        Offset("CUDA_MEMCPY2D", "srcY", (byte*)&m.SrcY - mb);
        Offset("CUDA_MEMCPY2D", "srcMemoryType", (byte*)&m.SrcMemoryType - mb);
        Offset("CUDA_MEMCPY2D", "srcHost", (byte*)&m.SrcHost - mb);
        Offset("CUDA_MEMCPY2D", "srcDevice", (byte*)&m.SrcDevice - mb);
        Offset("CUDA_MEMCPY2D", "srcArray", (byte*)&m.SrcArray - mb);
        Offset("CUDA_MEMCPY2D", "srcPitch", (byte*)&m.SrcPitch - mb);
        Offset("CUDA_MEMCPY2D", "dstXInBytes", (byte*)&m.DstXInBytes - mb);
        Offset("CUDA_MEMCPY2D", "dstY", (byte*)&m.DstY - mb);
        Offset("CUDA_MEMCPY2D", "dstMemoryType", (byte*)&m.DstMemoryType - mb);
        Offset("CUDA_MEMCPY2D", "dstHost", (byte*)&m.DstHost - mb);
        Offset("CUDA_MEMCPY2D", "dstDevice", (byte*)&m.DstDevice - mb);
        Offset("CUDA_MEMCPY2D", "dstArray", (byte*)&m.DstArray - mb);
        Offset("CUDA_MEMCPY2D", "dstPitch", (byte*)&m.DstPitch - mb);
        Offset("CUDA_MEMCPY2D", "WidthInBytes", (byte*)&m.WidthInBytes - mb);
        Offset("CUDA_MEMCPY2D", "Height", (byte*)&m.Height - mb);

        CuExternalMemoryHandleDesc emh = default;
        var emhb = (byte*)&emh;
        Size("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", sizeof(CuExternalMemoryHandleDesc));
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "type", (byte*)&emh.Type - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "handle", (byte*)&emh.Handle - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "handle.fd", (byte*)&emh.Handle.Fd - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "handle.win32.handle", (byte*)&emh.Handle.Win32Handle - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "handle.win32.name", (byte*)&emh.Handle.Win32Name - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "handle.nvSciBufObject", (byte*)&emh.Handle.NvSciObject - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "size", (byte*)&emh.Size - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "flags", (byte*)&emh.Flags - emhb);
        Offset("CUDA_EXTERNAL_MEMORY_HANDLE_DESC", "reserved", (byte*)emh.Reserved - emhb);
        list.Add(("sizeof(CUDA_EXTERNAL_MEMORY_HANDLE_DESC.reserved)", "sizeof(((CUDA_EXTERNAL_MEMORY_HANDLE_DESC*)0)->reserved)", 16 * sizeof(uint)));

        CuExternalMemoryBufferDesc emb = default;
        var embb = (byte*)&emb;
        Size("CUDA_EXTERNAL_MEMORY_BUFFER_DESC", sizeof(CuExternalMemoryBufferDesc));
        Offset("CUDA_EXTERNAL_MEMORY_BUFFER_DESC", "offset", (byte*)&emb.Offset - embb);
        Offset("CUDA_EXTERNAL_MEMORY_BUFFER_DESC", "size", (byte*)&emb.Size - embb);
        Offset("CUDA_EXTERNAL_MEMORY_BUFFER_DESC", "flags", (byte*)&emb.Flags - embb);
        Offset("CUDA_EXTERNAL_MEMORY_BUFFER_DESC", "reserved", (byte*)emb.Reserved - embb);

        CuExternalSemaphoreHandleDesc esh = default;
        var eshb = (byte*)&esh;
        Size("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", sizeof(CuExternalSemaphoreHandleDesc));
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "type", (byte*)&esh.Type - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "handle", (byte*)&esh.Handle - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "handle.fd", (byte*)&esh.Handle.Fd - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "handle.win32.handle", (byte*)&esh.Handle.Win32Handle - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "handle.win32.name", (byte*)&esh.Handle.Win32Name - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "handle.nvSciSyncObj", (byte*)&esh.Handle.NvSciObject - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "flags", (byte*)&esh.Flags - eshb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_HANDLE_DESC", "reserved", (byte*)esh.Reserved - eshb);

        CuExternalSemaphoreSignalParams sp = default;
        var spb = (byte*)&sp;
        Size("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", sizeof(CuExternalSemaphoreSignalParams));
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "params.fence.value", (byte*)&sp.FenceValue - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "params.nvSciSync.fence", (byte*)&sp.NvSciSyncFence - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "params.nvSciSync.reserved", (byte*)&sp.NvSciSyncFence - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "params.keyedMutex.key", (byte*)&sp.KeyedMutexKey - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "params.reserved", (byte*)sp.ParamsReserved - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "flags", (byte*)&sp.Flags - spb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_SIGNAL_PARAMS", "reserved", (byte*)sp.Reserved - spb);

        CuExternalSemaphoreWaitParams wp = default;
        var wpb = (byte*)&wp;
        Size("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", sizeof(CuExternalSemaphoreWaitParams));
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "params.fence.value", (byte*)&wp.FenceValue - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "params.nvSciSync.fence", (byte*)&wp.NvSciSyncFence - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "params.keyedMutex.key", (byte*)&wp.KeyedMutexKey - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "params.keyedMutex.timeoutMs", (byte*)&wp.KeyedMutexTimeoutMs - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "params.reserved", (byte*)wp.ParamsReserved - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "flags", (byte*)&wp.Flags - wpb);
        Offset("CUDA_EXTERNAL_SEMAPHORE_WAIT_PARAMS", "reserved", (byte*)wp.Reserved - wpb);

        foreach (var value in Enum.GetValues<CudaResult>())
        {
            Value("CUDA_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CudaDeviceAttribute>())
        {
            Value("CU_DEVICE_ATTRIBUTE_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CuMemoryType>())
        {
            Value("CU_MEMORYTYPE_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CuJitOption>())
        {
            Value("CU_JIT_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CuFunctionAttribute>())
        {
            Value("CU_FUNC_ATTRIBUTE_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CuExternalMemoryHandleType>())
        {
            Value("CU_EXTERNAL_MEMORY_HANDLE_TYPE_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CuExternalSemaphoreHandleType>())
        {
            Value("CU_EXTERNAL_SEMAPHORE_HANDLE_TYPE_" + Snake(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<CudaSchedulingMode>())
        {
            Value("CU_CTX_SCHED_" + Snake(value.ToString()), (long)value);
        }

        Value("CU_STREAM_DEFAULT", CuConstants.StreamDefault);
        Value("CU_STREAM_NON_BLOCKING", CuConstants.StreamNonBlocking);
        Value("CU_EVENT_DEFAULT", CuConstants.EventDefault);
        Value("CU_EVENT_BLOCKING_SYNC", CuConstants.EventBlockingSync);
        Value("CU_EVENT_DISABLE_TIMING", CuConstants.EventDisableTiming);
        Value("CU_MEMHOSTALLOC_PORTABLE", CuConstants.MemHostAllocPortable);
        Value("CU_MEMHOSTALLOC_DEVICEMAP", CuConstants.MemHostAllocDeviceMap);
        Value("CU_MEMHOSTALLOC_WRITECOMBINED", CuConstants.MemHostAllocWriteCombined);
        Value("CU_MEMHOSTALLOC_PORTABLE", (long)CudaHostAllocOptions.Portable);
        Value("CU_MEMHOSTALLOC_DEVICEMAP", (long)CudaHostAllocOptions.DeviceMap);
        Value("CU_MEMHOSTALLOC_WRITECOMBINED", (long)CudaHostAllocOptions.WriteCombined);
        Value("CU_MEMHOSTREGISTER_PORTABLE", CuConstants.MemHostRegisterPortable);
        Value("CU_MEMHOSTREGISTER_DEVICEMAP", CuConstants.MemHostRegisterDeviceMap);
        Value("CU_MEMHOSTREGISTER_IOMEMORY", CuConstants.MemHostRegisterIoMemory);
        Value("CU_MEMHOSTREGISTER_READ_ONLY", CuConstants.MemHostRegisterReadOnly);
        Value("CU_MEMHOSTREGISTER_PORTABLE", (long)CudaHostRegisterOptions.Portable);
        Value("CU_MEMHOSTREGISTER_DEVICEMAP", (long)CudaHostRegisterOptions.DeviceMap);
        Value("CU_MEMHOSTREGISTER_IOMEMORY", (long)CudaHostRegisterOptions.IoMemory);
        Value("CU_MEMHOSTREGISTER_READ_ONLY", (long)CudaHostRegisterOptions.ReadOnly);
        Value("CUDA_EXTERNAL_MEMORY_DEDICATED", CuConstants.ExternalMemoryDedicated);
        Value("CU_CTX_SCHED_AUTO", CuConstants.CtxSchedAuto);
        Value("CU_CTX_SCHED_SPIN", CuConstants.CtxSchedSpin);
        Value("CU_CTX_SCHED_YIELD", CuConstants.CtxSchedYield);
        Value("CU_CTX_SCHED_BLOCKING_SYNC", CuConstants.CtxSchedBlockingSync);

        // Labels repeat when two C# declarations mirror one constant; keep one row per (label, value) pair.
        return [.. list.GroupBy(c => (c.Item1, c.Item3)).Select(g => g.First())];
    }

    /// <summary>PascalCase to the C spelling: ErrorInvalidPtx to ERROR_INVALID_PTX, OpaqueWin32Kmt to OPAQUE_WIN32_KMT.</summary>
    private static string Snake(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
