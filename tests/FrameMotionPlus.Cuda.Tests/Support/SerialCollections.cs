namespace FrameMotionPlus.Cuda.Tests.Support;

/// <summary>
/// Tests that run alone, after every parallel test: GPU timing (other tests' kernels and host-side JIT under load
/// would distort the spans), tests that measure free device memory or count open descriptors, the dependency
/// controls (streams of parallel tests can share a hardware queue), and tests that start external processes.
/// <para>
/// A hang once seen in an external-process test was not about forking: the driver closes the descriptor when
/// <c>cuImportExternalMemory</c> fails, a test then closed the same number again, and the number had meanwhile been
/// handed to that process's output pipe. The import wrappers now give the driver a private duplicate
/// (<c>DescriptorHandoff</c>).
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialGpuDefinition
{
    public const string Name = "GPU timing and external tools (serial)";
}
