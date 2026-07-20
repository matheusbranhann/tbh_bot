# Dispatcher main-thread — notas de port (peça adiada da Fase 3)

O dispatcher é a única parte do engine que **não** dá para portar cego: precisa iterar contra o jogo vivo.
Ele existe para rodar chamadas Unity async/UI na **main-thread** (abrir caixa, mover item, cubo) — coisas que
crasham se chamadas de outra thread.

## Como funciona no Python (`tbh_bot/tbh_core.py`)
- `_install_dispatch` (1738) — faz hook no `InputManager.Update` (sym `upd`): rouba N bytes do prólogo,
  desvia (`E9`) para um code-cave que a cada frame checa um flag e executa o comando pedido, depois roda os
  bytes roubados e volta.
- `_dispatch_code` — **gera o shellcode** do cave (o switch de comandos: 12=call func, box/stash/fuse…).
- `_prologue_len` / `_orig_prologue` / `_plen_of` — medem quantos bytes do prólogo são **relocáveis** (precisa
  de **disassembler**; no Python é capstone).
- `_suspend_all` / `_resume_all` — suspendem/retomam todas as threads durante o patch (senão corre risco de
  executar meio-patch).
- `_dispatch` (1790) / `_dispatch_call` (1816) — escrevem os args no bloco de dados do cave e esperam o consumo.
- `_remote_call` (1534) + `_klass_by_name` (1855) — resolvem exports IL2CPP e chamam funções remotamente.

## O que falta para implementar em C# (`RealDispatcher : IMainThreadDispatcher`)
1. **Disassembler** — medir o prólogo relocável. Usar o NuGet **Iced** (`Iced.Intel`) para desmontar de `UPD`
   até acumular ≥5 bytes sem instrução rip-relative/branch (equivalente ao `_prologue_len`).
2. **Alloc do cave** — já temos: `TbhBot.Core.Memory.CodeCave.Alloc`.
3. **Shellcode** — portar `_dispatch_code` (emitir os bytes do loop de comandos + o trampolim dos bytes roubados).
4. **Suspend/resume de threads** — P/Invoke `CreateToolhelp32Snapshot`/`Thread32First/Next` +
   `OpenThread`/`SuspendThread`/`ResumeThread` (por `ProcessId`), para o patch do prólogo.
5. **Self-heal** — detectar hook órfão (`UPD[0]==0xE9`) e restaurar do `GameAssembly.dll` no disco
   (o arquivo nunca tem nossos hooks) ou de um cache `upd_prologue_<hash>.bin`.
6. **Validação AO VIVO** na box — este passo é obrigatório; não dá para confiar sem testar contra o jogo.

Enquanto isso: `StubDispatcher` (IsReady=false). Auto-box/stash/fuse ficam inertes; todo o resto
(cheats por patch + leitura de estado + escrita de runas/cubo/stage) funciona sem o dispatcher.
