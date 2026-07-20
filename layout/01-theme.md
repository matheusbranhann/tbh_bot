# 01 — Sistema de Design / Tema (tokens)

> Fonte da verdade para o **redesign** do painel WPF do TaskbarHero (projeto `tbh_sharp`, `TbhBot.App`).
> Este doc descreve o **tema atual** (cores, fontes, estilos nomeados) e **onde/como cada token é usado**.
> Arquivos de origem:
> - `src/TbhBot.App/Theme/Dark.xaml` — o dicionário de recursos (todos os tokens)
> - `src/TbhBot.App/App.xaml` — faz o merge de `Theme/Dark.xaml` em `Application.Resources` (tema global, único, sempre dark)
> - `src/TbhBot.App/MainWindow.xaml` (+ `.cs`) — shell (header, abas, status bar)
> - `src/TbhBot.App/Views/*.cs` — as 5 abas montam UI **em C# no code-behind** e puxam os tokens via `FindResource`

---

## 1. Propósito

O tema é um **dicionário de recursos WPF único e global** (`ResourceDictionary` mesclado em `App.xaml`). Ele define:

1. **Cores cruas** (`Color x:Key="C.*"`) — a paleta.
2. **Brushes** (`SolidColorBrush x:Key="..."`) — um pincel por cor, com nome curto (`Fg`, `Bg`, `Acc`…). É **isto** que o resto do app consome, por chave.
3. **Fontes** (`FontFamily x:Key="Mono"` e `"UI"`).
4. **Estilos implícitos** (por `TargetType`) que reestilizam todos os `Button`, `CheckBox`, `TextBox`, `TabItem`, `TabControl`, `TextBlock` automaticamente.
5. **Estilos nomeados** (`x:Key`) reutilizáveis: `Sub.Text`, `Mono.Text`, `Accent.Button`, `Card.Border`.

Estética declarada no comentário do arquivo: **"precision instrument"** — fundo quase-preto, superfícies em cinza-carvão, um único acento laranja. É a **mesma paleta do antigo painel Python** (portada 1:1).

Tema é **fixo** (não há light/dark toggle, não há `prefers-color-scheme`). Um único `MergedDictionary`. Trocar o tema = trocar/substituir `Dark.xaml`.

---

## 2. Estrutura visual atual (como os tokens se encaixam no shell)

```
MainWindow  (Background = Bg  #050506)
┌─────────────────────────────────────────────────────────────┐
│ HEADER  Border  bg=Surf  borda-inferior 1px=Stroke           │
│   "tbh_bot"  16px Bold  Fg      [● conectado …] Sub.Text     │
│                                    (cor Acc / Red)            │
│                                    [▶ Abrir jogo]  Button def │
├─────────────────────────────────────────────────────────────┤
│ TabControl  (bg=Bg)   abas: Trainer Inventory Market Runes …│
│   TabItem  bg=Surf, borda-inf 2px:                           │
│     selecionada → Acc (laranja) + texto Fg                   │
│     normal      → texto Sub                                  │
│   ┌───────── conteúdo da aba (UserControl em C#) ─────────┐  │
│   │  Card.Border  (bg=Card, borda=Stroke, raio 10, pad 12)│  │
│   │     ...linhas, botões, switches, textos...            │  │
│   └───────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│ STATUS BAR  Border  bg=Surf  borda-superior 1px=Stroke       │
│   TextBlock  Sub.Text   (última msg do _svc.Log)             │
└─────────────────────────────────────────────────────────────┘
```

**Agrupamento lógico das camadas de fundo (do mais escuro/mais fundo ao mais claro/mais na frente):**

`Bg` (janela) → `Surf` (header/status/aba) → `Card` (painel-card, input) → `Card2` (card elevado / botão default / linha alternada / trilho de switch off).

**Agrupamento das bordas:** `Stroke` (divisores e cartões, sutil) → `Stroke2` (bordas de controle mais fortes: botão, trilho do switch, hover).

**Agrupamento do texto:** `Fg` branco (primário) → `Sub` cinza-azulado (secundário/legenda) → `Subtle` cinza-escuro (terciário/desabilitado/placeholder).

---

## 3. Tabela de cores (Color `C.*`) — chave → hex → significado

| Chave (Color) | Hex | Brush homônimo | Significado / papel semântico | Onde aparece |
|---|---|---|---|---|
| `C.Bg` | `#050506` | `Bg` | Fundo base da app (quase preto) | `MainWindow.Background`, `TabControl.Background` |
| `C.Surf` | `#0C0D0F` | `Surf` | "Superfície" um degrau acima do fundo | Header, status bar, fundo do `TabItem` |
| `C.Card` | `#16181C` | `Card` | Preenchimento de cartão / campo de texto | `Card.Border`, `TextBox` background |
| `C.Card2` | `#212429` | `Card2` | Cartão/elemento **elevado**; superfície interativa | Botão default, trilho do switch (off), **linha alternada** de tabela (`InventoryView`), cabeçalho de linha do Trainer |
| `C.Stroke` | `#2B2E34` | `Stroke` | Borda **sutil** (divisores, cartões) | Bordas de `Card.Border`, `TextBox`, divisores de header/status, borda base do `TabItem` |
| `C.Stroke2` | `#3D4149` | `Stroke2` | Borda **mais forte** / estado hover | Borda do botão default; **hover** do botão default (vira bg); borda do trilho do switch |
| `C.Fg` | `#FFFFFF` | `Fg` | Texto **primário** (branco) | Títulos, valores, texto default de todo `TextBlock`/`Button`/`CheckBox`/`TextBox` |
| `C.Sub` | `#9AA0AB` | `Sub` | Texto **secundário** (cinza-azulado) | `Sub.Text`, legendas, aba **não** selecionada, status bar |
| `C.Subtle` | `#666B74` | `Subtle` | Texto **terciário/apagado** | Dicas/hints (10px), placeholder de lista vazia, **knob do switch quando OFF**, texto de nós bloqueados |
| `C.Acc` | `#FF7A18` | `Acc` | **Acento laranja** — ação/ativo/ligado/selecionado | Botão de acento (`Accent.Button`), trilho do switch **ON**, underline da aba selecionada, ponto "● conectado", preços válidos, caret do `TextBox`, estado "forçado/ativo" |
| `C.AccH` | `#FF9440` | `AccH` | Laranja **claro** (hover/realce do acento) | Hover do `Accent.Button`, borda do `Accent.Button`, borda de célula de stage já-completada (`StagesView`) |
| `C.AccTxt` | `#0A0A0A` | `AccTxt` | Texto/knob **sobre** o acento (quase preto) | Foreground do `Accent.Button`, cor do knob do switch **ON** |
| `C.Red` | `#F87171` | `Red` | **Erro / desconectado / negativo** | "● jogo fechado" no header, "sem preço" no Market |
| `C.Amber` | `#FBBF24` | `Amber` | **Alerta / destaque especial** (âmbar) | Borda de célula de **boss** no `StagesView` |

> Observação: **não** há tokens de sombra, gradiente ou elevação além dos degraus de cor acima. Cantos arredondados são fixos por estilo (ver §5).

---

## 4. Fontes (tokens de tipografia)

| Chave | Família | Uso |
|---|---|---|
| `UI` | **Segoe UI** | Texto de interface: títulos, rótulos de botão, abas, checkbox, `TextBlock` default |
| `Mono` | **Consolas** | Números/valores, campos de entrada, legendas técnicas (`Sub.Text`, `Mono.Text`, `TextBox`, células de grade, contadores) |

**Tamanhos de fonte em uso (px) e onde:**

| Tamanho | Peso | Onde |
|---|---|---|
| 16 | Bold | Título "tbh_bot" no header |
| 13 | SemiBold | `TabItem` (rótulo de aba) |
| 12 | Normal | `TextBlock` default (base) |
| 12 | SemiBold | `Button` default e `Accent.Button` |
| 12 | Normal | `CheckBox`; linhas de info do painel de runa |
| 11 | Normal | `Sub.Text`, `Mono.Text`, `TextBox` (mono) |
| 10 | Normal/Bold | Dicas/hints (`Subtle`), badges de nível de runa (Mono Bold), células de grade de stage |

Não há escala tipográfica formal (h1/h2/body) — tamanhos são aplicados **ad hoc** por controle no code-behind.

---

## 5. Tabela de CONTROLES (estilos de controle temáticos)

Aqui "controle" = cada **estilo de controle** que o tema define e reestiliza. As abas **instanciam** esses estilos (via `FindResource` / atribuição de `.Style`).

| Controle | Tipo (estilo) | Texto/Label | Estado inicial | O que LÊ (origem visual) | Ao INTERAGIR (comportamento EXATO) | O que REFLETE/atualiza | Habilita/Desabilita quando |
|---|---|---|---|---|---|---|---|
| **Botão default** | `Style TargetType="Button"` (implícito) | qualquer `Content` | bg=`Card2`, borda 1px=`Stroke2`, texto=`Fg`, UI 12 SemiBold, pad 12,6, canto 8, cursor mão | tokens `Card2`/`Stroke2`/`Fg` | **Trigger `IsMouseOver=True`** → bg do border vira `Stroke2` (clareia). `IsEnabled=False` → `Opacity=0.45` | só visual (hover/disabled). Ação vem do `Click` no code-behind | Fica opaco (0.45) quando `IsEnabled=false` |
| **Botão de acento** | `x:Key="Accent.Button"` (BasedOn Button) | qualquer `Content` | bg=`Acc` laranja, texto=`AccTxt` (quase-preto), borda=`AccH`, canto 8 | tokens `Acc`/`AccTxt`/`AccH` | **Trigger `IsMouseOver=True`** → bg vira `AccH` (laranja mais claro). `IsEnabled=False` → `Opacity=0.45` | usado como **indicador de estado "ligado/ativo"**: várias telas trocam `.Style` para `Accent.Button` quando algo está ativo (ex.: `MarketView` botão do overlay; `TrainerView` botão "Apply" de uma linha ativa vira acento; `StagesView` botões Refresh/unlock) | opacidade 0.45 se `IsEnabled=false` |
| **CheckBox = switch** | `Style TargetType="CheckBox"` (implícito, **template redesenhado**) | rótulo à direita (ContentPresenter, margem 8px) | trilho 34×18 raio 9, bg=`Card2`, borda=`Stroke2`; knob elipse 12×12 à **esquerda**, fill=`Subtle` | `IsChecked` | **Trigger `IsChecked=True`** → trilho bg vira `Acc`, knob vira `AccTxt` e desliza para a **direita** (alinha à direita, margem 0,0,3,0). Sem animação (troca discreta) | Reflete ligado/desligado de **proteção/automação** (godmode, hitkill, auto-stash, auto-box, etc.) | segue `IsEnabled` do controle |
| **Campo de texto** | `Style TargetType="TextBox"` (implícito) | valor digitado | bg=`Card`, borda 1px=`Stroke`, texto **Mono 11**, pad 6,3, canto 6, **caret=`Acc`** | tokens `Card`/`Stroke`/`Acc` | edição normal; sem trigger de foco/hover no template (borda não muda). Parse dos valores é feito no code-behind (`double.TryParse`/`int.TryParse`, `InvariantCulture`) | valor lido pelas telas ao clicar em Apply | — |
| **Aba (item)** | `Style TargetType="TabItem"` (implícito) | header da aba | bg=`Surf`, borda-inferior 2px=`Stroke`, texto=`Sub`, UI 13 SemiBold, pad 16,8, margem-direita 2 | `IsSelected`, `IsMouseOver` | **`IsSelected=True`** → borda-inferior vira `Acc` (underline laranja) + texto vira `Fg`. **`IsMouseOver=True`** → texto vira `Fg` | qual aba está ativa | — |
| **Container de abas** | `Style TargetType="TabControl"` (implícito) | — | bg=`Bg`, `BorderThickness=0` | token `Bg` | hospeda os `TabItem`; seleção troca o `Content` | — | — |

**Padrões de estado herdados por TODOS os controles temáticos:**
- **Desabilitado** = `Opacity 0.45` (só nos botões, via trigger). Não há cor "disabled" dedicada.
- **Hover** = clareia um degrau (`Stroke2` no default; `AccH` no acento). Sem sombra, sem realce de borda.
- **Cantos:** botão 8, `Accent.Button` 8, `TextBox` 6, switch (trilho) 9, `Card.Border` 10. Escala de raio **não** é um token — está hard-coded em cada template.

---

## 6. Estilos de TEXTO nomeados (não interativos, mas são tokens)

| Estilo | Alvo | Foreground | Fonte | Tamanho | Uso |
|---|---|---|---|---|---|
| _(implícito)_ | `TextBlock` | `Fg` | `UI` | 12 | Texto padrão de qualquer label. Também define `TextFormattingMode=Ideal` |
| `Sub.Text` | `TextBlock` | `Sub` | `Mono` | 11 | Legendas técnicas/secundárias: status do header ("● conectando…"), status bar, subtítulos de aba |
| `Mono.Text` | `TextBlock` | `Fg` | `Mono` | 11 | Valores monoespaçados em destaque (branco) |

> Diferença chave: `Sub.Text` = cinza (`Sub`), `Mono.Text` = branco (`Fg`). Ambos são **Consolas 11**.

## 6b. Estilo de container nomeado

| Estilo | Alvo | O que faz |
|---|---|---|
| `Card.Border` | `Border` | bg=`Card`, borda 1px=`Stroke`, `CornerRadius=10`, `Padding=12`. É o **cartão-base reutilizável** de todas as telas (envolvem listas, grupos de controles, resultados do Market, viewport de runas, tabela de inventário). |

---

## 7. Cores **hard-coded** nas telas (NÃO são tokens do tema — mas carregam significado)

Estas cores vivem em C# nas views (via `Frozen(hex)` = `SolidColorBrush` congelado). O redesign precisa saber que elas existem e o que significam, porque **não** virão do `Dark.xaml`.

### 7.1 `InventoryView.cs` — cor por **grade/raridade** (`GradeCol[0..9]`)
| Grade | Hex | (nome típico) |
|---|---|---|
| 0 | `#9AA0AB` | Common (= cinza `Sub`) |
| 1 | `#5CBF6A` | Uncommon (verde) |
| 2 | `#4BB0CC` | Rare (azul) |
| 3 | `#B06BE8` | Epic (roxo) |
| 4 | `#E5564C` | (vermelho) |
| 5 | `#E8A13A` | (laranja) |
| 6 | `#E8C93A` | (dourado) |
| 7 | `#4BE0D0` | (ciano) |
| 8 | `#FF9440` | (= `AccH`) |
| 9 | `#FF5EA8` | Cosmic (rosa) |
> `GradeBrush(g)`: se `g` fora de 0..9 → cai para `Sub`. Linha alternada da tabela usa `Card2`; linha par usa `Transparent`.

### 7.2 `StagesView.cs` — cor por **dificuldade** (`DiffCol`)
| Índice | Hex | Dificuldade |
|---|---|---|
| 0 | `#5CBF6A` | Normal (verde) |
| 1 | `#4BB0CC` | Nightmare (azul) |
| 2 | `#E8A13A` | Hell (laranja) |
| 3 | `#E5564C` | Torment (vermelho) |

Fundos/bordas fixos das células de stage:
| Nome | Hex | Significado |
|---|---|---|
| `UnlockedFill` | `#2F2110` | Célula **liberada** (laranja-escuro) |
| `LockedFill` | `#141519` | Célula **bloqueada** |
| `LockedStroke` | `#2B2E34` | Borda de célula bloqueada (= `Stroke`) |
| `LockedText` | `#666B74` | Texto bloqueado (= `Subtle`) |
Bordas dinâmicas: célula já-completada → `AccH`; célula atual/normal desbloqueada → `Acc`; **boss** → `Amber`; bloqueada → `LockedStroke`.

### 7.3 `RunesView.cs` — cor por **categoria** de runa (`CatColor`) + estados
| Categoria (`CatLabel`) | Chave | Hex |
|---|---|---|
| Caixa/Drop | `chest` | `#f0a83a` |
| Combate | `combat` | `#e5564c` |
| Ouro | `gold` | `#e8c93a` |
| EXP | `exp` | `#5cbf6a` |
| Utilidade | `util` | `#4bb0cc` |

Estados do nó de runa:
| Nome | Hex | Significado |
|---|---|---|
| `SelBorder` | `#ffffff` | Runa **selecionada** (borda branca, 3px) |
| `MaxedBorder` | `#ffd24a` | Runa **maxada** (dourado) — também colore o texto "nível/max" |
| `LockedBorder` | `#34343e` | Runa **bloqueada** |
| `NodeFill` | `#26262e` | Preenchimento do nó |
| `LineBrush` | `#33333f` | Linhas de **conexão** entre runas |
> Prioridade da borda: selecionada > bloqueada > maxada > cor-da-categoria. Texto do nível: maxado→`MaxedBorder` (dourado); >0→`Fg`; 0→`Subtle`.

**Semântica de cor consolidada (cross-view):**
- **Laranja `Acc`/`AccH`** = ativo / selecionado / ligado / conectado / liberado / preço válido.
- **Dourado/âmbar (`Amber` `#FBBF24`, `#ffd24a`, `#e8c93a`)** = **maxado / boss / topo** ("chegou ao teto").
- **Vermelho (`Red` `#F87171`, `#e5564c`)** = **erro / desconectado / sem-preço / dificuldade máxima / combate**.
- **Cinza `Subtle`** = desabilitado / off / vazio / bloqueado.
- **Verde `#5CBF6A`** = ok / baixo-tier / EXP.

---

## 8. Comportamento vivo (o que o tema participa)

O tema em si é **estático** (recursos), mas os elementos estilizados **trocam de token em runtime**:

- **Header `Conn` (MainWindow.xaml.cs `UpdateConn`)**: disparado por `_svc.StateChanged`. Se `_svc.IsAttached` → `Foreground = FindResource("Acc")` e texto "● conectado · build XXXX · offsets ✓/só-AOB". Senão → `FindResource("Red")` e "● jogo fechado — reconecta ao reabrir".
- **Status bar (`Status`)**: assina `_svc.Log`; cada mensagem substitui o texto (`Sub.Text`).
- **Troca de `.Style` como "estado ligado"**: várias telas fazem `botão.Style = St("Accent.Button")` quando algo ativa e `= null` (volta ao default) quando desativa — o `Accent.Button` é o **indicador visual de "ON/forçado"**. Ex.: `MarketView` (`_ovBtn.Style = on ? Accent.Button : null`), `TrainerView` (`row.Apply.Style = row.Active ? Accent.Button : null`).
- Auto-refresh de conteúdo (timers das abas) é comportamento **das telas**, não do tema — documentado nos docs por-aba.

---

## 9. Estados especiais

- **Jogo fechado (`_svc.IsAttached == false`)**: header vira `Red` "jogo fechado". As telas guardam ações com `IsAttached`; controles ficam sem efeito (não há um "disabled em massa" via tema — cada tela decide). O botão "▶ Abrir jogo" (`OnLaunchGame` → `_svc.LaunchGame()`) permanece ativo.
- **Lista vazia / sem dado**: telas inserem um `TextBlock` com `Foreground=Subtle` ("clique numa runa", placeholder de tabela).
- **Valor ausente**: Market mostra "sem preço" em `Red`; runa sem efeito mostra "—".
- **Desabilitado**: botões caem para `Opacity 0.45` (trigger de `IsEnabled`).
- **Sem hover/foco visível no `TextBox`**: o template não muda borda em foco — cuidado no redesign (acessibilidade de foco é fraca hoje).

---

## 10. Chamadas de backend que tocam o tema (indiretas)

O tema não chama backend. Quem troca tokens em runtime consome estes serviços:

| Método/Serviço | O que faz | Reflexo visual no tema |
|---|---|---|
| `EngineService.IsAttached` (`_svc`) | true se anexado ao processo do jogo | header `Acc` vs `Red`; telas habilitam/desabilitam |
| `EngineService.StateChanged` (evento) | dispara `UpdateConn()` | recolore `Conn` |
| `EngineService.Log` (evento) | mensagens de status | preenche status bar (`Sub.Text`) |
| `EngineService.Engine.BuildHash` / `OffsetsLoaded` | build/offsets carregados | texto do header ("build XXXX · offsets ✓/só-AOB") |
| `EngineService.LaunchGame()` | abre o jogo (Steam appid) | botão "▶ Abrir jogo" |

Helpers de acesso a token (em todas as views):
- `B(key)` / `Brush(key)` → `(Brush)Application.Current.FindResource(key)`
- `St(key)` / `S(key)` → `(Style)Application.Current.FindResource(key)`
- `Frozen(hex)` → cria `SolidColorBrush` **fora** do tema (as cores da §7)

---

## 11. Notas para o redesign (o que PRESERVAR na função)

1. **Contrato de chaves de recurso é uma API.** As views resolvem por **string** (`FindResource("Acc")`, `FindResource("Card.Border")`…). Se você renomear/remover uma chave, **quebra em runtime** (exceção `ResourceReferenceKeyNotFoundException`), não em compilação. Mantenha **todas** estas chaves vivas (ou refatore as views junto): brushes `Bg Surf Card Card2 Stroke Stroke2 Fg Sub Subtle Acc AccH AccTxt Red Amber`; fontes `Mono UI`; estilos `Sub.Text Mono.Text Accent.Button Card.Border`; estilos implícitos de `Button CheckBox TextBox TabItem TabControl TextBlock`.
2. **`Accent.Button` é semântico, não só estético.** É o sinal de "ligado/forçado/ativo". Qualquer novo visual precisa manter um estilo distinto para esse estado (as views fazem `.Style = Accent.Button` / `null`).
3. **Estado desligado do switch usa `Subtle` no knob e `Card2` no trilho; ligado usa `Acc`/`AccTxt`.** O switch é o toggle de proteção/automação — mantenha um ON claramente laranja e um OFF claramente apagado.
4. **Paleta semântica cross-view** (dourado=maxado/boss, vermelho=erro/desconectado, laranja=ativo, cinza=off, verde=ok) deve ser preservada mesmo que os hexes mudem. As cores da §7 estão **hard-coded em C#** — se o redesign quiser centralizá-las, terá que editar as views (`GradeCol`, `DiffCol`, `CatColor`, etc.), não só o XAML.
5. **Sempre dark, um só dicionário.** Não há suporte a light theme hoje; `App.xaml` mescla só `Dark.xaml`. Se for adicionar theming, o ponto de entrada é o `MergedDictionaries`.
6. **Sem animação nos triggers** (switch e hover trocam valor discreto). Adicionar transições é livre, mas os **triggers** (`IsMouseOver`, `IsChecked`, `IsSelected`, `IsEnabled`) precisam continuar existindo/produzindo o mesmo estado final.
7. **Armadilha de foco:** `TextBox` não tem indicação visual de foco no template atual — se o redesign visar acessibilidade, adicionar um estado de foco é melhoria, não regressão.
8. **Cantos e tamanhos são hard-coded** em cada template/estilo (não há tokens de raio/spacing). Se quiser um sistema de espaçamento/raio, terá que introduzir novos recursos e aplicá-los.
9. **`AccTxt` (#0A0A0A) é a cor de texto SOBRE laranja** — garanta contraste do rótulo dentro de qualquer botão/knob de acento no novo visual.
