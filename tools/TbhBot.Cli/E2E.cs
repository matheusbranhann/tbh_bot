using System.Diagnostics;
using TbhBot.Core;
using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Market;

namespace TbhBot.Cli;

/// <summary>
/// Teste ponta-a-ponta AO VIVO de todas as funcionalidades do engine (o que as abas do painel usam).
/// Seguro: reverte todo cheat aplicado e grava o MESMO valor nos writes que disparariam o anti-cheat
/// (cubo/stage), deixando o jogo no estado original.
/// </summary>
internal static class E2E
{
    private static int _pass, _fail;

    public static async Task RunAsync(Engine e, string cacheDir)
    {
        Console.WriteLine("== E2E — teste ao vivo de TODAS as funcionalidades ==\n");

        if (!e.Attach()) { Console.WriteLine("[x] não attachou — o jogo está aberto?"); return; }
        var hash = BuildInfo.DllHash(e.Target.ModulePath);
        var path = Path.Combine(cacheDir, $"offsets_{hash}.json");
        bool loaded = e.Symbols.LoadOffsetsJson(path);
        Console.WriteLine($"attach pid={e.Target.ProcessId}  hash={hash}  offsets={(loaded ? "carregados" : "NÃO ("+path+")")}\n");

        var m = e.Memory;
        nint b = e.Target.ModuleBase;

        // ===================== LEITURAS =====================
        Section("LEITURAS (Fases 1-3)");
        Ok("attach", e.IsAttached);
        Ok("offsets (gra/uo_ti/cube_slot?)", e.Symbols.Has("gra") && e.Symbols.Has("uo_ti"),
           $"gra={e.Symbols.Has("gra")} uo_ti={e.Symbols.Has("uo_ti")} cube_slot={e.Symbols.Has("cube_slot")}");
        Bench(e);
        var stats = e.Stats.ReadStats();
        Ok("ReadStats", stats.Count == 25, $"{stats.Count}/25 · AD={stats.GetValueOrDefault("Attack Damage"):g4}");
        var stage = e.Stats.ReadStage();
        Ok("ReadStage", stage.Count == 18, $"{stage.Count}/18 · WaveMonsterAmount={stage.GetValueOrDefault("WaveMonsterAmount")}");
        var (mx, cur, wv) = e.Save.StageProgress();
        Ok("StageProgress", mx is >= 1101 and <= 4310, $"max={mx} cur={cur} wave={wv}");
        var cube = e.Save.CubeLevel();
        Ok("CubeLevel", cube is >= 0 and <= 200, $"Lv.{cube?.ToString() ?? "—"}");
        var runes = e.Save.ReadRunes();
        Ok("ReadRunes", runes.Count > 0, $"{runes.Count} runas");
        int inv = e.Save.InventoryCount();
        Ok("InventoryCount", inv >= 0, $"{inv} itens");
        Ok("ResolvePsd", e.Resolver.ResolvePsd() != 0);

        // ===================== CHEATS (aplica → verifica byte → reverte) =====================
        Section("CHEATS (aplica → verifica → reverte)");
        {
            long ynj = e.Symbols.Ynj.Count > 0 ? e.Symbols.Ynj[0] : 0;
            byte orig = ynj != 0 ? First(m.ReadBytes(b + (nint)ynj, 1)) : (byte)0;
            e.Cheats.SetActk(true);
            bool on = ynj != 0 && First(m.ReadBytes(b + (nint)ynj, 1)) == 0xC3;
            e.Cheats.SetActk(false);
            bool off = ynj != 0 && First(m.ReadBytes(b + (nint)ynj, 1)) == orig;
            Ok("ACTk (NOP ynj + revert)", ynj != 0 && on && off, $"on={on} revert={off}");
        }
        {
            nint a = e.Scanner.FindAob(GameConstants.AobGodmode);
            e.Cheats.SetGodmode(true);
            bool on = a != 0 && First(m.ReadBytes(a, 1)) == 0xC3;
            e.Cheats.SetGodmode(false);
            bool off = a != 0 && First(m.ReadBytes(a, 1)) == 0x57;
            Ok("Godmode (AOB patch + revert)", a != 0 && on && off, $"@0x{a:X} on={on} revert={off}");
        }

        // ===================== ESCRITAS (não-destrutivas) =====================
        Section("ESCRITAS (não-destrutivas: altera+restaura, ou grava o MESMO valor)");
        if (stats.Count > 0)
        {
            string k = stats.ContainsKey("Movement Speed") ? "Movement Speed" : stats.Keys.First();
            double o = stats[k];
            e.Stats.ApplyStats(new Dictionary<string, double> { [k] = o + 1 });
            double after = e.Stats.ReadStats().GetValueOrDefault(k);
            e.Stats.ApplyStats(new Dictionary<string, double> { [k] = o });   // restaura
            Ok("ApplyStats (altera+restaura)", Math.Abs(after - (o + 1)) < 0.5, $"{k}: {o:g4}→{after:g4}→{o:g4}");
        }
        if (stage.Count > 0)
        {
            string k = stage.ContainsKey("WaveAmount") ? "WaveAmount" : stage.Keys.First();
            int o = stage[k];
            e.Stats.ApplyStage(new Dictionary<string, int> { [k] = o + 1 });
            int after = e.Stats.ReadStage().GetValueOrDefault(k);
            e.Stats.ApplyStage(new Dictionary<string, int> { [k] = o });
            Ok("ApplyStage (altera+restaura)", after == o + 1, $"{k}: {o}→{after}→{o}");
        }
        if (runes.Count > 0)
        {
            var kv = runes.First();
            Ok("SetRune (write path, mesmo nível)", e.Save.SetRune(kv.Key, kv.Value), $"rune {kv.Key} Lv.{kv.Value}");
        }
        if (cube is int cl)
        {
            var (ok, _) = e.Save.SetCubeLevel(cl);   // MESMO valor -> sem force-close
            Ok("SetCubeLevel (write path, sem force-close)", ok, $"reescreveu Lv.{cl}");
        }
        if (mx >= 1101)
        {
            var (ok, _) = e.Save.SetMaxStage(mx);    // MESMO valor -> sem force-close
            Ok("SetMaxStage (write path, sem force-close)", ok, $"reescreveu {mx}");
        }

        // ===================== MARKET =====================
        Section("MARKET (Fase 6)");
        try
        {
            var md = new MarketDb();
            var price = await md.GetPriceAsync("Soul Stone");
            Ok("MarketDb.GetPriceAsync (rede)", true, price is null ? "rede OK, item sem preço/nome exato" : $"US$ {price}");
        }
        catch (Exception ex) { Ok("MarketDb.GetPriceAsync", false, ex.Message); }

        // ===================== CONCORRÊNCIA (loop aplica Want*) =====================
        Section("CONCORRÊNCIA (Fase 4 — AutomationLoop aplica as flags Want*)");
        {
            var loop = new TbhBot.Core.Automation.AutomationLoop(e);
            var cts = new CancellationTokenSource();
            nint a = e.Scanner.FindAob(GameConstants.AobGodmode);
            e.WantGodmode = true;
            var t = loop.RunAsync(cts.Token);
            await Task.Delay(700);
            bool applied = a != 0 && First(m.ReadBytes(a, 1)) == 0xC3;
            e.WantGodmode = false;
            await Task.Delay(700);
            bool reverted = a != 0 && First(m.ReadBytes(a, 1)) == 0x57;
            cts.Cancel();
            try { await t; } catch { /* cancel */ }
            Ok("AutomationLoop aplica/reverte via Want*", applied && reverted, $"aplicou={applied} reverteu={reverted}");
        }

        Console.WriteLine($"\n===== RESUMO: {_pass} PASS · {_fail} FAIL =====");
    }

    private static byte First(byte[] a) => a.Length > 0 ? a[0] : (byte)0;
    private static void Section(string s) => Console.WriteLine($"\n-- {s} --");

    private static void Ok(string name, bool cond, string detail = "")
    {
        if (cond) { _pass++; Console.WriteLine($"  [PASS] {name}{(detail.Length > 0 ? "  ·  " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  ·  " + detail : "")}"); }
    }

    private static void Bench(Engine e)
    {
        const int N = 8192;
        nint r = e.Target.ModuleBase;
        _ = e.Memory.ReadArray<ulong>(r, N);
        var sw = Stopwatch.StartNew();
        ulong a = 0; for (int i = 0; i < N; i++) a += e.Memory.ReadU64(r + i * 8);
        sw.Stop(); double seq = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ulong bb = 0; foreach (var v in e.Memory.ReadArray<ulong>(r, N)) bb += v;
        sw.Stop(); double bat = sw.Elapsed.TotalMilliseconds;
        Ok("batch read >5x", seq / Math.Max(bat, 0.0001) > 5 && a == bb, $"{seq / Math.Max(bat, 0.0001):F0}x");
    }
}
