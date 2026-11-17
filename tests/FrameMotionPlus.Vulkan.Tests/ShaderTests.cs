using System.Diagnostics;
using System.Reflection;

namespace FrameMotionPlus.Vulkan.Tests;

public class ShaderTests
{
    private static readonly string[] Shaders = ["present.vert.spv", "present.frag.spv", "copy.comp.spv"];

    private static string ShaderDirectory()
    {
        var root = typeof(ShaderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(static a => a.Key == "RepositoryRoot").Value!;
        return Path.Combine(root, "native", "shaders");
    }

    [Fact]
    public void Embedded_spirv_is_the_checked_in_spirv()
    {
        var dir = ShaderDirectory();
        if (!Directory.Exists(dir))
        {
            Assert.Skip($"source tree not found at {dir}");
        }

        foreach (var name in Shaders)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "spirv", name)), FrameMotionPlus.Vulkan.Shaders.Load(name));
        }
    }

    [Fact]
    public void Checked_in_spirv_is_up_to_date_with_the_sources()
    {
        var dir = ShaderDirectory();
        if (!Directory.Exists(dir) || OperatingSystem.IsWindows())
        {
            Assert.Skip("needs the source tree and bash");
        }

        var version = Run("glslangValidator", "--version");
        if (version is null)
        {
            Assert.Skip("glslangValidator is not installed");
        }

        var recorded = File.ReadAllText(Path.Combine(dir, "spirv", "COMPILER")).Trim();
        var installed = version.Split('\n')[0].Trim();
        if (recorded != installed)
        {
            Assert.Skip($"installed compiler '{installed}' differs from the one that generated the SPIR-V ('{recorded}')");
        }

        var output = Directory.CreateTempSubdirectory("fmp-spirv-");
        try
        {
            Assert.NotNull(Run("bash", Path.Combine(dir, "build.sh"), output.FullName));
            foreach (var name in Shaders)
            {
                Assert.True(
                    File.ReadAllBytes(Path.Combine(dir, "spirv", name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output.FullName, name))),
                    $"native/shaders/spirv/{name} is stale: run native/shaders/build.sh");
            }
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            using var process = Process.Start(info)!;

            // Disposing the process does not close redirected readers taken synchronously; left to the finalizer,
            // their pipe descriptors disappear at some later collection and disturb tests that count descriptors.
            using var outReader = process.StandardOutput;
            using var errReader = process.StandardError;
            var stdout = outReader.ReadToEnd();
            var stderr = errReader.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"{file} failed: {stderr}");
            return stdout;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
