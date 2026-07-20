using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TbhBot.Core.Market;

/// <summary>
/// Busca preco de itens do Steam Community Market (endpoint priceoverview) para o
/// TaskbarHero (appid 3678970). Cache em memoria + arquivo JSON ao lado do exe.
/// Robusto a erro de rede: qualquer falha retorna null (nunca lanca para o chamador).
/// Portado do racional de market_db.py (que usa search/render em lote); aqui a busca
/// e sob demanda por item, com o mesmo cuidado de nunca destruir cache bom.
/// </summary>
public sealed class MarketDb
{
    public const string AppId = "3678970";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36";

    // Um unico HttpClient estatico reaproveitado (evita esgotar sockets).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;
    private readonly object _fileLock = new();

    // Preco cacheado por no maximo este tempo antes de re-buscar na Steam.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public MarketDb(string? cachePath = null)
    {
        _cachePath = cachePath ??
            System.IO.Path.Combine(AppContext.BaseDirectory, "market_prices.json");
        _cache = LoadFromDisk(_cachePath);
    }

    /// <summary>Numero de itens no cache (memoria).</summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Retorna o menor preco (USD) do item, ou null se nao encontrado / erro de rede.
    /// Usa cache (memoria+disco) dentro do TTL; caso contrario consulta a Steam.
    /// </summary>
    public async Task<decimal?> GetPriceAsync(string itemName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var key = itemName.Trim();

        if (_cache.TryGetValue(key, out var hit) &&
            DateTime.UtcNow - hit.FetchedUtc < CacheTtl)
            return hit.PriceUsd;

        decimal? price = await FetchFromSteamAsync(key, ct).ConfigureAwait(false);

        // BLINDAGEM: so grava/atualiza o cache quando veio preco de verdade.
        // Nunca sobrescreve um preco bom por null (rate-limit / rede).
        if (price is not null)
        {
            _cache[key] = new CacheEntry
            {
                Name = key,
                PriceUsd = price,
                FetchedUtc = DateTime.UtcNow
            };
            SaveToDisk();
            return price;
        }

        // Sem preco novo: devolve o que houver no cache (mesmo vencido), senao null.
        return hit?.PriceUsd;
    }

    private static async Task<decimal?> FetchFromSteamAsync(string itemName, CancellationToken ct)
    {
        try
        {
            var url = "https://steamcommunity.com/market/priceoverview/?" +
                      "appid=" + AppId +
                      "&currency=1" + // 1 = USD
                      "&market_hash_name=" + Uri.EscapeDataString(itemName);

            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null; // 429 (rate-limit) ou outro: sem preco

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var po = JsonSerializer.Deserialize<PriceOverview>(body);
            if (po is null || !po.Success) return null;

            // Prefere lowest_price; cai para median_price se faltar.
            return ParseMoney(po.LowestPrice) ?? ParseMoney(po.MedianPrice);
        }
        catch
        {
            // Qualquer erro (rede, timeout, cancelamento, JSON): trata como "sem preco".
            return null;
        }
    }

    /// <summary>Converte "$1,234.56" / "0,03€" etc. em decimal. Null se nao der.</summary>
    internal static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Mantem so digitos e separadores; remove simbolo de moeda e espacos.
        var chars = text.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray();
        if (chars.Length == 0) return null;
        var s = new string(chars);

        // Normaliza para o formato invariante (ponto = decimal).
        int lastDot = s.LastIndexOf('.');
        int lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            // O separador que aparece por ultimo e o decimal; o outro e de milhar.
            if (lastComma > lastDot)
                s = s.Replace(".", "").Replace(',', '.'); // 1.234,56 -> 1234.56
            else
                s = s.Replace(",", "");                    // 1,234.56 -> 1234.56
        }
        else if (lastComma >= 0)
        {
            // So virgula: se parece milhar (3 digitos depois) remove, senao vira decimal.
            var after = s.Length - lastComma - 1;
            s = after == 3 ? s.Replace(",", "") : s.Replace(',', '.');
        }
        // so ponto (ou nenhum): ja esta ok.

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private void SaveToDisk()
    {
        try
        {
            var doc = new CacheFile
            {
                Updated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                AppId = AppId,
                Count = _cache.Count,
                Items = _cache.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            lock (_fileLock)
            {
                // Escrita atomica: temp + replace (nunca deixa o arquivo pela metade).
                var tmp = _cachePath + ".tmp";
                System.IO.File.WriteAllText(tmp, json);
                if (System.IO.File.Exists(_cachePath))
                    System.IO.File.Replace(tmp, _cachePath, null);
                else
                    System.IO.File.Move(tmp, _cachePath);
            }
        }
        catch
        {
            // Cache em disco e best-effort; falha ao gravar nao pode quebrar a busca.
        }
    }

    private static ConcurrentDictionary<string, CacheEntry> LoadFromDisk(string path)
    {
        var dict = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
        try
        {
            if (!System.IO.File.Exists(path)) return dict;
            var json = System.IO.File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<CacheFile>(json);
            if (doc?.Items is null) return dict;
            foreach (var kv in doc.Items)
                if (kv.Value is not null)
                    dict[kv.Key] = kv.Value;
        }
        catch
        {
            // Cache corrompido/ausente: comeca vazio.
        }
        return dict;
    }

    // ---- modelos de (de)serializacao ----

    private sealed class PriceOverview
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("lowest_price")] public string? LowestPrice { get; set; }
        [JsonPropertyName("median_price")] public string? MedianPrice { get; set; }
        [JsonPropertyName("volume")] public string? Volume { get; set; }
    }

    public sealed class CacheEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("price_usd")] public decimal? PriceUsd { get; set; }
        [JsonPropertyName("fetched_utc")] public DateTime FetchedUtc { get; set; }
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("updated")] public string? Updated { get; set; }
        [JsonPropertyName("appid")] public string? AppId { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("items")] public Dictionary<string, CacheEntry>? Items { get; set; }
    }
}
