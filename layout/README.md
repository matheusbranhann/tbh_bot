# `layout/` — Especificação de layout & comportamento (para redesenhar o painel)

**Objetivo:** dar a quem for **redesenhar** o painel uma fonte da verdade **completa** de *o que cada tela
e cada botão fazem* — para não precisar adivinhar nem ler o código. O visual atual é feio de propósito
(placeholder); a **função** documentada aqui é o que deve ser 100% preservado.

## Como usar
1. Leia **`00-overview.md`** — arquitetura, como a UI conversa com o jogo, padrões que toda tela segue.
2. Leia **`01-theme.md`** — o design system atual (cores, fontes, estilos). Você pode trocar tudo, mas
   veja os **significados** (dourado = maxado, vermelho = desconectado, etc.) — esses precisam continuar
   distinguíveis no novo visual.
3. Para cada tela, o doc correspondente traz: **Propósito · Estrutura visual · Tabela de controles
   (tipo, o que lê, o que faz ao interagir, quando habilita/desabilita) · Comportamento vivo · Estados
   especiais · Cores/significado · Chamadas de backend · Notas p/ o redesign**.
4. **`09-engine-behavior.md`** é a ponte: liga cada controle da UI ao efeito real no jogo (o "como o
   botão reage" no nível do motor).

## Índice
| Arquivo | Tela / assunto |
|---|---|
| `00-overview.md` | Arquitetura, padrões UI↔jogo, estados globais |
| `01-theme.md` | Design system atual: cores, fontes, estilos e seus significados |
| `02-mainwindow.md` | Janela host: cabeçalho, conexão, botão Abrir jogo, abas, barra de status |
| `03-trainer.md` | Aba Trainer (Proteção, Automação, Stats, Campos de fase, Profiles, Cubo) |
| `04-inventory.md` | Aba Inventory (lista ordenável de itens com preço) |
| `05-market.md` | Aba Market (busca de preço Steam + toggle do overlay) |
| `06-runes.md` | Aba Runes (árvore com zoom/pan, painel de info, desbloqueio) |
| `07-stages.md` | Aba Stages (mapa dos 120 estágios) |
| `08-overlay.md` | Overlay de preço (janela OCR click-through) |
| `09-engine-behavior.md` | O que cada flag/automação faz no jogo (ponte UI→motor) |

## Regras de ouro do redesign (não quebrar)
- **Preserve a FUNÇÃO**, redesenhe o visual. Nenhum comportamento descrito pode sumir.
- **Toda leitura/escrita guarda em `_svc.IsAttached`** e roda em background — mantenha isso.
- **Clamps de segurança são obrigatórios**: runa nunca passa do teto; botão do cubo trava em Lv.100;
  auto-modo liga ACTk junto. Se remover, quebra/dá crash.
- Os **timers de auto-refresh** (1–3 s por tela) fazem a UI refletir o jogo em tempo real — mantenha.
- Estados **jogo fechado / conectado / erro** precisam ser visíveis em cada tela.
