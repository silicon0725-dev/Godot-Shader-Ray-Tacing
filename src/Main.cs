using System;
using Godot;

namespace VoxelPoolGodot;

/// <summary>
/// 主控：组装渲染管线、每帧提交 compute、处理输入与体素编辑。
/// </summary>
public partial class Main : Node
{
    private FlyCamera _cam;
    private VoxelWorld _world;
    private PathTracer _pt = new();
    private TextureRect _texRect;
    private Label _statsLabel;
    private Label _helpLabel;

    private uint _sampleIdx = 0;          // 累积样本数（0 = 下一帧覆盖）
    private uint _randSeed = 1;
    private int _preset = 0;
    private int _placeMatIdx = 0;
    private readonly byte[] _placeMats = {
        MatId.WhitePlaster, MatId.Red, MatId.Green, MatId.PoolTile,
        MatId.Metal, MatId.Stone, MatId.Wood, MatId.EmissiveBlock, MatId.Water,
        MatId.GlassRed, MatId.GlassGreen, MatId.GlassBlue,
        MatId.GlassCyan, MatId.GlassMagenta, MatId.GlassYellow, MatId.GlassWhite,
        MatId.RedLight, MatId.GreenLight, MatId.BlueLight,
        MatId.CyanLight, MatId.MagentaLight, MatId.YellowLight,
    };
    private bool _running = false;
    private float _statTimer = 0f;
    private int _fpsCounter = 0;
    private int _fpsShown = 0;
    private float _waterTime = 0f;      // 水面流动时间（0 = 静止）
    private bool _waterAnim = false;    // M 键切换
    private float _lightTime = 0f;      // 灯光时钟（后室坏灯闪烁，始终运行）
    private bool _hasSelection = false;
    private Vector3I _selCell;
    private Vector3I _selNormal;
    private bool _autoExposure = true;  // E 键切换（人眼适应）
    private float _sunElev = 1.0f;      // 太阳高度角（弧度）
    private float _sunAzim = 0.0f;      // 太阳方位角（弧度）
    private float _renderScale = 1.0f;  // PageUp/Down 渲染分辨率比例
    private bool _giCache = true;       // K 键切换：GI 缓存(平滑) / 经典多弹射(对照)
    private bool _wasMoving = false;    // 上帧是否移动（时间重投影切换）
    private float _orbitSpeed = 0f;     // VOXELORBIT 测试用
    private float _elapsed = 0f;
    private Vector4 _prevPos, _prevRight, _prevUp, _prevFwd;   // 上帧相机（缩放后基）

    /// <summary>记录本帧缩放相机基（供下帧 temporal 重投影）</summary>
    private void SavePrevCamera()
    {
        float tanF = Mathf.Tan(Mathf.DegToRad(_cam.FovYDeg * 0.5f));
        float aspect = (float)_pt.Width / _pt.Height;
        _prevPos = new Vector4(_cam.Position.X, _cam.Position.Y, _cam.Position.Z, 0);
        Vector3 r = _cam.Right.Normalized() * (tanF * aspect);
        Vector3 u = _cam.UpV.Normalized() * tanF;
        Vector3 f = _cam.Forward.Normalized() * tanF;
        _prevRight = new Vector4(r.X, r.Y, r.Z, 0);
        _prevUp = new Vector4(u.X, u.Y, u.Z, 0);
        _prevFwd = new Vector4(f.X, f.Y, f.Z, 0);
    }

    private void ApplyRenderScale()
    {
        _pt.SetScale(_renderScale);
        var size = GetViewport().GetVisibleRect().Size;
        _pt.Resize((int)size.X, (int)size.Y);
        var texRd = new Texture2Drd { TextureRdRid = _pt.PresentTexture };
        _texRect.Texture = texRd;
        _sampleIdx = 0;
        ShowStatus($"渲染比例 {_renderScale:0.00}×（{_pt.Width}x{_pt.Height}）");
    }

    private static readonly string[] _placeMatNames = {
        "白墙", "红", "绿", "泳池瓷砖", "金属", "石材", "木头", "发光块", "水",
        "红玻璃", "绿玻璃", "蓝玻璃", "青玻璃", "洋红玻璃", "黄玻璃", "白玻璃",
        "红光块", "绿光块", "蓝光块", "青光块", "洋红光块", "黄光块",
    };

    public override void _Ready()
    {
        // 测试用：VOXELANIM=1 启动即开水面动画；VOXELORBIT=rad/s 自动环绕
        if (System.Environment.GetEnvironmentVariable("VOXELANIM") == "1") _waterAnim = true;
        if (float.TryParse(System.Environment.GetEnvironmentVariable("VOXELORBIT"), out var orb))
            _orbitSpeed = orb;

        _cam = GetNode<FlyCamera>("Camera3D");
        _world = GetNode<VoxelWorld>("VoxelWorld");
        _texRect = GetNode<TextureRect>("UI/TextureRect");
        _statsLabel = GetNode<Label>("UI/Stats");
        _helpLabel = GetNode<Label>("UI/Help");

        _helpLabel.Text =
            "点击画面捕获鼠标 | ESC 释放\n" +
            "WASD 移动 / Space 上升 / Ctrl 下降 / Shift 加速\n" +
            "左键 删除方块 | 右键 放置方块 | T 切换材质\n" +
            "1 泳池 | 2 康奈尔盒 | 3 后室 | 4 展厅 | R 重置相机 | M 水面动画\n" +
            "F5 保存 | F9 读取 | E 自动曝光 | PageUp/Down 渲染比例\n" +
            "[ ] 太阳高度 | - = 太阳方位（黄昏/夜晚有星空）";

        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null)
        {
            _statsLabel.Text = "错误：需要 Forward+ (Vulkan) 渲染器。\n请在项目设置中选择 forward_plus 后重试。";
            return;
        }

        var size = GetViewport().GetVisibleRect().Size;
        if (!_pt.Init((int)size.X, (int)size.Y))
        {
            _statsLabel.Text = "错误：管线初始化失败（着色器编译失败？）";
            return;
        }

        var texRd = new Texture2Drd { TextureRdRid = _pt.PresentTexture };
        _texRect.Texture = texRd;

        LoadPreset(0);
        ApplyCameraOverride();

        // 测试用：启动时读取存档（若存在）
        if (System.Environment.GetEnvironmentVariable("VOXELAUTOLOAD") == "1" &&
            FileAccess.FileExists("user://pool.save"))
            LoadGame();
        // 测试用：VOXELPRESET=1 康奈尔盒 / 2 后室；VOXELGI=0 经典多弹射（A/B 对照）
        if (System.Environment.GetEnvironmentVariable("VOXELPRESET") == "1")
            LoadPreset(1);
        else if (System.Environment.GetEnvironmentVariable("VOXELPRESET") == "2")
            LoadPreset(2);
        else if (System.Environment.GetEnvironmentVariable("VOXELPRESET") == "3")
            LoadPreset(3);
        if (System.Environment.GetEnvironmentVariable("VOXELGI") == "0")
            _giCache = false;
        if (float.TryParse(System.Environment.GetEnvironmentVariable("VOXELSUNELEV"),
                           out var elevDeg))
        {
            _sunElev = Mathf.DegToRad(elevDeg);
            UpdateSun();
        }

        // 调试：体素统计 + 玻璃材质表
        if (System.Environment.GetEnvironmentVariable("VOXELDBG") == "1")
        {
            var counts = new int[256];
            foreach (var v in _world.Voxels) counts[v]++;
            for (byte id = MatId.GlassMin; id <= MatId.GlassMax; id++)
                GD.Print($"体素 id={id}: {counts[id]} 个");
            GD.Print($"水体素: {counts[MatId.Water]} 个");
            var mats = _world.BuildMaterialBuffer();
            for (byte id = MatId.GlassMin; id <= MatId.GlassMax; id++)
                GD.Print($"材质 id={id}: σ=({mats[id*8+0]:F2},{mats[id*8+1]:F2},{mats[id*8+2]:F2})");
        }

        _running = true;
    }

    /// <summary>测试用：VOXELCAM="x,y,z,yaw,pitch" 覆盖出生机位</summary>
    private void ApplyCameraOverride()
    {
        var env = System.Environment.GetEnvironmentVariable("VOXELCAM");
        if (string.IsNullOrEmpty(env)) return;
        var parts = env.Split(',');
        if (parts.Length != 5) return;
        if (float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y) &&
            float.TryParse(parts[2], out var z) && float.TryParse(parts[3], out var yaw) &&
            float.TryParse(parts[4], out var pitch))
        {
            _cam.SetSpawn(new Vector3(x, y, z), yaw, pitch);
            GD.Print($"相机覆盖: ({x},{y},{z}) yaw={yaw} pitch={pitch}");
        }
    }

    // ==========================================================================
    //  存档（F5 保存 / F9 读取，user://pool.save）
    // ==========================================================================
    private const uint SaveMagic = 0x56785001u; // "VXP1"

    private void SaveGame()
    {
        var s = _world.Scene;
        var b = new System.Collections.Generic.List<byte>(40960);
        void F(float v) => b.AddRange(BitConverter.GetBytes(v));
        void U(uint v) => b.AddRange(BitConverter.GetBytes(v));

        U(SaveMagic);
        U((uint)_world.Voxels.Length);
        F(s.SunDir.X); F(s.SunDir.Y); F(s.SunDir.Z); F(s.SunIntensity);
        F(s.SunColor.R); F(s.SunColor.G); F(s.SunColor.B); F(s.Exposure);
        F(s.SkyIntensity); F(s.SunOn ? 1f : 0f);
        F(s.WaterY); F(s.PoolMin.X); F(s.PoolMin.Y); F(s.PoolSize.X); F(s.PoolSize.Y);
        F(_cam.Position.X); F(_cam.Position.Y); F(_cam.Position.Z);
        F(_cam.Yaw); F(_cam.Pitch); F(_waterTime); F(_waterAnim ? 1f : 0f);
        U((uint)_world.Spheres.Length);
        foreach (var sp in _world.Spheres)
        {
            F(sp.Pos.X); F(sp.Pos.Y); F(sp.Pos.Z); F(sp.Radius);
            F(sp.Albedo.R); F(sp.Albedo.G); F(sp.Albedo.B); F(sp.Type);
            F(sp.Emission.R); F(sp.Emission.G); F(sp.Emission.B); F(0f);
        }
        b.AddRange(_world.Voxels);

        using var f = FileAccess.Open("user://pool.save", FileAccess.ModeFlags.Write);
        if (f == null) { ShowStatus("保存失败：无法写入 user://"); return; }
        f.StoreBuffer(b.ToArray());
        ShowStatus($"已保存（{b.Count / 1024} KB，{_world.Spheres.Length} 球，样本 {_sampleIdx}）");
    }

    private void LoadGame()
    {
        if (!FileAccess.FileExists("user://pool.save")) { ShowStatus("没有存档"); return; }
        using var f = FileAccess.Open("user://pool.save", FileAccess.ModeFlags.Read);
        var data = f.GetBuffer((int)f.GetLength());
        int o = 0;
        float Rf() { float v = BitConverter.ToSingle(data, o); o += 4; return v; }
        uint Ru() { uint v = BitConverter.ToUInt32(data, o); o += 4; return v; }

        if (Ru() != SaveMagic) { ShowStatus("存档版本不符"); return; }
        uint voxelCount = Ru();
        var s = _world.Scene;
        s.SunDir = new Vector3(Rf(), Rf(), Rf()).Normalized();
        s.SunIntensity = Rf();
        s.SunColor = new Color(Rf(), Rf(), Rf());
        s.Exposure = Rf();
        s.SkyIntensity = Rf();
        s.SunOn = Rf() > 0.5f;
        s.WaterY = Rf();
        s.PoolMin = new Vector2(Rf(), Rf());
        s.PoolSize = new Vector2(Rf(), Rf());
        var camPos = new Vector3(Rf(), Rf(), Rf());
        float yaw = Rf(), pitch = Rf();
        _waterTime = Rf();
        _waterAnim = Rf() > 0.5f;

        int nSp = (int)Ru();
        var spheres = new SphereDef[nSp];
        for (int i = 0; i < nSp; i++)
        {
            spheres[i] = new SphereDef
            {
                Pos = new Vector3(Rf(), Rf(), Rf()),
                Radius = Rf(),
                Albedo = new Color(Rf(), Rf(), Rf()),
                Type = Rf(),
                Emission = new Color(Rf(), Rf(), Rf()),
            };
            Rf(); // 对齐填充
        }
        _world.Spheres = spheres;
        if (voxelCount == (uint)_world.Voxels.Length)
        {
            System.Array.Copy(data, o, _world.Voxels, 0, voxelCount);
        }
        else { ShowStatus("存档体素数不符，已忽略体素"); }

        _pt.SetScene(_world.PackVoxels(), _world.PackSpheres(), _world.BuildMaterialBuffer());
        _cam.SetSpawn(camPos, yaw, pitch);
        _sampleIdx = 0;
        ShowStatus($"已读取（{nSp} 球）");
    }

    private string _statusMsg = "";
    private float _statusTimer = 0f;
    private void ShowStatus(string msg)
    {
        _statusMsg = msg;
        _statusTimer = 2.5f;
        GD.Print(msg);
    }

    private void LoadPreset(int index)
    {
        _preset = index;
        if (index == 0) _world.BuildPool();
        else if (index == 1) _world.BuildCornell();
        else if (index == 2) _world.BuildBackrooms();
        else if (index == 3)
        {
            _world.BuildShowroomMesh();
            // 加载完整 OBJ 网格车（本地路径，不入 Git）
            string objPath = @"F:\光线追踪\car_model\bugatti\bugatti.obj";
            if (System.IO.File.Exists(objPath))
                _pt.LoadMesh(objPath, 1.8f, new Vector3(16f, 1.0f, 16f));
        }
        else _world.BuildShowroom();

        _pt.SetScene(_world.PackVoxels(), _world.PackSpheres(), _world.BuildMaterialBuffer());
        _pt.ResetExposure(_world.Scene.Exposure);
        _cam.SetSpawn(_world.Scene.SpawnPos, _world.Scene.SpawnYaw, _world.Scene.SpawnPitch);
        // 从场景太阳向量反解高度/方位角（供 [ ] - = 调整）
        var sd = _world.Scene.SunDir.Normalized();
        _sunElev = Mathf.Asin(Mathf.Clamp(sd.Y, -1f, 1f));
        _sunAzim = Mathf.Atan2(sd.X, sd.Z);
        _sampleIdx = 0;
    }

    /// <summary>按高度/方位角更新太阳方向（重置累积与焦散）</summary>
    private void UpdateSun()
    {
        var s = _world.Scene;
        float ce = Mathf.Cos(_sunElev);
        s.SunDir = new Vector3(ce * Mathf.Sin(_sunAzim), Mathf.Sin(_sunElev),
                               ce * Mathf.Cos(_sunAzim)).Normalized();
        _sampleIdx = 0;
        _pt.ClearCaustics();
    }

    public override async void _Process(double delta)
    {
        if (!_running) return;

        // 窗口尺寸变化 → 重建纹理
        var size = GetViewport().GetVisibleRect().Size;
        if ((int)size.X != _pt.Width || (int)size.Y != _pt.Height)
        {
            _pt.Resize((int)size.X, (int)size.Y);
            var texRd = new Texture2Drd { TextureRdRid = _pt.PresentTexture };
            _texRect.Texture = texRd;
            _sampleIdx = 0;
        }

        // 测试用：VOXELORBIT=rad/s 自动环绕（强制移动模式以验证时间重投影）
        if (_orbitSpeed != 0f)
            _cam.NudgeYaw(_orbitSpeed * (float)delta);
        _elapsed += (float)delta;

        bool moving = _cam.MovedThisFrame;
        if (moving) _sampleIdx = 0;
        else if (_wasMoving) _sampleIdx = 1;   // 移动→静止：时间历史作为第 1 个样本无缝续接
        _wasMoving = moving;

        // 水面流动动画 + 灯光时钟（VOXELNOFLICKER=1 冻结灯光时钟用于 A/B 测试）
        if (_waterAnim) _waterTime += (float)delta;
        if (System.Environment.GetEnvironmentVariable("VOXELNOFLICKER") != "1")
            _lightTime += (float)delta;

        // 准星目标体素（供描边显示与编辑用）
        _hasSelection = _world.Raycast(_cam.Position, _cam.Forward.Normalized(), 100f,
                                       out _selCell, out _selNormal);

        _randSeed = (uint)(Time.GetTicksMsec() & 0x7FFFFFFF) | 1u;

        var p = _world.Scene;
        var bytes = BuildParams(p);
        // 移动帧多采样：4 spp 抗噪（GI 缓存提速后的性能预算；静止 1 spp）
        int spp = moving ? 4 : 1;
        // 测试/调参：VOXELSPP=n 强制每帧采样数
        if (int.TryParse(System.Environment.GetEnvironmentVariable("VOXELSPP"), out var envSpp)
            && envSpp >= 1 && envSpp <= 8)
            spp = envSpp;
        _pt.Render(bytes, p.SunOn, spp, moving);
        if (!moving) _sampleIdx += (uint)spp;

        // 记录本帧相机供下帧时间重投影
        SavePrevCamera();

        // 自动截图（VOXELSHOT=path；VOXELSHOTN=样本数 或 VOXELSHOTSEC=秒数触发）
        string shotPath = System.Environment.GetEnvironmentVariable("VOXELSHOT");
        float shotSec = 0f;
        float.TryParse(System.Environment.GetEnvironmentVariable("VOXELSHOTSEC"), out shotSec);
        bool shotDue = shotSec > 0f
            ? _elapsed >= shotSec
            : _sampleIdx >= ShotAtSamples();
        if (shotPath != null && shotDue && !_shotTaken)
        {
            _shotTaken = true;
            await ToSignal(GetTree(), "process_frame");
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(shotPath);
            GD.Print($"已保存截图: {shotPath} (samples={_sampleIdx})");
            if (System.Environment.GetEnvironmentVariable("VOXELQUIT") == "1")
                GetTree().Quit();
            // 测试用：截图后自动保存
            if (System.Environment.GetEnvironmentVariable("VOXELAUTOSAVE") == "1")
                SaveGame();
        }

        // 统计信息
        _statTimer += (float)delta;
        _fpsCounter++;
        if (_statTimer >= 0.25f)
        {
            _fpsShown = (int)(_fpsCounter / _statTimer);
            _fpsCounter = 0;
            _statTimer = 0f;
            string status = _statusTimer > 0f ? $"\n{_statusMsg}" : "";
            string expo = _autoExposure ? "自动曝光" : "手动曝光";
            _statsLabel.Text =
                $"{p.Name}  |  {_fpsShown} fps  |  {expo}  |  {_renderScale:0.00}x\n" +
                $"累积样本: {_sampleIdx}  |  渲染: {_pt.Width}x{_pt.Height}\n" +
                $"放置材质: {_placeMatNames[_placeMatIdx]}{status}";
        }
        if (_statusTimer > 0f) _statusTimer -= (float)delta;
    }

    private uint _shotAtSamples = 240;
    private bool _shotTaken = false;   // 防止多 spp 跳过触发点后重复截图

    private uint ShotAtSamples()
    {
        var env = System.Environment.GetEnvironmentVariable("VOXELSHOTN");
        if (uint.TryParse(env, out var n) && n > 0) return n;
        return _shotAtSamples;
    }

    /// <summary>按 GLSL Params 布局打包 12 个 vec4</summary>
    private byte[] BuildParams(SceneDef p)
    {
        var b = new System.Collections.Generic.List<byte>(192);
        void V4(float x, float y, float z, float w)
        {
            b.AddRange(BitConverter.GetBytes(x));
            b.AddRange(BitConverter.GetBytes(y));
            b.AddRange(BitConverter.GetBytes(z));
            b.AddRange(BitConverter.GetBytes(w));
        }

        float aspect = (float)_pt.Width / _pt.Height;
        float tanF = Mathf.Tan(Mathf.DegToRad(_cam.FovYDeg * 0.5f));
        Vector3 fwd = _cam.Forward.Normalized();
        Vector3 right = _cam.Right.Normalized();
        Vector3 up = _cam.UpV.Normalized();

        V4(_cam.Position.X, _cam.Position.Y, _cam.Position.Z, 0);                          // uCamPos
        V4(right.X * tanF * aspect, right.Y * tanF * aspect, right.Z * tanF * aspect, 0);  // uCamRight
        V4(up.X * tanF, up.Y * tanF, up.Z * tanF, 0);                                      // uCamUp
        V4(fwd.X * tanF, fwd.Y * tanF, fwd.Z * tanF, 0);                                   // uCamFwd
        V4(_pt.Width, _pt.Height, 1f / _pt.Width, 1f / _pt.Height);                        // uRes
        V4(_sampleIdx, _randSeed, 4f, p.SunOn ? 1f : 0f);                                  // uFrame
        V4(p.SunDir.X, p.SunDir.Y, p.SunDir.Z, p.SunIntensity);                            // uSun
        V4(p.SunColor.R, p.SunColor.G, p.SunColor.B, p.Exposure);                          // uSunColor
        V4(p.SkyIntensity, p.WaterY, p.PoolMin.X, p.PoolMin.Y);                            // uSkyWater
        V4(p.PoolSize.X, p.PoolSize.Y, _world.WorldSx, _world.WorldSy);                    // uPoolWorld
        // 焦散解码比例 = 定点还原 / 已累积光子帧数（含本帧）；z = 水面流动时间
        uint photonFrames = _pt.CausticsFrames + 1;
        V4(_world.WorldSz, (1f / 4096f) / photonFrames, _waterTime, _world.Spheres.Length); // uMisc
        V4(_hasSelection ? _selCell.X : 0, _hasSelection ? _selCell.Y : 0,
           _hasSelection ? _selCell.Z : 0, _hasSelection ? 1f : 0f);                        // uSelect
        V4(_autoExposure ? 1f : 0f, 0.18f, _lightTime, _giCache ? 1f : 0f);                   // uExposure
        V4(0, 0, 0, 0);                                                                       // uTemporal（Render 补丁）
        // 上帧相机（首帧无历史）
        V4(_prevPos.X, _prevPos.Y, _prevPos.Z, 0);                                            // uPrevPos
        V4(_prevRight.X, _prevRight.Y, _prevRight.Z, 0);                                      // uPrevRight
        V4(_prevUp.X, _prevUp.Y, _prevUp.Z, 0);                                               // uPrevUp
        V4(_prevFwd.X, _prevFwd.Y, _prevFwd.Z, 0);                                            // uPrevFwd
        V4(_pt.EmissiveCount, 0, 0, 0);                                                       // uBake: 发光体素数
        return b.ToArray();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_running) return;

        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (Input.MouseMode != Input.MouseModeEnum.Captured &&
                mb.ButtonIndex == MouseButton.Left)
            {
                Input.MouseMode = Input.MouseModeEnum.Captured;
                return;
            }
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var fwd = _cam.Forward.Normalized();
                if (mb.ButtonIndex == MouseButton.Left)
                    EditVoxel(remove: true);
                else if (mb.ButtonIndex == MouseButton.Right)
                    EditVoxel(remove: false);
            }
        }

        if (@event is InputEventKey { Pressed: true } key && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Escape:
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                    break;
                case Key.T:
                    _placeMatIdx = (_placeMatIdx + 1) % _placeMats.Length;
                    break;
                case Key.Key1: LoadPreset(0); break;
                case Key.Key2: LoadPreset(1); break;
                case Key.Key3: LoadPreset(2); break;
                case Key.Key4: LoadPreset(3); break;
                case Key.R:
                    _cam.SetSpawn(_world.Scene.SpawnPos, _world.Scene.SpawnYaw, _world.Scene.SpawnPitch);
                    _sampleIdx = 0;
                    break;
                case Key.M:
                    _waterAnim = !_waterAnim;
                    if (!_waterAnim) _waterTime = 0f;
                    _sampleIdx = 0;
                    break;
                case Key.F5: SaveGame(); break;
                case Key.F9: LoadGame(); break;
                case Key.K:
                    _giCache = !_giCache;
                    _sampleIdx = 0;
                    ShowStatus(_giCache ? "GI：缓存模式（平滑）" : "GI：多弹射模式（对照）");
                    break;
                case Key.Pageup:
                    _renderScale = _renderScale switch { 0.5f => 0.75f, _ => 1.0f };
                    ApplyRenderScale();
                    break;
                case Key.Pagedown:
                    _renderScale = _renderScale switch { 1.0f => 0.75f, _ => 0.5f };
                    ApplyRenderScale();
                    break;
                case Key.E:
                    _autoExposure = !_autoExposure;
                    ShowStatus(_autoExposure ? "自动曝光：开（人眼适应）" : "自动曝光：关（手动）");
                    break;
                case Key.Bracketleft:
                    _sunElev = Mathf.Max(_sunElev - 0.08f, -0.30f);
                    UpdateSun();
                    ShowStatus($"太阳高度 {Mathf.RadToDeg(_sunElev):F0}°（黄昏→夜晚试试继续降）");
                    break;
                case Key.Bracketright:
                    _sunElev = Mathf.Min(_sunElev + 0.08f, 1.35f);
                    UpdateSun();
                    ShowStatus($"太阳高度 {Mathf.RadToDeg(_sunElev):F0}°");
                    break;
                case Key.Minus:
                    _sunAzim -= 0.12f;
                    UpdateSun();
                    ShowStatus($"太阳方位 {Mathf.RadToDeg(_sunAzim):F0}°");
                    break;
                case Key.Equal:
                    _sunAzim += 0.12f;
                    UpdateSun();
                    ShowStatus($"太阳方位 {Mathf.RadToDeg(_sunAzim):F0}°");
                    break;
            }
        }
    }

    /// <summary>准星处删除/放置体素（CPU DDA 拾取）</summary>
    private void EditVoxel(bool remove)
    {
        var ro = _cam.Position;
        var rd = _cam.Forward.Normalized();
        if (!_world.Raycast(ro, rd, 100f, out var cell, out var normal)) return;

        if (remove)
        {
            if (_world[cell.X, cell.Y, cell.Z] == MatId.Empty) return;
            _world[cell.X, cell.Y, cell.Z] = MatId.Empty;
        }
        else
        {
            var target = cell + normal;
            if (!_world.InBounds(target.X, target.Y, target.Z)) return;
            if (_world[target.X, target.Y, target.Z] != MatId.Empty) return;
            _world[target.X, target.Y, target.Z] = _placeMats[_placeMatIdx];
        }

        _pt.UpdateVoxels(_world.PackVoxels());
        _sampleIdx = 0;
    }
}
