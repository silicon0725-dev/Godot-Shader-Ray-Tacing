using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace VoxelPoolGodot;

/// <summary>
/// OBJ 网格加载 + BVH 构建（中位数分割，GPU 友好扁平布局）。
/// 顶点/三角形/材质索引/BVH 节点全部输出为 float[] / uint[] 供 GPU 上传。
/// </summary>
public static class MeshLoader
{
    // ---- 背景幕/棚灯过滤（与体素化器同一套规则）----
    private static bool IsBackdrop(string name)
    {
        string n = name.ToLower();
        return n.Contains("studio") || n.Contains("back") || n.Contains("drop")
            || n.Contains("sun") || n.Contains("floor") || n.Contains("curtain")
            || n == "none" || n.Contains("alight") || n.Contains("backdrop");
    }

    // ---- OBJ 解析 ----
    public static void LoadObj(string path, float scale, Vector3 offset,
        out float[] verts, out uint[] tris, out uint[] triMats,
        out string[] matNames, out Vector3 meshMin, out Vector3 meshMax,
        float rotYDeg = 0f)
    {
        var positions = new List<float>(744000 * 3);
        var faceList = new List<uint>(760000 * 3);
        var matList = new List<uint>(760000);
        var matNameList = new List<string>();
        var matIdMap = new Dictionary<string, uint>();
        float rc = (float)Math.Cos(rotYDeg * Math.PI / 180.0), rs = (float)Math.Sin(rotYDeg * Math.PI / 180.0);

        uint currentMat = 0;
        string currentMatName = "default";
        bool _skipCurrent = false;
        if (!matIdMap.ContainsKey("default")) { matIdMap["default"] = 0; matNameList.Add("default"); }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("v "))
            {
                var parts = line.Split(' ', 4);
                if (parts.Length >= 4)
                {
                    float x = float.Parse(parts[1]), y = float.Parse(parts[2]), z = float.Parse(parts[3]);
                    // 先绕 Y 轴旋转（在原点），再缩放平移
                    float rx = x * rc + z * rs;
                    float rz = -x * rs + z * rc;
                    positions.Add(rx * scale + offset.X);
                    positions.Add(y * scale + offset.Y);
                    positions.Add(rz * scale + offset.Z);
                }
            }
            else if (line.StartsWith("usemtl"))
            {
                var name = line.Substring(7).Trim();
                if (!matIdMap.TryGetValue(name, out currentMat))
                {
                    currentMat = (uint)matNameList.Count;
                    matIdMap[name] = currentMat;
                    matNameList.Add(name);
                }
                currentMatName = name;
                // 过滤摄影棚/背景幕（它们的三角形会包住整个场景）
                _skipCurrent = IsBackdrop(name);
            }
            else if (line.StartsWith("f ") && !_skipCurrent)
            {
                var parts = line.Split(' ');
                // 提取顶点索引（可能带 /vt/vn）
                var indices = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    var idx = parts[i].Split('/')[0];
                    if (int.TryParse(idx, out var v)) indices.Add(v > 0 ? v - 1 : positions.Count / 3 + v);
                }
                // 三角扇
                for (int i = 1; i + 1 < indices.Count; i++)
                {
                    faceList.Add((uint)indices[0]);
                    faceList.Add((uint)indices[i]);
                    faceList.Add((uint)indices[i + 1]);
                    matList.Add(currentMat);
                }
            }
        }

        int vertCount = positions.Count / 3;
        verts = new float[vertCount * 4]; // vec4 xyz+pad
        for (int i = 0; i < vertCount; i++)
        {
            verts[i * 4 + 0] = positions[i * 3 + 0];
            verts[i * 4 + 1] = positions[i * 3 + 1];
            verts[i * 4 + 2] = positions[i * 3 + 2];
            verts[i * 4 + 3] = 0;
        }

        // ---- 材质级清理 ----
        // 1) 剔除"笼子"材质：包围盒尺寸远超其余材质最大值（如 17³ 的摄影棚盒，
        //    12 个三角形却撑大整个网格包围盒，让刀挤进一个格子）
        int nMat = matNameList.Count;
        var matMin = new Vector3[nMat];
        var matMax = new Vector3[nMat];
        for (int m = 0; m < nMat; m++)
        {
            matMin[m] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            matMax[m] = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        }
        for (int i = 0; i < matList.Count; i++)
        {
            int m = (int)matList[i];
            for (int v = 0; v < 3; v++)
            {
                int vi = (int)faceList[i * 3 + v];
                matMin[m].X = Math.Min(matMin[m].X, verts[vi * 4]);
                matMin[m].Y = Math.Min(matMin[m].Y, verts[vi * 4 + 1]);
                matMin[m].Z = Math.Min(matMin[m].Z, verts[vi * 4 + 2]);
                matMax[m].X = Math.Max(matMax[m].X, verts[vi * 4]);
                matMax[m].Y = Math.Max(matMax[m].Y, verts[vi * 4 + 1]);
                matMax[m].Z = Math.Max(matMax[m].Z, verts[vi * 4 + 2]);
            }
        }
        float[] matDim = new float[nMat];
        for (int m = 0; m < nMat; m++)
        {
            if (matMin[m].X == float.MaxValue) { matDim[m] = 0; continue; }
            var s = matMax[m] - matMin[m];
            matDim[m] = Math.Max(Math.Max(s.X, s.Y), s.Z);
        }
        // 逐个剔除：只要某材质尺寸 > 其余材质最大尺寸的 3 倍就算笼子
        int[] matTriCount = new int[nMat];
        foreach (var mc in matList) matTriCount[mc]++;
        bool[] isCage = new bool[nMat];
        for (int m = 0; m < nMat; m++)
        {
            if (matDim[m] <= 0) continue;
            float restMax = 0;
            for (int o = 0; o < nMat; o++)
                if (o != m && !isCage[o]) restMax = Math.Max(restMax, matDim[o]);
            if (restMax > 0 && matDim[m] > restMax * 3f)
            {
                isCage[m] = true;
                GD.Print($"剔除笼子材质: {matNameList[m]} (尺寸 {matDim[m]:F1} >> 其余最大 {restMax:F1}, {matTriCount[m]} 三角形)");
            }
        }

        // 2) 剔除笼子材质的三角形（BVH 能吃下全量，无需抽稀）
        bool anyCage = false;
        for (int m = 0; m < nMat; m++) anyCage |= isCage[m];
        if (anyCage)
        {
            var keepFace = new List<uint>(faceList.Count);
            var keepMat = new List<uint>(matList.Count);
            for (int i = 0; i < matList.Count; i++)
            {
                if (isCage[matList[i]]) continue;
                keepFace.Add(faceList[i * 3]);
                keepFace.Add(faceList[i * 3 + 1]);
                keepFace.Add(faceList[i * 3 + 2]);
                keepMat.Add(matList[i]);
            }
            faceList = keepFace;
            matList = keepMat;
        }

        int triCount = matList.Count;
        tris = new uint[triCount * 4]; // uvec4: 3 vertex indices + material ID
        for (int i = 0; i < triCount; i++)
        {
            tris[i * 4 + 0] = faceList[i * 3 + 0];
            tris[i * 4 + 1] = faceList[i * 3 + 1];
            tris[i * 4 + 2] = faceList[i * 3 + 2];
            tris[i * 4 + 3] = matList[i];
        }
        triMats = matList.ToArray();
        matNames = matNameList.ToArray();

        // 包围盒：只从被三角形实际引用的顶点计算（排除杂散/背景顶点）
        meshMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        meshMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        var usedVerts = new HashSet<int>();
        for (int i = 0; i < faceList.Count; i++) usedVerts.Add((int)faceList[i]);
        foreach (int vi in usedVerts)
        {
            meshMin.X = Math.Min(meshMin.X, verts[vi * 4]);
            meshMin.Y = Math.Min(meshMin.Y, verts[vi * 4 + 1]);
            meshMin.Z = Math.Min(meshMin.Z, verts[vi * 4 + 2]);
            meshMax.X = Math.Max(meshMax.X, verts[vi * 4]);
            meshMax.Y = Math.Max(meshMax.Y, verts[vi * 4 + 1]);
            meshMax.Z = Math.Max(meshMax.Z, verts[vi * 4 + 2]);
        }

        GD.Print($"OBJ 加载: {vertCount} 顶点, {triCount} 三角形, {matNames.Length} 材质");
    }

    // ---- BVH 构建（uint 索引版，杜绝旧 float 编码 bug）----------------------
    // 布局：bvhNodes[2i].xyz=min / bvhNodes[2i+1].xyz=max（float）
    //       bvhMeta[i] = (left, right, firstTri, triCount)；left=0xFFFFFFFF=空
    // 叶子 ≤8 三角形；中位数分割保证树平衡（深度 ~log2(n/8)）
    public static void BuildBVH(float[] verts, uint[] tris,
        out float[] bvhNodes, out uint[] bvhMeta, out uint[] reorderedTris)
    {
        const uint NIL = 0xFFFFFFFFu;
        const int LEAF_SIZE = 8;
        int triCount = tris.Length / 4;

        var nodes = new List<float>(triCount * 4);       // 每节点 2 个 vec4
        var meta = new List<uint>(triCount * 4);          // 每节点 1 个 uvec4
        var reordered = new List<uint>(Math.Max(triCount, 1) * 4);

        if (triCount == 0)
        {
            // 空网格：单个退化根节点（AABB 永不命中）
            bvhNodes = new float[] { 1e9f, 1e9f, 1e9f, 0, -1e9f, -1e9f, -1e9f, 0 };
            bvhMeta = new uint[] { NIL, NIL, 0u, 0u };
            reorderedTris = new uint[4];
            return;
        }

        var centroids = new float[triCount * 3];
        var triIdx = new int[triCount];
        for (int i = 0; i < triCount; i++)
        {
            triIdx[i] = i;
            int v0 = (int)tris[i * 4], v1 = (int)tris[i * 4 + 1], v2 = (int)tris[i * 4 + 2];
            centroids[i * 3] = (verts[v0 * 4] + verts[v1 * 4] + verts[v2 * 4]) / 3;
            centroids[i * 3 + 1] = (verts[v0 * 4 + 1] + verts[v1 * 4 + 1] + verts[v2 * 4 + 1]) / 3;
            centroids[i * 3 + 2] = (verts[v0 * 4 + 2] + verts[v1 * 4 + 2] + verts[v2 * 4 + 2]) / 3;
        }

        int nodeCount = BuildBVHRec(verts, tris, centroids, triIdx, 0, triCount,
            nodes, meta, reordered, LEAF_SIZE);
        _ = nodeCount;

        bvhNodes = nodes.ToArray();
        bvhMeta = meta.ToArray();
        reorderedTris = reordered.ToArray();

        int leaves = 0, maxDepth = 0;
        for (int i = 0; i < meta.Count / 4; i++)
            if (meta[i * 4 + 3] > 0) leaves++;
        // 深度用节点数估算（平衡中位数分割）
        maxDepth = (int)Math.Ceiling(Math.Log2(Math.Max(triCount / (double)LEAF_SIZE, 1))) + 1;
        GD.Print($"BVH: {bvhNodes.Length / 8} 节点 ({leaves} 叶), {reorderedTris.Length / 4} 三角形, 深度≈{maxDepth}");
    }

    private static int BuildBVHRec(float[] verts, uint[] tris, float[] centroids,
        int[] triIdx, int start, int count,
        List<float> nodes, List<uint> meta, List<uint> reordered, int leafSize)
    {
        // 本节点 AABB（三角形并集）
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
        float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        for (int i = start; i < start + count; i++)
        {
            int ti = triIdx[i];
            for (int v = 0; v < 3; v++)
            {
                int vi = (int)tris[ti * 4 + v];
                mnx = Math.Min(mnx, verts[vi * 4]);
                mny = Math.Min(mny, verts[vi * 4 + 1]);
                mnz = Math.Min(mnz, verts[vi * 4 + 2]);
                mxx = Math.Max(mxx, verts[vi * 4]);
                mxy = Math.Max(mxy, verts[vi * 4 + 1]);
                mxz = Math.Max(mxz, verts[vi * 4 + 2]);
            }
        }

        int self = nodes.Count / 8;

        if (count <= leafSize)
        {
            uint firstTri = (uint)(reordered.Count / 4);
            for (int i = start; i < start + count; i++)
            {
                int ti = triIdx[i];
                reordered.Add(tris[ti * 4]);
                reordered.Add(tris[ti * 4 + 1]);
                reordered.Add(tris[ti * 4 + 2]);
                reordered.Add(tris[ti * 4 + 3]);
            }
            nodes.Add(mnx); nodes.Add(mny); nodes.Add(mnz); nodes.Add(0);
            nodes.Add(mxx); nodes.Add(mxy); nodes.Add(mxz); nodes.Add(0);
            meta.Add(0xFFFFFFFFu); meta.Add(0xFFFFFFFFu);  // left, right = NIL
            meta.Add(firstTri); meta.Add((uint)count);
            return self;
        }

        // 最长轴中位数分割（质心排序）
        float ex = mxx - mnx, ey = mxy - mny, ez = mxz - mnz;
        int axis = ex > ey && ex > ez ? 0 : (ey > ez ? 1 : 2);
        Array.Sort(triIdx, start, count, Comparer<int>.Create((a, b) =>
            centroids[a * 3 + axis].CompareTo(centroids[b * 3 + axis])));
        int mid = start + count / 2;

        // 占位（AABB 已知可先写，子索引后回填）
        nodes.Add(mnx); nodes.Add(mny); nodes.Add(mnz); nodes.Add(0);
        nodes.Add(mxx); nodes.Add(mxy); nodes.Add(mxz); nodes.Add(0);
        meta.Add(0); meta.Add(0); meta.Add(0); meta.Add(0);

        int left = BuildBVHRec(verts, tris, centroids, triIdx, start, mid - start,
            nodes, meta, reordered, leafSize);
        int right = BuildBVHRec(verts, tris, centroids, triIdx, mid, start + count - mid,
            nodes, meta, reordered, leafSize);
        meta[self * 4 + 0] = (uint)left;
        meta[self * 4 + 1] = (uint)right;
        // 内部节点 triCount = 0

        return self;
    }

    // ---- OBJ 材质名 → 引擎材质映射 ----
    // 返回 float[]: 每材质 8 个 float (albedo.xyz+rough, emission.xyz+type)
    // type: 0=漫反射 1=金属 2=发光 3=玻璃
    public static float[] BuildMeshMaterials(string[] matNames, string mtlPath = null)
    {
        // 解析 MTL 文件获取 Kd 颜色
        var mtlKd = new Dictionary<string, float[]>();
        if (mtlPath != null && File.Exists(mtlPath))
        {
            string cur = null;
            foreach (var line in File.ReadLines(mtlPath))
            {
                var p = line.Trim().Split(' ');
                if (p[0] == "newmtl" && p.Length > 1) cur = p[1];
                else if (p[0] == "Kd" && p.Length >= 4 && cur != null)
                    mtlKd[cur] = new[] { float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]) };
            }
        }

        var mats = new float[Math.Max(matNames.Length, 1) * 8];
        void Set(int id, float r, float g, float b, float rough,
                 float er, float eg, float eb, float type)
        {
            if (id * 8 + 7 >= mats.Length) return;
            mats[id * 8 + 0] = r; mats[id * 8 + 1] = g; mats[id * 8 + 2] = b; mats[id * 8 + 3] = rough;
            mats[id * 8 + 4] = er; mats[id * 8 + 5] = eg; mats[id * 8 + 6] = eb; mats[id * 8 + 7] = type;
        }

        for (int i = 0; i < matNames.Length; i++)
        {
            string n = matNames[i].ToLower();
            var kd = mtlKd.TryGetValue(matNames[i], out var c) ? c : new[] { 0.5f, 0.5f, 0.5f };

            if (n == "default") { Set(i, 0.5f, 0.5f, 0.5f, 0.8f, 0, 0, 0, 0); continue; }

            // 玻璃
            if (n.Contains("glass") || n.Contains("window") || n.Contains("wind"))
                Set(i, 0.06f, 0.06f, 0.06f, 0, 0, 0, 0, 3);
            // 发光
            else if (n.Contains("tail") || n.Contains("brak"))
                Set(i, 0, 0, 0, 1, 3.0f, 0.1f, 0.05f, 2);
            else if ((n.Contains("light") || n.Contains("head") || n.Contains("lamp")) && !n.Contains("studio"))
                Set(i, 0, 0, 0, 1, 4.0f, 3.8f, 3.2f, 2);
            // 背景/棚 → 深灰吸收
            else if (n.Contains("studio") || n.Contains("back") || n.Contains("drop")
                  || n.Contains("floor") || n.Contains("sun") || n == "none" || n.Contains("curtain"))
                Set(i, 0.1f, 0.1f, 0.1f, 1, 0, 0, 0, 0);
            // 金属/镀铬（含中文：刀头=刀刃，环=金属环，金色材质）
            else if (n.Contains("chrome") || n.Contains("grill") || n.Contains("metal")
                  || n.Contains("engine") || n.Contains("trim") || n.Contains("silver")
                  || n.Contains("nut") || n.Contains("mirror")
                  || n.Contains("刀头") || n.Contains("环") || n.Contains("blade")
                  || (kd[0] > 0.8f && kd[1] > 0.6f && kd[2] < 0.1f))  // 金色 Kd → 金属
                Set(i, kd[0], kd[1], kd[2], 0.08f, 0, 0, 0, 1);
            // 轮胎/黑色粗糙
            else if (n.Contains("tire") || n.Contains("tyre") || n.Contains("rubber")
                  || n.Contains("wheel") || n.Contains("rim"))
                Set(i, 0.07f, 0.07f, 0.08f, 0.95f, 0, 0, 0, 0);
            // 车漆/光泽表面（含中文材质.005/.006/.007 → 用 Kd 颜色）
            else if (n.Contains("navy") || n.Contains("body") || n.Contains("paint") || n.Contains("blue"))
            {
                if (n.Contains("navy")) Set(i, 0.04f, 0.05f, 0.10f, 0.18f, 0, 0, 0, 0);
                else Set(i, 0.05f, 0.10f, 0.70f, 0.18f, 0, 0, 0, 0);
            }
            // 默认：用 MTL 的 Kd 颜色，中等粗糙度
            else if (Math.Max(kd[0], Math.Max(kd[1], kd[2])) < 0.06f)
                // 纯黑 Kd + 高光泽（如刀身/镡的 Ns 600-1000）→ 暗钢金属（有反光而非死黑）
                Set(i, 0.24f, 0.25f, 0.27f, 0.18f, 0, 0, 0, 1);
            else
                Set(i, kd[0], kd[1], kd[2], 0.6f, 0, 0, 0, 0);
        }
        return mats;
    }
}
