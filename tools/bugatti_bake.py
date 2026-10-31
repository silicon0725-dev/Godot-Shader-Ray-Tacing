# 布加迪：从高模烘焙法线贴图到低模 + 导出带 UV 的低模 OBJ
import bpy, time

ASSET = r"F:\光线追踪\VoxelPoolGodot\assets\bugatti_rt"
BLEND = ASSET + r"\bugatti_rt.blend"
PNG = ASSET + r"\bugatti_normal.png"
OBJ_OUT = ASSET + r"\bugatti_low.obj"

t0 = time.time()
bpy.ops.wm.open_mainfile(filepath=BLEND)
lo = bpy.data.objects["bugatti_lo"]
hi = bpy.data.objects["bugatti_hi"]
print(f"[INFO] blend loaded {time.time()-t0:.0f}s", flush=True)

# 烘焙贴图（法线：切线空间）
img = bpy.data.images.new("normal_bake", width=2048, height=2048, float_buffer=False)
try:
    img.colorspace_settings.name = 'Non-Color'
except Exception:
    pass

# 给低模每个材质塞一个 ImageTexture -> Normal 节点（烘焙目标），烘焙后还原
added = []
for slot in lo.material_slots:
    mat = slot.material
    if mat is None or not mat.use_nodes:
        continue
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        continue
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = img
    tex.select = True
    nt.nodes.active = tex
    link = nt.links.new(tex.outputs['Color'], bsdf.inputs['Normal'])
    added.append((nt, tex, link, bsdf))

scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 1
scene.cycles.device = 'CPU'
try:
    scene.cycles.bake_type = 'NORMAL'
except Exception:
    pass

bpy.ops.object.select_all(action='DESELECT')
hi.select_set(True)
lo.select_set(True)
bpy.context.view_layer.objects.active = lo
print(f"[INFO] baking normal map 2048x2048 ...", flush=True)
bpy.ops.object.bake(
    type='NORMAL',
    use_selected_to_active=True,
    margin=16,
    margin_type='EXTEND',
    normal_space='TANGENT',
    normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z',
)
print(f"[INFO] bake done {time.time()-t0:.0f}s", flush=True)

# 还原材质节点
for nt, tex, link, bsdf in added:
    try:
        nt.links.remove(link)
        nt.nodes.remove(tex)
    except Exception:
        pass

img.save_render(filepath=PNG)
print(f"[INFO] png saved {time.time()-t0:.0f}s", flush=True)

# 导出低模（隐藏高模，只导 lo）
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
