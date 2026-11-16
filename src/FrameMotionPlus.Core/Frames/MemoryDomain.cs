namespace FrameMotionPlus.Core.Frames;

/// <summary>
/// Where a frame's memory lives. Each generator and presenter declares its domain, and the pipeline
/// inserts a bridge only where domains differ.
/// </summary>
public enum MemoryDomain
{
    Unknown = 0,

    /// <summary>Ordinary pageable host memory (tests, tooling).</summary>
    Host = 1,

    /// <summary>CUDA device memory; the address is a CUdeviceptr.</summary>
    CudaDevice = 2,

    /// <summary>Pinned host memory mapped into the CUDA address space.</summary>
    CudaMappedHost = 3,

    /// <summary>A Vulkan buffer; the address is the VkBuffer handle.</summary>
    Vulkan = 4,
}
