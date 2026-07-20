using TbhBot.Core;

namespace TbhBot.Core.Automation;

/// <summary>
/// AutomationLoop: UM unico loop de automacao. Portado do racional do _auto_loop (tbh_core.py ~1922):
/// "UM SO loop numa thread e UM dispatcher" — as duas threads antigas disputavam o dispatcher e faziam
/// o auto-box passar fome (starvation). Aqui cada tick:
///   1) aplica protecao/cheats conforme as flags de intencao do engine (ACTk/Godmode/Hitkill);
///   2) SE alguma automacao de acao estiver ligada (Autobox/Autostash/Autofuse), executa via
///      engine.Dispatcher na ordem de PRIORIDADE box -> stash -> fuse.
///
/// IMPORTANTE: as ACOES reais (abrir caixa, mover pro bau, fundir no cubo) dependem da Fase 3 —
/// o IMainThreadDispatcher e implementado pelo orquestrador. Enquanto Dispatcher.IsReady==false,
/// apenas logamos "dispatcher nao pronto" e seguimos (nao trava, nao lança).
/// Tudo async e cancelavel (nada de Thread.Sleep; sempre await Task.Delay(.., ct)).
/// </summary>
public sealed class AutomationLoop(Engine engine)
{
    // Passo do loop. No Python era 0.12s quando fez algo / 0.5s ocioso; 250ms e um meio-termo
    // idiomatico e responsivo o bastante p/ abrir caixa "na hora que dropa".
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Log textual (equivalente ao self.log do Python).</summary>
    public event Action<string>? Log;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // So opera com o jogo vivo/conectado; senao apenas espera (o Watchdog reconecta).
                // WdHold = restart em andamento: NÃO aplica nada durante o boot (start limpo, evita bater
                // no honesty-check do ACTk / escrever com o jogo carregando). O WatchdogService libera depois.
                if (!engine.WdHold && engine.IsAttached && engine.Target.IsAlive())
                {
                    ApplyCheats();
                    RunActions();
                }
            }
            catch (Exception ex)
            {
                // Um tick nunca derruba o loop (no Python: except -> sleep(0.3)).
                Log?.Invoke($"auto: erro ({ex.Message}) — seguindo");
            }

            try
            {
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Aplica/retira protecao e cheats conforme as flags de intencao. Idempotente: setar toda
    /// iteracao e barato e faz self-heal (se o jogo reiniciou e o cheat sumiu, ele volta).
    /// </summary>
    private void ApplyCheats()
    {
        engine.Cheats.SetActk(engine.WantActk);
        engine.Cheats.SetGodmode(engine.WantGodmode);

        // Stats/stage forçados: re-aplica TODO tick (o jogo sobrescreve uma escrita única). Snapshot da ref.
        var st = engine.WantStats;
        if (st.Count > 0) { try { engine.Stats.ApplyStats(st); } catch { } }
        var sg = engine.WantStage;
        if (sg.Count > 0) { try { engine.Stats.ApplyStage(sg); } catch { } }
    }

    /// <summary>
    /// Executa as automacoes de acao na ordem de prioridade box -> stash -> fuse (racional do
    /// _auto_loop: caixa e prioridade maxima, checada TODO tick pra abrir assim que dropa; o fuse
    /// vem por ultimo). As acoes reais rodam pelo dispatcher (Fase 3).
    /// </summary>
    private void RunActions()
    {
        // Mesma ORDEM/GATING do _auto_loop do Python: caixa -> stash -> fuse rodam sempre; auto-boss,
        // evolução e a ORDENAÇÃO do baú só quando NADA mais aconteceu no tick (did==false) — senão o
        // box/stash matariam de fome os modos que bloqueiam. 'did' acumula ao longo do tick.
        bool did = false;

        // 1) CAIXA (prioridade máxima): acha as StageBox vivas + abre via dispatcher (llx main-thread).
        if (engine.WantAutobox && engine.AutoBox.OpenAll(() => engine.WantAutobox && engine.IsAttached))
            did = true;

        // 2) STASH em lote: move inventário -> baú (cmd2 = iw via dispatcher).
        if (engine.WantAutostash && engine.AutoStash.MoveAllToStash(() => engine.WantAutostash && engine.IsAttached) > 0)
            did = true;

        // 3) FUSE: uma síntese por tick (enche o cubo + funde 9 -> 1, level-safe). NÃO gateado por !did.
        if (engine.WantAutofuse && engine.AutoFuse.DoSynth(() => engine.WantAutofuse && engine.IsAttached))
            did = true;

        // 4) AUTO-BOSS: gasta 1 soulstone no x-10 e volta. Só quando ocioso (pode bloquear a luta inteira).
        if (engine.WantAutoboss && !did && engine.StageAutomation.AutoBoss(() => engine.WantAutoboss && engine.IsAttached))
            did = true;

        // 5) EVOLUÇÃO: mantém você sempre na fase mais nova liberada. Só quando ocioso.
        if (engine.WantEvolve && !did && engine.StageAutomation.Evolve(() => engine.WantEvolve && engine.IsAttached))
            did = true;

        // 6) ORDENAÇÃO do baú por grade (sob a flag do auto-stash): UM move por tick, só quando ocioso —
        //    amortizado pra não travar o auto-box (igual ao _sort_grade_step do Python).
        if (engine.WantAutostash && !did)
            engine.AutoStash.SortStep(2);
    }
}
