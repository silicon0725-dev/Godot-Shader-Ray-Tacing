# 布加迪修复：删除 decimate 产生的崩坏长条三角形（边长 > 1.5），重新导出
import bpy, time

ASSET = r"F:\光线追踪\VoxelPoolGodot\assets\bugatti_rt"
BLEND = ASSET + r"\bugatti_rt.blend"
OBJ_OUT = ASSET + r"\bugatti_low.obj"

t0 = time.time()
bpy.ops.wm.open_mainfile(filepath=BLEND)
lo = bpy.data.objects["bugatti_lo"]
hi = bpy.data.objects["bugatti_hi"]
print(f"[INFO] blend loaded {time.time()-t0:.0f}s", flush=True)

import bmesh
bm = bmesh.new()
bm.from_mesh(lo.data)
n_before = len(bm.faces)
bad = [f for f in bm.faces if max(e.calc_length() for e in f.edges) > 1.5]
for f in bad:
    bm.faces.remove(f)
bm.to_mesh(lo.data)
bm.free()
print(f"[INFO] removed {len(bad)}/{n_before} degenerate faces, {time.time()-t0:.0f}s", flush=True)

# 只导出低模
hi.hide_set(True)
bpy.ops.object.select_all(action='DESELECT')
lo.select_set(True)
bpy.context.view_layer.objects.active = lo
bpy.ops.wm.obj_export(
    filepath=OBJ_OUT,
    export_selected_objects=True,
    export_materials=True,
    export_triangulated_mesh=True,
    export_normals=False,
)
print(f"[DONE] obj exported, total {time.time()-t0:.0f}s", flush=True)
