"""Etapa 1+2+3 da automação: vídeo → pose 3D por quadro (MediaPipe), suavizada, com mão dominante,
quadro do contato e recorte detectados automaticamente.

uso:  python video_para_pose.py VIDEO.mp4 SAIDA.json [--golpe forehand]
saída JSON: fps, quadros [[x,y,z]*33] em metros (eixos do Blender: X = esquerda do atleta, Y = frente(-), Z = cima),
            contato (s, relativo ao início do recorte), mao ("direita"/"esquerda"), inicio/fim (quadros do vídeo)
"""
import sys, os, json, math, argparse
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import numpy as np
import cv2
import mediapipe as mp

AQUI = os.path.dirname(os.path.abspath(__file__))
MODELO = os.path.join(AQUI, "modelos", "pose_landmarker_heavy.task")

# MediaPipe landmark ids
NOSE, L_SH, R_SH, L_EL, R_EL, L_WR, R_WR = 0, 11, 12, 13, 14, 15, 16
L_IDX, R_IDX, L_HIP, R_HIP, L_KN, R_KN, L_AN, R_AN, L_FT, R_FT = 19, 20, 23, 24, 25, 26, 27, 28, 31, 32


def extrair(video):
    BaseOptions = mp.tasks.BaseOptions
    PL = mp.tasks.vision.PoseLandmarker
    opts = mp.tasks.vision.PoseLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=MODELO),
        running_mode=mp.tasks.vision.RunningMode.VIDEO,
        num_poses=1, min_pose_detection_confidence=0.5, min_tracking_confidence=0.5)
    cap = cv2.VideoCapture(video)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    quadros, vis, img2d = [], [], []
    with PL.create_from_options(opts) as det:
        i = 0
        while True:
            ok, frame = cap.read()
            if not ok: break
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            res = det.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), int(i * 1000 / fps))
            if res.pose_world_landmarks:
                w = res.pose_world_landmarks[0]
                # MediaPipe world: x = esquerda do atleta (quando de frente para a câmera), y = baixo, z = profundidade (− = para a câmera)
                quadros.append([[p.x, p.z, -p.y] for p in w])
                vis.append([getattr(p, "visibility", 1.0) or 1.0 for p in w])
                img2d.append([[p.x, p.y] for p in res.pose_landmarks[0]])
            else:
                quadros.append(None); vis.append(None); img2d.append(None)
            i += 1
    cap.release()
    return fps, quadros, vis, img2d


def preencher(quadros):
    """Preenche quadros sem detecção por interpolação linear."""
    n = len(quadros); idx = [i for i, q in enumerate(quadros) if q is not None]
    if not idx: raise SystemExit("Nenhuma pessoa detectada no vídeo.")
    arr = np.zeros((n, 33, 3))
    for i in range(n):
        if quadros[i] is not None: arr[i] = quadros[i]; continue
        a = max([j for j in idx if j < i], default=None); b = min([j for j in idx if j > i], default=None)
        if a is None: arr[i] = quadros[b]
        elif b is None: arr[i] = quadros[a]
        else: t = (i - a) / (b - a); arr[i] = np.array(quadros[a]) * (1 - t) + np.array(quadros[b]) * t
    return arr


def one_euro(x, fps, min_cutoff=1.6, beta=0.35, d_cutoff=1.0):
    """Filtro One-Euro por coordenada: tira o tremido sem atrasar os movimentos rápidos (o golpe)."""
    def alpha(c): te = 1.0 / fps; tau = 1.0 / (2 * math.pi * c); return 1.0 / (1.0 + tau / te)
    y = np.copy(x); dx_prev = np.zeros_like(x[0]); x_prev = x[0]
    for i in range(1, len(x)):
        dx = (x[i] - x_prev) * fps
        a_d = alpha(d_cutoff); dx_hat = a_d * dx + (1 - a_d) * dx_prev
        cutoff = min_cutoff + beta * np.abs(dx_hat)
        a = 1.0 / (1.0 + (1.0 / (2 * math.pi * cutoff)) * fps)
        y[i] = a * x[i] + (1 - a) * y[i - 1]
        x_prev, dx_prev = x[i], dx_hat
    return y


def detectar(arr, fps, golpe, bruto=None):
    """Mão dominante = punho com maior velocidade de pico. Contato = pico de velocidade dessa mão
    (no saque/smash: o pico entre os quadros em que a mão está acima da cabeça)."""
    fonte = arr if bruto is None else bruto       # speeds from the lightly-filtered take (the smoother flattens the swing burst)
    def vel(k):
        p = fonte[:, k]
        v = np.linalg.norm(np.diff(p, axis=0), axis=1) * fps
        return np.concatenate([[0], v])
    # face the athlete towards -Y (like the rig) so "forward" means towards the net
    across = (arr[:max(1, len(arr) // 6), L_HIP] - arr[:max(1, len(arr) // 6), R_HIP]).mean(axis=0)            + (arr[:max(1, len(arr) // 6), L_SH] - arr[:max(1, len(arr) // 6), R_SH]).mean(axis=0)
    yaw = math.atan2(across[1], across[0]); c_, s_ = math.cos(-yaw), math.sin(-yaw)
    rot = np.array([[c_, -s_, 0], [s_, c_, 0], [0, 0, 1]])
    arr = arr @ rot.T
    if bruto is not None: fonte = bruto @ rot.T
    vr, vl = vel(R_WR), vel(L_WR)
    mao = "direita" if vr.max() >= vl.max() else "esquerda"
    wr = R_WR if mao == "direita" else L_WR
    v = vr if mao == "direita" else vl
    acima = arr[:, wr, 2] > arr[:, NOSE, 2]
    if golpe in ("saque", "smash") and acima.any():
        cand = np.where(acima, v, 0)
        c = int(np.argmax(cand))
    else:
        # groundstrokes/volleys: contact = fastest FORWARD motion of the hitting hand (not the recovery)
        fwd = np.concatenate([[0], -np.diff(fonte[:, wr, 1]) * fps])
        # ignore the first/last 0.3 s (tracking jumps when the clip starts/ends) and favour real swing speed
        edge = int(0.3 * fps); mask = np.zeros(len(v)); mask[edge:max(edge + 1, len(v) - edge)] = 1
        sh = R_SH if mao == "direita" else L_SH
        below = (arr[:, wr, 2] < arr[:, sh, 2] - 0.05).astype(float)   # groundstroke contact is below the shoulder
        score = (np.maximum(fwd, 0) * 0.6 + v * 0.4) * mask * np.maximum(below, 0.15)
        c = int(np.argmax(score))
    lento = 1.0
    if golpe == "saque":
        # start of the serve = toss hand at its lowest point before it rises (searched up to 9 s back: slow motion)
        off = L_WR if mao == "direita" else R_WR
        lo = max(0, c - int(9 * fps)); hi = max(lo + 1, c - int(0.25 * fps))
        ini = lo + int(np.argmin(arr[lo:hi, off, 2]))
        # slow-motion: a real serve takes ~1.25 s from the toss to the contact
        lento = max(1.0, ((c - ini) / fps) / 1.25)
        if lento < 1.3: lento = 1.0
        # end = 0.85 s of REAL time after the contact (landing + first recovery step)
        fim = min(len(arr) - 1, c + int(0.85 * lento * fps))
        return mao, c, ini, fim, float(v[c]), lento
    antes = {"smash": 1.1}.get(golpe, 0.9)
    depois = {"smash": 0.8}.get(golpe, 0.75)
    ini = max(0, c - int(antes * fps)); fim = min(len(arr) - 1, c + int(depois * fps))
    return mao, c, ini, fim, float(v[c]), lento


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("video"); ap.add_argument("saida"); ap.add_argument("--golpe", default="")
    ap.add_argument("--de-json", action="store_true", help="entrada já é pose 3D (de um FBX), não vídeo")
    ap.add_argument("--contato-quadro", type=int, default=-1, help="quadro do contato definido à mão (videos_golpes/ajustes.json)")
    a = ap.parse_args()
    golpe = a.golpe or os.path.splitext(os.path.basename(a.video))[0].lower()
    if a.de_json:
        raw = json.load(open(a.video, encoding="utf-8")); fps = raw["fps"]
        arr = np.array(raw["quadros"], float); detect = len(arr); quadros = arr
        print(f"[pose] {len(arr)} quadros a {fps:.1f} fps (animação FBX)")
        # mocap already clean: only a light smoothing
        sm = one_euro(arr.reshape(len(arr), -1), fps, min_cutoff=4.0, beta=0.6).reshape(arr.shape)
    else:
        fps, quadros, vis, img2d = extrair(a.video)
        detect = sum(q is not None for q in quadros)
        print(f"[pose] {len(quadros)} quadros a {fps:.1f} fps, pessoa detectada em {detect}")
        arr = preencher(quadros)
        sm = one_euro(arr.reshape(len(arr), -1), fps).reshape(arr.shape)
    mao, c, ini, fim, vpk, lento = detectar(sm, fps, golpe, bruto=arr)
    if a.contato_quadro >= 0 and golpe != "saque":
        c = min(len(sm) - 1, a.contato_quadro)
        ini = max(0, c - int(0.9 * fps * lento)); fim = min(len(sm) - 1, c + int(0.75 * fps * lento))
        print(f"[pose] contato ajustado à mão no quadro {c}")
    print(f"[pose] mão {mao}, contato no quadro {c} ({c / fps:.2f}s), pico {vpk:.1f} m/s, recorte {ini}-{fim}"
          + (f", câmera lenta detectada {lento:.1f}x → velocidade real" if lento > 1 else ""))
    fps_real = fps * lento
    out = {"golpe": golpe, "fps": fps_real, "lento": lento, "mao": mao, "inicio": ini, "fim": fim, "contato": (c - ini) / fps_real,
           "quadros": sm[ini:fim + 1].round(5).tolist(), "deteccao": detect / max(1, len(quadros))}
    with open(a.saida, "w", encoding="utf-8") as f: json.dump(out, f)


if __name__ == "__main__":
    main()
