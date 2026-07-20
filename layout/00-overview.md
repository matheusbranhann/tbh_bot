# 00 — Visão geral da arquitetura (para o redesign)

> Este documento e os demais em `layout/` descrevem **o que cada tela e cada controle FAZEM** — a
> **função**, não o visual atual (que será redesenhado). Quem for redesenhar deve preservar 100% da
> função descrita aqui e pode mudar livremente cores, disposição, componentes e estética.

## O que é o app
Painel (trainer) do jogo **TaskbarHero** (Steam appid `3678970`, jogo Unity IL2CPP, offline com validação
no backend TheBackend). O painel lê/escreve a memória do jogo para: forçar stats, desbloquear cubo/runas/
estágios, automatizar caixas/baú/fusão/boss/evolução, mostrar inventário/preços, e reabrir o jogo sozinho
se cair (auto-restart). É um **WPF em C#/.NET 10**, entregue como **1 .exe self-contained** (`dist/TbhBot.App.exe`).

## Projetos (solução `TbhBot.slnx`)
| Projeto | O que é | UI? |
|---|---|---|
| `TbhBot.Core` | Motor: attach ao processo, resolução de offsets, cheats, leitura/escrita de save, automações, dispatcher main-thread. | Não |
| `TbhBot.App` | Painel WPF (o que o usuário vê). Depende do Core. | Sim (WPF) |
| `TbhBot.Cli` | Testes de linha de comando (`--e2e`, `--features`, etc). Só p/ dev. | Não |

## Estrutura da UI (`src/TbhBot.App`)
```
MainWindow.xaml(.cs)      janela host: cabeçalho + TabControl (5 abas) + barra de status
Views/
  TrainerView.cs          aba 1 — Proteção, Automação, Stats, Campos de fase, Profiles, Desbloqueio do cubo
  InventoryView.cs        aba 2 — lista de itens (nome/grade/qtd/preço), ordenável
  MarketView.cs           aba 3 — busca de preço Steam + toggle do overlay
  RunesView.cs            aba 4 — ÁRVORE de runas (zoom/pan/clique-info/desbloqueio)
  StagesView.cs           aba 5 — mapa dos 120 estágios
Services/
  EngineService.cs        dono do Engine; ConnectLoop (attach/reconnect), AutomationLoop, WatchdogService; eventos StateChanged/Log
  WatchdogService.cs      auto-restart (fecha->reabre->fecha popup->reentra)
  OverlayService.cs       overlay de preço por OCR (janela separada topmost click-through)
  OverlayWindow.cs        a janela do overlay
  ProfileStore.cs         salvar/carregar perfis em %APPDATA%/tbh_bot/profiles.json
  Native.cs               P/Invoke (captura de tela, clique real, enumerar janelas)
Theme/Dark.xaml           TODAS as cores/fontes/estilos (o "design system" atual) — ver 01-theme.md
Assets/runes_icons.json   ícones das runas (embutidos)
```

## Como a UI conversa com o jogo (padrão que TODA view segue)
- Cada view é um `UserControl` que recebe **`EngineService _svc`** no construtor.
- **`EngineService`** é o dono do `Engine` (motor). Expõe:
  - `Engine` — o motor (métodos de leitura/escrita).
  - `IsAttached` — `true` só quando o jogo está aberto e conectado. **TODA leitura/escrita guarda nisto**
    (`if (!_svc.IsAttached) return;`).
  - `event StateChanged` — disparado (no thread da UI) quando conecta/desconecta.
  - `event Log` — texto de status (vai pra barra inferior + `%APPDATA%/tbh_bot/session.log`).
  - `RaiseLog(msg)` — a view manda um log pra barra.
  - `LaunchGame()` — abre o jogo via `steam://run/3678970`.
- **Leitura é sempre em background**: `Task.Run(() => { if(!_svc.IsAttached) return; var x = _svc.Engine.…; Dispatcher.Invoke(() => atualiza UI); })`. Nunca lê a memória no thread da UI (trava).
- **Auto-refresh por timer**: cada view tem um `DispatcherTimer` (1–3 s) que relê e repinta sozinho.
- **Escrita** (aplicar stat, desbloquear, etc.) também roda em `Task.Run` e loga o resultado.
- **Reconexão automática**: o `ConnectLoop` do EngineService re-attacha sozinho quando o jogo volta →
  dispara `StateChanged` → as views repintam. O usuário nunca precisa reconectar na mão.

## As 5 abas (resumo — detalhe em cada doc)
| Aba | Doc | Função |
|---|---|---|
| Trainer | `03-trainer.md` | Proteção (ACTk/God), automações (box/stash/fuse/boss/evolution/auto-restart), stats forçados, campos de fase, profiles, desbloqueio do cubo |
| Inventory | `04-inventory.md` | Lista de todos os itens com nome/grade/quantidade/preço, ordenável |
| Market | `05-market.md` | Busca de preço no Steam Market + liga/desliga o overlay de preço |
| Runes | `06-runes.md` | Árvore de runas navegável (zoom/pan); clicar mostra info + desbloqueia/upa |
| Stages | `07-stages.md` | Mapa dos 120 estágios; desbloquear progressão |

## Persistência (fora do jogo)
- `%APPDATA%/tbh_bot/profiles.json` — perfis do usuário (ver 03-trainer.md, seção Profiles).
- `%APPDATA%/tbh_bot/session.log` — histórico de logs (útil pra depurar auto-restart etc).

## Estados globais que o redesign PRECISA tratar
- **Jogo fechado**: `IsAttached=false`. Toda tela mostra um estado "vazio/desconectado" (labels `--`,
  textos "jogo fechado — reabre ao reconectar"). O cabeçalho mostra "● jogo fechado" em vermelho.
- **Conectado**: cabeçalho "● conectado · build \<hash\> · offsets ✓" em accent.
- **Durante o auto-restart**: o jogo some e volta sozinho; a UI para de ler no meio (via `IsAttached`) e
  volta quando reconecta. Não precisa de tratamento especial na tela além dos guardas de `IsAttached`.

## O que NÃO mexer no redesign (é função, não estética)
- Os guardas de `IsAttached` antes de qualquer leitura/escrita (senão trava/quebra).
- Leitura/escrita sempre em background (`Task.Run`) + `Dispatcher.Invoke` pra tocar a UI.
- Os timers de auto-refresh (a UI tem que refletir o jogo em tempo real).
- Os **clamps de segurança** (ex.: nunca passar do teto de uma runa — ver 06-runes.md; travar o botão do
  cubo em Lv.100 — ver 03-trainer.md).
- O **encadeamento** auto-modo → liga ACTk junto (ver 03-trainer.md).

Cada `NN-*.md` detalha uma tela/serviço no formato: Propósito · Estrutura visual · Tabela de controles ·
Comportamento vivo · Estados especiais · Cores/estilo · Chamadas de backend · Notas p/ o redesign.
