using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Leitura/escrita de dados de save/runtime: progresso de estagio, nivel do cubo,
/// runas (client-side) e contagem de inventario. Porta stage_progress/set_maxstage,
/// cube_level/set_cube_level, read_runes/set_rune e read_inventory do tbh_core.py.
///
/// AVISO (do Python): escrever os ObscuredInt runtime (uo_max / cube bese) dispara o
/// honesty-check periodico do ACTk (~12s) e o jogo fecha sozinho, MAS o valor persiste
/// (auto-save) e recarrega limpo. Comportamento fiel ao original.
/// </summary>
public sealed class SaveData(
    MemoryAccess mem,
    SymbolTable sym,
    Il2CppResolver resolver)
{
    private const int CubeLevelOff = 0x1CC;   // uw.Cube.bese (ObscuredInt) = nivel RUNTIME do cubo
    public const int StageMaxKey = 4310;      // TORMENT 3-10 = maior key (libera 120/120)
    public const int CubeMaxLevel = 100;

    // static_fields da classe estatica de estagio (uu.uo): [base+uo_ti] -> +StaticFieldsOff
    private nint UoStaticFields()
        => sym.Has("uo_ti") ? resolver.StaticFields(sym.Get("uo_ti")) : 0;

    // static_fields do cubo (uw.Cube): [base+cube_slot] -> +StaticFieldsOff.
    // cube_slot nao vem do dump -> resolve por disasm (ResolveCubeSlot) na 1a vez e cacheia no sym.
    private nint CubeStaticFields()
    {
        long cs = sym.Get("cube_slot");
        if (cs == 0) { cs = resolver.ResolveCubeSlot(); if (cs != 0) sym["cube_slot"] = cs; }
        return cs != 0 ? resolver.StaticFields(cs) : 0;
    }

    // ---------------- PROGRESSO DE ESTAGIO ----------------

    /// <summary>(maxCompletedStage, currentStageKey, wave). -1 em cada campo que nao resolver.</summary>
    public (int Max, int Cur, int Wave) StageProgress()
    {
        nint sf = UoStaticFields();
        if (sf == 0) return (-1, -1, -1);
        int G(string key)
        {
            long o = sym.Get(key);
            if (o == 0) return -1;
            return ObscuredValue.ReadInt(mem, sf + (nint)o) ?? -1;
        }
        return (G("uo_max"), G("uo_cur"), G("uo_wave"));
    }

    /// <summary>
    /// Desbloqueia estagios ate `value` (default 4310). Escreve o ObscuredInt runtime uo_max
    /// (autoritativo) + espelha no int do save (CommonSaveData.maxCompletedStage) por consistencia.
    /// </summary>
    public (bool Ok, int Value) SetMaxStage(int value = StageMaxKey)
    {
        nint sf = UoStaticFields();
        long off = sym.Get("uo_max");
        if (sf == 0 || off == 0) return (false, value);
        bool ok = ObscuredValue.WriteInt(mem, sf + (nint)off, value);
        // espelha no save int (best-effort): PSD -> [+0x10] (CommonSaveData) -> +commonsave_maxstage
        try
        {
            nint psd = resolver.ResolvePsd();
            if (psd != 0)
            {
                nint csd = mem.ReadPtr(psd + 0x10);
                if (MemoryAccess.IsValidPointer(csd))
                    mem.Write<int>(csd + (nint)sym.Get("commonsave_maxstage", 0x54), value);
            }
        }
        catch { /* espelhamento e opcional */ }
        return (ok, value);
    }

    // ---------------- CUBO ----------------

    /// <summary>Nivel atual do cubo (ObscuredInt bese @ cube_sf+0x1CC). null se nao resolveu.</summary>
    public int? CubeLevel()
    {
        nint sf = CubeStaticFields();
        return sf == 0 ? null : ObscuredValue.ReadInt(mem, sf + CubeLevelOff);
    }

    /// <summary>Sobe o nivel RUNTIME do cubo (indexa a lista de recipes; libera tiers altos).</summary>
    public (bool Ok, int Level) SetCubeLevel(int level = CubeMaxLevel)
    {
        nint sf = CubeStaticFields();
        if (sf == 0) return (false, level);
        return (ObscuredValue.WriteInt(mem, sf + CubeLevelOff, level), level);
    }

    // ---------------- RUNAS (tabela Runes, 100% client-side) ----------------

    /// <summary>{RuneKey -> Level} da lista de RuneSaveData @ PSD+RuneListOff. Vazio se nao resolver.</summary>
    public Dictionary<int, int> ReadRunes()
    {
        var outd = new Dictionary<int, int>();
        nint psd = resolver.ResolvePsd();
        if (psd == 0) return outd;
        nint lst = mem.ReadPtr(psd + GameConstants.RuneListOff);
        if (lst == 0) return outd;
        nint arr = mem.ReadPtr(lst + 0x10);
        uint size = mem.ReadU32(lst + 0x18);
        if (arr == 0 || size >= 100000) return outd;
        // batch: le a lista de ponteiros de uma vez, depois key/level de cada elemento
        ulong[] elems = mem.ReadArray<ulong>(arr + 0x20, (int)size);
        foreach (ulong re in elems)
        {
            nint r = (nint)re;
            if (r == 0) continue;
            outd[mem.ReadI32(r + 0x10)] = mem.ReadI32(r + 0x14);   // RuneKey@0x10 -> Level@0x14
        }
        return outd;
    }

    /// <summary>
    /// Seta o Level de UMA runa (client-side). Clamp so >=0 (o teto por-runa NAO esta aqui —
    /// o chamador nao pode passar do max, senao NRE em RuneNode.mav = loading infinito).
    /// </summary>
    public bool SetRune(int key, int level)
    {
        level = Math.Max(0, level);
        nint psd = resolver.ResolvePsd();
        if (psd == 0) return false;
        nint lst = mem.ReadPtr(psd + GameConstants.RuneListOff);
        if (lst == 0) return false;
        nint arr = mem.ReadPtr(lst + 0x10);
        uint size = mem.ReadU32(lst + 0x18);
        if (arr == 0 || size == 0) return false;
        for (int i = 0; i < size; i++)
        {
            nint r = mem.ReadPtr(arr + 0x20 + i * 8);
            if (r != 0 && mem.ReadI32(r + 0x10) == key)
            {
                mem.Write<int>(r + 0x14, level);
                return true;
            }
        }
        return false;
    }

    // ---------------- INVENTARIO ----------------

    /// <summary>Conta itens (nao-nulos) da lista @ PSD+inv_list_off. Demo de batch read (ReadArray).</summary>
    public int InventoryCount()
    {
        nint psd = resolver.ResolvePsd();
        if (psd == 0) return 0;
        nint invOff = (nint)sym.Get("inv_list_off", GameConstants.InvListOff);
        nint lst = mem.ReadPtr(psd + invOff);
        if (lst == 0) return 0;
        nint arr = mem.ReadPtr(lst + 0x10);
        uint size = mem.ReadU32(lst + 0x18);
        if (arr == 0 || size >= 500000) return 0;
        // 1 syscall p/ a lista toda; conta as entradas ocupadas
        ulong[] elems = mem.ReadArray<ulong>(arr + 0x20, (int)size);
        int n = 0;
        foreach (ulong it in elems)
            if (it != 0) n++;
        return n;
    }
}
