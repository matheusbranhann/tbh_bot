namespace TbhBot.Core.Il2Cpp;

/// <summary>
/// Tabela de simbolos/offsets resolvidos por build (o <c>self.sym</c> do Python).
/// Populada por <see cref="LoadKnownBuild"/> (build conhecida) ou pelo re-dump em runtime.
/// Todo valor e um long (RVA ou offset); a busca sempre tem default (igual a <c>sym.get(k, def)</c>).
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, long> _sym = new(StringComparer.Ordinal);
    private long[] _ynj = [];
    private string? _invClass;
    private string? _raClass;

    /// <summary>Equivale a <c>self.sym.get(key, def)</c>: retorna o valor ou o default (0) se ausente.</summary>
    public long Get(string key, long def = 0) => _sym.TryGetValue(key, out var v) ? v : def;

    public bool Has(string key) => _sym.ContainsKey(key);

    /// <summary>Leitura/escrita direta; ler chave ausente devolve 0 (nao lanca), como o dict do Python.</summary>
    public long this[string key]
    {
        get => _sym.TryGetValue(key, out var v) ? v : 0;
        set => _sym[key] = value;
    }

    /// <summary>Lista de RVAs auxiliares (ynj) — usada por outros modulos (ex.: cadeias de dispatch).</summary>
    public IReadOnlyList<long> Ynj => _ynj;

    /// <summary>Nome da classe concreta do inventario (inv_class), quando conhecido.</summary>
    public string? InvClass => _invClass;

    /// <summary>Nome (ofuscado) da classe do gerente de move de itens (ra_class) — usado no auto-stash.</summary>
    public string? RaClass => _raClass;

    /// <summary>
    /// Copia os offsets de <see cref="GameConstants.KnownBuilds"/>[hash12] para o dict interno.
    /// Gra->"gra", BauTi->"bau_ti" (so se != null), Ynj/InvClass em campos proprios e
    /// cada Extra.* com a chave snake_case igual ao Python. false se o hash for desconhecido.
    /// </summary>
    public bool LoadKnownBuild(string hash12)
    {
        if (!GameConstants.KnownBuilds.TryGetValue(hash12, out var b))
            return false;

        _sym["gra"] = b.Gra;
        // bau_ti pode ser null (resolve em runtime pelo inv_class) -> nao gravar,
        // assim Get("bau_ti") devolve 0/falsy igual a sym.get("bau_ti") == None no Python.
        if (b.BauTi is long bt)
            _sym["bau_ti"] = bt;

        _ynj = (long[])b.Ynj.Clone();
        _invClass = b.InvClass;

        foreach (var (k, v) in b.Extra)
            _sym[k] = v;

        return true;
    }

    /// <summary>
    /// Carrega um cache de offsets no formato do Python (<c>offsets_&lt;hash&gt;.json</c>): dict plano de
    /// string->numero, com <c>ynj</c> (lista) e <c>inv_class</c> (string). É a ponte enquanto a extração
    /// por dump em C# não é portada — reaproveita o que o engine Python já resolveu para o build atual.
    /// </summary>
    public bool LoadOffsetsJson(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var s = File.OpenRead(path); return LoadOffsetsJson(s); }
        catch { return false; }
    }

    /// <summary>Idem, a partir de um <see cref="Stream"/> (ex.: recurso embutido no assembly).</summary>
    public bool LoadOffsetsJson(Stream stream)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var ynj = new List<long>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Value.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Number:
                        if (p.Value.TryGetInt64(out var n)) _sym[p.Name] = n;
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        if (p.Name == "ynj")
                            foreach (var e in p.Value.EnumerateArray())
                                if (e.TryGetInt64(out var y)) ynj.Add(y);
                        break;
                    case System.Text.Json.JsonValueKind.String:
                        if (p.Name == "inv_class") _invClass = p.Value.GetString();
                        else if (p.Name == "ra_class") _raClass = p.Value.GetString();
                        break;
                }
            }
            if (ynj.Count > 0) _ynj = [.. ynj];
            return true;
        }
        catch { return false; }
    }
}
