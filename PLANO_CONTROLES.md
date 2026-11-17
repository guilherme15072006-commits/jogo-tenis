# Plano — Controles touch, golpes e física da bola

Projeto Unity: `TenisAAA/` (Unity 6000.6.3). Unidades em metros, segundos e radianos.
Coordenadas da quadra: **x** = comprimento (rede em x = 0, jogador local em x < 0), **y** = largura, **z** = altura.

## Arquitetura

O jogo tem duas camadas:

```
Assets/Scripts/Core/    (Tenis.Core: C# puro, sem UnityEngine, roda igual no servidor)
  Math/Vec3.cs, DetRandom.cs        vetor double + RNG determinístico (xorshift)
  Config/TennisConfig.cs            TODOS os parâmetros (JSON), sem mexer no código
  Physics/BallState.cs, BallSimulator.cs, Surface.cs, NetModel.cs, RacketImpact.cs
  Court/Court.cs                    medidas ITF, zonas de mira, caixas de saque
  Input/GestureSample.cs, GestureClassifier.cs, GestureFilters.cs
  Shots/ShotContext.cs, ShotSelector.cs, PowerModel.cs, AimModel.cs, TimingModel.cs, ShotSolver.cs
  Player/PlayerMotor.cs, AthleteProfile.cs
  Serve/ServeFlow.cs, ServeRules.cs

Assets/Scripts/Game/    (Tenis.Game: camada Unity)
  Touch/TouchRouter.cs              multitoque: dono de cada dedo (joystick × área de golpe)
  Touch/VirtualJoystick.cs          joystick flutuante no lado esquerdo
  Touch/GestureZone.cs              captura do gesto no lado direito
  PlayerController.cs, BallView.cs, AimIndicator.cs, ConfigLoader.cs

Assets/StreamingAssets/tennis_config.json   parâmetros editáveis
Assets/Tests/EditMode/                      testes automáticos do núcleo
```

**Determinismo (requisito 9):** a simulação usa `double`, passo fixo de 1/240 s e integração RK4. Toda aleatoriedade (erro de mira, net cord) vem de um `DetRandom` com semente da partida e índice do golpe. O servidor roda o mesmo `Tenis.Core` com as mesmas entradas e confere o resultado. Não é bit-exato entre CPUs diferentes; se a validação exigir isso, o próximo passo é migrar para ponto fixo.

## Mapeamento da especificação → módulos

| Spec | Módulo | Entrega |
|---|---|---|
| 3. Joystick esquerdo | `VirtualJoystick` + `PlayerMotor` | aceleração, desaceleração, inércia, penalidade de troca de direção, velocidade máxima, retorno natural ao soltar, equilíbrio corporal |
| 3. Área de gestos direita | `GestureZone` + `GestureClassifier` | direção, comprimento, velocidade, ângulo, início/fim, contexto da bola |
| 3. Multitoque | `TouchRouter` | cada dedo pertence a uma zona do início ao fim do toque |
| 4. Golpes | `ShotSelector` | forehand/backhand (uma ou duas mãos), plano, topspin, slice, lob, curtinha, smash contextual, voleios (direita, esquerda, bloqueio, direcionado) |
| 5. Classificador | `GestureClassifier` + `GestureFilters` | ângulo + distância + velocidade + duração + contexto + estado; filtros de acidental, curto, toque involuntário, duplicado, conflito |
| 6. Potência | `PowerModel` | velocidade do dedo (saturada), amplitude, preparação, velocidade da raquete, equilíbrio, tipo, físico; limites por golpe |
| 7. Mira | `AimModel` + `ShotSolver` | 6 zonas, intenção → trajetória física resolvida no simulador, erro gaussiano por precisão/contato/posição/intensidade; indicador opcional |
| 8. Timing | `TimingModel` | janela variável por altura, velocidade, distância, tipo, preparação e deslocamento; Perfeito/Muito bom/Bom/Atrasado/Falha |
| 9. Física | `BallSimulator` | gravidade, arrasto, Magnus, decaimento de giro, quique com atrito e troca giro↔velocidade, superfícies, rede (fita e corpo), raquete |
| 10. Saque | `ServeFlow` + `ServeRules` | posicionamento → lançamento → preparação → gesto → contato → finalização; plano/topspin/slice; 1º e 2º saque, falta, let, área válida |

## Ordem de execução
1. Núcleo: física da bola + testes (quique, Magnus, rede, determinismo).
2. Classificador de gestos + filtros + testes.
3. Seleção de golpe, potência, mira, timing e solver de trajetória + testes.
4. Saque e regras + testes.
5. Camada Unity: toque, joystick, movimento, cena de teste (cinza) jogável.
6. Ajuste fino dos parâmetros jogando (no aparelho).

## Status (11/11/2026)
- [x] Núcleo `Tenis.Core`: física, gestos e filtros, seleção de golpe, potência, mira, timing, solver de trajetória, saque e regras, movimentação.
- [x] Placar completo (`TennisScore`): 15/30/40, iguais/vantagem, games, sets, tiebreak, rodízio de saque e lado do saque.
- [x] Estados de animação (`AthleteAnimation`): corrida com passada e inclinação, equilíbrio, split-step, fases do golpe, todas as fases do smash e do saque.
- [x] Motor da partida (`MatchEngine`): saque → rali → árbitro → placar, IA que usa as mesmas regras (gestos sintéticos), prévia de mira com elipse de erro.
- [x] 27 testes automáticos passando no Unity, incluindo um set inteiro jogado por um robô (76 pontos, ralis de até 36 golpes).
- [x] Camada Unity: multitoque, atleta procedural animado, indicador de mira opcional, câmera de transmissão, placar na tela.
- [ ] Jogar no editor e no celular; ajustar `tennis_config.json` (a IA está consistente demais: ralis longos).
- [ ] Trocar o atleta procedural pelo personagem com rig (Mixamo) lendo os mesmos estados de animação.
- [ ] Colocar a partida dentro do estádio (`estadio_aaa`) e migrar para URP.
- [ ] Validação no servidor e, se preciso, ponto fixo para determinismo bit-exato.
