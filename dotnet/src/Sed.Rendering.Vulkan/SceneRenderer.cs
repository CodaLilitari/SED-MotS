using System.Reflection;
using System.Runtime.InteropServices;
using Sed.Core.Math;
using Sed.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Sed.Rendering.Vulkan;

/// <summary>
/// Renders a textured, depth-tested <see cref="RenderScene"/> to an offscreen
/// image. Textures are 8-bit palette indices; the fragment shader shades each
/// index through the CMP light ramp (by per-vertex intensity) and resolves it
/// against the palette, matching the engine's lighting. Translucent submeshes are
/// alpha-blended in a second pass; markers/selection draw flat (mode 1).
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    public const Format ColorFormat = Format.R8G8B8A8Unorm;
    public const Format DepthFormat = Format.D32Sfloat;

    private readonly VulkanDevice _dev;
    private readonly Vk _vk;

    private RenderPass _renderPass;
    private DescriptorSetLayout _setLayout;
    private Sampler _sampler;
    private PipelineLayout _layout;
    private Pipeline _pipeline;             // opaque, depth on
    private Pipeline _translucentPipeline;  // depth test on / write off, alpha blend
    private Pipeline _selectionPipeline;    // depth off (highlight)

    private GpuTexture _white;              // 1x1 index, for flat marker/selection draws
    private GpuTexture _palette;            // 256x1 RGBA
    private GpuTexture _lightRamp;          // 256x64 R8
    private float _brightness;              // 0 = real lighting, 1 = full bright
    private float _ceilZ, _ceilOffX, _ceilOffY, _horizOffX, _horizOffY;   // sky params

    /// <summary>Editor brightness boost (0 = engine-accurate lighting, 1 = full bright).</summary>
    public void SetBrightness(float brightness) => _brightness = System.Math.Clamp(brightness, 0f, 1f);

    /// <summary>Sets the level's sky parameters (ceiling plane height + texture offsets).</summary>
    public void SetSky(float ceilingHeight, float ceilOffX, float ceilOffY, float horizOffX = 0, float horizOffY = 0)
    {
        _ceilZ = ceilingHeight; _ceilOffX = ceilOffX; _ceilOffY = ceilOffY;
        _horizOffX = horizOffX; _horizOffY = horizOffY;
    }

    // Geometry
    private VkBuffer _vertexBuffer; private DeviceMemory _vertexMemory;
    private VkBuffer _indexBuffer; private DeviceMemory _indexMemory;

    // Per-scene
    private DescriptorPool _descPool;
    private readonly List<GpuTexture> _sceneTextures = new();
    private readonly List<DrawItem> _draws = new();
    private readonly Dictionary<string, MatTex> _materialCache = new(StringComparer.OrdinalIgnoreCase);

    // Overlays
    private VkBuffer _selVertexBuffer; private DeviceMemory _selVertexMemory;
    private VkBuffer _selIndexBuffer; private DeviceMemory _selIndexMemory; private uint _selIndexCount;
    private VkBuffer _markerVertexBuffer; private DeviceMemory _markerVertexMemory;
    private VkBuffer _markerIndexBuffer; private DeviceMemory _markerIndexMemory; private uint _markerIndexCount;

    // Targets
    private uint _width, _height;
    private Image _color, _depth; private DeviceMemory _colorMem, _depthMem;
    private ImageView _colorView, _depthView; private Framebuffer _framebuffer;
    private VkBuffer _readback; private DeviceMemory _readbackMem;

    private readonly record struct DrawItem(DescriptorSet Set, uint IndexOffset, uint IndexCount, float InvW, float InvH, bool Translucent, bool Flat, uint SkyMode, uint ClampMode);
    private readonly record struct MatTex(DescriptorSet Set, float InvW, float InvH);
    private struct GpuTexture { public Image Image; public DeviceMemory Memory; public ImageView View; public DescriptorSet Set; }

    public SceneRenderer(VulkanDevice device)
    {
        _dev = device;
        _vk = device.Vk;
        CreateRenderPass();
        CreateSampler();
        CreateDescriptorSetLayout();
        CreatePipelines();

        _white = CreateImage2D(new byte[] { 255 }, 1, 1, Format.R8Unorm, 1);
        // Default colormap: grayscale palette + identity ramp (replaced by SetColormap).
        var defPal = new byte[256 * 3];
        for (int i = 0; i < 256; i++) { defPal[i * 3] = (byte)i; defPal[i * 3 + 1] = (byte)i; defPal[i * 3 + 2] = (byte)i; }
        var defRamp = new byte[64 * 256];
        for (int l = 0; l < 64; l++) for (int i = 0; i < 256; i++) defRamp[l * 256 + i] = (byte)i;
        SetColormap(defPal, defRamp);
    }

    /// <summary>Uploads the level palette (256×3 RGB) and 64×256 light ramp used for shading.</summary>
    public void SetColormap(byte[] paletteRgb, byte[] lightTable)
    {
        DestroyTexture(_palette);
        DestroyTexture(_lightRamp);

        var rgba = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            rgba[i * 4] = paletteRgb[i * 3]; rgba[i * 4 + 1] = paletteRgb[i * 3 + 1];
            rgba[i * 4 + 2] = paletteRgb[i * 3 + 2]; rgba[i * 4 + 3] = 255;
        }
        _palette = CreateImage2D(rgba, 256, 1, Format.R8G8B8A8Unorm, 4);
        _lightRamp = CreateImage2D(lightTable, 256, 64, Format.R8Unorm, 1);
    }

    /// <summary>Untextured convenience: one flat-shaded submesh using vertex colors.</summary>
    public void SetMesh(Mesh mesh)
    {
        var scene = new RenderScene();
        scene.Mesh.Vertices.AddRange(mesh.Vertices);
        scene.Mesh.Indices.AddRange(mesh.Indices);
        scene.Submeshes.Add(new Submesh { Material = "", IndexOffset = 0, IndexCount = mesh.Indices.Count });
        SetScene(scene, _ => null, flat: true);
    }

    public void SetScene(RenderScene scene, TextureLookup lookup) => SetScene(scene, lookup, flat: false);

    private void SetScene(RenderScene scene, TextureLookup lookup, bool flat)
    {
        DestroyScene();
        if (scene.Mesh.IsEmpty) return;

        UploadGeometry(scene);
        CreateDescriptorPool((uint)scene.Submeshes.Count + 1);
        _white.Set = AllocateMaterialDescriptor(_white.View);
        _materialCache.Clear();

        foreach (var sub in scene.Submeshes)
        {
            if (string.IsNullOrEmpty(sub.Material) || _materialCache.ContainsKey(sub.Material)) continue;
            var tex = lookup(sub.Material);
            MatTex mt;
            if (tex is { } t && t.Indices.Length == t.Width * t.Height && t.Width > 0 && t.Height > 0)
            {
                var gpu = CreateImage2D(t.Indices, (uint)t.Width, (uint)t.Height, Format.R8Unorm, 1);
                gpu.Set = AllocateMaterialDescriptor(gpu.View);
                _sceneTextures.Add(gpu);
                mt = new MatTex(gpu.Set, 1f / t.Width, 1f / t.Height);
            }
            else mt = new MatTex(_white.Set, 1f, 1f);
            _materialCache[sub.Material] = mt;
        }

        RebuildDraws(scene, flat);
    }

    /// <summary>Re-uploads geometry after an edit, reusing the loaded textures.</summary>
    public void UpdateGeometry(RenderScene scene)
    {
        DestroyGeometryBuffers();
        if (scene.Mesh.IsEmpty) { _draws.Clear(); return; }
        UploadGeometry(scene);
        RebuildDraws(scene, flat: false);
    }

    private void RebuildDraws(RenderScene scene, bool flat)
    {
        _draws.Clear();
        foreach (var sub in scene.Submeshes)
        {
            var mt = !string.IsNullOrEmpty(sub.Material) && _materialCache.TryGetValue(sub.Material, out var c)
                ? c : new MatTex(_white.Set, 1f, 1f);
            _draws.Add(new DrawItem(mt.Set, (uint)sub.IndexOffset, (uint)sub.IndexCount, mt.InvW, mt.InvH, sub.Translucent, flat, (uint)sub.SkyMode, (uint)sub.ClampMode));
        }
    }

    public void SetSelection(Mesh? mesh) =>
        (_selVertexBuffer, _selVertexMemory, _selIndexBuffer, _selIndexMemory, _selIndexCount) =
            UploadOverlay(mesh, _selVertexBuffer, _selVertexMemory, _selIndexBuffer, _selIndexMemory);

    public void SetMarkers(Mesh? mesh) =>
        (_markerVertexBuffer, _markerVertexMemory, _markerIndexBuffer, _markerIndexMemory, _markerIndexCount) =
            UploadOverlay(mesh, _markerVertexBuffer, _markerVertexMemory, _markerIndexBuffer, _markerIndexMemory);

    private (VkBuffer, DeviceMemory, VkBuffer, DeviceMemory, uint) UploadOverlay(
        Mesh? mesh, VkBuffer oldVb, DeviceMemory oldVm, VkBuffer oldIb, DeviceMemory oldIm)
    {
        var d = _dev.Device;
        if (oldVb.Handle != 0) _vk.DestroyBuffer(d, oldVb, null);
        if (oldVm.Handle != 0) _vk.FreeMemory(d, oldVm, null);
        if (oldIb.Handle != 0) _vk.DestroyBuffer(d, oldIb, null);
        if (oldIm.Handle != 0) _vk.FreeMemory(d, oldIm, null);
        if (mesh is null || mesh.IsEmpty) return (default, default, default, default, 0);

        var verts = mesh.Vertices.ToArray();
        var indices = mesh.Indices.ToArray();
        var (vb, vm) = CreateHostBuffer((ulong)(verts.Length * (int)MeshVertex.Stride), BufferUsageFlags.VertexBufferBit);
        Upload(vm, MemoryMarshal.AsBytes<MeshVertex>(verts));
        var (ib, im) = CreateHostBuffer((ulong)(indices.Length * sizeof(uint)), BufferUsageFlags.IndexBufferBit);
        Upload(im, MemoryMarshal.AsBytes<uint>(indices));
        return (vb, vm, ib, im, (uint)indices.Length);
    }

    public byte[] Render(Mat4 viewProjection, Vec3 camPos, uint width, uint height,
        float clearR = 0.05f, float clearG = 0.05f, float clearB = 0.08f) =>
        Render(viewProjection, camPos, Vec3.Zero, width, height, clearR, clearG, clearB);

    /// <summary>Renders one frame; <paramref name="camAngles"/> = (yaw, pitch, roll) degrees for sky.</summary>
    public byte[] Render(Mat4 viewProjection, Vec3 camPos, Vec3 camAngles, uint width, uint height,
        float clearR = 0.05f, float clearG = 0.05f, float clearB = 0.08f)
    {
        EnsureTargets(width, height);
        var cmd = BeginOneTimeCommands();

        var clears = stackalloc ClearValue[2];
        clears[0] = new ClearValue { Color = new ClearColorValue(clearR, clearG, clearB, 1f) };
        clears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo, RenderPass = _renderPass, Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_width, _height)),
            ClearValueCount = 2, PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(cmd, in rpBegin, SubpassContents.Inline);

        var viewport = new Viewport(0, 0, _width, _height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(_width, _height));
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        PushHead(cmd, viewProjection, camPos, camAngles, _width, _height);

        // Opaque pass.
        if (_draws.Count > 0)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            BindSceneBuffers(cmd);
            foreach (var draw in _draws)
                if (!draw.Translucent) DrawSubmesh(cmd, draw, draw.Flat ? 1u : 0u, 1f);
        }

        // Thing markers (flat).
        if (_markerIndexCount > 0 && _white.Set.Handle != 0)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            BindOverlay(cmd, _white.Set, _markerVertexBuffer, _markerIndexBuffer, 1u, 1f);
            _vk.CmdDrawIndexed(cmd, _markerIndexCount, 1, 0, 0, 0);
        }

        // Translucent pass.
        if (_draws.Any(d => d.Translucent))
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _translucentPipeline);
            BindSceneBuffers(cmd);
            foreach (var draw in _draws)
                if (draw.Translucent) DrawSubmesh(cmd, draw, 0u, 0.5f);
        }

        // Selection overlay (flat, depth off).
        if (_selIndexCount > 0 && _white.Set.Handle != 0)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _selectionPipeline);
            BindOverlay(cmd, _white.Set, _selVertexBuffer, _selIndexBuffer, 1u, 1f);
            _vk.CmdDrawIndexed(cmd, _selIndexCount, 1, 0, 0, 0);
        }

        _vk.CmdEndRenderPass(cmd);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new Extent3D(_width, _height, 1),
        };
        _vk.CmdCopyImageToBuffer(cmd, _color, ImageLayout.TransferSrcOptimal, _readback, 1, &region);
        EndSubmitAndWait(cmd);

        int size = (int)(_width * _height * 4);
        var pixels = new byte[size];
        void* mapped;
        _vk.MapMemory(_dev.Device, _readbackMem, 0, (ulong)size, 0, &mapped);
        new Span<byte>(mapped, size).CopyTo(pixels);
        _vk.UnmapMemory(_dev.Device, _readbackMem);
        return pixels;
    }

    private void BindSceneBuffers(CommandBuffer cmd)
    {
        var vb = _vertexBuffer; ulong off = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in off);
        _vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint32);
    }

    private void DrawSubmesh(CommandBuffer cmd, DrawItem draw, uint mode, float alpha)
    {
        var set = draw.Set;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        PushTail(cmd, draw.InvW, draw.InvH, mode, alpha, draw.SkyMode, draw.ClampMode);
        _vk.CmdDrawIndexed(cmd, draw.IndexCount, 1, draw.IndexOffset, 0, 0);
    }

    private void BindOverlay(CommandBuffer cmd, DescriptorSet set, VkBuffer vb, VkBuffer ib, uint mode, float alpha)
    {
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        PushTail(cmd, 1f, 1f, mode, alpha, 0u, 0u);
        ulong off = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in off);
        _vk.CmdBindIndexBuffer(cmd, ib, 0, IndexType.Uint32);
    }

    // Push layout: mvp[0], camPos[64], sky[80], camAngles[96], horizScreen[112] (head 128 B);
    // invTexSize[128], mode[136], alpha[140], brightness[144], skyMode[148],
    // clampMode[152] (tail 32 B incl. padding).
    private void PushHead(CommandBuffer cmd, Mat4 mvp, Vec3 camPos, Vec3 camAngles, uint w, uint h)
    {
        var buf = stackalloc byte[128];
        fixed (float* m = mvp.M) System.Buffer.MemoryCopy(m, buf, 64, 64);
        *(float*)(buf + 64) = (float)camPos.X; *(float*)(buf + 68) = (float)camPos.Y; *(float*)(buf + 72) = (float)camPos.Z; *(float*)(buf + 76) = 0f;
        *(float*)(buf + 80) = _ceilZ; *(float*)(buf + 84) = _ceilOffX; *(float*)(buf + 88) = _ceilOffY; *(float*)(buf + 92) = 0f;
        *(float*)(buf + 96) = (float)camAngles.X; *(float*)(buf + 100) = (float)camAngles.Y; *(float*)(buf + 104) = (float)camAngles.Z; *(float*)(buf + 108) = 0f;
        *(float*)(buf + 112) = _horizOffX; *(float*)(buf + 116) = _horizOffY; *(float*)(buf + 120) = w; *(float*)(buf + 124) = h;
        _vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 128, buf);
    }

    private void PushTail(CommandBuffer cmd, float invW, float invH, uint mode, float alpha, uint skyMode, uint clampMode)
    {
        var buf = stackalloc byte[32];
        *(float*)(buf + 0) = invW; *(float*)(buf + 4) = invH; *(uint*)(buf + 8) = mode;
        *(float*)(buf + 12) = alpha; *(float*)(buf + 16) = _brightness; *(uint*)(buf + 20) = skyMode;
        *(uint*)(buf + 24) = clampMode;
        _vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 128, 32, buf);
    }

    // ---- setup ----

    private void CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat, Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = DepthFormat, Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1, PColorAttachments = &colorRef, PDepthStencilAttachment = &depthRef,
        };
        var dep = new SubpassDependency
        {
            SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit, SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.TransferBit, DstAccessMask = AccessFlags.TransferReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo, AttachmentCount = 2, PAttachments = attachments,
            SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 1, PDependencies = &dep,
        };
        if (_vk.CreateRenderPass(_dev.Device, in info, null, out _renderPass) != Result.Success)
            throw new VulkanException("vkCreateRenderPass failed");
    }

    private void CreateSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest, MinFilter = Filter.Nearest, MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat, AddressModeV = SamplerAddressMode.Repeat, AddressModeW = SamplerAddressMode.Repeat,
        };
        if (_vk.CreateSampler(_dev.Device, in info, null, out _sampler) != Result.Success)
            throw new VulkanException("vkCreateSampler failed");
    }

    private void CreateDescriptorSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i, DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
            };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings,
        };
        if (_vk.CreateDescriptorSetLayout(_dev.Device, in info, null, out _setLayout) != Result.Success)
            throw new VulkanException("vkCreateDescriptorSetLayout failed");
    }

    private void CreateDescriptorPool(uint maxSets)
    {
        DestroyDescriptorPool();
        var size = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = maxSets * 3 };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, MaxSets = maxSets, PoolSizeCount = 1, PPoolSizes = &size,
        };
        if (_vk.CreateDescriptorPool(_dev.Device, in info, null, out _descPool) != Result.Success)
            throw new VulkanException("vkCreateDescriptorPool failed");
    }

    private DescriptorSet AllocateMaterialDescriptor(ImageView matView)
    {
        var layout = _setLayout;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool,
            DescriptorSetCount = 1, PSetLayouts = &layout,
        };
        if (_vk.AllocateDescriptorSets(_dev.Device, in alloc, out var set) != Result.Success)
            throw new VulkanException("vkAllocateDescriptorSets failed");

        var infos = stackalloc DescriptorImageInfo[3];
        infos[0] = new DescriptorImageInfo { Sampler = _sampler, ImageView = matView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[1] = new DescriptorImageInfo { Sampler = _sampler, ImageView = _palette.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[2] = new DescriptorImageInfo { Sampler = _sampler, ImageView = _lightRamp.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc WriteDescriptorSet[3];
        for (uint i = 0; i < 3; i++)
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = i, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[i],
            };
        _vk.UpdateDescriptorSets(_dev.Device, 3, writes, 0, null);
        return set;
    }

    private void CreatePipelines()
    {
        var vert = LoadShaderModule("mesh.vert.spv");
        var frag = LoadShaderModule("mesh.frag.spv");
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vert, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = frag, PName = entry };

            var binding = new VertexInputBindingDescription { Binding = 0, Stride = MeshVertex.Stride, InputRate = VertexInputRate.Vertex };
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32Sfloat, 24);
            attrs[3] = new VertexInputAttributeDescription(3, 0, Format.R32G32Sfloat, 36);
            var vin = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4, PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };

            var depthOn = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less };
            var depthTestNoWrite = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.Less };
            var depthOff = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always };

            var noBlend = new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            var alphaBlend = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.Zero, AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blendOpaque = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &noBlend };
            var blendAlpha = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &alphaBlend };

            var dyn = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynState = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };

            var push = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 160);
            var setLayout = _setLayout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &push,
            };
            if (_vk.CreatePipelineLayout(_dev.Device, in layoutInfo, null, out _layout) != Result.Success)
                throw new VulkanException("vkCreatePipelineLayout failed");

            var baseInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &vin, PInputAssemblyState = &ia, PViewportState = &vp, PRasterizationState = &rs,
                PMultisampleState = &ms, PDynamicState = &dynState,
                Layout = _layout, RenderPass = _renderPass, Subpass = 0,
            };

            var iOpaque = baseInfo; iOpaque.PDepthStencilState = &depthOn; iOpaque.PColorBlendState = &blendOpaque;
            if (_vk.CreateGraphicsPipelines(_dev.Device, default, 1, in iOpaque, null, out _pipeline) != Result.Success)
                throw new VulkanException("opaque pipeline failed");
            var iTrans = baseInfo; iTrans.PDepthStencilState = &depthTestNoWrite; iTrans.PColorBlendState = &blendAlpha;
            if (_vk.CreateGraphicsPipelines(_dev.Device, default, 1, in iTrans, null, out _translucentPipeline) != Result.Success)
                throw new VulkanException("translucent pipeline failed");
            var iSel = baseInfo; iSel.PDepthStencilState = &depthOff; iSel.PColorBlendState = &blendOpaque;
            if (_vk.CreateGraphicsPipelines(_dev.Device, default, 1, in iSel, null, out _selectionPipeline) != Result.Success)
                throw new VulkanException("selection pipeline failed");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_dev.Device, vert, null);
            _vk.DestroyShaderModule(_dev.Device, frag, null);
        }
    }

    // ---- images / buffers ----

    private GpuTexture CreateImage2D(byte[] data, uint w, uint h, Format format, int bpp)
    {
        var imgInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format,
            Extent = new Extent3D(w, h, 1), MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal, Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        if (_vk.CreateImage(_dev.Device, in imgInfo, null, out var image) != Result.Success)
            throw new VulkanException("vkCreateImage failed");
        _vk.GetImageMemoryRequirements(_dev.Device, image, out var reqs);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = reqs.Size, MemoryTypeIndex = _dev.FindMemoryType(reqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        if (_vk.AllocateMemory(_dev.Device, in alloc, null, out var memory) != Result.Success)
            throw new VulkanException("vkAllocateMemory (image) failed");
        _vk.BindImageMemory(_dev.Device, image, memory, 0);

        var (staging, stagingMem) = CreateHostBuffer((ulong)(w * h * bpp), BufferUsageFlags.TransferSrcBit);
        Upload(stagingMem, data);

        var cmd = BeginOneTimeCommands();
        Transition(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, AccessFlags.TransferWriteBit);
        var copy = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = new Extent3D(w, h, 1) };
        _vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, 1, &copy);
        Transition(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        EndSubmitAndWait(cmd);
        _vk.DestroyBuffer(_dev.Device, staging, null);
        _vk.FreeMemory(_dev.Device, stagingMem, null);

        var viewInfo = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
        if (_vk.CreateImageView(_dev.Device, in viewInfo, null, out var view) != Result.Success)
            throw new VulkanException("vkCreateImageView failed");
        return new GpuTexture { Image = image, Memory = memory, View = view };
    }

    private void Transition(CommandBuffer cmd, Image image, ImageLayout oldL, ImageLayout newL, PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcA, AccessFlags dstA)
    {
        var b = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = oldL, NewLayout = newL,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image, SrcAccessMask = srcA, DstAccessMask = dstA,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _vk.CmdPipelineBarrier(cmd, src, dst, 0, 0, null, 0, null, 1, in b);
    }

    private void EnsureTargets(uint width, uint height)
    {
        if (width == _width && height == _height && _framebuffer.Handle != 0) return;
        DestroyTargets();
        _width = width; _height = height;
        (_color, _colorMem, _colorView) = CreateAttachment(ColorFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        (_depth, _depthMem, _depthView) = CreateAttachment(DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        var views = stackalloc ImageView[2] { _colorView, _depthView };
        var fb = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = _renderPass, AttachmentCount = 2, PAttachments = views, Width = _width, Height = _height, Layers = 1 };
        if (_vk.CreateFramebuffer(_dev.Device, in fb, null, out _framebuffer) != Result.Success)
            throw new VulkanException("vkCreateFramebuffer failed");
        (_readback, _readbackMem) = CreateHostBuffer((ulong)(_width * _height * 4), BufferUsageFlags.TransferDstBit);
    }

    private (Image, DeviceMemory, ImageView) CreateAttachment(Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        var info = new ImageCreateInfo { SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(_width, _height, 1), MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined };
        if (_vk.CreateImage(_dev.Device, in info, null, out var image) != Result.Success) throw new VulkanException("vkCreateImage failed");
        _vk.GetImageMemoryRequirements(_dev.Device, image, out var reqs);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = reqs.Size, MemoryTypeIndex = _dev.FindMemoryType(reqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        if (_vk.AllocateMemory(_dev.Device, in alloc, null, out var memory) != Result.Success) throw new VulkanException("vkAllocateMemory failed");
        _vk.BindImageMemory(_dev.Device, image, memory, 0);
        var viewInfo = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1) };
        if (_vk.CreateImageView(_dev.Device, in viewInfo, null, out var view) != Result.Success) throw new VulkanException("vkCreateImageView failed");
        return (image, memory, view);
    }

    private void UploadGeometry(RenderScene scene)
    {
        var verts = scene.Mesh.Vertices.ToArray();
        var indices = scene.Mesh.Indices.ToArray();
        (_vertexBuffer, _vertexMemory) = CreateHostBuffer((ulong)(verts.Length * (int)MeshVertex.Stride), BufferUsageFlags.VertexBufferBit);
        Upload(_vertexMemory, MemoryMarshal.AsBytes<MeshVertex>(verts));
        (_indexBuffer, _indexMemory) = CreateHostBuffer((ulong)(indices.Length * sizeof(uint)), BufferUsageFlags.IndexBufferBit);
        Upload(_indexMemory, MemoryMarshal.AsBytes<uint>(indices));
    }

    private (VkBuffer, DeviceMemory) CreateHostBuffer(ulong size, BufferUsageFlags usage)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        if (_vk.CreateBuffer(_dev.Device, in info, null, out var buffer) != Result.Success) throw new VulkanException("vkCreateBuffer failed");
        _vk.GetBufferMemoryRequirements(_dev.Device, buffer, out var reqs);
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = reqs.Size, MemoryTypeIndex = _dev.FindMemoryType(reqs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit) };
        if (_vk.AllocateMemory(_dev.Device, in alloc, null, out var memory) != Result.Success) throw new VulkanException("vkAllocateMemory (buffer) failed");
        _vk.BindBufferMemory(_dev.Device, buffer, memory, 0);
        return (buffer, memory);
    }

    private void Upload(DeviceMemory memory, ReadOnlySpan<byte> data)
    {
        void* mapped;
        _vk.MapMemory(_dev.Device, memory, 0, (ulong)data.Length, 0, &mapped);
        data.CopyTo(new Span<byte>(mapped, data.Length));
        _vk.UnmapMemory(_dev.Device, memory);
    }

    private ShaderModule LoadShaderModule(string nameSuffix)
    {
        var asm = typeof(SceneRenderer).GetTypeInfo().Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(nameSuffix, StringComparison.Ordinal)) ?? throw new VulkanException($"shader '{nameSuffix}' not found");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var code = ms.ToArray();
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            if (_vk.CreateShaderModule(_dev.Device, in info, null, out var module) != Result.Success) throw new VulkanException($"vkCreateShaderModule {nameSuffix}");
            return module;
        }
    }

    private CommandBuffer BeginOneTimeCommands()
    {
        var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _dev.CommandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        _vk.AllocateCommandBuffers(_dev.Device, in alloc, out var cmd);
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, in begin);
        return cmd;
    }

    private void EndSubmitAndWait(CommandBuffer cmd)
    {
        _vk.EndCommandBuffer(cmd);
        var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        _vk.CreateFence(_dev.Device, in fi, null, out var fence);
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        if (_vk.QueueSubmit(_dev.GraphicsQueue, 1, in submit, fence) != Result.Success) throw new VulkanException("vkQueueSubmit failed");
        _vk.WaitForFences(_dev.Device, 1, in fence, true, ulong.MaxValue);
        _vk.DestroyFence(_dev.Device, fence, null);
        _vk.FreeCommandBuffers(_dev.Device, _dev.CommandPool, 1, in cmd);
    }

    // ---- teardown ----

    private void DestroyTexture(GpuTexture t)
    {
        var d = _dev.Device;
        if (t.View.Handle != 0) _vk.DestroyImageView(d, t.View, null);
        if (t.Image.Handle != 0) _vk.DestroyImage(d, t.Image, null);
        if (t.Memory.Handle != 0) _vk.FreeMemory(d, t.Memory, null);
    }

    private void DestroyDescriptorPool()
    {
        if (_descPool.Handle != 0) { _vk.DestroyDescriptorPool(_dev.Device, _descPool, null); _descPool = default; }
    }

    private void DestroyGeometryBuffers()
    {
        var d = _dev.Device;
        if (_vertexBuffer.Handle != 0) { _vk.DestroyBuffer(d, _vertexBuffer, null); _vertexBuffer = default; }
        if (_vertexMemory.Handle != 0) { _vk.FreeMemory(d, _vertexMemory, null); _vertexMemory = default; }
        if (_indexBuffer.Handle != 0) { _vk.DestroyBuffer(d, _indexBuffer, null); _indexBuffer = default; }
        if (_indexMemory.Handle != 0) { _vk.FreeMemory(d, _indexMemory, null); _indexMemory = default; }
    }

    private void DestroyScene()
    {
        foreach (var t in _sceneTextures) DestroyTexture(t);
        _sceneTextures.Clear();
        _draws.Clear();
        _materialCache.Clear();
        DestroyDescriptorPool();
        DestroyGeometryBuffers();
    }

    private void DestroyTargets()
    {
        var d = _dev.Device;
        if (_readback.Handle != 0) { _vk.DestroyBuffer(d, _readback, null); _readback = default; }
        if (_readbackMem.Handle != 0) { _vk.FreeMemory(d, _readbackMem, null); _readbackMem = default; }
        if (_framebuffer.Handle != 0) { _vk.DestroyFramebuffer(d, _framebuffer, null); _framebuffer = default; }
        if (_colorView.Handle != 0) { _vk.DestroyImageView(d, _colorView, null); _colorView = default; }
        if (_depthView.Handle != 0) { _vk.DestroyImageView(d, _depthView, null); _depthView = default; }
        if (_color.Handle != 0) { _vk.DestroyImage(d, _color, null); _color = default; }
        if (_depth.Handle != 0) { _vk.DestroyImage(d, _depth, null); _depth = default; }
        if (_colorMem.Handle != 0) { _vk.FreeMemory(d, _colorMem, null); _colorMem = default; }
        if (_depthMem.Handle != 0) { _vk.FreeMemory(d, _depthMem, null); _depthMem = default; }
    }

    public void Dispose()
    {
        DestroyScene();
        var d = _dev.Device;
        foreach (var b in new[] { _selVertexBuffer, _selIndexBuffer, _markerVertexBuffer, _markerIndexBuffer })
            if (b.Handle != 0) _vk.DestroyBuffer(d, b, null);
        foreach (var m in new[] { _selVertexMemory, _selIndexMemory, _markerVertexMemory, _markerIndexMemory })
            if (m.Handle != 0) _vk.FreeMemory(d, m, null);
        DestroyTargets();
        DestroyTexture(_white);
        DestroyTexture(_palette);
        DestroyTexture(_lightRamp);
        if (_selectionPipeline.Handle != 0) _vk.DestroyPipeline(d, _selectionPipeline, null);
        if (_translucentPipeline.Handle != 0) _vk.DestroyPipeline(d, _translucentPipeline, null);
        if (_pipeline.Handle != 0) _vk.DestroyPipeline(d, _pipeline, null);
        if (_layout.Handle != 0) _vk.DestroyPipelineLayout(d, _layout, null);
        if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(d, _setLayout, null);
        if (_sampler.Handle != 0) _vk.DestroySampler(d, _sampler, null);
        if (_renderPass.Handle != 0) _vk.DestroyRenderPass(d, _renderPass, null);
    }
}
