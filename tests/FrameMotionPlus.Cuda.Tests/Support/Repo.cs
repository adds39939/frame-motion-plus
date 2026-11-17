using System.Diagnostics;
using System.Reflection;

namespace FrameMotionPlus.Cuda.Tests.Support;

/// <summary>The repository checkout and the optional dev-machine tools the toolchain and layout tests use.</summary>
internal static class Repo
{
    private static readonly Lazy<string?> _root = new(FindRoot);

    public static string? Root => _root.Value;

    public static string RequireRoot()
    {
        var root = Root;
        if (root is null)
        {
            Assert.Skip("repository checkout not found");
        }

        return root;
    }

    public static string KernelsDirectory => Path.Combine(RequireRoot(), "native", "kernels");

    /// <summary>
    /// A CUDA toolkit with nvcc and cuda.h: $FMP_CUDA_PATH, $CUDA_PATH, or the one bundled with the VFX SDK under
    /// lib/ on the dev machine. Null on CI.
    /// </summary>
    public static string? CudaToolkit
    {
        get
        {
            foreach (var candidate in new[]
            {
                Environment.GetEnvironmentVariable("FMP_CUDA_PATH"),
                Environment.GetEnvironmentVariable("CUDA_PATH"),
                Root is null ? null : Path.Combine(Root, "lib", "VideoFX", "external", "cuda"),
            })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "include", "cuda.h")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    private static string? FindRoot()
    {
        var recorded = typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;
        if (recorded is not null && File.Exists(Path.Combine(recorded, "FrameMotionPlus.slnx")))
        {
            return Path.GetFullPath(recorded);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FrameMotionPlus.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}

internal static class Tools
{
    /// <summary>Full path of <paramref name="name"/> on PATH, or null.</summary>
    public static string? Which(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, name);
            if (!string.IsNullOrEmpty(dir) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static (int ExitCode, string Output) Run(string file, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutMs = 300_000)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"cannot start {file}");
        process.StandardInput.Close(); // the tools read no input; never hand them the test host's
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} did not finish in {timeoutMs} ms");
        }

        // The output pipes reach end-of-file only when every copy of their write ends is closed. Never wait on that
        // indefinitely once the process itself has exited.
        if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException($"{file} exited with {process.ExitCode} but its output pipes stayed open");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public static string TempDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fmp-cuda-tests-{purpose}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
