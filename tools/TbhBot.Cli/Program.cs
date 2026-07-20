using System.Diagnostics;
using TbhBot.Core;
using TbhBot.Core.Automation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Diagnóstico: --verify-offsets <path.json> carrega um cache de offsets e imprime símbolos-chave (sem jogo).
if (args.Length >= 2 && args[0] == "--verify-offsets")
{
    var st = new TbhBot.Core.Il2Cpp.SymbolTable();
    bool ok = st.LoadOffsetsJson(args[1]);
    Console.WriteLine($"LoadOffsetsJson = {ok}   ynj=[{string.Join(",", st.Ynj)}]   invClass={st.InvClass}");
    foreach (var k in new[] { "gra", "upd", "llx", "uo_ti", "uo_max", "uo_cur", "uo_wave", "inv_klass_ti", "inv_list_off", "PlayerSaveData.RuneSaveData", "cube_slot" })
        Console.WriteLine($"  {k,-30} {(st.Has(k) ? "0x" + st.Get(k).ToString("X") : "—")}");
    return;
}

// Verifica que os offsets estão EMBUTIDOS no assembly do Core e carregam (não precisa do jogo).
if (args.Contains("--verify-embedded"))
{
    var asm = typeof(TbhBot.Core.Engine).Assembly;
    var names = asm.GetManifestResourceNames();
    Console.WriteLine("recursos embutidos: " + (names.Length == 0 ? "(nenhum)" : string.Join(", ", names)));
    var res = System.Array.Find(names, n => n.EndsWith("json", StringComparison.OrdinalIgnoreCase));
    if (res is null) { Console.WriteLine("[FAIL] nenhum offsets_*.json embutido"); return; }
    var st = new TbhBot.Core.Il2Cpp.SymbolTable();
    using var s = asm.GetManifestResourceStream(res);
    bool ok = s is not null && st.LoadOffsetsJson(s);
    Console.WriteLine($"[{(ok && st.Get("uo_max") == 0x50 ? "PASS" : "FAIL")}] load embutido={ok}  uo_max=0x{st.Get("uo_max"):X}  gra=0x{st.Get("gra"):X}  ynj[0]=0x{(st.Ynj.Count > 0 ? st.Ynj[0] : 0):X}  invClass={st.InvClass}");
    return;
}

// Teste do AUTO-BOX ao vivo: acha StageBox vivas + conta iuw + abre as esperando (llx via dispatcher).
if (args.Contains("--autobox"))
{
    string[] nm = ["NORMAL", "BOSS", "ACTBOSS"];
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var boxes = eng.AutoBox.FindStageBoxes();
    Console.WriteLine($"StageBoxes vivas: {boxes.Count}  [{string.Join(", ", boxes.Select(kv => $"{nm[kv.Key]}@0x{kv.Value:X}"))}]");
    for (int t = 0; t <= 2; t++) Console.WriteLine($"  iuw({nm[t]}) = {eng.AutoBox.IuwCount(t)}");
    bool opened = eng.AutoBox.OpenAll(() => true);
    Console.WriteLine($"[{(eng.Target.IsAlive() ? "PASS" : "FAIL")}] OpenAll -> abriu={opened}  jogo vivo={eng.Target.IsAlive()}");
    return;
}

// Diagnóstico: attach + READS (StageProgress) em loop durante o boot — o attach/read crasha o jogo?
if (args.Contains("--readspam"))
{
    var eng = new TbhBot.Core.Engine();
    Console.WriteLine("tentando attachar (retry até subir)...");
    for (int i = 0; i < 60 && !eng.Attach(); i++) System.Threading.Thread.Sleep(1000);
    if (!eng.IsAttached) { Console.WriteLine("[x] não attachou"); return; }
    bool reattach = args.Contains("--reattach");
    Console.WriteLine($"attachado (pid {eng.Target.ProcessId}). {(reattach ? "RE-ATTACH" : "reads")} por 45s...");
    for (int i = 0; i < 45; i++)
    {
        bool alive = eng.Target.IsAlive();
        if (!alive) { Console.WriteLine($"[{i}s] JOGO MORREU!"); return; }
        if (reattach) { try { eng.Attach(); } catch { } }   // mimica o watchdog: recria todos os objetos
        int cur = 0; try { cur = eng.Save.StageProgress().Cur; } catch { }
        if (i % 3 == 0) Console.WriteLine($"[{i}s] alive={alive} cur={cur}");
        System.Threading.Thread.Sleep(1000);
    }
    Console.WriteLine($"45s de {(reattach ? "RE-ATTACH" : "reads")} — jogo SOBREVIVEU");
    return;
}

// Diagnóstico: RemoteCall (CreateRemoteThread) em loop durante o boot — isso crasha o jogo bootando?
if (args.Contains("--rcspam"))
{
    var eng = new TbhBot.Core.Engine();
    Console.WriteLine("attachando (retry)...");
    for (int i = 0; i < 60 && !eng.Attach(); i++) System.Threading.Thread.Sleep(1000);
    if (!eng.IsAttached) { Console.WriteLine("[x] não attachou"); return; }
    Console.WriteLine($"attachado (pid {eng.Target.ProcessId}). RemoteCall (IuwCount + ClassFromName) por 40s...");
    for (int i = 0; i < 40; i++)
    {
        if (!eng.Target.IsAlive()) { Console.WriteLine($"[{i}s] JOGO MORREU durante RemoteCall!"); return; }
        try { eng.AutoBox.IuwCount(2); } catch { }                                  // RemoteCall.Invoke
        try { eng.Il2Cpp.ClassFromName("TaskbarHero.Data", "RuneInfoData"); } catch { }  // RemoteCall (class_from_name)
        try { eng.Inventory.List(); } catch { }                                     // izb via RemoteCall por item
        if (i % 3 == 0) Console.WriteLine($"[{i}s] alive=True (RemoteCall ok)");
        System.Threading.Thread.Sleep(1000);
    }
    Console.WriteLine("40s de RemoteCall — jogo SOBREVIVEU");
    return;
}

// Teste do índice de preços do overlay (não precisa do jogo): índice embutido + lookups exato/fuzzy/grade.
if (args.Contains("--priceidx"))
{
    var idx = new TbhBot.Core.Market.PriceIndex();
    Console.WriteLine($"[{(idx.Count > 100 ? "PASS" : "FAIL")}] PriceIndex: {idx.Count} bases");
    foreach (var (name, grade) in new[] { ("Void Opal", "Beyond"), ("void opl", "Beyond"), ("Minor Ruby", "Common"), ("Diamond", "Immortal"), ("Dragon Heart", "Legendary") })
    {
        var b = idx.ResolveBase([name]);
        var pr = b is null ? null : idx.PriceOf(b, grade);
        Console.WriteLine($"      '{name}' ({grade}) -> base='{b}'  {(pr is { } p ? $"{(p.approx ? "~$" : "$")}{p.price:0.00}" : "sem preço")}");
    }
    return;
}

// Diagnóstico READ-ONLY de Evolution/Auto-boss: progresso, can-enter, soulstones, alvo do evolve. NÃO consome.
if (args.Contains("--stagenav"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }

    var (mx, cur, wave) = eng.Save.StageProgress();
    Console.WriteLine($"[{(mx > 0 && cur > 0 ? "PASS" : "FAIL")}] StageProgress: max={mx} cur={cur} wave={wave}");
    var t = eng.StageNav.StageTable();
    Console.WriteLine($"[{(t.Count >= 100 ? "PASS" : "FAIL")}] StageTable: {t.Count} estágios");

    // alvo do evolve (Torment 3-9 = 4309, clampado ao max): o que o modo FARIA, sem executar
    int alvo = Math.Min(mx, 4309);
    string tipo = t.TryGetValue(alvo, out var ai) ? (ai.Type == 1 ? "x-10 (precisa pedra)" : $"normal nv.{ai.Lvl}") : "?";
    Console.WriteLine($"      evolve escolheria: {alvo} ({tipo})  ·  cur>=alvo? {cur >= alvo} (se sim, já está farmando)");

    // can-enter dos bosses + soulstones (auto-boss)
    var counts = eng.Inventory.ReadCounts();
    foreach (var (ss, boss) in new[] { (190004, 4310), (190003, 3310) })
        Console.WriteLine($"      boss {boss}: CanEnter={eng.StageNav.CanEnter(boss)}  soulstone {ss} x{counts.GetValueOrDefault(ss)}");
    Console.WriteLine($"      IuwCount(ACTBOSS=2)={eng.AutoBox.IuwCount(2)}");
    return;
}

// Diagnóstico do LAYOUT da árvore de runas: reproduz o algoritmo e reporta a distribuição espacial.
if (args.Contains("--runelayout"))
{
    var eng = new TbhBot.Core.Engine();
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var defs = eng.RuneDefs.Read();
    Console.WriteLine($"defs: {defs.Count}");

    var ch = defs.ToDictionary(kv => kv.Key, kv => kv.Value.Next.Where(defs.ContainsKey).ToList());
    var par = defs.Keys.ToDictionary(k => k, _ => new List<int>());
    foreach (var (k, cs) in ch) foreach (int c in cs) par[c].Add(k);
    var roots = defs.Keys.Where(k => par[k].Count == 0).OrderBy(k => k).ToList();
    Console.WriteLine($"roots (sem pai): {roots.Count}");
    int withNext = defs.Values.Count(d => d.Next.Any(defs.ContainsKey));
    Console.WriteLine($"runas com next válido: {withNext}  ·  runas folha: {defs.Count - defs.Values.Count(d => d.Next.Any(defs.ContainsKey))}");

    var depth = new Dictionary<int, int>(); var tpar = new Dictionary<int, int?>(); var q = new Queue<int>();
    foreach (int r in roots) { depth[r] = 0; tpar[r] = null; q.Enqueue(r); }
    while (q.Count > 0) { int n = q.Dequeue(); foreach (int c in ch[n]) if (!depth.ContainsKey(c)) { depth[c] = depth[n] + 1; tpar[c] = n; q.Enqueue(c); } }
    foreach (int k in defs.Keys) if (!depth.ContainsKey(k)) depth[k] = 0;
    var tch = defs.Keys.ToDictionary(k => k, _ => new List<int>());
    foreach (var (n, p) in tpar) if (p is int pp) tch[pp].Add(n);
    var yp = new Dictionary<int, double>(); double ctr = 0.0;
    void Assign(int n) { var cc = tch[n].OrderBy(x => x).ToList(); if (cc.Count == 0) { yp[n] = ctr; ctr += 1; } else { foreach (int c in cc) Assign(c); yp[n] = cc.Average(c => yp[c]); } }
    foreach (int r in roots) { Assign(r); ctr += 1.3; }
    foreach (int k in defs.Keys) if (!yp.ContainsKey(k)) { yp[k] = ctr; ctr += 1; }
    const double CW = 104, RH = 58, PAD = 28;
    var pos = defs.Keys.ToDictionary(k => k, k => (x: PAD + depth[k] * CW, y: PAD + yp[k] * RH));
    Console.WriteLine($"x: {pos.Values.Min(p => p.x):0} .. {pos.Values.Max(p => p.x):0}   y: {pos.Values.Min(p => p.y):0} .. {pos.Values.Max(p => p.y):0}");
    Console.WriteLine($"maxdepth={depth.Values.Max()}  nós no viewport(0..860 x 0..600)={pos.Values.Count(p => p.x < 860 && p.y < 600)}");
    Console.WriteLine("depth histograma: " + string.Join(" ", depth.Values.GroupBy(d => d).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
    return;
}

// Teste das features de engine (rune defs, inventário, stage table).
if (args.Contains("--features"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var defs = eng.RuneDefs.Read();
    var d0 = defs.Values.FirstOrDefault();
    Console.WriteLine($"[{(defs.Count > 50 ? "PASS" : "FAIL")}] RuneDefs: {defs.Count} runas" + (d0 is not null ? $"  (ex: '{d0.Name}' max={d0.Max} next=[{string.Join(",", d0.Next)}])" : ""));
    foreach (var d in defs.Values.Take(6))
        Console.WriteLine($"      name='{d.Name}' icon='{d.Icon}' max={d.Max}");
    var lv = eng.RuneLevels.Read();
    Console.WriteLine($"[{(lv.Count > 50 ? "PASS" : "FAIL")}] RuneLevels: {lv.Count} runas c/ tabela de nível");
    foreach (var kv in lv.Take(4))
    {
        var rows = kv.Value;
        int status = rows.Count > 0 ? rows[^1].Status : -1;
        Console.WriteLine($"      rune {kv.Key}: {rows.Count} níveis · efeito='{TbhBot.Core.Game.RuneLevels.EffectName(status)}'{(TbhBot.Core.Game.RuneLevels.IsPercent(status) ? "%" : "")} · L1 val={rows[0].Value} custo={rows[0].Cost}");
    }
    var inv = eng.Inventory.List();
    bool named = inv.Any(i => !i.Name.StartsWith('#'));
    Console.WriteLine($"[{(inv.Count > 0 && named ? "PASS" : "FAIL")}] Inventory: {inv.Count} itens (nomes resolvidos={named})");
    foreach (var it in inv.OrderByDescending(i => i.Unit * i.Qty).Take(5))
        Console.WriteLine($"      '{it.Name}' [{TbhBot.Core.Game.Inventory.GradeName(it.Grade)}] x{it.Qty}  ${it.Unit:0.00} = ${it.Unit * it.Qty:0.00}");
    var stt = eng.StageNav.StageTable();
    Console.WriteLine($"[{(stt.Count >= 100 ? "PASS" : "FAIL")}] StageTable: {stt.Count} estágios");
    return;
}

// Teste do AUTO-FUSE: --fuse = preview (não consome). --fuse --go = FUNDE de verdade (consome 9 itens -> 1).
if (args.Contains("--fuse"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var prev = eng.AutoFuse.Preview();
    Console.WriteLine("fundíveis (synth,grade)->qtd: " + (prev.Count == 0 ? "(nenhum)" :
        string.Join(", ", prev.OrderBy(k => k.Key).Select(kv => $"({kv.Key.Item1},{kv.Key.Item2})={kv.Value}"))));
    bool any9 = prev.Any(kv => kv.Value >= 9 && kv.Key.Item2 <= eng.AutoFuse.MaxGrade && eng.AutoFuse.Types.Contains(kv.Key.Item1));
    Console.WriteLine($"tem >=9 fundível (teto grade {eng.AutoFuse.MaxGrade})? {any9}");
    if (args.Contains("--go"))
    {
        bool fused = eng.AutoFuse.DoSynth(() => true);
        Console.WriteLine($"[{(eng.Target.IsAlive() ? "PASS" : "FAIL")}] DoSynth -> fundiu={fused}  jogo vivo={eng.Target.IsAlive()}");
    }
    else Console.WriteLine("(passe  --fuse --go  para FUNDIR de verdade)");
    return;
}

// Teste do AUTO-STASH ao vivo: resolve 'ra' + move alguns itens inv->baú (maxn=3, gentil).
if (args.Contains("--stash"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    nint ra = eng.AutoStash.ResolveRa();
    Console.WriteLine($"[{(ra != 0 ? "PASS" : "FAIL")}] ra singleton = 0x{ra:X}");
    var (inv0, stash0) = eng.AutoStash.SlotCounts();
    int moved = eng.AutoStash.MoveAllToStash(() => true, 3);   // move até 3 itens (teste gentil)
    System.Threading.Thread.Sleep(500);
    var (inv1, stash1) = eng.AutoStash.SlotCounts();
    bool ok = eng.Target.IsAlive() && (moved == 0 || inv1 < inv0);
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] moveu={moved}  slots-inv-ocupados {inv0}->{inv1}  baú-livres {stash0}->{stash1}  vivo={eng.Target.IsAlive()}");
    return;
}

// Teste da resolução IL2CPP (export table + RemoteCall + class_from_name).
if (args.Contains("--klass"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    long ex = TbhBot.Core.Memory.RemoteCall.ResolveExport(eng.Memory, "il2cpp_class_from_name");
    Console.WriteLine($"[{(ex != 0 ? "PASS" : "FAIL")}] export il2cpp_class_from_name = 0x{ex:X}");
    var api = new TbhBot.Core.Game.Il2CppApi(eng.Memory);
    long sb = api.ClassFromName("TaskbarHero.UI", "StageBox");
    Console.WriteLine($"[{(sb != 0 ? "PASS" : "FAIL")}] StageBox klass = 0x{sb:X}  (vivo={eng.Target.IsAlive()})");
    return;
}

// Teste do DISPATCHER main-thread (transporte seguro: cmd1 argP=0 = no-op, não chama função do jogo).
if (args.Contains("--dispatch"))
{
    var eng = new TbhBot.Core.Engine();
    eng.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!eng.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    Console.WriteLine($"attach pid={eng.Target.ProcessId}  hash={TbhBot.Core.Il2Cpp.BuildInfo.DllHash(eng.Target.ModulePath)}");
    var disp = eng.Dispatcher as TbhBot.Core.Game.RealDispatcher;
    if (disp is null) { Console.WriteLine("[x] dispatcher não é RealDispatcher"); return; }
    Console.WriteLine($"IsReady antes = {disp.IsReady} (instala preguiçosamente no 1º comando)");
    for (int i = 0; i < 5; i++)
    {
        byte before = disp.Counter();
        bool ok = disp.Command(1, 0, 0);   // 1ª chamada instala o hook; cmd1 com argP=0 = no-op seguro
        System.Threading.Thread.Sleep(120);
        byte after = disp.Counter();
        bool alive = eng.Target.IsAlive();
        Console.WriteLine($"  cmd#{i}: ready={disp.IsReady} dispatch_ok={ok} cnt {before}->{after} vivo={alive}");
        if (!alive) { Console.WriteLine("  [FAIL] o jogo FECHOU (hook ruim)"); return; }
    }
    disp.Remove();
    Console.WriteLine($"[{(eng.Target.IsAlive() ? "PASS" : "FAIL")}] hook instalado, transportou comandos e removido; jogo vivo={eng.Target.IsAlive()}");
    return;
}

// Teste ponta-a-ponta ao vivo: --e2e [--offsets <dir do cache offsets_*.json>]
if (args.Contains("--e2e"))
{
    int i = Array.IndexOf(args, "--offsets");
    string cacheDir = (i >= 0 && i + 1 < args.Length)
        ? args[i + 1]
        : @"d:\SteamLibrary\steamapps\common\TaskbarHero\tbh_bot\_cache_bundle";
    await TbhBot.Cli.E2E.RunAsync(new TbhBot.Core.Engine(), cacheDir);
    return;
}

Console.WriteLine("== TbhBot.Cli — smoke das Fases 0-4 ==\n");

using var engine = new Engine();
engine.Log += m => Console.WriteLine($"  [engine] {m}");

bool attached = engine.Attach();
if (attached)
{
    var t = engine.Target;
    Console.WriteLine($"[ok] pid={t.ProcessId}  base=0x{t.ModuleBase:X}  ({t.ModuleSize / 1024 / 1024} MB)\n");

    // ---- Fase 1: batch read ----
    Bench(engine);

    // ---- Fase 2: offsets por build ----
    Console.WriteLine("\n[Fase 2] símbolos do build reconhecido:");
    foreach (var k in new[] { "gra", "upd", "llx", "cube_slot", "iw", "izb" })
        Console.WriteLine($"    {k,-10} {(engine.Symbols.Has(k) ? "0x" + engine.Symbols.Get(k).ToString("X") : "—")}");

    // ---- Fase 3: leituras de estado (batch) ----
    Console.WriteLine("\n[Fase 3] leituras de estado:");
    var (mx, cur, wave) = engine.Save.StageProgress();
    Console.WriteLine($"    inventário: {engine.Save.InventoryCount()} itens");
    Console.WriteLine($"    runas:      {engine.Save.ReadRunes().Count}");
    Console.WriteLine($"    cubo:       Lv.{engine.Save.CubeLevel()?.ToString() ?? "—"}");
    Console.WriteLine($"    stage:      max={mx} cur={cur} wave={wave}" + (mx < 0 ? "   (uo_* precisa da extração por dump — Fase 2 futura)" : ""));
    var stats = engine.Stats.ReadStats();
    Console.WriteLine($"    stats:      {stats.Count} lidos" + (stats.Count > 0 ? $"  (Attack Damage={stats.GetValueOrDefault("Attack Damage"):g4})" : ""));
}
else
{
    Console.WriteLine("[x] Jogo não encontrado — pulo Fases 1-3. Ainda demonstro a Fase 4 (arquitetura de concorrência).\n");
}

// ---- Fase 4: concorrência ----
// SÓ com --loop: o loop aplica/retira cheats conforme as flags; contra um jogo com automação externa
// (ex.: a box) isso BRIGARIA com ela (restaura ynj/godmode). Sem --loop = validação read-only segura.
if (!args.Contains("--loop"))
{
    Console.WriteLine("[Fase 4] pulada (read-only). Rode com  --loop  para exercitar Watchdog + AutomationLoop.");
    return;
}
Console.WriteLine("[Fase 4] Watchdog + AutomationLoop por ~2s (Task/CancellationToken, threads reais, sem GIL)…");
using var cts = new CancellationTokenSource();

var wd = new Watchdog(engine);
wd.Log += m => Console.WriteLine($"  [watchdog] {m}");
wd.Attached += () => Console.WriteLine("  [watchdog] jogo conectado");
wd.Detached += () => Console.WriteLine("  [watchdog] jogo caiu");

var loop = new AutomationLoop(engine);
loop.Log += m => Console.WriteLine($"  [loop] {m}");

var tasks = new[] { wd.RunAsync(cts.Token), loop.RunAsync(cts.Token) };
await Task.Delay(2000);
await cts.CancelAsync();
try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

Console.WriteLine("[Fase 4] loops encerrados de forma limpa (cancelamento cooperativo).");
Console.WriteLine("\n=> Cheats + leituras por patch funcionam; auto-box/stash/fuse aguardam o dispatcher main-thread");
Console.WriteLine("   (Game/DISPATCHER_PORT_NOTES.md) e o stage-progress aguarda a extração de offsets por dump.");

static void Bench(Engine engine)
{
    const int N = 8192;
    nint region = engine.Target.ModuleBase;
    _ = engine.Memory.ReadArray<ulong>(region, N);   // aquece

    var sw = Stopwatch.StartNew();
    ulong accSeq = 0;
    for (int i = 0; i < N; i++) accSeq += engine.Memory.ReadU64(region + i * 8);
    sw.Stop();
    double seq = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    ulong accBatch = 0;
    foreach (var v in engine.Memory.ReadArray<ulong>(region, N)) accBatch += v;
    sw.Stop();
    double bat = sw.Elapsed.TotalMilliseconds;

    Console.WriteLine($"[Fase 1] batch read: sequencial {seq:F2} ms vs batch {bat:F2} ms  →  {seq / Math.Max(bat, 0.0001):F0}x  (checksum {(accSeq == accBatch ? "ok" : "DIVERGE")})");
}
