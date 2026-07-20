using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Editor de stats de jogador (Pstat) e de estagio (StageData).
/// Porta _stats_obj/read_stats/apply_stats e _stage_obj/read_stage/apply_stage do tbh_core.py.
/// Resolve os objetos por AOB -> desloc rel32 -> chain de ponteiros (sem depender de simbolos).
/// </summary>
public sealed class StatEditor(MemoryAccess mem, MemoryScanner scan)
{
    // cache dos slots de estagio (a AOB casa varias vezes; so um resolve p/ um StageData sadio)
    private List<nint>? _stageSlots;
    private nint _stageSlot;   // slot "bom" memorizado (revalidado a cada leitura)
    private nint _statsSlot;   // slot da AOB pstat, cacheado (evita re-scan do modulo a cada auto-read)

    // ---------------- PSTAT (stats do jogador) ----------------

    /// <summary>Resolve o objeto Pstat: AOB -> rel int32@+3 -> [slot+7+rel] -> PStatChain.</summary>
    private nint StatsObj()
    {
        if (_statsSlot == 0) _statsSlot = scan.FindAob(GameConstants.AobPstat);
        nint slot = _statsSlot;
        if (slot == 0) return 0;
        // desloc RIP-relativo lido como uint (Python usa u32; os deslocs observados sao positivos)
        uint rel = mem.ReadU32(slot + 3);
        nint p = (nint)mem.ReadU64(slot + 7 + (nint)rel);
        foreach (int off in GameConstants.PStatChain)
        {
            if (!MemoryAccess.IsValidPointer(p)) return 0;
            p = (nint)mem.ReadU64(p + off);
        }
        return MemoryAccess.IsValidPointer(p) ? p : 0;
    }

    /// <summary>Le todos os stats da tabela GameConstants.Stats ('f'=float / 'd'=double).</summary>
    public Dictionary<string, double> ReadStats()
    {
        var outd = new Dictionary<string, double>();
        nint p = StatsObj();
        if (p == 0) return outd;
        foreach (var (name, (off, typ)) in GameConstants.Stats)
            outd[name] = typ == 'd' ? mem.Read<double>(p + off) : mem.Read<float>(p + off);
        return outd;
    }

    /// <summary>Aplica os stats fornecidos (o merge speed/manual e feito pelo chamador).</summary>
    public void ApplyStats(IReadOnlyDictionary<string, double> stats)
    {
        if (stats.Count == 0) return;
        nint p = StatsObj();
        if (p == 0) return;
        foreach (var (name, val) in stats)
        {
            if (!GameConstants.Stats.TryGetValue(name, out var t)) continue;
            if (t.Type == 'd') mem.Write<double>(p + t.Off, val);
            else mem.Write<float>(p + t.Off, (float)val);
        }
    }

    // ---------------- STAGE (StageData corrente) ----------------

    /// <summary>[slot] -> StageChain -> StageData; 0 se a chain quebra.</summary>
    private nint StageDataOf(nint slot)
    {
        nint p = (nint)mem.ReadU64(slot);
        foreach (int off in GameConstants.StageChain)
        {
            if (!MemoryAccess.IsValidPointer(p)) return 0;
            p = (nint)mem.ReadU64(p + off);
        }
        return MemoryAccess.IsValidPointer(p) ? p : 0;
    }

    /// <summary>Sanidade: act/stage/level/wave/monsters em faixas plausiveis (evita casar lixo).</summary>
    private bool Sane(nint sd)
    {
        uint a = mem.ReadU32(sd + 0x48), s = mem.ReadU32(sd + 0x4C), l = mem.ReadU32(sd + 0x50),
             w = mem.ReadU32(sd + 0x54), mo = mem.ReadU32(sd + 0x58);
        return a is >= 1 and <= 12 && s is >= 1 and <= 40 && l is >= 1 and <= 300
               && w is >= 1 and <= 80 && mo is >= 1 and <= 400;
    }

    /// <summary>Acha um StageData sadio, memorizando o slot bom (revalidado a cada chamada).</summary>
    private nint StageObj()
    {
        if (_stageSlots is null)
        {
            _stageSlots = new List<nint>();
            foreach (nint m in scan.FindAllAob(GameConstants.AobStage))
            {
                uint rel = mem.ReadU32(m + 3);
                _stageSlots.Add(m + 7 + (nint)rel);
            }
        }
        if (_stageSlot != 0)
        {
            nint sd0 = StageDataOf(_stageSlot);
            if (sd0 != 0 && Sane(sd0)) return sd0;
            _stageSlot = 0;   // slot velho apodreceu -> re-scan abaixo
        }
        foreach (nint slot in _stageSlots)
        {
            nint sd = StageDataOf(slot);
            if (sd != 0 && Sane(sd)) { _stageSlot = slot; return sd; }
        }
        return 0;
    }

    /// <summary>Le os campos de estagio (int32) da tabela GameConstants.StageFields.</summary>
    public Dictionary<string, int> ReadStage()
    {
        var outd = new Dictionary<string, int>();
        nint sd = StageObj();
        if (sd == 0) return outd;
        foreach (var (k, o) in GameConstants.StageFields)
            outd[k] = mem.ReadI32(sd + o);
        return outd;
    }

    /// <summary>Escreve os campos de estagio (int32) fornecidos.</summary>
    public void ApplyStage(IReadOnlyDictionary<string, int> fields)
    {
        if (fields.Count == 0) return;
        nint sd = StageObj();
        if (sd == 0) return;
        foreach (var (k, val) in fields)
            if (GameConstants.StageFields.TryGetValue(k, out int off))
                mem.Write<int>(sd + off, val);
    }
}
