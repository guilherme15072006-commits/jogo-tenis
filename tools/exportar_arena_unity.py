"""(roda no Blender) Exporta a arena (estadio_aaa.blend) para o Unity, leve o bastante para rodar em tempo real.
blender -b estadio_aaa/estadio_aaa.blend -P tools/exportar_arena_unity.py -- SAIDA.fbx

- Remove a quadra/linhas/rede do Blender (o jogo tem a sua própria quadra).
- 23 mil assentos viram UMA malha por lado com um assento simplificado (24 triângulos em vez de 1.136).
- Letreiros viram malha; materiais viram cores simples (o Unity não lê os nós do Blender).
"""
import bpy, sys, bmesh, math
from mathutils import Vector, Matrix

OUT = sys.argv[sys.argv.index("--") + 1]
sc = bpy.context.scene
APAGAR = ("Piso_Saibro", "Linha_", "Rede", "Fita_Rede", "Cinta_Central", "Poste", "Cylinder", "Marcas_Bola", "Camera", "Sol")
for o in list(bpy.data.objects):
    if o.name.startswith(APAGAR): bpy.data.objects.remove(o, do_unlink=True)

# ---------------------------------------------------------------- seats: one merged low-poly mesh per side
def seat_boxes():
    """Simplified tip-up seat (pan + back + beam), facing +Y in its local frame like the original."""
    out = []
    def box(c, s):
        hx, hy, hz = s[0] / 2, s[1] / 2, s[2] / 2
        return [Vector((c[0] + x, c[1] + y, c[2] + z)) for x in (-hx, hx) for y in (-hy, hy) for z in (-hz, hz)]
    out.append(box((0, -0.10, 0.68), (0.44, 0.05, 0.42)))   # back
    out.append(box((0, -0.03, 0.56), (0.42, 0.04, 0.36)))   # folded pan
    out.append(box((0, -0.17, 0.25), (0.48, 0.05, 0.08)))   # beam
    return out
FACES = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))
total = 0
for k in range(4):
    par = bpy.data.objects.get(f"Assentos_{k}")
    if par is None: continue
    mw = par.matrix_world
    verts, faces = [], []
    boxes = seat_boxes()
    for v in par.data.vertices:
        M = mw @ Matrix.Translation(v.co)
        for b in boxes:
            i0 = len(verts); verts += [tuple(M @ p) for p in b]
            faces += [tuple(i0 + i for i in f) for f in FACES]
    me = bpy.data.meshes.new(f"Assentos_Lado_{k}"); me.from_pydata(verts, [], faces); me.update()
    o = bpy.data.objects.new(f"Assentos_Lado_{k}", me); sc.collection.objects.link(o)
    o["cor"] = "assento"; total += len(par.data.vertices)
    child = bpy.data.objects.get(f"Assento_{k}")
    if child: bpy.data.objects.remove(child, do_unlink=True)
    bpy.data.objects.remove(par, do_unlink=True)
print("ASSENTOS", total)

# ---------------------------------------------------------------- text → mesh
for o in sc.objects: o.select_set(o.type == 'FONT')
if any(o.type == 'FONT' for o in sc.objects):
    bpy.context.view_layer.objects.active = next(o for o in sc.objects if o.type == 'FONT')
    bpy.ops.object.convert(target='MESH')

# ---------------------------------------------------------------- simple materials (Unity reads base colour / emission)
CORES = {
    "assento": ((0.62, 0.48, 0.32), 0.45, None),
    "Concreto": ((0.70, 0.69, 0.66), 0.85, None),
    "Muro_Verde": ((0.02, 0.10, 0.05), 0.5, None),
    "Painel_LED": ((0.01, 0.06, 0.03), 0.3, (0.01, 0.14, 0.07)),
    "Texto_LED": ((0.95, 0.95, 0.95), 0.3, (1.0, 1.0, 1.0)),
    "Texto_Amarelo": ((1.0, 0.8, 0.1), 0.3, (1.0, 0.75, 0.08)),
    "Telao": ((0.01, 0.02, 0.05), 0.2, (0.02, 0.04, 0.12)),
    "Vidro_Camarote": ((0.08, 0.10, 0.12), 0.05, (0.6, 0.48, 0.3)),
    "Vidro_Guarda_Corpo": ((0.70, 0.80, 0.82), 0.05, None),
    "Aco_Pintado": ((0.80, 0.81, 0.82), 0.35, None),
    "Aco_Escuro": ((0.10, 0.10, 0.11), 0.4, None),
    "Membrana_Cobertura": ((0.90, 0.90, 0.88), 0.55, None),
    "Refletor": ((1.0, 1.0, 1.0), 0.2, (1.0, 0.96, 0.9)),
    "Tunel_Escuro": ((0.01, 0.01, 0.01), 0.9, None),
    "Faixa_Degrau": ((0.75, 0.60, 0.08), 0.6, None),
    "Fachada": ((0.22, 0.26, 0.29), 0.2, None),
    "Praca": ((0.42, 0.42, 0.40), 0.85, None),
}
cache = {}
def simple(name):
    if name in cache: return cache[name]
    rgb, rough, emit = CORES[name]
    m = bpy.data.materials.new("U_" + name); m.use_nodes = True; b = m.node_tree.nodes["Principled BSDF"]
    for n in list(m.node_tree.nodes):
        if n.type not in ('BSDF_PRINCIPLED', 'OUTPUT_MATERIAL'): m.node_tree.nodes.remove(n)
    b.inputs["Base Color"].default_value = (*rgb, 1); b.inputs["Roughness"].default_value = rough
    if emit: b.inputs["Emission Color"].default_value = (*emit, 1); b.inputs["Emission Strength"].default_value = 1.0
    cache[name] = m; return m
for o in sc.objects:
    if o.type != 'MESH': continue
    if o.get("cor") == "assento":
        o.data.materials.clear(); o.data.materials.append(simple("assento")); continue
    for i, slot in enumerate(o.material_slots):
        n = slot.material.name.split(".")[0] if slot.material else ""
        key = next((k for k in CORES if n.startswith(k)), None)
        if key: o.material_slots[i].material = simple(key)
    for a in list(o.data.color_attributes): o.data.color_attributes.remove(a)

tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in sc.objects if o.type == 'MESH')
print("TRIANGULOS", tris)
for o in sc.objects: o.select_set(o.type == 'MESH')
bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
                         use_mesh_modifiers=True, object_types={'MESH'}, path_mode='STRIP', bake_anim=False)
print("DONE")
