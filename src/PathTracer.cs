using System;
using System.Collections.Generic;
using Godot;

namespace VoxelPoolGodot;

/// <summary>
/// RenderingDevice 渲染管线管理：
/// 焦散光子映射 → 路径追踪（渐进累积）→ tonemap 呈现
/// </summary>
public partial class PathTracer : IDisposable
{
    private RenderingDevice _rd;
    private Rid _pathShader, _causticsShader, _clearShader, _presentShader;
    private Rid _exposureReduceShader, _exposureFinalizeShader, _temporalShader;
    private Rid _pathPipe, _causticsPipe, _clearPipe, _presentPipe;
    private Rid _exposureReducePipe, _exposureFinalizePipe, _temporalPipe;

    private Rid _paramsBuf;                       // 288 字节 UBO
    private Rid _voxelBuf, _sphereBuf, _matBuf;   // 存储缓冲
    private Rid _accumTex, _presentTex, _causticsTex;
    private Rid _giCacheTex, _giSampler;          // GI 辐照缓存（16³ 3D 纹理）
    private Rid _lumBuf, _exposureBuf;            // 自动曝光
    private Rid _frameTex, _posTex;               // 时间重投影：本帧样本/主命中位置
    private Rid _histA, _histB;                   // 历史双缓冲（ping-pong）
    private bool _histFlip = false;

    private Rid _pathSet, _causticsSet, _clearSet, _presentSet;
    private Rid _exposureReduceSet, _exposureFinalizeSet;
    private Rid _temporalSetAB, _temporalSetBA;
    private int _width = 0, _height = 0;
    public const int CausticsRes = 256;   // 降一档：每纹素光子密度×4，平滑度提升
    public const int PhotonGrid = 256;

    public bool Valid => _rd != null;
    public Rid PresentTexture => _presentTex;
    public int Width => _width;
    public int Height => _height;
    private float _scale = 1.0f;                 // 渲染分辨率比例（性能档位）

    /// <summary>设置渲染分辨率比例（0.5/0.75/1.0），纹理按窗口×比例重建</summary>
    public void SetScale(float s)
    {
        _scale = s;
        _width = 0;   // 强制 Resize 重建
    }

    public bool Init(int width, int height)
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) return false;

        // ---- 编译 shader ----
        _pathShader = Compile("res://shaders/pathtrace.glsl.txt");
        _causticsShader = Compile("res://shaders/caustics.glsl.txt");
        _clearShader = Compile("res://shaders/caustics_clear.glsl.txt");
        _presentShader = Compile("res://shaders/present.glsl.txt");
        _exposureReduceShader = Compile("res://shaders/exposure_reduce.glsl.txt");
        _exposureFinalizeShader = Compile("res://shaders/exposure_finalize.glsl.txt");
        _temporalShader = Compile("res://shaders/temporal.glsl.txt");
        if (!_pathShader.IsValid || !_causticsShader.IsValid ||
            !_clearShader.IsValid || !_presentShader.IsValid ||
            !_exposureReduceShader.IsValid || !_exposureFinalizeShader.IsValid ||
            !_temporalShader.IsValid)
        {
            GD.PushError("着色器编译失败");
            return false;
        }

        _pathPipe = _rd.ComputePipelineCreate(_pathShader);
        _causticsPipe = _rd.ComputePipelineCreate(_causticsShader);
        _clearPipe = _rd.ComputePipelineCreate(_clearShader);
        _presentPipe = _rd.ComputePipelineCreate(_presentShader);
        _exposureReducePipe = _rd.ComputePipelineCreate(_exposureReduceShader);
        _exposureFinalizePipe = _rd.ComputePipelineCreate(_exposureFinalizeShader);
        _temporalPipe = _rd.ComputePipelineCreate(_temporalShader);

        // ---- 参数 UBO（18 个 vec4：相机/时间重投影扩展）----
        _paramsBuf = _rd.UniformBufferCreate(288, new byte[288]);

        // ---- 自动曝光缓冲 ----
        _lumBuf = MakeStorageBuffer(new byte[16384 * 4]);       // 块亮度槽位
        _exposureBuf = MakeStorageBuffer(new byte[16]);         // [当前, 目标, 亮度]

        // ---- 焦散纹理（一次性创建）----
        _causticsTex = CreateTex(CausticsRes, CausticsRes,
            RenderingDevice.DataFormat.R32Uint, 4);

        // ---- GI 缓存采样器 + 初始缓存纹理（SetScene 会重建）----
        _giSampler = _rd.SamplerCreate(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MipFilter = RenderingDevice.SamplerFilter.Nearest,
        });
        RecreateGiCache();

        // ---- 占位存储缓冲（Resize 构建绑定时需要有效的 Rid）----
        _voxelBuf = MakeStorageBuffer(new byte[4]);
        _sphereBuf = MakeStorageBuffer(new byte[16]);
        _matBuf = MakeStorageBuffer(new byte[16]);

        // ---- 清零 set ----
        _clearSet = MakeSet(_clearShader, 0, new[] {
            U(RenderingDevice.UniformType.Image, 0, _causticsTex),
        });

        // ---- 累积/呈现纹理 ----
        if (!Resize(width, height)) return false;
        return true;
    }

    private Rid Compile(string path)
    {
        // 直接读取（不用 FileExists——导出 PCK 内嵌文件存在检查的怪癖）
        string src = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(src))
        {
            GD.PushError($"无法读取着色器: {path}");
            return new Rid();
        }
        var source = new RDShaderSource { SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        string err = spirv.CompileErrorCompute;
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"{path} GLSL 编译错误:\n{err}");
            return new Rid();
        }
        return _rd.ShaderCreateFromSpirV(spirv, "");
    }

    private Rid CreateTex(int w, int h, RenderingDevice.DataFormat fmt, int bytesPerPx)
    {
        var f = new RDTextureFormat
        {
            Width = (uint)w,
            Height = (uint)h,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            Format = fmt,
            Samples = RenderingDevice.TextureSamples.Samples1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.StorageBit,
        };
        var data = new Godot.Collections.Array<byte[]> { new byte[w * h * bytesPerPx] };
        return _rd.TextureCreate(f, new RDTextureView(), data);
    }

    /// <summary>窗口尺寸变化时重建累积/呈现纹理与相关绑定组</summary>
    public bool Resize(int width, int height)
    {
        int w = Mathf.Max(2, (int)(width * _scale));
        int h = Mathf.Max(2, (int)(height * _scale));
        if (w == _width && h == _height && _presentTex.IsValid) return true;
        _width = w; _height = h;

        if (_accumTex.IsValid) _rd.FreeRid(_accumTex);
        if (_presentTex.IsValid) _rd.FreeRid(_presentTex);
        _accumTex = CreateTex(w, h, RenderingDevice.DataFormat.R16G16B16A16Sfloat, 8);
        _presentTex = CreateTex(w, h, RenderingDevice.DataFormat.R8G8B8A8Unorm, 4);
        _frameTex = CreateTex(w, h, RenderingDevice.DataFormat.R16G16B16A16Sfloat, 8);
        _posTex = CreateTex(w, h, RenderingDevice.DataFormat.R16G16B16A16Sfloat, 8);
        _histA = CreateTex(w, h, RenderingDevice.DataFormat.R16G16B16A16Sfloat, 8);
        _histB = CreateTex(w, h, RenderingDevice.DataFormat.R16G16B16A16Sfloat, 8);
        RebuildSets();
        return true;
    }

    /// <summary>GI 辐照缓存（16³ 探针，场景/编辑变化时重建重收敛）</summary>
    private void RecreateGiCache()
    {
        if (_giCacheTex.IsValid) _rd.FreeRid(_giCacheTex);
        var f = new RDTextureFormat
        {
            Width = 16, Height = 16, Depth = 16,
            ArrayLayers = 1, Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type3D,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            Samples = RenderingDevice.TextureSamples.Samples1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.StorageBit,
        };
        var data = new Godot.Collections.Array<byte[]> { new byte[16 * 16 * 16 * 8] };
        _giCacheTex = _rd.TextureCreate(f, new RDTextureView(), data);
    }

    /// <summary>上传场景数据（体素/球体/材质），并重建绑定组</summary>
    public void SetScene(byte[] voxelWordsPacked, float[] sphereData, float[] matData)
    {
        if (_voxelBuf.IsValid) _rd.FreeRid(_voxelBuf);
        if (_sphereBuf.IsValid) _rd.FreeRid(_sphereBuf);
        if (_matBuf.IsValid) _rd.FreeRid(_matBuf);

        _voxelBuf = MakeStorageBuffer(voxelWordsPacked);
        _sphereBuf = MakeStorageBuffer(FloatsToBytes(sphereData));
        _matBuf = MakeStorageBuffer(FloatsToBytes(matData));
        RecreateGiCache();
        RebuildSets();
        ClearCaustics();
    }

    /// <summary>体素编辑后增量更新（只重传体素缓冲并清焦散）</summary>
    public void UpdateVoxels(byte[] voxelWordsPacked)
    {
        if (_voxelBuf.IsValid) _rd.FreeRid(_voxelBuf);
        _voxelBuf = MakeStorageBuffer(voxelWordsPacked);
        RecreateGiCache();
        RebuildSets();
        ClearCaustics();
    }

    private void RebuildSets()
    {
        // 路径追踪：UBO + 体素 + 球 + 材质 + 累积图 + 焦散图(只读) + GI缓存
        var giU = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 6 };
        giU.AddId(_giSampler);
        giU.AddId(_giCacheTex);
        _pathSet = MakeSet(_pathShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 1, _voxelBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 2, _sphereBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 3, _matBuf),
            U(RenderingDevice.UniformType.Image, 4, _accumTex),
            U(RenderingDevice.UniformType.Image, 5, _causticsTex),
            giU,
            U(RenderingDevice.UniformType.Image, 7, _giCacheTex),
            U(RenderingDevice.UniformType.Image, 8, _frameTex),
            U(RenderingDevice.UniformType.Image, 9, _posTex),
        });
        // 焦散：UBO + 体素 + 焦散图(读写)
        _causticsSet = MakeSet(_causticsShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 1, _voxelBuf),
            U(RenderingDevice.UniformType.Image, 2, _causticsTex),
        });
        // 清零
        _clearSet = MakeSet(_clearShader, 0, new[] {
            U(RenderingDevice.UniformType.Image, 0, _causticsTex),
        });
        // 呈现
        _presentSet = MakeSet(_presentShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.Image, 1, _accumTex),
            U(RenderingDevice.UniformType.Image, 2, _presentTex),
            U(RenderingDevice.UniformType.StorageBuffer, 3, _exposureBuf),
        });
        // 自动曝光：归约 + 汇总
        _exposureReduceSet = MakeSet(_exposureReduceShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.Image, 1, _accumTex),
            U(RenderingDevice.UniformType.StorageBuffer, 2, _lumBuf),
        });
        _exposureFinalizeSet = MakeSet(_exposureFinalizeShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 1, _lumBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 2, _exposureBuf),
        });
        // 时间重投影（两个方向的 ping-pong set 各建一份，运行时切换）
        var thU1 = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 };
        thU1.AddId(_giSampler);
        thU1.AddId(_histA);
        _temporalSetAB = MakeSet(_temporalShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.Image, 1, _frameTex),
            U(RenderingDevice.UniformType.Image, 2, _posTex),
            thU1,
            U(RenderingDevice.UniformType.Image, 4, _histB),
            U(RenderingDevice.UniformType.Image, 5, _accumTex),
        });
        var thU2 = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 };
        thU2.AddId(_giSampler);
        thU2.AddId(_histB);
        _temporalSetBA = MakeSet(_temporalShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.Image, 1, _frameTex),
            U(RenderingDevice.UniformType.Image, 2, _posTex),
            thU2,
            U(RenderingDevice.UniformType.Image, 4, _histA),
            U(RenderingDevice.UniformType.Image, 5, _accumTex),
        });
    }

    private bool _pendingCausticsClear = false;
    private uint _causticsFrames = 0;

    /// <summary>焦散光子已累积的帧数（解码时按此归一化）</summary>
    public uint CausticsFrames => _causticsFrames;

    /// <summary>场景切换时把自动曝光重置到基准值（避免狂跳）</summary>
    public void ResetExposure(float baseExposure)
    {
        if (!_exposureBuf.IsValid) return;
        var b = new byte[16];
        BitConverter.GetBytes(baseExposure).CopyTo(b, 0);
        BitConverter.GetBytes(baseExposure).CopyTo(b, 4);
        _rd.BufferUpdate(_exposureBuf, 0, 16, b);
    }

    public void ClearCaustics()
    {
        _pendingCausticsClear = true;
        _causticsFrames = 0;
    }

    /// <summary>提交本帧计算：清零(可选) → 焦散光子 → 路径追踪 ×spp →
    /// (移动时: 时间重投影混合) → 自动曝光 → tonemap 呈现。
    /// moving=true 时 pathtrace 写帧纹理，temporal 与历史混合后写累积。</summary>
    public void Render(byte[] paramsBytes, bool causticsOn, int spp = 1, bool moving = false)
    {
        var work = (byte[])paramsBytes.Clone();
        float sampleBase = BitConverter.ToSingle(work, 80);   // uFrame.x 偏移 5×16
        // uTemporal（第 14 个 vec4，偏移 13×16=208）：x=moving, y=alpha, z=spp, w=无历史
        BitConverter.GetBytes(moving ? 1f : 0f).CopyTo(work, 208);
        BitConverter.GetBytes(0.15f).CopyTo(work, 212);
        BitConverter.GetBytes((float)spp).CopyTo(work, 216);
        BitConverter.GetBytes(_noHistory ? 1f : 0f).CopyTo(work, 220);

        _rd.BufferUpdate(_paramsBuf, 0, (uint)work.Length, work);

        long cl = _rd.ComputeListBegin();
        if (_pendingCausticsClear)
        {
            _rd.ComputeListBindComputePipeline(cl, _clearPipe);
            _rd.ComputeListBindUniformSet(cl, _clearSet, 0);
            _rd.ComputeListDispatch(cl, (uint)((CausticsRes + 15) / 16), (uint)((CausticsRes + 15) / 16), 1);
            _pendingCausticsClear = false;
        }
        if (causticsOn)
        {
            _rd.ComputeListBindComputePipeline(cl, _causticsPipe);
            _rd.ComputeListBindUniformSet(cl, _causticsSet, 0);
            _rd.ComputeListDispatch(cl, (uint)(PhotonGrid / 16), (uint)(PhotonGrid / 16), 1);
            _causticsFrames++;
        }
        _rd.ComputeListBindComputePipeline(cl, _pathPipe);
        _rd.ComputeListBindUniformSet(cl, _pathSet, 0);
        _rd.ComputeListDispatch(cl, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        _rd.ComputeListEnd();

        // 追加采样：每个独立 compute list（UBO 需在提交间更新）
        for (int i = 1; i < spp; i++)
        {
            BitConverter.GetBytes(sampleBase + i).CopyTo(work, 80);
            _rd.BufferUpdate(_paramsBuf, 0, (uint)work.Length, work);
            cl = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(cl, _pathPipe);
            _rd.ComputeListBindUniformSet(cl, _pathSet, 0);
            _rd.ComputeListDispatch(cl, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
            _rd.ComputeListEnd();
        }

        // 移动时：时间重投影（帧样本 + 历史 → 累积）；静止则路径追踪已直接累加
        if (moving)
        {
            BitConverter.GetBytes(1f).CopyTo(work, 80);   // 累积除数 = 1
            _rd.BufferUpdate(_paramsBuf, 0, (uint)work.Length, work);
            cl = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(cl, _temporalPipe);
            _rd.ComputeListBindUniformSet(cl, _histFlip ? _temporalSetBA : _temporalSetAB, 0);
            _rd.ComputeListDispatch(cl, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
            _rd.ComputeListEnd();
            _histFlip = !_histFlip;
            _noHistory = false;
        }
        else
        {
            _noHistory = true;   // 下次移动的第一帧无有效历史
            BitConverter.GetBytes(sampleBase + spp).CopyTo(work, 80);
            _rd.BufferUpdate(_paramsBuf, 0, (uint)work.Length, work);
        }

        cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _exposureReducePipe);
        _rd.ComputeListBindUniformSet(cl, _exposureReduceSet, 0);
        _rd.ComputeListDispatch(cl, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        _rd.ComputeListEnd();

        cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _exposureFinalizePipe);
        _rd.ComputeListBindUniformSet(cl, _exposureFinalizeSet, 0);
        _rd.ComputeListDispatch(cl, 1, 1, 1);
        _rd.ComputeListEnd();

        cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _presentPipe);
        _rd.ComputeListBindUniformSet(cl, _presentSet, 0);
        _rd.ComputeListDispatch(cl, (uint)((_width + 15) / 16), (uint)((_height + 15) / 16), 1);
        _rd.ComputeListEnd();
    }

    private bool _noHistory = true;

    // ---- 工具 ----
    private Rid MakeStorageBuffer(byte[] data)
    {
        return _rd.StorageBufferCreate((uint)data.Length, data,
            (RenderingDevice.StorageBufferUsage)0);
    }

    private Rid MakeSet(Rid shader, uint index, RDUniform[] uniforms)
    {
        var arr = new Godot.Collections.Array<RDUniform>();
        foreach (var u in uniforms) arr.Add(u);
        return _rd.UniformSetCreate(arr, shader, index);
    }

    private static RDUniform U(RenderingDevice.UniformType type, int binding, Rid id)
    {
        var u = new RDUniform { UniformType = type, Binding = binding };
        u.AddId(id);
        return u;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        for (int i = 0; i < floats.Length; i++)
            BitConverter.GetBytes(floats[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    public void Dispose()
    {
        if (_rd == null) return;
        foreach (var rid in new[] { _pathShader, _causticsShader, _clearShader, _presentShader,
                                    _paramsBuf, _voxelBuf, _sphereBuf, _matBuf,
                                    _accumTex, _presentTex, _causticsTex })
            if (rid.IsValid) _rd.FreeRid(rid);
    }
}
