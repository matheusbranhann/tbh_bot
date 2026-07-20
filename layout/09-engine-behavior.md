# 09 — Comportamento de Backend (Engine + loops): a ponte controle→jogo

> **Fonte da verdade da FUNÇÃO, não do visual.** Este doc descreve o que cada controle da UI FAZ
> no nível do motor (`Engine`, `AutomationLoop`, `StageAutomation`, `WatchdogService`, `RealDispatcher`).
> Quem for redesenhar o layout pode mudar TUDO no visual, mas **precisa preservar estes comportamentos**.
>
> Arquivos lidos: `src/TbhBot.Core/Engine.cs`, `src/TbhBot.Core/Automation/AutomationLoop.cs`,
> `src/TbhBot.App/Services/WatchdogService.cs`, `src/TbhBot.Core/Game/StageAutomation.cs`,
> `src/TbhBot.App/Services/EngineService.cs`, `src/TbhBot.App/Views/TrainerView.cs`,
> `src/TbhBot.Core/Game/{Cheats,AutoBox,AutoStash,AutoFuse,StatEditor,StageNav,SaveData,RealDispatcher}.cs`,
> `src/TbhBot.App/Theme/Dark.xaml`.

---

## 0. Modelo mental: como um clique vira efeito no jogo

O painel **nunca** mexe no jogo direto a partir do handler do checkbox. Um controle só faz **uma coisa**:
setar uma *flag de intenção* `Want*` no `Engine` (um `bool` público simples) ou trocar um `Dictionary`
forçado. Quem observa essas flags e age é o **`AutomationLoop`**, num tick de 250 ms, numa única thread.

```
   [ UI: CheckBox/Button ]                [ Engine (estado) ]              [ Loops (ação) ]
        Auto-box  ──Checked──►  engine.WantAutobox = true  ──lido a cada 250ms──►  AutomationLoop
        God Mode  ──Checked──►  engine.WantGodmode = true                                │
        stat row  ──Aplicar──►  engine.WantStats = {...}                                 ▼
        Auto-restart Checked─►  engine.WantWatchdog = true  ──lido──►  WatchdogService  (thread própria)
                                                                                          │
                                                            ações Unity ─► RealDispatcher (main-thread)
                                                                                          │
                                                                                          ▼
                                                                                    [ JOGO ]
```

**Por que assim (racional do Python `_auto_loop`):** um único loop numa thread e um único dispatcher.
Duas threads disputando o dispatcher faziam o auto-box passar fome (*starvation*). Todo o desenho abaixo
existe pra manter essa disciplina.

### 0.1 As 10 flags de intenção (`Engine.cs`, linhas 39–51)

| Flag | Tipo | Quem lê | Efeito quando `true` (resumo) |
|---|---|---|---|
| `WantActk` | `bool` | AutomationLoop (todo tick) | Neutraliza o detector do anti-cheat (ACTk): NOP/`ret` nos RVAs de `sym.Ynj`. Pré-requisito de qualquer auto-modo. |
| `WantGodmode` | `bool` | AutomationLoop (todo tick) | Patcha o prólogo da função de dano do player → `ret` imediato (não toma dano). |
| `WantAutobox` | `bool` | AutomationLoop (todo tick, **prioridade 1**) | Acha as `StageBox` vivas e abre cada uma (dispatcher cmd1 = `llx`). |
| `WantAutostash` | `bool` | AutomationLoop (todo tick, **prioridade 2** + sort quando ocioso) | Move inventário→baú em lote (cmd2 = `iw`) e, ocioso, ordena o baú por grade (1 move/tick). |
| `WantAutofuse` | `bool` | AutomationLoop (todo tick, **prioridade 3**) | Uma síntese por tick no cubo (enche 9 + funde → 1), level-safe, respeitando os filtros. |
| `WantAutoboss` | `bool` | AutomationLoop (**só quando ocioso**) | Gasta 1 soulstone entrando no x-10 daquela dificuldade; a volta é nativa. |
| `WantEvolve` | `bool` | AutomationLoop (**só quando ocioso**) | Sobe **1 fase por vez** pela corrente `NextStageKey` até Torment 3-9; aí **se auto-desliga**. |
| `WantWatchdog` | `bool` | WatchdogService (thread própria) | Auto-restart: jogo fechou? reabre via Steam, fecha popup, reentra no estágio, religa tudo. |
| `WantStats` | `Dictionary<string,double>` | AutomationLoop (todo tick, se `Count>0`) | Re-escreve os stats do player TODO tick (o jogo sobrescreveria uma escrita única). |
| `WantStage` | `Dictionary<string,int>` | AutomationLoop (todo tick, se `Count>0`) | Re-escreve os campos do `StageData` corrente TODO tick. |

Mais duas variáveis de coordenação (não são controles, mas comandam o comportamento):

| Var | Tipo | Papel |
|---|---|---|
| `WdHold` | `volatile bool` | **Gate de "start limpo"**. O Watchdog liga durante o reboot; enquanto `true`, o AutomationLoop **não aplica nada** (nem cheat, nem stat) mesmo com as flags ligadas. |
| `SteamAppId` | `const int = 3678970` | AppId usado pelo `steam://run/<appid>` no relançamento. |

### 0.2 Quem hospeda os loops (`EngineService.Start`)

`EngineService.Start()` (chamado uma vez na abertura do app) dispara **três** tasks concorrentes sobre o
mesmo `Engine`:

1. **ConnectLoop** (`ConnectLoopAsync`) — a "tick" de reconexão. A cada **1000 ms**: se não está atado,
   tenta `Engine.Attach()`. **Re-attacha inclusive durante `WdHold`** (o hold só impede *aplicar*, não *atar*).
   Ao mudar de estado dispara `StateChanged` no thread da UI.
2. **AutomationLoop** (`RunAsync`) — o motor de 250 ms (seção 1).
3. **WatchdogService** (`RunAsync`) — dono único do auto-restart (seção 7).

Todo log dessas três rotas passa por `OnLog` → grava em `%APPDATA%/tbh_bot/session.log` **e** empurra pra
barra de status no thread da UI (`Application.Current.Dispatcher.BeginInvoke`).

---

## 1. `AutomationLoop` — o motor (tick 250 ms)

**Propósito.** Único laço que, a cada 250 ms, lê as flags `Want*` e (a) aplica proteção/cheats/valores
forçados e (b) executa as automações de ação na ordem de prioridade fixa. Nada de `Thread.Sleep`: tudo é
`await Task.Delay(.., ct)` cancelável.

### Estrutura do tick

```
RunAsync(ct)  ── loop até cancelar ─────────────────────────────────────────────┐
  │                                                                              │
  ├─ GATE:  !WdHold  &&  IsAttached  &&  Target.IsAlive()                        │
  │     │                                                                        │
  │     ├─ (falso) → não faz nada neste tick (só espera)                         │
  │     │                                                                        │
  │     └─ (verdadeiro) →                                                        │
  │           ApplyCheats():                                                     │
  │              • Cheats.SetActk(WantActk)         ── idempotente, self-heal    │
  │              • Cheats.SetGodmode(WantGodmode)   ── idempotente, self-heal    │
  │              • se WantStats.Count>0  → Stats.ApplyStats(WantStats)           │
  │              • se WantStage.Count>0  → Stats.ApplyStage(WantStage)           │
  │                                                                              │
  │           RunActions():  (bool did = false)                                 │
  │              1) WantAutobox   → AutoBox.OpenAll(...)          SEMPRE  did|=   │
  │              2) WantAutostash → AutoStash.MoveAllToStash(...) SEMPRE  did|=   │
  │              3) WantAutofuse  → AutoFuse.DoSynth(...)         SEMPRE  did|=   │
  │              4) WantAutoboss  && !did → StageAutomation.AutoBoss(...)  did|=  │
  │              5) WantEvolve    && !did → StageAutomation.Evolve(...)    did|=  │
  │              6) WantAutostash && !did → AutoStash.SortStep(2)   (1 move)      │
  │                                                                              │
  ├─ try/catch por tick: exceção NÃO derruba o loop → loga "auto: erro (..)"     │
  └─ await Task.Delay(250ms, ct)                                                 ┘
```

### Regras invioláveis da ordem (o coração deste doc)

- **`box → stash → fuse` rodam SEMPRE**, em toda iteração, um atrás do outro. Caixa é prioridade máxima:
  checada todo tick pra abrir "na hora que dropa". `fuse` **não** é gateado por `!did` (comentário no
  código: `NÃO gateado por !did`).
- **`autoboss` e `evolve` só rodam quando `did == false`** — ou seja, quando **nada mais** aconteceu no
  tick. Motivo: esses modos **bloqueiam** (a luta do boss pode travar a thread por até 10 min); se não
  fossem gateados, o box/stash morreriam de fome.
- **`SortStep` (ordenar o baú) também só quando ocioso** (`WantAutostash && !did`), e faz **UM** move
  STASH→STASH por tick (amortizado pra não travar o auto-box).
- `did` é acumulativo (`|=`) ao longo do tick.

### Comportamento vivo

- **Passo fixo de 250 ms** (`TickInterval`). No Python era 0,12 s ativo / 0,5 s ocioso; 250 ms é o
  meio-termo escolhido — responsivo o bastante pra abrir caixa quase instantâneo.
- `ApplyCheats` é **idempotente e self-heal**: setar todo tick é barato e, se o jogo reiniciou e o cheat
  sumiu, ele **volta sozinho** no próximo tick.
- **Snapshot da referência** de `WantStats`/`WantStage`: o loop lê `var st = engine.WantStats` (assign
  atômico). A UI **troca o dicionário inteiro** (nunca muta o existente) pra não correr com o loop.

### Estados especiais

| Situação | Comportamento |
|---|---|
| `WdHold == true` (reboot em curso) | Tick **não aplica nada** — nem cheats, nem stats forçados, nem ações. Evita bater no *honesty-check* do ACTk e escrever com o jogo carregando. |
| Jogo desatado / morto (`!IsAttached` ou `!IsAlive`) | Tick só espera. Quem reconecta é o ConnectLoop; o AutomationLoop não atacha. |
| Exceção num tick | Capturada; loga `auto: erro (<msg>) — seguindo`. O loop **nunca** cai por um tick ruim. |
| `Dispatcher.IsReady == false` (cave não instalado) | As ações que dependem do main-thread simplesmente não fazem nada visível; não trava nem lança. |

---

## 2. Card **PROTEÇÃO** — `WantActk` / `WantGodmode`

**Propósito.** Liga/desliga os dois cheats de patch de código: bypass do anti-cheat e invencibilidade.
São a base (o ACTk é pré-requisito de todos os auto-modos).

### Estrutura visual atual (`TrainerView.BuildProtection`)

```
┌ PROTEÇÃO ─────────────────────────────┐
│  (•——) ACTk Bypass                    │   ← switch (CheckBox estilizado)
│  (•——) God Mode                       │   ← switch
└───────────────────────────────────────┘
```

### Tabela de CONTROLES

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (comportamento EXATO) | O que REFLETE | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| ACTk Bypass | CheckBox (switch) | `ACTk Bypass` | `engine.WantActk` (false) | — | Checked → `engine.WantActk = true`; Unchecked → `false`. **Efeito no tick:** `Cheats.SetActk(true)` percorre cada RVA de `sym.Ynj` e escreve `0xC3` (`ret`) guardando o byte original; `SetActk(false)` restaura os bytes. | Só o próprio estado do switch. | Sempre habilitado (não checa `IsAttached`; se desatado, o loop simplesmente não aplica). É **ligado automaticamente** quando qualquer auto-modo é ligado (ver 3.1). |
| God Mode | CheckBox (switch) | `God Mode` | `engine.WantGodmode` (false) | — | Checked → `engine.WantGodmode = true`. **Efeito no tick:** `Cheats.SetGodmode(true)` acha `AOB_GODMODE` **uma vez** (cacheia; o patch quebra o próprio padrão), lê o prólogo `push rdi (0x57)` e escreve `0xC3` (`ret` imediato → player não toma dano). `false` restaura `0x57`. | Só o próprio switch. | Sempre habilitado. |

### Chamadas de backend

- `Cheats.SetActk(bool)` — patch/unpatch por RVA (`sym.Ynj`, byte `0xC3`), guarda originais em `_actkOrig`.
- `Cheats.SetGodmode(bool)` — scan único do AOB (`GameConstants.AobGodmode`), patch `0x57↔0xC3`.

### Notas p/ redesign

- **Estes dois são chamados TODO tick** independentemente de "estar ligado". É idempotente de propósito
  (self-heal após restart). O redesign não precisa debouncar.
- O ACTk **precisa estar ligado** pra qualquer auto-modo funcionar (todos usam o hook do dispatcher, que o
  detector do jogo enxergaria). Preserve o acoplamento auto-modo→ACTk (seção 3.1).

---

## 3. Card **AUTOMAÇÃO** — parte A: ações que rodam SEMPRE (`WantAutobox`/`WantAutostash`/`WantAutofuse`)

**Propósito.** Ligar as três automações de ação de prioridade máxima e configurar os filtros do auto-fuse.

### Estrutura visual atual (`TrainerView.BuildAutomation`)

```
┌ AUTOMAÇÃO ─────────────────────────────────────────────────────┐
│  (•——) 🎁 Auto-box (abre caixas)                                │
│  (•——) 📦 Auto-stash (move pro baú + organiza por grade)        │
│  (•——) ⚗️ Auto-fuse (funde no cubo)                             │
│         fundir até: [ Rare ▾ ]                                   │  ← filtros do fuse (indentados 24px)
│         tipos:  ☑ Equip   ☑ Acess   ☑ Mat                        │
│         funde do grade menor ATÉ o escolhido (nunca acima)…      │  ← hint 10px
│  (•——) 🗡 Auto-boss (gasta soulstone no x-10 e volta)           │  ← parte B (seção 4)
│  (•——) 📈 Evolution (sobe 1 fase por vez até Torment 3-9…)      │  ← parte B
│  (•——) 🛡 Auto-restart (jogo fechou? reabre via Steam…)         │  ← seção 7
└─────────────────────────────────────────────────────────────────┘
```

### 3.1 Encadeamento AUTO-MODO → ACTk (`AutoToggle`)

Todos os toggles de auto-modo (auto-box/stash/fuse/boss/evolve) são criados por `AutoToggle`, que além de
setar o `Want*` respectivo, no evento `Checked` **liga o ACTk também** se estiver desligado:

```
cb.Checked → se _actkCheck.IsChecked != true:
                _actkCheck.IsChecked = true   (dispara Checked do ACTk → WantActk=true)
                log "ACTk Bypass ligado junto (o auto-modo usa o hook)"
```

> **Auto-restart (🛡) é a EXCEÇÃO:** usa `Toggle` normal (não `AutoToggle`) — **não** liga o ACTk sozinho.

### Tabela de CONTROLES — ações sempre

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (comportamento EXATO) | O que REFLETE | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Auto-box | CheckBox (switch, `AutoToggle`) | `🎁 Auto-box (abre caixas)` | `engine.WantAutobox` (false) | — | Checked → `WantAutobox=true` **+ liga ACTk**. **Efeito (prioridade 1, todo tick):** `AutoBox.OpenAll(keep)` acha as `StageBox` vivas por klass, conta abríveis via `iuw` (getter puro), e pra cada tipo (0..2) dispara `dispatcher.Command(1, ptr)` (`llx`) até o contador `iuw` parar de decrementar (guard 15). **Revalida a box antes de cada clique** (nunca clica em cadáver = crash nativo). Retorna `true` se abriu alguma → marca `did`. | Só o switch. Log `🎁 caixa <tipo> aberta`. | Sempre habilitado. |
| Auto-stash | CheckBox (switch, `AutoToggle`) | `📦 Auto-stash (move pro baú + organiza por grade)` | `engine.WantAutostash` (false) | — | Checked → `WantAutostash=true` **+ liga ACTk**. **Efeito (prioridade 2, todo tick):** `AutoStash.MoveAllToStash(keep, max=150)` resolve o singleton `ra`, lista slots de inventário ocupados + slots de baú livres e move em lote via `dispatcher.CommandStash(ra,[1,src,2,slot,1])` (INVENTORY→STASH). Retorna quantos moveu. **+ Efeito ocioso:** ver 3.3 (SortStep). | Só o switch. Log `📦 N itens movidos pro baú`. | Sempre habilitado. |
| Auto-fuse | CheckBox (switch, `AutoToggle`) | `⚗️ Auto-fuse (funde no cubo)` | `engine.WantAutofuse` (false) | — | Checked → `WantAutofuse=true` **+ liga ACTk**. **Efeito (prioridade 3, todo tick, NÃO gateado por !did):** `AutoFuse.DoSynth(keep)` faz **UMA** fusão: se algum tipo tem ≥9 de um grade ≤ teto, abre o cubo, seta Include-Stash, põe recipe Lv.65~80, seleciona o tipo, AUTO-FILL (cmd4), confere o grade real enfiado (fail-safe do teto), FUNDE (cmd5 → consome 9→1) e fecha. Grade do resultado é rolado no servidor. | Só o switch. Log `⚗️ Síntese: <tipo> <grade> (9 fundidos → 1 acima)`. | Sempre habilitado. |
| fundir até | ComboBox | `fundir até:` [10 grades] | `SelectedIndex = _synthGrade = 2` (**Rare**) | `_synthGrade` | SelectionChanged → `_synthGrade = SelectedIndex`; `PushFuseConfig()` empurra `engine.AutoFuse.MaxGrade = _synthGrade` (se atado). Teto: só funde grades `0..MaxGrade`. | Nada visual além da seleção. | Sempre selecionável; só empurra pro engine se `IsAttached`. |
| tipo Equip | CheckBox | `Equip` | marcado (`_synthTypes` contém 0) | `_synthTypes` | Checked → `_synthTypes.Add(0)`; Unchecked → `Remove(0)`; ambos chamam `PushFuseConfig()` → `engine.AutoFuse.Types = {..}`. Filtra quais `synth`-types (0=Gear/Equip) o fuse considera. | Só o próprio check. | Sempre. |
| tipo Acess | CheckBox | `Acess` | marcado (contém 1) | `_synthTypes` | idem, tipo **1** (Accessory). | — | Sempre. |
| tipo Mat | CheckBox | `Mat` | marcado (contém 2) | `_synthTypes` | idem, tipo **2** (Material). Material dispensa o filtro de nível (Lv≥61 não exigido). | — | Sempre. |

### 3.2 Como os filtros chegam no engine (`PushFuseConfig` / `Tick`)

- `PushFuseConfig()` roda em **3 momentos**: no `SelectionChanged` do combo, no Checked/Unchecked de cada
  tipo, e **a cada tick de 1 s** do `DispatcherTimer` da view (`Tick()`).
- Se `!_svc.IsAttached`, `PushFuseConfig` **retorna sem fazer nada** (não erra). Ao reatar, o próximo tick
  de 1 s re-sincroniza `MaxGrade`/`Types`.
- Faixa de grade (índice do combo): `0=Common,1=Uncommon,2=Rare,3=Legendary,4=Immortal,5=Arcana,
  6=Beyond,7=Celestial,8=Divine,9=Cosmic`. Regra do fuse: funde **do menor grade ATÉ o escolhido, nunca acima**.

### 3.3 SortStep (ordenação do baú — sob a flag do auto-stash)

Não é um controle separado; é o **passo 6** do `RunActions`, disparado só quando `WantAutostash && !did`:
`AutoStash.SortStep(2)` conserta a 1ª posição fora de ordem do baú com ≤2 moves STASH→STASH, packing
`common→uncommon→rare→…` nos slots de menor índice. **Um passo por tick** (amortizado).

### Chamadas de backend (parte A)

- `AutoBox.OpenAll(keep)` → `FindStageBoxes` + `IuwCount` + `dispatcher.Command(1, box)` (llx).
- `AutoStash.MoveAllToStash(keep,150)` → `ResolveRa` + `dispatcher.CommandStash(ra, moveReq[5])` (iw).
- `AutoStash.SortStep(2)` → moves STASH→STASH via `CommandStash`.
- `AutoFuse.DoSynth(keep)` + `AutoFuse.MaxGrade`/`Types` (setados por `PushFuseConfig`).

### Notas p/ redesign

- **Preserve a ordem `box→stash→fuse` sempre** e o gate `!did` só pra boss/evolve/sort. É o que evita
  starvation do auto-box.
- **Preserve o encadeamento auto-modo→ACTk** e a exceção do Auto-restart. Se a UI nova separar "proteção"
  de "automação", ligar um auto-modo ainda precisa garantir o ACTk ligado.
- **Nunca** clicar em `StageBox` destruída — mas isso é interno ao `OpenAll`; a UI só precisa ligar a flag.

---

## 4. Card **AUTOMAÇÃO** — parte B: modos ociosos (`WantAutoboss`/`WantEvolve`) — `StageAutomation`

**Propósito.** Modos de progressão/farm que **bloqueiam** (podem segurar a thread do loop durante a luta
do boss). Por isso só rodam quando o tick está ocioso (`!did`). Implementados em `StageAutomation.cs`.

### Tabela de CONTROLES — modos ociosos

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (comportamento EXATO) | O que REFLETE | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Auto-boss | CheckBox (switch, `AutoToggle`) | `🗡 Auto-boss (gasta soulstone no x-10 e volta)` | `engine.WantAutoboss` (false) | — | Checked → `WantAutoboss=true` **+ liga ACTk**. **Efeito (só ocioso):** `StageAutomation.AutoBoss(keep)` — ver 4.1. Um boss por chamada; gasta 1 soulstone. | Só o switch. Logs `🗡 auto-boss: …`. | Sempre habilitado. **Não** se auto-desliga. |
| Evolution | CheckBox (switch, `AutoToggle`) | `📈 Evolution (sobe 1 fase por vez até Torment 3-9, depois desliga)` | `engine.WantEvolve` (false) | `save.StageProgress()` | Checked → `WantEvolve=true` **+ liga ACTk**. **Efeito (só ocioso):** `StageAutomation.Evolve(keep)` — ver 4.2. **Auto-desliga** ao chegar em Torment 3-9. | O switch **se desmarca sozinho** quando o motor zera `WantEvolve` (ver 4.3). | Sempre habilitado. |

### 4.1 `AutoBoss(keep)` — gasta soulstone no x-10

- Guarda: se `sym.Get("jgd") == 0` (offsets de estágio ausentes) → loga e sai.
- Lê o inventário (`Inventory.ReadCounts`) e percorre os **pares na ordem Torment→Hell**:
  `BossPairs = [(soulstone 190004, boss 4310 = Torment 3-10), (190003, 3310 = Hell 3-10)]`.
- Para o 1º par com soulstone > 0 e `nav.CanEnter(boss) == 0` (Success): loga e chama `BossRun`.
- `BossRun`: guarda `volta = StageProgress().Cur` (de onde veio), `nav.EnterBoss(boss)` (par jgd+jgk),
  espera o desfecho com `WaitBossDone(boss, 600_000, keep)`.
  - **A volta é NATIVA** (o `jgd` grava o ponto de retorno `beyq`). Só intervém se o jogo **travar no boss**
    por >15 s → `nav.GoToStage(volta)`.
  - **A pedra só é COBRADA quando o boss MORRE** → entrar é reversível.
- `WaitBossDone`: fase 1 confirma que a entrada pegou (timeout 8 s); fase 2 compara a contagem de caixas
  `ACTBOSS` (`box.IuwCount(2)`) — se subiu, MATOU (retorna `true`); se saiu sem caixa, party morreu (`false`).

### 4.2 `Evolve(keep)` — sobe 1 fase por vez

- Lê `(mx, cur, wave) = save.StageProgress()`. Se `cur >= EvolveTarget (4309 = Torment 3-9)` →
  loga "climb completo", chama `DisableEvolve()` e retorna `false`.
- **PACING:** só avança quando a fase atual foi **limpa** — `info.Waves > 0 && wave >= info.Waves`, com
  anti-double-step (`cur == _lastNav || _lastNav == 0`). Sem isso viraria um "pulo lento".
- `next = info.Next`. Guardas: `0 < next <= 4309` e existe na tabela.
- Se `next` é **x-10** (`Type == 1`): checa `CanEnter`. Se precisa soulstone (`r==2`), loga "farmando até
  cair" e espera. Se ok, `EnterBossWait` (mata o boss) e vai **PRA FRENTE** (`Next` do boss) — **nunca
  volta** (senão re-entra o boss pra sempre).
- Se `next` é normal: `nav.GoToStage(next)`, `_lastNav = next`. Se `next >= 4309` → desliga o modo.
- **Alvo é `Next(cur)`**, nunca `min(max,4309)` — não teleporta (a aba Stages fixa `max=4310`, o que
  teleportaria).

### 4.3 Auto-desligamento da Evolução (reflexo na UI)

`Evolve` chama `DisableEvolve` (injetado no `Engine.Attach`: `() => WantEvolve = false`) ao chegar no topo.
A view **reflete isso** no seu `Tick()` de 1 s:

```
if switch "evolve".IsChecked == true  &&  !engine.WantEvolve:
        switch "evolve".IsChecked = false     ← desmarca sozinho
```

### Chamadas de backend (parte B)

- `StageAutomation.AutoBoss(keep)` / `.Evolve(keep)`.
- `Inventory.ReadCounts()` — {itemKey → qtd} (filtra 100000..999999); usado p/ contar soulstones.
- `StageNav.CanEnter(key)` → jgc: `0=Success,1=EndStage,2=NeedSoulStone,3=NeedChestSpace,4=Failed`.
- `StageNav.EnterBoss(key)` — par `jgd`(cmd13) + `jgk`(cmd12/Call). `StageNav.GoToStage(key)` — jgk.
- `AutoBox.IuwCount(2)` — caixas ACTBOSS (detecta a morte do boss).
- `SaveData.StageProgress()` — `(Max, Cur, Wave)`.

### Estados especiais

| Situação | Comportamento |
|---|---|
| `did == true` no tick | Boss/Evolve **não rodam** neste tick (gate `!did`). |
| Boss trava >15 s (fase não volta) | `BossRun` força `GoToStage(volta)`. |
| Entrada do boss não pega em 8 s | `WaitBossDone` retorna `null` → "entrada não pegou". |
| Usuário desliga o modo no meio da luta | `keep()` fica `false` → as esperas abortam na hora. |
| Evolução chega em Torment 3-9 | `WantEvolve=false` (via `DisableEvolve`); switch desmarca no próximo tick de 1 s. |

### Notas p/ redesign

- **Boss/Evolve BLOQUEIAM.** Se a UI nova mostrar "rodando…", saiba que a thread pode ficar 10 min numa
  luta. Não presuma resposta imediata.
- **Evolution auto-desliga** — o switch **precisa** refletir `WantEvolve` (senão fica marcado mentindo).
- **Auto-boss consome soulstone irreversivelmente só na morte do boss** — não é "grátis". Mantenha o texto
  avisando "gasta soulstone".

---

## 5. Valores FORÇADOS: `WantStats` (stats do player) e `WantStage` (StageData corrente)

**Propósito.** Escrever um valor e **mantê-lo** contra o recálculo do jogo: o AutomationLoop re-aplica os
dicionários TODO tick (`ApplyStats`/`ApplyStage`), porque uma escrita única seria sobrescrita.

### Estrutura visual atual (`BuildStats` / `BuildStageFields`)

Cada linha é um `Grid` de 4 colunas: **nome (190px) | valor [TextBox 86px] | atual (--) | [Aplicar]**.
Stats agrupados em `⚔ ATTACK / 🛡 DEFENSE / ✦ OTHER` (25 stats de `GameConstants.Stats`). Campos de fase =
todos os `GameConstants.StageFields`.

```
┌ STATS (jogador · leitura automática) ─────────────────────────┐
│  ⚔  ATTACK                                                     │
│  Attack Damage        [   ] 1234.5        [ Aplicar ]          │
│  Attack Speed         [   ] 1.20          [ Aplicar ]          │
│   …                                                            │
└────────────────────────────────────────────────────────────────┘
┌ CAMPOS DE FASE (StageData corrente · leitura automática) ─────┐
│  Act                  [   ] 3             [ Aplicar ]          │
│   …                                                            │
└────────────────────────────────────────────────────────────────┘
```

### Tabela de CONTROLES — valores forçados

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (comportamento EXATO) | O que REFLETE | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Campo de valor (stat) | TextBox | vazio (mostra o atual quando vazio) | vazio → preenchido com o valor lido | leitura de `Stats.ReadStats()` | Digitar: se a linha está **Active** (forçando), `TextChanged` chama `RebuildForcedStats(applyNow:true)` → re-escreve na hora. Parse: `double` invariante. | Coluna "atual" mostra o valor lido. Campo vazio é auto-preenchido pela leitura. | — |
| Aplicar / ✓ Forçado (stat) | Button | `Aplicar` ↔ `✓ Forçado` | `Aplicar` (inativo) | `row.Value.Text` | Click → `ToggleForceStat`: se **inativo** e valor válido → `Active=true`, botão vira **`✓ Forçado`** (estilo `Accent.Button`), `RebuildForcedStats(applyNow:true)` → escreve **NA HORA** (`Task.Run` → `Stats.ApplyStats`) **e** monta `engine.WantStats` (o loop segue forçando). Se **ativo** → `Active=false`, botão volta a `Aplicar`, remove a linha de `WantStats`. | O texto/cor do botão. | Sempre clicável; a escrita só ocorre `if (_svc.IsAttached)`. Valor inválido → log `stat '<n>': valor inválido`, nada muda. |
| Campo de valor (fase) | TextBox | vazio | vazio → preenchido | `Stats.ReadStage()` | idem stat, mas parse `int` invariante; `RebuildForcedStage`. | Coluna "atual". | — |
| Aplicar / ✓ Forçado (fase) | Button | `Aplicar` ↔ `✓ Forçado` | `Aplicar` | `row.Value.Text` | idem stat: `ToggleForceStage` → `engine.WantStage` + `Stats.ApplyStage` na hora. ⚠ Act/StageNo podem ser **rejeitados pelo servidor**. | Botão. | Sempre; escrita só se `IsAttached`. |

### Como o dict forçado é montado (assign atômico)

`RebuildForcedStats(applyNow)` varre **todas** as linhas ativas, monta um `Dictionary` novo e faz
`engine.WantStats = d` (troca a referência inteira — o loop lê por snapshot, sem race). Se `applyNow`,
dispara `Task.Run` que chama `Stats.ApplyStats(d)` imediatamente (fora do tick). Idem `RebuildForcedStage`.

### Comportamento vivo

- **Leitura automática a cada 1 s** (`DispatcherTimer` da view): `ReadStats()` e `ReadStage()` rodam em
  **background** (`Task.Run`), e no thread da UI atualizam a coluna "atual". **Campo vazio** recebe o valor
  lido (`if (IsNullOrWhiteSpace(Value.Text)) Value.Text = txt`).
- Enquanto uma linha está `Active`, o AutomationLoop re-escreve o valor a cada 250 ms.

### Estados especiais

| Situação | Comportamento |
|---|---|
| `!IsAttached` | `ReadStats`/`ReadStage` retornam cedo; coluna "atual" fica `--`. |
| Stat/campo não resolvido | Coluna "atual" = `--`; o campo de valor **não** é auto-preenchido. |
| Valor inválido ao clicar Aplicar | Log de erro; a linha **não** entra em `WantStats/WantStage`. |

### Chamadas de backend

- `StatEditor.ReadStats()` / `ApplyStats(dict)` — resolve Pstat por AOB→chain, lê/escreve float/double.
- `StatEditor.ReadStage()` / `ApplyStage(dict)` — resolve `StageData` sadio por AOB, lê/escreve int32.

### Notas p/ redesign

- **Preserve "escreve na hora + mantém forçado"** e o toggle `Aplicar ↔ ✓ Forçado`. O valor **precisa** ser
  re-aplicado todo tick, senão o jogo desfaz.
- **Troca de dicionário inteiro** (nunca mutar in-place) — é o que garante ausência de race com o loop.
- Mantenha o aviso de que **Act/StageNo podem ser rejeitados pelo servidor**.

---

## 6. Botão de desbloqueio do CUBO (`Save.SetCubeLevel`) — apoio

Não é uma flag `Want*` (é uma escrita única), mas é um controle da mesma view que dispara backend.

| Controle | Tipo | Label | Estado inicial | O que LÊ | Ao INTERAGIR | O que REFLETE | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Cubo → Lv.100 | Button (`Accent.Button`) | `🧊 Cubo → Lv.100` ↔ `🧊 Cubo já está Lv.100` | desabilitado até ler | `Save.CubeLevel()` (a cada 1 s, background) | Click → `Confirm(...)` (avisa que o jogo **fecha ~12 s** pelo anti-cheat mas persiste) → `Task.Run(Save.SetCubeLevel(100))`. | Label `cubo: Lv.N` / `cubo: Lv.N (máximo)` / `cubo: jogo fechado`. | **TRAVA quando já é Lv.100** (`_cubeBtn.IsEnabled = cube is int && !max`). Desabilitado se `!IsAttached`. |

> Estágios ficam na **aba Stages** (outro doc). O card DESBLOQUEIOS aqui só tem o cubo.

---

## 7. Card **AUTOMAÇÃO** — Auto-restart: `WatchdogService` (`WantWatchdog`)

**Propósito.** Resiliência: quando o jogo fecha por completo (crash, anti-cheat, fechamento manual),
reabre sozinho com **start limpo**, fecha o popup de OFFLINE REWARDS, religa tudo e reentra no estágio.

### Controle (`TrainerView`, `Toggle` — NÃO `AutoToggle`)

| Controle | Tipo | Label | Estado inicial | O que LÊ | Ao INTERAGIR | O que REFLETE | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Auto-restart | CheckBox (switch) | `🛡 Auto-restart (jogo fechou? reabre via Steam + reaplica tudo)` | `engine.WantWatchdog` (false) | — | Checked → `engine.WantWatchdog = true`. **Não liga o ACTk** (única diferença dos outros auto-modos). O `WatchdogService.RunAsync` (task própria, iniciada no `EngineService.Start`) observa essa flag. | Só o switch. Logs `🛡 watchdog: …`. | Sempre habilitado. |

### Fluxo do `WatchdogService.RunAsync` (fiel ao `_watchdog_loop` do Python)

```
loop:
  ├─ !WantWatchdog          → WdHold=false; delay 1000; continue
  ├─ IsAttached (jogo vivo) → WdHold=false; rastreia lastStage = StageProgress().Cur (>0); delay 2000
  └─ JOGO FECHOU:
        1) WdHold = true                       ← START LIMPO (config segue nos Want*, nada aplicado no boot)
        2) espera o exe SUMIR                   (poll 500ms, teto 240) + delay 1000
        3) svc.LaunchGame()                     ← steam://run/3678970
        4) espera SUBIR                          (Startup=120 iterações × 2000ms); quem re-attacha é o ConnectLoop
              └─ não subiu → "não subiu — tentando de novo"; WdHold=false; continue
           delay 3000  (deixa carregar)
        5) CloseOfflinePopup(120s)              ← OCR do botão "Close" (tolerante; erro = prossegue)
        6) reCur = StageProgress().Cur (>0)     senão fallback = lastStage
        7) WdHold = false                       ← RELIGA tudo (o AutomationLoop re-aplica ACTk/God/stats)
           delay 3000  (deixa re-aplicar + instalar o dispatcher)
        8) ReEnter(reCur)                        ← boss? EnterBoss : GoToStage
```

Em **qualquer aborto** (usuário desliga `WantWatchdog` no meio, exceção): `WdHold` volta a `false` pra não
deixar o AutomationLoop congelado.

### `ReEnter(cur)` e o popup

- `ReEnter`: consulta `StageNav.StageTable()`, `isBoss = info.Type == 1` → `EnterBoss(cur)` (par jgd+jgk)
  ou `GoToStage(cur)` (jgk). Loga o resultado.
- `CloseOfflinePopup`: captura a janela (`Native.CaptureWindowScaledBgra`, escala 2.5×), roda OCR
  (`OcrEngine`). Só age se o texto contém `offline`/`last login`/`reward` **e** acha a palavra `close` →
  clica no centro (`Native.ClickReal`), confere de novo, 2ª tentativa. Se OCR indisponível (`_ocr==null`),
  **pula silenciosamente**.

### Comportamento vivo

- Jogo vivo: acorda a cada **2 s** (rastreia `lastStage`). Watchdog desligado: a cada **1 s**.
- Reboot: várias esperas encadeadas (500 ms / 1 s / 2 s / 3 s) — não é instantâneo (~24 s medidos).

### Estados especiais

| Situação | Comportamento |
|---|---|
| `WantWatchdog == false` | `WdHold=false` garantido; só polling de 1 s. |
| Jogo não sobe em ~4 min | Loga e recomeça o ciclo. |
| OCR indisponível / popup não achado | Prossegue sem fechar (tolerante). |
| `StageProgress` falha na releitura | Usa `lastStage` (último estágio vivo). |
| Exceção em qualquer passo | `WdHold=false`, delay 3 s, segue. |

### Chamadas de backend / serviços

- `EngineService.LaunchGame()` → `steam://run/3678970`.
- `SaveData.StageProgress()`, `StageNav.StageTable()/EnterBoss()/GoToStage()`.
- `Native.CaptureWindowScaledBgra`, `Native.ClickReal`, `OcrEngine` (Windows OCR).

### Notas p/ redesign

- **`WdHold` é o contrato central** entre Watchdog e AutomationLoop: durante o reboot, o loop não aplica
  nada. Se a UI nova adicionar mais automações, elas **herdam** esse gate automaticamente (é no loop).
- **Auto-restart não liga o ACTk sozinho** — decisão deliberada (o start é "limpo"). Mantenha.
- Quem **re-attacha** é o ConnectLoop, não o Watchdog. O Watchdog só observa `IsAttached`.

---

## 8. `RealDispatcher` — o transporte main-thread (por baixo de tudo)

**Propósito.** Muitas ações (abrir caixa, mover item, fundir, entrar em fase) são chamadas Unity que
**crasham** se feitas de outra thread. O dispatcher instala um *code-cave* que faz hook no
`InputManager.Update` e, a cada frame, consome UM comando numerado escrito pelo bot e chama a função do jogo
**na main-thread**. Instala preguiçosamente (na 1ª ação). Nenhum controle da UI mexe nele direto.

### Comandos usados pelos auto-modos

| cmd | Função do jogo | Usado por |
|---|---|---|
| 1 | `llx` (abrir StageBox) | Auto-box |
| 2 | `iw` (mover item, `MoveRequest`+fake delegate) | Auto-stash / SortStep |
| 3 | `ilo` (tipo de síntese) | Auto-fuse |
| 4 | `ipu` (auto-fill do cubo) | Auto-fuse |
| 5 | `imx` (síntese — consome 9→1) | Auto-fuse |
| 6 | `inf` (setar recipe/tier) | Auto-fuse |
| 12 | `Call` arbitrário `[dFUNC](argP,argI)` | StageNav (jgk/jgc) |
| 13 | `jgd` (reserva soulstone + ponto de retorno) | Auto-boss / Evolve (EnterBoss) |

### Notas p/ redesign

- O redesign **não toca** nisto — mas saiba que se o dispatcher não instalou (offsets `upd`/`llx`
  faltando), os auto-modos ligam mas **não produzem efeito visível**. A UI deve tolerar "flag ligada,
  nada acontecendo" sem travar.
- O `Counter()` do dispatcher é prova de vida do hook; não há controle exposto pra ele hoje (candidato a
  indicador de status no redesign).

---

## 9. Cores/estilo usados hoje (`Theme/Dark.xaml`) e significado

| Recurso | Hex | Onde/Significado |
|---|---|---|
| `Bg` | `#050506` | Fundo da view. |
| `Card` / `Card.Border` | `#16181C` | Fundo dos cards (PROTEÇÃO/AUTOMAÇÃO/STATS/…). Borda `Stroke #2B2E34`, raio 10. |
| `Card2` | `#212429` | Fundo do botão padrão e do "track" do switch **desligado**. |
| `Acc` (accent) | `#FF7A18` (laranja) | Switch **ligado** (track), títulos de grupo de stats, `Accent.Button` (Salvar/Cubo/**✓ Forçado**), caret. **= ativo/forçado.** |
| `AccH` | `#FF9440` | Hover do accent. |
| `AccTxt` | `#0A0A0A` | Texto sobre accent + "knob" do switch ligado. |
| `Fg` | `#FFFFFF` | Texto primário, labels, valores. |
| `Sub` | `#9AA0AB` (mono) | Rótulos secundários ("fundir até:", "tipos:"), coluna "atual". |
| `Subtle` | `#666B74` | Hints de 10px, "knob" do switch **desligado**. |
| `Stroke` / `Stroke2` | `#2B2E34` / `#3D4149` | Bordas de card / borda e hover do botão. |
| `Red` | `#F87171` | Definido no tema (erro/desconectado) — **não** usado explicitamente nesta view hoje. |
| `Amber` | `#FBBF24` | Definido no tema — não usado nesta view hoje. |

Padrões visuais chave:
- **Switch ligado = track laranja + knob à direita**; desligado = track cinza + knob à esquerda.
- **Botão `✓ Forçado` = laranja (`Accent.Button`)**; `Aplicar` = cinza (`Card2`). O laranja = "valor sendo
  forçado a cada tick".
- Botão desabilitado = `Opacity 0.45` (ex.: Cubo travado em Lv.100, ou desatado).

---

## 10. Notas p/ o redesign — o que é ESSENCIAL preservar (função, não visual)

1. **Um clique = uma flag.** Controles só setam `Want*`/dicionários no `Engine`. Nunca chame o jogo do
   handler da UI. O `AutomationLoop` é o único a agir.
2. **Ordem `box→stash→fuse` SEMPRE; `boss/evolve/sort` só ocioso (`!did`).** É o antídoto contra
   starvation do auto-box.
3. **`fuse` NÃO é gateado por `!did`** (roda mesmo com box/stash tendo agido no tick).
4. **Auto-modo liga o ACTk junto** (exceto Auto-restart). Preserve esse acoplamento em qualquer novo layout.
5. **Evolution auto-desliga** ao chegar em Torment 3-9 (`WantEvolve=false`); o switch **precisa** refletir
   isso (a view re-checa a cada 1 s).
6. **Cheats e valores forçados re-aplicam TODO tick** (idempotente, self-heal). Não debounce.
7. **`WantStats`/`WantStage` trocam o dicionário inteiro** (assign atômico). Nunca mutar in-place.
8. **`WdHold` congela a aplicação durante o reboot.** Toda automação nova herda esse gate por rodar no loop.
9. **Auto-boss gasta soulstone (só na morte do boss); boss/evolve BLOQUEIAM** a thread até 10 min. A UI
   não pode presumir resposta imediata.
10. **Armadilhas conhecidas:** clicar em `StageBox` destruída = crash nativo (interno ao `OpenAll`, mas não
    contorne); escrever `ObscuredInt` (cubo/maxstage) fecha o jogo ~12 s mas **persiste** (avise no confirm);
    Act/StageNo forçados podem ser rejeitados pelo servidor; se o dispatcher não instalou, os auto-modos
    ligam sem efeito visível — a UI deve tolerar sem travar.
11. **Guardas de jogo fechado:** toda leitura/escrita da view checa `_svc.IsAttached`; sem isso, mostra
    `--` / "jogo fechado" e desabilita botões. Preserve os três estados: atado / desatado / reiniciando.
