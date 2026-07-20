# 08 — Overlay de Preço (Price Overlay)

> Fonte da verdade da **FUNÇÃO**, não do visual atual. Uma IA/pessoa pode redesenhar o layout
> lendo só este doc, sem adivinhar comportamento.
>
> Código real:
> - `src/TbhBot.App/Services/OverlayService.cs` — o motor (timer, captura, OCR, casamento, badge).
> - `src/TbhBot.App/Services/OverlayWindow.cs` — a janela topmost transparente click-through.
> - `src/TbhBot.App/Services/Native.cs` — P/Invoke (cursor, enum de janelas, captura GDI).
> - `src/TbhBot.Core/Market/PriceIndex.cs` — índice de preços embutido (base→grade→USD).
> - `src/TbhBot.App/Views/MarketView.cs` — **onde vive o toggle** (aba "Market").
> - `src/TbhBot.App/Theme/Dark.xaml` — tema (brushes/estilos do painel; o badge NÃO usa o tema).

---

## 1. Propósito

O Price Overlay é uma janela **topmost, transparente e CLICK-THROUGH** que cobre a tela virtual
inteira. A cada **220 ms** ele captura a região sob o cursor (**só quando o mouse está PARADO sobre a
janela do jogo**), faz **OCR nativo do Windows (WinRT `Windows.Media.Ocr`)**, acha a linha "`<Grade>
Grade`" do tooltip, resolve o **nome** do item nas linhas acima, casa com o **PriceIndex** (preços da
Steam embutidos) e desenha um **badge de preço** ao lado do tooltip do jogo — sem interferir no clique
nem no foco do jogo.

É a porta do antigo `tbh_overlay.py` para C# nativo (sem processo Python separado). É **opt-in**:
começa **OFF** e só liga quando o usuário clica no toggle na aba Market.

---

## 2. Estrutura visual atual

O overlay tem **duas peças**: (a) o **toggle** dentro da aba **Market** do painel; (b) a **janela de
badge** que aparece SOBRE o jogo (fora do painel). O badge NÃO é um elemento do painel — é uma
`OverlayWindow` OS-level desenhada por cima de tudo.

### 2a. Toggle na aba Market (dentro do painel)

A aba Market empilha (StackPanel vertical, `Margin=16`) — o bloco do overlay é o 3º/4º item:

```
┌─ Aba "Market" ─────────────────────────────────────────────┐
│  Steam Community Market            (título, 18px SemiBold)  │
│  Busca o preco (USD) ... nome exato do mercado. (Sub.Text)  │
│                                                             │
│  ┌───────────────────────────┐                             │
│  │ ◉  Price overlay: OFF      │  ← BOTÃO-TOGGLE do overlay  │
│  └───────────────────────────┘     (alinhado à esquerda)   │
│  passe o mouse sobre um item no jogo → mostra o preço       │
│  Steam ao lado do tooltip (OCR nativo).        (Sub.Text)   │
│                                                             │
│  ┌───────────────────────────┐ ┌────────┐   ← busca MANUAL │
│  │ [ TextBox de busca      ]  │ │ Buscar │     (outra       │
│  └───────────────────────────┘ └────────┘      feature)    │
│  ┌ resultados (cards, mais recente no topo) ─────────────┐ │
│  └───────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

> **Agrupamento lógico:** o **toggle + legenda** são o overlay. A **linha de busca + lista de
> resultados** são a *busca manual* de preço (usa `MarketDb`, endpoint Steam ao vivo) — feature
> SEPARADA, apenas co-locada na mesma aba. Ver doc da aba Market para a busca; aqui só o overlay.

### 2b. Janela de badge (sobre o jogo, fora do painel)

`OverlayWindow` = janela sem borda, do tamanho da tela virtual inteira, transparente, click-through.
O canvas normalmente está **vazio**; quando há item sob o cursor parado, desenha **um único badge**:

```
        (tooltip do jogo)              (badge desenhado pelo overlay)
   ┌───────────────────────┐          ┌──────────┐
   │  Flame Sword           │          │  $ 12.30 │   ← ao lado, à direita
   │  Legendary Grade  ─────┼───+14px─▶│          │      da linha de grade
   │  ...                   │          └──────────┘
   └───────────────────────┘
```

- Posição X do badge = borda direita da palavra "Grade" (`gb.x1`) + **14 px**.
- Posição Y do badge = **centro vertical** da linha de grade, deslocado **−13 px** (centraliza o border).

---

## 3. Tabela de CONTROLES

A feature do overlay tem **1 controle interativo** (o toggle). Por completude da aba, incluo também os
2 controles co-locados da busca manual (feature separada). Coordenadas de tela/OCR são internas (sem
controle de UI).

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte de dado) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| **Price overlay toggle** (`_ovBtn`) | `Button` (usado como on/off; **não** é CheckBox) | `◉  Price overlay: OFF` ↔ `◉  Price overlay: ON` | **OFF** (opt-in; estilo default, sem accent) | Estado do overlay via `_overlay.Enabled` (é `_win is not null`) | Clique → `bool on = _overlay.Toggle();` — se estava OFF: valida OCR e índice, cria a `OverlayWindow`, inicia o `DispatcherTimer` (220 ms) e loga `◉ Price overlay: ON (...)`. Se estava ON: `Stop()` (para timer, fecha janela, limpa cache) e loga `◉ Price overlay: OFF`. Depois: `_ovBtn.Content = on ? "...ON" : "...OFF"` e `_ovBtn.Style = on ? Accent.Button : null`. | Texto e cor do próprio botão; badge passa a aparecer/sumir sobre o jogo; barra de status recebe o log via `svc.RaiseLog` | Sempre habilitado. Toggle **falha silenciosamente para OFF** (retorna false, sem ligar) se `OcrEngine` indisponível ou `PriceIndex.Count == 0` (ver §5) |
| **Busca de preço** (`_search`) — *feature separada* | `TextBox` | placeholder vazio; digita o nome exato do mercado | vazio | texto digitado | `Enter` → `DoSearchAsync()` (idem botão). Limpa e refoca ao terminar. | dispara busca `MarketDb.GetPriceAsync` | sempre habilitado |
| **Buscar** (`_btn`) — *feature separada* | `Button` (`Accent.Button`) | `Buscar` (vira `...` durante a busca) | habilitado | `_search.Text` (trim) | Clique → `DoSearchAsync()`: `IsEnabled=false`, `Content="..."`, chama `await _db.GetPriceAsync(name)` (Steam ao vivo, cache 6 h), insere card no topo dos resultados; `finally` restaura, limpa e refoca | lista de resultados (card com `$x.xx` em Acc, ou `sem preço` em Red) | desabilita enquanto a busca roda; ignora texto vazio |

> **O toggle é um `Button`, não um switch/CheckBox.** O estado ON é comunicado trocando (i) o texto
> `OFF`→`ON` e (ii) o estilo para `Accent.Button` (laranja). Não há binding — o botão é a única fonte
> visual do estado. **Não persiste**: o painel reseta switches a cada restart, então o overlay volta a
> **OFF** ao reabrir o app.

### 3a. Estados do BADGE (saída visual, não é controle — mas é o coração da função)

O que o badge mostra depende do que o OCR e o `PriceIndex` acharam. **Preserve exatamente estes casos:**

| Situação detectada | Texto do badge | Cor de fundo (`Face`) | Cor da tinta (`Ink`) | Significado |
|---|---|---|---|---|
| Item vendável, preço **≥ $1** | `$ 12` (≥100 sem casas) ou `$ 12.30` (<100, 2 casas) | `#146c2e` (verde) | `#eaffea` (branco esverdeado) | tem valor real na Steam |
| Item vendável, preço **< $1** | `$ 0.05` | `#3d5a1e` (verde-oliva escuro) | `#eaffea` | vale pouco, mas vendável |
| Preço **aproximado** (grade do tooltip não está no índice → grade mais próximo) | prefixo `~$ ` (ex.: `~$ 12.30`) | `#146c2e` ou `#3d5a1e` (pela faixa do preço) | `#eaffea` | valor do grade mais próximo, não exato |
| Item conhecido mas **sem preço** para o grade | `—` | `#242424` (cinza escuro) | `#7a7a7a` (cinza) | reconhecido, sem cotação |
| Tooltip diz **"Untradable"** | `Untradable` | `#3a2323` (vinho escuro) | `#d08a8a` (vermelho fosco) | o jogo marcou como não-comercializável |
| Nome não resolvido / linha "Grade" não achada / cursor movendo / fora do jogo | *(nenhum badge — canvas vazio)* | — | — | nada a mostrar |

- Sinal do prefixo: `~$ ` = aproximado (`approx==true`); `$ ` = exato. Formatação: `price < 100`
  usa `"0.00"`; `>= 100` usa `"0"` (inteiro).
- **Anti-piscar:** se o OCR falha em UM frame mas o cursor mal se moveu (≤18 px) e faz ≤6 frames
  (~1,3 s) que havia badge, o overlay **repete o último badge** em vez de apagar.

---

## 4. Comportamento vivo (timers / auto-refresh / background)

- **Timer principal:** `DispatcherTimer` com **`Interval = 220 ms`** (roda no thread da UI/Dispatcher).
  Cada tick → `TickAsync()`.
- **Reentrância:** guarda `_busy`. Se um tick anterior ainda processa (OCR async), o novo tick sai na
  hora. Um frame ruim é engolido por `try/catch` — **nunca derruba o overlay**.
- **Cada tick faz, em ordem:** `_n++` (contador de frame) → `_win.EnsureTopmost()` (reafirma
  topmost, pois o jogo/Steam pode subir por cima) → `badge = await ScanAsync()` → `_win.Render(badge)`.
- **`ScanAsync` só produz badge quando TODAS as condições valem:**
  1. `GetCursorPos` OK.
  2. Existe janela do jogo (título contém `TaskBarHero` ou é exatamente `TBH`).
  3. O cursor está **sobre a janela do jogo** — testado por
     `GetAncestor(WindowFromPoint(cursor), GA_ROOT=2) == gameHwnd`.
  4. O cursor está **PARADO**: movimento em X **e** Y ≤ **22 px** desde o último tick. Se moveu, retorna
     `null` no frame (mas preserva `_last` para quando parar).
  5. A região capturada (clampada ao retângulo do jogo, meia-caixa **`Radius=460`** em torno do cursor)
     tem **≥ 80×80** px.
- **Captura:** `Native.CaptureBgra` faz **screen-grab GDI** da região `(rx0,ry0,w,h)` e upscala por
  **`Scale=1.7`** (`StretchBlt`, modo `HALFTONE`) para (`bw,bh`), devolvendo BGRA top-down (alpha
  forçado a 255). É um **grab de TELA** (não `PrintWindow`), então lê o tooltip como está renderizado.
- **OCR:** BGRA → `SoftwareBitmap` Bgra8 → `OcrEngine.RecognizeAsync` (assíncrono).
- **`EnsureTopmost`** roda a **cada** tick (220 ms) — é o "auto-refresh" que mantém o badge acima do jogo.
- **Aparece/some sozinho:** o badge some quando o cursor se move, sai do jogo, ou o OCR deixa de achar
  a linha de grade (respeitando o anti-piscar de ~1,3 s). Aparece assim que o cursor para sobre um item
  com tooltip legível.

---

## 5. Estados especiais

- **OCR do Windows indisponível** (`OcrEngine.TryCreateFromUserProfileLanguages()` retorna `null` —
  nenhum idioma de OCR instalado): `Toggle()` **não liga**, loga
  `overlay: OCR do Windows indisponível (instale um idioma) — não liguei` e retorna `false`. O botão
  permanece OFF.
- **Índice de preços vazio** (`_prices.Count == 0` — `item_prices.json` embutido ausente/corrompido):
  `Toggle()` **não liga**, loga `overlay: índice de preços vazio`, retorna `false`. Botão fica OFF.
- **Jogo fechado / não encontrado:** `ScanAsync` acha a janela por **título via `EnumWindows`** (NÃO
  usa `_svc.IsAttached` nem memória do processo). Sem janela do jogo → `_last=null`, retorna `null` →
  canvas vazio (nenhum badge). O overlay continua ligado e volta a mostrar quando o jogo reaparece.
  → **O overlay é 100% visual/OCR e independe do Engine estar attachado à memória do jogo.**
- **Cursor fora do jogo / sobre outra janela:** `GetAncestor(WindowFromPoint,...) != gameHwnd` →
  `_last=null`, nenhum badge.
- **Frame de OCR falho / GDI falho:** `CaptureBgra` retorna `null`, ou o OCR não acha "Grade" → sem
  badge naquele frame; anti-piscar segura o último por até 6 frames se o cursor mal moveu.
- **Nome não reconhecido:** se nenhuma linha acima da grade casa com um item conhecido (exato, ou fuzzy
  `ratio ≥ 0.70`), retorna `null` → sem badge (não mostra `—`; o `—` é só para item conhecido *sem
  preço no grade*).
- **`Stop()` limpa tudo:** para o timer, fecha a janela, zera `_last` e `_lx/_ly=-9999`.
- **Fechamento do app:** `OverlayWindow.Owner = MainWindow` — o overlay **fecha junto com o app** (senão
  seguraria o processo vivo).

---

## 6. Cores / estilo usados hoje

### Toggle (dentro do painel — usa o tema `Dark.xaml`)
- **OFF:** estilo `Button` default → fundo `Card2` `#212429`, borda `Stroke2` `#3D4149`, texto `Fg`
  `#FFFFFF`, cantos 8, hover → fundo `Stroke2`.
- **ON:** estilo `Accent.Button` → fundo `Acc` `#FF7A18` (laranja), texto `AccTxt` `#0A0A0A`, hover →
  `AccH` `#FF9440`. **Significado: laranja = overlay LIGADO.**
- Legendas em `Sub.Text` (`Sub` `#9AA0AB`, Consolas 11). Título da aba em `Fg` 18 SemiBold.

### Badge (fora do painel — **cores hex FIXAS no código, NÃO o tema**)
O badge é desenhado na `OverlayWindow`, uma janela OS-level; por isso **não** referencia brushes do
tema — todas as cores são hardcoded (ver tabela §3a). Significado das cores:
- **Verde `#146c2e`** = preço ≥ $1 (item vale a pena).
- **Verde-oliva `#3d5a1e`** = preço < $1 (vale pouco).
- **Cinza `#242424` / tinta `#7a7a7a`** = conhecido, sem preço (`—`).
- **Vinho `#3a2323` / tinta `#d08a8a`** = "Untradable".
- Tinta padrão dos preços: `#eaffea` (branco esverdeado).
- Borda do badge: **preta** (`Brushes.Black`), 1 px; canto 3; padding `7,3,7,3`.
- Fonte do badge: **Segoe UI, 12, Bold**.

---

## 7. Chamadas de backend (métodos usados e o que fazem)

### `OverlayService` (motor)
- `Toggle()` → liga/desliga; valida OCR e índice; retorna novo estado (`bool`).
- `Start()` / `Stop()` → ciclo de vida da janela + timer.
- `TickAsync()` → 1 frame: topmost + scan + render.
- `ScanAsync()` → captura + OCR + monta o `Badge?`.
- `ReadBadge(...)` → acha a linha de grade mais próxima do cursor, resolve o nome acima, consulta preço,
  decide texto/cor.
- `event Action<string>? Log` → texto para a barra de status.

### `PriceIndex` (`TbhBot.Core.Market`) — índice embutido, offline
- `Count` → nº de bases no índice (usado como guarda no `Toggle`).
- `ResolveBase(IEnumerable<string> texts)` → dentre as linhas candidatas (as acima da grade), devolve a
  base conhecida: **match exato ganha na hora**; senão **fuzzy** (LCS ≈ `SequenceMatcher.ratio`) com
  limiar **≥ 0.70**.
- `PriceOf(baseName, gradeWord)` → `(price, approx)?`: material (1 grade) → preço único (exato); grade
  listado → exato; grade ausente → **grade mais próximo** (`approx=true`); nenhum grade → `max`
  (`approx=true`).
- Fonte: recurso embutido `Data/item_prices.json` (`base`/`name`/`market_name`, `grade` 0–9, `price_usd`).
  Mapa de grade: common/normal=0, uncommon=1, rare=2, legendary=3, immortal=4, arcana=5, beyond=6,
  celestial=7, divine=8, cosmic=9.

### `OverlayWindow`
- `Show()` / `Close()` → cria/derruba a janela topmost transparente.
- `EnsureTopmost()` → `SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)` a cada tick.
- `Render(Badge?)` → limpa o canvas; se `null`, deixa vazio; senão converte pixel→DIP (÷ DPI e
  descontando a origem da tela virtual) e desenha 1 `Border`+`TextBlock`.

### `Native` (P/Invoke)
- `GetCursorPos`, `WindowFromPoint`, `GetAncestor(GA_ROOT=2)` → onde está o cursor e em qual janela-raiz.
- `EnumWindows`/`IsWindowVisible`/`GetWindowText*`/`GetWindowRect` → localizar a janela do jogo por título.
- `CaptureBgra(rx0,ry0,w,h,bw,bh)` → screen-grab GDI upscalado → bytes BGRA (`null` se GDI falhar).

### WinRT
- `OcrEngine.TryCreateFromUserProfileLanguages()` (pode ser `null`), `OcrEngine.RecognizeAsync`,
  `SoftwareBitmap`, `CryptographicBuffer` — OCR nativo do Windows, sem dependência externa.

### `EngineService` (só p/ log)
- `RaiseLog(msg)` → roteia o log do overlay para a barra de status (no thread da UI) e para o
  `session.log`. O overlay **não** chama nenhum método de leitura/escrita de memória do Engine.

### `MarketDb` (co-locado, **não é do overlay**)
- `GetPriceAsync(name)` → busca ao vivo na Steam (priceoverview, USD, cache 6 h). Usado só pela **busca
  manual**, não pelo overlay (o overlay usa o índice embutido `PriceIndex`).

---

## 8. Notas para o redesign (o que PRESERVAR na função)

**Essencial (não pode mudar sem quebrar a feature):**
1. **Opt-in, OFF por padrão.** Nunca ligar o overlay sozinho no boot. O usuário liga pelo toggle.
2. **As duas guardas do `Toggle`**: sem OCR do Windows → não liga + loga; índice vazio → não liga + loga.
   Falha sempre **silenciosa para OFF** (retorna false), nunca exceção para a UI.
3. **Só dispara com o mouse PARADO (≤22 px) e SOBRE o jogo** (checagem por `GetAncestor==gameHwnd`).
   Sem isso, o badge piscaria por toda a tela.
4. **Cadência de 220 ms + guarda `_busy`** — o OCR é async e pode passar de 220 ms; reentrar trava a UI.
5. **`EnsureTopmost` a cada tick** — o jogo/Steam reafirmam topmost; sem isso o badge some atrás do jogo.
6. **Janela CLICK-THROUGH**: `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` +
   `IsHitTestVisible=false` + `Focusable=false` + `ShowActivated=false`. **Jamais** roubar clique/foco do
   jogo — é o requisito nº 1 do overlay.
7. **Owner = MainWindow** — o overlay tem que morrer com o app (senão segura o processo).
8. **Anti-piscar** (repetir último badge ≤6 frames se cursor mal moveu) — sem isso o preço tremela.
9. **Casamento de grade + nome**: linha `<Grade> Grade` mais próxima do cursor → nome nas linhas ACIMA
   (janela de −6 a 200 px acima, mais próxima primeiro) → `ResolveBase` (exato > fuzzy 0.70) →
   `PriceOf`. É este pipeline que dá o preço; preserve a ordem e os limiares.
10. **Semântica das 5 cores/estados do badge** (verde ≥$1, oliva <$1, `~$` aproximado, `—` sem preço,
    Untradable) — são a linguagem visual da feature.

**Armadilhas conhecidas:**
- O badge usa **cores hex fixas**, não o tema. Se for "temar" o badge, lembre que ele é uma janela
  OS-level fora do `ResourceDictionary` do painel; a legibilidade sobre o tooltip do jogo é o que importa,
  não a consistência com o painel.
- **Toggle é `Button`, não CheckBox** — o estado ON é só texto (`ON`) + `Accent.Button`. Se trocar por um
  switch do tema, replique a lógica `Toggle()`/log e o fato de **não persistir** (reseta a OFF no restart).
- Captura é **screen-grab** (não `PrintWindow`): funciona porque lê a tela composta; um redesign que
  troque para captura da janela precisa lidar com a natureza click-through do próprio overlay.
- `Scale=1.7` e `Radius=460` são calibrações do OCR/tooltip do jogo — mexer nelas muda taxa de acerto.
- A busca da janela é **por título** (`TaskBarHero`/`TBH`); mudança de título do jogo cega o overlay
  (diferente do resto do painel, que acha por PID/processo).
- O overlay **não** valida `_svc.IsAttached` — funciona mesmo sem o Engine attachado; não adicione essa
  dependência achando que "conserta" algo.
