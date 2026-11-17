"""Gera as animações de tênis por poses-chave (sem vídeo) e instala no jogo.

Cada golpe é descrito por poucas poses-chave (posição da bacia, giro de quadril e de ombros, inclinação,
alvo das mãos, posição dos pés). Um esqueleto simples com IK (cotovelos e joelhos) é interpolado com curva
suave e sai no MESMO formato da captura por vídeo, então reaproveita pose_para_fbx.py para aplicar no jogador.

uso:  python tools/gerar_animacoes.py [golpe ...]      (sem argumentos gera todos)
Atleta destro, de frente para -Y; direita dele = -X; cima = Z. Mãos em coordenadas do tronco
(x = esquerda, y = trás, z = cima, origem no meio dos ombros).
"""
import os, sys, json, math, subprocess, shutil
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import processar_videos as pv

FPS = 30
HIP_W, SH_W, SPINE = 0.10, 0.19, 0.50
UPPER, FORE, THIGH, SHIN = 0.29, 0.27, 0.43, 0.43
ANKLE_Z, TOE_Z, FOOT_L = 0.08, 0.02, 0.17
STAND = 0.95 - TOE_Z                      # hip height above the lowest foot point when standing

def rz(a): c, s = math.cos(a), math.sin(a); return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1.0]])
def rx(a): c, s = math.cos(a), math.sin(a); return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
def ry(a): c, s = math.cos(a), math.sin(a); return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
def unit(v): n = np.linalg.norm(v); return v / n if n > 1e-9 else v

def ik(root, target, l1, l2, pole):
    d = target - root; dist = min(np.linalg.norm(d), (l1 + l2) * 0.999); dn = unit(d)
    a = (l1 * l1 - l2 * l2 + dist * dist) / (2 * dist); h = math.sqrt(max(0.0, l1 * l1 - a * a))
    p = unit(pole - dn * pole.dot(dn))
    return root + dn * a + p * h, root + dn * dist

KEYS = ["pel", "yp", "yc", "lf", "ls", "rh", "lh", "lfoot", "rfoot", "elev", "rd", "ld"]
READY = dict(pel=(0, 0.02, 0.80), yp=0, yc=0, lf=20, ls=0, rh=(-0.06, -0.42, -0.24), lh=(0.06, -0.42, -0.22),
             lfoot=(0.31, 0.02, 0), rfoot=(-0.31, 0.02, 0), elev=0,
             rd=(0.25, -0.75, 0.6), ld=(-0.2, -0.6, 0.6))   # rd/ld = direção da raquete/mão no tronco (0 = segue o antebraço)

def vec(p):
    out = []
    for k in KEYS:
        v = p[k]; out += list(v) if isinstance(v, (tuple, list)) else [v]
    return np.array(out, float)

def unvec(v):
    p, i = {}, 0
    for k in KEYS:
        n = 3 if isinstance(READY[k], tuple) else 1
        p[k] = v[i:i + n] if n == 3 else float(v[i]); i += n
    return p

def landmarks(p):
    L = np.zeros((33, 3))
    pel = np.array(p["pel"], float); yp, yc = math.radians(p["yp"]), math.radians(p["yc"])
    axp = rz(yp) @ np.array([1.0, 0, 0])
    lhip, rhip = pel + axp * HIP_W, pel - axp * HIP_W
    Rc = rz(yc) @ rx(math.radians(p["lf"])) @ ry(math.radians(p["ls"]))
    shm = pel + Rc @ np.array([0, 0, SPINE])
    lsh, rsh = shm + Rc @ np.array([SH_W, 0, 0]), shm - Rc @ np.array([SH_W, 0, 0])
    L[0] = shm + Rc @ np.array([0, -0.08, 0.22])
    for side, sh, key, dk, pole, (S, E, W, I) in (("r", rsh, "rh", "rd", (-0.5, 0.5, -0.8), (12, 14, 16, 20)),
                                                  ("l", lsh, "lh", "ld", (0.5, 0.5, -0.8), (11, 13, 15, 19))):
        el, wr = ik(sh, shm + Rc @ np.array(p[key], float), UPPER, FORE, Rc @ np.array(pole))
        hd = np.array(p[dk], float)
        hdir = unit(Rc @ hd) if np.linalg.norm(hd) > 0.1 else unit(wr - el)
        L[S], L[E], L[W], L[I] = sh, el, wr, wr + hdir * 0.09
    fwd = rz(yp) @ np.array([0, -1.0, 0])
    for hip, key, out, (H, K, A, F) in ((lhip, "lfoot", 0.25, (23, 25, 27, 31)), (rhip, "rfoot", -0.25, (24, 26, 28, 32))):
        f = np.array(p[key], float); ank = np.array([f[0], f[1], ANKLE_Z + f[2]])
        kn, ank = ik(hip, ank, THIGH, SHIN, fwd + axp * out)
        toe = ank + fwd * FOOT_L; toe[2] = TOE_Z + f[2] * 0.6
        L[H], L[K], L[A], L[F] = hip, kn, ank, toe
    return L

def animar(chaves, loop=False):
    """chaves: [(t, {overrides sobre READY})] → (quadros[n,33,3], elev[n])"""
    ts = np.array([t for t, _ in chaves]); P = np.array([vec({**READY, **o}) for _, o in chaves])
    m = np.zeros_like(P)
    for i in range(len(ts)):
        a, b = max(0, i - 1), min(len(ts) - 1, i + 1)
        if loop and i in (0, len(ts) - 1):       # periodic tangent so the loop has no hitch
            m[i] = (P[1] - P[-2]) / ((ts[1] - ts[0]) + (ts[-1] - ts[-2]))
        else:
            m[i] = (P[b] - P[a]) / (ts[b] - ts[a]) if b > a else 0
    n = int(round(ts[-1] * FPS)) + 1; Q, E = [], []
    for f in range(n):
        t = min(f / FPS, ts[-1]); i = min(len(ts) - 2, max(0, int(np.searchsorted(ts, t, side="right")) - 1))
        h = ts[i + 1] - ts[i]; u = (t - ts[i]) / h
        v = (2*u**3 - 3*u**2 + 1) * P[i] + (u**3 - 2*u**2 + u) * h * m[i] + (-2*u**3 + 3*u**2) * P[i + 1] + (u**3 - u**2) * h * m[i + 1]
        p = unvec(v); Q.append(landmarks(p)); E.append(p["elev"])
    return np.array(Q), np.array(E)

# ============================================================== golpes (t em segundos)
CLIPES = {}
CLIPES["espera"] = dict(loop=True, chaves=[
    (0.00, dict()),
    (0.17, dict(pel=(0.015, 0.02, 0.825), elev=0.025, lf=18)),
    (0.34, dict(pel=(0.0, 0.02, 0.79), lf=21)),
    (0.51, dict(pel=(-0.015, 0.02, 0.825), elev=0.025, lf=18)),
    (0.68, dict())])
CLIPES["corrida"] = dict(loop=True, chaves=[      # passo lateral (side-step) amplo e rápido, corpo baixo
    (0.00, dict(pel=(0, 0.02, 0.78), lfoot=(0.40, 0.02, 0), rfoot=(-0.40, 0.02, 0), lf=22)),
    (0.11, dict(pel=(0, 0.02, 0.84), lfoot=(0.30, 0.02, 0.06), rfoot=(-0.18, 0.02, 0.08), elev=0.06, lf=20)),
    (0.22, dict(pel=(0, 0.02, 0.81), lfoot=(0.16, 0.02, 0.02), rfoot=(-0.16, 0.02, 0.02), elev=0.02, lf=21)),
    (0.33, dict(pel=(0, 0.02, 0.84), lfoot=(0.18, 0.02, 0.08), rfoot=(-0.30, 0.02, 0.06), elev=0.06, lf=20)),
    (0.44, dict(pel=(0, 0.02, 0.78), lfoot=(0.40, 0.02, 0), rfoot=(-0.40, 0.02, 0), lf=22))])
CLIPES["corrida_frente"] = dict(loop=True, chaves=[   # corrida de frente: joelho alto, braços alternados, fase de voo
    (0.00, dict(pel=(0, 0, 0.86), lf=14, yp=6, yc=-6, lfoot=(0.13, -0.40, 0), rfoot=(-0.13, 0.30, 0.18),
                rh=(-0.20, -0.30, -0.30), lh=(0.20, 0.15, -0.40), rd=(0.1, -0.6, 0.8), ld=(0, 0, 0))),
    (0.16, dict(pel=(0, 0, 0.92), lf=16, elev=0.06, lfoot=(0.13, 0.10, 0.04), rfoot=(-0.13, -0.10, 0.30),
                rh=(-0.20, -0.10, -0.32), lh=(0.20, -0.10, -0.34), rd=(0.1, -0.6, 0.8), ld=(0, 0, 0))),
    (0.33, dict(pel=(0, 0, 0.86), lf=14, yp=-6, yc=6, lfoot=(0.13, 0.30, 0.18), rfoot=(-0.13, -0.40, 0),
                rh=(-0.20, 0.15, -0.40), lh=(0.20, -0.30, -0.30), rd=(0.1, 0.4, 0.9), ld=(0, 0, 0))),
    (0.49, dict(pel=(0, 0, 0.92), lf=16, elev=0.06, lfoot=(0.13, -0.10, 0.30), rfoot=(-0.13, 0.10, 0.04),
                rh=(-0.20, -0.10, -0.34), lh=(0.20, -0.10, -0.32), rd=(0.1, -0.6, 0.8), ld=(0, 0, 0))),
    (0.66, dict(pel=(0, 0, 0.86), lf=14, yp=6, yc=-6, lfoot=(0.13, -0.40, 0), rfoot=(-0.13, 0.30, 0.18),
                rh=(-0.20, -0.30, -0.30), lh=(0.20, 0.15, -0.40), rd=(0.1, -0.6, 0.8), ld=(0, 0, 0)))])
CLIPES["corrida_tras"] = dict(loop=True, chaves=[     # recuo: passos curtos para trás, tronco à frente
    (0.00, dict(pel=(0, 0.04, 0.80), lf=22, lfoot=(0.22, -0.18, 0), rfoot=(-0.22, 0.22, 0.06), elev=0.0)),
    (0.14, dict(pel=(0, 0.04, 0.84), lf=20, lfoot=(0.22, 0.05, 0.08), rfoot=(-0.22, 0.05, 0.02), elev=0.04)),
    (0.28, dict(pel=(0, 0.04, 0.80), lf=22, lfoot=(0.22, 0.22, 0.06), rfoot=(-0.22, -0.18, 0), elev=0.0)),
    (0.42, dict(pel=(0, 0.04, 0.84), lf=20, lfoot=(0.22, 0.05, 0.02), rfoot=(-0.22, 0.05, 0.08), elev=0.04)),
    (0.56, dict(pel=(0, 0.04, 0.80), lf=22, lfoot=(0.22, -0.18, 0), rfoot=(-0.22, 0.22, 0.06), elev=0.0))])
CLIPES["parada"] = dict(chaves=[      # freada: pé da frente planta, quadril abaixa, tronco para trás, braços equilibram
    (0.00, dict(pel=(0, 0, 0.86), lf=16, lfoot=(0.13, -0.30, 0), rfoot=(-0.13, 0.25, 0.10))),
    (0.12, dict(pel=(0, 0.06, 0.74), lf=0, ls=0, lfoot=(0.20, -0.48, 0), rfoot=(-0.24, 0.30, 0),
                rh=(-0.30, -0.30, -0.10), lh=(0.32, -0.25, -0.08), ld=(0.6, -0.4, 0.3))),
    (0.24, dict(pel=(0, 0.04, 0.76), lf=8, lfoot=(0.22, -0.40, 0), rfoot=(-0.26, 0.20, 0))),
    (0.45, dict())])
CLIPES["split"] = dict(chaves=[
    (0.0, {}), (0.15, dict(pel=(0, 0, 0.92), lfoot=(0.22, 0, 0.06), rfoot=(-0.22, 0, 0.06), elev=0.07)),
    (0.30, dict(pel=(0, 0, 0.79), lfoot=(0.37, 0, 0), rfoot=(-0.37, 0, 0), lf=18)),
    (0.42, dict(pel=(0, 0, 0.84), lfoot=(0.34, 0, 0), rfoot=(-0.34, 0, 0), lf=14))])
CLIPES["forehand"] = dict(contato=0.66, chaves=[
    (0.00, {}),
    (0.30, dict(pel=(-0.05, 0.03, 0.85), yp=-35, yc=-70, rh=(-0.45, 0.15, -0.05), rd=(-0.4, 0.1, 0.9), lh=(-0.05, -0.42, -0.08), rfoot=(-0.30, 0.10, 0), lfoot=(0.27, -0.05, 0))),
    (0.52, dict(pel=(-0.05, 0.02, 0.82), yp=-30, yc=-55, lf=16, rh=(-0.50, 0.05, -0.50), rd=(-0.8, 0.3, -0.5), lh=(0.10, -0.40, -0.12), rfoot=(-0.30, 0.10, 0), lfoot=(0.27, -0.05, 0))),
    (0.66, dict(pel=(0.02, -0.06, 0.90), yp=10, yc=5, lf=8, rh=(-0.42, -0.50, -0.32), rd=(-0.9, -0.4, 0.1), lh=(0.22, -0.15, -0.30), rfoot=(-0.30, 0.10, 0), lfoot=(0.27, -0.05, 0))),
    (0.90, dict(pel=(0.05, -0.08, 0.92), yp=35, yc=70, lf=6, rh=(0.30, -0.25, 0.05), rd=(0.8, 0.3, 0.5), lh=(0.20, -0.20, -0.15), rfoot=(-0.22, 0.12, 0.05), lfoot=(0.27, -0.05, 0))),
    (1.30, {})])
CLIPES["backhand2"] = dict(contato=0.66, chaves=[
    (0.00, {}),
    (0.30, dict(pel=(0.05, 0.02, 0.85), yp=35, yc=75, rh=(0.26, -0.10, -0.25), lh=(0.32, -0.02, -0.24), rd=(0.9, 0.1, 0.3), ld=(0.9, 0.1, 0.3), rfoot=(-0.10, -0.25, 0), lfoot=(0.30, 0.10, 0))),
    (0.52, dict(pel=(0.05, 0.0, 0.82), yp=30, yc=60, lf=16, rh=(0.24, -0.08, -0.48), lh=(0.30, 0.0, -0.47), rd=(0.8, 0.1, -0.5), ld=(0.8, 0.1, -0.5), rfoot=(-0.10, -0.25, 0), lfoot=(0.30, 0.10, 0))),
    (0.66, dict(pel=(0.0, -0.06, 0.90), yp=-10, yc=-5, lf=8, rh=(0.18, -0.48, -0.32), lh=(0.26, -0.42, -0.30), rd=(0.9, -0.3, 0.1), ld=(0.9, -0.3, 0.1), rfoot=(-0.10, -0.25, 0), lfoot=(0.30, 0.10, 0))),
    (0.90, dict(pel=(-0.04, -0.08, 0.92), yp=-35, yc=-70, lf=6, rh=(-0.28, -0.18, 0.08), lh=(-0.20, -0.24, 0.08), rd=(-0.6, 0.2, 0.7), ld=(-0.6, 0.2, 0.7), rfoot=(-0.10, -0.25, 0), lfoot=(0.26, 0.12, 0.05))),
    (1.30, {})])
CLIPES["voleio_fh"] = dict(contato=0.30, chaves=[
    (0.00, dict(rh=(-0.10, -0.36, -0.15), lh=(0.06, -0.36, -0.14))),
    (0.15, dict(yc=-30, yp=-10, rh=(-0.38, -0.20, 0.0), rd=(-0.4, -0.1, 0.9), lh=(0.10, -0.38, -0.10))),
    (0.30, dict(pel=(0, -0.12, 0.84), yc=-10, rh=(-0.35, -0.48, -0.08), rd=(-0.5, -0.3, 0.8), lh=(0.22, -0.20, -0.20), lfoot=(0.15, -0.35, 0))),
    (0.50, dict(pel=(0, -0.10, 0.84), yc=5, rh=(-0.20, -0.50, -0.25), rd=(-0.5, -0.6, 0.5), lh=(0.20, -0.25, -0.22), lfoot=(0.15, -0.35, 0))),
    (0.80, {})])
CLIPES["voleio_bh"] = dict(contato=0.30, chaves=[
    (0.00, dict(rh=(-0.10, -0.36, -0.15), lh=(0.06, -0.36, -0.14))),
    (0.15, dict(yc=35, yp=10, rh=(0.30, -0.18, 0.0), rd=(0.5, -0.1, 0.85), lh=(0.20, -0.20, -0.05))),
    (0.30, dict(pel=(0, -0.12, 0.84), yc=15, rh=(0.30, -0.42, -0.05), rd=(0.5, -0.3, 0.8), lh=(0.25, 0.05, -0.20), rfoot=(-0.10, -0.38, 0))),
    (0.50, dict(pel=(0, -0.10, 0.84), yc=0, rh=(-0.10, -0.50, -0.25), rd=(0.3, -0.6, 0.6), lh=(0.22, -0.05, -0.25), rfoot=(-0.10, -0.38, 0))),
    (0.80, {})])
CLIPES["smash"] = dict(contato=0.90, chaves=[
    (0.00, {}),
    (0.35, dict(pel=(0, 0.03, 0.85), yp=-55, yc=-75, lf=0, rh=(-0.30, 0.15, 0.10), rd=(-0.2, 0.2, 0.95), lh=(0.10, -0.25, 0.50), rfoot=(-0.20, 0.22, 0), lfoot=(0.15, -0.15, 0))),
    (0.70, dict(pel=(0, 0.05, 0.80), yp=-55, yc=-70, lf=-8, rh=(-0.12, 0.18, 0.15), rd=(0, 0.5, -0.85), lh=(0.12, -0.20, 0.56), rfoot=(-0.20, 0.22, 0), lfoot=(0.15, -0.15, 0))),
    (0.90, dict(pel=(0, -0.03, 0.95), yp=-10, yc=0, lf=4, rh=(-0.15, -0.20, 0.56), rd=(0, -0.25, 0.97), lh=(0.20, -0.10, -0.20), rfoot=(-0.18, 0.15, 0.10), lfoot=(0.15, -0.12, 0.08), elev=0.18)),
    (1.10, dict(pel=(0, -0.08, 0.86), yp=25, yc=45, lf=30, rh=(0.25, -0.35, -0.35), rd=(0.4, -0.5, -0.75), lh=(0.22, -0.05, -0.25), rfoot=(-0.15, -0.20, 0.03), lfoot=(0.18, -0.05, 0), elev=0.04)),
    (1.50, {})])


def gerar(nome):
    c = CLIPES[nome]
    Q, E = animar(c["chaves"], c.get("loop", False))
    os.makedirs(pv.TMP, exist_ok=True); os.makedirs(pv.PREVIAS, exist_ok=True); os.makedirs(pv.ANIM, exist_ok=True)
    pose = os.path.join(pv.TMP, f"{nome}.json")
    contato = c.get("contato", 0.0)
    with open(pose, "w", encoding="utf-8") as f:
        json.dump({"golpe": nome, "fps": FPS, "mao": "direita", "contato": contato, "quadros": Q.round(5).tolist(),
                   "sem_normalizar": True, "altura_quadril_em_pe": STAND, "escala_altura": 1.03, "elevacao": E.round(4).tolist()}, f)
    fbx = os.path.join(pv.TMP, f"{nome}.fbx")
    if os.path.exists(fbx): os.remove(fbx)
    prev = os.path.join(pv.PREVIAS, f"{nome}_tmp.png")
    r = subprocess.run([pv.BLENDER, "-b", pv.RIG, "-P", os.path.join(pv.FERR, "pose_para_fbx.py"), "--", pose, fbx, prev],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if not os.path.exists(fbx):
        print(r.stdout[-2500:], r.stderr[-1500:]); raise RuntimeError(f"falha no Blender ({nome})")
    tam = next((l for l in r.stdout.splitlines() if l.startswith("TAMANHO_MAX")), "")
    partes = next((l.split(" ", 1)[1].split(";") for l in r.stdout.splitlines() if l.startswith("PREVIA")), [])
    shutil.copy2(fbx, os.path.join(pv.ANIM, f"{nome}.fbx"))
    if "contato" in c: pv.atualizar_contato(nome, contato)
    pv.montar_previa(nome, partes)
    print(f"  ✓ {nome}: {len(Q)} quadros ({len(Q) / FPS:.2f}s) {tam}")


def capturados():
    """Golpes que já têm captura real (vídeo ou FBX em videos_golpes): nunca são sobrescritos pelo gerador."""
    out = set()
    for f in os.listdir(pv.VIDEOS):
        ext = os.path.splitext(f)[1].lower()
        if ext in pv.EXT_VIDEO + (".fbx",):
            g = pv.golpe_do_nome(f)
            if g: out.add(g)
    return out


if __name__ == "__main__":
    pular = capturados()
    for nome in (sys.argv[1:] or list(CLIPES)):
        if nome in pular and not sys.argv[1:]:
            print(f"  · {nome}: mantido (veio de captura real)"); continue
        gerar(nome)
