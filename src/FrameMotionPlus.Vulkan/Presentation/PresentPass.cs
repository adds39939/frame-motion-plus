using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan.Interop;
using Vortice.Vulkan;

namespace FrameMotionPlus.Vulkan;

/// <summary>The output colour transform the presenter pass applies (shader specialization constant TRANSFORM).</summary>
public enum OutputTransform
{
    /// <summary>Values pass through: 8-bit SDR to an SDR target, or 10-bit PQ to an HDR10 ST 2084 target.</summary>
    Passthrough = 0,

    /// <summary>
    /// PLACEHOLDER for a 10-bit PQ ring on an SDR-only target: PQ decoded, 203 nits mapped to SDR white, hard clip,
    /// sRGB encode, no gamut mapping. Watchable, not correct; Phase 7 replaces it with tone mapping.
    /// </summary>
    PqToSdrPlaceholder = 1,
}

/// <summary>The scaler stage (shader specialization constant SCALER). Better filters slot in as new values.</summary>
public enum PresentScaler
{
    /// <summary>Hardware bilinear; texel-exact fetch when the content rectangle equals the source size.</summary>
    Bilinear = 0,
}

/// <summary>Where the source lands in the target: aspect-preserving fit, centred, black bars elsewhere.</summary>
public readonly record struct ContentRect(int X, int Y, int Width, int Height)
{
    public static ContentRect Fit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return default;
        }

        long w;
        long h;
        if ((long)sourceWidth * targetHeight >= (long)sourceHeight * targetWidth)
        {
            // Source is relatively wider: full width, letterbox bars top and bottom.
            w = targetWidth;
            h = (((long)sourceHeight * targetWidth * 2) + sourceWidth) / (2L * sourceWidth);
        }
        else
        {
            // Source is relatively taller: full height, pillarbox bars left and right.
            h = targetHeight;
            w = (((long)sourceWidth * targetHeight * 2) + sourceHeight) / (2L * sourceHeight);
        }

        w = Math.Clamp(w, 1, targetWidth);
        h = Math.Clamp(h, 1, targetHeight);
        return new ContentRect((int)((targetWidth - w) / 2), (int)((targetHeight - h) / 2), (int)w, (int)h);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CopyPushConstants
{
    public uint PitchWords;
    public uint Width;
    public uint Height;
    public uint Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PresentPushConstants
{
    public float RectX;
    public float RectY;
    public float RectWidth;
    public float RectHeight;
    public int Identity;
    public int Pad0;
    public int Pad1;
    public int Pad2;
}

/// <summary>
/// Shader modules, layouts, sampler, descriptor sets and pipelines: the compute copy (ring slot to staged image, one
/// descriptor set per ring slot) and the graphics pipeline variants of the scale/transform pass.
/// </summary>
internal sealed unsafe class PresentPass : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly List<(VkFormat Format, OutputTransform Transform, PresentScaler Scaler, VkPipeline Pipeline)> _pipelines = [];
    private VkShaderModule _vert;
    private VkShaderModule _frag;
    private VkShaderModule _copy;
    private VkSampler _sampler;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorSetLayout _copySetLayout;
    private VkDescriptorPool _pool;
    private VkDescriptorPool _copyPool;
    private VkDescriptorSet[] _copySets = [];

    public PresentPass(VulkanDevice device)
    {
        _device = device;
        try
        {
            Create();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public VkPipelineLayout Layout { get; private set; }

    public VkDescriptorSet DescriptorSet { get; private set; }

    public VkPipelineLayout CopyLayout { get; private set; }

    public VkPipeline CopyPipeline { get; private set; }

    /// <summary>The copy's descriptor set for ring slot <paramref name="slot"/> (slot buffer + staged image).</summary>
    public VkDescriptorSet CopySet(int slot) => _copySets[slot];

    /// <summary>
    /// Transform for a ring of <paramref name="ringFormat"/> on a target in <paramref name="target"/>. The ring
    /// format stands for the content's colour in this phase: 8-bit is SDR, 10-bit is PQ (Phase 7 reads the frame's
    /// colour metadata instead).
    /// </summary>
    public static OutputTransform SelectTransform(PixelFormat ringFormat, TargetColorSpace target) =>
        (ringFormat.Family(), target) switch
        {
            (PixelFormatFamily.EightBit, TargetColorSpace.Sdr) => OutputTransform.Passthrough,
            (PixelFormatFamily.TenBit, TargetColorSpace.Hdr10St2084) => OutputTransform.Passthrough,
            (PixelFormatFamily.TenBit, TargetColorSpace.Sdr) => OutputTransform.PqToSdrPlaceholder,
            _ => throw new NotSupportedException($"{ringFormat} content on a {target} target is Phase 7 (SDR in HDR)."),
        };

    public void BindSource(VkImageView view)
    {
        var image = new VkDescriptorImageInfo { imageView = view, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var write = new VkWriteDescriptorSet
        {
            dstSet = DescriptorSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &image,
        };
        _device.Api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    /// <summary>Points one copy descriptor set per ring slot at that slot's buffer and the staged image's word view.</summary>
    public void BindCopy(PresentationRing ring, VkImageView stagedWords)
    {
        var api = _device.Api;
        VulkanException.Check(api.vkResetDescriptorPool(_copyPool, 0), "vkResetDescriptorPool");
        _copySets = new VkDescriptorSet[ring.SlotCount];
        var layout = _copySetLayout;
        var writes = stackalloc VkWriteDescriptorSet[2];
        for (var i = 0; i < ring.SlotCount; i++)
        {
            var allocInfo = new VkDescriptorSetAllocateInfo { descriptorPool = _copyPool, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            VulkanException.Check(api.vkAllocateDescriptorSets(&allocInfo, &set), "vkAllocateDescriptorSets");
            _copySets[i] = set;
            var buffer = new VkDescriptorBufferInfo { buffer = ring.Buffer(i), offset = 0, range = (ulong)ring.ContentSizeBytes };
            var image = new VkDescriptorImageInfo { imageView = stagedWords, imageLayout = VkImageLayout.General };
            writes[0] = new VkWriteDescriptorSet
            {
                dstSet = set,
                dstBinding = 0,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &buffer,
            };
            writes[1] = new VkWriteDescriptorSet
            {
                dstSet = set,
                dstBinding = 1,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageImage,
                pImageInfo = &image,
            };
            api.vkUpdateDescriptorSets(2, writes, 0, null);
        }
    }

    public VkPipeline Pipeline(VkFormat format, OutputTransform transform, PresentScaler scaler)
    {
        foreach (var p in _pipelines)
        {
            if (p.Format == format && p.Transform == transform && p.Scaler == scaler)
            {
                return p.Pipeline;
            }
        }

        var pipeline = CreatePipeline(format, transform, scaler);
        _pipelines.Add((format, transform, scaler, pipeline));
        return pipeline;
    }

    public void Dispose()
    {
        var api = _device.Api;
        foreach (var p in _pipelines)
        {
            api.vkDestroyPipeline(p.Pipeline);
        }

        _pipelines.Clear();
        if (!CopyPipeline.IsNull)
        {
            api.vkDestroyPipeline(CopyPipeline);
            CopyPipeline = default;
        }

        if (!CopyLayout.IsNull)
        {
            api.vkDestroyPipelineLayout(CopyLayout);
            CopyLayout = default;
        }

        if (!_copyPool.IsNull)
        {
            api.vkDestroyDescriptorPool(_copyPool);
            _copyPool = default;
        }

        if (!_copySetLayout.IsNull)
        {
            api.vkDestroyDescriptorSetLayout(_copySetLayout);
            _copySetLayout = default;
        }

        if (!_copy.IsNull)
        {
            api.vkDestroyShaderModule(_copy);
            _copy = default;
        }

        if (!Layout.IsNull)
        {
            api.vkDestroyPipelineLayout(Layout);
            Layout = default;
        }

        if (!_pool.IsNull)
        {
            api.vkDestroyDescriptorPool(_pool);
            _pool = default;
        }

        if (!_setLayout.IsNull)
        {
            api.vkDestroyDescriptorSetLayout(_setLayout);
            _setLayout = default;
        }

        if (!_sampler.IsNull)
        {
            api.vkDestroySampler(_sampler);
            _sampler = default;
        }

        if (!_vert.IsNull)
        {
            api.vkDestroyShaderModule(_vert);
            _vert = default;
        }

        if (!_frag.IsNull)
        {
            api.vkDestroyShaderModule(_frag);
            _frag = default;
        }
    }

    private void Create()
    {
        var api = _device.Api;
        _vert = CreateModule(Shaders.Load("present.vert.spv"));
        _frag = CreateModule(Shaders.Load("present.frag.spv"));

        var samplerInfo = new VkSamplerCreateInfo
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            maxLod = 0,
        };
        VkSampler sampler;
        VulkanException.Check(api.vkCreateSampler(&samplerInfo, &sampler), "vkCreateSampler");
        _sampler = sampler;

        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Fragment,
            pImmutableSamplers = &sampler,
        };
        var setLayoutInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = 1, pBindings = &binding };
        VkDescriptorSetLayout setLayout;
        VulkanException.Check(api.vkCreateDescriptorSetLayout(&setLayoutInfo, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;

        var range = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Fragment, offset = 0, size = (uint)sizeof(PresentPushConstants) };
        var layoutInfo = new VkPipelineLayoutCreateInfo
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &range,
        };
        VkPipelineLayout layout;
        VulkanException.Check(api.vkCreatePipelineLayout(&layoutInfo, &layout), "vkCreatePipelineLayout");
        Layout = layout;

        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };
        VkDescriptorPool pool;
        VulkanException.Check(api.vkCreateDescriptorPool(&poolInfo, &pool), "vkCreateDescriptorPool");
        _pool = pool;

        var allocInfo = new VkDescriptorSetAllocateInfo { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        VulkanException.Check(api.vkAllocateDescriptorSets(&allocInfo, &set), "vkAllocateDescriptorSets");
        DescriptorSet = set;
        CreateCopy();
    }

    private void CreateCopy()
    {
        var api = _device.Api;
        _copy = CreateModule(Shaders.Load("copy.comp.spv"));
        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.StorageBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Compute,
        };
        bindings[1] = new VkDescriptorSetLayoutBinding
        {
            binding = 1,
            descriptorType = VkDescriptorType.StorageImage,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Compute,
        };
        var setLayoutInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = 2, pBindings = bindings };
        VkDescriptorSetLayout setLayout;
        VulkanException.Check(api.vkCreateDescriptorSetLayout(&setLayoutInfo, &setLayout), "vkCreateDescriptorSetLayout");
        _copySetLayout = setLayout;

        var range = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = (uint)sizeof(CopyPushConstants) };
        var layoutInfo = new VkPipelineLayoutCreateInfo
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &range,
        };
        VkPipelineLayout layout;
        VulkanException.Check(api.vkCreatePipelineLayout(&layoutInfo, &layout), "vkCreatePipelineLayout");
        CopyLayout = layout;

        const int MaxSlots = 6;
        var sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = MaxSlots };
        sizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageImage, descriptorCount = MaxSlots };
        var poolInfo = new VkDescriptorPoolCreateInfo { maxSets = MaxSlots, poolSizeCount = 2, pPoolSizes = sizes };
        VkDescriptorPool pool;
        VulkanException.Check(api.vkCreateDescriptorPool(&poolInfo, &pool), "vkCreateDescriptorPool");
        _copyPool = pool;

        using var entry = new Utf8StringList(["main"]);
        var info = new VkComputePipelineCreateInfo
        {
            stage = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Compute, module = _copy, pName = entry.Pointer[0] },
            layout = layout,
        };
        VkPipeline pipeline;
        VulkanException.Check(api.vkCreateComputePipelines(default, 1, &info, &pipeline), "vkCreateComputePipelines");
        CopyPipeline = pipeline;
    }

    private VkShaderModule CreateModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var info = new VkShaderModuleCreateInfo { codeSize = (nuint)code.Length, pCode = (uint*)p };
            VkShaderModule module;
            VulkanException.Check(_device.Api.vkCreateShaderModule(&info, &module), "vkCreateShaderModule");
            return module;
        }
    }

    private VkPipeline CreatePipeline(VkFormat format, OutputTransform transform, PresentScaler scaler)
    {
        using var entry = new Utf8StringList(["main"]);
        var specData = stackalloc int[2] { (int)scaler, (int)transform };
        var specEntries = stackalloc VkSpecializationMapEntry[2];
        specEntries[0] = new VkSpecializationMapEntry { constantID = 0, offset = 0, size = sizeof(int) };
        specEntries[1] = new VkSpecializationMapEntry { constantID = 1, offset = sizeof(int), size = sizeof(int) };
        var spec = new VkSpecializationInfo { mapEntryCount = 2, pMapEntries = specEntries, dataSize = 2 * sizeof(int), pData = specData };

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _vert, pName = entry.Pointer[0] };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            stage = VkShaderStageFlags.Fragment,
            module = _frag,
            pName = entry.Pointer[0],
            pSpecializationInfo = &spec,
        };

        var vertexInput = new VkPipelineVertexInputStateCreateInfo();
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { topology = VkPrimitiveTopology.TriangleList };
        var viewportState = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
        var raster = new VkPipelineRasterizationStateCreateInfo
        {
            polygonMode = VkPolygonMode.Fill,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.CounterClockwise,
            lineWidth = 1.0f,
        };
        var multisample = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
        var blendAttachment = new VkPipelineColorBlendAttachmentState
        {
            blendEnable = false,
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
        };
        var blend = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &blendAttachment };
        var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamic = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dynamicStates };
        var rendering = new VkPipelineRenderingCreateInfo { colorAttachmentCount = 1, pColorAttachmentFormats = &format };

        var info = new VkGraphicsPipelineCreateInfo
        {
            pNext = &rendering,
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &raster,
            pMultisampleState = &multisample,
            pColorBlendState = &blend,
            pDynamicState = &dynamic,
            layout = Layout,
        };
        VkPipeline pipeline;
        VulkanException.Check(_device.Api.vkCreateGraphicsPipelines(default, 1, &info, &pipeline), "vkCreateGraphicsPipelines");
        return pipeline;
    }
}

internal static class Shaders
{
    public static byte[] Load(string name)
    {
        using var stream = typeof(Shaders).Assembly.GetManifestResourceStream("FrameMotionPlus.Vulkan.Shaders." + name)
            ?? throw new InvalidOperationException($"Embedded shader {name} is missing.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
