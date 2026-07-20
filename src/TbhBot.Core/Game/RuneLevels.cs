using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>Uma linha de RuneLevelInfoData: nível, valor do efeito nesse nível, custo (ouro) e o EAccountStatus (efeito).</summary>
public sealed record RuneLevelRow(int Level, int Value, int Cost, int Status);

/// <summary>
/// Lê RuneLevelInfoData ([RuneKey][Level] -> valor/custo/efeito), a tabela que dá as INFORMAÇÕES da runa
/// (o RuneInfoData não tem descrição — o efeito vem do EAccountStatus daqui). Mesmo padrão de scan por klass
/// do <see cref="RuneDefs"/>. Offsets do dump: RuneKey@0x30, Level@0x34, Value@0x38, Cost@0x3C, Status@0x40.
/// 100% estático -> cacheado.
/// </summary>
public sealed class RuneLevels(MemoryAccess mem, Il2CppApi api, MemoryScanner scan)
{
    private const int ORuneKey = 0x30, OLevel = 0x34, OValue = 0x38, OCost = 0x3C, OStatus = 0x40;
    private const uint PageReadWrite = 0x04, PageExecuteReadWrite = 0x40;

    private Dictionary<int, List<RuneLevelRow>>? _cache;

    /// <summary>{RuneKey: [linhas por nível, ordenadas]}. Vazio se não resolveu a klass.</summary>
    public Dictionary<int, List<RuneLevelRow>> Read(bool force = false)
    {
        if (!force && _cache is { Count: > 0 }) return _cache;

        var outd = new Dictionary<int, List<RuneLevelRow>>();
        long klass = api.ClassFromName("TaskbarHero.Data", "RuneLevelInfoData");
        if (klass == 0) return outd;
        ulong klassPtr = (ulong)klass;

        foreach (var (regionBase, size) in scan.Regions(PageReadWrite, PageExecuteReadWrite))
        {
            byte[] d = mem.ReadBytes(regionBase, size);
            if (d.Length < 8) continue;
            for (int j = 0; j + 8 <= d.Length; j += 8)
            {
                if (BitConverter.ToUInt64(d, j) != klassPtr) continue;
                nint a = regionBase + j;
                int key = mem.ReadI32(a + ORuneKey);
                int lvl = mem.ReadI32(a + OLevel);
                if (key <= 0 || key >= 20_000_000 || lvl < 0 || lvl > 1000) continue;   // sanidade
                var row = new RuneLevelRow(lvl, mem.ReadI32(a + OValue), mem.ReadI32(a + OCost), mem.ReadI32(a + OStatus));
                if (!outd.TryGetValue(key, out var list)) { list = new List<RuneLevelRow>(); outd[key] = list; }
                list.Add(row);
            }
        }
        foreach (var list in outd.Values) list.Sort((x, y) => x.Level.CompareTo(y.Level));
        if (outd.Count > 0) _cache = outd;
        return outd;
    }

    // EAccountStatus (StatusSystem) — 42 valores; o índice É o efeito da runa. (dump TypeDefIndex 3068)
    private static readonly string[] StatusNames =
    {
        "IncreaseGoldAmount", "AdditionalGold", "IncreaseExpAmount", "AdditionalExp",
        "DropChanceNormalChest", "DropChanceStageBossChest", "WaveCountReduction", "WaveMonsterAmount",
        "MaxAmountNormalChest", "MaxAmountStageBossChest", "MaxAmountActBossChest", "CubeExpPercent",
        "CubeAlchemyGoldPercent", "AllHeroMoveSpeed", "AllHeroAttackSpeed", "AllHeroAttackDamage",
        "AllHeroAttackDamagePercent", "AllHeroArmor", "AllHeroArmorPercent", "AdditionalGoldStageBoss",
        "AdditionalGoldActBoss", "AdditionalGoldNormalMonster", "AdditionalExpStageBoss", "AdditionalExpActBoss",
        "AdditionalExpNormalMonster", "MaxInventorySlot", "UnlockStashPageCount", "UnlockArrangeSlotCount",
        "UnlockSkillSlotCount", "DropChanceNormalChestPercent", "DropChanceStageBossChestPercent",
        "UnlockAutoOpenNormalChest", "ReduceAutoOpenNormalChestTime", "UnlockAutoOpenStageBossChest",
        "ReduceAutoOpenStageBossChestTime", "UnlockAutoOpenActBossChest", "ReduceAutoOpenActBossChestTime",
        "OpenOneTypeChestAllAtOnce", "OpenAllTypeChestAllAtOnce", "UnlockOfflineReward",
        "OfflineRewardGoldPercent", "OfflineRewardExpPercent",
    };

    /// <summary>Nome do efeito (EAccountStatus) já "espaçado" p/ leitura, ex.: "All Hero Attack Damage Percent".</summary>
    public static string EffectName(int status)
    {
        string raw = status is >= 0 && status < StatusNames.Length ? StatusNames[status] : $"Status {status}";
        var sb = new System.Text.StringBuilder(raw.Length + 8);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && char.IsUpper(raw[i]) && !char.IsUpper(raw[i - 1])) sb.Append(' ');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>Efeito é percentual? (nome termina em "Percent" — muda o formato do valor no painel).</summary>
    public static bool IsPercent(int status) =>
        status is >= 0 && status < StatusNames.Length && StatusNames[status].EndsWith("Percent", StringComparison.Ordinal);
}
