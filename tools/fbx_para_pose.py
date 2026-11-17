"""(roda no Blender) Animação FBX de outro esqueleto (DeepMotion, QuickMagic, Rokoko, Mixamo, 3ds Max Biped...)
→ posições das articulações por quadro, no mesmo formato da pose por vídeo (33 pontos estilo MediaPipe).
blender -b -P fbx_para_pose.py -- ANIM.fbx SAIDA.json
"""
import bpy, sys, json
from mathutils import Vector

a = sys.argv[sys.argv.index("--") + 1:]
FBX, OUT = a[0], a[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=FBX)
sc = bpy.context.scene
arm = max((o for o in sc.objects if o.type == 'ARMATURE'), key=lambda o: len(o.data.bones))
names = [b.name for b in arm.data.bones]

def find(*cands):
    low = {n.lower().replace("_", " ").replace(".", " "): n for n in names}
    for c in cands:
        for k, n in low.items():
            if k.endswith(c) or k == c: return n
    return None

# name patterns for the common rigs: Biped (Bip001 L UpperArm), Mixamo (mixamorig:LeftArm), generic (upperarm l / LeftUpperArm)
B = {}
for s, S in (("l", "left"), ("r", "right")):
    B[s + "_sh"] = find(f"{s} upperarm", f"{S}arm", f"{S}upperarm", f"upperarm {s}", f"upper arm {s}")
    B[s + "_el"] = find(f"{s} forearm", f"{S}forearm", f"{S}lowerarm", f"lowerarm {s}", f"forearm {s}")
    B[s + "_wr"] = find(f"{s} hand", f"{S}hand", f"hand {s}")
    B[s + "_hip"] = find(f"{s} thigh", f"{S}upleg", f"{S}upperleg", f"thigh {s}", f"upperleg {s}")
    B[s + "_kn"] = find(f"{s} calf", f"{S}leg", f"{S}lowerleg", f"calf {s}", f"lowerleg {s}", f"shin {s}")
    B[s + "_an"] = find(f"{s} foot", f"{S}foot", f"foot {s}")
    B[s + "_toe"] = find(f"{s} toe0", f"{S}toebase", f"{S}toes", f"toe {s}", f"ball {s}")
    B[s + "_idx"] = find(f"{s} finger2", f"{S}handmiddle1", f"{S}handindex1", f"{s} finger1")
B["head"] = find(" head", "head")
missing = [k for k, v in B.items() if v is None and not k.endswith(("toe", "idx"))]
if missing:
    print("ERRO esqueleto não reconhecido, faltam:", missing, "ossos:", names[:40]); sys.exit(2)

act = arm.animation_data.action
f0, f1 = int(act.frame_range[0]), int(act.frame_range[1])
fps = sc.render.fps / sc.render.fps_base
mw = arm.matrix_world
def head(n): return mw @ arm.pose.bones[n].head
def tail(n): return mw @ arm.pose.bones[n].tail

quadros = []
for f in range(f0, f1 + 1):
    sc.frame_set(f)
    L = [[0.0, 0.0, 0.0] for _ in range(33)]
    def put(i, v): L[i] = [round(v.x, 5), round(v.y, 5), round(v.z, 5)]
    for s, off in (("l", 1), ("r", 0)):          # MediaPipe: odd = left, even = right
        put(11 + (1 - off), head(B[s + "_sh"])); put(13 + (1 - off), head(B[s + "_el"])); put(15 + (1 - off), head(B[s + "_wr"]))
        put(19 + (1 - off), head(B[s + "_idx"]) if B[s + "_idx"] else tail(B[s + "_wr"]))
        put(23 + (1 - off), head(B[s + "_hip"])); put(25 + (1 - off), head(B[s + "_kn"])); put(27 + (1 - off), head(B[s + "_an"]))
        put(31 + (1 - off), tail(B[s + "_toe"]) if B[s + "_toe"] else tail(B[s + "_an"]))
    hd = head(B["head"]); put(0, hd + (tail(B["head"]) - hd) * 0.4)
    quadros.append(L)

# drop leading bind/T-pose frames (arms straight out at shoulder height, hands far from the hips)
def is_tpose(q):
    l, r = Vector(q[15]), Vector(q[16]); ls, rs = Vector(q[11]), Vector(q[12])
    return abs(l.z - ls.z) < 0.08 and abs(r.z - rs.z) < 0.08 and (l - r).length > 1.2
k = 0
while k < len(quadros) - 1 and is_tpose(quadros[k]): k += 1
quadros = quadros[k:]
print("POSE", len(quadros), "quadros a", fps, "fps (pulados", k, "de pose T)")
json.dump({"fps": fps, "quadros": quadros, "origem": "fbx"}, open(OUT, "w", encoding="utf-8"))
