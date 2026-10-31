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

    // ---- 行读取：res://（编辑器=项目目录，导出=pck 内）与普通路径双兼容 ----
    // 不存在时返回空序列（MTL 可选）
    private static IEnumerable<string> ReadObjLines(string path)
    {
        if (path.StartsWith("res://"))
        {
            var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (f == null) yield break;
            using (f)
            {
                while (!f.EofReached())
                    yield return f.GetLine();
            }
        }
        else
        {
            if (!File.Exists(path)) yield break;
            foreach (var l in File.ReadLines(path)) yield return l;
        }
    }

    // ---- OBJ 解析 ----
    public static void LoadObj(string path, float scale, Vector3 offset,
        out float[] verts, out uint[] tris, out uint[] triMats,
        out string[] matNames, out Vector3 meshMin, out Vector3 meshMax,
        out float[] uvs, float rotYDeg = 0f)
    {
        var positions = new List<float>(744000 * 3);
        var uvList = new List<float>(1024);          // vt 坐标（每项 2 float）
        var faceList = new List<uint>(760000 * 3);
        var faceUv = new List<int>(760000 * 3);      // 每角对应的 vt 索引（-1 = 无）
        var matList = new List<uint>(760000);
        var matNameList = new List<string>();
        var matIdMap = new Dictionary<string, uint>();
        float rc = (float)Math.Cos(rotYDeg * Math.PI / 180.0), rs = (float)Math.Sin(rotYDeg * Math.PI / 180.0);

        uint currentMat = 0;
        string currentMatName = "default";
        bool _skipCurrent = false;
        if (!matIdMap.ContainsKey("default")) { matIdMap["default"] = 0; matNameList.Add("default"); }

        foreach (var line in ReadObjLines(path))
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
            else if (line.StartsWith("vt "))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 3)
                {
                    uvList.Add(float.Parse(parts[1]));
                    uvList.Add(float.Parse(parts[2]));
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
                // 提取顶点索引（v/vt/vn 三段，vt 供法线贴图）
                var indices = new List<int>();
                var uvIndices = new List<int>();
                int uvCount = uvList.Count / 2;
                for (int i = 1; i < parts.Length; i++)
                {
                    var seg = parts[i].Split('/');
                    int vt = -1;
                    if (seg.Length > 1 && seg[1].Length > 0 && int.TryParse(seg[1], out var vt0))
                        vt = vt0 > 0 ? vt0 - 1 : uvCount + vt0;
                    // 顶点段解析失败（空段）时两个列表都跳过，保持逐角对齐
                    if (int.TryParse(seg[0], out var v))
                    {
                        indices.Add(v > 0 ? v - 1 : positions.Count / 3 + v);
                        uvIndices.Add(vt);
                    }
                }
                // 三角扇
                for (int i = 1; i + 1 < indices.Count; i++)
                {
                    faceList.Add((uint)indices[0]);
                    faceList.Add((uint)indices[i]);
                    faceList.Add((uint)indices[i + 1]);
                    faceUv.Add(uvIndices[0]);
                    faceUv.Add(uvIndices[i]);
                    faceUv.Add(uvIndices[i + 1]);
                    matList.Add(currentMat);
                }
            }
        }

        // ---- 顶点膨胀：OBJ 的 v/vt 分离索引 → (v,vt) 去重合并成带 UV 的单顶点表 ----
        // （无 vt 的模型退化为原顶点表、UV 全 0 —— 与武士刀路径完全兼容）
        int cornerCount = faceList.Count;
        var posOut = new List<float>(positions.Count);
        var uvOut = new List<float>(positions.Count / 3 * 2 + 8);
        var remap = new Dictionary<long, int>(positions.Count / 3);
        for (int c = 0; c < cornerCount; c++)
        {
            long key = ((long)faceList[c] << 32) | (uint)(faceUv[c] + 1);
            if (!remap.TryGetValue(key, out int ni))
            {
                ni = posOut.Count / 3;
                int vi = (int)faceList[c];
                posOut.Add(positions[vi * 3 + 0]);
                posOut.Add(positions[vi * 3 + 1]);
                posOut.Add(positions[vi * 3 + 2]);
                int ti = faceUv[c];
                if (ti >= 0 && ti * 2 + 1 < uvList.Count)
                {
                    uvOut.Add(uvList[ti * 2 + 0]);
                    uvOut.Add(uvList[ti * 2 + 1]);
                }
                else
                {
                    uvOut.Add(0f);
                    uvOut.Add(0f);
                }
                remap[key] = ni;
            }
            faceList[c] = (uint)ni;
        }

        int vertCount = posOut.Count / 3;
        verts = new float[vertCount * 4]; // vec4 xyz+pad
        for (int i = 0; i < vertCount; i++)
        {
            verts[i * 4 + 0] = posOut[i * 3 + 0];
            verts[i * 4 + 1] = posOut[i * 3 + 1];
            verts[i * 4 + 2] = posOut[i * 3 + 2];
            verts[i * 4 + 3] = 0;
        }
        uvs = uvOut.ToArray();

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

    // ---- BVH CPU 自检：随机射线 对比 BVH 遍历 vs 暴力求交 ----
    // 返回 false 时打印首个不一致（定位数据/遍历 bug）
    public static bool BvhSelfTest(float[] verts, float[] bvhNodes, uint[] bvhMeta, uint[] bvhTris,
        int tests = 200)
    {
        int triCount = bvhTris.Length / 4;
        var rng = new Random(42);
        // 包围盒
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
        float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        for (int t = 0; t < triCount; t++)
            for (int v = 0; v < 3; v++)
            {
                int vi = (int)bvhTris[t * 4 + v];
                mnx = Math.Min(mnx, verts[vi * 4]); mny = Math.Min(mny, verts[vi * 4 + 1]);
                mnz = Math.Min(mnz, verts[vi * 4 + 2]);
                mxx = Math.Max(mxx, verts[vi * 4]); mxy = Math.Max(mxy, verts[vi * 4 + 1]);
                mxz = Math.Max(mxz, verts[vi * 4 + 2]);
            }
        var ctr = new Vector3((mnx + mxx) / 2, (mny + mxy) / 2, (mnz + mxz) / 2);
        float rad = Math.Max(Math.Max(mxx - mnx, mxy - mny), mxz - mnz) * 0.8f + 1f;

        for (int test = 0; test < tests; test++)
        {
            // 随机外部点 → 随机内部点
            var ro = ctr + new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1,
                                       rng.NextSingle() * 2 - 1) * rad;
            var tgt = ctr + new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1,
                                        rng.NextSingle() * 2 - 1) * rad * 0.5f;
            var rd = (tgt - ro).Normalized();

            // 暴力最近命中
            float brute = float.MaxValue;
            for (int t = 0; t < triCount; t++)
            {
                var hit = TriHit(verts, bvhTris, t, ro, rd);
                if (hit > 1e-4 && hit < brute) brute = hit;
            }
            // BVH 遍历
            float bvh = BvhHit(verts, bvhNodes, bvhMeta, bvhTris, ro, rd);
            bool ok = (brute == float.MaxValue && bvh == float.MaxValue)
                   || Math.Abs(brute - bvh) < 1e-3;
            if (!ok)
            {
                GD.Print($"[BVH自检失败] test={test} ro=({ro.X:F1},{ro.Y:F1},{ro.Z:F1}) " +
                         $"rd=({rd.X:F2},{rd.Y:F2},{rd.Z:F2}) 暴力={brute:F3} BVH={bvh:F3}");
                return false;
            }
        }
        GD.Print($"[BVH自检通过] {tests} 条射线一致 (三角形 {triCount})");
        return true;
    }

    private static float TriHit(float[] verts, uint[] tris, int t, Vector3 ro, Vector3 rd)
    {
        var v0 = new Vector3(verts[(int)tris[t * 4] * 4], verts[(int)tris[t * 4] * 4 + 1], verts[(int)tris[t * 4] * 4 + 2]);
        var v1 = new Vector3(verts[(int)tris[t * 4 + 1] * 4], verts[(int)tris[t * 4 + 1] * 4 + 1], verts[(int)tris[t * 4 + 1] * 4 + 2]);
        var v2 = new Vector3(verts[(int)tris[t * 4 + 2] * 4], verts[(int)tris[t * 4 + 2] * 4 + 1], verts[(int)tris[t * 4 + 2] * 4 + 2]);
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var pv = rd.Cross(e2);
        float det = e1.Dot(pv);
        if (Math.Abs(det) < 1e-10) return -1;
        float invDet = 1f / det;
        var tv = ro - v0;
        float u = tv.Dot(pv) * invDet;
        if (u < 0 || u > 1) return -1;
        var qv = tv.Cross(e1);
        float v = rd.Dot(qv) * invDet;
        if (v < 0 || u + v > 1) return -1;
        return e2.Dot(qv) * invDet;
    }

    private static float BvhHit(float[] verts, float[] bvhNodes, uint[] bvhMeta, uint[] bvhTris,
        Vector3 ro, Vector3 rd)
    {
        const uint NIL = 0xFFFFFFFFu;
        float best = float.MaxValue;
        var inv = new Vector3(
            1f / (Math.Abs(rd.X) < 1e-10f ? 1e-10f : rd.X),
            1f / (Math.Abs(rd.Y) < 1e-10f ? 1e-10f : rd.Y),
            1f / (Math.Abs(rd.Z) < 1e-10f ? 1e-10f : rd.Z));
        var stack = new int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int n = stack[--sp];
            uint l = bvhMeta[n * 4], r = bvhMeta[n * 4 + 1];
            uint first = bvhMeta[n * 4 + 2], cnt = bvhMeta[n * 4 + 3];
            var mn = new Vector3(bvhNodes[n * 8], bvhNodes[n * 8 + 1], bvhNodes[n * 8 + 2]);
            var mx = new Vector3(bvhNodes[n * 8 + 4], bvhNodes[n * 8 + 5], bvhNodes[n * 8 + 6]);
            // AABB slab
            float t1 = (mn.X - ro.X) * inv.X, t2 = (mx.X - ro.X) * inv.X;
            float tN = Math.Min(t1, t2), tF = Math.Max(t1, t2);
            t1 = (mn.Y - ro.Y) * inv.Y; t2 = (mx.Y - ro.Y) * inv.Y;
            tN = Math.Max(tN, Math.Min(t1, t2)); tF = Math.Min(tF, Math.Max(t1, t2));
            t1 = (mn.Z - ro.Z) * inv.Z; t2 = (mx.Z - ro.Z) * inv.Z;
            tN = Math.Max(tN, Math.Min(t1, t2)); tF = Math.Min(tF, Math.Max(t1, t2));
            if (tN > tF || tF < 0 || tN > best) continue;
            if (cnt > 0)
            {
                for (uint ti = first; ti < first + cnt; ti++)
                {
                    float h = TriHit(verts, bvhTris, (int)ti, ro, rd);
                    if (h > 1e-4 && h < best) best = h;
                }
            }
            else
            {
                if (r != NIL && sp < 64) stack[sp++] = (int)r;
                if (l != NIL && sp < 64) stack[sp++] = (int)l;
            }
        }
        return best;
    }

    // ---- OBJ 材质名 → 引擎材质映射 ----
    // 返回 float[]: 每材质 8 个 float (albedo.xyz+rough, emission.xyz+type)
    // type: 0=漫反射 1=金属 2=发光 3=玻璃 4=金属+法线贴图 5=车漆/漫反射+法线贴图
    public static float[] BuildMeshMaterials(string[] matNames, string mtlPath = null,
        bool normalMap = false)
    {
        float tMetal = normalMap ? 4f : 1f;
        float tPaint = normalMap ? 5f : 0f;
        // 解析 MTL 文件获取 Kd 颜色
        var mtlKd = new Dictionary<string, float[]>();
        if (mtlPath != null)
        {
            string cur = null;
            foreach (var line in ReadObjLines(mtlPath))
            {
                var p = line.Trim().Split(' ');
                if (p.Length == 0 || p[0].Length == 0) continue;
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
                Set(i, kd[0], kd[1], kd[2], 0.08f, 0, 0, 0, tMetal);
            // 轮胎/黑色粗糙
            else if (n.Contains("tire") || n.Contains("tyre") || n.Contains("rubber")
                  || n.Contains("wheel") || n.Contains("rim"))
                Set(i, 0.07f, 0.07f, 0.08f, 0.95f, 0, 0, 0, tPaint);
            // 车漆/光泽表面（含中文材质.005/.006/.007 → 用 Kd 颜色）
            else if (n.Contains("navy") || n.Contains("body") || n.Contains("paint") || n.Contains("blue"))
            {
                if (n.Contains("navy")) Set(i, 0.04f, 0.05f, 0.10f, 0.18f, 0, 0, 0, tPaint);
                else Set(i, 0.05f, 0.10f, 0.70f, 0.18f, 0, 0, 0, tPaint);
            }
            // 默认：用 MTL 的 Kd 颜色，中等粗糙度
            else if (Math.Max(kd[0], Math.Max(kd[1], kd[2])) < 0.06f)
                // 纯黑 Kd + 高光泽（如刀身/镡的 Ns 600-1000）→ 暗钢金属（有反光而非死黑）
                Set(i, 0.24f, 0.25f, 0.27f, 0.18f, 0, 0, 0, tMetal);
            else
                Set(i, kd[0], kd[1], kd[2], 0.6f, 0, 0, 0, tPaint);
        }
        return mats;
    }
}
