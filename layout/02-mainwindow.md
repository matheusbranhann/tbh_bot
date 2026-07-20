# 02 — Janela principal (`MainWindow`) + `EngineService`

> Fonte da verdade: `src/TbhBot.App/MainWindow.xaml`, `src/TbhBot.App/MainWindow.xaml.cs`,
> `src/TbhBot.App/Services/EngineService.cs`, `src/TbhBot.App/Services/WatchdogService.cs`,
> `src/TbhBot.Core/Automation/AutomationLoop.cs`, `src/TbhBot.Core/Engine.cs`,
> `src/TbhBot.Core/Memory/ProcessTarget.cs`, tema `src/TbhBot.App/Theme/Dark.xaml`.
>
> Este documento descreve a **FUNÇÃO**, não o visual atual. Quem for redesenhar pode mudar tudo que for
> aparência, mas **não pode inventar comportamento** — os textos, os métodos chamados, as guardas e os
> timers abaixo são o contrato real do código.

---

## 1. Propósito

`MainWindow` é o **shell** (casca) do painel: uma janela única que hospeda um cabeçalho fixo (título +
status de conexão + botão de abrir o jogo), um `TabControl` com as **5 telas** (Trainer, Inventory,
Market, Runes, Stages) e uma **barra de status** inferior que mostra o último log. Ela não tem lógica de
jogo própria: **cria um único `EngineService`** e o injeta em todas as abas; o `EngineService` é o dono do
`Engine` (attach/re-attach ao processo do jogo em background) e da automação.

---

## 2. Estrutura visual atual

`Grid` de 3 linhas (`Auto` / `*` / `Auto`) preenchendo a `Window` inteira:

```
┌──────────────────────────────────────────────────────────────────────────┐  Window
│  Row 0 — CABEÇALHO  (Border, Background=Surf, borda inferior 1px Stroke)   │  Title="tbh_bot (C#)"
│  ┌──────────────────────────────────────────────────────────────────────┐ │  840h x 900w, CenterScreen
│  │ [DockPanel]                                                            │ │
│  │  "tbh_bot"  ● conectado · build 3f1a9c2b · offsets ✓   [ ▶ Abrir jogo ]│ │
│  │   ↑título    ↑Conn (label de conexão)                    ↑Dock=Right   │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────────────┤
│  Row 1 — TabControl x:Name="Tabs"  (Margin 6, ocupa todo o espaço "*")     │
│  ┌──────────────────────────────────────────────────────────────────────┐ │
│  │ [ Trainer ][ Inventory ][ Market ][ Runes ][ Stages ]   ← 5 TabItem   │ │
│  │ ┌──────────────────────────────────────────────────────────────────┐ │ │
│  │ │  conteúdo da aba selecionada (UserControl injetado)               │ │ │
│  │ │  Trainer=TrainerView · Inventory=InventoryView · Market=MarketView│ │ │
│  │ │  Runes=RunesView · Stages=StagesView                              │ │ │
│  │ └──────────────────────────────────────────────────────────────────┘ │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────────────┤
│  Row 2 — BARRA DE STATUS (Border, Background=Surf, borda superior 1px)     │
│  │  <último log emitido pelo Engine/automação/watchdog>                    │
└──────────────────────────────────────────────────────────────────────────┘
```

**Agrupamento lógico:**
- **Cabeçalho** (`Border` Row 0): um `DockPanel`. O botão `▶ Abrir jogo` está ancorado à **direita**
  (`DockPanel.Dock="Right"`); o restante do fluxo, da esquerda p/ a direita, é **título** `tbh_bot` e a
  seguir a **label de conexão** `Conn` (Margin esquerda 16).
- **Miolo** (Row 1): `TabControl` que é montado 100% em code-behind (o XAML tem só `<TabControl
  x:Name="Tabs" .../>` vazio). As 5 abas são adicionadas na ordem fixa via `AddTab(...)`.
- **Rodapé** (Row 2): `Border` com um único `TextBlock x:Name="Status"`.

---

## 3. Tabela de CONTROLES

> `N` da linha final = **10** elementos documentados (5 do shell + as 5 abas).

### 3.1 Elementos do shell (cabeçalho + rodapé)

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte de dado) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Título | `TextBlock` (display) | `tbh_bot` (literal fixo; **≠** do `Title` da janela, que é `tbh_bot (C#)`) | Sempre visível | — (constante) | Não interativo | — | Sempre habilitado/estático |
| `Conn` (label de conexão) | `TextBlock` (display), `Style=Sub.Text` | Texto muda por estado (ver abaixo) | XAML inicia com `● conectando…` (cor Sub/cinza) | `_svc.IsAttached`, `_svc.Engine.BuildHash`, `_svc.Engine.OffsetsLoaded` | Não interativo. É **reescrito** por `UpdateConn()` | **Conectado:** `● conectado   ·   build {hash[..8]}   ·   offsets {✓ | só-AOB}` em cor **Acc** (laranja). **Desconectado:** `● jogo fechado — reconecta ao reabrir` em cor **Red** (vermelho). `build` = 8 primeiros chars do `BuildHash`, ou `?` se nulo/curto | Estático (só o texto/cor mudam) |
| `LaunchBtn` (`▶ Abrir jogo`) | `Button` (estilo padrão do tema) | `▶ Abrir jogo` | Habilitado, `Dock=Right`, Padding 10,4 | — | `Click` → `OnLaunchGame` → `_svc.LaunchGame()` → `Process.Start("steam://run/3678970", UseShellExecute=true)`. **Não checa `IsAttached`**: dispara sempre. Se lançar exceção, emite log `launcher: falha (<msg>)` na barra de status | Nada visual muda no botão; o efeito colateral é o jogo abrir e, ~1 s depois, o `ConnectLoop` attachar e o `Conn` virar verde/laranja | **SEMPRE habilitado** (não há guarda por estado de conexão) |
| `Tabs` (`TabControl`) | `TabControl` | 5 headers (tabela 3.2) | Primeira aba (`Trainer`) selecionada por padrão | Itens montados em code-behind por `AddTab` | Clicar num `TabItem` troca o conteúdo exibido (`UserControl` da aba). Não há evento custom no `MainWindow`; é seleção nativa | Mostra o `UserControl` correspondente. Todas as views recebem **a mesma instância** de `_svc` no construtor | Sempre habilitado |
| `Status` (barra inferior) | `TextBlock` (display), `Style=Sub.Text` | Vazio (`Text=""`) | Vazio até o 1º log | Evento `_svc.Log` | Não interativo. `MainWindow` faz `_svc.Log += m => Status.Text = m` → **substitui** o texto pelo último log (não acumula; histórico completo vai só p/ o arquivo `session.log`) | Sempre mostra a **última** mensagem emitida por Engine/AutomationLoop/Watchdog/overlay | Estático |

### 3.2 As 5 abas (`TabItem`) — ordem fixa de `MainWindow.MainWindow()`

| # | Header (texto exato) | Conteúdo (`UserControl`) | Injetado com | Doc detalhada |
|---|---|---|---|---|
| 1 | `Trainer`   | `TrainerView`   | `new TrainerView(_svc)`   | (ver doc da aba Trainer) |
| 2 | `Inventory` | `InventoryView` | `new InventoryView(_svc)` | (ver doc da aba Inventory) |
| 3 | `Market`    | `MarketView`    | `new MarketView(_svc)`    | (ver doc da aba Market) |
| 4 | `Runes`     | `RunesView`     | `new RunesView(_svc)`     | (ver doc da aba Runes) |
| 5 | `Stages`    | `StagesView`    | `new StagesView(_svc)`    | (ver doc da aba Stages) |

> Todas as abas compartilham **um único** `EngineService`/`Engine`. Não há um Engine por aba. Isso é
> essencial: as flags `Want*`, o attach e o `AutomationLoop` são globais.

---

## 4. Comportamento vivo (timers / auto-refresh / background)

O `MainWindow` em si **não tem timer próprio**. Toda a "vida" vem do `EngineService`, iniciado no
construtor por `_svc.Start()`. `Start()` dispara três loops assíncronos cancelláveis (todos amarrados a um
`CancellationTokenSource`):

1. **`ConnectLoopAsync` — a "TICK" de reconexão (a cada 1000 ms):**
   - Lê `IsAttached`. Se **não** attachado, tenta `Engine.Attach()` (em `try/catch`; falha vira `false`).
   - Quando o estado (`now`) **muda** em relação ao anterior (`last`), faz `Post(() =>
     StateChanged?.Invoke())` — ou seja, marshaliza p/ o **thread da UI** e dispara `StateChanged`, que o
     `MainWindow` liga a `UpdateConn`. **Resultado prático:** a label `Conn` e sua cor se atualizam
     sozinhas ~1 s depois de o jogo abrir/fechar, sem ação do usuário.

2. **`AutomationLoop.RunAsync` — automação (tick de 250 ms):**
   - Só opera quando `!engine.WdHold && engine.IsAttached && engine.Target.IsAlive()`.
   - A cada tick: `ApplyCheats()` (aplica `WantActk`/`WantGodmode` + reaplica `WantStats`/`WantStage`
     forçados TODO tick, pois o jogo sobrescreve escrita única) e `RunActions()` (ordem de prioridade:
     **box → stash → fuse**; auto-boss/evolve/ordenação-do-baú só quando `did==false`, i.e. ocioso).
   - Um tick nunca derruba o loop: exceção vira log `auto: erro (<msg>) — seguindo`.

3. **`WatchdogService.RunAsync` — Auto-restart (dono único do relançamento):**
   - Só age se `engine.WantWatchdog`. Com jogo vivo: a cada 2000 ms rastreia o estágio atual
     (`Save.StageProgress().Cur`) como fallback de reentrada e mantém `WdHold=false`.
   - **Jogo fechou:** seta `WdHold=true` (start limpo — o `AutomationLoop` não aplica nada no boot),
     espera o exe sumir totalmente, `svc.LaunchGame()`, espera subir (até `Startup=120` × 2 s), fecha o
     popup OFFLINE REWARDS via OCR+clique real (tolerante), lê o estágio, seta `WdHold=false`
     (religa tudo) e **reentra** no estágio (`EnterBoss` se for boss, senão `GoToStage`).

`Post(...)` (em `EngineService`) garante que **todo** `StateChanged`/`Log` roda no thread da UI
(`Dispatcher.BeginInvoke` se necessário), porque as leituras de memória rodam em background.

---

## 5. Estados especiais

- **Jogo fechado / não attachado** (`_svc.IsAttached == false`):
  - `Conn` → `● jogo fechado — reconecta ao reabrir`, cor **Red**.
  - `AutomationLoop` e `WatchdogService` (quando ligado) só observam; o `ConnectLoop` continua tentando
    `Attach()` a cada 1 s. Nada de jogo é lido/escrito.
  - O botão `▶ Abrir jogo` continua **habilitado** e funcional (é justamente o caminho de recuperação).
- **Estado transitório inicial:** ao abrir a janela, o construtor chama `_svc.Start()` e **logo em
  seguida** `UpdateConn()` de forma síncrona — nesse instante o `Attach()` do `ConnectLoop` (async) ainda
  não rodou, então tipicamente o primeiro paint mostra `● jogo fechado…` em vermelho e, ~1 s depois, vira
  verde/laranja se o jogo estiver aberto. O texto XAML `● conectando…` (cinza) é sobrescrito quase de
  imediato.
- **Offsets não resolvidos** (`OffsetsLoaded == false`): conectado mas a label mostra `offsets só-AOB`
  (em vez de `offsets ✓`) — significa que só leituras por AOB (stats/stage/godmode) funcionam; o resto
  depende do cache/embutido de offsets do build.
- **`BuildHash` nulo/curto:** o campo `build` vira `?`.
- **Barra de status vazia:** enquanto nenhum log foi emitido, `Status` fica em branco. Ela nunca mostra
  histórico — só a última linha; o histórico completo (com timestamp) está no `session.log`.
- **Falha ao abrir o jogo:** `LaunchGame()` engole a exceção e emite `launcher: falha (<msg>)` na barra.
- **Fechamento da janela:** `Closed += _svc.Stop()` → cancela o CTS (para os 3 loops) e `Engine.Dispose()`
  (fecha o handle do processo).

---

## 6. Cores / estilo usados hoje (tema `Dark.xaml`)

| Elemento | Brush/estilo | Hex | Significado |
|---|---|---|---|
| Fundo da janela | `Bg` | `#050506` | preto quase absoluto (fundo raiz) |
| Cabeçalho e rodapé | `Surf` | `#0C0D0F` | superfície ligeiramente acima do fundo |
| Bordas do cabeçalho/rodapé | `Stroke` | `#2B2E34` | linha divisória 1px |
| Título `tbh_bot` | `Fg` | `#FFFFFF` | texto principal, Bold 16 |
| `Conn` **conectado** | `Acc` | `#FF7A18` | **laranja = attachado/OK** (o `●` herda a cor) |
| `Conn` **desconectado** | `Red` | `#F87171` | **vermelho = jogo fechado/erro** |
| `Conn`/`Status` (fonte) | `Sub.Text` | fg `#9AA0AB`, Consolas 11 | mono/subtítulo cinza (cor base antes de `UpdateConn` pintar) |
| `▶ Abrir jogo` | `Button` padrão | fundo `Card2` `#212429`, borda `Stroke2` `#3D4149`, hover `Stroke2` | botão neutro (NÃO usa `Accent.Button`) |
| `TabItem` selecionado | borda inferior 2px `Acc`, fg `Fg` | `#FF7A18` / `#FFFFFF` | aba ativa sublinhada de laranja |
| `TabItem` inativo | fg `Sub` | `#9AA0AB` | cinza |

Outros brushes disponíveis no tema (usados pelas abas, não pelo shell): `Card`/`Card2`, `Amber` `#FBBF24`,
`Accent.Button` (laranja com texto `AccTxt` `#0A0A0A`), estilo `Card.Border` (card arredondado r=10),
`CheckBox` renderizado como **switch** (track laranja quando ligado). Fontes: `UI`=Segoe UI, `Mono`=Consolas.

> Significado condensado das cores de status: **laranja = vivo/conectado**, **vermelho = morto/fechado**.

---

## 7. Chamadas de backend (o que o shell usa)

**`EngineService` (fachada da UI sobre o `Engine`):**
- `Engine` (propriedade) — o `Engine` compartilhado por todas as abas.
- `IsAttached` → `Engine.IsAttached && Engine.Target.IsAlive()` — verdade só se o handle está aberto **e**
  o processo do jogo continua vivo.
- `event StateChanged` — disparado **no thread da UI** quando o attach liga/desliga. `MainWindow` liga em
  `UpdateConn`.
- `event Log(string)` — cada log do Engine/automação/watchdog/overlay; `MainWindow` liga em `Status.Text`.
- `Start()` — idempotente (guarda `_cts is not null`); cria o CTS, assina `Engine.Log`, instancia e roda
  `ConnectLoopAsync`, `AutomationLoop.RunAsync`, `WatchdogService.RunAsync`.
- `Stop()` — cancela o CTS, zera-o e `Engine.Dispose()`. Chamado no `Closed` da janela.
- `LaunchGame()` — `Process.Start("steam://run/" + Engine.SteamAppId)` (`SteamAppId = 3678970`),
  `UseShellExecute=true`; erro → log `launcher: falha (...)`. Usado pelo botão **e** pelo watchdog.
- `RaiseLog(string)` — porta de entrada de log externo (ex.: overlay) → grava em arquivo + emite `Log` na
  UI. (Não é chamado pelo shell diretamente, mas existe.)
- `session.log` — `LogToFile` escreve **append** em `%APPDATA%\tbh_bot\session.log`, formato
  `HH:mm:ss  <msg>`, com `lock` global e best-effort (`catch { }`). É o **histórico rolável** que a barra
  de status (linha única) não guarda.

**`Engine` (campos lidos pelo `UpdateConn`):**
- `BuildHash` — md5 dos ~2 MB do `GameAssembly.dll` (identifica o build). `null` se não conseguiu ler.
- `OffsetsLoaded` — `true` se resolveu offsets (build conhecido, cache `offsets_<hash>.json`, ou embutido);
  `false` → modo "só-AOB".
- `SteamAppId` — const `3678970` (usado no `steam://run/`).
- `Attach()` / `Target.IsAlive()` / `Dispose()` — attach ao processo `TaskBarHero`, checagem de vida,
  liberação do handle.

---

## 8. Notas p/ o redesign (o que é ESSENCIAL preservar na função)

- **Um só `EngineService`/`Engine` p/ todas as abas.** Ele é criado no shell e injetado por construtor em
  cada `UserControl`. Não crie um Engine por tela — as flags `Want*`, o attach e o `AutomationLoop` são
  globais e únicos. Qualquer redesign deve continuar passando **a mesma instância** para as 5 views.
- **A reconexão é automática e assíncrona.** Não amarre a UI a "conectar" — o `ConnectLoop` re-attacha
  sozinho a cada 1 s e avisa via `StateChanged` no thread da UI. O redesign só precisa **refletir** o
  estado (texto + cor), não gerenciá-lo.
- **`StateChanged` e `Log` já chegam no thread da UI** (via `Post`/`Dispatcher`). Pode setar controles
  direto no handler. Mas as leituras de dados das abas rodam em background — mantenha esse padrão.
- **O botão de abrir o jogo NÃO deve depender de `IsAttached`.** Ele é o caminho de recuperação quando o
  jogo está fechado; desabilitá-lo quando desconectado seria um bug (o usuário nunca conseguiria abrir).
- **A barra de status mostra só a última linha.** Se o redesign quiser um histórico visível, leia de
  `%APPDATA%\tbh_bot\session.log` — não tente acumular no `TextBlock` (o binding só substitui).
- **Preservar os textos/estados de conexão** (são o contrato que o usuário reconhece):
  `● conectado   ·   build {8hex}   ·   offsets {✓|só-AOB}` (laranja) vs
  `● jogo fechado — reconecta ao reabrir` (vermelho). O `build` é sempre truncado a 8 hex.
  ⚠️ O enunciado do pedido citava versões abreviadas (`● conectado · build X · offsets ✓` /
  `● jogo fechado`); os textos **reais do código** são os acima — use os do código.
- **Ordem fixa das abas:** Trainer, Inventory, Market, Runes, Stages. Trainer é a default selecionada.
- **Encerramento limpo:** chame `Stop()` no fechar da janela (cancela loops + `Dispose`); sem isso, o
  handle do processo e os loops vazam.
- **Cuidado com o `Title` vs. cabeçalho:** o `Window.Title` é `tbh_bot (C#)` (barra de tarefas/título do
  SO); o texto no cabeçalho é só `tbh_bot`. São dois textos diferentes de propósito.
- **`WdHold` é gatilho de "start limpo".** Só o `WatchdogService` o controla; se o redesign mexer no fluxo
  de restart, respeite que enquanto `WdHold==true` o `AutomationLoop` **não aplica nada** (evita bater no
  honesty-check do ACTk durante o boot). O `ConnectLoop` continua attachando mesmo com `WdHold`.
```
