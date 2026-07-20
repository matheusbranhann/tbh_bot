using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>Definição estática de UMA runa (da RuneInfoData). Alimenta a ÁRVORE de runas + o clamp por-runa.</summary>
/// <param name="Key">RuneKey (@0x30) — a mesma chave do RuneSaveData.</param>
/// <param name="Name">NameKey (@0x38) sem o prefixo "RuneName_" (chave de localização / rótulo do nó).</param>
/// <param name="Max">MaxLevel (@0x34) — TETO por-runa (nunca passar disto: over-max = NRE em RuneNode.mav = loading infinito).</param>
/// <param name="Next">Conexões da árvore (Next1@0x40 + Next2@0x48): chaves das runas ligadas a esta.</param>
/// <param name="Icon">IconPath (@0x58) — caminho do sprite, "" se ausente.</param>
public sealed record RuneDef(int Key, string Name, int Max, int[] Next, string Icon);

/// <summary>
/// Lê as definições de CADA runa (~197) via RuneInfoData. Porta de read_rune_defs() do tbh_core.py.
/// Não há tabela exportada: resolve o Il2CppClass* de "TaskbarHero.Data".RuneInfoData e VARRE a memória
/// (regiões RW/RWX) por ponteiros para essa klass — cada match 8-alinhado é o início de uma instância
/// (o header IL2CPP guarda a klass @+0). Dado 100% ESTÁTICO -> cacheado após a 1ª leitura bem-sucedida.
/// </summary>
public sealed class RuneDefs(MemoryAccess mem, Il2CppApi api, MemoryScanner scan)
{
    private readonly MemoryAccess _mem = mem;
    private readonly Il2CppApi _api = api;
    private readonly MemoryScanner _scan = scan;

    // Offsets dos campos da RuneInfoData (confirmados no Python: 0x30/0x34/0x38/0x40/0x48/0x58).
    private const int ORuneKey = 0x30;   // int
    private const int OMaxLevel = 0x34;  // int
    private const int ONameKey = 0x38;   // System.String*
    private const int ONext1 = 0x40;     // System.String* (chaves separadas por espaço)
    private const int ONext2 = 0x48;     // System.String*
    private const int OIconPath = 0x58;  // System.String*

    // Protects varridos: PAGE_READWRITE (0x04) e PAGE_EXECUTE_READWRITE (0x40) — igual ao Python.
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;

    private Dictionary<int, RuneDef>? _cache;

    /// <summary>{RuneKey: RuneDef}. Cacheado (estático). Retorna vazio se não resolveu a klass / nada casou.</summary>
    public Dictionary<int, RuneDef> Read(bool force = false)
    {
        if (!force && _cache is { Count: > 0 }) return _cache;

        var defs = new Dictionary<int, RuneDef>();
        long klass = _api.ClassFromName("TaskbarHero.Data", "RuneInfoData");
        if (klass == 0) return defs;

        ulong klassPtr = (ulong)klass;
        foreach (var (regionBase, size) in _scan.Regions(PageReadWrite, PageExecuteReadWrite))
        {
            byte[] d = _mem.ReadBytes(regionBase, size);
            if (d.Length < 8) continue;

            // Varre só posições 8-alinhadas (j % 8 == 0 no Python): o ponteiro-klass fica alinhado no header.
            for (int j = 0; j + 8 <= d.Length; j += 8)
            {
                if (BitConverter.ToUInt64(d, j) != klassPtr) continue;

                nint a = regionBase + j;
                int key = _mem.ReadI32(a + ORuneKey);
                int mx = _mem.ReadI32(a + OMaxLevel);
                string? name = Il2Str(_mem.ReadPtr(a + ONameKey));

                // Filtro de falso-positivo (idem Python): precisa de nome válido + key plausível.
                if (string.IsNullOrEmpty(name) || key < 0 || key >= 20_000_000) continue;

                var next = new List<int>();
                foreach (int fld in (int[])[ONext1, ONext2])
                {
                    string? s = Il2Str(_mem.ReadPtr(a + fld));
                    if (string.IsNullOrEmpty(s)) continue;
                    foreach (string tok in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (tok.All(char.IsDigit) && int.TryParse(tok, out int nk)) next.Add(nk);
                }

                string icon = Il2Str(_mem.ReadPtr(a + OIconPath)) ?? "";
                // name.replace("RuneName_", "") — rótulo limpo do nó.
                string clean = name.Replace("RuneName_", "");
                defs[key] = new RuneDef(key, clean, mx == 0 ? 1 : mx, [.. next], icon);
            }
        }

        if (defs.Count > 0) _cache = defs;
        return defs;
    }

    /// <summary>
    /// System.String IL2CPP (len@0x10, chars UTF-16LE @0x14) COM validação — porta fiel de _il2str do Python.
    /// A validação (len 1..300 + faixa de caracteres) é LOAD-BEARING: filtra lixo durante o scan cru de memória,
    /// por isso NÃO se usa mem.ReadIl2CppString (que não valida os caracteres). null se inválido.
    /// </summary>
    private string? Il2Str(nint ptr)
    {
        if ((ulong)ptr < 0x10000) return null;
        int ln = _mem.ReadI32(ptr + 0x10);
        if (ln <= 0 || ln > 300) return null;
        byte[] raw = _mem.ReadBytes(ptr + 0x14, ln * 2);
        if (raw.Length != ln * 2) return null;
        string s = System.Text.Encoding.Unicode.GetString(raw);
        // Python: all(31 < ord(c) < 0x3000 or c in ' /-_.')
        foreach (char c in s)
            if (!((c > 31 && c < 0x3000) || c is ' ' or '/' or '-' or '_' or '.')) return null;
        return s;
    }
}
