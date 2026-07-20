# Aba TRAINER — documentação de layout e comportamento

> Fonte da verdade da **função** (não do visual atual). Uma IA/pessoa vai redesenhar o layout usando este
> documento; nenhum comportamento pode ser adivinhado.
>
> Código-fonte: `src/TbhBot.App/Views/TrainerView.cs` (view inteira, construída em C# code-behind, sem XAML
> por-tela). Tema: `src/TbhBot.App/Theme/Dark.xaml`. Backend: `src/TbhBot.Core/` (Engine + Game/*).
> Persistência de profiles: `src/TbhBot.App/Services/ProfileStore.cs`.

---

## 0. Visão geral da aba

**Propósito.** Painel-mãe do bot: liga/desliga proteção e automações, edita stats do jogador e campos da
fase corrente **em tempo real**, salva/carrega perfis e desbloqueia o nível do cubo. É a maior aba. Toda
leitura de memória roda em background; um timer de 1 s mantém os valores atuais e os labels vivos.

**Layout macro.** Um único `ScrollViewer` vertical (barra horizontal desabilitada) contendo um `StackPanel`
com margem 16. Dentro dele, **6 cards** empilhados verticalmente, nesta ordem:

```
┌─ ScrollViewer (vertical) ────────────────────────────────┐
│  ┌───────────────────────────────────────────────────┐   │
│  │ PROTEÇÃO                                           │   │  card 1
│  │   • ACTk Bypass          (toggle)                  │   │
│  │   • God Mode             (toggle)                  │   │
│  └───────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────┐   │
│  │ AUTOMAÇÃO                                          │   │  card 2
│  │   • 🎁 Auto-box          (auto-toggle)             │   │
│  │   • 📦 Auto-stash        (auto-toggle)             │   │
│  │   • ⚗️ Auto-fuse         (auto-toggle)             │   │
│  │       fundir até: [combo grade v]                  │   │
│  │       tipos: ☑Equip ☑Acess ☑Mat                   │   │
│  │       (nota de rodapé cinza)                       │   │
│  │   • 🗡 Auto-boss         (auto-toggle)             │   │
│  │   • 📈 Evolution         (auto-toggle)             │   │
│  │   • 🛡 Auto-restart      (toggle simples)          │   │
│  └───────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────┐   │
│  │ STATS (jogador · leitura automática)               │   │  card 3
│  │   ⚔ ATTACK    (header)                             │   │
│  │     Attack Damage   [valor][atual]    [Aplicar]    │   │
│  │     ... (11 linhas)                                 │   │
│  │   🛡 DEFENSE   (header)                             │   │
│  │     ... (8 linhas)                                  │   │
│  │   ✦ OTHER      (header)                            │   │
│  │     ... (6 linhas)                                  │   │
│  │   (nota de rodapé cinza)                            │   │
│  └───────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────┐   │
│  │ CAMPOS DE FASE (StageData corrente · leitura auto) │   │  card 4
│  │     Act            [valor][atual]     [Aplicar]    │   │
│  │     ... (18 linhas)                                 │   │
│  │   (nota de rodapé cinza)                            │   │
│  └───────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────┐   │
│  │ PROFILES                                           │   │  card 5
│  │   [combo editável ▾] [💾Salvar][📂Carregar][🗑Apagar] │   │
│  │   (nota de rodapé cinza)                            │   │
│  └───────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────┐   │
│  │ DESBLOQUEIOS                                       │   │  card 6
│  │   cubo: Lv.N            (label mono)                │   │
│  │   [🧊 Cubo → Lv.100]    (botão accent)             │   │
│  │   (nota de rodapé cinza)                            │   │
│  └───────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

Cada card é um `Border` estilo `Card.Border` com título em cima (SemiBold 14, `Fg`) e o corpo abaixo,
construído pelo helper `Card(title, body)`. Margem inferior 12 entre cards.

**Contagem total de controles interativos documentados: 103**
(2 proteção + 10 automação + 25×2 stats + 18×2 campos de fase + 4 profiles + 1 cubo).

---

## 1. Card PROTEÇÃO

**Propósito.** Liga os dois cheats de patch de código que não fazem "ação" (só ficam ativos): ACTk Bypass
(neutraliza o detector anti-cheat) e God Mode (jogador não toma dano). São reaplicados todo tick pelo
`AutomationLoop` (idempotente / self-heal).

**Estrutura visual.** Card `PROTEÇÃO` → corpo = `StackPanel` vertical com 2 toggles (CheckBox estilizado
como switch). Sem colunas, sem sub-agrupamento.

### Tabela de controles

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte) | Ao INTERAGIR (comportamento exato) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| ACTk Bypass | CheckBox-switch (`Toggle`, key `"actk"`) | `ACTk Bypass` | `_svc.Engine.WantActk` (default `false`) | flag `Engine.WantActk` | Checked→`WantActk=true`; Unchecked→`WantActk=false`. O loop chama `Cheats.SetActk(WantActk)` a cada tick (NOP→`ret` nos RVAs `Ynj`). | Só o próprio estado do switch. Também é **alvo do encadeamento** dos auto-modos (é ligado por eles). | Sempre habilitado (não há guarda de attach; só tem efeito real com jogo conectado). |
| God Mode | CheckBox-switch (`Toggle`, key `"god"`) | `God Mode` | `_svc.Engine.WantGodmode` (default `false`) | flag `Engine.WantGodmode` | Checked→`WantGodmode=true`; Unchecked→`false`. Loop chama `Cheats.SetGodmode()` (AOB godmode: prólogo `0x57`→`0xC3` ret; off restaura `0x57`). | Só o próprio estado. | Sempre habilitado. |

**Nota:** os toggles de PROTEÇÃO **não** disparam o encadeamento; só os auto-modos ligam o ACTk. Desligar
um auto-modo **não** desliga o ACTk.

---

## 2. Card AUTOMAÇÃO

**Propósito.** Liga as automações de ação (abrir caixa, mover pro baú, fundir no cubo) e os modos de
progressão/resiliência (auto-boss, evolução, auto-restart), além dos **filtros do auto-fuse**. Toda flag
`Want*` é lida pelo `AutomationLoop` a cada 250 ms; a ordem de prioridade no loop é box → stash → fuse →
(ocioso:) auto-boss → evolução → ordenação do baú.

**Estrutura visual.** Card `AUTOMAÇÃO` → `StackPanel` vertical:
1. 3 auto-toggles (Auto-box, Auto-stash, Auto-fuse);
2. bloco de **filtros do fuse** indentado (margem-esquerda 24): linha "fundir até:" + combo de grade;
   linha "tipos:" + 3 checkboxes (Equip/Acess/Mat); nota de rodapé;
3. 2 auto-toggles (Auto-boss, Evolution) + 1 toggle simples (Auto-restart).

Os filtros ficam visualmente **entre** o Auto-fuse e os modos de progressão, mas lógicamente pertencem só
ao Auto-fuse.

### 2.1 O ENCADEAMENTO (auto-modo → liga ACTk)

`AutoToggle` é um `Toggle` normal com um handler extra em `Checked`: **ao LIGAR** qualquer auto-modo, se o
switch do ACTk **não** estiver marcado, ele marca `_actkCheck.IsChecked = true` (o que dispara o `Checked`
do ACTk → `WantActk=true`) e loga `"ACTk Bypass ligado junto (o auto-modo usa o hook)"`. Racional: todo
auto-modo usa o hook do dispatcher, detectável pelo anti-cheat → o ACTk precisa estar ativo.

- Usam `AutoToggle` (encadeiam ACTk): **Auto-box, Auto-stash, Auto-fuse, Auto-boss, Evolution**.
- Usa `Toggle` simples (NÃO encadeia): **Auto-restart (watchdog)**.
- O encadeamento **só liga**; desligar o auto-modo não desliga o ACTk. Ligar via Profile também dispara o
  encadeamento (é o mesmo `IsChecked=true`).

### Tabela de controles

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE/atualiza | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Auto-box | AutoToggle (key `"autobox"`) | `🎁 Auto-box (abre caixas)` | `Engine.WantAutobox` (false) | flag | Checked→`WantAutobox=true` **+ liga ACTk**; Unchecked→false. Loop: `AutoBox.OpenAll(...)` abre StageBox vivas via dispatcher main-thread. | próprio estado + marca ACTk | Sempre habilitado |
| Auto-stash | AutoToggle (key `"autostash"`) | `📦 Auto-stash (move pro baú + organiza por grade)` | `Engine.WantAutostash` (false) | flag | Checked→`WantAutostash=true` **+ liga ACTk**; Unchecked→false. Loop: `AutoStash.MoveAllToStash(...)` + `SortStep(2)` (1 move/tick quando ocioso). | próprio estado + marca ACTk | Sempre habilitado |
| Auto-fuse | AutoToggle (key `"autofuse"`) | `⚗️ Auto-fuse (funde no cubo)` | `Engine.WantAutofuse` (false) | flag | Checked→`WantAutofuse=true` **+ liga ACTk**; Unchecked→false. Loop: `AutoFuse.DoSynth(...)` (1 fusão/tick, consome 9→1, só Lv.65~80). | próprio estado + marca ACTk | Sempre habilitado |
| fundir até | ComboBox (`_synthCombo`) | itens = `GradeNames` (Common..Cosmic, 10) | `SelectedIndex = _synthGrade` = **2 (Rare)** | campo `_synthGrade` | `SelectionChanged`→`_synthGrade = SelectedIndex; PushFuseConfig()`. Empurra `Engine.AutoFuse.MaxGrade` (teto de grade a fundir). | `_synthGrade`, e `AutoFuse.MaxGrade` quando attached | Sempre habilitado (mas `PushFuseConfig` só escreve se `IsAttached`) |
| Equip | CheckBox (`_typeChecks[0]`) | `Equip` | marcado (`_synthTypes` contém 0) | set `_synthTypes` | Checked→`_synthTypes.Add(0)`; Unchecked→`Remove(0)`; ambos chamam `PushFuseConfig()`→`AutoFuse.Types`. | `_synthTypes` + `AutoFuse.Types` | Sempre |
| Acess | CheckBox (`_typeChecks[1]`) | `Acess` | marcado (contém 1) | `_synthTypes` | Add/Remove 1 + `PushFuseConfig()` | idem | Sempre |
| Mat | CheckBox (`_typeChecks[2]`) | `Mat` | marcado (contém 2) | `_synthTypes` | Add/Remove 2 + `PushFuseConfig()` | idem | Sempre |
| Auto-boss | AutoToggle (key `"autoboss"`) | `🗡 Auto-boss (gasta soulstone no x-10 e volta)` | `Engine.WantAutoboss` (false) | flag | Checked→`WantAutoboss=true` **+ liga ACTk**; Unchecked→false. Loop (só quando ocioso `!did`): `StageAutomation.AutoBoss(...)`. | próprio estado + marca ACTk | Sempre |
| Evolution | AutoToggle (key `"evolve"`) | `📈 Evolution (sobe 1 fase por vez até Torment 3-9, depois desliga)` | `Engine.WantEvolve` (false) | flag | Checked→`WantEvolve=true` **+ liga ACTk**; Unchecked→false. Loop (ocioso): `StageAutomation.Evolve(...)`. **Auto-desliga** ao chegar em Torment 3-9 (ver §2.2). | próprio estado + marca ACTk | Sempre |
| Auto-restart | Toggle simples (key `"watchdog"`) | `🛡 Auto-restart (jogo fechou? reabre via Steam + reaplica tudo)` | `Engine.WantWatchdog` (false) | flag | Checked→`WantWatchdog=true`; Unchecked→false. **NÃO liga ACTk.** O `WatchdogService` reabre via `steam://run/3678970` e reaplica tudo. | próprio estado | Sempre |

**Nota de rodapé (texto fixo, cor `Subtle` 10px):**
`"funde do grade menor ATÉ o escolhido (nunca acima), só no Lv.65~80."`

### 2.2 Reconcile da Evolution (auto-desliga)

A evolução se **auto-desliga** no backend: `StageAutomation.DisableEvolve` (ligado a `() => WantEvolve =
false`) é invocado quando o climb chega em Torment 3-9. O switch da UI **não sabe** disso sozinho, então
o **timer (1 s)** reconcilia:

```csharp
if (_switches["evolve"].IsChecked == true && !_svc.Engine.WantEvolve)
    _switches["evolve"].IsChecked = false;   // reflete o desligamento vindo do engine
```

Ou seja: o usuário vê o switch de Evolution **desmarcar sozinho** ~1 s depois de o bot completar o climb.

---

## 3. Card STATS (jogador · leitura automática)

**Propósito.** Editor dos 25 stats do jogador (Pstat). Cada linha lê o valor atual continuamente e permite
**forçar** um valor: clica Aplicar → escreve NA HORA + o loop mantém forçado a cada tick (o jogo recalcula
e sobrescreveria uma escrita única, por isso é re-aplicado).

**Estrutura visual.** Card → `StackPanel`. Os 25 stats são agrupados em 3 seções via `StatGroups`, cada
seção tem um **header** (cor `Acc`, SemiBold 11) seguido das linhas:

- `⚔  ATTACK` — 11 stats: Attack Damage, Attack Speed, Critical Chance, Critical Damage, Cooldown
  Reduction, Cast Speed, Physical Damage, Fire Damage, Cold Damage, Lightning Damage, Chaos Damage.
- `🛡  DEFENSE` — 8 stats: Max Hp, Armor, Dodge Chance, Block Chance, All Element Resistance, Hp Regen /Sec,
  Dmg Absorption, Dmg Reduction.
- `✦  OTHER` — 6 stats: Movement Speed, Area of Effect %, Area of Effect Damage, Add HP/Kill, Life Leech,
  Skill Heal.

Cada **linha** é um `Grid` de 4 colunas (`BuildRowGrid`):

```
| nome (190px)          | valor (86px) | atual (*)      | [Aplicar] (auto) |
| Attack Damage         | [ 123.5 ]    | 123.5          | [ Aplicar ]      |
```

- Col 0 (190): `TextBlock` nome do stat (cor `Fg`).
- Col 1 (86): `TextBox` de valor (mono 11, largura 76). Campo de entrada.
- Col 2 (*): `TextBlock` valor **atual** lido (cor `Sub`, mono 11); começa `"--"`.
- Col 3 (auto): `Button` Aplicar.

### Tabela de controles (padrão que se repete por linha — 25 linhas × 2 controles)

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE/atualiza | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Valor (por stat) | TextBox (`row.Value`) | vazio → preenchido com valor atual no 1º read | vazio | preenchido por `ReadStats()` quando vazio | `TextChanged`: **se a linha estiver Active** → `RebuildForcedStats(applyNow:true)` (re-escreve na hora e atualiza `WantStats`). Se inativa, digitar não escreve nada (só arma o botão). | valor forçado quando ativa | Sempre editável |
| Aplicar (por stat) | Button (`row.Apply`) | `Aplicar` / (ativo) `✓ Forçado` | `Aplicar`, estilo default (inativo) | estado `row.Active` | Clica → `ToggleForceStat(row)`: **se inativa** → valida `double.TryParse` (invalido → log `"stat '<nome>': valor inválido"`, aborta); senão `Active=true`, botão vira `✓ Forçado` (estilo `Accent.Button`) e `RebuildForcedStats(applyNow:true)` (escreve JÁ via `Task.Run`→`Stats.ApplyStats`). **Se já ativa** → `Active=false`, botão volta a `Aplicar`, `RebuildForcedStats(applyNow:false)` (remove do dict forçado; o loop para de re-escrever). | `Engine.WantStats` (dict de forçados), texto/estilo do botão | Sempre clicável (mas a escrita só ocorre se `_svc.IsAttached`) |

**`RebuildForcedStats`**: reconstrói `Engine.WantStats` a partir de **todas** as linhas ativas com valor
parseável (assign atômico da referência inteira, para não correr com o loop). Se `applyNow && count>0`,
dispara `Stats.ApplyStats(d)` em `Task.Run` (imediato). O `AutomationLoop` reescreve `WantStats` todo tick.

**Nota de rodapé (`Subtle` 10px):**
`"digite o valor e clique Aplicar → escreve NA HORA e mantém forçado (o jogo recalcularia uma escrita
única). Clique de novo em ✓ Forçado para parar. Campo vazio mostra o valor atual."`

**Detalhes de tipo/precisão:** `GameConstants.Stats` guarda `(offset, tipo)` — `'f'`=float, `'d'`=double
(ex.: Cast Speed, Hp Regen /Sec, Area of Effect %/Damage, Skill Heal são double). Leitura formata com
`"0.###"`. Entrada parseada como `double` (`InvariantCulture`).

---

## 4. Card CAMPOS DE FASE (StageData corrente · leitura automática)

**Propósito.** Mesmíssima mecânica dos stats, porém sobre os 18 campos **inteiros** do `StageData` da fase
atual (via `ApplyStage`/`ReadStage`). Permite forjar drops, multiplicadores de boss, soulstone etc.

**Estrutura visual.** Card → `StackPanel` sem sub-agrupamento (não há headers de grupo). Uma linha por
campo, no mesmo `Grid` de 4 colunas do card STATS. Campos, em ordem de `GameConstants.StageFields`:
`Act, StageNo, StageLevel, WaveAmount, WaveMonsterAmount, MonsterDropItemKey, FirstClearDropKey,
MonsterDropItemRate, BossDropItemRate, BossDropItemKey, BossMonsterKey, BossDamageMultiplier,
BossGoldMultiplier, BossExpMultiplier, BossHpMultiplier, BossScale, SoulStoneItemKey, SoulStoneAmount`.

### Tabela de controles (18 linhas × 2 controles)

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE/atualiza | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Valor (por campo) | TextBox (`row.Value`) | vazio→preenchido no read | vazio | `ReadStage()` preenche quando vazio | `TextChanged`: se Active → `RebuildForcedStage(applyNow:true)`. | valor forçado (int) | Sempre editável |
| Aplicar (por campo) | Button (`row.Apply`) | `Aplicar` / `✓ Forçado` | `Aplicar` | `row.Active` | Clica → `ToggleForceStage(row)`: valida `int.TryParse` (invalido → log `"campo '<nome>': valor inválido"`, aborta); ativa/desativa igual aos stats; escreve via `Stats.ApplyStage`. | `Engine.WantStage`, texto/estilo do botão | Sempre clicável (escrita só com `IsAttached`) |

**`RebuildForcedStage`**: constrói `Engine.WantStage` (dict `string→int`) das linhas ativas; `applyNow`
dispara `Stats.ApplyStage(d)` em `Task.Run`. Loop re-aplica todo tick.

**Nota de rodapé (`Subtle` 10px):**
`"digite e clique Aplicar → escreve NA HORA e mantém forçado. Clique em ✓ Forçado para parar. ⚠ Act/StageNo
podem ser rejeitados pelo servidor."`

---

## 5. Card PROFILES

**Propósito.** Salvar/carregar/apagar perfis nomeados que capturam **switches + filtros do fuse +
stats/campos forçados**. Persistido em `%APPDATA%/tbh_bot/profiles.json` (`{nome: Profile}`).

**Estrutura visual.** Card → `StackPanel`; primeira linha = `StackPanel` horizontal (`bar`) com um combo
editável (200px) + 3 botões; abaixo, nota de rodapé.

### Tabela de controles

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE/atualiza | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| Nome do profile | ComboBox editável (`_profCombo`, `IsEditable=true`) | placeholder vazio; itens = nomes salvos ordenados | itens de `ProfileStore.Load().Keys` ordenados; `Text` = nome digitado/selecionado | `.Text` (nome digitado) e a lista salva | digitar = novo nome; selecionar = escolhe existente. Não dispara ação sozinho. | preenchido por `RefreshProfileCombo` | Sempre |
| 💾 Salvar | Button accent (`Accent.Button`) | `💾 Salvar` | — | `_profCombo.Text` + estado atual da UI | `SaveProfile()`: nome vazio → log `"digite um nome pro profile primeiro"`; senão `Capture()` (ver abaixo) → `all[nome]=profile` → `ProfileStore.Save` → `RefreshProfileCombo(nome)` → log `"profile '<nome>' salvo ✓"`. | arquivo json + combo | Sempre |
| 📂 Carregar | Button default | `📂 Carregar` | — | `ProfileStore.Load()` | `LoadProfile()`: nome inexistente → log `"profile '<nome>' não existe"`; senão `Apply(profile)` (ver abaixo) → log `"profile '<nome>' carregado ✓"`. | switches, combo, checkboxes de tipo, linhas de stat/stage (e via handlers, o Engine) | Sempre |
| 🗑 Apagar | Button default | `🗑 Apagar` | — | `ProfileStore.Load()` | `DeleteProfile()`: se removeu → `Save` + `RefreshProfileCombo("")` + limpa `Text` + log `"profile '<nome>' apagado"`; senão log `"'<nome>' não existe"`. | arquivo json + combo | Sempre |

**`Capture()` (o que um Profile guarda):**
- `FuseGrade = _synthGrade`; `FuseTypes = _synthTypes`;
- `Switches[key] = cb.IsChecked` para **todos** os 8 switches (`actk, god, autobox, autostash, autofuse,
  autoboss, evolve, watchdog`);
- `Stats[nome] = valor` **só das linhas de stat ativas** com valor parseável (double);
- `Stage[nome] = valor` **só dos campos de fase ativos** (int).

**`Apply(profile)` (o que carregar faz, na ordem):**
1. Para cada switch presente no profile → `cb.IsChecked = on`. **Isso dispara os handlers** → seta as flags
   `Want*` no Engine; e se ligar um auto-modo, **dispara o encadeamento ACTk** (§2.1).
2. `_synthCombo.SelectedIndex = FuseGrade` (→ `PushFuseConfig`); checkboxes de tipo marcam conforme
   `FuseTypes`.
3. Stats: cada linha vira `Active = (profile tem esse stat)`, preenche o valor, atualiza o botão; depois
   `RebuildForcedStats(applyNow:true)` (aplica tudo).
4. Stage: idem com `RebuildForcedStage(applyNow:true)`.

**Nota de rodapé (`Subtle` 10px):**
`"salva switches + filtros do fuse + stats/campos forçados. Guardado em %APPDATA%/tbh_bot/profiles.json."`

---

## 6. Card DESBLOQUEIOS

**Propósito.** Só o **cubo** (os estágios ficam na aba Stages). Um botão eleva o nível **runtime** do cubo
para 100, o que libera os tiers altos de recipe. O botão **trava** quando o cubo já está Lv.100.

**Estrutura visual.** Card → `StackPanel`: label mono do estado do cubo (`_cubeLabel`), botão accent, nota
de rodapé.

### Tabela de controles

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ | Ao INTERAGIR (exato) | O que REFLETE/atualiza | Habilita/Desabilita |
|---|---|---|---|---|---|---|---|
| (label cubo) | TextBlock mono (`_cubeLabel`) — **não interativo** | `cubo: --` | — | `Save.CubeLevel()` a cada tick | — | `cubo: Lv.N` / `cubo: Lv.N (máximo)` / `cubo: --` / `cubo: jogo fechado` | — |
| 🧊 Cubo → Lv.100 | Button accent (`_cubeBtn`, `Accent.Button`) | `🧊 Cubo → Lv.100` (ou `🧊 Cubo já está Lv.100`) | conteúdo `🧊 Cubo → Lv.100`, habilitação decidida no 1º `RefreshUnlocks` | `Save.CubeLevel()` | `DoCube()`: se `!IsAttached` aborta; abre `MessageBox` de confirmação (Yes/No, aviso). Se Yes → `Task.Run` `Save.SetCubeLevel(100)`. | via `RefreshUnlocks` (label + habilitação + texto do botão) | **Desabilita** quando `!IsAttached` OU `cube >= 100`. Habilita só com `cube` lido e `< 100`. |

**MessageBox de confirmação (`Confirm`):** título `"Confirmar"`, ícone Warning, botões Yes/No, texto:
`"Elevar o nível do cubo para 100.\n\nO jogo fecha ~12s pelo anti-cheat — reabra e o valor fica salvo.
Continuar?"`.

**`RefreshUnlocks` (todo tick):**
- `!IsAttached` → label `"cubo: jogo fechado"`, botão desabilitado (retorna cedo, sem ler).
- attached → lê `CubeLevel()` em background; no thread da UI: `max = cube >= 100`;
  - label = `"cubo: Lv.{c} (máximo)"` se max, `"cubo: Lv.{c}"` se não, `"cubo: --"` se `null`;
  - `_cubeBtn.IsEnabled = (cube is int) && !max`;
  - conteúdo = `"🧊 Cubo já está Lv.100"` se max, senão `"🧊 Cubo → Lv.100"`.

**Nota de rodapé (`Subtle` 10px):** `"os estágios ficam na aba Stages."`

---

## 7. Comportamento vivo (timers / auto-refresh)

- **Timer principal: `DispatcherTimer` de 1 s** (`_timer`, `Interval = 1s`).
  - Iniciado em `Loaded` (com um `Tick()` imediato) e **parado em `Unloaded`** (não roda com a aba fora de
    tela).
  - Cada `Tick()` chama, nesta ordem: `RefreshUnlocks()`, `ReadStats()`, `ReadStage()`, `PushFuseConfig()`,
    e o **reconcile do switch de Evolution** (§2.2).
- **Leituras em background:** `ReadStats`, `ReadStage`, `RefreshUnlocks` disparam `Task.Run` para ler a
  memória (nunca no thread da UI) e depois `Dispatcher.Invoke` para escrever nos controles.
- **Auto-fill do campo vazio:** `ReadStats`/`ReadStage`, quando o `Value` está vazio/whitespace,
  preenchem-no com o valor atual lido (`"0.###"` para stats, inteiro para campos). Uma vez preenchido/tocado,
  não é sobrescrito.
- **Coluna "atual":** sempre atualizada com o valor lido; `"--"` quando o stat/campo não veio na leitura
  (attached mas sem resolver).
- **Aplicação contínua dos forçados:** o `AutomationLoop` (250 ms, thread separada, dono do
  `EngineService`) re-executa `ApplyStats(WantStats)` e `ApplyStage(WantStage)` todo tick — é isso que
  "mantém forçado". A UI só edita as **referências** `WantStats`/`WantStage` (swap atômico).
- **`PushFuseConfig` todo tick** garante que `AutoFuse.MaxGrade`/`Types` reflitam os filtros mesmo após um
  re-attach (o objeto `AutoFuse` é recriado a cada `Engine.Attach`).

---

## 8. Estados especiais (jogo fechado, erros, listas vazias, `--`)

- **Guarda de attach:** `ReadStats`, `ReadStage`, `PushFuseConfig`, `DoCube` retornam cedo se
  `!_svc.IsAttached`. `RefreshUnlocks` trata explicitamente o caso desconectado (label "jogo fechado",
  botão off).
- **Sutileza:** com o jogo fechado, `ReadStats`/`ReadStage` **retornam cedo e não zeram** a coluna "atual" —
  os últimos valores lidos permanecem exibidos (não viram `"--"`). Só ficam `"--"` quando attached mas o
  stat/campo específico não resolve.
- **Escritas com jogo fechado:** clicar Aplicar/Cubo não quebra — `RebuildForced*` seta a intenção, mas a
  escrita imediata só ocorre dentro do `Task.Run` sob `if (_svc.IsAttached)`; o `DoCube` aborta antes do
  diálogo se `!IsAttached`.
- **Valor inválido:** parse falho no Aplicar → log na barra de status (`_svc.RaiseLog`) e **não** ativa a
  linha (`Active` continua false).
- **Exceções de leitura:** cada `Read*`/`Push*` envolve o acesso em `try/catch` (retorna dict vazio /
  ignora), então uma leitura ruim num tick não derruba a UI.
- **`WdHold` (restart em andamento):** o `AutomationLoop` NÃO aplica nada durante o boot pós-restart; a UI
  não muda de comportamento, mas os forçados só voltam a ser escritos quando o watchdog libera.
- **Cubo `null`:** label `"cubo: --"`, botão desabilitado.

---

## 9. Cores / estilo usados hoje (tema `Dark.xaml`)

Paleta "precision instrument" (mesma do painel Python). Brushes por chave e o significado no Trainer:

| Chave / hex | Onde | Significado |
|---|---|---|
| `Bg` `#050506` | `Background` da view | fundo geral |
| `Card` `#16181C` + `Stroke` `#2B2E34` | `Card.Border` (todos os cards), raio 10, padding 12 | container de seção |
| `Fg` `#FFFFFF` | títulos de card (SemiBold 14), nomes de stat/campo, texto de switch, `_cubeLabel` | texto primário |
| `Sub` `#9AA0AB` | coluna "atual" (mono 11), labels "fundir até:"/"tipos:" | valor lido / rótulo secundário |
| `Subtle` `#666B74` | todas as notas de rodapé (FontSize 10) | ajuda/legenda |
| `Acc` `#FF7A18` (laranja) | headers de grupo de stat (SemiBold 11); track do switch **ligado**; fundo do `Accent.Button` | **ativo / ligado / ação primária** |
| `AccH` `#FF9440` | hover do accent; borda do accent | hover laranja claro |
| `AccTxt` `#0A0A0A` | texto sobre botão accent; knob do switch ligado | contraste sobre laranja |
| `Card2` `#212429` | fundo do combo/botão default; track do switch **desligado** | superfície de controle |
| `Stroke`/`Stroke2` `#2B2E34`/`#3D4149` | bordas de combo/textbox/botão default; hover do botão default | contornos |
| `Mono` (Consolas) | `_cubeLabel`, coluna "atual", `TextBox` de valor | valores numéricos |

**Significados-chave de cor:**
- **Laranja (`Acc`) = ativo/forçado/primário.** Switch ligado fica laranja; botão Aplicar ativo vira
  `Accent.Button` laranja com texto quase-preto e rótulo `✓ Forçado`; botões de ação primária (💾 Salvar,
  🧊 Cubo) são accent.
- **Botão desabilitado:** opacidade 0.45 (trigger `IsEnabled=False` no template) — é o visual do botão do
  cubo quando trava em Lv.100 ou com jogo fechado.
- **Switch (CheckBox estilizado):** template custom = trilho 34×18 arredondado + knob 12×12. Off = trilho
  `Card2`, knob `Subtle` à esquerda. On = trilho `Acc`, knob `AccTxt` à direita. O rótulo vem à direita do
  trilho.

> Observação: este card **não** usa `Red`/`Amber` do tema (esses aparecem no status/outras telas); no
> Trainer não há sinalização vermelha/âmbar por-controle. O único "estado de perigo" comunicado é textual
> (⚠ na nota dos campos de fase e o aviso do MessageBox do cubo).

---

## 10. Chamadas de backend (Engine / serviços)

Via `EngineService _svc`:
- `_svc.IsAttached` — guarda de conexão (Engine attached + processo vivo).
- `_svc.Engine` — fachada; a UI escreve as flags/dicts de intenção e chama métodos de leitura/escrita.
- `_svc.RaiseLog(msg)` — manda texto pra barra de status + `session.log`.

Flags de intenção escritas pela UI (lidas pelo `AutomationLoop`):
`WantActk, WantGodmode, WantAutobox, WantAutostash, WantAutofuse, WantAutoboss, WantEvolve, WantWatchdog`;
dicts `WantStats` (`string→double`) e `WantStage` (`string→int`).

Métodos chamados:
- `Engine.Stats.ReadStats()` → `Dictionary<string,double>` dos 25 stats (Pstat, resolvido por AOB→chain).
- `Engine.Stats.ApplyStats(dict)` → escreve os stats (float/double conforme tabela). Chamado no clique
  (imediato) e todo tick pelo loop.
- `Engine.Stats.ReadStage()` / `ApplyStage(dict)` → 18 campos int do `StageData` corrente (achado por AOB +
  sanidade act/stage/level/wave/monsters).
- `Engine.Save.CubeLevel()` → `int?` (ObscuredInt `bese` @ cube_sf+0x1CC).
- `Engine.Save.SetCubeLevel(100)` → escreve o nível runtime do cubo (dispara honesty-check → jogo fecha
  ~12s mas persiste).
- `Engine.AutoFuse.MaxGrade` / `.Types` (setados por `PushFuseConfig`) → teto de grade e tipos permitidos
  na síntese.
- (Indireto, via loop) `Cheats.SetActk/SetGodmode`, `AutoBox.OpenAll`, `AutoStash.MoveAllToStash/SortStep`,
  `AutoFuse.DoSynth`, `StageAutomation.AutoBoss/Evolve` — a UI **não** os chama direto; só liga as flags.

Via `ProfileStore _profStore`:
- `.Load()` → `Dictionary<string,Profile>` de `%APPDATA%/tbh_bot/profiles.json`.
- `.Save(all)` → grava o json (indentado).

---

## 11. Notas para o redesign (função a preservar; armadilhas)

**Essencial preservar (função, não visual):**
1. **Mecânica "Aplicar = escreve na hora + mantém forçado"** e o toggle de volta (`✓ Forçado` → clicar
   desativa). O valor forçado tem que continuar sendo re-escrito todo tick pelo loop — a UI só troca
   `WantStats`/`WantStage` (referência inteira, assign atômico, nunca mutar in-place).
2. **Auto-fill do campo vazio** com o valor atual, sem sobrescrever o que o usuário digitou.
3. **Encadeamento auto-modo → ACTk** (ligar Auto-box/stash/fuse/boss/evolve liga o ACTk; watchdog não).
   Vale inclusive ao carregar profile. Só liga, nunca desliga o ACTk.
4. **Reconcile do switch de Evolution** que se auto-desmarca ~1 s após o climb completo (Torment 3-9).
5. **Trava do botão do cubo** em Lv.100 (`IsEnabled=false` + texto muda) e o **MessageBox de confirmação**
   antes de elevar (o usuário precisa saber que o jogo fecha ~12s).
6. **Todas as leituras em background** + guardas `IsAttached`; nunca ler memória no thread da UI.
7. **Filtros do fuse** (teto de grade + 3 tipos) empurrados pro Engine continuamente (`PushFuseConfig`
   todo tick), pra sobreviver a re-attach.
8. **Profiles** capturam/aplicam: 8 switches + FuseGrade + FuseTypes + stats/campos ATIVOS. Carregar aplica
   mexendo nos controles (dispara handlers), não escrevendo direto no Engine.
9. **Agrupamento dos stats** em ATTACK/DEFENSE/OTHER e os nomes exatos (chaves de `GameConstants.Stats`) —
   uma linha só é criada se o nome existir na tabela (`ContainsKey`).

**Armadilhas conhecidas:**
- A coluna "atual" **não** vira `"--"` quando o jogo fecha (fica congelada no último valor); só vira `"--"`
  com jogo aberto e stat/campo não resolvido. Um redesign que queira "cinza quando offline" precisa tratar
  isso de propósito.
- Editar um `TextBox` de uma linha **inativa** não escreve nada; a escrita imediata só existe se a linha já
  estiver `Active` (o `TextChanged` só re-aplica quando `row.Active`).
- Parse é `InvariantCulture` (ponto decimal). Stats aceitam float; campos de fase aceitam **só inteiro**.
- `Act`/`StageNo` podem ser rejeitados pelo servidor (aviso na nota de rodapé) — forçar não garante efeito.
- Escrever o nível do cubo (e stages) **fecha o jogo ~12s** pelo anti-cheat, mas o valor persiste; não é
  bug — é o aviso do MessageBox.
- Os labels de header de grupo (`⚔/🛡/✦`) e os emojis dos toggles são texto — parte da identidade dos
  controles; se removê-los, manter o significado (ataque/defesa/outros).
- O timer só roda com a aba visível (`Loaded`/`Unloaded`); um redesign que renderize a aba sempre precisa
  manter esse start/stop para não vazar `DispatcherTimer`.
