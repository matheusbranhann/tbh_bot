# 05 — Aba Market (busca de preço Steam + overlay de preço)

> Fonte da verdade da **função** desta aba (não do visual atual). Quem redesenha pode mudar cores,
> disposição e componentes, mas tem que preservar 100% do comportamento descrito aqui.
>
> Código-fonte: `src/TbhBot.App/Views/MarketView.cs` (a tela) · `src/TbhBot.Core/Market/MarketDb.cs`
> (preço Steam) · `src/TbhBot.App/Services/OverlayService.cs` + `OverlayWindow.cs` (overlay) ·
> `src/TbhBot.Core/Market/PriceIndex.cs` (índice de preços do overlay). Estilos em `Theme/Dark.xaml`.

---

## 1. Propósito

Consultar o **preço (USD) de um item no Steam Community Market** do TaskbarHero (appid `3678970`) pelo
**nome exato do mercado**, e manter um histórico visual das buscas. Além disso, liga/desliga o **overlay de
preço**: um HUD topmost/click-through que, com o mouse parado sobre um item **dentro do jogo**, lê o
tooltip por OCR e desenha o preço Steam ao lado dele.

**Ponto importante para o redesign:** esta aba é a única que **NÃO usa o jogo/Engine**. Preços são externos
ao jogo (vêm da web da Steam) e o índice do overlay é embutido no exe. O construtor recebe `EngineService`
**só por convenção das abas** — usa apenas `svc.RaiseLog(...)` para mandar as mensagens do overlay para a
barra de status. **Não há nenhum guarda de `_svc.IsAttached` nesta tela.** Ela funciona com o jogo fechado.

---

## 2. Estrutura visual atual

Tudo dentro de um único `StackPanel` vertical (raiz, `Margin=16`), empilhado de cima para baixo. Sem cards
de agrupamento na moldura da aba (o único uso de card é em cada linha de resultado).

```
┌──────────────────────────────────────────────────────────────────┐
│  Steam Community Market                       (título, 18pt/SemiBold, Fg) │
│  Busca o preco (USD) de um item pelo nome exato do mercado.  (subtítulo, Sub) │
│                                                                    │
│  [ ◉  Price overlay: OFF ]      ← botão toggle, alinhado à esquerda │
│  passe o mouse sobre um item no jogo → mostra o preço Steam ...     │  (subtítulo, Sub)
│                                                                    │
│  ┌───────────────────────────────────────────┐ ┌────────────┐     │
│  │ TextBox de busca (1*)                       │ │  Buscar    │     │  ← linha de busca (Grid 2 col)
│  └───────────────────────────────────────────┘ └────────────┘     │
│                                                                    │
│  ┌ ScrollViewer (vertical Auto) ───────────────────────────────┐   │
│  │  ┌─ card resultado (mais recente no TOPO) ───────────────┐   │   │
│  │  │  Nome do item (1*, elipse)                    $12.34   │   │   │  ← preço em Acc (laranja) se tem
│  │  ├───────────────────────────────────────────────────────┤   │   │
│  │  │  Outro item                                  sem preço │   │   │  ← "sem preço" em Red se não tem
│  │  └───────────────────────────────────────────────────────┘   │   │
│  └───────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

**Agrupamento lógico** (de cima para baixo):
1. **Cabeçalho** — título + subtítulo explicativo.
2. **Bloco do overlay** — botão toggle + subtítulo explicativo (separado da busca).
3. **Bloco de busca** — `Grid` de 2 colunas: `TextBox` (largura `1*`) + botão `Buscar` (largura `Auto`,
   `MinWidth=90`, margem esquerda 8).
4. **Lista de resultados** — `ScrollViewer` (scroll vertical `Auto`) contendo um `StackPanel` (`_results`)
   onde cada busca vira um `Border`-card inserido no índice 0 (topo).

Cada **card de resultado** é um `Border` (estilo `Card.Border`, `Margin=0,0,0,6`) com um `Grid` interno
(`Margin=10`, 2 colunas `1*` / `Auto`): à esquerda o **nome** (`TextTrimming=CharacterEllipsis`), à direita
o **preço**.

---

## 3. Tabela de CONTROLES

Três controles interativos. Os cards de resultado são **somente-leitura** (sem clique) e estão descritos
na seção 4.

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte de dado) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| `_ovBtn` | `Button` (toggle manual) | `◉  Price overlay: OFF` / `◉  Price overlay: ON` | `OFF`, estilo de botão **padrão** (Card2/Stroke2) | Estado do `OverlayService` (`_overlay.Enabled`) | Clique → `bool on = _overlay.Toggle()`. `Toggle()` liga se estava desligado (cria `OverlayWindow` + timer 220 ms) ou desliga se estava ligado. **Não é toggle cego:** se não conseguir ligar (OCR do Windows indisponível **ou** índice de preços vazio), `Toggle()` retorna `false` e loga o motivo — o botão **permanece OFF**. O texto passa a `ON`/`OFF` conforme o retorno, e o estilo vira `Accent.Button` (laranja) quando `on==true`, ou volta ao estilo padrão (`Style=null`) quando `off`. | Próprio texto + próprio estilo (accent quando ON). O overlay em si passa a desenhar badges sobre o jogo. Mensagens do overlay vão para a barra de status via `RaiseLog`. | Sempre habilitado (nunca desabilita). O "não ligou" é sinalizado por permanecer OFF + log, não por disable. |
| `_search` | `TextBox` | (vazio; sem placeholder) | vazio, foco não forçado | Texto digitado pelo usuário (o nome exato do item de mercado) | **Enter** (`KeyDown`, `Key.Enter`) → dispara `DoSearchAsync()` (mesma ação do botão Buscar). Digitação normal só edita o texto. | O texto é limpo (`Clear()`) e o foco volta para ele ao fim de cada busca (bloco `finally`). | Sempre habilitado. |
| `_btn` | `Button` | `Buscar` | habilitado, estilo `Accent.Button` (laranja) | `_search.Text` (via `DoSearchAsync`) | Clique → `DoSearchAsync()`: `name = _search.Text.Trim()`. Se `name.Length==0` → **no-op** (retorna sem fazer nada). Senão: desabilita o botão, guarda o texto atual e troca o conteúdo para `"..."`; chama `await _db.GetPriceAsync(name)`; passa o `decimal? price` para `AddResult(name, price)`. Qualquer exceção → `AddResult(name, null)`. No `finally`: restaura o texto do botão, reabilita, limpa o `TextBox` e devolve o foco a ele. | Insere um card de resultado no topo da lista; enquanto busca, o próprio label vira `"..."` (indicador de carregando). | Desabilitado **durante** a busca (`IsEnabled=false` no começo de `DoSearchAsync`, reabilitado no `finally`). Fora disso, sempre habilitado. |

> **Atenção à cor do preço no card (a task pediu "verde/vermelho", mas o código real é diferente):**
> na **lista de resultados**, o preço presente usa o brush **`Acc` (laranja `#FF7A18`)**, não verde. Só o
> **overlay** (seção 8) desenha badges em **verde**. "Sem preço" usa `Red` (`#F87171`) nos dois lugares.

---

## 4. Comportamento vivo

- **A aba em si NÃO tem timer / auto-refresh.** Nada nela repinta sozinho. Os cards são estáticos depois de
  criados; a lista só cresce quando o usuário busca. Ao contrário das outras abas, não há `DispatcherTimer`
  lendo memória.
- **Busca assíncrona:** `DoSearchAsync` é `async` — a UI não trava enquanto espera a rede. Durante a espera,
  o botão fica desabilitado e mostra `"..."`.
- **Card de resultado:** criado por `AddResult(name, price)` e **inserido no índice 0** (`_results.Children.Insert(0, card)`) → **mais recente sempre no topo**. Nunca remove cards antigos (o histórico da sessão
  cresce indefinidamente; some só ao trocar de aba/fechar o app, pois não persiste).
- **Cache de preço (dentro do `MarketDb`, transparente para a UI):** cada preço fica em cache por até
  **6 horas** (TTL) em memória e em disco (`market_prices.json` ao lado do exe). Buscar o mesmo item dentro
  da janela de 6 h devolve o valor cacheado **sem** bater na Steam. Isso é invisível na tela, mas explica
  por que buscas repetidas podem ser instantâneas.
- **Overlay (quando ON):** roda um `DispatcherTimer` próprio a **220 ms** por tick. Cada tick garante
  topmost, captura a região sob o cursor, faz OCR e (re)desenha o badge. É a única parte "viva" — e vive na
  **janela do overlay**, não na aba. Detalhe completo no doc do overlay; resumo na seção 8.

---

## 5. Estados especiais

- **Jogo fechado:** a **aba de busca funciona normalmente** (preço é da web, não do jogo). Não há guarda de
  `IsAttached`. O **overlay**, porém, só desenha badges quando encontra a janela do jogo pelo título
  (`"TaskBarHero"` ou `"TBH"`); com o jogo fechado, `ScanAsync` não acha a janela e simplesmente não mostra
  badge (o overlay pode ficar ON, mas fica "vazio"). Ligar o overlay com o jogo fechado não dá erro.
- **Busca vazia:** `name` vazio após `Trim()` → `DoSearchAsync` retorna sem criar card (no-op silencioso).
- **Sem preço / erro de rede / rate-limit (HTTP 429):** `GetPriceAsync` nunca lança — devolve `null` (ou um
  valor **stale** do cache, se existir, mesmo vencido). O card mostra **"sem preço"** em vermelho quando o
  resultado final é `null`. `DoSearchAsync` ainda tem `try/catch` como cinto-e-suspensório, também caindo em
  `AddResult(name, null)`.
- **Overlay não liga (2 guardas em `Toggle()`):**
  - `_ocr is null` (nenhum idioma de OCR instalado no Windows) → loga
    `"overlay: OCR do Windows indisponível (instale um idioma) — não liguei"` e retorna `false`.
  - `_prices.Count == 0` (índice `item_prices.json` embutido ausente/vazio) → loga
    `"overlay: índice de preços vazio"` e retorna `false`.
  - Em ambos, o botão continua **OFF** (o texto e o estilo refletem o `false` retornado).
- **Overlay ON:** logs `"◉ Price overlay: ON (passe o mouse sobre um item)"` ao ligar e
  `"◉ Price overlay: OFF"` ao desligar, na barra de status.
- **Preço `$0.00` / abaixo de $1:** o card ainda formata `"$0.00"` (formato `0.00`, cultura invariante). No
  **overlay**, valores `< 1` são pintados numa cor de face diferente (verde-oliva) — ver seção 6.
- **Nome com preço mas item "Untradable" (só no overlay):** o overlay desenha o badge **"Untradable"** (não
  vendável na Steam). Isso é do overlay, não da busca por texto.

---

## 6. Cores/estilo usados hoje

**Do tema (`Dark.xaml`) — na aba:**

| Elemento | Recurso do tema | Cor / significado |
|---|---|---|
| Título "Steam Community Market" | brush `Fg` | `#FFFFFF` branco — texto primário |
| Subtítulos (2×) | estilo `Sub.Text` | `Sub` `#9AA0AB`, fonte mono (Consolas) 11pt — texto auxiliar |
| Nome do item (card) | brush `Fg` | `#FFFFFF` branco |
| Preço presente (card) | brush `Acc` | `#FF7A18` **laranja** — "tem preço" (**não** é verde) |
| "sem preço" (card) | brush `Red` | `#F87171` vermelho — "sem preço/erro" |
| Card de resultado | estilo `Card.Border` | fundo `Card` `#16181C`, borda `Stroke` `#2B2E34`, raio 10, padding 12 |
| Botão `Buscar` | estilo `Accent.Button` | fundo `Acc` `#FF7A18`, texto `AccTxt` `#0A0A0A`, hover `AccH` `#FF9440` — ação primária |
| `TextBox` de busca | estilo `TextBox` (padrão) | fundo `Card`, borda `Stroke`, mono, caret `Acc` |
| Toggle overlay **OFF** | estilo `Button` padrão | fundo `Card2` `#212429`, borda `Stroke2` `#3D4149` — inativo |
| Toggle overlay **ON** | estilo `Accent.Button` | laranja — **ativo/ligado** |

**Cores fixas em hex (só no overlay — badges, `OverlayService.ReadBadge` / `OverlayWindow.Render`):** essas
**NÃO** são do tema; são hardcoded no serviço. Face = fundo do badge, Ink = tinta do texto.

| Situação do badge | Face (fundo) | Ink (texto) | Significado |
|---|---|---|---|
| Preço ≥ $1 | `#146c2e` verde | `#eaffea` | tem preço "cheio" |
| Preço < $1 | `#3d5a1e` verde-oliva | `#eaffea` | tem preço, baixo |
| Grade conhecido, preço desconhecido | `#242424` | `#7a7a7a` | mostra `—` |
| Item "Untradable" | `#3a2323` vinho | `#d08a8a` | não vende na Steam |
| Aproximação (grade não listado) | (mesma face acima) | — | prefixo `~$ ` no texto |

---

## 7. Chamadas de backend

**Serviços/métodos que a aba usa:**

- **`MarketDb.GetPriceAsync(name)` → `Task<decimal?>`** — retorna o **menor** preço (USD) do item ou `null`.
  Fluxo: se houver cache válido (< 6 h) devolve o cacheado; senão consulta o endpoint Steam
  `market/priceoverview/?appid=3678970&currency=1&market_hash_name=<escapado>` (GET, timeout 25 s, UA de
  browser, gzip/deflate). Prefere `lowest_price`, cai para `median_price`. Faz parse de moeda robusto a
  formatos (`$1,234.56`, `1.234,56€`, etc.) via `ParseMoney`. Em erro/429/timeout retorna `null` (nunca
  lança). **Blindagem:** só grava/atualiza o cache quando veio preço de verdade — nunca sobrescreve um bom
  preço por `null`. Persiste em `market_prices.json` (ao lado do exe) com escrita atômica (temp + replace).
- **`OverlayService.Toggle()` → `bool`** — liga/desliga o overlay; retorna o **novo** estado real (ver
  controle `_ovBtn`). Guardas: OCR disponível + índice de preços não-vazio.
- **`OverlayService.Enabled`** (indireto, via retorno de `Toggle`) — se a janela do overlay existe.
- **`OverlayService.Log` (evento)** — no ctor da view: `_overlay.Log += m => svc.RaiseLog(m)`. Roteia todas
  as mensagens do overlay para a barra de status.
- **`EngineService.RaiseLog(msg)`** — coloca a mensagem na barra de status (thread da UI) + grava em
  `%APPDATA%/tbh_bot/session.log`. É o **único** uso que a view faz do `EngineService`.

**Serviços internos do overlay (resumo — detalhe no doc do overlay):**

- **`PriceIndex`** — carrega o `item_prices.json` **embutido** no exe: `base normalizada → {grade: preço}`.
  `ResolveBase(linhas)` acha o item por nome (match exato ou fuzzy ≥ 0.70). `PriceOf(base, grade)` devolve o
  preço do grade do tooltip (materiais = preço único; gear = casa pelo grade, com aproximação para o grade
  mais próximo).
- **`OverlayWindow`** — janela topmost, transparente, click-through (`WS_EX_TRANSPARENT | TOOLWINDOW |
  NOACTIVATE`), cobrindo toda a tela virtual; dona = `MainWindow` (fecha junto com o app). Desenha o badge
  em pixels de tela convertidos para DIP.
- **`OverlayService` (loop):** timer 220 ms → captura a região (raio 460 px) sob o cursor **parado** (movimento
  > 22 px = não desenha) e **sobre a janela do jogo** → upscale 1.7× → `Windows.Media.Ocr` → acha a linha
  `"<X> Grade"` mais próxima do cursor → resolve o nome nas linhas acima → casa no índice → desenha o badge
  ao lado do tooltip. Anti-piscar: segura o último badge por até 6 frames.

---

## 8. O que o overlay faz (resumo)

Com o overlay **ON**, ao **parar o mouse sobre um item dentro do jogo**, o tooltip do jogo abre; o overlay
captura a área sob o cursor, faz **OCR nativo do Windows**, identifica o item (nome + grade escritos no
tooltip), procura o preço Steam no índice embutido e **desenha um pequeno badge de preço ao lado do
tooltip** (janela própria, topmost, click-through — não rouba clique nem foco do jogo). Só dispara com o
mouse parado e sobre a janela do jogo. É um atalho para ver o valor de um item sem sair para buscar por
nome. **Detalhe completo (captura, OCR, resolução de nome/grade, badges) fica no doc do overlay.**

---

## 9. Notas p/ o redesign

**Essencial preservar (função, não visual):**

- **Busca assíncrona não-bloqueante:** `GetPriceAsync` é `await`ado; **desabilitar o botão** durante a busca
  e mostrar um indicador de carregando (hoje é o label virar `"..."`). Reabilitar sempre no `finally`.
- **Enter no campo dispara a busca** (equivalente ao botão). Manter esse atalho.
- **Mais recente no TOPO** da lista (`Insert(0, ...)`), histórico crescente da sessão.
- **Distinção visual "tem preço" vs "sem preço"** por cor. **Armadilha:** a task fala "verde/vermelho", mas
  na lista o "tem preço" real é **laranja (`Acc`)**; o verde é só do overlay. Ao redesenhar, decidir
  conscientemente a cor — não copiar cegamente "verde" achando que reflete o código atual.
- **Limpar + refocar** o campo de busca ao fim de cada busca.
- **Busca vazia = no-op** (não criar card em branco).
- **Preço formatado** com 2 casas, cultura invariante (`0.00`).
- **Toggle do overlay é de 3 estados, não binário cego:** o botão só vai para ON se `Toggle()` retornar
  `true`. Se falhar (OCR indisponível / índice vazio), permanecer OFF e o motivo já vai por log. Refletir o
  **retorno**, não o clique.
- **Estado ON = accent** (destaque visual claro de "ligado").
- **Roteamento de log do overlay para a barra de status** (via `RaiseLog`) — o usuário depende dessas
  mensagens para saber por que o overlay não ligou.

**Armadilhas conhecidas:**

- **Esta aba não depende do jogo.** Não adicionar guardas de `_svc.IsAttached` na busca — ela tem que
  funcionar offline. O `EngineService` está aqui só para `RaiseLog`.
- **Estado do overlay não persiste.** O texto/estilo do botão vivem só na `MarketView`; ao trocar de aba e
  voltar, ou reabrir o app, o botão volta a OFF (mas se o `OverlayService` já estivesse ON, a janela pode
  continuar). Cuidar da sincronia botão↔serviço se o layout recriar a view. (Hoje a view é criada uma vez.)
- **Janela do overlay é dona = MainWindow** → fecha junto com o app. Não recriar sem esse `Owner`, senão o
  overlay segura o processo vivo.
- **Cache de preço nunca deve ser destruído por `null`** (rede/429). Preservar a blindagem do `MarketDb`
  (só grava quando veio preço de verdade). Arquivo `market_prices.json` fica ao lado do exe.
- **Rate-limit da Steam (429):** buscas repetidas rápidas podem devolver `null`/stale. É esperado; não é bug
  da UI. O TTL de 6 h e o cache em disco existem para reduzir isso.
- **Nome tem que ser o `market_hash_name` exato.** A busca é literal (não fuzzy) — o fuzzy só existe no
  overlay (via OCR). Um placeholder/dica no campo ajudando o usuário a colar o nome exato seria útil.
