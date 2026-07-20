using TbhBot.Core.Native;

namespace TbhBot.Core.Memory;

/// <summary>
/// Aloca uma code-cave RWX de 0x1000 bytes perto de 'near' (dentro de +-0x7ff00000, alcance de um jmp rel32).
/// Porta de tbh_core.py: alloc_cave() (1105-1137).
/// </summary>
public static class CodeCave
{
    private const long Lim = 0x7ff00000;                 // alcance maximo do E9 rel32 (+- ~2GB)
    private const nuint Size = 0x1000;
    private const uint AllocType = NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE;   // 0x3000
    private const uint Prot = NativeMethods.PAGE_EXECUTE_READWRITE;                        // 0x40
    private const ulong Gran = 0x10000;                  // granularidade de alocacao do Windows

    public static nint Alloc(MemoryAccess mem, nint near)
    {
        nint h = mem.Target.Handle;
        long nearL = near;

        // 1) tentativas rapidas em offsets fixos perto do alvo
        foreach (long o in new long[]
                 { 0x7000000, 0x7200000, 0x7400000, 0x7800000, 0x8000000,
                   0x9000000, 0xA000000, -0x2000000, -0x4000000, -0x8000000 })
        {
            nint a = TryAt(h, nearL, nearL + o);
            if (a != 0) return a;
        }

        // 2) ASLR pode ocupar os offsets fixos -> escaneia regioes LIVRES via VirtualQueryEx
        ulong addr = (ulong)Math.Max(0x10000L, nearL - Lim);
        ulong hi = (ulong)(nearL + Lim);
        while (addr < hi)
        {
            if (NativeMethods.VirtualQueryEx(h, (nint)addr, out var mbi, (nuint)MbiSize) == 0)
                break;
            ulong regionBase = mbi.BaseAddress;
            ulong regionSize = mbi.RegionSize;
            if (regionSize == 0) break;
            if (mbi.State == NativeMethods.MEM_FREE && regionSize >= 0x2000)
            {
                ulong cand = (regionBase + Gran - 1) & ~(Gran - 1);   // alinha p/ cima na granularidade
                if (cand + 0x1000 <= regionBase + regionSize
                    && Math.Abs((long)cand - nearL) < Lim)
                {
                    nint a = NativeMethods.VirtualAllocEx(h, (nint)cand, Size, AllocType, Prot);
                    if (a != 0 && Math.Abs((long)a - nearL) < Lim) return a;
                    if (a != 0) NativeMethods.VirtualFreeEx(h, a, 0, NativeMethods.MEM_RELEASE);
                }
            }
            addr = regionBase + regionSize;
        }
        return 0;
    }

    // aloca no endereco pedido; mantem so se ficou dentro do alcance, senao libera
    private static nint TryAt(nint h, long near, long addr)
    {
        nint a = NativeMethods.VirtualAllocEx(h, (nint)addr, Size, AllocType, Prot);
        if (a != 0 && Math.Abs((long)a - near) < Lim) return a;
        if (a != 0) NativeMethods.VirtualFreeEx(h, a, 0, NativeMethods.MEM_RELEASE);
        return 0;
    }

    private static readonly int MbiSize =
        System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
}
