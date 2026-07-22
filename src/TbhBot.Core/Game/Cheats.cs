using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

// Cheats por patch de código: ACTk (NOP do detector) e Godmode (prólogo -> ret). Guarda os bytes
// originais p/ restaurar no desligar. (Hitkill e Speedhack foram removidos a pedido do usuário.)
public sealed class Cheats(MemoryAccess mem, SymbolTable sym, MemoryScanner scan)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly MemoryScanner _scan = scan;

    private readonly Dictionary<long, byte[]> _actkOrig = new();   // rva -> byte original
    private nint _godAddr;                                         // endereço do AOB godmode, CACHEADO
    private bool _godScanned;                                      // (o patch 57->C3 quebra o próprio padrão)

    private nint Base => _mem.Target.ModuleBase;
    public Action<string>? Log;

    // ACTk: em cada RVA do sym.Ynj, on -> 0xC3 (ret) guardando o byte antigo; off -> restaura.
    public void SetActk(bool on)
    {
        foreach (long rva in _sym.Ynj)
        {
            nint a = Base + (nint)rva;
            byte[] head = _mem.ReadBytes(a, 12);
            if (head.Length < 1) continue;
            if (on)
            {
                if (head[0] == 0xC3) continue;                  // já patchado
                if (IsSharedStub(head))
                {
                    // NÃO patcha: alvo errado. Ver o comentário de IsSharedStub — isso já fechou o
                    // jogo do usuário uma vez (offsets do build 535f apontavam pro pool de getters).
                    Log?.Invoke($"ACTk: alvo 0x{rva:X} parece stub compartilhado do runtime — NÃO patchei " +
                                "(patchar isso derruba o jogo). Offsets deste build precisam ser refeitos.");
                    continue;
                }
                _actkOrig[rva] = [head[0]];
                _mem.WriteBytes(a, [0xC3]);
            }
            else if (head[0] == 0xC3 && _actkOrig.TryGetValue(rva, out byte[]? orig))
            {
                _mem.WriteBytes(a, orig);
            }
        }
    }

    /// <summary>
    /// O RVA aponta pra um STUB DEDUPLICADO em vez de uma função de verdade?
    ///
    /// O IL2CPP funde corpos idênticos: centenas de getters triviais do runtime viram UM só endereço
    /// (<c>mov rax,[rcx+0x10]; ret</c> — 5 bytes, seguido de <c>int3</c> de padding). Escrever
    /// <c>0xC3</c> nesse endereço faz TODOS esses getters devolverem lixo e o jogo morre na hora.
    ///
    /// Assinatura do stub: um <c>ret</c> logo no começo. Detector ACTk de verdade tem ~128 bytes e
    /// abre com prólogo (<c>push rbx; sub rsp,0x20; ...</c>) — nenhum <c>ret</c> nos primeiros bytes.
    /// </summary>
    private static bool IsSharedStub(byte[] head)
    {
        int n = Math.Min(head.Length, 12);
        for (int i = 0; i < n; i++) if (head[i] == 0xC3) return true;
        return false;
    }

    // Godmode: AOB_GODMODE aponta o prólogo (push rdi=0x57). on -> 0xC3 (ret imediato); off -> restaura 0x57.
    // Acha o AOB UMA vez e cacheia (o patch quebra o próprio padrão -> re-scan no revert não acharia).
    public void SetGodmode(bool on)
    {
        if (!_godScanned) { _godAddr = _scan.FindAob(GameConstants.AobGodmode); _godScanned = true; }
        nint a = _godAddr;
        if (a == 0) return;
        byte[] cur = _mem.ReadBytes(a, 1);
        if (cur.Length < 1) return;
        if (on) { if (cur[0] != 0xC3) _mem.WriteBytes(a, [0xC3]); }
        else if (cur[0] == 0xC3) _mem.WriteBytes(a, [0x57]);
    }
}
