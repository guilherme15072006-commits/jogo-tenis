// Baixa em lote as animações do Mixamo para o jogo (roda na página do Mixamo, já logado na sua conta Adobe).
// Como usar: abra https://www.mixamo.com, faça login, aperte F12 → Console, cole este arquivo inteiro e Enter.
// O Chrome pode perguntar "permitir vários downloads" → Permitir. Os .fbx caem na pasta Downloads;
// mova para JogoTenis/videos_golpes/ (o PROCESSAR_VIDEOS.bat converte FBX para o boneco do jogo).
//
// Baixa no personagem padrão (Y Bot), SEM pele (só o esqueleto), 30 fps, "In Place" nas corridas
// (o jogo é que move o jogador).
(async () => {
  // [nome do arquivo no jogo, busca no Mixamo, preferir nome que contenha, in place?]
  const LISTA = [
    ["espera",          "tennis idle",        "idle",      false],
    ["corrida",         "running",            "running",   true ],
    ["corrida_frente",  "fast run",           "run",       true ],
    ["corrida_tras",    "running backward",   "backward",  true ],
    ["passo_esq",       "left strafe",        "left",      true ],
    ["passo_dir",       "right strafe",       "right",     true ],
    ["parada",          "run to stop",        "stop",      true ],
    ["split",           "jump in place",      "jump",      true ],
    ["virar_esq",       "left turn",          "turn",      true ],
    ["virar_dir",       "right turn",         "turn",      true ],
    ["mix_tennis_1",    "tennis",             "",          false],
    ["mix_tennis_2",    "tennis forehand",    "forehand",  false],
    ["mix_tennis_3",    "tennis backhand",    "backhand",  false],
    ["mix_tennis_4",    "tennis serve",       "serve",     false],
  ];
  const token = localStorage.getItem("access_token");
  if (!token) { alert("Faça login no Mixamo primeiro."); return; }
  const H = { "Authorization": "Bearer " + token, "X-Api-Key": "mixamo2", "Content-Type": "application/json", "Accept": "application/json" };
  const api = (p, o = {}) => fetch("https://www.mixamo.com/api/v1/" + p, { headers: H, ...o }).then(r => { if (!r.ok) throw new Error(p + " → " + r.status); return r.json(); });
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const charId = (await api("characters/primary")).primary_character_id;
  console.log("Personagem:", charId);
  const feitos = [];
  for (const [arq, busca, prefer, inplace] of LISTA) {
    try {
      const res = await api(`products?page=1&limit=24&order=&type=Motion&query=${encodeURIComponent(busca)}`);
      const lista = res.results || [];
      const p = lista.find(x => prefer && x.name.toLowerCase().includes(prefer)) || lista[0];
      if (!p) { console.warn("Nada encontrado:", busca); continue; }
      const det = await api(`products/${p.id}?similar=0&character_id=${charId}`);
      const g = det.details.gms_hash;
      const gh = { ...g, params: (g.params || []).map(x => x[1]).join(","), inplace: inplace && "inplace" in g ? true : g.inplace };
      await api("animations/export", { method: "POST", body: JSON.stringify({
        character_id: charId, product_name: p.name, type: "Motion", gms_hash: [gh],
        preferences: { format: "fbx7_2019", skin: "false", fps: "30", reducekf: "0" } }) });
      let url = null;
      for (let i = 0; i < 60 && !url; i++) {
        await sleep(2000);
        const m = await api(`characters/${charId}/monitor`);
        if (m.status === "completed") url = m.job_result;
        else if (m.status === "failed") throw new Error("exportação falhou");
      }
      if (!url) throw new Error("tempo esgotado");
      const blob = await (await fetch(url)).blob();
      const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = arq + ".fbx"; a.click();
      feitos.push(`${arq}.fbx  ←  "${p.name}"`); console.log("OK", arq, "←", p.name);
      await sleep(1500);
    } catch (e) { console.error("Falhou", arq, e.message); }
  }
  console.log("PRONTO:\n" + feitos.join("\n"));
  alert("Mixamo: " + feitos.length + " animações baixadas. Veja o Console para a lista.");
})();
