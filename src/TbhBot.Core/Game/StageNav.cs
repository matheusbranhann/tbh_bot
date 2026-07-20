using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Navegação de estágios — base do modo Evolução / Auto-boss (porta de stage_table / _stage_cache /
/// goto_stage / enter_boss do tbh_core.py). São 120 estágios; o progresso é UM int (maxCompletedStage)
/// e a corrente de entrada é a mesma NextStageKey que o jogo usa. Aqui só o transporte:
///  - <see cref="StageTable"/>: lê a tabela VIVA {key -> {next,type,ss,lvl}} (cacheada, é estática).
///  - <see cref="StageCache"/>: pega o StageCache* de um key pelo Dictionary&lt;int,StageCache&gt; do jogo.
///  - <see cref="GoToStage"/>: jgk (passo final de entrada de estágio NORMAL), na main-thread.
///  - <see cref="EnterBoss"/>: o PAR jgd+jgk que reproduz o CLIQUE do x-10.
/// </summary>
public sealed class StageNav(MemoryAccess mem, SymbolTable sym, Il2CppResolver resolver, RealDispatcher disp)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly Il2CppResolver _resolver = resolver;
    private readonly RealDispatcher _disp = disp;
    public Action<string>? Log;

    // A tabela de estágios é estática no jogo -> cacheia depois de ler cheia (>=100 keys, como o Python).
    private Dictionary<int, StageInfo>? _stageTbl;

    private nint Base => _mem.Target.ModuleBase;

    /// <summary>
    /// {stageKey -> {next,type,ss,lvl}} lido da tabela VIVA do jogo (120 estágios). Cacheado.
    /// bal (singleton da tabela balanceada) = deref duplo do static_fields de bal_ti; a lista fica em +stage_off,
    /// e cada StageInfoData tem key@0x30, type@0x40, lvl@0x50, ss@0x94, next@0xA0.
    /// </summary>
    public Dictionary<int, StageInfo> StageTable()
    {
        if (_stageTbl is { } cached) return cached;

        long ti = _sym.Get("bal_ti"), off = _sym.Get("stage_off");
        var empty = new Dictionary<int, StageInfo>();
        if (ti == 0 || off == 0) return empty;

        // klass=[base+ti]; bal=[[klass+0xB8]] (StaticFields já faz [[base+ti]+0xB8]; falta o último deref).
        nint sf = _resolver.StaticFields(ti);
        if (sf == 0) return empty;
        nint bal = _mem.ReadPtr(sf);
        if (!MemoryAccess.IsValidPointer(bal)) return empty;

        nint lst = _mem.ReadPtr(bal + (nint)off);
        if (!MemoryAccess.IsValidPointer(lst)) return empty;
        nint arr = _mem.ReadPtr(lst + 0x10);
        uint n = _mem.ReadU32(lst + 0x18);
        if (!MemoryAccess.IsValidPointer(arr) || n == 0 || n > 500) return empty;

        var t = new Dictionary<int, StageInfo>();
        // BATCH: os elementos são ponteiros contíguos a partir de arr+0x20.
        ulong[] elems = _mem.ReadArray<ulong>(arr + 0x20, (int)n);
        foreach (ulong e in elems)
        {
            nint o = (nint)e;
            if (!MemoryAccess.IsValidPointer(o)) continue;
            int k = _mem.ReadI32(o + 0x30);
            if (k == 0) continue;
            t[k] = new StageInfo(
                Next:  _mem.ReadI32(o + 0xA0),
                Type:  _mem.ReadI32(o + 0x40),
                Ss:    _mem.ReadI32(o + 0x94),
                Lvl:   _mem.ReadI32(o + 0x50),
                Waves: _mem.ReadI32(o + 0x54));   // WaveAmount — usado p/ detectar "estágio limpo" na evolução
        }
        if (t.Count >= 100) _stageTbl = t;   // só cacheia quando veio íntegra (evita fixar uma leitura parcial no boot)
        return t;
    }

    /// <summary>
    /// StageCache* do stageKey, pelo Dictionary&lt;int,StageCache&gt; do próprio jogo (0 se não existe).
    /// sf = static_fields de uo_ti (deref duplo, = _uo_sf); dict em +uo_dict; entries em dict+0x18 (n em +0x20),
    /// cada entrada de 0x18 bytes com key@+8 e value(StageCache*)@+0x10.
    /// </summary>
    public nint StageCache(int key)
    {
        long tiUo = _sym.Get("uo_ti"), off = _sym.Get("uo_dict");
        if (tiUo == 0 || off == 0) return 0;

        nint sf = _resolver.StaticFields(tiUo);   // = _uo_sf(): [[base+uo_ti]+0xB8]
        if (sf == 0) return 0;
        nint d = _mem.ReadPtr(sf + (nint)off);
        if (!MemoryAccess.IsValidPointer(d)) return 0;

        nint ent = _mem.ReadPtr(d + 0x18);
        uint n = _mem.ReadU32(d + 0x20);
        if (!MemoryAccess.IsValidPointer(ent) || n == 0 || n > 4096) return 0;

        for (uint i = 0; i < n; i++)
        {
            nint a = ent + 0x20 + (nint)(i * 0x18);
            if (_mem.ReadU32(a + 8) == (uint)key) return _mem.ReadPtr(a + 0x10);
        }
        return 0;
    }

    /// <summary>
    /// Vai pra um estágio NORMAL. jgk é o passo final de entrada dos dois caminhos do jogo — precisa da
    /// main-thread (Call/cmd12). Guarda contra key inexistente: sem o StageCache, jgk lança KeyNotFound.
    /// </summary>
    public bool GoToStage(int key)
    {
        long jgk = _sym.Get("jgk");
        if (jgk == 0) return false;
        if (StageCache(key) == 0) return false;                 // key inexistente -> jgk crasharia
        _disp.Call((long)(Base + (nint)jgk), (nint)key);        // rcx = key
        return true;
    }

    /// <summary>
    /// jgc(cache) do próprio jogo: 0=Success, 1=EndStage, 2=NeedSoulStone, 3=NeedChestSpace, 4=Failed.
    /// Melhor que checar o inventário na mão — o jogo também confere ESPAÇO DE BAÚ. Só lê (main-thread Call).
    /// null se offsets ausentes / key inexistente.
    /// </summary>
    public int? CanEnter(int key)
    {
        long jgc = _sym.Get("jgc");
        nint c = StageCache(key);
        if (jgc == 0 || c == 0) return null;
        return _disp.Call((long)(Base + (nint)jgc), c);
    }

    /// <summary>
    /// Entra num ACTBOSS (x-10) reproduzindo o CLIQUE: jgd(cache,FAKE) + jgk(key).
    /// O PAR é obrigatório: o Action&lt;bool&gt; que o clique passa NÃO é cosmético — ele chama o jgk. jgd sozinho
    /// reservaria a soulstone (beyt) e NÃO carregaria a fase (cliente meio-feito); jgk sozinho não reserva a
    /// pedra nem grava o ponto de retorno (beyq). A pedra só é COBRADA quando o boss morre -> entrar é reversível.
    /// </summary>
    public bool EnterBoss(int key)
    {
        long jgd = _sym.Get("jgd"), jgk = _sym.Get("jgk");
        if (jgd == 0 || jgk == 0) return false;
        nint c = StageCache(key);
        if (c == 0) return false;

        _disp.Command(13, c);                                   // cmd13 = jgd(cache,FAKE): marca o retorno + reserva a pedra
        Thread.Sleep(150);                                      // deixa o jgd assentar antes do jgk (mesma folga do Python)
        _disp.Call((long)(Base + (nint)jgk), (nint)key);        // o que o callback do clique faria
        return true;
    }
}

/// <summary>Uma linha da tabela de estágios: NextStageKey, tipo (1=x-10/boss), soulstone, nível recomendado e nº de waves.</summary>
public sealed record StageInfo(int Next, int Type, int Ss, int Lvl, int Waves = 0);
