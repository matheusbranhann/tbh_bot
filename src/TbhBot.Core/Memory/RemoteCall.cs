using TbhBot.Core.Native;

namespace TbhBot.Core.Memory;

/// <summary>
/// Chamada de função do jogo numa THREAD REMOTA (porta de _remote_call/_export do tbh_core.py).
/// Use SÓ para getters puros SEM afinidade de thread (il2cpp_class_from_name, iuw, etc.) — métodos
/// Unity async/UI TÊM que ir pelo <see cref="Game.RealDispatcher"/> (main-thread), senão crasham.
/// Serializado: o scratch é compartilhado, chamadas concorrentes se corrompem.
/// </summary>
public static class RemoteCall
{
    private static readonly object _lock = new();
    private static nint _scratch;         // cave RWX reaproveitada (shellcode + RES)
    private static nint _scratchHandle;   // handle do processo DONO do _scratch — se mudar (relançou), re-aloca

    /// <summary>Resolve um export do GameAssembly.dll por nome (via export table do PE). 0 se ausente.</summary>
    public static long ResolveExport(MemoryAccess mem, string name)
    {
        try
        {
            nint b = mem.Target.ModuleBase;
            uint lfa = mem.ReadU32(b + 0x3C);
            nint pe = b + (nint)lfa;
            uint expRva = mem.ReadU32(pe + 0x88);          // DataDirectory[0] (export), PE32+
            if (expRva == 0) return 0;
            nint ed = b + (nint)expRva;
            uint nNames = mem.ReadU32(ed + 0x18);          // NumberOfNames
            uint afun = mem.ReadU32(ed + 0x1C);            // AddressOfFunctions
            uint anam = mem.ReadU32(ed + 0x20);            // AddressOfNames
            uint aord = mem.ReadU32(ed + 0x24);            // AddressOfNameOrdinals
            var tgt = System.Text.Encoding.ASCII.GetBytes(name);
            for (uint i = 0; i < nNames; i++)
            {
                uint nrva = mem.ReadU32(b + (nint)anam + (nint)(i * 4));
                var s = mem.ReadBytes(b + (nint)nrva, 64);
                int z = Array.IndexOf(s, (byte)0); if (z < 0) z = s.Length;
                if (z == tgt.Length && s.AsSpan(0, z).SequenceEqual(tgt))
                {
                    ushort ordi = mem.Read<ushort>(b + (nint)aord + (nint)(i * 2));
                    uint frva = mem.ReadU32(b + (nint)afun + (nint)(ordi * 4));
                    return (long)(b + (nint)frva);
                }
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>Chama func(args...) (até 4 args em rcx/rdx/r8/r9) numa thread remota. Retorna rax. 0 em falha.</summary>
    public static ulong Invoke(MemoryAccess mem, long func, params long[] args)
    {
        if (func == 0) return 0;
        lock (_lock)
        {
            nint h = mem.Target.Handle;
            // Se o processo mudou (jogo relançou pelo auto-restart), o _scratch antigo aponta pra memória do
            // processo MORTO — executar shellcode nele crasharia o novo jogo. Re-aloca quando o handle muda.
            if (_scratch == 0 || _scratchHandle != h)
            {
                _scratch = NativeMethods.VirtualAllocEx(h, 0, 0x1000,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_EXECUTE_READWRITE);
                _scratchHandle = _scratch != 0 ? h : 0;
            }
            if (_scratch == 0) return 0;
            nint sc = _scratch, RES = sc + 0x100;

            var a = new long[4];
            for (int i = 0; i < 4 && i < args.Length; i++) a[i] = args[i];

            var c = new List<byte> { 0x48, 0x83, 0xEC, 0x38 };                 // sub rsp,0x38 (shadow space + align)
            byte[][] regs = [[0x48, 0xB9], [0x48, 0xBA], [0x49, 0xB8], [0x49, 0xB9]]; // mov rcx/rdx/r8/r9
            for (int i = 0; i < 4; i++) { c.AddRange(regs[i]); c.AddRange(BitConverter.GetBytes((ulong)a[i])); }
            c.AddRange([0x48, 0xB8]); c.AddRange(BitConverter.GetBytes((ulong)func)); c.AddRange([0xFF, 0xD0]); // mov rax,func; call rax
            c.AddRange([0x48, 0xB9]); c.AddRange(BitConverter.GetBytes((ulong)RES));                            // mov rcx,RES
            c.AddRange([0x48, 0x89, 0x01, 0x48, 0x83, 0xC4, 0x38, 0x31, 0xC0, 0xC3]);                          // [rcx]=rax; add rsp,0x38; xor eax,eax; ret

            mem.WriteBytes(sc, c.ToArray());
            mem.WriteBytes(RES, new byte[8]);
            nint th = NativeMethods.CreateRemoteThread(h, 0, 0, sc, 0, 0, 0);
            if (th == 0) return 0;
            NativeMethods.WaitForSingleObject(th, 5000);
            NativeMethods.CloseHandle(th);
            return mem.ReadU64(RES);
        }
    }
}
