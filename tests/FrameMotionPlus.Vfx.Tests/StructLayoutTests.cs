using System.Globalization;
using System.Text;
using FrameMotionPlus.Cuda.Tests.Support;
using FrameMotionPlus.Vfx.Interop;
using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>
/// Proves the interop declarations against the SDK's own headers: a C program compiled with gcc against
/// nvVideoEffects.h, nvCVImage.h, nvCVStatus.h and nvVFXVideoFrameGeneration.h prints every size, offset, value and
/// selector string, and each must equal the C# declaration. Skips where gcc or the headers are absent (CI, or a
/// machine without the SDK); on the dev machine it uses lib/VideoFX/include and the feature's include directory.
/// </summary>
public class StructLayoutTests
{
    [Fact]
    public void Interop_layouts_values_and_selectors_match_the_sdk_headers()
    {
        var location = Sdk.Location;
        var include = Path.Combine(location.Directory, "include");
        var featureInclude = Path.Combine(location.FeatureDirectory, "include");
        var gcc = Tools.Which("gcc");
        if (gcc is null || OperatingSystem.IsWindows() || !File.Exists(Path.Combine(include, "nvVideoEffects.h")) ||
            !File.Exists(Path.Combine(featureInclude, "nvVFXVideoFrameGeneration.h")))
        {
            Assert.Skip($"needs gcc and the VFX SDK headers (gcc: {gcc ?? "missing"}, headers: {include}, {featureInclude})");
        }

        var (numbers, strings) = Checks();
        var dir = Tools.TempDirectory("vfx-layout");
        try
        {
            var source = new StringBuilder();
            source.AppendLine("#include <stdio.h>\n#include <stddef.h>\n#include \"nvVideoEffects.h\"\n#include \"nvVFXVideoFrameGeneration.h\"\nint main(void)\n{");
            foreach (var (label, expression, _) in numbers)
            {
                source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%lld\\n\", \"{label}\", (long long)({expression}));\n");
            }

            foreach (var (label, expression, _) in strings)
            {
                source.Append(CultureInfo.InvariantCulture, $"    printf(\"%s=%s\\n\", \"{label}\", {expression});\n");
            }

            source.AppendLine("    return 0;\n}");
            File.WriteAllText(Path.Combine(dir, "layout.c"), source.ToString());
            var (compileExit, compileOutput) = Tools.Run(gcc, ["-std=c11", "-Wall", "-I", include, "-I", featureInclude, "-o", "layout", "layout.c"], dir);
            Assert.True(compileExit == 0, compileOutput);
            var (runExit, output) = Tools.Run(Path.Combine(dir, "layout"), [], dir);
            Assert.Equal(0, runExit);

            var fromC = new Dictionary<string, string>();
            foreach (var parts in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('=', 2)))
            {
                fromC.TryAdd(parts[0], parts[1]);
            }

            var mismatches = numbers
                .Where(c => long.Parse(fromC[c.Label], CultureInfo.InvariantCulture) != c.CSharp)
                .Select(c => $"{c.Label}: C {fromC[c.Label]}, C# {c.CSharp}")
                .Concat(strings.Where(c => fromC[c.Label] != c.CSharp).Select(c => $"{c.Label}: C '{fromC[c.Label]}', C# '{c.CSharp}'"))
                .ToList();
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            Assert.True(numbers.Count > 90, $"only {numbers.Count} numeric checks");
            Assert.True(strings.Count >= 15, $"only {strings.Count} selector checks");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static unsafe (List<(string Label, string Expression, long CSharp)> Numbers, List<(string Label, string Expression, string CSharp)> Strings) Checks()
    {
        var list = new List<(string, string, long)>();
        void Size(string cType, int csharp) => list.Add(($"sizeof({cType})", $"sizeof({cType})", csharp));
        void Offset(string cType, string member, long csharp) => list.Add(($"offsetof({cType},{member})", $"offsetof({cType}, {member})", csharp));
        void Value(string cName, long csharp) => list.Add((cName, cName, csharp));

        NvCVImage image = default;
        var b = (byte*)&image;
        Size("NvCVImage", sizeof(NvCVImage));
        Size("NvCV_Status", sizeof(NvCVStatus));
        Size("NvCVImage_PixelFormat", sizeof(NvCVImagePixelFormat));
        Size("NvCVImage_ComponentType", sizeof(NvCVImageComponentType));
        Size("NvVFX_Handle", sizeof(nint));
        Size("CUstream", sizeof(nint));
        Offset("NvCVImage", "width", (byte*)&image.Width - b);
        Offset("NvCVImage", "height", (byte*)&image.Height - b);
        Offset("NvCVImage", "pitch", (byte*)&image.Pitch - b);
        Offset("NvCVImage", "pixelFormat", (byte*)&image.PixelFormat - b);
        Offset("NvCVImage", "componentType", (byte*)&image.ComponentType - b);
        Offset("NvCVImage", "pixelBytes", &image.PixelBytes - b);
        Offset("NvCVImage", "componentBytes", &image.ComponentBytes - b);
        Offset("NvCVImage", "numComponents", &image.NumComponents - b);
        Offset("NvCVImage", "planar", &image.Planar - b);
        Offset("NvCVImage", "gpuMem", &image.GpuMem - b);
        Offset("NvCVImage", "colorspace", &image.Colorspace - b);
        Offset("NvCVImage", "reserved", image.Reserved - b);
        Offset("NvCVImage", "pixels", (byte*)&image.Pixels - b);
        Offset("NvCVImage", "deletePtr", (byte*)&image.DeletePtr - b);
        Offset("NvCVImage", "deleteProc", (byte*)&image.DeleteProc - b);
        Offset("NvCVImage", "bufferBytes", (byte*)&image.BufferBytes - b);

        foreach (var value in Enum.GetValues<NvCVStatus>())
        {
            Value(VfxErrors.Name(value), (long)value);
        }

        foreach (var value in Enum.GetValues<NvCVImagePixelFormat>())
        {
            Value(value == NvCVImagePixelFormat.FormatUnknown ? "NVCV_FORMAT_UNKNOWN" : "NVCV_" + Upper(value.ToString()), (long)value);
        }

        foreach (var value in Enum.GetValues<NvCVImageComponentType>())
        {
            Value(value == NvCVImageComponentType.TypeUnknown ? "NVCV_TYPE_UNKNOWN" : "NVCV_" + Upper(value.ToString()), (long)value);
        }

        Value("NVCV_CHUNKY", NvCVConstants.Chunky);
        Value("NVCV_CPU", NvCVConstants.Cpu);
        Value("NVCV_GPU", NvCVConstants.Gpu);
        Value("NVCV_CUDA", NvCVConstants.Gpu);
        Value("NVCV_CPU_PINNED", NvCVConstants.CpuPinned);
        Value("NVCV_LOG_ERROR", NvCVConstants.LogError);
        Value("NVCV_LOG_WARNING", NvCVConstants.LogWarning);
        Value("NVVFXVIDEOFRAMEGENERATION_MODE_LOW", (long)VfgMode.Low);
        Value("NVVFXVIDEOFRAMEGENERATION_MODE_MEDIUM", (long)VfgMode.Medium);
        Value("NVVFXVIDEOFRAMEGENERATION_MODE_HIGH", (long)VfgMode.High);

        var strings = new List<(string, string, string)>();
        void Selector(string cName, byte* csharp) => strings.Add((cName, cName, VfxSelectors.Name(csharp)));
        Selector("NVVFX_FX_VIDEO_FRAME_GENERATION", VfxSelectors.VideoFrameGeneration);
        Selector("NVVFXVIDEOFRAMEGENERATION_FRAME_MULTIPLIER", VfxSelectors.FrameMultiplier);
        Selector("NVVFXVIDEOFRAMEGENERATION_FRAME_INDEX", VfxSelectors.FrameIndex);
        Selector("NVVFXVIDEOFRAMEGENERATION_TIMESTEP", VfxSelectors.Timestep);
        Selector("NVVFXVIDEOFRAMEGENERATION_SHOT_CHANGE", VfxSelectors.ShotChange);
        Selector("NVVFXVIDEOFRAMEGENERATION_AUTOMATIC_SHOT_CHANGE_DETECTION_ENABLED", VfxSelectors.AutomaticShotChangeDetectionEnabled);
        Selector("NVVFXVIDEOFRAMEGENERATION_MODE", VfxSelectors.Mode);
        Selector("NVVFX_INPUT_IMAGE_0", VfxSelectors.InputImage0);
        Selector("NVVFX_INPUT_IMAGE_1", VfxSelectors.InputImage1);
        Selector("NVVFX_OUTPUT_IMAGE", VfxSelectors.OutputImage0);
        Selector("NVVFX_OUTPUT_IMAGE_0", VfxSelectors.OutputImage0);
        Selector("NVVFX_CUDA_STREAM", VfxSelectors.CudaStream);
        Selector("NVVFX_INPUT_WIDTH", VfxSelectors.InputWidth);
        Selector("NVVFX_INPUT_HEIGHT", VfxSelectors.InputHeight);
        Selector("NVVFX_MODEL_CACHE_DIRECTORY", VfxSelectors.ModelCacheDirectory);
        Selector("NVVFX_MODEL_CACHE_MODE", VfxSelectors.ModelCacheMode);

        // Not a selector: the feature version these declarations were checked against.
        strings.Add(("NVVFXVIDEOFRAMEGENERATION_VERSION", "NVVFXVIDEOFRAMEGENERATION_VERSION", "1.3.0.0"));
        return (list, strings);
    }

    /// <summary>PascalCase to the header's spelling: Rgb10A2 to RGB10A2, Bgra to BGRA, P32 to P32.</summary>
    private static string Upper(string name) => name.ToUpperInvariant();
}
