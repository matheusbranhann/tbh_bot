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
            byte[] cur = _mem.ReadBytes(a, 1);
            if (cur.Length < 1) continue;
            if (on)
            {
                if (cur[0] != 0xC3) { _actkOrig[rva] = cur; _mem.WriteBytes(a, [0xC3]); }
            }
            else if (cur[0] == 0xC3 && _actkOrig.TryGetValue(rva, out byte[]? orig))
            {
                _mem.WriteBytes(a, orig);
            }
        }
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
