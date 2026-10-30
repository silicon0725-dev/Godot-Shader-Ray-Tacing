using System;
using Godot;

namespace VoxelPoolGodot;

/// <summary>体素材质 ID（C# 与 GLSL 保持一致；10..16 为彩色玻璃介质）</summary>
public static class MatId
{
    public const byte Empty = 0;
    public const byte WhitePlaster = 1;
    public const byte Red = 2;
    public const byte Green = 3;
    public const byte PoolTile = 4;
    public const byte Metal = 5;
    public const byte Water = 6;
    public const byte Stone = 7;
    public const byte Wood = 8;
    public const byte EmissiveBlock = 9;
    // 彩色玻璃体素（材质表 albedo.rgb = Beer-Lambert 吸收系数 σ）
    public const byte GlassRed = 10;
    public const byte GlassGreen = 11;
    public const byte GlassBlue = 12;
    public const byte GlassCyan = 13;
    public const byte GlassMagenta = 14;
    public const byte GlassYellow = 15;
    public const byte GlassWhite = 16;
    public const byte GlassMin = 10;
    public const byte GlassMax = 16;
    // 后室材质
    public const byte Wallpaper = 17;
    public const byte Carpet = 18;
    public const byte CeilingTile = 19;
    // 彩色发光块（白光块是 9）
    public const byte RedLight = 20;
    public const byte GreenLight = 21;
    public const byte BlueLight = 22;
    public const byte CyanLight = 23;
    public const byte MagentaLight = 24;
    public const byte YellowLight = 25;
}

/// <summary>材质/球体类型常量（与 GLSL 一致）</summary>
public static class MatType
{
    public const float Diffuse = 0f;
    public const float Metal = 1f;
    public const float Emissive = 2f;
    public const float Water = 3f;
    public const float GlassVoxel = 4f;   // 彩色玻璃体素介质
}

public struct SphereDef
{
    public Vector3 Pos;
    public float Radius;
    public Color Albedo;      // 漫反射颜色 / 玻璃=吸收系数σ
    public float Type;        // 0 漫反射 1 金属 2 玻璃
    public Color Emission;    // 自发光（×强度）
}

public class SceneDef
{
    public string Name = "";
    public Vector3 SpawnPos;
    public float SpawnYaw, SpawnPitch;
    public Vector3 SunDir = Vector3.Up;    // 指向太阳
    public float SunIntensity = 0f;
    public Color SunColor = Colors.White;
    public float SkyIntensity = 1f;
    public float Exposure = 1f;
    public bool SunOn = true;
    // 水体信息（无水场景 WaterY 设为 -1000）
    public float WaterY = -1000f;
    public Vector2 PoolMin = Vector2.Zero;
    public Vector2 PoolSize = Vector2.One;
}

/// <summary>
/// 体素世界：32³ 体素，每体素 1 字节材质 ID。
/// 场景预设（泳池 / 康奈尔盒）在此构建；同时提供 CPU 侧 DDA 用于方块编辑拾取。
/// </summary>
public partial class VoxelWorld : Node
{
    public const int Sx = 32, Sy = 32, Sz = 32;
    public byte[] Voxels = new byte[Sx * Sy * Sz];

    public int WorldSx => Sx;
    public int WorldSy => Sy;
    public int WorldSz => Sz;

    public SceneDef Scene { get; set; }
    public SphereDef[] Spheres { get; set; } = Array.Empty<SphereDef>();

    public byte this[int x, int y, int z]
    {
        get => Voxels[x + y * Sx + z * Sx * Sy];
        set => Voxels[x + y * Sx + z * Sx * Sy] = value;
    }

    public bool InBounds(int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 && x < Sx && y < Sy && z < Sz;

    // ------------------------------------------------------------------
    //  材质表 → GPU 缓冲（每材质 2×vec4：albedo / emission+类型）
    // ------------------------------------------------------------------
    public float[] BuildMaterialBuffer()
    {
        // 20 个材质槽（0..16 被占用）
        var m = new float[28 * 8];   // 0..25 被占用
        void Set(int id, Vector4 alb, Vector4 em)
        {
            m[id * 8 + 0] = alb.X; m[id * 8 + 1] = alb.Y; m[id * 8 + 2] = alb.Z; m[id * 8 + 3] = alb.W;
            m[id * 8 + 4] = em.X; m[id * 8 + 5] = em.Y; m[id * 8 + 6] = em.Z; m[id * 8 + 7] = em.W;
        }
        // albedo.w = 粗糙度（1=纯漫反射；越小越接近镜面；金属=模糊宽度）
        Set(MatId.Empty, new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.WhitePlaster, new Vector4(0.82f, 0.80f, 0.76f, 0.92f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.Red, new Vector4(0.62f, 0.10f, 0.10f, 0.90f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.Green, new Vector4(0.10f, 0.52f, 0.18f, 0.90f), new Vector4(0, 0, 0, MatType.Diffuse));
        // 泳池瓷砖：光面砖（程序化纹理会在 shader 里叠加砖缝）
        Set(MatId.PoolTile, new Vector4(0.62f, 0.72f, 0.74f, 0.18f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.Metal, new Vector4(0.92f, 0.92f, 0.94f, 0.06f), new Vector4(0, 0, 0, MatType.Metal));
        Set(MatId.Water, new Vector4(0.2f, 0.5f, 0.8f, 1), new Vector4(0, 0, 0, MatType.Water));
        Set(MatId.Stone, new Vector4(0.52f, 0.50f, 0.48f, 0.80f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.Wood, new Vector4(0.58f, 0.40f, 0.22f, 0.85f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.EmissiveBlock, new Vector4(0, 0, 0, 1), new Vector4(4.0f, 3.8f, 3.4f, MatType.Emissive));
        // 彩色玻璃：albedo.rgb = 吸收系数 σ（对齐原版 [.06, 2.4, 2.4] 量级：
        // 吸收通道 2.4/m → 1m 透射 9%，颜色饱和；主通道近乎全透）
        Set(MatId.GlassRed,    new Vector4(0.06f, 2.4f, 2.4f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassGreen,  new Vector4(2.4f, 0.08f, 2.4f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassBlue,   new Vector4(2.4f, 2.4f, 0.06f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassCyan,   new Vector4(2.4f, 0.08f, 0.06f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassMagenta,new Vector4(2.4f, 0.06f, 1.8f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassYellow, new Vector4(0.08f, 0.06f, 2.4f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        Set(MatId.GlassWhite,  new Vector4(0.06f, 0.06f, 0.06f, 0), new Vector4(0, 0, 0, MatType.GlassVoxel));
        // 后室：黄壁纸 / 地毯 / 天花板（shader 里有程序化纹理）
        Set(MatId.Wallpaper,   new Vector4(0.76f, 0.66f, 0.32f, 0.90f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.Carpet,      new Vector4(0.52f, 0.44f, 0.20f, 0.97f), new Vector4(0, 0, 0, MatType.Diffuse));
        Set(MatId.CeilingTile, new Vector4(0.80f, 0.80f, 0.76f, 0.85f), new Vector4(0, 0, 0, MatType.Diffuse));
        // 彩色发光块：纯色光 × 4 强度（走 GI 弹射照亮场景 + 坏灯闪烁）
        Set(MatId.RedLight,     new Vector4(0, 0, 0, 1), new Vector4(4.0f, 0.10f, 0.06f, MatType.Emissive));
        Set(MatId.GreenLight,   new Vector4(0, 0, 0, 1), new Vector4(0.10f, 4.0f, 0.20f, MatType.Emissive));
        Set(MatId.BlueLight,    new Vector4(0, 0, 0, 1), new Vector4(0.14f, 0.36f, 4.0f, MatType.Emissive));
        Set(MatId.CyanLight,    new Vector4(0, 0, 0, 1), new Vector4(0.16f, 3.4f, 4.0f, MatType.Emissive));
        Set(MatId.MagentaLight, new Vector4(0, 0, 0, 1), new Vector4(4.0f, 0.20f, 3.0f, MatType.Emissive));
        Set(MatId.YellowLight,  new Vector4(0, 0, 0, 1), new Vector4(4.0f, 3.7f, 0.72f, MatType.Emissive));
        return m;
    }

    /// <summary>体素打包成 u32 缓冲（4 体素/字，与 GLSL 端解码一致）</summary>
    public byte[] PackVoxels()
    {
        int words = (Voxels.Length + 3) / 4;
        var wordsArr = new uint[words];
        for (int i = 0; i < Voxels.Length; i++)
            wordsArr[i >> 2] |= (uint)Voxels[i] << ((i & 3) * 8);
        var bytes = new byte[words * 4];
        Buffer.BlockCopy(wordsArr, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public float[] PackSpheres()
    {
        var f = new float[Spheres.Length * 12];
        for (int i = 0; i < Spheres.Length; i++)
        {
            var s = Spheres[i];
            int o = i * 12;
            f[o + 0] = s.Pos.X; f[o + 1] = s.Pos.Y; f[o + 2] = s.Pos.Z; f[o + 3] = s.Radius;
            f[o + 4] = s.Albedo.R; f[o + 5] = s.Albedo.G; f[o + 6] = s.Albedo.B; f[o + 7] = s.Type;
            f[o + 8] = s.Emission.R; f[o + 9] = s.Emission.G; f[o + 10] = s.Emission.B; f[o + 11] = 0;
        }
        return f;
    }

    // ------------------------------------------------------------------
    //  CPU 侧 Amanatides-Woo DDA（编辑拾取用）
    // ------------------------------------------------------------------
    public bool Raycast(Vector3 ro, Vector3 rd, float maxDist,
                        out Vector3I hitCell, out Vector3I hitNormal)
    {
        hitCell = default; hitNormal = default;
        // 世界 AABB 入口
        Vector3 bmin = Vector3.Zero, bmax = new(Sx, Sy, Sz);
        var safe = new Vector3(
            Math.Abs(rd.X) < 1e-8f ? 1e-8f : rd.X,
            Math.Abs(rd.Y) < 1e-8f ? 1e-8f : rd.Y,
            Math.Abs(rd.Z) < 1e-8f ? 1e-8f : rd.Z);
        var inv = new Vector3(1f / safe.X, 1f / safe.Y, 1f / safe.Z);
        Vector3 tA = (bmin - ro) * inv, tB = (bmax - ro) * inv;
        Vector3 tmn = tA.Min(tB), tmx = tA.Max(tB);
        float tEnter = Mathf.Max(Mathf.Max(tmn.X, tmn.Y), tmn.Z);
        float tExit = Mathf.Min(Mathf.Min(tmx.X, tmx.Y), tmx.Z);
        if (tEnter > tExit || tExit < 0 || tEnter > maxDist) return false;

        Vector3 pos = ro + rd * Mathf.Max(tEnter + 1e-4f, 0f);
        var cell = new Vector3I(
            Mathf.Clamp((int)MathF.Floor(pos.X), 0, Sx - 1),
            Mathf.Clamp((int)MathF.Floor(pos.Y), 0, Sy - 1),
            Mathf.Clamp((int)MathF.Floor(pos.Z), 0, Sz - 1));

        var step = new Vector3I(Mathf.Sign(rd.X), Mathf.Sign(rd.Y), Mathf.Sign(rd.Z));
        var delta = new Vector3(MathF.Abs(1f / safe.X), MathF.Abs(1f / safe.Y), MathF.Abs(1f / safe.Z));
        var next = new Vector3(
            cell.X + Math.Max(step.X, 0), cell.Y + Math.Max(step.Y, 0), cell.Z + Math.Max(step.Z, 0));
        var tMax = new Vector3(
            Mathf.Max((next.X - pos.X) / safe.X, 0),
            Mathf.Max((next.Y - pos.Y) / safe.Y, 0),
            Mathf.Max((next.Z - pos.Z) / safe.Z, 0));

        for (int i = 0; i < 256; i++)
        {
            float t;
            int axis;
            if (tMax.X < tMax.Y && tMax.X < tMax.Z) { t = tMax.X; axis = 0; tMax.X += delta.X; cell.X += step.X; }
            else if (tMax.Y < tMax.Z)               { t = tMax.Y; axis = 1; tMax.Y += delta.Y; cell.Y += step.Y; }
            else                                     { t = tMax.Z; axis = 2; tMax.Z += delta.Z; cell.Z += step.Z; }

            if (t > maxDist) return false;
            if (!InBounds(cell.X, cell.Y, cell.Z)) return false;
            byte m = Voxels[cell.X + cell.Y * Sx + cell.Z * Sx * Sy];
            if (m != MatId.Empty && m != MatId.Water)
            {
                hitCell = cell;
                hitNormal = axis switch
                {
                    0 => new Vector3I(-step.X, 0, 0),
                    1 => new Vector3I(0, -step.Y, 0),
                    _ => new Vector3I(0, 0, -step.Z),
                };
                return true;
            }
        }
        return false;
    }

    // ==================================================================
    //  场景预设
    // ==================================================================
    public void BuildPool()
    {
        Array.Clear(Voxels, 0, Voxels.Length);

        // 台面基座：y=0..4 实心（顶面 y=5）
        for (int x = 0; x < Sx; x++)
            for (int z = 0; z < Sz; z++)
                for (int y = 0; y <= 4; y++)
                    this[x, y, z] = MatId.Stone;

        // 泳池内池 x∈[8,24) z∈[8,24)：挖空 y=1..4，池底铺瓷砖
        for (int x = 8; x < 24; x++)
            for (int z = 8; z < 24; z++)
            {
                this[x, 0, z] = MatId.PoolTile;   // 池底
                for (int y = 1; y <= 4; y++) this[x, y, z] = MatId.Empty;
            }

        // 注水 y=1..3（水面 y=4）
        for (int x = 8; x < 24; x++)
            for (int z = 8; z < 24; z++)
                for (int y = 1; y <= 3; y++)
                    this[x, y, z] = MatId.Water;

        // 池壁贴瓷砖（内池四周一圈）
        for (int x = 7; x <= 24; x++)
            for (int z = 7; z <= 24; z++)
                for (int y = 1; y <= 4; y++)
                {
                    bool edgeX = x == 7 || x == 24, edgeZ = z == 7 || z == 24;
                    bool interiorX = x > 7 && x < 24, interiorZ = z > 7 && z < 24;
                    if ((edgeX && interiorZ) || (edgeZ && interiorX))
                        this[x, y, z] = MatId.PoolTile;
                }

        // 彩虹玻璃砖墙（展示彩色玻璃体素）：x=5，z=13..18 每列一色，3 层高
        byte[] glassCols = {
            MatId.GlassRed, MatId.GlassGreen, MatId.GlassBlue,
            MatId.GlassCyan, MatId.GlassMagenta, MatId.GlassYellow,
        };
        for (int z = 13; z <= 18; z++)
            for (int y = 5; y <= 7; y++)
                this[5, y, z] = glassCols[z - 13];

        // 白玻璃立方体（2×2×2）叠在台面一角
        for (int x = 24; x <= 25; x++)
            for (int z = 25; z <= 26; z++)
                for (int y = 5; y <= 6; y++)
                    this[x, y, z] = MatId.GlassWhite;

        // 彩色发光块霓虹灯带：玻璃墙前一排六色（夜里最出彩）
        byte[] neonCols = {
            MatId.RedLight, MatId.GreenLight, MatId.BlueLight,
            MatId.CyanLight, MatId.MagentaLight, MatId.YellowLight,
        };
        for (int z = 13; z <= 18; z++)
            this[7, 5, z] = neonCols[z - 13];

        // 场景参数
        Scene = new SceneDef
        {
            Name = "泳池 Pool",
            SpawnPos = new Vector3(16f, 11.5f, 30.5f),
            SpawnYaw = 0f,               // 面向 -Z（池中心）
            SpawnPitch = -0.32f,
            SunDir = new Vector3(0.32f, 0.88f, 0.20f).Normalized(),
            SunIntensity = 2.8f,
            SunColor = new Color(1.0f, 0.96f, 0.88f),
            SkyIntensity = 0.85f,
            Exposure = 1.15f,
            SunOn = true,
            WaterY = 4.0f,
            PoolMin = new Vector2(8f, 8f),
            PoolSize = new Vector2(16f, 16f),
        };

        // 玻璃球：7 色 Beer-Lambert 吸收系数（σ 越大吸收越强）
        var glassDefs = new (Vector3 pos, Color sigma)[]
        {
            (new Vector3(13.0f, 5.85f, 6.5f),  new Color(0.05f, 0.9f, 0.9f)),  // 红
            (new Vector3(16.0f, 5.85f, 5.5f),  new Color(0.9f, 0.06f, 0.9f)),  // 绿
            (new Vector3(19.0f, 5.85f, 6.5f),  new Color(0.9f, 0.9f, 0.05f)),  // 蓝
            (new Vector3(11.0f, 5.85f, 10.0f), new Color(0.9f, 0.06f, 0.05f)), // 青
            (new Vector3(21.0f, 5.85f, 10.0f), new Color(0.9f, 0.05f, 0.7f)),  // 洋红
            (new Vector3(11.0f, 5.85f, 22.0f), new Color(0.06f, 0.05f, 0.9f)), // 黄
            (new Vector3(21.0f, 5.85f, 22.0f), new Color(0.05f, 0.05f, 0.05f)),// 白玻璃
        };
        var spheres = new System.Collections.Generic.List<SphereDef>();
        foreach (var (pos, sigma) in glassDefs)
            spheres.Add(new SphereDef
            {
                Pos = pos, Radius = 0.85f,
                Albedo = sigma, Type = 2f,   // 玻璃
                Emission = Colors.Black,
            });

        // 白球（漫反射）与镜面球
        spheres.Add(new SphereDef { Pos = new Vector3(16f, 5.85f, 25.5f), Radius = 0.85f,
            Albedo = new Color(0.85f, 0.83f, 0.78f), Type = 0f });
        spheres.Add(new SphereDef { Pos = new Vector3(16f, 5.85f, 6.5f), Radius = 0.85f,
            Albedo = new Color(0.92f, 0.92f, 0.95f), Type = 1f });

        // 彩色光球（NEE 区域光，贴近台面增强染色）
        spheres.Add(new SphereDef { Pos = new Vector3(10.5f, 7.2f, 16f), Radius = 0.55f,
            Albedo = Colors.Black, Type = 0f, Emission = new Color(0.8f, 7.5f, 9.3f) });
        spheres.Add(new SphereDef { Pos = new Vector3(21.5f, 7.8f, 16f), Radius = 0.55f,
            Albedo = Colors.Black, Type = 0f, Emission = new Color(9.3f, 0.9f, 6.5f) });
        spheres.Add(new SphereDef { Pos = new Vector3(16f, 8.5f, 9.5f), Radius = 0.55f,
            Albedo = Colors.Black, Type = 0f, Emission = new Color(9.9f, 8.8f, 7.2f) });

        Spheres = spheres.ToArray();
    }

    public void BuildCornell()
    {
        Array.Clear(Voxels, 0, Voxels.Length);

        // 房间 x∈[7,25) z∈[7,25)，内空 15×15×15
        for (int x = 7; x < 25; x++)
            for (int z = 7; z < 25; z++)
                for (int y = 0; y < 18; y++)
                {
                    bool wall = x == 7 || x == 24 || z == 7 || z == 24 || y == 0 || y == 17;
                    if (wall) this[x, y, z] = MatId.WhitePlaster;
                }
        // 左墙绿 / 右墙红
        for (int z = 7; z < 25; z++)
            for (int y = 0; y < 18; y++)
            {
                this[7, y, z] = MatId.Green;
                this[24, y, z] = MatId.Red;
            }

        Scene = new SceneDef
        {
            Name = "康奈尔盒 Cornell",
            SpawnPos = new Vector3(16f, 9.5f, 30.0f),
            SpawnYaw = 0f,
            SpawnPitch = -0.05f,
            SunDir = Vector3.Up,
            SunIntensity = 0f,
            SunColor = Colors.White,
            SkyIntensity = 0.03f,
            Exposure = 1.1f,
            SunOn = false,
            WaterY = -1000f,
        };

        var spheres = new System.Collections.Generic.List<SphereDef>
        {
            // 顶部面积光球
            new() { Pos = new Vector3(16f, 14.5f, 16f), Radius = 1.3f,
                    Albedo = Colors.Black, Type = 0f, Emission = new Color(11f, 10.2f, 9.0f) },
            // 玻璃球 + 镜面球
            new() { Pos = new Vector3(12.5f, 2.4f, 13.5f), Radius = 1.6f,
                    Albedo = new Color(0.10f, 0.10f, 0.10f), Type = 2f },
            new() { Pos = new Vector3(19.5f, 2.4f, 18.5f), Radius = 1.6f,
                    Albedo = new Color(0.93f, 0.93f, 0.95f), Type = 1f },
        };
        Spheres = spheres.ToArray();
    }

    /// <summary>
    /// 后室：黄壁纸办公迷宫——地毯地面、吊顶天花、网格状荧光灯板（部分坏灯闪烁）。
    /// 墙网 x/z mod 9 + 哈希门洞，形成 ~7 格的房间与走廊。
    /// </summary>
    public void BuildBackrooms()
    {
        Array.Clear(Voxels, 0, Voxels.Length);
        const int lo = 2, hi = 30, ceil = 8;

        for (int x = lo; x < hi; x++)
            for (int z = lo; z < hi; z++)
            {
                this[x, 0, z] = MatId.Carpet;                 // 地毯
                this[x, ceil, z] = ((x - lo) % 4 == 1 && (z - lo) % 4 == 1)
                                  ? MatId.EmissiveBlock       // 灯板网格（每 4 格一盏）
                                  : MatId.CeilingTile;
            }

        // 外墙
        for (int x = lo; x < hi; x++)
            for (int z = lo; z < hi; z++)
                for (int y = 1; y < ceil; y++)
                {
                    if (x == lo || x == hi - 1 || z == lo || z == hi - 1)
                        this[x, y, z] = MatId.Wallpaper;
                }

        // 内部隔墙网格：x/z 每 9 格一道，哈希开门洞
        var rng = new System.Random(20261030);
        for (int x = lo + 1; x < hi - 1; x++)
            for (int z = lo + 1; z < hi - 1; z++)
            {
                bool onWall = (x - lo) % 9 == 4 || (z - lo) % 9 == 4;
                if (!onWall) continue;
                // 门洞：每段墙开 2-3 个 2 宽的口
                double h = rng.NextDouble();
                if (h < 0.22) continue;
                for (int y = 1; y < ceil; y++)
                    this[x, y, z] = MatId.Wallpaper;
            }

        Scene = new SceneDef
        {
            Name = "后室 Backrooms",
            SpawnPos = new Vector3(5.5f, 2.4f, 5.5f),
            SpawnYaw = 0.8f,
            SpawnPitch = 0.02f,
            SunDir = Vector3.Up,
            SunIntensity = 0f,
            SunColor = new Color(1f, 0.96f, 0.85f),
            SkyIntensity = 0.02f,
            Exposure = 1.0f,
            SunOn = false,
            WaterY = -1000f,
        };

        // 灯光全部由天花灯板（发光体素）承担——shader 端做解析 NEE 面光源
        Spheres = System.Array.Empty<SphereDef>();
    }
}
