# Aba INVENTORY — documentação de layout e comportamento

> Fonte da verdade da **FUNÇÃO** (não do visual atual). Uma IA/pessoa que for redesenhar o layout
> deve preservar todo o comportamento descrito aqui, mesmo que troque completamente a aparência.
>
> Código-fonte real:
> - View: `src/TbhBot.App/Views/InventoryView.cs`
> - Motor: `src/TbhBot.Core/Game/Inventory.cs`
> - Tema: `src/TbhBot.App/Theme/Dark.xaml`
> - Serviço: `src/TbhBot.App/Services/EngineService.cs`

---

## 1. Propósito

A aba **Inventory** é uma **LISTA/TABELA read-only** do inventário completo do jogador (inventário +
todos os baús juntos). Para cada tipo de item mostra **Item, Grade, Quantidade, Preço unitário (USD),
Preço total (USD)**, com **cabeçalho clicável que ordena** e um **rodapé de somatório** ("Total: $X · N
itens (M tipos)"). É a porta do `inv_tree` do antigo painel Python (`tbh_panel.py`).

Não há nenhuma ação de escrita: a aba **só lê** memória do jogo e o JSON de preços embutido. Nenhuma
linha é clicável, nenhum item é vendido/movido/apagado por esta tela.

---

## 2. Estrutura visual atual

`Content` = um `DockPanel` raiz (Margin 12). De cima para baixo:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  [ TOPO — DockPanel, dock=Top, margem inferior 10 ]                            │
│  Total: $123.45   ·   87 itens (23 tipos)                        [ ⟳ Refresh ] │  ← _total (verde)      botão à direita
│                                                                                │
│  [ HEADER clicável — DockPanel, dock=Top ]                                     │
│  Item ▲/▼        Grade        Qtd      Unit $      Total ▼                     │  ← 5 colunas clicáveis (setas na coluna ativa)
│                                                                                │
│  [ CARD — Border(Card.Border) + ScrollViewer vertical → StackPanel _rows ]     │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │ Minor Ruby        Common        12     $0.03     $0.36                    │ │  ← linha par (fundo transparente)
│  │ Arcane Shard      Rare           3     $2.50     $7.50                    │ │  ← linha ímpar (fundo Card2)
│  │ …                                                                        │ │
│  └──────────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

Agrupamento lógico:

1. **Barra de topo** (`top`, DockPanel): o **somatório** (`_total`, à esquerda, verde, fonte mono) e o
   botão **⟳ Refresh** (dock=Right). São visualmente a mesma faixa horizontal.
2. **Cabeçalho de colunas** (`header`, `BuildHeader()`): 5 `TextBlock` clicáveis com a seta de ordenação
   na coluna ativa. Dockado logo abaixo do topo, sempre visível (não rola).
3. **Corpo rolável** (`card` → `ScrollViewer` → `_rows`): as linhas de dados, uma por tipo de item,
   com scroll vertical automático e scroll horizontal **desabilitado**.

**Alinhamento de colunas**: header e linhas usam as **mesmas larguras fixas** para as 4 colunas da
direita, e a coluna **Item (nome)** preenche o resto (`Width=0` + fill). As colunas da direita são
dockadas em **ordem inversa** (Total, depois Unit, depois Qtd, depois Grade) para o DockPanel empilhar
da direita para a esquerda; o nome entra por último ocupando o espaço remanescente.

Larguras fixas (const, em px lógicos):

| Coluna  | const  | Valor | Alinhamento do texto |
|---------|--------|-------|----------------------|
| Grade   | WGrade | 120   | esquerda             |
| Qtd     | WQty   | 60    | direita              |
| Unit $  | WUnit  | 90    | direita              |
| Total $ | WTotal | 100   | direita              |
| Item    | (fill) | 0     | esquerda             |

---

## 3. Tabela de CONTROLES

Todos os controles interativos são: **1 botão Refresh** + **5 cabeçalhos de coluna clicáveis** = **6 controles**.
As linhas de dados **não** são interativas. O `ScrollViewer` rola por roda/barra mas não é um "controle" com lógica.

| Controle | Tipo | Texto/Label | Estado inicial | O que LÊ (fonte de dado) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| **Refresh** | `Button` | `⟳ Refresh` (Padding 9,4,9,4), dock=Right | Sempre habilitado | — (só dispara ação) | `Click` → chama `Refresh()` (idêntico ao tick do timer): se `!_svc.IsAttached` mostra a Note "jogo fechado"; senão roda `_svc.Engine.Inventory.List()` em `Task.Run` e re-renderiza | Recarrega `_data`, re-renderiza `_rows` e atualiza `_total` | Nunca desabilitado (mesmo com jogo fechado; nesse caso só mostra a Note) |
| **Header "Item"** | `TextBlock` clicável (Cursor=Hand) | `Item` (+` ▲`/` ▼` se ativo), left, fill | `_sortKey="total"` → **inativo** (sem seta) | `_sortKey`, `_sortDesc`, `_data` | `MouseLeftButtonUp` → `SortBy("name")`. Se já for a coluna ativa: **inverte** `_sortDesc`. Se não: vira ativa e `_sortDesc=false` (texto começa **ascendente A→Z**). Reconstrói o header (atualiza setas) e `RenderRows()` | Reordena as linhas por `Name` (LINQ `OrderBy(i => i.Name)`), move a seta ▲/▼ para esta coluna | Sempre clicável |
| **Header "Grade"** | `TextBlock` clicável | `Grade` (+seta), left, W=120 | inativo | idem | `SortBy("grade")`. Nova coluna → `_sortDesc=false` (**ascendente**, Common→Cosmic). Repetir clique inverte | Ordena por `Grade` (int 0..9/10) via `OrderBy(i => i.Grade)` | Sempre clicável |
| **Header "Qtd"** | `TextBlock` clicável | `Qtd` (+seta), **right**, W=60 | inativo | idem | `SortBy("qty")`. Nova coluna → `_sortDesc=**true**` (números começam **descendente**, maior qtd primeiro). Repetir inverte | Ordena por `Qty` via `OrderBy(i => i.Qty)` | Sempre clicável |
| **Header "Unit $"** | `TextBlock` clicável | `Unit $` (+seta), **right**, W=90 | inativo | idem | `SortBy("unit")`. Nova coluna → `_sortDesc=true` (**descendente**, mais caro primeiro). Repetir inverte | Ordena por `Unit` (preço unitário USD) via `OrderBy(i => i.Unit)` | Sempre clicável |
| **Header "Total $"** | `TextBlock` clicável | `Total $` (+seta), **right**, W=100 | **ATIVO por padrão**, seta ` ▼` (desc) | idem | `SortBy("total")`. Como é o default, o **1º clique inverte** para ascendente ▲. Novo clique volta a desc | Ordena por `Unit*Qty` (valor total da pilha) via `OrderBy(i => i.Unit*i.Qty)` | Sempre clicável |

**Regra de sentido inicial da seta** (em `SortBy`, ao ATIVAR uma coluna nova):
`_sortDesc = key is "total" or "unit" or "qty"` → colunas **numéricas** (`total`, `unit`, `qty`) começam
**descendentes** (▼, maior primeiro); colunas **textuais/ordinais** (`name`, `grade`) começam
**ascendentes** (▲). Reclicar na coluna ativa **sempre inverte** o sentido.

**Ordenação default ao abrir a aba**: `_sortKey="total"`, `_sortDesc=true` → **maior valor total primeiro**, seta ▼ na coluna "Total $".

---

## 4. Comportamento vivo (timers / auto-refresh)

- **Timer de 3 segundos** (`DispatcherTimer`, `Interval = 3s`): a cada tick chama `Refresh()`. Iniciado no
  evento `Loaded` e parado no `Unloaded`.
- **Evento `Loaded`**: `_timer.Start()` **e** um `Refresh()` imediato (não espera 3s para a 1ª carga).
- **Evento `Unloaded`**: `_timer.Stop()` **e** `_svc.StateChanged -= Refresh` (desassina para não vazar).
- **`_svc.StateChanged += Refresh`**: o `EngineService` dispara `StateChanged` **no thread da UI** quando
  **conecta/desconecta** do jogo (a cada ~1s o `ConnectLoopAsync` reataca sozinho). Ou seja, a lista
  **reage à reconexão** além do timer de 3s.
- **Leitura em background**: `Refresh()` (quando attachado) roda `Inventory.List()` dentro de `Task.Run`
  para **não travar a UI** (a leitura faz syscalls de memória + possíveis `RemoteCall` de grade). O
  resultado volta ao thread da UI via `Dispatcher.Invoke` (`_data = data; RenderRows()`).
- **Re-render local sem tocar a memória**: `SortBy()` chama `RenderRows()` diretamente sobre `_data` já
  em cache — ordenar **não** relê o jogo, é instantâneo.
- **Cache de grade**: `Inventory.ItemGrade` memoriza o grade por `itemKey` em `_gradeCache` (grade é dado
  estático do build), então os refreshes de 3s **não** repetem os `RemoteCall(izb)` já resolvidos.
- **Cache do JSON de preços/nomes**: `item_prices.json` é lido **uma vez** (`Load()` faz early-return se
  já carregado). Refreshes seguintes só releem a **contagem** de itens da memória.

---

## 5. Estados especiais

| Situação | O que aparece | Onde no código |
|---|---|---|
| **Jogo fechado** (`!_svc.IsAttached`) | `_rows` limpo, `_total.Text=""`, e uma **Note**: `"jogo fechado — reabra para listar o inventário"`. Não roda `Inventory.List()`. | `Refresh()` guarda no topo |
| **Guarda dupla de attach** | Dentro do `Task.Run`, revalida `if (!_svc.IsAttached) return;` antes de ler (jogo pode ter caído entre o agendamento e a execução). | `Refresh()` |
| **Exceção na leitura** | `try/catch` engole qualquer erro de `Inventory.List()` e simplesmente **não atualiza** (mantém a lista anterior). Sem crash, sem mensagem. | `Refresh()` → `catch { return; }` |
| **Lista vazia** (`_data.Count==0`) — attachado mas PSD/offsets ainda não resolvidos | `_total.Text=""` e Note: `"nenhum item lido (resolvendo offsets?)"`. | `RenderRows()` |
| **PSD não resolvido / lista nula** no motor | `ReadCounts()` retorna dicionário **vazio** (psd==0, lst==0, arr==0, ou `size>=500000`). Vira "lista vazia" acima. | `Inventory.ReadCounts()` |
| **Preço desconhecido** (`Unit==0`, item sem entrada no JSON) | Colunas **Unit $** e **Total $** mostram `"—"` (em-dash), não `$0.00`. | `BuildRow()`: `it.Unit > 0 ? "$…" : "—"` |
| **Nome não mapeado** (itemKey sem entrada no JSON) | Nome cai para `"#{key}"` (ex.: `#210034`). | `Inventory.ResolveName()` |
| **Grade fora de 0..9** (izb pode devolver 0..10) | Nome do grade vira `"?"` (`GradeName`) e a cor cai para o brush `Sub` (cinza) via `GradeBrush` (só há cor definida p/ 0..9). | `Inventory.GradeName` / `GradeBrush` |

---

## 6. Cores / estilo usados hoje (e significado)

**Brushes do tema (Dark.xaml)** referenciados por `FindResource`:

| Uso na aba | Brush/recurso | Hex | Significado |
|---|---|---|---|
| Rodapé `_total` | **cor fixa** `#5CBF6A` (não é brush do tema) | `#5CBF6A` | verde = "dinheiro/valor" (sempre verde) |
| Total $ de linha "cara" (`Unit >= 0.05`) | **cor fixa** `#5CBF6A` | `#5CBF6A` | verde = pilha com valor relevante (destaque) |
| Total $ de linha "barata" (`Unit < 0.05`) e Unit $ | `Sub` | `#9AA0AB` | cinza = valor baixo/secundário |
| Qtd, Nome do item | `Fg` | `#FFFFFF` | texto primário |
| Cabeçalho de coluna | `Sub` | `#9AA0AB` | rótulo secundário; Cursor=Hand indica clicável |
| Fundo de linha **ímpar** (índice `i%2==1`) | `Card2` | `#212429` | zebra/alternância p/ leitura |
| Fundo de linha **par** | `Brushes.Transparent` | — | herda o fundo do card |
| Card que envolve a lista | `Card.Border` (Background `Card`, Border `Stroke`, radius 10) | `#16181C` / `#2B2E34` | contêiner |
| Notes (jogo fechado / lista vazia) | `Subtle` | `#666B74` | texto apagado, informativo |
| Fonte mono (Total/Unit/Qtd/rodapé) | `Mono` = **Consolas** | — | alinhamento numérico |
| Fonte UI (rótulos/nome) | `UI` = **Segoe UI** | — | texto padrão |

**Cores por GRADE** — array fixo `GradeCol[0..9]` (hex embutidos na View, **não** vêm do tema).
Índice = grade `(0..9)` = Common..Cosmic. Aplicadas ao **nome do grade** (coluna Grade, em negrito):

| Idx | Grade      | Hex       | Cor aprox. |
|-----|------------|-----------|------------|
| 0   | Common     | `#9AA0AB` | cinza      |
| 1   | Uncommon   | `#5CBF6A` | verde      |
| 2   | Rare       | `#4BB0CC` | azul/ciano |
| 3   | Legendary  | `#B06BE8` | roxo       |
| 4   | Immortal   | `#E5564C` | vermelho   |
| 5   | Arcana     | `#E8A13A` | laranja    |
| 6   | Beyond     | `#E8C93A` | amarelo    |
| 7   | Celestial  | `#4BE0D0` | turquesa   |
| 8   | Divine     | `#FF9440` | laranja claro |
| 9   | Cosmic     | `#FF5EA8` | rosa/magenta |
| —   | (fora 0..9)| `Sub` `#9AA0AB` | cinza (fallback) |

**Regras de formatação numérica**:
- Preços: `"$" + valor.ToString("0.00", CultureInfo.InvariantCulture)` → **ponto** decimal, 2 casas (ex.: `$1.50`).
- Rodapé: `Total: ${grand:0.00}   ·   {items} itens ({rows.Count} tipos)` — `items` = soma de todas as
  quantidades; `rows.Count` = número de tipos distintos.
- Limiar de destaque verde no Total: `Unit >= 0.05` USD (constante mágica no `BuildRow`).
- Truncamento: cada célula usa `TextTrimming.CharacterEllipsis` (nome longo vira `…`).

---

## 7. Chamadas de backend

Métodos/serviços que a aba consome:

| Chamada | O que faz |
|---|---|
| `_svc.IsAttached` | `bool` — jogo attachado **e** processo vivo (`Engine.IsAttached && Target.IsAlive()`). Gate de tudo. |
| `_svc.StateChanged` (evento) | Disparado no thread da UI quando conecta/desconecta; a aba usa para dar `Refresh()`. |
| `_svc.Engine.Inventory.List()` | **Núcleo**: retorna `List<InvItem>` (key, nome, grade, qty, preço unit.) de todo o inventário. Não ordena (a View ordena). |
| `Inventory.ReadCounts()` (interno de `List`) | Lê `PlayerSaveData.itemSaveDatas` (offset `inv_list_off`=0xA8) — a lista mestra que já inclui inv + todos os baús. Conta ocorrências por `itemKey` (filtro 100000..999999). 1 batch read do array + itemKey de cada elemento (`itemsave_key`=0x10). |
| `Inventory.ItemGrade(key)` (interno) | Grade real via `izb` (`RemoteCall.Invoke(mem, Base+izb, key)` → ptr → `ReadI32(ptr+iteminfo_grade=0x38)`, sanidade 0..10). Memoizado em `_gradeCache`. `null` se izb ausente/ponteiro inválido. |
| `Inventory.KeyGrade(key)` (fallback) | Grade embutido no próprio key: `(key/1000)%10`. Usado quando `ItemGrade` devolve `null`. |
| `Inventory.ResolveName(key)` | Nome via `item_prices.json` (campo `base`/`market_name`/`name`). Fallback `"#{key}"`. Nome **não** é resolvível headless de forma confiável (viria da tabela de localização do jogo, que exige main-thread). |
| `Inventory.Prices()` → `price_usd` | Preço unitário USD do mesmo JSON. `0` = desconhecido → exibido como `"—"`. |
| `Inventory.Load()` / `OpenPrices()` | Carrega `item_prices.json` **uma vez**: caminho explícito → ao lado do exe → cwd → **resource embutido** no assembly (`Data/item_prices.json`), permitindo exe single-file sem arquivos soltos. Erros → nomes/`#key` e preços 0 (nunca quebra a leitura). |
| `Inventory.GradeName(gi)` (static) | Nome do grade 0..9 = Common..Cosmic; `"?"` fora da faixa. Usado na coluna Grade. |

`InvItem` = `record(int Key, string Name, int Grade, int Qty, double Unit=0)` — imutável, ordenável via LINQ.

---

## 8. Notas para o redesign (ESSENCIAL preservar)

**Função que NÃO pode mudar:**

1. **Somente leitura.** Nada nesta aba escreve na memória do jogo nem no servidor. Não introduza ações
   destrutivas (vender/mover) sem decisão explícita — hoje é 100% segura.
2. **A lista é a fonte única** = `Engine.Inventory.List()`, que já une inventário + todos os baús (não
   existem listas separadas por baú). Não tente reconstruir isso na UI.
3. **Ordenação client-side sobre cache** — 5 chaves (`name`, `grade`, `qty`, `unit`, `total`). `total` =
   `Unit*Qty`. Reordenar **não** deve reler a memória (só re-render de `_data`). Manter a regra de sentido
   inicial: numéricas começam **desc**, texto/ordinal começam **asc**; reclique inverte.
4. **Default = maior valor total primeiro** (`total` desc). Preserve como estado inicial.
5. **Rodapé com 3 números**: valor total (`$` 2 casas, InvariantCulture/ponto), soma de quantidades
   ("itens") e contagem de tipos ("tipos"). São distintos — não confundir "itens" com "tipos".
6. **Preço/nome desconhecido é normal**: `"—"` para preço 0 e `"#{key}"` para nome ausente. Não esconder
   nem zerar esses itens — eles existem no inventário mesmo sem preço.
7. **Cor por grade** deve seguir o array `GradeCol` (Common..Cosmic). Se mudar tons, manter o **mapeamento
   de índice** (grade 0 = Common cinza … grade 9 = Cosmic rosa) para consistência com as outras abas.
8. **Auto-refresh de 3s + reação a StateChanged.** A lista se atualiza sozinha; ao redesenhar mantenha o
   timer e o start/stop no `Loaded`/`Unloaded`, e o desassinamento de `StateChanged` no `Unloaded`.
9. **Leitura em background obrigatória.** `Inventory.List()` faz syscalls e `RemoteCall`; deve rodar fora
   do thread da UI (hoje `Task.Run` + `Dispatcher.Invoke`) para não congelar a interface.

**Armadilhas conhecidas:**

- **Guardas de attach em dois pontos** (antes de agendar e dentro da task): o jogo pode fechar no meio; se
  remover a segunda checagem, `List()` pode ler ponteiros mortos.
- **Erros silenciosos**: `catch { return; }` mantém a lista anterior em vez de piscar vazio. Se trocar por
  mostrar erro, cuidado para não flickar a cada tick de 3s durante quedas momentâneas.
- **Grade 10 é possível** (izb valida 0..10) mas o array de cores e `GradeName` só cobrem 0..9 → cai para
  cinza/`"?"`. Se o jogo ganhar um 11º grade, ampliar **ambos** (`GradeCol` e `GradeNames`).
- **Offsets podem mudar a cada update do jogo** (inv_list_off default 0xA8, resolvido do `SymbolTable`).
  A aba não trata isso — depende do resolvedor de offsets do Engine. "Lista vazia = resolvendo offsets?"
  é o sintoma esperado logo após um update.
- **Alinhamento header↔linhas** depende das larguras fixas (`WGrade/WQty/WUnit/WTotal`) serem idênticas
  nos dois e das colunas serem dockadas **na mesma ordem inversa**. Se migrar para um `Grid`/`DataGrid`,
  garantir que cabeçalho e corpo compartilhem as mesmas larguras.
- **Reconstrução do header em cada `SortBy`**: hoje o header é localizado como "o `Border` cujo filho é um
  `DockPanel`" e recriado só para atualizar as setas. Um redesign pode simplesmente re-bindar o texto da
  seta em vez de recriar — mas não perca a atualização visual da seta na coluna ativa.
