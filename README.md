# Jogo de Tênis (protótipo)

Jogo de tênis em Unity 6 com controles de toque para celular, física de bola própria e animações capturadas de vídeo.

**Jogar no navegador (celular ou PC):** https://guilherme15072006-commits.github.io/jogo-tenis/
Gire o celular na horizontal.

## Controles
- **Polegar esquerdo:** joystick (o atleta também corre sozinho até a bola; o joystick ajusta).
- **Polegar direito:** gesto de golpe
  - diagonal ↗ topspin · diagonal ↘ slice · lateral ↔ chapado · ↑ lob · toque rápido = curtinha · ↓ com bola alta = smash
- **Saque:** toque à direita para lançar a bola e deslize ↓ perto do ponto mais alto.
- No PC: WASD para andar e arrastar o mouse na metade direita para bater.

## Estrutura
- `TenisAAA/` — projeto Unity (`Assets/Scripts/Core` é C# puro: física, gestos, golpes, placar, IA — 27 testes automáticos).
- `tools/` — automação: vídeo → animação 3D (`PROCESSAR_VIDEOS.bat`), preparação do rig do Tripo, exportação da arena, gerador de animações.
- `docs/` — versão WebGL publicada no GitHub Pages.
- `PLANO_CONTROLES.md` — especificação e status.
