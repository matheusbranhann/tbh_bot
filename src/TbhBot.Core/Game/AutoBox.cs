using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Auto-abrir caixas (porta de _find_stageboxes/_valid_stagebox/_iuw_count + o loop de abertura do
/// tbh_core.py). Acha as StageBox VIVAS por klass, conta as esperando via iuw (RemoteCall, getter puro)
/// e abre cada uma via o dispatcher (cmd1 = llx na main-thread).
///
/// CUIDADO (medido): clicar numa StageBox DESTRUÍDA fecha o jogo — por isso <see cref="ValidStageBox"/>
/// checa o m_CachedPtr do Unity (@+0x10). Nunca dispara llx sem revalidar.
/// </summary>
public sealed class AutoBox(MemoryAccess mem, SymbolTable sym, MemoryScanner scan, Il2CppApi api, RealDispatcher disp)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly MemoryScanner _scan = scan;
    private readonly Il2CppApi _api = api;
    private readonly RealDispatcher _disp = disp;
    public Action<string>? Log;

    private long _klass;
    private Dictionary<int, nint>? _cache;

    private static bool HeapPtr(nint p) => (ulong)p >= 0x10000000000 && (ulong)p < 0x7f0000000000;

    /// <summary>A StageBox em `a` está VIVA (não é cadáver no heap)? Espelha o _valid_stagebox.</summary>
    public bool ValidStageBox(nint a, long klass)
    {
        if ((long)_mem.ReadU64(a) != klass) return false;
        if (!HeapPtr((nint)_mem.ReadU64(a + 0x10))) return false;             // m_CachedPtr -> destruída
        if (_mem.ReadU32(a + 0x38) > 2) return false;                         // EBoxType 0..2
        nint btn = (nint)_mem.ReadU64(a + 0x58);
        if (!HeapPtr(btn) || (long)_mem.ReadU64(btn) == klass) return false;
        var hb = _mem.ReadBytes(a + 0x128, 1); var bz = _mem.ReadBytes(a + 0xA0, 1);
        return hb.Length == 1 && hb[0] <= 1 && bz.Length == 1 && bz[0] <= 1;
    }

    /// <summary>{EBoxType -> ptr} das StageBox reais e vivas (cadáveres com klass intacto são rejeitados).</summary>
    public Dictionary<int, nint> FindStageBoxes()
    {
        long klass = _api.ClassFromName("TaskbarHero.UI", "StageBox");
        if (klass == 0) return new();
        if (_cache is not null && _klass == klass && _cache.Values.All(a => ValidStageBox(a, klass)))
            return _cache;

        _klass = klass;
        var needle = BitConverter.GetBytes((ulong)klass);
        var found = new Dictionary<int, nint>();
        foreach (var (rb, size) in _scan.Regions(0x04, 0x40))
        {
            var d = _mem.ReadBytes(rb, size);
            if (d.Length == 0) continue;
            int j = IndexOf(d, needle, 0);
            while (j >= 0)
            {
                if (j % 8 == 0 && ValidStageBox(rb + j, klass))
                    found.TryAdd((int)_mem.ReadU32(rb + j + 0x38), rb + j);
                j = IndexOf(d, needle, j + 8);
            }
            if (found.Count >= 3) break;
        }
        _cache = found;
        return found;
    }

    /// <summary>Contagem REAL de caixas abríveis do tipo (iuw = uw.tw, getter puro off-thread). null se sem sym.</summary>
    public int? IuwCount(int boxtype)
    {
        long iuw = _sym.Get("iuw");
        if (iuw == 0) return null;
        ulong r = RemoteCall.Invoke(_mem, (long)(_mem.Target.ModuleBase + (nint)iuw), boxtype);
        return (int)(r & 0xffffffff);
    }

    private static readonly string[] Names = ["NORMAL", "BOSS", "ACTBOSS"];

    /// <summary>Abre todas as caixas esperando (iuw>0). <paramref name="keepGoing"/> = condição de continuar. True se abriu alguma.</summary>
    public bool OpenAll(Func<bool> keepGoing)
    {
        var boxes = FindStageBoxes();
        if (boxes.Count == 0) return false;
        long klass = _klass;
        bool opened = false;
        for (int t = 0; t <= 2 && keepGoing(); t++)
        {
            if (!boxes.TryGetValue(t, out var tgt)) continue;
            int? cnt = IuwCount(t); int guard = 0;
            while (cnt is > 0 && guard < 15 && keepGoing())
            {
                if (!ValidStageBox(tgt, klass))                               // morreu no meio -> re-scan (nunca clicar em cadáver)
                {
                    _cache = null;
                    boxes = FindStageBoxes();
                    if (!boxes.TryGetValue(t, out tgt) || !ValidStageBox(tgt, klass)) break;
                }
                _disp.Command(1, tgt);                                        // llx(box) na main-thread
                Thread.Sleep(150);
                int? nw = IuwCount(t);
                if (nw is null) break;
                if (nw < cnt) { Log?.Invoke($"🎁 caixa {Names[t]} aberta{(nw > 0 ? $" ({nw} restam)" : "")}"); opened = true; cnt = nw; guard++; }
                else break;                                                   // não decrementou -> para esse tipo
            }
        }
        return opened;
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
