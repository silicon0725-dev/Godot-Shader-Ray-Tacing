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
    private Rid _exposureReduceShader, _exposureFinalizeShader, _temporalShader, _bakeShader;
    private Rid _pathPipe, _causticsPipe, _clearPipe, _presentPipe;
    private Rid _exposureReducePipe, _exposureFinalizePipe, _temporalPipe, _bakePipe;

    private Rid _paramsBuf;                       // 288 字节 UBO
    private Rid _voxelBuf, _sphereBuf, _matBuf;   // 存储缓冲
    private Rid _accumTex, _presentTex, _causticsTex;
    private Rid _giCacheTex, _giSampler;          // GI 辐照缓存（16³ 3D 纹理）
    private Rid _lumBuf, _exposureBuf;            // 自动曝光
    private Rid _emissiveBuf;                     // 发光体素列表（解析 NEE 面光源）
    private uint _emissiveCount = 0;
    // 三角网格（BVH 版）
    private Rid _meshVertBuf, _meshTriBuf, _meshBvhABuf, _meshBvhBBuf, _meshMatBuf;
    private bool _hasMesh = false;
    private Rid _frameTex, _posTex;               // 时间重投影：本帧样本/主命中位置
    private Rid _histA, _histB;                   // 历史双缓冲（ping-pong）
    private bool _histFlip = false;

    private Rid _pathSet, _causticsSet, _clearSet, _presentSet;
    private Rid _exposureReduceSet, _exposureFinalizeSet;
    private Rid _temporalSetAB, _temporalSetBA;
    private Rid _bakeSet;
    private int _bakePending = 0;       // 待烘焙迭代数
    private uint _frameTick = 0;
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
        _bakeShader = Compile("res://shaders/bake.glsl.txt");
        if (!_pathShader.IsValid || !_causticsShader.IsValid ||
            !_clearShader.IsValid || !_presentShader.IsValid ||
            !_exposureReduceShader.IsValid || !_exposureFinalizeShader.IsValid ||
            !_temporalShader.IsValid || !_bakeShader.IsValid)
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
        _bakePipe = _rd.ComputePipelineCreate(_bakeShader);

        // ---- 参数 UBO（19 个 vec4：相机/时间重投影/烘焙扩展）----
        _paramsBuf = _rd.UniformBufferCreate(304, new byte[304]);

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
        _emissiveBuf = MakeStorageBuffer(new byte[16]);

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
        // 简单 #include 解析（res://shaders/ 相对，单层展开）
        var includeMatches = System.Text.RegularExpressions.Regex.Matches(src, "#include \"([^\"]+)\"");
        foreach (System.Text.RegularExpressions.Match inc in includeMatches)
        {
            string incPath = path.Substring(0, path.LastIndexOf('/') + 1) + inc.Groups[1].Value;
            string incSrc = FileAccess.GetFileAsString(incPath);
            src = src.Replace(inc.Value, incSrc);
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
        var sphereBytes = FloatsToBytes(sphereData);
        _sphereBuf = MakeStorageBuffer(sphereBytes.Length > 0 ? sphereBytes : new byte[16]);
        _matBuf = MakeStorageBuffer(FloatsToBytes(matData));
        BuildEmissiveList(voxelWordsPacked, matData);
        RecreateGiCache();
        RebuildSets();
        ClearCaustics();
        StartBake(48);   // 预制光照：加载后预收敛 GI 缓存
    }

    /// <summary>扫描体素构建发光体素列表（供解析 NEE 面光源）</summary>
    private void BuildEmissiveList(byte[] voxelWordsPacked, float[] matData)
    {
        if (_emissiveBuf.IsValid) _rd.FreeRid(_emissiveBuf);
        var list = new System.Collections.Generic.List<float>();
        int sx = 32, sy = 32, sz = 32;
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++)
                for (int x = 0; x < sx; x++)
                {
                    int idx = x + y * sx + z * sx * sy;
                    byte m = voxelWordsPacked[idx];
                    bool emissive = m == 9 || (m >= 20 && m <= 25);
                    if (!emissive) continue;
                    // 中心 + 发光色（材质表 [id*8+4..6]）
                    list.Add(x + 0.5f); list.Add(y + 0.5f); list.Add(z + 0.5f); list.Add(0);
                    list.Add(matData[m * 8 + 4]); list.Add(matData[m * 8 + 5]);
                    list.Add(matData[m * 8 + 6]); list.Add(0);
                }
        _emissiveCount = (uint)(list.Count / 8);
        var data = new byte[list.Count * 4];
        for (int i = 0; i < list.Count; i++)
            BitConverter.GetBytes(list[i]).CopyTo(data, i * 4);
        _emissiveBuf = _emissiveCount > 0 ? MakeStorageBuffer(data)
                                          : MakeStorageBuffer(new byte[16]);
    }

    public uint EmissiveCount => _emissiveCount;
    public bool HasMesh => _hasMesh;

    /// <summary>加载 OBJ 三角网格（顶点/BVH/材质）上传 GPU</summary>
    public void LoadMesh(string objPath, float scale, Vector3 offset, float rotYDeg = 0f)
    {
        MeshLoader.LoadObj(objPath, scale, offset,
            out var verts, out var tris, out _, out var matNames, out var meshMin, out var meshMax,
            rotYDeg);
        // BVH（uint 索引版）：全量三角形无抽稀，每射线仅 ~40 次 AABB 测试
        MeshLoader.BuildBVH(verts, tris, out var bvhNodes, out var bvhMeta, out var bvhTris);
        // MTL 路径 = OBJ 同名 .mtl
        var mtlPath = System.IO.Path.ChangeExtension(objPath, ".mtl");
        var meshMats = MeshLoader.BuildMeshMaterials(matNames, mtlPath);

        if (_meshVertBuf.IsValid) _rd.FreeRid(_meshVertBuf);
        if (_meshBvhABuf.IsValid) _rd.FreeRid(_meshBvhABuf);
        if (_meshBvhBBuf.IsValid) _rd.FreeRid(_meshBvhBBuf);
        if (_meshTriBuf.IsValid) _rd.FreeRid(_meshTriBuf);
        if (_meshMatBuf.IsValid) _rd.FreeRid(_meshMatBuf);

        _meshVertBuf = MakeStorageBuffer(FloatsToBytes(verts));
        // BVH 节点 AABB（float）+ 元数据/三角形（uint）
        _meshBvhABuf = MakeStorageBuffer(FloatsToBytes(bvhNodes));
        var metaBytes = new byte[bvhMeta.Length * 4];
        System.Buffer.BlockCopy(bvhMeta, 0, metaBytes, 0, metaBytes.Length);
        _meshBvhBBuf = MakeStorageBuffer(metaBytes);
        var triBytes = new byte[bvhTris.Length * 4];
        System.Buffer.BlockCopy(bvhTris, 0, triBytes, 0, triBytes.Length);
        _meshTriBuf = MakeStorageBuffer(triBytes);
        _meshMatBuf = MakeStorageBuffer(FloatsToBytes(meshMats));

        _hasMesh = true;
        RebuildSets();   // pathSet 需要新绑定
        GD.Print($"网格模型已加载: {verts.Length / 4} 顶点, {bvhTris.Length / 4} 三角形 (BVH)");
        GD.Print($"  世界包围盒: ({meshMin.X:F1},{meshMin.Y:F1},{meshMin.Z:F1}) → ({meshMax.X:F1},{meshMax.Y:F1},{meshMax.Z:F1})");
    }

    /// <summary>预制光照：排定 N 轮烘焙迭代（每帧最多跑 4 轮）</summary>
    public void StartBake(int iterations) => _bakePending = Math.Max(_bakePending, iterations);

    /// <summary>体素编辑后增量更新（只重传体素缓冲并清焦散）</summary>
    public void UpdateVoxels(byte[] voxelWordsPacked)
    {
        if (_voxelBuf.IsValid) _rd.FreeRid(_voxelBuf);
        _voxelBuf = MakeStorageBuffer(voxelWordsPacked);
        RecreateGiCache();
        RebuildSets();
        ClearCaustics();
        StartBake(32);   // 编辑后重烘
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
            U(RenderingDevice.UniformType.StorageBuffer, 10, _emissiveBuf),
            // 三角网格（BVH）：占位缓冲保证绑定有效，LoadMesh 后替换
            // 11=顶点 12=节点AABB(float) 13=节点元数据(uint) 14=重排三角形(uint) 15=材质
            U(RenderingDevice.UniformType.StorageBuffer, 11,
              _meshVertBuf.IsValid ? _meshVertBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 12,
              _meshBvhABuf.IsValid ? _meshBvhABuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 13,
              _meshBvhBBuf.IsValid ? _meshBvhBBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 14,
              _meshTriBuf.IsValid ? _meshTriBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 15,
              _meshMatBuf.IsValid ? _meshMatBuf : MakeStorageBuffer(new byte[16])),
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
        // 烘焙（与 pathtrace 共用体素/球/材质/焦散/GI缓存）
        var bakeGiU = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 6 };
        bakeGiU.AddId(_giSampler);
        bakeGiU.AddId(_giCacheTex);
        _bakeSet = MakeSet(_bakeShader, 0, new[] {
            U(RenderingDevice.UniformType.UniformBuffer, 0, _paramsBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 1, _voxelBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 2, _sphereBuf),
            U(RenderingDevice.UniformType.StorageBuffer, 3, _matBuf),
            U(RenderingDevice.UniformType.Image, 5, _causticsTex),
            bakeGiU,
            U(RenderingDevice.UniformType.Image, 7, _giCacheTex),
            U(RenderingDevice.UniformType.StorageBuffer, 10, _emissiveBuf),
            // 网格 BVH（bake 的 shadowTransmittance 经 meshOccludes 引用）
            U(RenderingDevice.UniformType.StorageBuffer, 11,
              _meshVertBuf.IsValid ? _meshVertBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 12,
              _meshBvhABuf.IsValid ? _meshBvhABuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 13,
              _meshBvhBBuf.IsValid ? _meshBvhBBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 14,
              _meshTriBuf.IsValid ? _meshTriBuf : MakeStorageBuffer(new byte[16])),
            U(RenderingDevice.UniformType.StorageBuffer, 15,
              _meshMatBuf.IsValid ? _meshMatBuf : MakeStorageBuffer(new byte[16])),
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
        // 预制光照烘焙：待定迭代逐帧消化（每帧 ≤4 轮）+ 每 180 帧微烘 2 轮
        _frameTick++;
        if (_frameTick % 180u == 0u && _bakePending == 0) _bakePending = 2;
        for (int b = 0; b < 4 && _bakePending > 0; b++)
        {
            _rd.ComputeListBindComputePipeline(cl, _bakePipe);
            _rd.ComputeListBindUniformSet(cl, _bakeSet, 0);
            _rd.ComputeListDispatch(cl, 4, 4, 4);   // 32³ 体素网格（8³/组）
            _bakePending--;
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
