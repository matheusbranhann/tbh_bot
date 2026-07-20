using System.Text.Json;
using System.Text.Json.Serialization;
using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Leitura do INVENTARIO COMPLETO (inventario + baus) com nome e grade por item — alimenta a aba Inventory.
/// Porta <c>read_inventory</c> do tbh_core.py e a resolucao de nome/grade que o tbh_panel.py monta na aba:
///   • contagem por itemKey: a lista mestra <c>PlayerSaveData.itemSaveDatas</c> (@inv_list_off) ja contem
///     TODOS os itens (inv + todos os baus) — nao ha listas separadas por bau; cada elemento e um ItemSaveData
///     e o itemKey mora em +itemsave_key. Contamos as ocorrencias -> {itemKey: quantidade}.
///   • NOME: nao e resolvivel headless de forma confiavel (o nome vem de uma NameKey que a UI passa pela
///     tabela de localizacao do jogo — exige chamadas Unity na main-thread, que crasham off-thread). O painel
///     Python resolve o nome pelo <c>item_prices.json</c> embarcado (base_of_key: itemKey -> "base"). Fazemos
///     o mesmo: melhor aproximacao = o "base"/market_hash_name desse JSON, keyed por itemKey. Sem entrada,
///     cai para "#{key}".
///   • GRADE: via ItemInfo (izb, getter puro off-thread — mesmo padrao do AutoFuse.cs). Fallback para o grade
///     embutido no proprio itemKey: <c>gi = (key/1000)%10</c> (o que a aba usa na coluna Grade).
///
/// As defs de nome/grade sao ESTATICAS do build -> cacheadas (o JSON so e lido uma vez; izb por key e memoizado).
/// </summary>
public sealed class Inventory(MemoryAccess mem, SymbolTable sym, Il2CppResolver resolver, string? pricesPath = null)
{
    private readonly MemoryAccess _mem = mem;
    private readonly SymbolTable _sym = sym;
    private readonly Il2CppResolver _resolver = resolver;

    // Nomes de grade (index 0..9) — igual ao GRW do tbh_core.py.
    private static readonly string[] GradeNames =
        ["Common", "Uncommon", "Rare", "Legendary", "Immortal", "Arcana", "Beyond", "Celestial", "Divine", "Cosmic"];

    /// <summary>Nome do grade (Common..Cosmic); "?" fora de 0..9. Conveniencia para a view.</summary>
    public static string GradeName(int gi) => gi is >= 0 and <= 9 ? GradeNames[gi] : "?";

    // izb (ItemInfoData por key) memoizado — grade e dado estatico do item.
    private readonly Dictionary<int, int?> _gradeCache = new();
    // item_prices.json (itemKey -> base name / preco USD) carregado sob demanda, uma vez.
    private Dictionary<int, string>? _names;
    private Dictionary<int, double>? _prices;

    private nint Base => _mem.Target.ModuleBase;

    // offsets (do sym, com os mesmos defaults usados no AutoFuse.cs / read_inventory)
    private nint InvListOff => (nint)_sym.Get("inv_list_off", GameConstants.InvListOff);
    private nint ItemSaveKeyOff => (nint)_sym.Get("itemsave_key", 0x10);
    private nint ItGradeOff => (nint)_sym.Get("iteminfo_grade", 0x38);

    // ---------------- contagem (porta de read_inventory) ----------------

    /// <summary>
    /// {itemKey: quantidade} de TODOS os itens (inventario + baus) lendo a lista mestra itemSaveDatas.
    /// Vazio se nao attachado / nao resolveu o PSD (equivale ao <c>return None</c> do Python: chamador trata).
    /// Batch: 1 syscall para a lista de ponteiros, depois o itemKey de cada elemento.
    /// </summary>
    public Dictionary<int, int> ReadCounts()
    {
        var counts = new Dictionary<int, int>();
        nint psd = _resolver.ResolvePsd();
        if (psd == 0) return counts;

        nint lst = _mem.ReadPtr(psd + InvListOff);
        if (lst == 0) return counts;
        nint arr = _mem.ReadPtr(lst + 0x10);
        uint size = _mem.ReadU32(lst + 0x18);
        if (arr == 0 || size >= 500000) return counts;   // 0<=size<500000 (uint garante >=0)

        ulong[] elems = _mem.ReadArray<ulong>(arr + 0x20, (int)size);
        foreach (ulong e in elems)
        {
            if (e == 0) continue;
            int ik = _mem.ReadI32((nint)e + ItemSaveKeyOff);
            if (ik is >= 100000 and <= 999999)          // faixa valida de itemKey (mesmo filtro do Python)
                counts[ik] = counts.GetValueOrDefault(ik) + 1;
        }
        return counts;
    }

    // ---------------- lista com nome/grade/qtd ----------------

    /// <summary>
    /// Lista completa (nome + grade + quantidade) pronta para a aba Inventory. NAO ordenada aqui — o chamador
    /// ordena por nome/grade/qtd/preco (o record e imutavel e ordenavel via LINQ OrderBy). Vazia se nao resolveu.
    /// </summary>
    public List<InvItem> List()
    {
        var outl = new List<InvItem>();
        var prices = Prices();
        foreach (var (key, qty) in ReadCounts())
        {
            int grade = ItemGrade(key) ?? KeyGrade(key);   // izb primario; fallback = grade embutido no key
            string name = ResolveName(key);
            double unit = prices.GetValueOrDefault(key);   // 0 = preco desconhecido
            outl.Add(new InvItem(key, name, grade, qty, unit));
        }
        return outl;
    }

    // Grade embutido no proprio itemKey: (key/1000)%10 — o que a coluna Grade da aba usa (gi).
    private static int KeyGrade(int key) => (key / 1000) % 10;

    // ItemInfo.grade via izb (getter puro off-thread), memoizado. null se izb ausente / ponteiro invalido.
    private int? ItemGrade(int key)
    {
        if (key == 0) return null;
        if (_gradeCache.TryGetValue(key, out var cached)) return cached;

        int? grade = null;
        long izb = _sym.Get("izb");
        if (izb != 0)
        {
            ulong p = RemoteCall.Invoke(_mem, (long)(Base + (nint)izb), key);
            if (MemoryAccess.IsValidPointer((nint)p))
            {
                int g = _mem.ReadI32((nint)p + ItGradeOff);
                if (g is >= 0 and <= 10) grade = g;     // sanidade (mesmo range do AutoFuse.cs)
            }
        }
        _gradeCache[key] = grade;
        return grade;
    }

    // Nome via item_prices.json (base_of_key). Melhor aproximacao headless — ver o resumo da classe.
    private string ResolveName(int key)
    {
        var map = Names();
        return map.TryGetValue(key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"#{key}";
    }

    private Dictionary<int, string> Names() { Load(); return _names!; }
    private Dictionary<int, double> Prices() { Load(); return _prices!; }

    // Carrega item_prices.json UMA vez, preenchendo nome + preco. Fonte: arquivo (caminho/ao lado do exe/cwd)
    // OU o resource EMBUTIDO (Data/item_prices.json) -> funciona no exe single-file sem arquivos soltos.
    // {"110001": {"base":"Minor Ruby", "market_name":..., "price_usd":0.03}, ...}.
    private void Load()
    {
        if (_names is not null && _prices is not null) return;
        var names = new Dictionary<int, string>();
        var prices = new Dictionary<int, double>();
        try
        {
            using Stream? s = OpenPrices();
            if (s is not null)
            {
                var recs = JsonSerializer.Deserialize<Dictionary<string, PriceRec>>(s);
                if (recs is not null)
                    foreach (var (k, rec) in recs)
                        if (int.TryParse(k, out int ik) && rec is not null)
                        {
                            string? name = rec.Base ?? rec.MarketName ?? rec.Name;
                            if (!string.IsNullOrWhiteSpace(name)) names[ik] = name!;
                            if (rec.PriceUsd is > 0) prices[ik] = rec.PriceUsd.Value;
                        }
            }
        }
        catch { /* sem tabela (ausente/corrompida): cai para "#key"/preco 0 — nao pode quebrar a leitura */ }
        _names = names;
        _prices = prices;
    }

    // Abre item_prices.json: caminho explicito -> ao lado do exe -> cwd -> RESOURCE embutido no assembly.
    private Stream? OpenPrices()
    {
        if (!string.IsNullOrWhiteSpace(pricesPath) && File.Exists(pricesPath)) return File.OpenRead(pricesPath);
        foreach (var cand in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "item_prices.json"),
                     Path.Combine(Directory.GetCurrentDirectory(), "item_prices.json"),
                 })
            if (File.Exists(cand)) return File.OpenRead(cand);

        var asm = typeof(Inventory).Assembly;
        var res = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("item_prices.json", StringComparison.OrdinalIgnoreCase));
        return res is null ? null : asm.GetManifestResourceStream(res);
    }

    // Espelha os campos usados de item_prices.json (o resto e ignorado).
    private sealed class PriceRec
    {
        [JsonPropertyName("base")] public string? Base { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("market_name")] public string? MarketName { get; set; }
        [JsonPropertyName("price_usd")] public double? PriceUsd { get; set; }
    }
}

/// <summary>Uma linha do inventario: itemKey, nome (via item_prices.json), grade (0..9), quantidade e preco unitario USD (0=desconhecido).</summary>
public sealed record InvItem(int Key, string Name, int Grade, int Qty, double Unit = 0);
