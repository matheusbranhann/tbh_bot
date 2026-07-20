# taskbarhero-bot — v4.0 (C# / .NET 10)

Trainer/bot para o **TaskbarHero** (jogo Unity IL2CPP, Steam appid `3678970`). A partir da **v4.0** o projeto
é **100% C#** — uma reescrita completa do antigo painel em Python, com um novo visual **Neo-Dashboard** e
distribuído como **um único `.exe` self-contained** (sem instalar Python, .NET ou nada).

> **Por que migrar de Python → C#?** Num editor de memória o gargalo não é a linguagem, é o **padrão de
> leitura** (milhares de `ReadProcessMemory`, uma syscall cada) e o **GIL** serializando as threads. O C#
> ataca os dois: **batch-read** na camada de memória (~200× mais rápido, medido ao vivo) + concorrência
> nativa sem GIL.

## Baixar e usar

1. Baixe o `.exe` do último **[Release](https://github.com/matheusbranhann/taskbarhero-bot/releases)**.
2. Abra o TaskbarHero (pela Steam) e rode o `.exe`. Ele acha o jogo sozinho e conecta.
3. É portátil: roda em qualquer PC Windows x64, sem dependências.

## Funcionalidades

- **Trainer (Control Center):** ACTk Bypass, God Mode, e stats/campos de fase **forçados** (aplicados na hora
  e mantidos a cada tick). Profiles (salvar/carregar presets).
- **Automações:** Auto-box (abre caixas), Auto-stash (move + organiza por grade), Auto-fuse (funde no cubo
  com filtro de grade/tipo), Auto-boss (gasta soulstone no x-10), **Evolution** (sobe 1 fase por vez até
  Torment 3-9 e desliga sozinha), **Auto-restart** (reabre o jogo se cair, fecha o popup e reentra no estágio).
- **Inventory:** lista de todos os itens com nome/grade/quantidade/preço Steam, ordenável.
- **Market:** consulta de preço no Steam Community Market + **overlay de preço** por OCR (mostra o valor ao
  lado do tooltip do jogo).
- **Runes Tree:** árvore de runas navegável (zoom/pan), com painel de info e desbloqueio/level por clique.
- **Stage Map:** mapa dos 120 estágios com desbloqueio de progressão.
- **Desbloqueios client-side:** cubo → Lv.100, runas, estágios.

## Estrutura

```
TbhBot.slnx                  solução (.NET 10)
Directory.Build.props        props comuns (x64, nullable, langversion)
src/
  TbhBot.Core/               o ENGINE (sem UI): processo, memória (batch), IL2CPP,
                             cheats, save, automações, dispatcher main-thread
  TbhBot.App/                o PAINEL (WPF) — visual Neo-Dashboard, sidebar + 5 telas
tools/TbhBot.Cli/            harness de teste (--e2e, --features, benchmark)
tests/TbhBot.Tests/          testes de unidade
layout/                      especificação de layout/comportamento + mockup web do design
docs/                        notas de paridade e arquitetura
python_old_project/          o projeto ANTIGO em Python (referência histórica; aposentado)
```

## Build a partir do código

Requer o **.NET 10 SDK**.

```bash
dotnet build                              # compila tudo
dotnet run --project src/TbhBot.App       # abre o painel
dotnet run --project tools/TbhBot.Cli -- --e2e   # testes ponta-a-ponta (com o jogo aberto)
publish.bat                               # gera dist\TbhBot.App.exe (1 exe self-contained)
```

## Histórico

O projeto nasceu em Python (releases **v1.0 → v3.5**); todo esse código está preservado em
[`python_old_project/`](python_old_project/). A **v4.0** é a conversão completa para C#, com paridade total de
funcionalidades e um redesign do zero.
