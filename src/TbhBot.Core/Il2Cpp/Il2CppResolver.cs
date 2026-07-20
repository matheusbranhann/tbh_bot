using Iced.Intel;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Il2Cpp;

/// <summary>
/// Resolve estruturas IL2CPP em runtime: o singleton "bau" que segura o PlayerSaveData,
/// o proprio PlayerSaveData (PSD) e o bloco de static fields de um TypeInfo.
/// Portado de _valid_bau/_resolve_bau/_player_psd/StaticFields do tbh_core.py.
/// </summary>
public sealed class Il2CppResolver(MemoryAccess mem, SymbolTable sym, MemoryScanner scan)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly MemoryScanner _scan = scan;

    // cache runtime (self.cache do Python): instancia achada e flag de "ja tentou e falhou"
    private nint _bauInst;
    private bool _bauNotFound;

    private nint Base => _mem.Target.ModuleBase;

    /// <summary>
    /// Bloco de static fields de um TypeInfo: <c>[[base+rva] + StaticFieldsOff]</c>. Equivale EXATO ao
    /// <c>_uo_sf</c>/<c>_cube_sf</c> do Python (<c>k=u64(base+ti); return u64(k+0xB8)</c>) — o campo
    /// static_fields no klass é um PONTEIRO, então deref duplo. 0 se inválido.
    /// </summary>
    public nint StaticFields(long typeInfoRva)
    {
        var k = _mem.ReadPtr(Base + (nint)typeInfoRva);
        if (!MemoryAccess.IsValidPointer(k)) return 0;
        var sf = _mem.ReadPtr(k + GameConstants.StaticFieldsOff);
        return MemoryAccess.IsValidPointer(sf) ? sf : 0;
    }

    /// <summary>
    /// Resolve o RVA do static que segura o singleton do CUBO (cube_slot). NAO vem do dump (é runtime):
    /// desmonta <c>ilo</c> procurando <c>mov rXX,[rip+disp]</c> (o static) seguido de <c>mov rXX,[rXX+0xB8]</c>
    /// (o model). Porta de <c>_resolve_cube_slot</c> (tbh_core.py) com o disassembler Iced. 0 se não achar.
    /// </summary>
    public long ResolveCubeSlot()
    {
        long ilo = _sym.Get("ilo");
        if (ilo == 0) return 0;
        var code = _mem.ReadBytes(Base + (nint)ilo, 0x160);
        if (code.Length < 16) return 0;

        ulong start = (ulong)(Base + (nint)ilo);
        var decoder = Decoder.Create(64, code);
        decoder.IP = start;
        long last = 0;
        while (decoder.IP < start + (ulong)code.Length)
        {
            var ins = decoder.Decode();
            if (ins.IsInvalid) break;
            if (ins.Mnemonic != Mnemonic.Mov || ins.Op0Kind != OpKind.Register || ins.Op1Kind != OpKind.Memory)
                continue;
            if (ins.IsIPRelativeMemoryOperand)
                last = (long)ins.IPRelativeMemoryAddress - (long)Base;   // mov rXX,[rip+disp] -> RVA do static
            else if (ins.MemoryDisplacement64 == 0xB8 && last != 0)
                return last;                                             // mov rXX,[rXX+0xB8] -> confirma
        }
        return 0;
    }

    /// <summary>
    /// _valid_bau: a instancia so vale se [inst+inv_psd_off] for um PSD valido cuja lista de itens
    /// (@inv_list_off, count@+0x18) esteja em 0..500000.
    /// </summary>
    private bool ValidBau(nint inst)
    {
        long po = _sym.Get("inv_psd_off", GameConstants.InvPsdOff);
        long lo = _sym.Get("inv_list_off", GameConstants.InvListOff);
        var psd = _mem.ReadPtr(inst + (nint)po);
        if (!MemoryAccess.IsValidPointer(psd)) return false;
        var lst = _mem.ReadPtr(psd + (nint)lo);
        if (!MemoryAccess.IsValidPointer(lst)) return false;
        uint sz = _mem.ReadU32(lst + 0x18);
        return sz < 500000;   // 0<=sz<500000 (uint ja garante >=0)
    }

    /// <summary>
    /// _resolve_bau: acha o singleton que segura o PSD. Primeiro via bau_ti (TypeInfo exportado ->
    /// static fields -> [sf]); senao varre a regiao procurando o klass concreto (inv_klass_ti),
    /// validando a chain com <see cref="ValidBau"/>.
    /// </summary>
    public nint ResolveBau()
    {
        // 1) via TypeInfo exportado: sf=StaticFields(bau_ti) (já dereferenciado); bau=[sf]
        long ti = _sym.Get("bau_ti");
        if (ti != 0)
        {
            var sf = StaticFields(ti);                  // bloco de static fields ([k+0xB8])
            var bau = sf != 0 ? _mem.ReadPtr(sf) : 0;
            if (MemoryAccess.IsValidPointer(bau) && ValidBau(bau))
                return bau;
        }

        // 2) cache runtime
        if (_bauInst != 0 && ValidBau(_bauInst)) return _bauInst;
        if (_bauNotFound) return 0;                     // ja tentou e falhou

        // 3) via classe concreta (inv_klass_ti) -> klass -> scan da instancia (valida a chain)
        long kti = _sym.Get("inv_klass_ti");
        if (kti != 0)
        {
            var klass = _mem.ReadPtr(Base + (nint)kti);
            if (MemoryAccess.IsValidPointer(klass))
            {
                var needle = BitConverter.GetBytes((ulong)klass);
                foreach (var (rb, size) in _scan.Regions(0x04, 0x40))
                {
                    var data = _mem.ReadBytes(rb, size);
                    if (data.Length == 0) continue;
                    int j = IndexOf(data, needle, 0);
                    while (j >= 0)
                    {
                        var a = rb + j;
                        if (ValidBau(a)) { _bauInst = a; return a; }
                        j = IndexOf(data, needle, j + 8);
                    }
                }
            }
        }

        _bauNotFound = true;
        return 0;
    }

    /// <summary>
    /// _player_psd (racional): PSD via bau+inv_psd_off, VALIDANDO pela lista de runas
    /// (PSD+RuneListOff, count 50..2000) — robusto contra bau-fantasma pos-reload.
    /// Se invalido, varre a regiao pelo klass concreto ate achar um PSD com lista de runa valida.
    /// Devolve 0 se nao resolver.
    /// </summary>
    public nint ResolvePsd()
    {
        long po = _sym.Get("inv_psd_off", GameConstants.InvPsdOff);
        long ro = _sym.Get("PlayerSaveData.RuneSaveData", GameConstants.RuneListOff);

        var bau = ResolveBau();
        if (bau != 0)
        {
            var psd = _mem.ReadPtr(bau + (nint)po);
            if (MemoryAccess.IsValidPointer(psd) && HasValidRuneList(psd, ro))
                return psd;
        }

        // fallback: acha o klass (inv_klass_ti; senao [bau]) e escaneia por um PSD com runas validas
        nint klass = 0;
        long kti = _sym.Get("inv_klass_ti");
        if (kti != 0) klass = _mem.ReadPtr(Base + (nint)kti);
        if (klass == 0 && bau != 0) klass = _mem.ReadPtr(bau);
        if (klass == 0) return 0;

        var needle = BitConverter.GetBytes((ulong)klass);
        foreach (var (rb, size) in _scan.Regions(0x04, 0x40))
        {
            var data = _mem.ReadBytes(rb, size);
            if (data.Length == 0) continue;
            int j = IndexOf(data, needle, 0);
            while (j >= 0)
            {
                if (j % 8 == 0)   // so candidatos 8-alinhados (igual ao _player_psd)
                {
                    var psd = _mem.ReadPtr(rb + j + (nint)po);
                    if (MemoryAccess.IsValidPointer(psd) && HasValidRuneList(psd, ro))
                        return psd;
                }
                j = IndexOf(data, needle, j + 8);
            }
        }
        return 0;
    }

    /// <summary>Lista de runas @PSD+ro com count em 50..2000 (heuristica de PSD "de verdade").</summary>
    private bool HasValidRuneList(nint psd, long ro)
    {
        var lst = _mem.ReadPtr(psd + (nint)ro);
        if (lst == 0) return false;
        uint cnt = _mem.ReadU32(lst + 0x18);
        return cnt is > 50 and < 2000;
    }

    // busca de sub-array (8 bytes do ponteiro klass) dentro do dump da regiao
    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        int last = hay.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
}
