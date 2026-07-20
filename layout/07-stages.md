# Aba STAGES — documentação de layout e comportamento

> Fonte da verdade **da FUNÇÃO** (não do visual atual). Uma IA/pessoa deve conseguir
> redesenhar o layout sem adivinhar comportamento.
>
> Arquivo de código: `src/TbhBot.App/Views/StagesView.cs` (classe `StagesView : UserControl`).
> Backend: `src/TbhBot.Core/Game/SaveData.cs` (`StageProgress`, `SetMaxStage`).
> Tema: `src/TbhBot.App/Theme/Dark.xaml`. Serviço: `src/TbhBot.App/Services/EngineService.cs`.

---

## 1. Propósito

Mapa dos **120 estágios** do jogo (4 dificuldades × 3 atos × 10 estágios). Mostra, para cada
estágio, se está **liberado**, **bloqueado** ou é o **atual** (onde o jogador está), e permite
**desbloquear em massa** o progresso até uma dificuldade escolhida por um único clique
(`Save.SetMaxStage`, que fecha o jogo em ~12 s por anti-cheat mas persiste o progresso).

---

## 2. Estrutura visual atual

Chave de estágio: **`StageKey = (dif+1)*1000 + act*100 + est`**
onde `dif ∈ {0=NORMAL, 1=NIGHTMARE, 2=HELL, 3=TORMENT}`, `act ∈ {1,2,3}`, `est ∈ {1..10}`.
Exemplos: `NORMAL 1-1 = 1101`, `NORMAL 3-10 = 1310`, `TORMENT 3-10 = 4310` (a maior key).
`est == 10` é sempre um **boss** (marcado `★`, custa soulstone).

`rootGrid` tem 3 linhas (`Auto`, `Auto`, `Star`):

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Row 0]  BARRA DE TOPO  (DockPanel, margin 8,10,8,4)                            │
│  ┌ Dock.Left: StackPanel horizontal ─────────────┐          ┌ Dock.Right ────┐ │
│  │ [Desbloquear TUDO] [até NORMAL][até NIGHT…]    │  _status │  [⟳ Refresh]   │ │
│  │ [até HELL][até TORMENT]                        │  (meio)  │                │ │
│  └────────────────────────────────────────────────┘          └────────────────┘ │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Row 1]  _hint  ("passe o mouse sobre uma fase")   (mono, subtle, margin 14,…) │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Row 2]  cardBorder (Card, radius 10, border Stroke, margin 8,2,8,8)           │
│  └ ScrollViewer (vertical Auto, horizontal Disabled)                           │
│     └ Grid (overlay):  _map  +  _empty  (ocupam a mesma célula; um esconde o    │
│                                          outro por Visibility)                   │
│                                                                                 │
│     _map (StackPanel, margin 14,6,14,14) — 4 blocos de dificuldade empilhados:  │
│     ┌──────────────────────────────────────────────────────────────────────┐  │
│     │ NORMAL (colorido, 15 bold)                                    12/30    │  │  ← header (DockPanel)
│     │ Ato 1   [1][2][3][4][5][6][7][8][9][★]                                 │  │
│     │ Ato 2   [1][2][3][4][5][6][7][8][9][★]                                 │  │
│     │ Ato 3   [1][2][3][4][5][6][7][8][9][★]                                 │  │
│     ├──────────────────────────────────────────────────────────────────────┤  │
│     │ NIGHTMARE …                                                    0/30    │  │
│     │ …                                                                       │  │
│     │ HELL …                                                                  │  │
│     │ TORMENT …                                                               │  │
│     └──────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
```

Agrupamento lógico:
- **Barra de topo (Row 0)**: ações de desbloqueio (esquerda) · status textual (meio) · refresh (direita).
- **Hint (Row 1)**: linha única que descreve a última célula sob o mouse.
- **Mapa (Row 2)**: 4 blocos (um por dificuldade), cada um com **header** (nome + contador `X/30`)
  e **3 linhas de ato** (`Ato 1/2/3`), cada linha com um rótulo de largura 56 + **10 células 40×40**.
- **_empty** ocupa o mesmo espaço do `_map` e aparece quando o progresso ainda não resolveu.

### Elementos de exibição (não-interativos)

| Elemento | Texto inicial | Fonte | Atualizado por |
|---|---|---|---|
| `_status` (TextBlock, mono 11, `Sub`, ellipsis) | `—` | — | `Paint()` (contagem `X/120` + atual) e `Unlock()` (progresso da ação) |
| `_hint` (TextBlock, mono 11, `Subtle`) | `passe o mouse sobre uma fase` | cache `_lastMax/_lastCur` | `ShowHint()` no `MouseEnter` de célula |
| `_empty` (TextBlock, mono 13, `Sub`) | `carregando… (abra o jogo para as fases aparecerem)` | `_svc.IsAttached` | `Paint()` alterna texto/visibilidade |
| header de dificuldade — **nome** (TextBlock, mono 15 bold, cor da dificuldade) | `NORMAL`/`NIGHTMARE`/`HELL`/`TORMENT` | estático | nunca (fixo) |
| header de dificuldade — **contador** `_diffCounts[d]` (TextBlock, mono 11, `Sub`, alinhado à direita) | `0/30` | `Paint()` | `duc/30` = liberados naquela dificuldade |
| rótulo de ato (TextBlock, mono 11, `Sub`, largura 56) | `Ato 1/2/3` | estático | nunca |

---

## 3. Tabela de CONTROLES (interativos)

> São **6 botões** + **120 células** (as células são idênticas em comportamento → 1 linha-tipo).
> Total de tipos de controle interativo documentados: **7**.

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Desbloquear TUDO | `Button` (`Accent.Button`, laranja) | `Desbloquear TUDO` | sempre visível/habilitado | — | `Click → Unlock(4310, "TORMENT 3-10")`: se `!IsAttached` → `_status="jogo fechado"`; senão **MessageBox YesNo (Warning)** de confirmação; se `Yes` → `_status="aplicando…"` e em background `Save.SetMaxStage(4310)` | `_status` vira `✔ liberado até TORMENT 3-10 (4310) · …` ou `falhou — jogo fechado?` | sempre habilitado (a guarda é em runtime dentro de `Unlock`, não desabilita o botão) |
| até NORMAL | `Button` (padrão, `Foreground`=verde `#5CBF6A`, 11px) | `até NORMAL` | visível/habilitado | — | `Click → Unlock(1310, "NORMAL 3-10")` → mesmo fluxo (confirma → `SetMaxStage(1310)`) | `_status` idem | sempre habilitado (guarda em runtime) |
| até NIGHTMARE | `Button` (padrão, `Foreground`=azul `#4BB0CC`, 11px) | `até NIGHTMARE` | visível/habilitado | — | `Click → Unlock(2310, "NIGHTMARE 3-10")` → `SetMaxStage(2310)` | `_status` idem | sempre habilitado |
| até HELL | `Button` (padrão, `Foreground`=âmbar `#E8A13A`, 11px) | `até HELL` | visível/habilitado | — | `Click → Unlock(3310, "HELL 3-10")` → `SetMaxStage(3310)` | `_status` idem | sempre habilitado |
| até TORMENT | `Button` (padrão, `Foreground`=vermelho `#E5564C`, 11px) | `até TORMENT` | visível/habilitado | — | `Click → Unlock(4310, "TORMENT 3-10")` → `SetMaxStage(4310)` (equivale a "Desbloquear TUDO", só o label muda) | `_status` idem | sempre habilitado |
| ⟳ Refresh | `Button` (`Accent.Button`) | `⟳ Refresh` | visível/habilitado | — | `Click → Refresh()`: se `!IsAttached` → `Paint(-1,-1)`; senão lê `Save.StageProgress()` em background e repinta | repinta as 120 células, contadores e `_status` | sempre habilitado |
| célula de estágio (×120) | `Border` 40×40 com `TextBlock` filho, `Cursor=Hand` | `1..9` ou `★` (est 10 = boss) | pintada por `Paint()`; **hover-only** (não tem `Click`) | `MouseEnter` usa cache `_lastMax`/`_lastCur` | `MouseEnter → ShowHint(key)`: escreve em `_hint` `"{StageName} · {estado}{bossTag}"` | `_hint` na Row 1 | sempre "habilitada" para hover; **clique não faz nada** (sem handler de Click) |

Notas de comportamento das células:
- **Não são clicáveis** — apesar de `Cursor=Hand`, só respondem a `MouseEnter` (hover). Nenhuma célula
  navega nem escreve; o único caminho de escrita é pelos botões de desbloqueio.
- Estado no hint (`ShowHint`): `← ATUAL` se `key == cur && cur >= 0`; senão `liberado` se `key <= max`;
  senão `bloqueado`. `bossTag` = `"  ·  ★ boss (custa soulstone)"` quando `key % 100 == 10`.
- O hint usa os valores **em cache** da última pintura (`_lastMax`, `_lastCur`) — não faz leitura de
  memória no hover (instantâneo e barato).

---

## 4. Comportamento vivo (timers / auto-refresh)

- **`DispatcherTimer` de 2 s** (`Interval = TimeSpan.FromSeconds(2)`) → `Refresh()` a cada tick.
  Iniciado em `Loaded`, parado em `Unloaded`.
- **`_svc.StateChanged += Refresh`**: re-pinta imediatamente quando o serviço conecta/desconecta do jogo
  (disparado no thread da UI). Desinscrito em `Unloaded`.
- **`Loaded`** → `_timer.Start()` + `Refresh()` imediato.
- **`Unloaded`** → `_timer.Stop()` + `_svc.StateChanged -= Refresh` (limpeza).
- **`Refresh()`** (a cada 2 s ou por evento):
  - se `!_svc.IsAttached` → `Paint(-1, -1)` (estado "jogo fechado", sem tocar memória).
  - senão `Task.Run`: lê `_svc.Engine.Save.StageProgress()` **em background** (fora do thread da UI),
    captura `Max` e `Cur` (ignora `Wave`), exceção → `max=cur=-1`, e faz `Dispatcher.Invoke(Paint(max,cur))`.
- **`Paint(max, cur)`** guarda `_lastMax/_lastCur`, alterna `_empty`/`_map` por `resolved = max >= 0`,
  repinta as 120 células, atualiza os 4 contadores `X/30` e o `_status` (`X/120 liberados · atual: …`).

---

## 5. Estados especiais

| Situação | Detecção | O que aparece |
|---|---|---|
| Jogo fechado | `!_svc.IsAttached` (`Engine.IsAttached && Target.IsAlive()`) | `_map` escondido; `_empty` visível = **`jogo fechado — reconecta ao reabrir`**; `_status` = `jogo fechado ou resolvendo offsets…` |
| Jogo aberto mas offsets ainda não resolvidos | `IsAttached == true` porém `StageProgress().Max == -1` | `_empty` visível = **`jogo aberto, resolvendo offsets…`**; `_status` = `jogo fechado ou resolvendo offsets…`; `_map` escondido |
| Resolvido | `max >= 0` | `_empty` escondido; `_map` visível e pintado; `_status` = `{n}/120 liberados · atual: {NORMAL a-b}` (ou `—` se `cur < 0`) |
| Falha de leitura (exceção em `StageProgress`) | `catch` → `max=cur=-1` | cai no caminho "resolvendo offsets" (tratado como não-resolvido) |
| Clicar desbloquear com jogo fechado | `Unlock` vê `!IsAttached` | `_status = "jogo fechado"`, nenhuma confirmação é mostrada |
| Confirmação recusada | `MessageBox` != `Yes` | nada acontece (retorna) |
| `SetMaxStage` falha | `(ok=false)` ou exceção | `_status = "falhou — jogo fechado?"` |
| Estado atual desconhecido (`cur < 0`) | — | nenhuma célula pintada como "atual"; `_status` mostra `atual: —` |

Observação: `StageProgress` retorna `-1` em cada campo que não resolver (static fields ausentes /
símbolos `uo_max/uo_cur/uo_wave` não carregados). Só `Max` e `Cur` são usados pela tela; `Wave` é ignorado.

---

## 6. Cores/estilo usados hoje (e significado)

### Cores por dificuldade (`DiffCol`, usadas no nome do header e no `Foreground` dos botões "até X")
| Dificuldade | Hex | Cor |
|---|---|---|
| NORMAL | `#5CBF6A` | verde |
| NIGHTMARE | `#4BB0CC` | azul/ciano |
| HELL | `#E8A13A` | âmbar/laranja |
| TORMENT | `#E5564C` | vermelho |

### Estados de célula (definidos em `Paint`)
| Estado | Condição | Fundo | Borda (normal / boss) | Espessura (normal / boss) | Texto |
|---|---|---|---|---|---|
| **ATUAL** | `key == cur && cur >= 0` | `Acc` `#FF7A18` | `AccH` `#FF9440` | 2 | `AccTxt` `#0A0A0A`, **Bold** |
| **Liberado** | `key <= max` (e não é o atual) | `UnlockedFill` `#2F2110` (laranja-escuro) | `Acc` `#FF7A18` / `Amber` `#FBBF24` | 1 / 2 | `Fg` `#FFFFFF`, boss=Bold |
| **Bloqueado** | `key > max` | `LockedFill` `#141519` | `LockedStroke` `#2B2E34` / `Amber` `#FBBF24` | 1 / 2 | `LockedText` `#666B74` / boss=`Amber`, boss=Bold |

Significado das cores:
- **Laranja/accent** = o estágio **atual** (onde o jogador está agora) — realce mais forte (borda 2px, fundo laranja pleno).
- **Fundo laranja-escuro `#2F2110`** = estágio **liberado/completável**.
- **Âmbar `#FBBF24`** (borda 2px + texto) = **boss** (`est 10`, o `★`) — destacado em qualquer estado; custa soulstone.
- **Cinza escuro `#141519` + texto apagado `#666B74`** = **bloqueado**.

### Brushes de tema (`Dark.xaml`) usados
| Recurso | Hex | Uso na tela |
|---|---|---|
| `Card` | `#16181C` | fundo do `cardBorder` (contêiner do mapa) |
| `Stroke` | `#2B2E34` | borda do `cardBorder` |
| `Sub` | `#9AA0AB` | `_status`, `_empty`, contadores `X/30`, rótulos "Ato N" |
| `Subtle` | `#666B74` | `_hint` |
| `Acc` | `#FF7A18` | célula atual / borda de célula liberada / botões accent |
| `AccH` | `#FF9440` | borda da célula atual, hover dos botões accent |
| `AccTxt` | `#0A0A0A` | texto da célula atual e dos botões accent |
| `Amber` | `#FBBF24` | bosses |
| `Fg` | `#FFFFFF` | texto de célula liberada |
| `Mono` | Consolas | toda a tipografia da tela (status, hint, headers, células, rótulos) |
| `Accent.Button` (estilo) | — | botões "Desbloquear TUDO" e "⟳ Refresh" |
| estilo `Button` padrão | fundo `Card2` `#212429`, borda `Stroke2` `#3D4149` | botões "até X" |

Cores **fixas** (hardcoded, não vêm do tema): `DiffCol[]`, `UnlockedFill`, `LockedFill`,
`LockedStroke`, `LockedText` — todas criadas via `Frozen(hex)` (SolidColorBrush congelado).

---

## 7. Chamadas de backend

Todas via `_svc` (`EngineService`) e `_svc.Engine` (`Engine`).

| Método / propriedade | O que faz | Uso na tela |
|---|---|---|
| `_svc.IsAttached` | `Engine.IsAttached && Engine.Target.IsAlive()` — jogo conectado e vivo | guarda de `Refresh` e `Unlock` |
| `_svc.StateChanged` (event) | disparado no thread da UI ao conectar/desconectar | assina/desassina para repintar |
| `_svc.Engine.Save.StageProgress()` → `(int Max, int Cur, int Wave)` | Lê os `ObscuredInt` runtime `uo_max` (maxCompletedStage), `uo_cur` (stage atual) e `uo_wave` a partir dos static fields de `uo_ti`. `-1` em cada campo não resolvido. Decodificação ACTk: `((hidden-key)^key)`. | fonte única de dados do mapa (usa `Max` e `Cur`) |
| `_svc.Engine.Save.SetMaxStage(int value=4310)` → `(bool Ok, int Value)` | Escreve o `ObscuredInt` runtime `uo_max` (autoritativo) **e** espelha no int de save `CommonSaveData.maxCompletedStage` (via PSD→`+0x10`→`+0x54`). ⚠️ Dispara o honesty-check do ACTk → o jogo **fecha sozinho em ~12 s**, MAS o valor persiste (auto-save) e recarrega limpo. | ação dos 5 botões de desbloqueio |

**Não usado por esta tela, mas relacionado (para o redesign):**
- `Save.StageProgress()` é a **única** leitura. A tela **NÃO** chama `StageTable()`.
- `StageTable()` existe em **`Engine.StageNav`** (não em `Save`) e retorna `{key → {Next,Type,Ss,Lvl,Waves}}`
  da tabela viva do jogo (nível recomendado, soulstone, nº de waves, tipo boss). Um redesign que
  quisesse enriquecer as células (ex.: mostrar nível recomendado, custo de soulstone real, tooltip com
  waves) puxaria daí — mas hoje o mapa deriva "é boss" apenas de `est == 10`, sem consultar a tabela.

---

## 8. Notas para o redesign (o que preservar na FUNÇÃO)

**Essencial (não mudar o comportamento):**
1. **Modelo de dados é UM inteiro.** O progresso é só `max` (`maxCompletedStage`). Regra de liberação:
   **`liberado ⇔ key <= max`**. `cur` é um segundo inteiro (estágio atual) só para o realce "ATUAL".
   Não invente estado por-estágio que o jogo não tem.
2. **StageKey = `(dif+1)*1000 + act*100 + est`** com `dif 0..3`, `act 1..3`, `est 1..10`. `est == 10` = boss (`★`, custa soulstone).
   `StageName(key)` = `"{Diff} {(key%1000)/100}-{key%100}"`.
3. **Três estados visuais distintos por célula**: atual / liberado / bloqueado — e o boss recebe ênfase
   (âmbar) **em qualquer** dos três. Preserve essa distinção de 3 estados.
4. **Desbloqueio é destrutivo e precisa de confirmação.** `SetMaxStage` fecha o jogo em ~12 s (ACTk) mas
   persiste — o `MessageBox` de aviso deve continuar existindo. Guardar sempre com `IsAttached` antes.
5. **Leitura fora do thread da UI.** `StageProgress()` toca memória do processo → roda em `Task.Run` e só
   volta à UI via `Dispatcher.Invoke`. Nunca ler síncrono no thread da UI.
6. **Hover é barato e usa cache.** O hint lê `_lastMax/_lastCur` (setados na última pintura), não a memória.
   Manter isso para hover instantâneo.
7. **Dois textos de "vazio" diferentes**: `jogo fechado — reconecta ao reabrir` vs `jogo aberto, resolvendo
   offsets…`. A distinção (attached mas `max==-1`) é informativa e deve sobreviver.
8. **Auto-refresh de 2 s + `StateChanged`.** O mapa precisa se atualizar sozinho (o `cur` muda enquanto se
   joga) e reagir a conectar/desconectar. Parar o timer e desassinar no `Unloaded`.
9. **Contadores por dificuldade**: `X/30` (30 = 3 atos × 10) e total `X/120`. Preserve os totais.
10. **"Desbloquear TUDO" == "até TORMENT"** (ambos `4310`); os valores de "até X" são `(d+1)*1000+310`
    (NORMAL 1310, NIGHTMARE 2310, HELL 3310, TORMENT 4310).

**Armadilhas conhecidas:**
- As células **não são clicáveis** hoje. Se o redesign quiser navegação por clique, isso é uma FUNÇÃO
  NOVA (usaria `StageNav.GoToStage`/`EnterBoss`, main-thread) — não é comportamento atual e não deve ser
  assumido.
- `SetMaxStage` sempre fecha o jogo; a mensagem de sucesso já orienta "o jogo fecha em ~12s; reabra (já
  salvo)". Não remova esse aviso — sem ele o usuário acha que travou.
- Só `Max`/`Cur` são usados; `Wave` volta de `StageProgress` mas é ignorado aqui.
- O "é boss" é derivado só de `est == 10`; a tela não valida contra `StageTable`. Se um redesign passar a
  exibir soulstone/nível reais, terá que buscar `Engine.StageNav.StageTable()` (classe diferente de `Save`).
- Cores de dificuldade e fills de célula são **hardcoded** (não são recursos de tema) — se o tema mudar,
  esses hex continuam fixos por design (`Frozen`).
