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
    // ---- 均匀网格加速（32³ 格子，每格三角形列表）----
    // 输出 gridInfo(4 float: minX minY minZ cellSize) + gridCells(32³ × 2 float: firstTri count)
    // 三角形按格子重排（每三角形按质心分配到唯一格子）
    public static void BuildGrid(float[] verts, uint[] tris,
        out float[] gridInfo, out float[] gridCells, out uint[] gridTris)
    {
        const int G = 32;   // 32³ 网格（64³ 太密导致 GPU 超时）
        const int MAX_PER_CELL = 2000;  // 单格上限 2000（防 GPU 超时）
        int triCount = tris.Length / 4;

        // 包围盒：只从三角形的顶点算（过滤后的实际范围，不含背景幕顶点）
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
        float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        for (int t = 0; t < triCount; t++)
        {
            for (int v = 0; v < 3; v++)
            {
                int vi = (int)tris[t * 4 + v];
                mnx = Math.Min(mnx, verts[vi * 4]);
                mny = Math.Min(mny, verts[vi * 4 + 1]);
                mnz = Math.Min(mnz, verts[vi * 4 + 2]);
                mxx = Math.Max(mxx, verts[vi * 4]);
                mxy = Math.Max(mxy, verts[vi * 4 + 1]);
                mxz = Math.Max(mxz, verts[vi * 4 + 2]);
            }
        }
        float cell = Math.Max(Math.Max(mxx - mnx, mxy - mny), mxz - mnz) / G;
        cell = Math.Max(cell, 0.01f);

        // 质心 → 格子索引
        var cellOf = new int[triCount];
        var cellCount = new int[G * G * G];
        for (int t = 0; t < triCount; t++)
        {
            int v0 = (int)tris[t * 4], v1 = (int)tris[t * 4 + 1], v2 = (int)tris[t * 4 + 2];
            float cx = (verts[v0 * 4] + verts[v1 * 4] + verts[v2 * 4]) / 3;
            float cy = (verts[v0 * 4 + 1] + verts[v1 * 4 + 1] + verts[v2 * 4 + 1]) / 3;
            float cz = (verts[v0 * 4 + 2] + verts[v1 * 4 + 2] + verts[v2 * 4 + 2]) / 3;
            int gx = Math.Clamp((int)((cx - mnx) / cell), 0, G - 1);
            int gy = Math.Clamp((int)((cy - mny) / cell), 0, G - 1);
            int gz = Math.Clamp((int)((cz - mnz) / cell), 0, G - 1);
            cellOf[t] = gx + gy * G + gz * G * G;
            cellCount[cellOf[t]]++;
        }

        // 截断过密格子（防 GPU 超时）
        for (int i = 0; i < G * G * G; i++)
            cellCount[i] = Math.Min(cellCount[i], MAX_PER_CELL);

        // 前缀和 → 每格起始位置
        var cellStart = new int[G * G * G + 1];
        for (int i = 0; i < G * G * G; i++) cellStart[i + 1] = cellStart[i] + cellCount[i];

        // 填充重排三角形（截断后总数）
        int totalKept = cellStart[G * G * G];
        var fill = new int[G * G * G];
        gridTris = new uint[totalKept * 4];
        for (int t = 0; t < triCount; t++)
        {
            int ci = cellOf[t];
            if (fill[ci] >= cellCount[ci]) continue;  // 截断：跳过超额三角形
            int dst = cellStart[ci] + fill[ci]++;
            Array.Copy(tris, t * 4, gridTris, dst * 4, 4);
        }

        // 输出
        gridInfo = new float[] { mnx, mny, mnz, cell };
        gridCells = new float[G * G * G * 2];
        for (int i = 0; i < G * G * G; i++)
        {
            gridCells[i * 2] = cellStart[i];
            gridCells[i * 2 + 1] = cellCount[i];
        }

        int maxCount = 0;
        for (int i = 0; i < G * G * G; i++) maxCount = Math.Max(maxCount, cellCount[i]);
        GD.Print($"网格: {G}³={G*G*G} 格, 最大格 {maxCount} 三角形, 平均 {triCount / (G*G*G):F0}");
    }

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
        out string[] matNames, out Vector3 meshMin, out Vector3 meshMax)
    {
        var positions = new List<float>(744000 * 3);
        var faceList = new List<uint>(760000 * 3);
        var matList = new List<uint>(760000);
        var matNameList = new List<string>();
        var matIdMap = new Dictionary<string, uint>();

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
                    positions.Add(float.Parse(parts[1]) * scale + offset.X);
                    positions.Add(float.Parse(parts[2]) * scale + offset.Y);
                    positions.Add(float.Parse(parts[3]) * scale + offset.Z);
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

    // ---- BVH 构建（中位数分割）----
    // 输出扁平节点数组 + 重排后的三角形索引
    // 节点: nodeA = vec4(minX, minY, minZ, leftChildOrFirstTri)
    //       nodeB = vec4(maxX, maxY, maxZ, triCount)  (0=内部节点, >0=叶节点)
    public static void BuildBVH(float[] verts, uint[] tris,
        out float[] nodeA, out float[] nodeB, out uint[] reorderedTris)
    {
        int triCount = tris.Length / 4;
        var centroids = new float[triCount * 3];
        var triIndices = new int[triCount];
        for (int i = 0; i < triCount; i++)
        {
            triIndices[i] = i;
            int v0 = (int)tris[i * 4], v1 = (int)tris[i * 4 + 1], v2 = (int)tris[i * 4 + 2];
            centroids[i * 3] = (verts[v0 * 4] + verts[v1 * 4] + verts[v2 * 4]) / 3;
            centroids[i * 3 + 1] = (verts[v0 * 4 + 1] + verts[v1 * 4 + 1] + verts[v2 * 4 + 1]) / 3;
            centroids[i * 3 + 2] = (verts[v0 * 4 + 2] + verts[v1 * 4 + 2] + verts[v2 * 4 + 2]) / 3;
        }

        var nodeListA = new List<float>(triCount * 2);
        var nodeListB = new List<float>(triCount * 2);
        var reordered = new List<uint>(triCount * 4);

        BuildBVHRecursive(verts, tris, centroids, triIndices,
            0, triCount, nodeListA, nodeListB, reordered);

        nodeA = nodeListA.ToArray();
        nodeB = nodeListB.ToArray();
        reorderedTris = reordered.ToArray();

        GD.Print($"BVH: {nodeA.Length / 4} 节点, 叶子 {triCount} 三角形");
    }

    private static int BuildBVHRecursive(float[] verts, uint[] tris, float[] centroids,
        int[] triIndices, int start, int count,
        List<float> nodeA, List<float> nodeB, List<uint> reordered)
    {
        int nodeIdx = nodeA.Count / 4;

        // 计算三角形范围 AABB
        float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
        float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
        for (int i = start; i < start + count; i++)
        {
            int ti = triIndices[i];
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

        const int LEAF_SIZE = 4;
        if (count <= LEAF_SIZE)
        {
            // 叶节点
            int firstTri = reordered.Count / 4;
            for (int i = start; i < start + count; i++)
            {
                int ti = triIndices[i];
                reordered.Add(tris[ti * 4]);
                reordered.Add(tris[ti * 4 + 1]);
                reordered.Add(tris[ti * 4 + 2]);
                reordered.Add(tris[ti * 4 + 3]);
            }
            nodeA.Add(mnx); nodeA.Add(mny); nodeA.Add(mnz); nodeA.Add(firstTri);
            nodeB.Add(mxx); nodeB.Add(mxy); nodeB.Add(mxz); nodeB.Add(count);
            return nodeIdx;
        }

        // 内部节点：最长轴中位数分割
        float ex = mxx - mnx, ey = mxy - mny, ez = mxz - mnz;
        int axis = ex > ey && ex > ez ? 0 : (ey > ez ? 1 : 2);

        // 按质心排序
        int mid = start + count / 2;
        Array.Sort(triIndices, start, count, Comparer<int>.Create((a, b) =>
            centroids[a * 3 + axis].CompareTo(centroids[b * 3 + axis])));
        // 重新定位 mid（Array.Sort 排的是 triIndices[start..start+count)）

        // 先占位（子节点索引需要回填）
        int placeholderIdx = nodeA.Count / 4;
        nodeA.Add(0); nodeA.Add(0); nodeA.Add(0); nodeA.Add(0);
        nodeB.Add(0); nodeB.Add(0); nodeB.Add(0); nodeB.Add(0);

        int leftChild = BuildBVHRecursive(verts, tris, centroids, triIndices,
            start, mid - start, nodeA, nodeB, reordered);
        int rightChild = BuildBVHRecursive(verts, tris, centroids, triIndices,
            mid, start + count - mid, nodeA, nodeB, reordered);

        // 回填：nodeA.w = 左子索引，nodeB.w = -(右子索引+1)（负数=内部节点）
        nodeA[placeholderIdx * 4 + 0] = mnx;
        nodeA[placeholderIdx * 4 + 1] = mny;
        nodeA[placeholderIdx * 4 + 2] = mnz;
        nodeA[placeholderIdx * 4 + 3] = leftChild;
        nodeB[placeholderIdx * 4 + 0] = mxx;
        nodeB[placeholderIdx * 4 + 1] = mxy;
        nodeB[placeholderIdx * 4 + 2] = mxz;
        nodeB[placeholderIdx * 4 + 3] = -(rightChild + 1); // 负数编码区分叶节点

        return placeholderIdx;
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
            else
                Set(i, kd[0], kd[1], kd[2], 0.6f, 0, 0, 0, 0);
        }
        return mats;
    }
}
