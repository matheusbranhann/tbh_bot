# ROADMAP — migração Python → C# (.NET 10)

Cada fase é **entregável e testável sozinha**. A ordem prioriza validar cedo a premissa (a lentidão é o
padrão de leitura + o GIL, não a linguagem) e só depois portar UI/overlay/distribuição. O engine Python
(`../tbh_bot`) fica como referência viva e só é aposentado na Fase 8.

Legenda: 🟢 feito · 🟡 em andamento · ⚪ pendente

## Estado atual — Fases 0–4 implementadas e VALIDADAS AO VIVO (na box, build `c824ed7a2bb1`)
- **Fase 1** batch read **~200x** (8192 leituras: sequencial ~10 ms vs batch ~0.05 ms) + AOB scanner OK.
- **Fase 2** build-hash + `KnownBuilds` + **loader do cache `offsets_<hash>.json`** (ponte enquanto a extração por dump em C# não é portada).
- **Fase 3** leitura ao vivo confirmada: **inventário 63 itens · runas 197 · stage max=4310/cur=4309 · 25 stats** (Attack Damage=1e6). Cheats/ObscuredInt portados fiéis.
- **Fase 4** AutomationLoop + Watchdog (Task/CancellationToken) rodam e encerram limpo.

**Deferidos (marcados no código):** (a) o **dispatcher main-thread** (code-cave — precisa disassembler + iteração ao vivo; `Game/DISPATCHER_PORT_NOTES.md`); (b) a **extração de offsets por dump** em C# (o loader de JSON cobre builds já resolvidos pelo Python); (c) `cube_slot` neste build (o Python resolve por disasm).

---

## Fase 0 — Fundação 🟢
**Meta:** solução compilando + rodando, sem lógica de jogo.
- Solução `TbhBot` (Core lib + App WPF + Cli de teste), `Directory.Build.props` (x64/nullable/unsafe), gitignore/editorconfig.
- Camada de memória mínima: `ProcessTarget` (attach) + `MemoryAccess` (Read/Write/**ReadArray**).
- CLI de benchmark (sequencial vs batch).
- **Pronto quando:** `dotnet run --project tools/TbhBot.Cli` attacha no jogo e imprime o speedup do batch.

## Fase 1 — Núcleo de memória 🟢
**Meta:** endurecer a base de leitura antes de qualquer feature.
- `VirtualQueryEx` para validar ponteiros e enumerar regiões (base do AOB scan).
- **Scanner de AOB nativo** (pattern + máscara) — varre MBs em ms; substitui o `aob()` lento do Python.
- Helpers: pointer-chase (`Resolve(base, offsets…)`), leitura de **string IL2CPP** (len@0x10 + UTF-16@0x14).
- **Pronto quando:** lê a base, valida ponteiros e acha um AOB conhecido (ex.: prólogo de um cheat).

## Fase 2 — IL2CPP + offsets + auto-offset 🟢 (dump-extraction ⚪)
**Meta:** paridade com o sistema de offsets que se auto-atualiza por build.
- Resolução de símbolos por **assinatura** (portar `_extract_from_dump`).
- Singletons (`bau`/`bam`) via TypeInfo, `static_fields` (klass+0xB8), resolução do `PlayerSaveData`.
- Cache de offsets por **build-hash** (md5 de N MB do módulo) + invalidação por versão.
- **Pronto quando:** resolve o PSD e lê a **lista de runas** — igual ao Python.

## Fase 3 — Primitivos de jogo + cheats (paridade de engine) 🟢 (dispatcher ⚪)
**Meta:** o engine C# faz tudo que o Python faz.
- Cheats via AOB: godmode, hitkill, speedhack, actk (NOP do `ynj`).
- Leitura **batch**: inventário/baú, stats, tabela de stages, runas, cubo.
- ObscuredInt read/write (`_obs_int`/`_obs_set`) → `set_rune`/`set_maxstage`/`set_cube_level`.
- **Dispatcher main-thread**: code-cave hook no `InputManager.Update` (para chamadas UI/async legítimas).
- **Pronto quando:** lê o inventário e aplica um cheat + um `set_rune`, batendo com o Python.

## Fase 4 — Concorrência real (o motivo do C#) 🟢
**Meta:** provar o ganho do "sem GIL".
- Loop de automação (open-box → stash → fuse) numa `Task`/thread dedicada, com `CancellationToken`.
- `Channel<T>` / eventos para engine ↔ UI (sem travar a janela).
- Watchdog de reconexão (jogo fecha → reataca) e o fluxo de "recarrega uma vez" (stage/cube).
- **Pronto quando:** automação + leitura contínua rodam sem engasgar a UI.

## Fase 5 — UI (painel WPF) 🟢 (Runes/Inventory 🟡)
**Meta:** paridade visual e funcional das abas.
- Shell + abas: Trainer, Inventory, Market, Runes, Stages.
- Tema dark "precision instrument" (preto profundo, acento laranja, mono).
- **Canvas** das árvores (runas/stages) e **virtualização** das listas (inventário grande).
- MVVM + binding; long-ops sempre fora do thread da UI.
- **Pronto quando:** todas as abas funcionam como no painel Python.

## Fase 6 — Overlay + Market 🟢 Market · ⏸ Overlay
**Meta:** as features periféricas.
- Overlay de preço (layered/`WS_EX_LAYERED` + DWM), **OCR nativo** (`Windows.Media.Ocr`).
- Market DB (preços da Steam) + cache.
- **Pronto quando:** o overlay mostra o preço no hover, igual hoje.

## Fase 7 — Distribuição + auto-update 🟢 (auto-update pronto, ativa no 1º release C#)
**Meta:** manter o modelo "baixa 1 exe, ele se atualiza".
- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → 1 `.exe`.
- Portar `check_update`/`download_update`/`launch_updater` (troca o exe via GitHub releases; **asset .zip**).
- Bumpar `Version` a cada release (o auto-update compara com a tag `releases/latest`).
- **Pronto quando:** gera 1 exe e um cliente antigo se atualiza sozinho.

## Fase 8 — Corte + paridade final 🟢 (checklist + testes; corte final quando os ⏸ fecharem)
**Meta:** aposentar o Python.
- Checklist de feature-parity (Trainer/Inv/Market/Runes/Stages/Cubo/Auto-*/Overlay/Auto-offset/Auto-update).
- Testes (unit no Core, smoke no App), medição de perf (batch vs Python).
- Migrar o repositório/release para o build C# e arquivar o Python como referência.

---

### Notas de decisão
- **UI:** WPF (nativo, single-file trivial). Alternativa moderna: **Avalonia** (MVVM + theming melhor para o
  tema custom + canvas) — trocável na Fase 5 sem tocar no Core.
- **Core sem UI:** todo o conhecimento sensível (anti-cheat, ObscuredInt, offsets) fica em `TbhBot.Core`,
  testável pelo `TbhBot.Cli` sem abrir janela.
- **Regra do force-close:** stage/cube continuam "recarrega uma vez" (ObscuredInt/ACTk); runas seguem ao vivo.
