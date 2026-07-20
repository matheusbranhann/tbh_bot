# Aba RUNES — Documentação de Layout/Comportamento

> Fonte da verdade da **função** (não do visual atual). Uma IA/pessoa deve conseguir
> **redesenhar** esta tela sem adivinhar comportamento.
>
> Código-fonte: `src/TbhBot.App/Views/RunesView.cs`
> Tema: `src/TbhBot.App/Theme/Dark.xaml`
> Backend: `src/TbhBot.Core/Game/RuneDefs.cs`, `RuneLevels.cs`, `SaveData.cs` (métodos `ReadRunes`/`SetRune`)
> Serviço: `src/TbhBot.App/Services/EngineService.cs`

---

## 1. Propósito

A aba Runes é uma **árvore de runas navegável** (~197 runas). O usuário navega o grafo
com **zoom** (roda do mouse) e **pan** (arrastar), **clica** numa runa para abrir um painel
de informações à direita, e desbloqueia/sobe/maxa runas — sempre com **clamp no teto por-runa**
(passar do máximo causa NRE em `RuneNode.mav` = loading infinito). A tabela de runas é
100% client-side (escrita direta em memória, sem moeda, persiste). Botões em massa no topo
desbloqueiam tudo de uma vez.

---

## 2. Estrutura visual atual

Raiz = `DockPanel` (margem 10) com `LastChildFill = true`. Ordem de composição
(define quem ocupa o quê):

```
+--------------------------------------------------------------------------------+
| [🔓 Desbloquear TUDO (máx)] [Tudo nível 1] [⟳ Refresh]        (bar, Dock=Top)  |
+--------------------------------------------------------------------------------+
| 187/197 desbloqueadas · roda = zoom, arrastar = mover, ...   (status, Dock=Top)|
+----------------------------------------------------------+---------------------+
|                                                          |  PAINEL DE INFO     |
|   VIEWPORT (Canvas com zoom/pan) — preenche o resto      |  (Border 280px,     |
|                                                          |   Dock=Right)       |
|        ┌────┐        ┌────┐                              |  ┌───────────────┐  |
|        │icon│────────│icon│   linhas = conexões          |  │ [ícone 44x44] │  |
|        └────┘        └────┘                              |  │ Nome (Acc)    │  |
|         3/10          0/8      (badge "N/máx" abaixo)    |  │ Efeito: ...   │  |
|                                                          |  │ Categoria: .. │  |
|            ┌────┐                                        |  │ Nível: 3 / 10 │  |
|            │icon│  (borda colorida por categoria)        |  │ Custo p/ Lv.4 │  |
|            └────┘                                        |  │ Liga: A, B    │  |
|             5/5  (dourado = maxada)                      |  │ [🔓/➕/⏫ ação]│  |
|                                                          |  └───────────────┘  |
+----------------------------------------------------------+---------------------+
```

**Agrupamento lógico:**

- **Barra superior** (`WrapPanel`, Dock=Top, margem inferior 8): 3 botões de ação global.
  *NÃO há mais botões de categoria — foram removidos; o desbloqueio individual é por clique na runa.*
- **Linha de status** (`TextBlock` estilo `Sub.Text`, Dock=Top): contador + dica de navegação.
- **Painel de info** (`Border` estilo `Card.Border`, `Width=280`, margem esquerda 8, Dock=Right):
  `ScrollViewer` vertical → `StackPanel _info` com ícone, nome, atributos e botões de ação.
- **Viewport** (preenche o restante): `Border` (`Card.Border`, `Padding=0`) contendo um
  `Border` interno (`ClipToBounds=true`, cursor `SizeAll`) que hospeda o `Canvas`
  (background `#191920`) com `MatrixTransform` como `RenderTransform`. O Canvas desenha
  as **linhas** (conexões) primeiro, depois os **nós** (runas) e os **badges** de nível.

---

## 3. Tabela de CONTROLES

Legenda de fontes: `Defs` = `Engine.RuneDefs.Read()` (estático, `RuneDef{Key,Name,Max,Next[],Icon}`),
`Levels` = `Engine.Save.ReadRunes()` (`{RuneKey→Level}`), `Rows` = `Engine.RuneLevels.Read()`
(`{RuneKey→[RuneLevelRow{Level,Value,Cost,Status}]}`).

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte de dado) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| Desbloquear TUDO | `Button` (estilo `Accent.Button`, laranja) | `🔓 Desbloquear TUDO (máx)` | visível sempre | — | `Bulk(toMax:true)`: se `!IsAttached \|\| _busy` retorna; senão em background varre `Engine.RuneDefs.Read()` e p/ cada runa chama `Engine.Save.SetRune(k, min(d.Max, d.Max))` = `SetRune(k, d.Max)`. Status vira `"aplicando…"` → `"todas no máximo"` (ou `"falha (jogo fechado?)"`), depois `Refresh()` | Todos os nós ficam maxados (borda dourada), badges `mx/mx`, status recontado | Só age se `IsAttached && !_busy` (sem desabilitar visualmente o botão) |
| Tudo nível 1 | `Button` (estilo padrão) | `Tudo nível 1` | visível sempre | — | `Bulk(toMax:false)`: mesma guarda; p/ cada runa `SetRune(k, min(1, d.Max))`. Status `"aplicando…"` → `"todas nível 1"` → `Refresh()` | Todas as runas ficam Lv.1 (desbloqueadas); contador `X/197` sobe p/ 197/197 | Só age se `IsAttached && !_busy` |
| Refresh | `Button` (estilo padrão) | `⟳ Refresh` | visível sempre | — | Chama `Refresh()` (releitura completa em background) | Redesenha canvas + status + painel de info do selecionado | sempre clicável |
| Viewport — Zoom | `Border` (evento `MouseWheel`) | — | escala inicial 0.6 (auto-center) | escala atual `_mt.Matrix.M11` | `OnWheel`: fator `1.15` (roda p/ cima) ou `1/1.15` (p/ baixo); escala-alvo = `Clamp(M11*f, 0.2, 3.0)`; `ScaleAt(f,f, cursor.X, cursor.Y)` — **zoom centrado no cursor** | Matriz de transformação do canvas (tudo escala junto) | sempre ativo |
| Viewport — Pan | `Border` (Down/Move/Up, botão esquerdo) | — | sem pan | posição do mouse no viewport | `OnDown`: captura mouse, `_panning=true`, `_panMoved=false`. `OnMove`: translada a matriz pelo delta; se deslocou > 4px marca `_panMoved`. `OnUp`: solta captura; se `_panMoved` → **era pan, não seleciona** | Deslocamento (translate) da matriz | sempre ativo |
| Viewport — Clique (seleção) | Hit-test no `Canvas` | — | nenhuma runa selecionada (`_selected=0`) | `_pos` (bbox 40×40 de cada nó) | `OnUp` sem arrasto: converte para coords do canvas (`e.GetPosition(_canvas)`, já com transform); acha o nó cujo box `[x,x+40]×[y,y+40]` contém o ponto → `Select(k)`. Se nenhum: status `"clique em (X,Y) — nenhuma runa aí"` | Nó ganha borda branca (`SelBorder`, espessura 3); painel de info à direita é preenchido | sempre ativo |
| Info: ScrollViewer | `ScrollViewer` (vertical auto) | — | conteúdo curto (sem scroll) | conteúdo do `_info` | Rola o painel quando o conteúdo excede 280px de largura/altura | — | scroll aparece só se transbordar |
| Info: Desbloquear | `Button` (`Accent.Button`) | `🔓 Desbloquear` | **só existe quando `lv <= 0`** | `Levels[key]` (nível atual) | `SetRune(key, 1)` → clamp `min(1, d.Max)` → `Engine.Save.SetRune(key, tgt)` em background; status `"aplicando…"` → `"runa '<Nome>' → Lv.<tgt>"` → `Refresh()` | Nó vira Lv.1 (borda de categoria, ícone opacidade 1.0); painel recarregado com botões de Lv+1/Maxar | Só renderizado se `lv <= 0`; só age se `IsAttached && !_busy` |
| Info: Level +1 | `Button` (`Accent.Button`) | `➕ Level +1` | **só existe quando `lv>0 && lv<mx`** | `Levels[key]`, `d.Max` | `SetRune(key, min(lv+1, mx))` → clamp adicional `min(target, d.Max)` → escreve. Status idem acima | Nível/badge sobem +1; painel recarregado; se atingir o teto, aparece `✓ no máximo` | Só renderizado se `0 < lv < mx` |
| Info: Maxar | `Button` (estilo padrão) | `⏫ Maxar (Lv.<mx>)` | **só existe quando `lv < mx`** | `Levels[key]`, `d.Max` | `SetRune(key, mx)` → clamp `min(mx, d.Max)` = `mx` → escreve. Status idem | Nó vira dourado (`MaxedBorder`), badge `mx/mx`, painel mostra `✓ no máximo` | Só renderizado se `lv < mx` |

> Observação: os botões NÃO usam `IsEnabled=false` como gate — a guarda real é
> `if (!_svc.IsAttached \|\| _busy) return;` dentro dos handlers (`SetRune`/`Bulk`).
> Com o jogo fechado o clique simplesmente não faz nada (ou o status já mostra "jogo fechado").

---

## 4. Comportamento vivo

- **Auto-refresh por timer:** `DispatcherTimer` com `Interval = 3s`; cada tick chama `Refresh()`.
  Iniciado no evento `Loaded` (junto com um `Refresh()` imediato) e parado no `Unloaded`.
- **Refresh por evento de conexão:** `_svc.StateChanged += Refresh` — quando o engine
  attacha/desconecta do jogo, a aba re-renderiza. Removido no `Unloaded`.
- **Leitura em background:** `Refresh()` roda `Defs/Levels/Rows` dentro de `Task.Run(...)`
  e só volta ao thread da UI via `Dispatcher.Invoke(Render(...))`. Guarda dupla de
  `IsAttached` (antes e dentro do Task) e `try/catch` que aborta silenciosamente.
  Guarda `if (_busy) return;` evita reentrância enquanto uma escrita está em curso.
- **Auto-centralização (uma vez só):** flag `_centered`. Na 1ª renderização com nós,
  calcula o **centróide** (`média(x)+20`, `média(y)+20`), aplica escala `0.6` e translada
  para colocar o centróide no centro do viewport. Usa `Dispatcher.BeginInvoke(..., ContextIdle)`
  para esperar o viewport ter tamanho. Depois disso o zoom/pan do usuário é preservado
  entre refreshes (não recentraliza).
- **Persistência da seleção:** ao fim de `Render`, se `_selected != 0` e ainda existe em
  `defs`, o painel de info é reconstruído (`ShowInfo(_selected)`) com os valores novos —
  então o painel "acompanha" a runa selecionada a cada refresh.
- **Escrita → refresh:** `WriteThen` seta `_busy=true`, status `"aplicando…"`, roda o trabalho
  em `Task.Run`, e no fim (`Dispatcher.Invoke`) limpa `_busy`, mostra `okMsg`/`"falha"` e chama
  `Refresh()`.

---

## 5. Estados especiais

- **Jogo fechado (`!_svc.IsAttached`):** `Refresh()` limpa o canvas e escreve
  `"jogo fechado — reabra para as runas"`. O painel de info **não** é limpo (mantém o último).
  Os handlers de escrita (`SetRune`/`Bulk`) retornam cedo, sem efeito.
- **Defs vazio / offsets ainda resolvendo:** se `defs is null || defs.Count == 0`, o canvas
  fica limpo e status = `"jogo fechado ou resolvendo offsets…"` (o scan por klass `RuneInfoData`
  ainda não achou nada — costuma ser transitório logo após o boot do jogo).
- **Clique fora de qualquer nó:** status = `"clique em (X,Y) — nenhuma runa aí"` (coords do canvas).
- **Painel sem seleção (`ShowInfoEmpty`):** mostra só `"clique numa runa"` em cor `Subtle`.
- **Efeito indisponível:** se não há `RuneLevelRow` (`status = -1`), a linha Efeito mostra `—`.
- **Custo indisponível:** a linha "Custo p/ Lv.N" só aparece se `lv < mx` **e** existe a linha
  do nível-alvo (`Rows` com `Level == nextLv`, ou fallback `nextLv-1`).
- **Runa no máximo:** sem botões de ação; aparece a linha `✓ no máximo` (texto dourado).
- **Ícone ausente:** `Icon(name)` retorna `null` (base64 não encontrado no recurso embutido
  `runes_icons.json`) → o nó/painel simplesmente não desenha imagem, sem erro.
- **Escrita falha:** exceção ou desconexão no meio → status `"falha (jogo fechado?)"`.

---

## 6. Cores/estilo usados hoje

**Estilos do tema (`Dark.xaml`) referenciados:**
- `Accent.Button` — botão laranja de destaque (fg `#0A0A0A` sobre `Acc #FF7A18`): usado em
  "Desbloquear TUDO", "Desbloquear", "Level +1".
- `Card.Border` — card padrão (fundo `Card #16181C`, borda `Stroke #2B2E34`, raio 10): usado no
  card do viewport e no painel de info.
- `Sub.Text` — mono cinza (`Sub #9AA0AB`, 11px): linha de status.
- Brushes de recurso via `B(key)`: `Fg #FFFFFF`, `Acc #FF7A18` (título do painel), `Sub #9AA0AB`
  (custo), `Subtle #666B74` (conexões, placeholder, badge de runa travada).
- Fonte `Mono` (Consolas) nos badges de nível.

**Cores fixas (hex embutidos no `RunesView`, `Frozen(...)`):**

| Constante | Hex | Significado |
|---|---|---|
| Canvas background | `#191920` | fundo do grafo |
| `NodeFill` | `#26262e` | fundo de todo nó (runa) |
| `LineBrush` | `#33333f` | linhas de conexão entre runas |
| `LockedBorder` | `#34343e` | borda de runa **travada** (nível 0); ícone com opacidade 0.45 |
| `MaxedBorder` | `#ffd24a` (dourado) | **runa maxada** (`lv>=mx && lv>0`): borda, badge e texto "no máximo"/"Nível" |
| `SelBorder` | `#ffffff` (branco) | **runa selecionada**: borda branca, espessura 3 (vence todas as outras) |

**Cores por CATEGORIA** (borda do nó quando desbloqueada e não-maxada; e cor da linha "Efeito"):

| Categoria | Hex | Rótulo (`CatLabel`) | Regra de classificação (`RuneCat`, por substring no nome, minúsculo) |
|---|---|---|---|
| `chest` | `#f0a83a` (laranja) | Caixa/Drop | contém `chest`/`drop`/`openall`/`openone`/`autoopen`/`wavecount` |
| `combat` | `#e5564c` (vermelho) | Combate | contém `attackdamage`/`attackspeed`/`armor`/`movespeed` |
| `gold` | `#e8c93a` (amarelo) | Ouro | contém `gold` |
| `exp` | `#5cbf6a` (verde) | EXP | contém `exp` |
| `util` | `#4bb0cc` (ciano) | Utilidade | qualquer outra (fallback) |

**Precedência da borda do nó** (em `AddNode`): `selecionado (branco)` > `travado (#34343e)` >
`maxado (dourado)` > `cor da categoria`. Espessura da borda = 3 se selecionado, senão 2.

**Badge de nível** (`TextBlock` abaixo do nó, `"lv/mx"`, mono 10 bold, centralizado, 40px de
largura): cor = dourado se maxado, `Fg` branco se `lv>0`, `Subtle` cinza se travado.

---

## 7. Chamadas de backend

Métodos do Engine/serviços usados pela aba:

- **`_svc.IsAttached`** (`EngineService`) — `Engine.IsAttached && Target.IsAlive()`. Guarda de
  todas as leituras/escritas.
- **`_svc.StateChanged`** (evento) — dispara `Refresh` no thread da UI ao conectar/desconectar.
- **`Engine.RuneDefs.Read()`** → `Dictionary<int, RuneDef>` — varre a memória pela klass
  `TaskbarHero.Data.RuneInfoData` e extrai por instância: `RuneKey` (@0x30), `MaxLevel` (@0x34,
  = teto por-runa; 0 vira 1), `NameKey` (@0x38, sem prefixo `RuneName_`), conexões `Next1/Next2`
  (@0x40/@0x48, tokens numéricos) e `IconPath` (@0x58). **Estático → cacheado** após 1º sucesso.
- **`Engine.Save.ReadRunes()`** → `Dictionary<int, int>` — lê a lista de `RuneSaveData` em
  `PSD + RuneListOff` (batch) e mapeia `RuneKey`(@0x10) → `Level`(@0x14). Fonte do nível **atual**.
- **`Engine.RuneLevels.Read()`** → `Dictionary<int, List<RuneLevelRow>>` — varre a klass
  `RuneLevelInfoData` e extrai por linha: `Level`(@0x34), `Value`(@0x38), `Cost`(@0x3C, ouro),
  `Status`(@0x40 = índice de `EAccountStatus` = o **efeito** da runa). Ordenado por nível. Estático → cacheado.
- **`Engine.Save.SetRune(key, level)`** → `bool` — acha o `RuneSaveData` de `key` e escreve
  `Level`(@0x14). Só faz `Math.Max(0, level)` — **NÃO clampa o teto**; o clamp por-runa é
  responsabilidade do chamador (`SetRune`/`Bulk` no view usam `min(target, d.Max)`).
- **`RuneLevels.EffectName(status)`** (estático) — traduz o índice `EAccountStatus` para nome
  legível espaçado (ex.: `AllHeroAttackDamagePercent` → "All Hero Attack Damage Percent").
- **`RuneLevels.IsPercent(status)`** (estático) — `true` se o nome do efeito termina em "Percent"
  (adiciona sufixo " (%)" na linha Efeito).

---

## 8. Notas para o redesign

**Essencial preservar na FUNÇÃO (não importa o visual):**

1. **CLAMP no teto por-runa é obrigatório e crítico.** Toda escrita passa por
   `min(target, d.Max)`. Passar do máximo causa **NRE em `RuneNode.mav` = loading infinito**
   no jogo (recuperável só por backup ES3). Nunca oferecer "+1" além do máximo; "Maxar" grava
   exatamente `d.Max`. `SaveData.SetRune` **não** protege o teto — quem chama tem que proteger.
2. **Três ações por runa, condicionais ao nível:** Desbloquear (só em Lv.0), Level +1
   (só em `0<lv<mx`), Maxar (só em `lv<mx`). No máximo, nenhuma ação — só o rótulo `✓ no máximo`.
3. **Seleção vs. pan:** distinguir clique de arrasto pelo limiar de **4px** (`_panMoved`). Arrastar
   nunca deve selecionar; clique parado seleciona a runa cujo box 40×40 contém o ponto **em
   coordenadas do canvas** (pós-transform).
4. **Zoom centrado no cursor + clamp `0.2..3.0`;** pan por translate da matriz. Preservar o
   estado de navegação entre refreshes (só centraliza uma vez, no centróide, escala 0.6).
5. **Árvore por BFS:** raízes = nós sem pai (ordenadas por key); profundidade define a **coluna**
   (x = `PAD + depth*104`), e a linha (y) de folhas incrementa um contador enquanto nós internos
   ficam na **média** dos filhos (`RH=58`, `PAD=28`). Nós órfãos/ciclos caem em depth 0. As
   **conexões** (`Next[]`) devem ser desenhadas como linhas entre os centros dos nós.
6. **Semântica de cor é informação, não decoração:** dourado = maxada, branco = selecionada,
   cinza-escuro + ícone esmaecido = travada, cor da categoria = desbloqueada. O badge `N/máx`
   e o contador de status `X/197 desbloqueadas` (runas com `Level>=1`) são leitura essencial.
7. **Efeito vem do `RuneLevels`, não do `RuneDefs`:** o `RuneInfoData` não tem descrição — o
   efeito exibido usa `rows[0].Status` → `EAccountStatus`. O custo do próximo nível vem da linha
   `RuneLevelInfoData` do nível-alvo.
8. **Tudo é background + guarda de `IsAttached`.** Leituras nunca no thread da UI; escritas com
   flag `_busy` p/ evitar reentrância; timer de 3s + evento `StateChanged` mantêm vivo. Com o
   jogo fechado, degradar para mensagens ("jogo fechado…", "resolvendo offsets…") sem quebrar.

**Armadilhas conhecidas:**

- Não desabilitar botões por `IsEnabled` (o código não faz) — a proteção real está nos handlers;
  se o redesign optar por desabilitar, manter as guardas `IsAttached && !_busy` de qualquer forma.
- O painel de info **não** limpa ao desconectar; se quiser limpar, cuidar para não perder o
  `_selected` que é re-hidratado a cada `Render`.
- `RuneDefs`/`RuneLevels` são **cacheados** (estáticos): trocar de conta/atualização de jogo não
  reflete sem `force=true`/re-attach; o layout confia nisso para performance (~197 runas por scan).
```

---
*9 controles interativos documentados na tabela (3 botões de barra, 3 interações do viewport —
zoom/pan/clique-seleção, 1 ScrollViewer, 3 botões de ação do painel — condicionais ao nível).*
