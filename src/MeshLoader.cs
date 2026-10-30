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
            }
            else if (line.StartsWith("f "))
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

        // 包围盒
        meshMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        meshMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < vertCount; i++)
        {
            meshMin.X = Math.Min(meshMin.X, verts[i * 4]);
            meshMin.Y = Math.Min(meshMin.Y, verts[i * 4 + 1]);
            meshMin.Z = Math.Min(meshMin.Z, verts[i * 4 + 2]);
            meshMax.X = Math.Max(meshMax.X, verts[i * 4]);
            meshMax.Y = Math.Max(meshMax.Y, verts[i * 4 + 1]);
            meshMax.Z = Math.Max(meshMax.Z, verts[i * 4 + 2]);
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
        BuildBVHRecursive(verts, tris, centroids, triIndices,
            mid, start + count - mid, nodeA, nodeB, reordered);

        // 回填
        nodeA[placeholderIdx * 4 + 0] = mnx;
        nodeA[placeholderIdx * 4 + 1] = mny;
        nodeA[placeholderIdx * 4 + 2] = mnz;
        nodeA[placeholderIdx * 4 + 3] = leftChild;
        nodeB[placeholderIdx * 4 + 0] = mxx;
        nodeB[placeholderIdx * 4 + 1] = mxy;
        nodeB[placeholderIdx * 4 + 2] = mxz;
        nodeB[placeholderIdx * 4 + 3] = 0; // 0 = 内部节点

        return placeholderIdx;
    }

    // ---- OBJ 材质名 → 引擎材质映射 ----
    // 返回 float[]: 每材质 8 个 float (albedo.xyz+rough, emission.xyz+type)
    public static float[] BuildMeshMaterials(string[] matNames)
    {
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
            if (n == "default") { Set(i, 0.5f, 0.5f, 0.5f, 0.8f, 0, 0, 0, 0); continue; }
            if (n.Contains("glass") || n.Contains("window") || n.Contains("wind"))
                Set(i, 0.06f, 0.06f, 0.06f, 0, 0, 0, 0, 2);  // 玻璃（albedo=σ）
            else if (n.Contains("tail") || n.Contains("brak"))
                Set(i, 0, 0, 0, 1, 3.0f, 0.1f, 0.05f, 2);     // 红尾灯
            else if ((n.Contains("light") || n.Contains("head") || n.Contains("lamp")) && !n.Contains("studio"))
                Set(i, 0, 0, 0, 1, 4.0f, 3.8f, 3.2f, 2);       // 白前灯
            else if (n.Contains("studio") || n.Contains("back") || n.Contains("drop") || n.Contains("floor") || n.Contains("sun") || n.Contains("none") || n.Contains("curtain"))
                Set(i, 0.1f, 0.1f, 0.1f, 1, 0, 0, 0, 0);       // 背景/棚（深灰吸收）
            else if (n.Contains("tire") || n.Contains("tyre") || n.Contains("rubber") || n.Contains("wheel") || n.Contains("rim"))
                Set(i, 0.07f, 0.07f, 0.08f, 0.95f, 0, 0, 0, 0); // 轮胎
            else if (n.Contains("chrome") || n.Contains("grill") || n.Contains("metal") || n.Contains("engine")
                  || n.Contains("trim") || n.Contains("silver") || n.Contains("nut") || n.Contains("mirror"))
                Set(i, 0.92f, 0.92f, 0.95f, 0.06f, 0, 0, 0, 1); // 镀铬/金属
            else if (n.Contains("navy") || n.Contains("body") || n.Contains("paint") || n.Contains("blue"))
            {
                if (n.Contains("navy"))
                    Set(i, 0.04f, 0.05f, 0.10f, 0.18f, 0, 0, 0, 0);  // 藏青漆
                else
                    Set(i, 0.05f, 0.10f, 0.70f, 0.18f, 0, 0, 0, 0);  // 蓝漆
            }
            else
                Set(i, 0.4f, 0.4f, 0.42f, 0.7f, 0, 0, 0, 0);    // 默认灰
        }
        return mats;
    }
}
