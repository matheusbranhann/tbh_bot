using System.Runtime.InteropServices;
using Iced.Intel;
using TbhBot.Core.Il2Cpp;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

/// <summary>
/// Dispatcher main-thread REAL (porta de _install_dispatch/_dispatch_code/_dispatch do tbh_core.py).
/// Faz hook no InputManager.Update (sym "upd") com um code-cave que, a cada frame, consome UM comando
/// numerado escrito por nós e chama a função do jogo pedida NA MAIN-THREAD. É o transporte que destrava
/// auto-box/stash/fuse (chamadas Unity que crasham se feitas de outra thread).
///
/// Instala preguiçosamente (na 1ª ação). Prólogo relocável medido com Iced -> à prova de update.
/// </summary>
public sealed class RealDispatcher : IMainThreadDispatcher, IDisposable
{
    private readonly MemoryAccess _mem;
    private readonly SymbolTable _sym;
    private readonly object _lock = new();

    private nint _cave, _data, _upd;
    private byte[] _stolen = [];
    private bool _ready;
    private bool _installTried;

    public Action<string>? Log;

    public RealDispatcher(MemoryAccess mem, SymbolTable sym) { _mem = mem; _sym = sym; }

    private nint Base => _mem.Target.ModuleBase;
    public bool IsReady => _ready;

    // Offsets do bloco de dados (relativo a _data = cave+0x800). Espelham o _dispatch_code do Python.
    private const int D_EN = 0, D_DO = 1, D_INOP = 2, D_CMD = 4, D_ARGP = 8, D_ARGI = 0x10,
                      D_CNT = 0x14, D_ARG2 = 0x18, D_REQ = 0x20, D_FUNC = 0x38, D_RET = 0x40;

    // ===================== API pública (IMainThreadDispatcher) =====================

    public bool Command(int cmd, nint argP = 0, int argI = 0)
    {
        if (!EnsureInstalled()) return false;
        return Dispatch(cmd, argP, argI, null, 0, 800);
    }

    public int? Call(long funcVa, nint argP = 0, int argI = 0)
    {
        if (funcVa == 0 || !EnsureInstalled()) return null;
        _mem.Write<ulong>(_data + D_FUNC, (ulong)funcVa);
        return Dispatch(12, argP, argI, null, 0, 1000) ? LastRet() : null;
    }

    /// <summary>cmd2 (stash): iw(ra, &amp;MoveRequest, fakeDelegate). moveReq = 5 ints.</summary>
    public bool CommandStash(nint ra, int[] moveReq) => EnsureInstalled() && Dispatch(2, ra, 0, moveReq, 0, 1000);

    // ===================== INSTALL =====================

    private bool EnsureInstalled()
    {
        if (_ready) return true;
        lock (_lock) { if (!_installTried) { _installTried = true; Install(); } }
        return _ready;
    }

    private bool Install()
    {
        long upd = _sym.Get("upd");
        if (upd == 0 || _sym.Get("llx") == 0) { Log?.Invoke("dispatcher: sym upd/llx faltando"); return false; }
        nint UPD = Base + (nint)upd;

        // hook órfão de sessão morta (upd[0]==0xE9): restaura o prólogo original do GameAssembly.dll no disco.
        var head = _mem.ReadBytes(UPD, 1);
        if (head.Length == 1 && head[0] == 0xE9)
        {
            var raw = ReadDllBytes(upd, 24);
            int? p0 = raw is not null ? PlenOf(raw, (ulong)UPD) : null;
            if (raw is null || p0 is null) { Log?.Invoke("dispatcher: hook órfão sem recuperação"); return false; }
            var hs0 = SuspendAll();
            try { _mem.WriteBytes(UPD, raw.AsSpan(0, p0.Value)); } finally { ResumeAll(hs0); }
            Log?.Invoke("dispatcher: hook órfão removido");
        }

        int? plen = PrologueLen(UPD);
        if (plen is null) { Log?.Invoke("dispatcher: prólogo não-relocável (rip/branch)"); return false; }
        var stolen = _mem.ReadBytes(UPD, plen.Value);
        if (stolen.Length != plen.Value) return false;

        nint cave = CodeCave.Alloc(_mem, UPD);
        if (cave == 0) { Log?.Invoke("dispatcher: cave falhou"); return false; }

        _mem.WriteBytes(cave + 0x800, new byte[0x50]);                     // limpa o bloco de dados
        _mem.WriteBytes(cave, DispatchCode(cave, UPD + plen.Value, stolen));
        _mem.WriteBytes(cave + 0x900, [0xC3]);                             // ret p/ o fake delegate
        _mem.WriteBytes(cave + 0x920, new byte[0x48]);
        _mem.Write<ulong>(cave + 0x920 + 0x18, (ulong)(cave + 0x900));     // fake[+0x18] = ret

        var patch = new List<byte> { 0xE9 };
        patch.AddRange(BitConverter.GetBytes((int)(cave - (UPD + 5))));
        for (int i = 5; i < plen.Value; i++) patch.Add(0x90);             // NOP-fill até o fim do prólogo roubado

        var hs = SuspendAll();
        try { _mem.WriteBytes(UPD, patch.ToArray()); } finally { ResumeAll(hs); }

        _mem.WriteBytes(cave + 0x800, [0x01]);                             // enable
        _cave = cave; _data = cave + 0x800; _upd = UPD; _stolen = stolen; _ready = true;
        Log?.Invoke($"dispatcher instalado @ 0x{cave:X} (prólogo {plen} bytes)");
        return true;
    }

    public void Remove()
    {
        if (!_ready) return;
        try
        {
            _mem.WriteBytes(_data, [0x00]);                                // en=0
            var hs = SuspendAll();
            try { _mem.WriteBytes(_upd, _stolen); } finally { ResumeAll(hs); }
            Log?.Invoke("dispatcher removido (Update restaurado)");
        }
        catch { /* best-effort */ }
        _ready = false;
    }

    /// <summary>Contador que o cave incrementa a cada comando consumido — prova de vida do hook.</summary>
    public byte Counter() => _ready ? First(_mem.ReadBytes(_data + D_CNT, 1)) : (byte)0;

    // ===================== DISPATCH (produtor/consumidor) =====================

    private bool Dispatch(int cmd, nint argP, int argI, int[]? req, int arg2, int timeoutMs)
    {
        if (!_ready) return false;
        lock (_lock)
        {
            nint d = _data;
            for (int i = 0; i < 30; i++) { if (First(_mem.ReadBytes(d + D_DO, 1)) == 0) break; Thread.Sleep(15); }
            if (req is { Length: >= 5 })
                for (int i = 0; i < 5; i++) _mem.Write<int>(d + D_REQ + i * 4, req[i]);
            _mem.Write<int>(d + D_ARG2, arg2);
            _mem.Write<ulong>(d + D_ARGP, (ulong)argP);
            _mem.Write<int>(d + D_ARGI, argI);
            _mem.Write<int>(d + D_CMD, cmd);
            _mem.WriteBytes(d + D_DO, [0x01]);                             // doFlag=1
            bool got = false;
            int iters = Math.Max(1, timeoutMs / 15);
            for (int i = 0; i < iters; i++) { if (First(_mem.ReadBytes(d + D_DO, 1)) == 0) { got = true; break; } Thread.Sleep(15); }
            _mem.WriteBytes(d + D_INOP, [0x00]);                           // reset inop (unwind de NRE pode ter pulado)
            return got;
        }
    }

    private int? LastRet() => _ready ? (int)(_mem.ReadU64(_data + D_RET) & 0xffffffff) : null;

    // ===================== SHELLCODE (porta byte-a-byte de _dispatch_code) =====================

    private byte[] DispatchCode(nint cave, nint backVa, byte[] stolen)
    {
        long B = Base;
        long LLX = B + _sym.Get("llx"), IW = B + _sym.Get("iw"), ILO = B + _sym.Get("ilo"),
             IPU = B + _sym.Get("ipu"), IMX = B + _sym.Get("imx"), INF = B + _sym.Get("inf"),
             ILI = B + _sym.Get("ili"), IOG = B + _sym.Get("iog"), IOA = B + _sym.Get("ioa"),
             IMA = B + _sym.Get("ima"), LLM = B + _sym.Get("llm"), JGD = B + _sym.Get("jgd");
        long D = (long)cave + 0x800;
        long dEN = D, dDO = D + 1, dINOP = D + 2, dCMD = D + 4, dARGP = D + 8, dARGI = D + 0x10,
             dCNT = D + 0x14, dARG2 = D + 0x18, dREQ = D + 0x20, dFUNC = D + 0x38, dRET = D + 0x40;
        long FAKE = (long)cave + 0x920;

        var c = new List<byte>();
        var lab = new Dictionary<string, int>();
        var fix = new List<(int pos, string label)>();
        void Raw(params byte[] bs) => c.AddRange(bs);
        void Imm(long r) => c.AddRange(BitConverter.GetBytes((ulong)r));
        void Jcc(byte[] op, string l) { c.AddRange(op); fix.Add((c.Count, l)); c.AddRange(new byte[4]); }
        void Jmp(string l) { c.Add(0xE9); fix.Add((c.Count, l)); c.AddRange(new byte[4]); }
        void Lbl(string n) => lab[n] = c.Count;

        Raw(0x50, 0x51, 0x52, 0x53, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, 0x9C); // push rax rcx rdx rbx r8-r11; pushfq
        Raw(0x48, 0xB8); Imm(dEN); Raw(0x80, 0x38, 0x00); Jcc([0x0F, 0x84], "skip");        // cmp byte[en],0; je skip
        Raw(0x48, 0xB8); Imm(dDO); Raw(0x80, 0x38, 0x00); Jcc([0x0F, 0x84], "skip");        // cmp byte[do],0; je skip
        Raw(0xC6, 0x00, 0x00);                                                              // byte[do]=0
        Raw(0x48, 0xB8); Imm(dINOP); Raw(0x80, 0x38, 0x00); Jcc([0x0F, 0x85], "skip");      // cmp byte[inop],0; jne skip
        Raw(0x48, 0xB8); Imm(dINOP); Raw(0xC6, 0x00, 0x01);                                 // byte[inop]=1
        Raw(0x48, 0xB8); Imm(dCMD); Raw(0x8B, 0x00);                                        // eax=[cmd]

        Raw(0x83, 0xF8, 0x01); Jcc([0x0F, 0x85], "c2");                                     // cmd1 box
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x31, 0xD2, 0x4D, 0x31, 0xC0, 0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(LLX); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c2"); Raw(0x83, 0xF8, 0x02); Jcc([0x0F, 0x85], "c3");                          // cmd2 stash
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0xBA); Imm(dREQ);
        Raw(0x49, 0xB8); Imm(FAKE);
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IW); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c3"); Raw(0x83, 0xF8, 0x03); Jcc([0x0F, 0x85], "c4");                          // cmd3 ilo(argI)
        Raw(0x48, 0xB8); Imm(dARGI); Raw(0x8B, 0x08, 0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(ILO); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c4"); Raw(0x83, 0xF8, 0x04); Jcc([0x0F, 0x85], "c5");                          // cmd4 ipu()
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IPU); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c5"); Raw(0x83, 0xF8, 0x05); Jcc([0x0F, 0x85], "c6");                          // cmd5 imx()
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IMX); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c6"); Raw(0x83, 0xF8, 0x06); Jcc([0x0F, 0x85], "c7");                          // cmd6 inf(argP)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(INF); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c7"); Raw(0x83, 0xF8, 0x07); Jcc([0x0F, 0x85], "c8");                          // cmd7 ili(argI)
        Raw(0x48, 0xB8); Imm(dARGI); Raw(0x8B, 0x08, 0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(ILI); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c8"); Raw(0x83, 0xF8, 0x08); Jcc([0x0F, 0x85], "c9");                          // cmd8 iog(argI,argP)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x10, 0x48, 0x85, 0xD2); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0xB8); Imm(dARGI); Raw(0x8B, 0x08);
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IOG); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c9"); Raw(0x83, 0xF8, 0x09); Jcc([0x0F, 0x85], "c10");                         // cmd9 ioa(argI,arg2)
        Raw(0x48, 0xB8); Imm(dARGI); Raw(0x8B, 0x08);
        Raw(0x48, 0xB8); Imm(dARG2); Raw(0x8B, 0x10);
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IOA); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c10"); Raw(0x83, 0xF8, 0x0A); Jcc([0x0F, 0x85], "c11");                        // cmd10 ima(argP)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(IMA); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c11"); Raw(0x83, 0xF8, 0x0B); Jcc([0x0F, 0x85], "c13");                        // cmd11 llm(argP) -> c13 (corrente!)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(LLM); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c13"); Raw(0x83, 0xF8, 0x0D); Jcc([0x0F, 0x85], "c12");                        // cmd13 jgd(argP,FAKE)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08, 0x48, 0x85, 0xC9); Jcc([0x0F, 0x84], "done");
        Raw(0x48, 0xBA); Imm(FAKE);
        Raw(0x48, 0x83, 0xEC, 0x20, 0x48, 0xB8); Imm(JGD); Raw(0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("c12"); Raw(0x83, 0xF8, 0x0C); Jcc([0x0F, 0x85], "done");                       // cmd12 [dFUNC](argP,argI)
        Raw(0x48, 0xB8); Imm(dARGP); Raw(0x48, 0x8B, 0x08);
        Raw(0x48, 0xB8); Imm(dARGI); Raw(0x8B, 0x10);
        Raw(0x48, 0xB8); Imm(dFUNC); Raw(0x48, 0x8B, 0x00);
        Raw(0x48, 0x83, 0xEC, 0x20, 0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20); Jmp("done");

        Lbl("done");
        Raw(0x49, 0xBB); Imm(dRET); Raw(0x49, 0x89, 0x03);                                  // r11=dRET; [r11]=rax
        Raw(0x48, 0xB8); Imm(dINOP); Raw(0xC6, 0x00, 0x00);                                 // inop=0
        Raw(0x48, 0xB8); Imm(dCNT); Raw(0xFF, 0x00);                                        // cnt++

        Lbl("skip");
        Raw(0x9D, 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5B, 0x5A, 0x59, 0x58);  // popfq; pops
        c.AddRange(stolen);                                                                 // prólogo roubado real
        c.Add(0xE9); int rbp = c.Count; c.AddRange(new byte[4]);

        var arr = c.ToArray();
        int backRel = (int)((long)backVa - ((long)cave + rbp + 4));
        BitConverter.GetBytes(backRel).CopyTo(arr, rbp);
        foreach (var (pos, l) in fix)
            BitConverter.GetBytes(lab[l] - (pos + 4)).CopyTo(arr, pos);
        return arr;
    }

    // ===================== prólogo relocável (Iced) =====================

    private int? PrologueLen(nint addr, int minlen = 5, int maxlen = 8)
        => PlenOf(_mem.ReadBytes(addr, 24), (ulong)addr, minlen, maxlen);

    private static int? PlenOf(byte[] raw, ulong ip, int minlen = 5, int maxlen = 8)
    {
        if (raw.Length == 0) return null;
        var decoder = Decoder.Create(64, raw);
        decoder.IP = ip;
        int n = 0;
        ulong end = ip + (ulong)raw.Length;
        while (decoder.IP < end)
        {
            var ins = decoder.Decode();
            if (ins.IsInvalid) return null;
            if (ins.IsIPRelativeMemoryOperand) return null;                                 // rip-rel -> não reloca
            if (ins.FlowControl is not FlowControl.Next) return null;                       // branch/call/ret -> idem
            n += ins.Length;
            if (n >= minlen) return n <= maxlen ? n : null;
        }
        return null;
    }

    // ===================== prólogo do disco (recuperação de hook órfão) =====================

    /// <summary>Lê n bytes de um RVA a partir do GameAssembly.dll NO DISCO (mapeando RVA->offset pelo PE).</summary>
    private byte[]? ReadDllBytes(long rva, int n)
    {
        try
        {
            var path = _mem.Target.ModulePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var f = File.ReadAllBytes(path);
            int peOff = BitConverter.ToInt32(f, 0x3C);
            int nSec = BitConverter.ToUInt16(f, peOff + 6);
            int optSize = BitConverter.ToUInt16(f, peOff + 20);
            int secTab = peOff + 24 + optSize;
            for (int i = 0; i < nSec; i++)
            {
                int s = secTab + i * 40;
                uint va = BitConverter.ToUInt32(f, s + 12);
                uint vsz = BitConverter.ToUInt32(f, s + 8);
                uint praw = BitConverter.ToUInt32(f, s + 20);
                if ((ulong)rva >= va && (ulong)rva < va + vsz)
                {
                    long off = praw + (rva - va);
                    if (off < 0 || off + n > f.Length) return null;
                    var outb = new byte[n];
                    Array.Copy(f, off, outb, 0, n);
                    return outb;
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ===================== suspend/resume de threads (durante o patch do prólogo) =====================

    private List<nint> SuspendAll()
    {
        var hs = new List<nint>();
        nint snap = CreateToolhelp32Snapshot(0x4, 0);                                        // TH32CS_SNAPTHREAD
        if (snap == 0 || snap == -1) return hs;
        try
        {
            var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            for (bool ok = Thread32First(snap, ref te); ok; ok = Thread32Next(snap, ref te))
                if (te.th32OwnerProcessID == (uint)_mem.Target.ProcessId)
                {
                    nint h = OpenThread(0x0002, false, te.th32ThreadID);                     // THREAD_SUSPEND_RESUME
                    if (h != 0) { SuspendThread(h); hs.Add(h); }
                }
        }
        finally { CloseHandle(snap); }
        return hs;
    }

    private static void ResumeAll(List<nint> hs)
    {
        foreach (nint h in hs) { try { ResumeThread(h); CloseHandle(h); } catch { /* ignore */ } }
    }

    private static byte First(byte[] a) => a.Length > 0 ? a[0] : (byte)0;

    public void Dispose() => Remove();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(nint hSnapshot, ref THREADENTRY32 lpte);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(nint hSnapshot, ref THREADENTRY32 lpte);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(nint hThread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint hThread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID;
        public int tpBasePri, tpDeltaPri;
        public uint dwFlags;
    }
}
