using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TbhBot.Core.Market;

/// <summary>
/// Índice de preços para o OVERLAY (porta de load_index/resolve_base/price_of do tbh_overlay.py).
/// base normalizada -> {grade: preço USD}. Construído do item_prices.json EMBUTIDO (base/grade/price_usd
/// por item). Materiais (base com 1 grade) usam o preço único; gear (base com vários grades) casa pelo
/// grade do tooltip, com aproximação pro grade mais próximo. O casamento de nome é fuzzy (SequenceMatcher).
/// </summary>
public sealed class PriceIndex
{
    // palavra de grade -> índice (igual ao GW do Python).
    private static readonly Dictionary<string, int> GradeWord = new(StringComparer.OrdinalIgnoreCase)
    {
        ["common"] = 0, ["normal"] = 0, ["uncommon"] = 1, ["rare"] = 2, ["legendary"] = 3, ["immortal"] = 4,
        ["arcana"] = 5, ["beyond"] = 6, ["celestial"] = 7, ["divine"] = 8, ["cosmic"] = 9,
    };

    private readonly Dictionary<string, Dictionary<int, double>> _byg = new();
    private readonly List<string> _names = new();

    public int Count => _byg.Count;

    public PriceIndex()
    {
        try
        {
            using Stream? s = OpenEmbedded();
            if (s is null) return;
            var recs = JsonSerializer.Deserialize<Dictionary<string, Rec>>(s);
            if (recs is null) return;
            foreach (var rec in recs.Values)
            {
                if (rec is null || rec.PriceUsd is not > 0) continue;
                string b = Norm(rec.Base ?? rec.MarketName ?? rec.Name ?? "");
                if (b.Length == 0) continue;
                int gi = rec.Grade is >= 0 and <= 9 ? rec.Grade.Value : -1;
                if (!_byg.TryGetValue(b, out var g)) { g = new Dictionary<int, double>(); _byg[b] = g; }
                g[gi] = Math.Max(g.GetValueOrDefault(gi), rec.PriceUsd.Value);
            }
            _names.AddRange(_byg.Keys);
        }
        catch { /* sem índice: overlay não mostra preço, mas não quebra */ }
    }

    /// <summary>Normaliza p/ casar: minúsculas, só [a-z0-9 ], trim (igual ao norm do Python).</summary>
    public static string Norm(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9 ]", "").Trim();

    /// <summary>Dentre linhas candidatas, a que melhor casa com um item conhecido (exato ganha; senão fuzzy >=0.70).</summary>
    public string? ResolveBase(IEnumerable<string> texts)
    {
        string? best = null; double bestR = 0.0;
        foreach (var t in texts)
        {
            string nb = Norm(t);
            if (nb.Length == 0) continue;
            if (_byg.ContainsKey(nb)) return nb;                 // match exato ganha na hora
            foreach (var cand in _names)
            {
                double r = Ratio(nb, cand);
                if (r > bestR && r >= 0.70) { bestR = r; best = cand; }
            }
        }
        return best;
    }

    /// <summary>Preço da base p/ o grade do tooltip. Retorna (preço, "ok"|"aprox") ou null se desconhecido.</summary>
    public (double price, bool approx)? PriceOf(string? baseName, string? gradeWord)
    {
        if (baseName is null || !_byg.TryGetValue(baseName, out var gmap) || gmap.Count == 0) return null;
        int? gi = gradeWord is not null && GradeWord.TryGetValue(gradeWord, out int v) ? v : null;

        if (gmap.Count == 1) return (gmap.Values.First(), false);      // material -> preço único
        if (gi is int g && gmap.TryGetValue(g, out double p)) return (p, false);
        if (gi is int g2)                                              // grade não listado -> mais próximo
        {
            int nearest = gmap.Keys.OrderBy(k => Math.Abs(k - g2)).First();
            return (gmap[nearest], true);
        }
        return (gmap.Values.Max(), true);
    }

    // SequenceMatcher.ratio() do Python: 2*M/T, M = nº de chars casados (maior subsequência comum, guloso).
    // Aproximação fiel o bastante p/ o threshold 0.70 (mesmo espírito do difflib).
    private static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        int m = Lcs(a, b);
        return 2.0 * m / (a.Length + b.Length);
    }

    private static int Lcs(string a, string b)
    {
        int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static Stream? OpenEmbedded()
    {
        var asm = typeof(PriceIndex).Assembly;
        var res = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("item_prices.json", StringComparison.OrdinalIgnoreCase));
        return res is null ? null : asm.GetManifestResourceStream(res);
    }

    private sealed class Rec
    {
        [JsonPropertyName("base")] public string? Base { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("market_name")] public string? MarketName { get; set; }
        [JsonPropertyName("grade")] public int? Grade { get; set; }
        [JsonPropertyName("price_usd")] public double? PriceUsd { get; set; }
    }
}
