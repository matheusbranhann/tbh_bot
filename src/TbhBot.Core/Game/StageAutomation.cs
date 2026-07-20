using System.Diagnostics;
using TbhBot.Core.Il2Cpp;

namespace TbhBot.Core.Game;

/// <summary>
/// Modos EVOLUÇÃO e AUTO-BOSS (porta de _do_evolve / _do_autoboss / _boss_run / _wait_boss_done do
/// tbh_core.py). Compõe o transporte do <see cref="StageNav"/> com o progresso (<see cref="SaveData.StageProgress"/>),
/// a contagem de caixas (<see cref="AutoBox.IuwCount"/>) e o inventário (soulstones). Roda na thread do
/// AutomationLoop — pode BLOQUEAR durante a luta do boss (até 10 min), como no Python; as esperas checam
/// <c>keep()</c> pra abortar na hora se o usuário desligar o modo / o jogo fechar.
/// </summary>
public sealed class StageAutomation(StageNav nav, SaveData save, AutoBox box, Inventory inv, SymbolTable sym)
{
    public Action<string>? Log;
    private void Emit(string m) => Log?.Invoke(m);

    /// <summary>Chamado pela evolução quando chega no topo (Torment 3-9) p/ DESLIGAR o modo (ligado no Engine).</summary>
    public Action? DisableEvolve;

    private const int EvolveTarget = 4309;                 // TORMENT 3-9 (o 3-10 fica pro Auto-boss)
    private int _lastNav;                                   // último estágio p/ onde naveguei (anti-double-step)
    // (soulstone, boss x-10): Torment primeiro, depois Hell — igual ao Python.
    private static readonly (int ss, int boss)[] BossPairs = [(190004, 4310), (190003, 3310)];
    private static readonly string[] EnterResult = ["Success", "EndStage", "NeedSoulStone", "NeedChestSpace", "Failed"];
    private static readonly string[] Diffs = ["NORMAL", "NIGHTMARE", "HELL", "TORMENT"];

    private static string StageName(int k)
    {
        int di = k / 1000 - 1;
        return di is >= 0 and < 4 ? $"{Diffs[di]} {(k % 1000) / 100}-{k % 100}" : k.ToString();
    }
    private static string Enter(int? r) => r is >= 0 and <= 4 ? EnterResult[r.Value] : $"r={r}";

    /// <summary>
    /// EVOLUÇÃO: SOBE UMA FASE POR VEZ pela corrente NextStageKey, no ritmo em que você LIMPA a fase atual
    /// (wave >= WaveAmount), até Torment 3-9 — aí DESLIGA o modo sozinho. NÃO pula pro fim: o alvo é
    /// Next(cur), não min(max,4309) (que teleportava porque a aba Stages fixa max=4310). x-10 no caminho =
    /// mata o boss e vai PRA FRENTE (Next do boss), nunca volta (senão re-entra o boss pra sempre).
    /// </summary>
    public bool Evolve(Func<bool> keep)
    {
        var (mx, cur, wave) = save.StageProgress();
        if (mx <= 0 || cur <= 0) return false;

        // Chegou (ou passou) em Torment 3-9: climb completo -> desliga o modo.
        if (cur >= EvolveTarget)
        {
            Emit("📈 evolução: cheguei em TORMENT 3-9 — climb completo, desligando o modo");
            DisableEvolve?.Invoke();
            return false;
        }

        var t = nav.StageTable();
        if (!t.TryGetValue(cur, out var info)) return false;

        // PACING: só avança quando a fase ATUAL foi limpa (última wave). Sem isso viraria um "pulo lento".
        // Anti-double-step: só considera limpo depois de ter navegado pra cá e a wave ter passado de 1.
        if (!(info.Waves > 0 && wave >= info.Waves && (cur == _lastNav || _lastNav == 0)))
            return false;

        int next = info.Next;
        if (next <= 0 || next > EvolveTarget || !t.TryGetValue(next, out var nInfo)) return false;

        if (nInfo.Type == 1)                                // próximo é x-10: cruza matando o boss
        {
            int? r = nav.CanEnter(next);
            if (r != 0)
            {
                Emit(r == 2
                    ? $"📈 evolução: {StageName(next)} precisa da soulstone {nInfo.Ss} — farmando até cair"
                    : $"📈 evolução: {StageName(next)} -> {Enter(r)}");
                return false;
            }
            Emit($"📈 evolução: {StageName(next)} liberado — matando o boss pra abrir o próximo ato");
            if (!EnterBossWait(next, keep)) return false;
            // Depois do kill vai PRA FRENTE (Next do boss), nunca de volta -> escapa do loop do boss.
            int fwd = t.TryGetValue(next, out var bi) ? bi.Next : 0;
            if (fwd > 0 && fwd <= EvolveTarget) { nav.GoToStage(fwd); _lastNav = fwd; }
            return true;
        }

        Emit($"📈 evolução: {StageName(cur)} -> {StageName(next)} (nível {nInfo.Lvl})");
        nav.GoToStage(next);
        _lastNav = next;
        if (next >= EvolveTarget)
        {
            Emit("📈 evolução: cheguei em TORMENT 3-9 — climb completo, desligando o modo");
            DisableEvolve?.Invoke();
        }
        return true;
    }

    /// <summary>Entra num x-10 e espera o boss morrer (sem voltar pro ponto de partida — a evolução segue em frente).</summary>
    private bool EnterBossWait(int boss, Func<bool> keep)
    {
        if (!nav.EnterBoss(boss)) { Emit($"boss: falhou ao entrar em {StageName(boss)}"); return false; }
        bool? ok = WaitBossDone(boss, 600_000, keep);
        Emit($"boss {StageName(boss)}: " + (ok == true ? "✅ morto" : ok is null ? "entrada não pegou" : "party morreu"));
        return ok == true;
    }

    /// <summary>
    /// AUTO-BOSS: se tem soulstone, entra no x-10 daquela dificuldade e DEIXA O JOGO VOLTAR sozinho.
    /// Não liga/desliga cheat — a pedra só é COBRADA quando o boss MORRE, então entrar é reversível.
    /// A volta é nativa (o jgd grava o ponto de retorno beyq). Um boss por chamada.
    /// </summary>
    public bool AutoBoss(Func<bool> keep)
    {
        if (sym.Get("jgd") == 0) { Emit("auto-boss: offsets de estágio ausentes"); return false; }
        var counts = inv.ReadCounts();
        foreach (var (ss, boss) in BossPairs)
        {
            if (counts.GetValueOrDefault(ss) <= 0) continue;
            int? r = nav.CanEnter(boss);                    // gate do jogo (pedra + espaço de baú)
            if (r != 0) { Emit($"auto-boss: {StageName(boss)} -> {Enter(r)}"); continue; }

            Emit($"🗡 auto-boss: entrando em {StageName(boss)} (soulstone {ss}: {counts[ss]})");
            return BossRun(boss, keep);
        }
        return false;
    }

    /// <summary>Entra num x-10, espera o desfecho e deixa o jogo voltar; devolve pro ponto de partida se travar.</summary>
    private bool BossRun(int boss, Func<bool> keep)
    {
        int volta = save.StageProgress().Cur;               // de onde vim = pra onde o jogo me devolve
        if (!nav.EnterBoss(boss)) { Emit($"boss: falhou ao entrar em {StageName(boss)}"); return false; }

        bool? ok = WaitBossDone(boss, 600_000, keep);
        Emit($"boss {StageName(boss)}: " + (ok == true ? "✅ morto (caixa dropou)"
            : ok is null ? "entrada não pegou" : "saiu sem matar (party morreu)"));

        if (ok == true)                                     // a volta é do jogo (beyq); só intervenho se travar
        {
            var sw = Stopwatch.StartNew();
            while (save.StageProgress().Cur == boss && sw.ElapsedMilliseconds < 15_000) Thread.Sleep(500);
            if (save.StageProgress().Cur == boss && volta > 0)
            {
                Emit($"auto-boss: o jogo travou no boss — devolvendo pro {StageName(volta)}");
                nav.GoToStage(volta);
            }
        }
        return ok == true;
    }

    /// <summary>
    /// Espera o desfecho: True (boss morto = caixa ACTBOSS apareceu), False (saiu sem caixa = party morreu),
    /// null (a entrada nem pegou). Duas fases; a 1ª (confirmar que a entrada pegou) é obrigatória senão
    /// "fase != boss" é verdade no instante 0 e dá falso-positivo imediato.
    /// </summary>
    private bool? WaitBossDone(int boss, int timeoutMs, Func<bool> keep)
    {
        int Cur() => save.StageProgress().Cur;

        var sw = Stopwatch.StartNew();
        while (Cur() != boss)                               // 1) a entrada pegou?
        {
            if (sw.ElapsedMilliseconds > 8_000) { Emit($"auto-boss: a entrada não pegou (fase={Cur()})"); return null; }
            if (!keep()) return null;
            Thread.Sleep(300);
        }

        int b0 = box.IuwCount(2) ?? 0;                      // caixas ACTBOSS antes da luta
        sw.Restart();
        while (sw.ElapsedMilliseconds < timeoutMs)          // 2) desfecho
        {
            Thread.Sleep(1000);
            if (!keep()) return false;
            int? b = box.IuwCount(2);
            if (b is int bv && bv > b0) return true;         // caixa do boss caiu = MATOU
            if (Cur() != boss)                               // saiu sem caixa = party morreu
            {
                Thread.Sleep(1000);
                b = box.IuwCount(2);
                return b is int bv2 && bv2 > b0;
            }
        }
        return false;
    }
}
