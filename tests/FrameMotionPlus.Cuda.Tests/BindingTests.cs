using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameMotionPlus.Cuda.Interop;
using FrameMotionPlus.Cuda.Tests.Support;

namespace FrameMotionPlus.Cuda.Tests;

/// <summary>Every bound entry point exists in the installed driver and is the versioned name cuda.h maps to.</summary>
public partial class BindingTests
{
    private static IReadOnlyList<string> BoundNames { get; } =
    [
        .. typeof(CuNative).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(m => (Method: m, Import: m.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(x => x.Import is not null)
            .Select(x => x.Import!.EntryPoint ?? x.Method.Name)
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public void Bindings_cover_what_phases_2_to_5_need()
    {
        string[] required =
        [
            "cuInit", "cuDeviceGetUuid_v2", "cuDevicePrimaryCtxRetain", "cuDevicePrimaryCtxRelease_v2", "cuCtxSetCurrent",
            "cuStreamCreateWithPriority", "cuMemAlloc_v2", "cuMemHostAlloc", "cuMemHostRegister_v2", "cuMemcpy2DAsync_v2",
            "cuModuleLoadData", "cuLaunchKernel", "cuEventElapsedTime", "cuStreamWaitEvent", "cuImportExternalMemory",
            "cuExternalMemoryGetMappedBuffer", "cuImportExternalSemaphore", "cuSignalExternalSemaphoresAsync",
            "cuWaitExternalSemaphoresAsync",
        ];
        Assert.Empty(required.Except(BoundNames));
        Assert.True(BoundNames.Count >= 60, $"{BoundNames.Count} bindings");
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Every_bound_symbol_is_exported_by_the_installed_driver()
    {
        Gpu.Require();
        Assert.True(CudaLibrary.TryLoad(out var handle));
        var missing = BoundNames.Where(name => !NativeLibrary.TryGetExport(handle, name, out _)).ToList();
        Assert.True(missing.Count == 0, $"not exported by {CudaDriver.LibraryName}: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_bound_symbol_is_the_name_cuda_h_maps_to()
    {
        var toolkit = Repo.CudaToolkit;
        if (toolkit is null)
        {
            Assert.Skip("cuda.h not found");
        }

        var header = File.ReadAllText(Path.Combine(toolkit, "include", "cuda.h"));
        var declared = DeclarationPattern().Matches(header).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in DefinePattern().Matches(header))
        {
            mapped.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        }

        // Declarations use the plain names; the defines rename them to the exported symbols.
        declared.UnionWith(declared.Where(mapped.ContainsKey).Select(d => mapped[d]).ToList());

        var problems = new List<string>();
        foreach (var name in BoundNames)
        {
            if (!declared.Contains(name))
            {
                problems.Add($"{name} is not declared in cuda.h");
            }

            // A plain name the header redirects (cuMemAlloc -> cuMemAlloc_v2) must not be bound directly.
            if (mapped.TryGetValue(name, out var target) && target != name)
            {
                problems.Add($"cuda.h maps {name} to {target}");
            }

            // A versioned name must be what the header maps the plain name to, where it maps it at all.
            var plain = VersionSuffix().Replace(name, string.Empty);
            if (plain != name && mapped.TryGetValue(plain, out var plainTarget) && plainTarget != name)
            {
                problems.Add($"bound {name} but cuda.h maps {plain} to {plainTarget}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex(@"CUresult\s+CUDAAPI\s+(\w+)\s*\(")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*#define\s+(cu\w+)\s+(?:__CUDA_API_PT[DS][SZ]\()?(cu\w+)\)?\s*$", RegexOptions.Multiline)]
    private static partial Regex DefinePattern();

    [GeneratedRegex(@"_v\d+$")]
    private static partial Regex VersionSuffix();
}
