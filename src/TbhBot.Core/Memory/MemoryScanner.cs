using TbhBot.Core.Native;

namespace TbhBot.Core.Memory;

/// <summary>
/// Scanner de AOB e enumerador de regioes de memoria.
/// Porta de tbh_core.py: aob() (1065-1086), parse_aob() (791-796) e _mem_regions() (1219-1226).
/// Sem cache aqui (o Python cacheava por 'key'); quem chama e responsavel por memoizar.
/// </summary>
public sealed class MemoryScanner(MemoryAccess mem)
{
    private readonly MemoryAccess _mem = mem;

    // Le em blocos de 0x400000 (com overlap do tamanho do padrao p/ pegar matches na borda), igual ao Python.
    private const int Chunk = 0x400000;

    /// <summary>Padrao "48 8B ?? ..": token "??" (ou so '?') = wildcard. Retorna bytes + mascara (1=comparar).</summary>
    private static (byte[] Bytes, bool[] Mask) ParseAob(string pattern)
    {
        var toks = pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[toks.Length];
        var mask = new bool[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            string t = toks[i];
            if (t.Length > 0 && t.TrimStart('?').Length == 0)   // "??" ou "?" -> wildcard
            {
                bytes[i] = 0; mask[i] = false;
            }
            else
            {
                bytes[i] = Convert.ToByte(t, 16); mask[i] = true;
            }
        }
        return (bytes, mask);
    }

    /// <summary>Primeiro endereco absoluto que casa o padrao no modulo; 0 se nao achar.</summary>
    public nint FindAob(string pattern)
    {
        var hits = Scan(pattern, findAll: false);
        return hits.Count > 0 ? hits[0] : 0;
    }

    /// <summary>Todos os enderecos que casam o padrao no modulo.</summary>
    public List<nint> FindAllAob(string pattern) => Scan(pattern, findAll: true);

    private List<nint> Scan(string pattern, bool findAll)
    {
        var (pb, mk) = ParseAob(pattern);
        int pl = pb.Length;
        var hits = new List<nint>();
        if (pl == 0) return hits;

        nint moduleBase = _mem.Target.ModuleBase;
        int size = _mem.Target.ModuleSize;
        byte first = pb[0];
        bool firstWild = !mk[0];

        int off = 0;
        while (off < size)
        {
            // overlap de 'pl' bytes p/ nao perder match que cruza a fronteira do bloco
            int n = Math.Min(Chunk + pl, size - off);
            byte[] d = _mem.ReadBytes(moduleBase + off, n);
            if (d.Length >= pl)
            {
                int limit = d.Length - pl;
                int i = 0;
                while (i <= limit)
                {
                    // procura o 1o byte (a menos que seja wildcard, ai varre tudo)
                    int j = firstWild ? i : Array.IndexOf(d, first, i);
                    if (j < 0 || j > limit) break;

                    bool ok = true;
                    for (int k = 0; k < pl; k++)
                    {
                        if (mk[k] && d[j + k] != pb[k]) { ok = false; break; }
                    }
                    if (ok)
                    {
                        nint a = moduleBase + off + j;
                        if (!findAll) { hits.Add(a); return hits; }
                        hits.Add(a);
                    }
                    i = j + 1;
                }
            }
            off += Chunk;
        }
        return hits;
    }

    /// <summary>
    /// Regioes MEM_COMMIT cujo Protect esta em 'protects' e tamanho &lt; 0x8000000.
    /// Porta de _mem_regions(): varre todo o espaco do usuario via VirtualQueryEx.
    /// </summary>
    public List<(nint Base, int Size)> Regions(params uint[] protects)
    {
        var protSet = new HashSet<uint>(protects);
        var outList = new List<(nint, int)>();
        nint h = _mem.Target.Handle;
        ulong addr = 0;
        while (addr < 0x7fffffffffffUL)
        {
            if (NativeMethods.VirtualQueryEx(h, (nint)addr, out var mbi,
                    (nuint)Marshal_SizeOf) == 0)
                break;
            ulong regionBase = mbi.BaseAddress != 0 ? mbi.BaseAddress : 0;
            ulong regionSize = mbi.RegionSize != 0 ? mbi.RegionSize : 0x1000;
            if (mbi.State == NativeMethods.MEM_COMMIT
                && protSet.Contains(mbi.Protect)
                && regionSize > 0 && regionSize < 0x8000000UL)
            {
                outList.Add(((nint)regionBase, (int)regionSize));
            }
            addr = regionBase + regionSize;
        }
        return outList;
    }

    // sizeof(MEMORY_BASIC_INFORMATION) — layout do struct em Native (2 ulong ptr-fields + 6 uint = 0x30 no x64)
    private static readonly int Marshal_SizeOf =
        System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
}
