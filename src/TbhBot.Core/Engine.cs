using TbhBot.Core.Game;
using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core;

/// <summary>
/// Fachada de alto nível: attach ao jogo, resolução de offsets por build, cheats, leitura/escrita de save e
/// o dispatcher main-thread (stub por ora). Os loops (automação/watchdog) leem as flags <c>Want*</c> daqui.
/// </summary>
public sealed class Engine : IDisposable
{
    public ProcessTarget   Target   { get; } = new();
    public MemoryAccess    Memory   { get; private set; } = null!;
    public SymbolTable     Symbols  { get; private set; } = null!;
    public MemoryScanner   Scanner  { get; private set; } = null!;
    public Il2CppResolver  Resolver { get; private set; } = null!;
    public Game.Cheats     Cheats   { get; private set; } = null!;
    public StatEditor      Stats    { get; private set; } = null!;
    public SaveData        Save     { get; private set; } = null!;
    public Il2CppApi       Il2Cpp   { get; private set; } = null!;
    public AutoBox         AutoBox  { get; private set; } = null!;
    public AutoStash       AutoStash { get; private set; } = null!;
    public AutoFuse        AutoFuse { get; private set; } = null!;
    public RuneDefs        RuneDefs { get; private set; } = null!;
    public RuneLevels      RuneLevels { get; private set; } = null!;
    public Inventory       Inventory { get; private set; } = null!;
    public StageNav        StageNav { get; private set; } = null!;
    public StageAutomation StageAutomation { get; private set; } = null!;
    public IMainThreadDispatcher Dispatcher { get; private set; } = new StubDispatcher();

    public bool IsAttached => Target.IsAttached && Memory is not null;

    /// <summary>Hash do build (md5 dos 2MB do GameAssembly.dll) e se os offsets resolveram — pro status da UI.</summary>
    public string? BuildHash { get; private set; }
    public bool OffsetsLoaded { get; private set; }

    // Flags de intenção — o AutomationLoop lê a cada tick.
    public bool WantActk, WantGodmode, WantAutobox, WantAutostash, WantAutofuse, WantAutoboss, WantEvolve;
    public bool WantWatchdog;   // Auto-restart: jogo fechou? reabre via Steam + reaplica tudo

    /// <summary>Durante o restart: o loop re-attacha mas NÃO aplica nada (start limpo). Igual ao _wd_hold do Python.</summary>
    public volatile bool WdHold;

    /// <summary>Steam appid do TaskBarHero — usado pelo Auto-restart pra reabrir (steam://run/&lt;appid&gt;).</summary>
    public const int SteamAppId = 3678970;

    // Stats/stage FORÇADOS (re-aplicados a cada tick — o jogo recalcula e sobrescreve uma escrita única).
    // O painel troca a referência inteira (assign atômico) p/ não correr com o loop.
    public Dictionary<string, double> WantStats = new();
    public Dictionary<string, int> WantStage = new();

    public event Action<string>? Log;
    internal void Emit(string msg) => Log?.Invoke(msg);

    public bool Attach()
    {
        if (!Target.Attach()) return false;
        Memory  = new MemoryAccess(Target);
        Symbols = new SymbolTable();

        var hash = BuildInfo.DllHash(Target.ModulePath);
        BuildHash = hash;
        bool loaded = false;
        if (hash is null)
            Emit("build-hash indisponível (não consegui ler o GameAssembly.dll do disco)");
        else
        {
            loaded = Symbols.LoadKnownBuild(hash);
            if (!loaded)
            {
                // Fallback: cache de offsets no formato do Python (offsets_<hash>.json), ao lado do exe.
                foreach (var cand in new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "cache", $"offsets_{hash}.json"),
                    Path.Combine(AppContext.BaseDirectory, $"offsets_{hash}.json"),
                })
                {
                    if (Symbols.LoadOffsetsJson(cand))
                    {
                        loaded = true;
                        Emit($"offsets carregados do cache {Path.GetFileName(cand)}");
                        break;
                    }
                }
            }
            if (!loaded)
            {
                // Fallback final: offsets EMBUTIDOS no assembly (Offsets/offsets_<hash>.json) -> 1 exe.
                var asm = typeof(Engine).Assembly;
                var res = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith($"offsets_{hash}.json", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    if (s is not null && Symbols.LoadOffsetsJson(s))
                    {
                        loaded = true;
                        Emit($"offsets embutidos ({hash})");
                    }
                }
            }
            Emit(loaded
                ? $"build {hash} — offsets prontos"
                : $"build {hash} desconhecido e sem cache — só reads por AOB (stats/stage/god) funcionam; auto-offset por dump = futuro");
        }

        OffsetsLoaded = loaded;
        Scanner    = new MemoryScanner(Memory);
        Resolver   = new Il2CppResolver(Memory, Symbols, Scanner);
        Cheats     = new Game.Cheats(Memory, Symbols, Scanner);
        Stats      = new StatEditor(Memory, Scanner);
        Save       = new SaveData(Memory, Symbols, Resolver);
        var disp   = new Game.RealDispatcher(Memory, Symbols) { Log = Emit };
        Dispatcher = disp;
        Il2Cpp     = new Il2CppApi(Memory);
        AutoBox    = new AutoBox(Memory, Symbols, Scanner, Il2Cpp, disp) { Log = Emit };
        AutoStash  = new AutoStash(Memory, Symbols, Scanner, Il2Cpp, Resolver, disp) { Log = Emit };
        AutoFuse   = new AutoFuse(Memory, Symbols, Resolver, disp) { Log = Emit };
        RuneDefs   = new RuneDefs(Memory, Il2Cpp, Scanner);
        RuneLevels = new RuneLevels(Memory, Il2Cpp, Scanner);
        Inventory  = new Inventory(Memory, Symbols, Resolver);
        StageNav   = new StageNav(Memory, Symbols, Resolver, disp) { Log = Emit };
        StageAutomation = new StageAutomation(StageNav, Save, AutoBox, Inventory, Symbols)
        {
            Log = Emit,
            DisableEvolve = () => WantEvolve = false,   // evolução chegou no topo -> desliga o modo sozinha
        };
        return true;
    }

    public void Dispose() => Target.Dispose();
}
