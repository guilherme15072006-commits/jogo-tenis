"""(roda no Blender) Prepara um personagem riggado pelo Tripo para o jogo.
blender -b -P preparar_rig_tripo.py -- TRIPO.fbx PASTA_SAIDA

1. Escala real (1,80 m) e pés no chão.
2. Limpa vazamento de pesos: cada pedaço do Tripo pertence a uma região (cabeça, tronco, braço E/D, perna E/D);
   pesos de ossos de outra região são removidos (ex.: short que seguia os dedos porque a mão encostava na coxa).
3. Raquete presa 100 % na mão direita (rígida, não entorta com os dedos).
4. Braços abertos em pose T aplicada como pose de descanso (o Unity monta o avatar humanoide a partir dela).
5. Exporta FBX + prévias de teste de deformação.
"""
import bpy, sys, math, os
from mathutils import Vector, Matrix

a = sys.argv[sys.argv.index("--") + 1:]
FBX, OUT = a[0], a[1]
RACKET_PARTS = {3, 12, 17, 27}          # pedaços da raquete neste modelo do Tripo
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=FBX)
sc = bpy.context.scene
arm = [o for o in sc.objects if o.type == 'ARMATURE'][0]
meshes = [o for o in sc.objects if o.type == 'MESH']

def region(bone):
    b = bone.lower()
    if b.startswith("left_") and any(k in b for k in ("shoulder", "upperarm", "lowerarm", "hand", "thumb", "index", "middle", "ring", "pinky")): return "LA"
    if b.startswith("right_") and any(k in b for k in ("shoulder", "upperarm", "lowerarm", "hand", "thumb", "index", "middle", "ring", "pinky")): return "RA"
    if b.startswith("left_") and any(k in b for k in ("upperleg", "lowerleg", "foot", "toes")): return "LL"
    if b.startswith("right_") and any(k in b for k in ("upperleg", "lowerleg", "foot", "toes")): return "RL"
    return "C"                                   # hips, spine, chest, neck, head, eyes
ALLOWED = {"LA": {"LA", "C"}, "RA": {"RA", "C"}, "LL": {"LL", "C"}, "RL": {"RL", "C"}, "C": {"C", "LA", "RA", "LL", "RL"}}
# shoulders of the shirt may follow the upper arms, the shorts may follow both thighs, nothing below the waist follows a hand
CHEST_OK_ARM = {"shoulder", "upperarm"}; HIPS_OK_LEG = {"upperleg"}

def part_id(o):
    try: return int(o.name.split("_part_")[1].split(".")[0].split("_")[0])
    except Exception: return -1

# ---------------------------------------------------------------- 2. weight cleanup
removed = 0
for m in meshes:
    pid = part_id(m)
    if pid in RACKET_PARTS: continue
    gname = {g.index: g.name for g in m.vertex_groups}
    tot = {}
    for v in m.data.vertices:
        for g in v.groups: tot[region(gname[g.group])] = tot.get(region(gname[g.group]), 0) + g.weight
    if not tot: continue
    reg = max(tot, key=tot.get)
    # strongest bone of the part inside its own region: fallback for vertices left with no valid weight
    gw = {}
    for v in m.data.vertices:
        for g in v.groups:
            if region(gname[g.group]) == reg: gw[g.group] = gw.get(g.group, 0) + g.weight
    main_g = max(gw, key=gw.get) if gw else None
    lowest_z = min((m.matrix_world @ v.co).z for v in m.data.vertices)
    for v in m.data.vertices:
        keep = []
        for g in v.groups:
            n = gname[g.group]; r = region(n); nl = n.lower()
            ok = r in ALLOWED[reg]
            if reg == "C" and r in ("LA", "RA"):
                ok = any(k in nl for k in CHEST_OK_ARM) and (m.matrix_world @ v.co).z > 0.62     # only the shirt near the shoulders
            if reg == "C" and r in ("LL", "RL"):
                ok = any(k in nl for k in HIPS_OK_LEG)
            if ok: keep.append((g.group, g.weight))
            else: removed += 1
        s = sum(w for _, w in keep)
        if s <= 1e-6:
            if main_g is None: continue
            keep, s = [(main_g, 1.0)], 1.0       # e.g. thigh vertices that only followed the fingers
        for g in list(v.groups): m.vertex_groups[g.group].remove([v.index])
        for gi, w in keep: m.vertex_groups[gi].add([v.index], w / s, 'REPLACE')
print("PESOS removidos", removed)

# ---------------------------------------------------------------- 3. rigid racket on the right hand
for m in meshes:
    if part_id(m) not in RACKET_PARTS: continue
    for g in list(m.vertex_groups): m.vertex_groups.remove(g)
    vg = m.vertex_groups.new(name="Right_Hand"); vg.add([v.index for v in m.data.vertices], 1.0, 'REPLACE')
print("RAQUETE presa na mão direita:", sorted(part_id(m) for m in meshes if part_id(m) in RACKET_PARTS))

# ---------------------------------------------------------------- 1. real scale
allv = [m.matrix_world @ v.co for m in meshes for v in m.data.vertices]
zmin, zmax = min(v.z for v in allv), max(v.z for v in allv)
S = 1.80 / (zmax - zmin)
# unparent, scale every object around the origin, bake (children of a scaled parent would be compensated otherwise)
for m in meshes:
    w = m.matrix_world.copy(); m.parent = None; m.matrix_world = w
for o in [arm] + meshes:
    o.matrix_world = Matrix.Scale(S, 4) @ o.matrix_world
bpy.context.view_layer.update()
for o in sc.objects: o.select_set(o in [arm] + meshes)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
for m in meshes:
    for md in m.modifiers:
        if md.type == 'ARMATURE': md.object = arm
allv = [m.matrix_world @ v.co for m in meshes for v in m.data.vertices]
dz = -min(v.z for v in allv)
arm.location.z += dz; bpy.context.view_layer.update()
print("ESCALA", round(S, 3), "altura", round(max(v.z for v in allv) - min(v.z for v in allv), 3))

# ---------------------------------------------------------------- 4. T-pose as rest pose
for o in sc.objects: o.select_set(False)
arm.select_set(True); bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
mw = arm.matrix_world
def aim(name, d):
    pb = arm.pose.bones[name]; bpy.context.view_layer.update()
    cur = (mw.to_3x3() @ (pb.tail - pb.head)).normalized()
    q = cur.rotation_difference(d.normalized()); head = mw @ pb.head
    M = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head)
    new = mw.inverted() @ M @ mw @ pb.matrix
    loc, rot, _ = new.decompose(); pb.matrix = Matrix.LocRotScale(loc, rot, Vector((1, 1, 1)))
    bpy.context.view_layer.update()
# character faces -Y; its left is +X
for side, sx in (("Left", 1), ("Right", -1)):
    for b in ("UpperArm", "LowerArm", "Hand"): aim(f"{side}_{b}", Vector((sx, 0, 0)))
bpy.ops.object.mode_set(mode='OBJECT')
for m in meshes:
    bpy.context.view_layer.objects.active = m
    for md in [md for md in m.modifiers if md.type == 'ARMATURE']: bpy.ops.object.modifier_apply(modifier=md.name)
bpy.context.view_layer.objects.active = arm
for o in sc.objects: o.select_set(False)
arm.select_set(True)
bpy.ops.object.mode_set(mode='POSE'); bpy.ops.pose.armature_apply(selected=False); bpy.ops.object.mode_set(mode='OBJECT')
for m in meshes:
    md = m.modifiers.new("Armature", 'ARMATURE'); md.object = arm
    if m.parent != arm:
        w = m.matrix_world.copy(); m.parent = arm; m.matrix_world = w
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "jogador_tripo_rig.blend"))

# ---------------------------------------------------------------- 5. deformation tests + export
sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.color_type = 'TEXTURE'
sc.render.resolution_x = 400; sc.render.resolution_y = 500
cam = bpy.data.objects.new("c", bpy.data.cameras.new("c")); sc.collection.objects.link(cam); sc.camera = cam
cam.location = (2.4, -4.0, 1.2); cam.rotation_euler = (math.radians(86), 0, math.radians(31))
def pose(rot):
    for pb in arm.pose.bones: pb.rotation_mode = 'XYZ'; pb.rotation_euler = (0, 0, 0)
    for n, (x, y, z) in rot.items(): arm.pose.bones[n].rotation_euler = (math.radians(x), math.radians(y), math.radians(z))
    bpy.context.view_layer.update()
tests = {"0_tpose": {}, "1_bracos_baixo": {"Right_UpperArm": (0, 0, -70), "Left_UpperArm": (0, 0, 70)},
         "2_bracos_cima": {"Right_UpperArm": (0, 0, 70), "Left_UpperArm": (0, 0, -70)},
         "3_cotovelos": {"Right_UpperArm": (0, 0, -60), "Left_UpperArm": (0, 0, 60), "Right_LowerArm": (70, 0, 0), "Left_LowerArm": (70, 0, 0)},
         "4_perna": {"Right_UpperArm": (0, 0, -70), "Left_UpperArm": (0, 0, 70), "Right_UpperLeg": (60, 0, 0), "Right_LowerLeg": (-70, 0, 0)},
         "5_tronco": {"Right_UpperArm": (0, 0, -70), "Left_UpperArm": (0, 0, 70), "Spine": (20, 20, 0), "Chest": (10, 20, 0)}}
paths = []
for name, r in tests.items():
    pose(r); p = os.path.join(OUT, f"teste_{name}.png"); sc.render.filepath = p; bpy.ops.render.render(write_still=True); paths.append(p)
pose({})
print("TESTES", ";".join(paths))
for o in sc.objects: o.select_set(o == arm or o in meshes)
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, "JogadorTripo.fbx"), use_selection=True, apply_unit_scale=True,
                         apply_scale_options='FBX_SCALE_ALL', add_leaf_bones=False, bake_anim=False,
                         path_mode='STRIP', embed_textures=False, use_armature_deform_only=False)
print("DONE")
