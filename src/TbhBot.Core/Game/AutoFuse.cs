using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Auto-fuse / síntese do cubo (porta de _do_synth + os helpers do tbh_core.py). UMA fusão por chamada.
/// Fluxo: conta os fundíveis; se um TIPO tem >=9 de um grade <= teto -> abre o cubo, põe em Synthesis Lv.65~80,
/// seleciona o tipo, AUTO-FILL (cmd4), confere o grade real enchido (fail-safe do teto) e FUNDE (cmd5), fecha.
/// CONSOME 9 itens -> 1 de grade acima (grade rolado no servidor). Level-safe (só o tier 65~80).
/// </summary>
public sealed class AutoFuse(MemoryAccess mem, SymbolTable sym, Il2CppResolver resolver, RealDispatcher disp)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly Il2CppResolver _resolver = resolver;
    private readonly RealDispatcher _disp = disp;
    public Action<string>? Log;

    // Config (o painel seta): teto de grade (0=common..; default 2=rare) e tipos permitidos {0=Gear,1=Accessory,2=Material}.
    public int MaxGrade = 2;
    public HashSet<int> Types = [0, 1, 2];

    private readonly Dictionary<int, (int Type, int Grade, int Synth, int Level)?> _itemInfo = new();
    private readonly Dictionary<int, long> _block = new();   // anti-livelock por tipo (tick de expiração)

    // offsets (do sym, com defaults do build c824)
    private long OGrade => _sym.Get("cube_grade", 0xC8);
    private long OBers => _sym.Get("cube_bers", 0xE0);
    private long OInlist => _sym.Get("cube_inlist", 0x100);
    private long OActive => _sym.Get("cube_active", 0x140);
    private long OBusy => _sym.Get("cube_busy", 0x150);
    private long OCubeType => _sym.Get("cube_type", 0x254);
    private long OLvRecipe => _sym.Get("cube_lvrecipe", 0x258);
    private long OItType => _sym.Get("iteminfo_type", 0x34);
    private long OItGrade => _sym.Get("iteminfo_grade", 0x38);
    private long OItSynth => _sym.Get("iteminfo_synth", 0x48);
    private long OItLevel => _sym.Get("iteminfo_level", 0x6C);

    private nint Base => _mem.Target.ModuleBase;

    private nint CubeSf()
    {
        long cs = _sym.Get("cube_slot");
        if (cs == 0) { cs = _resolver.ResolveCubeSlot(); if (cs != 0) _sym["cube_slot"] = cs; }
        return cs != 0 ? _resolver.StaticFields(cs) : 0;
    }

    // ---------------- item info (izb, getter puro off-thread) ----------------

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
                int type = _mem.ReadI32((nint)p + (nint)OItType), grade = _mem.ReadI32((nint)p + (nint)OItGrade),
                    synth = _mem.ReadI32((nint)p + (nint)OItSynth), level = _mem.ReadI32((nint)p + (nint)OItLevel);
                if (grade is >= 0 and <= 10 && synth is >= 0 and <= 3) info = (type, grade, synth, level);
            }
        }
        _itemInfo[key] = info;
        return info;
    }

    // [(index, itemKey, uniqueId)] de itemSaveDatas (o index é o que o auto-fill/ioa espera).
    private List<(int Index, int Key)> InvIndexItems()
    {
        var outl = new List<(int, int)>();
        nint psd = _resolver.ResolvePsd();
        if (psd == 0) return outl;
        nint lst = _mem.ReadPtr(psd + (nint)_sym.Get("inv_list_off", GameConstants.InvListOff));
        if (lst == 0) return outl;
        nint arr = _mem.ReadPtr(lst + 0x10);
        uint size = _mem.ReadU32(lst + 0x18);
        if (arr == 0 || size >= 500000) return outl;
        foreach (var (it, i) in _mem.ReadArray<ulong>(arr + 0x20, (int)size).Select((v, i) => (v, i)))
            if (it != 0) outl.Add((i, _mem.ReadI32((nint)it + (nint)_sym.Get("itemsave_key", 0x10))));
        return outl;
    }

    // {(synth, grade): qtd} dos fundíveis: grade<=teto, tipo permitido, Lv>=61 (material dispensa nível).
    private Dictionary<(int, int), int> SynthFuseable()
    {
        var c = new Dictionary<(int, int), int>();
        foreach (var (_, key) in InvIndexItems())
        {
            var info = ItemInfo(key);
            if (info is not { } inf) continue;
            var (_, grade, synth, level) = inf;
            if (grade >= 0 && grade <= MaxGrade && Types.Contains(synth) && (synth == 2 || level >= 61))
                c[(synth, grade)] = c.GetValueOrDefault((synth, grade)) + 1;
        }
        return c;
    }

    // ---------------- cubo UI (abrir/fechar/setar tier/tipo) ----------------

    private nint UiManager()
    {
        long ti = _sym.Get("uimgr_ti");
        if (ti == 0) return 0;
        nint sf = _resolver.StaticFields(ti);   // nq<T> instância = [static_fields+0]
        return sf != 0 ? _mem.ReadPtr(sf) : 0;
    }

    private bool CubeIsOpen()
    {
        nint sf = CubeSf();
        return sf != 0 && MemoryAccess.IsValidPointer(_mem.ReadPtr(sf + (nint)OActive));
    }

    private bool OpenCube()
    {
        if (CubeIsOpen()) return true;
        nint um = UiManager();
        long eby = _sym.Get("eby");
        if (um == 0 || eby == 0) { Log?.Invoke("fuse: UIManager/eby ausente"); return false; }
        nint uimain = _mem.ReadPtr(um + 0xA8);
        if (uimain == 0) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _disp.Call(Base + (nint)eby, uimain);
            for (int t = 0; t < 13; t++) { if (CubeIsOpen()) return true; Thread.Sleep(100); }
            Thread.Sleep(400);
        }
        return false;
    }

    private void CloseCube()
    {
        nint um = UiManager();
        long hgr = _sym.Get("hgr");
        if (um != 0 && hgr != 0) _disp.Call(Base + (nint)hgr, um);
    }

    // As recipes de SYNTHESIS (os tiers do dropdown). Última = Lv.65~80.
    private List<nint> SynthRecipes()
    {
        var outl = new List<nint>();
        nint sf = CubeSf();
        if (sf == 0) return outl;
        nint d = _mem.ReadPtr(sf + (nint)OBers);
        if (d == 0) return outl;
        nint ent = _mem.ReadPtr(d + 0x18); uint n = _mem.ReadU32(d + 0x20);
        nint syn = 0;
        for (uint i = 0; i < n; i++)
        {
            nint a = ent + 0x20 + (nint)(i * 0x18);
            if (_mem.ReadU32(a + 8) == 1) { syn = _mem.ReadPtr(a + 0x10); break; }   // ERecipeType.SYNTHESIS=1
        }
        if (syn == 0) return outl;
        nint arr = _mem.ReadPtr(syn + 0x10); uint m = _mem.ReadU32(syn + 0x18);
        if (arr == 0 || m == 0 || m > 64) return outl;
        for (uint i = 0; i < m; i++) outl.Add(_mem.ReadPtr(arr + 0x20 + (nint)(i * 8)));
        return outl;
    }

    private bool SynthSetLv6580()
    {
        var recs = SynthRecipes();
        if (recs.Count == 0) return false;
        nint target = recs[^1];                          // Lv.65~80 = último tier
        _disp.Command(6, target); Thread.Sleep(150);     // inf(uw)
        nint sf = CubeSf();
        return sf != 0 && _mem.ReadPtr(sf + (nint)OLvRecipe) == target;
    }

    private bool SynthSetType(int t)
    {
        _disp.Command(3, 0, t); Thread.Sleep(150);       // ilo(EItemSynthesisType) — RESETA o nível
        nint sf = CubeSf();
        return sf != 0 && _mem.ReadU32(sf + (nint)OCubeType) == (uint)t;
    }

    private bool SynthBusy()
    {
        nint sf = CubeSf();
        if (sf == 0) return true;
        var b = _mem.ReadBytes(sf + (nint)OBusy, 1);
        return b.Length == 0 || b[0] != 0;               // [model+busy]!=0 = ocupado
    }

    // Nº de itens REAIS no cubo (CubeInData com bfbk@0x18 != null).
    private int CubeRealn()
    {
        nint sf = CubeSf();
        if (sf == 0) return 0;
        nint lst = _mem.ReadPtr(sf + (nint)OInlist);
        if (lst == 0) return 0;
        uint n = Math.Min(_mem.ReadU32(lst + 0x18), 12u); nint arr = _mem.ReadPtr(lst + 0x10);
        int c = 0;
        for (uint i = 0; i < n; i++)
        {
            nint ent = _mem.ReadPtr(arr + 0x20 + (nint)(i * 8));
            if (ent != 0 && _mem.ReadU64(ent + 0x18) != 0) c++;
        }
        return c;
    }

    private void SetIncludeStash()
    {
        try
        {
            nint psd = _resolver.ResolvePsd();
            if (psd == 0) return;
            nint cs = _mem.ReadPtr(psd + (nint)_sym.Get("psd_common_off", 0x10));   // commonSaveData
            if (MemoryAccess.IsValidPointer(cs))
                _mem.WriteBytes(cs + (nint)_sym.Get("commonsave_usestorage", 0x60), [0x01]);  // useStorage=Include-Stash
        }
        catch { /* opcional */ }
    }

    private static readonly string[] TypeNames = ["Equipment", "Accessory", "Material"];
    private static readonly string[] GradeNames = ["common", "uncommon", "rare", "legendary", "immortal", "arcana", "beyond", "celestial", "divine", "cosmic"];

    /// <summary>UMA fusão (o loop re-chama). True se fundiu. CONSOME 9 itens -> 1.</summary>
    public bool DoSynth(Func<bool> keepGoing)
    {
        if (_sym.Get("ilo") == 0 || _sym.Get("imx") == 0 || _sym.Get("eby") == 0 || _sym.Get("uimgr_ti") == 0)
            return false;
        var counts = SynthFuseable();
        long now = Environment.TickCount64;

        int tgt = -1;
        foreach (int t in Types.OrderBy(x => x))
        {
            if (_block.TryGetValue(t, out var until) && until > now) continue;
            for (int g = 0; g <= MaxGrade; g++)
                if (counts.GetValueOrDefault((t, g)) >= 9) { tgt = t; break; }
            if (tgt >= 0) break;
        }
        if (tgt < 0) return false;                       // nada pra fundir (dado o teto/tipos) -> loop só verifica

        if (!OpenCube()) return false;
        nint sf = CubeSf();
        SetIncludeStash();                               // sem isto o auto-fill só olha o inventário
        if (!SynthSetLv6580()) { CloseCube(); return false; }
        SynthSetType(tgt);
        SynthSetLv6580();                                // trocar o tipo reseta o nível -> re-por 65~80
        _mem.Write<int>(sf + (nint)OGrade, 0x7F);        // SENTINELA: se o auto-fill não reescrever, fica >teto -> pula
        _disp.Command(4); Thread.Sleep(250);             // AUTO-FILL (ipu)
        SynthSetLv6580();
        if (CubeRealn() < 9) { _mem.Write<int>(sf + (nint)OGrade, 0x7F); _disp.Command(4); Thread.Sleep(250); }
        if (CubeRealn() < 9) { CloseCube(); return false; }

        int fg = _mem.ReadI32(sf + (nint)OGrade);        // grade REAL que o auto-fill enfiou
        if (fg < 0 || fg > MaxGrade)                     // TETO (fail-safe): só funde grade válido 0..teto
        {
            _block[tgt] = now + 20000;                   // anti-livelock: 20s
            CloseCube(); return false;
        }
        if (!keepGoing()) { CloseCube(); return false; }

        _disp.Command(5);                                // SYNTHESIS (imx) — CONSOME 9 -> 1
        for (int i = 0; i < 200; i++) { if (!SynthBusy()) break; Thread.Sleep(50); }
        Log?.Invoke($"⚗️ Síntese: {TypeNames[Math.Clamp(tgt, 0, 2)]} {(fg < GradeNames.Length ? GradeNames[fg] : fg)} (9 fundidos -> 1 acima)");
        Thread.Sleep(2000);                              // deixa a UI assentar antes de fechar
        CloseCube();
        return true;
    }

    /// <summary>Só conta o que fundiria (sem tocar em nada) — para o teste seguro.</summary>
    public Dictionary<(int, int), int> Preview() => SynthFuseable();
}
