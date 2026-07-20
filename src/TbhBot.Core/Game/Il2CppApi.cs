using TbhBot.Core.Memory;
using TbhBot.Core.Native;

namespace TbhBot.Core.Game;

/// <summary>
/// API IL2CPP (exports de GameAssembly.dll) via <see cref="RemoteCall"/> — resolve o Il2CppClass* de um
/// tipo por NOME (estável entre builds). Porta de _stagebox_klass/_export do tbh_core.py. Getters puros
/// = seguro fora da main-thread. Klass cacheado.
/// </summary>
public sealed class Il2CppApi(MemoryAccess mem)
{
    private readonly MemoryAccess _mem = mem;
    private nint _scratch;                                   // buffer p/ as strings (ns/name) + size out
    private readonly Dictionary<string, long> _exports = new();
    private readonly Dictionary<string, long> _klass = new();

    private long Export(string name)
    {
        if (_exports.TryGetValue(name, out var v)) return v;
        v = RemoteCall.ResolveExport(_mem, name);
        _exports[name] = v;
        return v;
    }

    /// <summary>Il2CppClass* de <paramref name="ns"/>.<paramref name="name"/>. 0 se não achar.</summary>
    public long ClassFromName(string ns, string name)
    {
        string ck = ns + "." + name;
        if (_klass.TryGetValue(ck, out var kc)) return kc;

        long domGet = Export("il2cpp_domain_get"),
             asmsGet = Export("il2cpp_domain_get_assemblies"),
             imgGet = Export("il2cpp_assembly_get_image"),
             clsFrom = Export("il2cpp_class_from_name");
        if (domGet == 0 || asmsGet == 0 || imgGet == 0 || clsFrom == 0) return 0;

        if (_scratch == 0)
            _scratch = NativeMethods.VirtualAllocEx(_mem.Target.Handle, 0, 0x1000,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_EXECUTE_READWRITE);
        if (_scratch == 0) return 0;

        nint NS = _scratch + 0x40, NM = _scratch + 0x80, SZ = _scratch + 0xC0;
        _mem.WriteBytes(NS, System.Text.Encoding.ASCII.GetBytes(ns + "\0"));
        _mem.WriteBytes(NM, System.Text.Encoding.ASCII.GetBytes(name + "\0"));

        ulong dom = RemoteCall.Invoke(_mem, domGet);
        if (dom == 0) return 0;
        _mem.WriteBytes(SZ, new byte[8]);
        ulong asms = RemoteCall.Invoke(_mem, asmsGet, (long)dom, (long)SZ);
        uint n = _mem.ReadU32(SZ);
        for (uint i = 0; i < Math.Min(n, 300u); i++)
        {
            nint a = (nint)_mem.ReadU64((nint)asms + (nint)(i * 8));
            if (a == 0) continue;
            ulong img = RemoteCall.Invoke(_mem, imgGet, (long)a);
            ulong kl = RemoteCall.Invoke(_mem, clsFrom, (long)img, (long)NS, (long)NM);
            if (kl != 0) { _klass[ck] = (long)kl; return (long)kl; }
        }
        return 0;
    }
}
