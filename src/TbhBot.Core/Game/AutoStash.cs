using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Auto-stash (porta de _ra/_slot_objs/_do_stash_bulk do tbh_core.py). Resolve o singleton "ra" (gerente
/// de move de itens), acha os itens ocupados do inventário e os slots LIVRES do baú, e move em lote via
/// o dispatcher (cmd2 = iw(ra, MoveRequest, fakeDelegate)). MoveRequest = (INVENTORY=1, srcIdx, STASH=2, slot, count=1).
/// Também organiza o baú por GRADE (SortStep = porta de _sort_grade_step): UM move STASH->STASH por chamada
/// (amortizado, chamado só quando ocioso), packing common->uncommon->rare->... nos slots de menor índice.
/// </summary>
public sealed class AutoStash(
    MemoryAccess mem, SymbolTable sym, MemoryScanner scan, Il2CppApi api, Il2CppResolver resolver, RealDispatcher disp)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly MemoryScanner _scan = scan;
    private readonly Il2CppApi _api = api;
    private readonly Il2CppResolver _resolver = resolver;
    private readonly RealDispatcher _disp = disp;
    public Action<string>? Log;

    private nint _ra;

    // Defs de item são estáticas (o grade de uma itemKey não muda) -> cacheia o resultado do izb.
    private readonly Dictionary<int, (int Type, int Grade, int Synth, int Level)?> _itemInfo = new();

    private nint Base => _mem.Target.ModuleBase;

    /// <summary>Singleton "ra" (gerente de move): [a]==klass, monitor@+8==0, m_CachedPtr@+0x10 válido. 0 se não achar.</summary>
    public nint ResolveRa()
    {
        long klass = _api.ClassFromName("", _sym.RaClass ?? "ra");
        if (klass == 0) return 0;
        if (_ra != 0 && (long)_mem.ReadU64(_ra) == klass) return _ra;         // cache
        var needle = BitConverter.GetBytes((ulong)klass);
        foreach (var (rb, size) in _scan.Regions(0x04, 0x40))
        {
            var d = _mem.ReadBytes(rb, size);
            if (d.Length == 0) continue;
            int j = IndexOf(d, needle, 0);
            while (j >= 0)
            {
                if (j % 8 == 0)
                {
                    nint a = rb + j;
                    if (_mem.ReadU64(a + 8) == 0 && MemoryAccess.IsValidPointer((nint)_mem.ReadU64(a + 0x10)))
                    { _ra = a; return a; }
                }
                j = IndexOf(d, needle, j + 8);
            }
        }
        return 0;
    }

    // Ponteiros dos slots de uma List (inv/stash) do PSD, em 1 leitura BULK.
    private List<nint> SlotObjs(long listOff)
    {
        var outl = new List<nint>();
        nint psd = _resolver.ResolvePsd();
        if (psd == 0) return outl;
        nint lp = _mem.ReadPtr(psd + (nint)listOff);
        if (lp == 0) return outl;
        nint arr = _mem.ReadPtr(lp + 0x10);
        uint sz = Math.Min(_mem.ReadU32(lp + 0x18), 4000u);
        if (arr == 0 || sz == 0) return outl;
        foreach (var p in _mem.ReadArray<ulong>(arr + 0x20, (int)sz))          // BATCH
            if (p != 0) outl.Add((nint)p);
        return outl;
    }

    /// <summary>(slots de inventário OCUPADOS, slots de baú LIVRES) — para verificar o move.</summary>
    public (int InvOccupied, int StashFree) SlotCounts()
    {
        int inv = 0, stash = 0;
        foreach (var o in SlotObjs(_sym.Get("inv_slots_off", 0x88)))
        {
            var d = _mem.ReadBytes(o + 0x10, 0x11);
            if (d.Length >= 0x11 && d[0x10] != 0 && BitConverter.ToUInt64(d, 8) != 0) inv++;
        }
        foreach (var o in SlotObjs(_sym.Get("stash_off", 0x90)))
        {
            var d = _mem.ReadBytes(o + 0x10, 0x11);
            if (d.Length >= 0x11 && d[0x10] != 0 && BitConverter.ToUInt64(d, 8) == 0) stash++;
        }
        return (inv, stash);
    }

    /// <summary>Move todos os itens do inventário pro baú (até maxn). Retorna quantos moveu.</summary>
    public int MoveAllToStash(Func<bool> keepGoing, int maxn = 150)
    {
        nint ra = ResolveRa();
        if (ra == 0) return 0;

        var srcs = new List<int>();                                           // itens ocupados do inv (idx)
        foreach (var o in SlotObjs(_sym.Get("inv_slots_off", 0x88)))
        {
            var d = _mem.ReadBytes(o + 0x10, 0x11);                           // idx@0, uid@8, unlock@0x10
            if (d.Length < 0x11 || d[0x10] == 0) continue;                    // unlock==0 -> pula
            if (BitConverter.ToUInt64(d, 8) != 0) srcs.Add(BitConverter.ToInt32(d, 0));  // uid!=0 -> ocupado
        }
        if (srcs.Count == 0) return 0;

        var slots = new List<int>();                                          // slots LIVRES do baú (idx)
        foreach (var o in SlotObjs(_sym.Get("stash_off", 0x90)))
        {
            var d = _mem.ReadBytes(o + 0x10, 0x11);
            if (d.Length < 0x11 || d[0x10] == 0) continue;
            if (BitConverter.ToUInt64(d, 8) == 0) slots.Add(BitConverter.ToInt32(d, 0));  // uid==0 -> livre
            if (slots.Count >= srcs.Count) break;
        }

        int n = Math.Min(Math.Min(srcs.Count, slots.Count), maxn);
        for (int k = 0; k < n && keepGoing(); k++)
            _disp.CommandStash(ra, [1, srcs[k], 2, slots[k], 1]);            // INVENTORY->STASH
        if (n > 0) Log?.Invoke($"📦 {n} itens movidos pro baú");
        return n;
    }

    // ---------------- ordenação por grade (porta de _sort_grade_step) ----------------

    /// <summary>
    /// UM passo de ordenação por grade do container (slotType 2=baú / 1=inventário): conserta a 1ª posição
    /// fora de ordem com &lt;=2 moves STASH->STASH via cmd2/iw (função LEGIT, o mesmo transporte do MoveAllToStash).
    /// Amortizado — chamar só quando ocioso, pra não bloquear o auto-box. Ordena common->uncommon->rare->...
    /// packed nos slots de menor índice. Retorna true se fez um move; false = já ordenado (ou não deu).
    /// </summary>
    public bool SortStep(int slotType = 2)
    {
        try
        {
            nint ra = ResolveRa();
            if (ra == 0) return false;
            nint psd = _resolver.ResolvePsd();
            if (psd == 0) return false;

            // uid -> itemKey pela lista master (itemSaveDatas): key@0x10, uid@0x18. Precisa do key p/ achar o grade.
            var u2k = new Dictionary<ulong, int>();
            nint master = _mem.ReadPtr(psd + (nint)_sym.Get("inv_list_off", GameConstants.InvListOff));
            if (master != 0)
            {
                nint marr = _mem.ReadPtr(master + 0x10);
                uint msz = Math.Min(_mem.ReadU32(master + 0x18), 5000u);
                if (marr != 0)
                    foreach (var o in _mem.ReadArray<ulong>(marr + 0x20, (int)msz))       // BATCH
                        if (o != 0)
                        {
                            var d = _mem.ReadBytes((nint)o + 0x10, 0x10);                 // [0..7]=key, [8..15]=uid
                            if (d.Length >= 0x10) u2k[BitConverter.ToUInt64(d, 8)] = BitConverter.ToInt32(d, 0);
                        }
            }

            // slots do container. A POSIÇÃO no array (i) é o índice do slot usado no MoveRequest (igual ao Python).
            long off = slotType == 2 ? _sym.Get("stash_off", 0x90) : _sym.Get("inv_slots_off", 0x88);
            nint lp = _mem.ReadPtr(psd + (nint)off);
            if (lp == 0) return false;
            nint arr = _mem.ReadPtr(lp + 0x10);
            uint sz = Math.Min(_mem.ReadU32(lp + 0x18), 400u);
            if (arr == 0 || sz == 0) return false;

            var uid = new Dictionary<int, ulong>();     // slot índice -> uid do item que o ocupa
            var grade = new Dictionary<int, int>();      // slot índice -> grade do item
            var unlocked = new List<int>();              // slots desbloqueados (ascendente)
            var ptrs = _mem.ReadArray<ulong>(arr + 0x20, (int)sz);                        // BATCH
            for (int i = 0; i < ptrs.Length; i++)
            {
                nint o = (nint)ptrs[i];
                if (o == 0) continue;
                var d = _mem.ReadBytes(o + 0x18, 0x09);                                   // [0..7]=uid, [8]=unlock (@0x20)
                if (d.Length < 0x09 || d[8] == 0) continue;                               // só slots desbloqueados
                unlocked.Add(i);
                ulong u = BitConverter.ToUInt64(d, 0);
                if (u != 0) { uid[i] = u; grade[i] = GradeOfKey(u2k.GetValueOrDefault(u, 0)); }
            }
            if (uid.Count < 2) return false;

            // uids na ordem de grade (empate = menor índice, sort estável igual ao Python).
            var want = uid.Keys.OrderBy(s => grade[s]).ThenBy(s => s).Select(s => uid[s]).ToList();
            unlocked.Sort();
            var targets = unlocked.Take(uid.Count).ToList();                              // os n menores slots
            var empties = unlocked.Where(i => !uid.ContainsKey(i)).ToList();

            for (int idx = 0; idx < targets.Count; idx++)                                 // acha a 1ª posição errada
            {
                int p = targets[idx];
                if (uid.TryGetValue(p, out var cur) && cur == want[idx]) continue;        // já certo
                int src = -1;                                                             // slot que tem o item desejado
                foreach (var kv in uid) if (kv.Value == want[idx]) { src = kv.Key; break; }
                if (src < 0) return false;
                if (uid.ContainsKey(p))                                                   // p ocupado por item errado -> tira pra um empty
                {
                    if (empties.Count == 0) return false;
                    _disp.CommandStash(ra, [slotType, p, slotType, empties[0], 1]);
                    Thread.Sleep(280);
                }
                _disp.CommandStash(ra, [slotType, src, slotType, p, 1]);
                Thread.Sleep(280);
                return true;                                                              // um move por chamada; o loop re-chama
            }
            return false;                                                                // já ordenado
        }
        catch { return false; }
    }

    // Grade REAL do item (via izb/ItemInfoData, cacheado). Fallback: dígito da key (porta de _grade_of_key).
    private int GradeOfKey(int key)
    {
        if (key == 0) return 99;
        var info = ItemInfo(key);
        return info is { } inf ? inf.Grade : (key / 1000) % 10;
    }

    // (Type, Grade, Synth, Level) via izb (getter puro off-thread), cacheado. Mesmo padrão do AutoFuse.
    private (int Type, int Grade, int Synth, int Level)? ItemInfo(int key)
    {
        if (key == 0) return null;
        if (_itemInfo.TryGetValue(key, out var c)) return c;
        long izb = _sym.Get("izb");
        (int, int, int, int)? info = null;
        if (izb != 0)
        {
            ulong p = RemoteCall.Invoke(_mem, (long)(Base + (nint)izb), key);
            if (MemoryAccess.IsValidPointer((nint)p))
            {
                int type = _mem.ReadI32((nint)p + (nint)_sym.Get("iteminfo_type", 0x34)),
                    grade = _mem.ReadI32((nint)p + (nint)_sym.Get("iteminfo_grade", 0x38)),
                    synth = _mem.ReadI32((nint)p + (nint)_sym.Get("iteminfo_synth", 0x48)),
                    level = _mem.ReadI32((nint)p + (nint)_sym.Get("iteminfo_level", 0x6C));
                if (grade is >= 0 and <= 10 && synth is >= 0 and <= 3) info = (type, grade, synth, level);
            }
        }
        _itemInfo[key] = info;
        return info;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        int last = hay.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            int k = 0; while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
}
