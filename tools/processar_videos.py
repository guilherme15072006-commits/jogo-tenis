"""AUTOMAÇÃO: vídeos de golpes → animações do seu jogador dentro do jogo.

Coloque os vídeos em  videos_golpes/  com o nome do golpe (forehand.mp4, backhand2.mp4, saque.mp4 ...)
e rode:   python tools/processar_videos.py          (ou dê dois cliques em PROCESSAR_VIDEOS.bat)

Para cada vídeo novo:
 1. MediaPipe extrai a pose 3D de cada quadro (no seu PC, grátis, sem internet).
 2. Filtro One-Euro tira o tremido; mão dominante, quadro do contato e recorte são detectados sozinhos.
 3. O Blender aplica o movimento no esqueleto do seu jogador e exporta FBX.
 4. O FBX vai para TenisAAA/Assets/Resources/Animacoes/<golpe>.fbx e o contato para contatos.json.
 5. Uma prévia (6 quadros) é salva em videos_golpes/previas/.
Também aceita FBX prontos (DeepMotion/Mixamo): coloque <golpe>.fbx em videos_golpes/ e ele é instalado direto.
"""
import os, sys, json, glob, shutil, subprocess
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VIDEOS = os.path.join(RAIZ, "videos_golpes")
PREVIAS = os.path.join(VIDEOS, "previas")
TMP = os.path.join(VIDEOS, "_trabalho")
ANIM = os.path.join(RAIZ, "TenisAAA", "Assets", "Resources", "Animacoes")
RIG = os.path.join(RAIZ, "jogador_rig", "jogador_tpose.blend")
FERR = os.path.join(RAIZ, "tools")
BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
GOLPES = ["espera", "corrida", "split", "forehand", "backhand2", "backhand1", "slice", "voleio_fh", "voleio_bh", "smash", "saque"]
EXT_VIDEO = (".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm")


def golpe_do_nome(arquivo):
    n = os.path.splitext(os.path.basename(arquivo))[0].lower().replace(" ", "_").replace("-", "_")
    for g in sorted(GOLPES, key=len, reverse=True):
        if n.startswith(g): return g
    return None


def atualizar_contato(golpe, segundos):
    p = os.path.join(ANIM, "contatos.json")
    dados = {"clipes": []}
    if os.path.exists(p):
        with open(p, encoding="utf-8") as f: dados = json.load(f)
    dados["clipes"] = [c for c in dados["clipes"] if c["nome"] != golpe] + [{"nome": golpe, "contato": round(segundos, 3)}]
    with open(p, "w", encoding="utf-8") as f: json.dump(dados, f, indent=2, ensure_ascii=False)


def montar_previa(golpe, partes):
    try:
        from PIL import Image
        ims = [Image.open(p) for p in partes if os.path.exists(p)]
        if not ims: return None
        w, h = ims[0].size; folha = Image.new("RGB", (w * len(ims), h), "black")
        for i, im in enumerate(ims): folha.paste(im.convert("RGB"), (i * w, 0))
        out = os.path.join(PREVIAS, f"{golpe}.png"); folha.save(out)
        for p in partes:
            try: os.remove(p)
            except OSError: pass
        return out
    except Exception as e:
        print("  (prévia não montada:", e, ")"); return None


def processar_video(video, golpe, de_fbx=False):
    os.makedirs(TMP, exist_ok=True); os.makedirs(PREVIAS, exist_ok=True)
    pose = os.path.join(TMP, f"{golpe}.json")
    entrada, extra = video, []
    if de_fbx:   # animação pronta em outro esqueleto → articulações → mesmo caminho do vídeo
        bruto = os.path.join(TMP, f"{golpe}_bruto.json")
        r = subprocess.run([BLENDER, "-b", "-P", os.path.join(FERR, "fbx_para_pose.py"), "--", video, bruto],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        msg = [l for l in r.stdout.splitlines() if l.startswith(("POSE", "ERRO"))]
        print("  " + " | ".join(msg))
        if not os.path.exists(bruto): raise RuntimeError("esqueleto do FBX não reconhecido")
        entrada, extra = bruto, ["--de-json"]
    aj_p = os.path.join(VIDEOS, "ajustes.json")
    aj = json.load(open(aj_p, encoding="utf-8")).get(os.path.basename(video), {}) if os.path.exists(aj_p) else {}
    if "contato_quadro" in aj: extra += ["--contato-quadro", str(aj["contato_quadro"])]
    r = subprocess.run([sys.executable, os.path.join(FERR, "video_para_pose.py"), entrada, pose, "--golpe", golpe] + extra)
    if r.returncode != 0: raise RuntimeError("falha na extração da pose")
    with open(pose, encoding="utf-8") as f: info = json.load(f)
    if info.get("deteccao", 1) < 0.6:
        print(f"  ⚠ pessoa detectada só em {info['deteccao']:.0%} dos quadros — grave de corpo inteiro e com boa luz")
    fbx = os.path.join(TMP, f"{golpe}.fbx")
    prev = os.path.join(PREVIAS, f"{golpe}_tmp.png")
    r = subprocess.run([BLENDER, "-b", RIG, "-P", os.path.join(FERR, "pose_para_fbx.py"), "--", pose, fbx, prev],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    linhas = [l for l in r.stdout.splitlines() if l.startswith(("FBX", "PREVIA", "Traceback", "Error"))]
    if not os.path.exists(fbx):
        print(r.stdout[-3000:], r.stderr[-2000:]); raise RuntimeError("falha no Blender")
    partes = next((l.split(" ", 1)[1].split(";") for l in linhas if l.startswith("PREVIA")), [])
    os.makedirs(ANIM, exist_ok=True)
    shutil.copy2(fbx, os.path.join(ANIM, f"{golpe}.fbx"))
    atualizar_contato(golpe, info["contato"])
    folha = montar_previa(golpe, partes)
    print(f"  ✓ {golpe}: mão {info['mao']}, contato em {info['contato']:.2f}s, {len(info['quadros'])} quadros"
          + (f", prévia: {folha}" if folha else ""))


def main():
    os.makedirs(VIDEOS, exist_ok=True)
    feitos_p = os.path.join(VIDEOS, "_processados.json")
    feitos = json.load(open(feitos_p, encoding="utf-8")) if os.path.exists(feitos_p) else {}
    arquivos = sorted(glob.glob(os.path.join(VIDEOS, "*")))
    novos = 0
    for arq in arquivos:
        if os.path.isdir(arq): continue
        golpe = golpe_do_nome(arq)
        ext = os.path.splitext(arq)[1].lower()
        if golpe is None:
            if ext in EXT_VIDEO + (".fbx",): print(f"• {os.path.basename(arq)}: nome não reconhecido. Use um de: {', '.join(GOLPES)}")
            continue
        chave = f"{os.path.basename(arq)}:{os.path.getsize(arq)}:{int(os.path.getmtime(arq))}"
        if feitos.get(chave): continue
        print(f"• {os.path.basename(arq)} → {golpe}")
        try:
            if ext == ".fbx":
                processar_video(arq, golpe, de_fbx=True)
            elif ext in EXT_VIDEO:
                processar_video(arq, golpe)
            else:
                continue
            feitos[chave] = True; novos += 1
        except Exception as e:
            print(f"  ✗ erro: {e}")
        with open(feitos_p, "w", encoding="utf-8") as f: json.dump(feitos, f, indent=1)
    print(f"\nPronto: {novos} animação(ões) nova(s). Abra o Unity (ou volte para ele) e aperte Play.")


if __name__ == "__main__":
    main()
