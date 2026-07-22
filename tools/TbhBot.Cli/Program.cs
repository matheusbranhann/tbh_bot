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

// Auto-update do PAINEL: consulta o GitHub e mostra o que o banner mostraria (não baixa nada).
if (args.Contains("--update-check"))
{
    var up = new TbhBot.Core.Update.AutoUpdate();
    var cur = TbhBot.Core.Update.AutoUpdate.CurrentVersion;
    Console.WriteLine($"versão deste build : {cur}   (repo {TbhBot.Core.Update.AutoUpdate.Repo})");
    var (av, tag, url) = await up.CheckAsync(cur);
    Console.WriteLine($"release mais nova  : {(tag.Length > 0 ? tag : "(não consultou / sem tag)")}");
    Console.WriteLine($"tem update?        : {av}");
    if (av) Console.WriteLine($"asset .zip         : {url}");
    else if (tag.Length > 0) Console.WriteLine("  -> já estamos na mais nova (ou o release não tem asset .zip)");
    return;
}

// Feed de offsets: baixa offsets_<hash>.json do repo pro build informado (ou o do jogo instalado).
// É o caminho de auto-cura quando o jogo atualiza — testa que o JSON publicado está acessível e válido.
if (args.Contains("--feed"))
{
    int fi = Array.IndexOf(args, "--feed");
    string? fh = fi + 1 < args.Length && !args[fi + 1].StartsWith("--") ? args[fi + 1] : null;
    if (fh is null)
    {
        var e0 = new TbhBot.Core.Engine();
        if (!e0.Attach()) { Console.WriteLine("passe o hash: --feed <hash> (ou abra o jogo)"); return; }
        fh = e0.BuildHash;
    }
    Console.WriteLine($"buscando {TbhBot.Core.Update.OffsetsFeed.BaseUrl}/offsets_{fh}.json");
    var got = await TbhBot.Core.Update.OffsetsFeed.TryFetchAsync(fh!);
    Console.WriteLine(got is null
        ? "[FAIL] não achou/não validou (build ainda não publicado no feed, ou sem rede)"
        : $"[PASS] salvo em {got}");
    return;
}

// ACTk SOAK: liga o bypass e SEGURA, checando que o jogo continua vivo e lendo. O --e2e só aplica e
// reverte na hora — não pegaria o crash que o alvo errado (pool de getters do runtime) causava.
if (args.Contains("--actk-soak"))
{
    int si = Array.IndexOf(args, "--actk-soak");
    int secs = si + 1 < args.Length && int.TryParse(args[si + 1], out var sv) ? sv : 45;
    var ae = new TbhBot.Core.Engine();
    if (!ae.Attach()) { Console.WriteLine("[x] jogo não está aberto"); return; }
    Console.WriteLine($"attach pid={ae.Target.ProcessId} build={ae.BuildHash} offsets={ae.OffsetsLoaded}");

    var rvas = ae.Symbols.Ynj;
    if (rvas.Count == 0) { Console.WriteLine("[FAIL] ynj vazio — sem alvo ACTk"); return; }
    foreach (var r in rvas)
    {
        var b = ae.Memory.ReadBytes(ae.Target.ModuleBase + (nint)r, 12);
        Console.WriteLine($"  alvo 0x{r:X}  bytes: {Convert.ToHexString(b)}");
        // stub deduplicado = 'ret' logo no início; detector de verdade abre com prólogo (push/sub rsp)
        bool stub = Array.IndexOf(b, (byte)0xC3, 0, Math.Min(12, b.Length)) >= 0;
        Console.WriteLine($"  parece stub compartilhado? {(stub ? "SIM -> alvo ERRADO" : "não -> função real")}");
    }

    Console.WriteLine($"\nligando ACTk e segurando {secs}s...");
    ae.Cheats.Log = m => Console.WriteLine("  [cheats] " + m);
    ae.Cheats.SetActk(true);
    foreach (var r in rvas)
        Console.WriteLine($"  0x{r:X} agora = {Convert.ToHexString(ae.Memory.ReadBytes(ae.Target.ModuleBase + (nint)r, 1))}");

    var sw = Stopwatch.StartNew();
    bool died = false;
    while (sw.Elapsed.TotalSeconds < secs)
    {
        await Task.Delay(3000);
        if (!ae.Target.IsAlive()) { died = true; break; }
        var (mx, cur, wv) = ae.Save.StageProgress();   // prova que o jogo continua LENDO/rodando
        Console.WriteLine($"  t={sw.Elapsed.TotalSeconds,4:0}s vivo · max={mx} cur={cur} wave={wv}");
    }
    Console.WriteLine(died
        ? $"\n[FAIL] o jogo FECHOU depois de {sw.Elapsed.TotalSeconds:0}s com o ACTk ligado"
        : $"\n[PASS] jogo vivo e respondendo os {secs}s inteiros com o ACTk ligado");
    ae.Cheats.SetActk(false);
    Console.WriteLine("ACTk revertido; jogo vivo=" + ae.Target.IsAlive());
    return;
}

// EVOLUÇÃO: valida a regra "sobe UMA fase por vez e desliga sozinho no Torment 3-9". Simula a
// cadeia Next a partir do estágio atual SEM navegar (não mexe no progresso de quem está jogando)
// e executa Evolve() de verdade só pra confirmar o auto-desligar quando já se está no topo.
if (args.Contains("--evolve"))
{
    var ev = new TbhBot.Core.Engine();
    ev.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!ev.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var (mx, cur, wave) = ev.Save.StageProgress();
    var tb = ev.StageNav.StageTable();
    Console.WriteLine($"estado: max={mx} cur={cur} wave={wave} · tabela={tb.Count} estágios");

    // 1) a corrente Next sobe de 1 em 1 até 4309? Testa do estágio ATUAL e do 1-1 Normal (1101) —
    //    o caso que o usuário descreveu. É só caminhada na tabela: não navega, não muda nada no jogo.
    static (int End, int Steps, List<int> Path) Walk(int from, Dictionary<int, TbhBot.Core.Game.StageInfo> t)
    {
        int node = from, steps = 0; var path = new List<int>();
        while (node > 0 && node < 4309 && steps < 500 && t.TryGetValue(node, out var ni))
        {
            if (ni.Next <= 0 || ni.Next == node) break;
            node = ni.Next; path.Add(node); steps++;
        }
        return (node, steps, path);
    }
    foreach (var (label, from) in new[] { ("estágio atual", cur), ("1-1 Normal", 1101) })
    {
        var (end, steps, path) = Walk(from, tb);
        bool ok = end == 4309 || from >= 4309;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] de {label} ({from}): {steps} passos até {end}" +
            (path.Count > 0 ? $" · {string.Join(" -> ", path.Take(5))}{(path.Count > 5 ? " ... -> " + path[^1] : "")}" : " (já no topo)"));
        if (path.Count > 0)
            Console.WriteLine($"[{(path[0] != 4309 || steps == 1 ? "PASS" : "FAIL")}] não teleporta: 1º alvo = {path[0]} (não 4309 de cara)");
    }

    // 2) auto-desligar: com cur >= 4309, Evolve tem que chamar DisableEvolve e não navegar.
    bool disabled = false;
    ev.StageAutomation.DisableEvolve = () => disabled = true;
    int before = ev.Save.StageProgress().Cur;
    bool acted = ev.StageAutomation.Evolve(() => true);
    int after = ev.Save.StageProgress().Cur;
    if (cur >= 4309)
        Console.WriteLine($"[{(disabled && !acted && after == before ? "PASS" : "FAIL")}] no topo: desligou sozinho={disabled} navegou={acted} (estágio {before}->{after})");
    else
        Console.WriteLine($"      (fora do topo: Evolve retornou {acted}, estágio {before}->{after}, desligou={disabled})");
    Console.WriteLine($"jogo vivo={ev.Target.IsAlive()}");
    return;
}

// Reproduz o cenário do PAINEL: dispatcher instalado + automação concorrente, e aí lê as runas.
// O CLI sozinho lê 197; o painel mostra "jogo fechado ou resolvendo offsets…". A diferença é ESTA.
if (args.Contains("--runes-contended"))
{
    var re = new TbhBot.Core.Engine();
    re.Log += m => Console.WriteLine($"  [engine] {m}");
    if (!re.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }

    Console.WriteLine("1) leitura LIMPA (sem dispatcher, sem concorrência):");
    var sw0 = Stopwatch.StartNew();
    int n0 = re.RuneDefs.Read(force: true).Count;
    Console.WriteLine($"   defs={n0}  em {sw0.ElapsedMilliseconds}ms");

    Console.WriteLine("2) instalando o dispatcher (o painel mantém ele instalado o tempo todo):");
    _ = re.Dispatcher.IsReady;                 // instala o hook (idempotente)
    var sw1 = Stopwatch.StartNew();
    int n1 = re.RuneDefs.Read(force: true).Count;
    Console.WriteLine($"   defs={n1}  em {sw1.ElapsedMilliseconds}ms");

    Console.WriteLine("3) com a automação CONCORRENTE martelando RemoteCall (autobox/autostash):");
    using var rcts = new CancellationTokenSource();
    var noise = Task.Run(async () =>
    {
        while (!rcts.IsCancellationRequested)
        {
            try { re.AutoBox.IuwCount(0); re.AutoStash.ResolveRa(); } catch { }
            await Task.Delay(50);
        }
    });
    await Task.Delay(400);
    for (int i = 1; i <= 3; i++)
    {
        var sw2 = Stopwatch.StartNew();
        int n2 = re.RuneDefs.Read(force: true).Count;
        Console.WriteLine($"   tentativa {i}: defs={n2}  em {sw2.ElapsedMilliseconds}ms {(n2 == 0 ? "  <<<< REPRODUZIU a falha do painel" : "")}");
    }
    rcts.Cancel();
    try { await noise; } catch { }
    Console.WriteLine($"jogo vivo={re.Target.IsAlive()}");
    return;
}

// SOMENTE LEITURA: mostra o que está aplicado no jogo agora. Não instala dispatcher nem patcha nada,
// então é seguro rodar com o painel aberto (serve pra conferir o re-apply depois do auto-restart).
// ---- papel de "jogo falso": processo chamado TaskBarHero que só carrega o GameAssembly.dll DEPOIS
// de N segundos. Reproduz a corrida do auto-restart (processo existe, módulo ainda não) sem o jogo.
if (args.Contains("--fake-game"))
{
    int fi = Array.IndexOf(args, "--fake-game");
    int atraso = fi + 1 < args.Length && int.TryParse(args[fi + 1], out int fd) ? fd : 0;
    Thread.Sleep(atraso * 1000);
    try { System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "GameAssembly.dll")); }
    catch (Exception ex) { Console.WriteLine("fake-game: falhei ao carregar o dll: " + ex.Message); return; }
    Thread.Sleep(Timeout.Infinite);
    return;
}

// ---- TESTE DE REGRESSÃO do attach, determinístico e sem o jogo. Monta dois "jogos falsos": o
// primeiro carrega o módulo na hora (attach tem de dar certo), o segundo só depois de 8s. Attachar
// nessa janela é EXATAMENTE o que o ConnectLoop faz quando o watchdog reabre o jogo. Se o Attach()
// devolver true nessa janela, a base é a da sessão ANTERIOR — o bug que matava auto-box/auto-stash.
if (args.Contains("--attach-regress"))
{
    string dir = Path.Combine(Path.GetTempPath(), "tbh_attach_regress");
    Directory.CreateDirectory(dir);
    string fakeExe = Path.Combine(dir, "TaskBarHero.exe");
    string cliDir = AppContext.BaseDirectory;
    foreach (var f in Directory.GetFiles(cliDir))
        File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    File.Copy(Path.Combine(cliDir, Path.GetFileName(Environment.ProcessPath!)), fakeExe, true);
    File.Copy(@"C:\Windows\System32\winmm.dll", Path.Combine(dir, "GameAssembly.dll"), true);

    Process Spawn(int atraso) => Process.Start(new ProcessStartInfo(fakeExe, $"--fake-game {atraso}")
    { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true })!;

    var alvo = new TbhBot.Core.Memory.ProcessTarget();
    bool bug = false;

    Console.WriteLine("1) jogo falso A (carrega o modulo na hora)");
    var a = Spawn(0);
    nint baseA = 0;
    for (int i = 0; i < 40 && baseA == 0; i++) { Thread.Sleep(250); if (alvo.Attach()) baseA = alvo.ModuleBase; }
    Console.WriteLine($"   attach em A: base=0x{baseA:X}  {(baseA != 0 ? "OK" : "FALHOU (teste inconclusivo)")}");
    if (baseA == 0) { try { a.Kill(); } catch { } return; }

    Console.WriteLine("2) mata A e sobe B, que so carrega o modulo depois de 8s");
    a.Kill(); a.WaitForExit();
    Thread.Sleep(500);
    var b = Spawn(8);
    Thread.Sleep(1500);                               // a janela do bug: processo vivo, modulo ausente

    bool attachOk = alvo.Attach();
    nint baseJanela = alvo.ModuleBase;
    Console.WriteLine($"3) attach DENTRO da janela: retorno={attachOk}  base=0x{baseJanela:X}  pid={alvo.ProcessId}");
    if (attachOk && baseJanela == baseA)
    {
        bug = true;
        Console.WriteLine("   >>> BUG: devolveu TRUE com a base do processo ANTERIOR (0x{0:X})", baseA);
    }
    else if (!attachOk && baseJanela == 0)
        Console.WriteLine($"   >>> CORRETO: recusou o attach (motivo: {alvo.ModuleError})");
    else
        Console.WriteLine("   >>> inesperado — inspecionar");

    Console.WriteLine("4) espera o modulo aparecer e re-attacha (o ConnectLoop tenta a cada 1s)");
    nint baseB = 0;
    for (int i = 0; i < 40 && baseB == 0; i++) { Thread.Sleep(500); if (alvo.Attach()) baseB = alvo.ModuleBase; }
    Console.WriteLine($"   base final=0x{baseB:X}  {(baseB != 0 ? "attachou" : "NAO attachou")}");

    try { b.Kill(); } catch { }
    alvo.Dispose();
    Console.WriteLine();
    Console.WriteLine(bug ? "RESULTADO: BUG PRESENTE" : "RESULTADO: SEM O BUG");
    Environment.ExitCode = bug ? 1 : 0;
    return;
}

// REGRESSÃO DO AUTO-RESTART: fica em loop imitando o ConnectLoop do painel (só chama Attach() quando
// !IsAttached) e imprime, a cada segundo, a base do módulo e a saúde do que depende de Base+RVA.
// Mate o jogo e reabra durante a execução: a base TEM de mudar para a do processo novo. Se ela ficar
// congelada na antiga, o attach prematuro voltou — que é o bug que matava auto-box e auto-stash.
if (args.Contains("--restart-watch"))
{
    int segundos = 240;
    int wi = Array.IndexOf(args, "--restart-watch");
    if (wi + 1 < args.Length && int.TryParse(args[wi + 1], out int s)) segundos = s;

    var we = new TbhBot.Core.Engine();
    we.Log += m => Console.WriteLine($"        [engine] {m}");
    nint basePrev = 0;
    int pidPrev = 0;
    Console.WriteLine("hh:mm:ss  pid      base              disp  boxes  ra                 obs");
    for (int i = 0; i < segundos; i++)
    {
        bool viva = we.IsAttached && we.Target.IsAlive();
        if (!viva) { try { we.Attach(); } catch { } }

        nint b = we.Target.ModuleBase;
        int pid = we.Target.ProcessId;
        string disp = "-", boxes = "-", ra = "-", obs = "";

        if (we.IsAttached && we.Target.IsAlive())
        {
            try { disp = we.Dispatcher.IsReady ? "ok" : "não"; } catch { disp = "err"; }
            try { boxes = we.AutoBox.FindStageBoxes().Count.ToString(); } catch { boxes = "err"; }
            try { nint r = we.AutoStash.ResolveRa(); ra = r == 0 ? "0 (FALHOU)" : $"0x{r:X}"; } catch { ra = "err"; }
        }
        else obs = we.Target.ModuleError ?? "sem jogo";

        if (pid != pidPrev && pid != 0) { obs += $"  <<< PROCESSO NOVO (era {pidPrev})"; pidPrev = pid; }
        if (b != basePrev) { obs += $"  <<< BASE MUDOU (era 0x{basePrev:X})"; basePrev = b; }

        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {pid,-7}  0x{b:X12}  {disp,-4}  {boxes,-5}  {ra,-17}  {obs}");
        System.Threading.Thread.Sleep(1000);
    }
    return;
}

// SÓ LEITURA: compara a resolução NOVA do godmode (busca pela cauda, aceita 57/C3) com a ANTIGA
// (padrão inteiro, exige 57). Com o cheat ligado elas divergem — e patchar a antiga era o "godmode 2x".
if (args.Contains("--godsite"))
{
    var ge = new TbhBot.Core.Engine();
    if (!ge.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    nint b = ge.Target.ModuleBase;
    nint velho = ge.Scanner.FindAob(TbhBot.Core.Il2Cpp.GameConstants.AobGodmode);
    nint novo = 0;
    foreach (nint t in ge.Scanner.FindAllAob(TbhBot.Core.Il2Cpp.GameConstants.AobGodmodeTail))
    {
        byte[] p = ge.Memory!.ReadBytes(t - 1, 1);
        if (p.Length == 1 && (p[0] == 0x57 || p[0] == 0xC3)) { novo = t - 1; break; }
    }
    string By(nint a) => a == 0 ? "--" : ge.Memory!.ReadBytes(a, 1)[0].ToString("x2");
    Console.WriteLine($"base={b:x}");
    Console.WriteLine($"  NOVO  (cauda, 57|C3) = {novo:x}  rva={(long)(novo - b):x}  byte={By(novo)}");
    Console.WriteLine($"  ANTIGO(padrão, só57) = {velho:x}  rva={(long)(velho - b):x}  byte={By(velho)}");
    Console.WriteLine(novo == velho
        ? "  iguais -> o godmode está DESLIGADO agora (o teste não discrimina neste estado)"
        : "  DIFERENTES -> com o cheat ligado, o código antigo patcharia a função errada");
    Console.WriteLine("  (nenhum byte foi escrito)");
    return;
}

if (args.Contains("--peek"))
{
    var pe = new TbhBot.Core.Engine();
    if (!pe.Attach()) { Console.WriteLine("[x] jogo não aberto"); return; }
    var (mx, cur, wv) = pe.Save.StageProgress();
    Console.WriteLine($"pid={pe.Target.ProcessId}  stage: max={mx} cur={cur} wave={wv}");
    var stats = pe.Stats.ReadStats();
    foreach (var k in new[] { "Movement Speed", "Attack Damage", "Attack Speed", "Critical Chance", "Critical Damage" })
        Console.WriteLine($"  {k,-18} = {(stats.TryGetValue(k, out var v) ? v.ToString("0.###") : "—")}");
    var sf = pe.Stats.ReadStage();
    foreach (var k in new[] { "WaveAmount", "WaveMonsterAmount" })
        Console.WriteLine($"  {k,-18} = {(sf.TryGetValue(k, out var s) ? s.ToString() : "—")}");
    var (invUsed, stashFree) = pe.AutoStash.SlotCounts();
    Console.WriteLine($"  inventário ocupado = {invUsed}   baú livre = {stashFree}");
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
