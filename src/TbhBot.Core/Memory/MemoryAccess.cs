using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TbhBot.Core.Native;

namespace TbhBot.Core.Memory;

/// <summary>
/// Camada de leitura/escrita de memória.
///
/// O que resolve a lentidão do Python NÃO é a linguagem — é o PADRÃO de leitura. O engine antigo lê
/// ponteiro-a-ponteiro (milhares de <c>ReadProcessMemory</c>, uma syscall cada). O primitivo-chave aqui é
/// <see cref="ReadArray{T}"/>: UMA syscall lê o bloco inteiro e o parse é em memória (zero-copy via
/// <see cref="MemoryMarshal"/>). É esse batch que dá o ganho de 10–100x — em qualquer linguagem, mas aqui
/// sem o custo do interpretador e sem GIL travando as threads.
/// </summary>
public sealed class MemoryAccess(ProcessTarget target)
{
    private readonly ProcessTarget _target = target;

    // ---- primitivos crus ----

    public bool TryReadRaw(nint address, Span<byte> buffer) =>
        NativeMethods.ReadProcessMemory(_target.Handle, address,
            ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length, out var read)
        && read == (nuint)buffer.Length;

    public bool TryWriteRaw(nint address, Span<byte> buffer) =>
        NativeMethods.WriteProcessMemory(_target.Handle, address,
            ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length, out var w)
        && w == (nuint)buffer.Length;

    // ---- leitura tipada (uma syscall por valor; use para leituras avulsas) ----

    public T Read<T>(nint address) where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
        return TryReadRaw(address, buf) ? Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buf)) : default;
    }

    public ulong  ReadU64(nint a) => Read<ulong>(a);
    public uint   ReadU32(nint a) => Read<uint>(a);
    public int    ReadI32(nint a) => Read<int>(a);
    public double ReadF64(nint a) => Read<double>(a);
    public nint   ReadPtr(nint a) => (nint)Read<ulong>(a);

    public byte[] ReadBytes(nint address, int count)
    {
        var buf = new byte[count];
        return TryReadRaw(address, buf) ? buf : [];
    }

    // ---- BATCH: o primitivo que importa ----

    /// <summary>Lê <paramref name="count"/> elementos de <typeparamref name="T"/> em UMA syscall.
    /// Use para varrer arrays (inventário/baú, listas de save, tabelas). Retorna vazio se a leitura falhar.</summary>
    public T[] ReadArray<T>(nint address, int count) where T : unmanaged
    {
        if (count <= 0) return [];
        var arr = new T[count];
        var bytes = MemoryMarshal.AsBytes(arr.AsSpan());
        return TryReadRaw(address, bytes) ? arr : [];
    }

    // ---- escrita tipada ----

    public bool Write<T>(nint address, T value) where T : unmanaged
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buf), value);
        return TryWriteRaw(address, buf);
    }

    public bool WriteBytes(nint address, ReadOnlySpan<byte> data) => TryWriteRaw(address, data.ToArray());

    // ---- helpers de ponteiro / IL2CPP (o `vptr`, `u64(p+off)` chain e string do Python) ----

    /// <summary>Acesso ao processo alvo (handle/base/size) para scanners e alloc de code-cave.</summary>
    public ProcessTarget Target => _target;

    /// <summary>Faixa canônica de user-mode x64 — equivalente ao <c>vptr</c> do engine Python.</summary>
    public static bool IsValidPointer(nint p) => (ulong)p is >= 0x10000 and < 0x00007FFFFFFFFFFF;

    /// <summary>Segue uma cadeia de ponteiros: <c>p = [p + off]</c> para cada offset. 0 se algum elo for inválido.</summary>
    public nint Chase(nint p, params int[] offsets)
    {
        foreach (var off in offsets)
        {
            if (!IsValidPointer(p)) return 0;
            p = ReadPtr(p + off);
        }
        return IsValidPointer(p) ? p : 0;
    }

    /// <summary>String IL2CPP (System.String): length em +0x10 (int32), chars UTF-16 em +0x14.</summary>
    public string ReadIl2CppString(nint strPtr, int maxLen = 512)
    {
        if (!IsValidPointer(strPtr)) return "";
        int len = ReadI32(strPtr + 0x10);
        if (len <= 0 || len > maxLen) return "";
        var bytes = ReadBytes(strPtr + 0x14, len * 2);
        return bytes.Length == len * 2 ? System.Text.Encoding.Unicode.GetString(bytes) : "";
    }

    /// <summary>C-string curta (UTF-8/latin1, corta no NUL) — usado para nome de klass IL2CPP.</summary>
    public string ReadCString(nint addr, int max = 64)
    {
        var b = ReadBytes(addr, max);
        if (b.Length == 0) return "";
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        return System.Text.Encoding.Latin1.GetString(b, 0, n);
    }
}
