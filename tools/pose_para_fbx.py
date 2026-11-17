"""Etapa 4 da automação (roda dentro do Blender): pose 3D (JSON) → animação no esqueleto do seu jogador → FBX.
blender -b jogador_tpose.blend -P pose_para_fbx.py -- POSE.json SAIDA.fbx [PREVIA.png]
"""
import bpy, sys, json, math
from mathutils import Vector, Matrix, Quaternion

a = sys.argv[sys.argv.index("--") + 1:]
POSE, SAIDA = a[0], a[1]
PREVIA = a[2] if len(a) > 2 else None
d = json.load(open(POSE, encoding="utf-8"))
Q = [[Vector(p) for p in q] for q in d["quadros"]]
fps = d["fps"]; n = len(Q)

NOSE, L_SH, R_SH, L_EL, R_EL, L_WR, R_WR = 0, 11, 12, 13, 14, 15, 16
L_IDX, R_IDX, L_HIP, R_HIP, L_KN, R_KN, L_AN, R_AN, L_FT, R_FT = 19, 20, 23, 24, 25, 26, 27, 28, 31, 32

# ---- normalise facing: rotate the whole take so the athlete starts facing -Y (the rig's front)
def facing_yaw(q):
    across = (q[L_HIP] - q[R_HIP]) + (q[L_SH] - q[R_SH])   # points to the athlete's LEFT (+X on the rig)
    return math.atan2(across.y, across.x)
yaw0 = sum(facing_yaw(q) for q in Q[:max(1, n // 6)]) / max(1, n // 6)
if d.get('sem_normalizar'): yaw0 = 0.0          # authored clips are already facing -Y
Rz = Matrix.Rotation(-yaw0, 3, 'Z')
Q = [[Rz @ p for p in q] for q in Q]

arm = bpy.data.objects["Esqueleto"]; sc = bpy.context.scene
for o in bpy.context.selected_objects: o.select_set(False)
arm.select_set(True); bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
for pb in arm.pose.bones: pb.rotation_mode = 'QUATERNION'
mw = arm.matrix_world
rest_hips_h = (mw @ arm.pose.bones["Hips"].head).z
foot_rest = min((mw @ arm.pose.bones["LeftFoot"].head).z, (mw @ arm.pose.bones["RightFoot"].head).z)

def reset():
    for pb in arm.pose.bones:
        pb.location = (0, 0, 0); pb.rotation_quaternion = (1, 0, 0, 0)
    bpy.context.view_layer.update()

def rotate_bone_world(name, qw):
    """Apply a world-space rotation to a pose bone around its head."""
    pb = arm.pose.bones[name]; head = mw @ pb.head
    M = Matrix.Translation(head) @ qw.to_matrix().to_4x4() @ Matrix.Translation(-head)
    new = mw.inverted() @ M @ mw @ pb.matrix
    loc, rot, _ = new.decompose()                       # keep it rigid: rotation + translation only
    pb.matrix = Matrix.LocRotScale(loc, rot, Vector((1, 1, 1)))
    pb.scale = (1, 1, 1)
    bpy.context.view_layer.update()

def aim(name, direction):
    pb = arm.pose.bones[name]
    cur = (mw.to_3x3() @ (pb.tail - pb.head)).normalized()
    if direction.length < 1e-6: return
    rotate_bone_world(name, cur.rotation_difference(direction.normalized()))

def frame_rot(x_cur, up_cur, x_ref, up_ref):
    """Rotation taking the reference frame (x_ref, up_ref) onto the current one (orthonormalised)."""
    def basis(x, up):
        z = up.normalized(); x = (x - z * x.dot(z)).normalized(); y = z.cross(x)
        return Matrix((x, y, z)).transposed()
    return (basis(x_cur, up_cur) @ basis(x_ref, up_ref).inverted()).to_quaternion()

X, UP = Vector((1, 0, 0)), Vector((0, 0, 1))
first = Q[0]
for f, q in enumerate(Q):
    reset()
    hip_mid = (q[L_HIP] + q[R_HIP]) / 2; sh_mid = (q[L_SH] + q[R_SH]) / 2
    # pelvis + trunk orientation
    q_hips = frame_rot(q[L_HIP] - q[R_HIP], sh_mid - hip_mid, X, UP)
    rotate_bone_world("Hips", q_hips)
    q_chest = frame_rot(q[L_SH] - q[R_SH], sh_mid - hip_mid, X, UP)
    rotate_bone_world("Spine", Quaternion().slerp(q_chest @ q_hips.inverted(), 0.5))
    rotate_bone_world("Chest", Quaternion().slerp(q_chest @ q_hips.inverted(), 0.5))
    aim("Neck", (q[NOSE] - sh_mid) * 0.35 + (sh_mid - hip_mid).normalized() * 0.65)
    # limbs: aim each segment along the captured bone
    for side, SH, EL, WR, IDX, HIP, KN, AN, FT in (("Left", L_SH, L_EL, L_WR, L_IDX, L_HIP, L_KN, L_AN, L_FT),
                                                    ("Right", R_SH, R_EL, R_WR, R_IDX, R_HIP, R_KN, R_AN, R_FT)):
        aim(side + "UpperArm", q[EL] - q[SH]); aim(side + "LowerArm", q[WR] - q[EL]); aim(side + "Hand", q[IDX] - q[WR])
        aim(side + "UpperLeg", q[KN] - q[HIP]); aim(side + "LowerLeg", q[AN] - q[KN]); aim(side + "Foot", q[FT] - q[AN])
    # vertical: keep the lowest foot on the ground (crouch, split-step and jumps come from the capture)
    low = min(q[L_FT].z, q[R_FT].z, q[L_AN].z, q[R_AN].z)
    hip_above_foot = hip_mid.z - low
    rest_above = rest_hips_h - foot_rest
    if f == 0: person_rest = d.get("altura_quadril_em_pe") or max(0.3, hip_above_foot)   # video: athlete starts standing
    if d.get("escala_altura"): dz = (hip_above_foot - person_rest) * d["escala_altura"]
    else: dz = (hip_above_foot / person_rest - 1.0) * rest_above      # crouch scaled to the rig's size
    if d.get("elevacao"): dz += d["elevacao"][f]                      # authored jumps
    pbh = arm.pose.bones["Hips"]
    pbh.matrix = mw.inverted() @ Matrix.Translation((0, 0, dz)) @ mw @ pbh.matrix
    bpy.context.view_layer.update()
    for pb in arm.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=f + 1)
        if pb.name == "Hips": pb.keyframe_insert("location", frame=f + 1)
sc.frame_start, sc.frame_end = 1, n
sc.render.fps = int(round(fps))
bpy.ops.object.mode_set(mode='OBJECT')
arm.animation_data.action.name = d.get("golpe", "golpe")

# sanity check: the skinned body must stay human-sized on every frame
body_chk = bpy.data.objects.get("Jogador")
if body_chk:
    worst = 0
    for fr in range(1, n + 1, max(1, n // 12)):
        sc.frame_set(fr)
        dg = bpy.context.evaluated_depsgraph_get(); ev = body_chk.evaluated_get(dg); me = ev.to_mesh()
        cs = [v.co for v in me.vertices]
        ext = max(max(c[i] for c in cs) - min(c[i] for c in cs) for i in range(3))
        worst = max(worst, ext); ev.to_mesh_clear()
        print("CHK", fr, round(ext, 2), "hips", tuple(round(x, 2) for x in arm.pose.bones["Hips"].matrix.translation))
    print("TAMANHO_MAX", round(worst, 2))

if PREVIA:
    body = bpy.data.objects.get("Jogador")
    sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.color_type = 'TEXTURE'
    sc.render.resolution_x = 320; sc.render.resolution_y = 400
    cam = bpy.data.objects.new("c", bpy.data.cameras.new("c")); sc.collection.objects.link(cam); sc.camera = cam
    cam.location = (3.2, -3.4, 1.3); cam.rotation_euler = (math.radians(85), 0, math.radians(43))
    import os
    fr = [1, n // 4, n // 2, int(d["contato"] * fps) + 1, (3 * n) // 4, n]
    paths = []
    for i, k in enumerate(fr):
        sc.frame_set(max(1, min(n, k))); p = PREVIA.replace(".png", f"_{i}.png"); sc.render.filepath = p
        bpy.ops.render.render(write_still=True); paths.append(p)
    print("PREVIA", ";".join(paths))

for o in bpy.context.selected_objects: o.select_set(False)
arm.select_set(True); bpy.context.view_layer.objects.active = arm
bpy.ops.export_scene.fbx(filepath=SAIDA, use_selection=True, apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
                         add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
                         bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0, object_types={'ARMATURE'})
print("FBX", SAIDA, "quadros", n)
