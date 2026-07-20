namespace TbhBot.Core.Il2Cpp;

// Offsets CONSTANTES (estaveis entre updates de codigo). Transcrito FIEL do tbh_core.py (linhas 202-237).
// Erro aqui quebra tudo -> cada hex confere contra o Python.
public static class GameConstants
{
    // [slot] -> [base+rva] -> +StaticFieldsOff
    public const int StaticFieldsOff = 0xB8;

    // [slot] -> ... -> objeto de stats -> +campo
    public static readonly int[] PStatChain = [0xB8, 0x40, 0x10, 0x20, 0x18];

    // nome:(offset,tipo) tipo 'f'=float 'd'=double
    public static readonly Dictionary<string, (int Off, char Type)> Stats = new()
    {
        ["Attack Damage"] = (0x3C, 'f'),
        ["Attack Speed"] = (0x4C, 'f'),
        ["Critical Chance"] = (0x5C, 'f'),
        ["Critical Damage"] = (0x6C, 'f'),
        ["Cooldown Reduction"] = (0xCC, 'f'),
        ["Cast Speed"] = (0x338, 'd'),
        ["Physical Damage"] = (0x1AC, 'f'),
        ["Fire Damage"] = (0x1BC, 'f'),
        ["Cold Damage"] = (0x1CC, 'f'),
        ["Lightning Damage"] = (0x1DC, 'f'),
        ["Chaos Damage"] = (0x1EC, 'f'),
        ["Max Hp"] = (0x7C, 'f'),
        ["Armor"] = (0x8C, 'f'),
        ["Dodge Chance"] = (0x12C, 'f'),
        ["Block Chance"] = (0x13C, 'f'),
        ["All Element Resistance"] = (0x36C, 'f'),
        ["Hp Regen /Sec"] = (0x198, 'd'),
        ["Dmg Absorption"] = (0x2AC, 'f'),
        ["Dmg Reduction"] = (0x24C, 'f'),
        ["Movement Speed"] = (0x9C, 'f'),
        ["Area of Effect %"] = (0xA8, 'd'),
        ["Area of Effect Damage"] = (0x398, 'd'),
        ["Add HP/Kill"] = (0x3CC, 'f'),
        ["Life Leech"] = (0x17C, 'f'),
        ["Skill Heal"] = (0x348, 'd'),
    };

    // [slot] -> StageInfoData
    public static readonly int[] StageChain = [0xB8, 0x88, 0x10];

    public static readonly Dictionary<string, int> StageFields = new()
    {
        ["Act"] = 0x48,
        ["StageNo"] = 0x4C,
        ["StageLevel"] = 0x50,
        ["WaveAmount"] = 0x54,
        ["WaveMonsterAmount"] = 0x58,
        ["MonsterDropItemKey"] = 0x68,
        ["FirstClearDropKey"] = 0x6C,
        ["MonsterDropItemRate"] = 0x70,
        ["BossDropItemRate"] = 0x74,
        ["BossDropItemKey"] = 0x78,
        ["BossMonsterKey"] = 0x7C,
        ["BossDamageMultiplier"] = 0x80,
        ["BossGoldMultiplier"] = 0x84,
        ["BossExpMultiplier"] = 0x88,
        ["BossHpMultiplier"] = 0x8C,
        ["BossScale"] = 0x90,
        ["SoulStoneItemKey"] = 0x94,
        ["SoulStoneAmount"] = 0x98,
    };

    // bau->PlayerSaveData->List<ItemSaveData>
    public const int InvPsdOff = 0x28;
    public const int InvListOff = 0xA8;

    // PlayerSaveData.RuneSaveData (List<RuneSaveData{RuneKey@0x10,Level@0x14}>)
    public const int RuneListOff = 0x80;

    public const string AobGodmode = "57 48 83 EC 50 80 3D ?? ?? ?? ?? ?? 41 0F ?? ?? 48 8B DA";
    public const string AobPstat = "48 8B 05 ?? ?? ?? ?? 83 B8 E4 00 00 00 00 75 ?? 48 8B C8 E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00 48 8B 48 20 48 85 C9 74 ?? 48 8B 15 ?? ?? ?? ?? E8";
    public const string AobStage = "48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00 48 8B 88 88 00 00 00";

    // prologo de Monster.gra (hitkill)
    public static readonly byte[] GraOrig = [0x48, 0x89, 0x5C, 0x24, 0x08];

    // RVAs conhecidos por build; chave = md5-12 dos 1os 2MB do GameAssembly.dll.
    // bau_ti None -> resolve em runtime pelo inv_class.
    public static readonly Dictionary<string, BuildOffsets> KnownBuilds = new()
    {
        ["8d3768c21857"] = new BuildOffsets
        {
            Gra = 0xC29EC0,
            BauTi = 0x5E3F908,
            Ynj = [0x7062F0, 0x184C540],
            InvClass = "bau",
            Extra = new Dictionary<string, long>
            {
                ["inv_psd_off"] = 0x28,
                ["inv_list_off"] = 0xA8,
            },
        },
        ["2c430296063a"] = new BuildOffsets
        {
            Gra = 0xC1F730,
            BauTi = null,
            Ynj = [0x6F65F0],
            InvClass = "bao",
            Extra = new Dictionary<string, long>
            {
                ["inv_klass_ti"] = 0x5DFED88,
                ["inv_psd_off"] = 0x28,
                ["inv_list_off"] = 0xB0,
                ["upd"] = 0x9ACE70,
                ["llx"] = 0xA34D90,
                ["iw"] = 0x88BD30,
                ["ilo"] = 0x8B5880,
                ["ipu"] = 0x8C5B90,
                ["imx"] = 0x8BA060,
                ["inf"] = 0x8BA970,
                ["ili"] = 0x8B4F50,
                ["iog"] = 0x8BFBC0,
                ["ioa"] = 0x8BE8B0,
                ["ima"] = 0x8B6DB0,
                ["llm"] = 0xA341C0,
                ["iuw"] = 0x901690,
                ["izb"] = 0x915830,
                ["inv_slots_off"] = 0x88,
                ["stash_off"] = 0x90,
                ["cube_slot"] = 0x5DD2A30,
            },
        },
    };
}
