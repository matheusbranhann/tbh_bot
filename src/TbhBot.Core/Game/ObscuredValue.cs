using System;
using TbhBot.Core.Memory;

namespace TbhBot.Core.Game;

// Porta de _obs_int/_obs_set (tbh_core.py ~1954-1972).
// ObscuredInt do Anti-Cheat Toolkit ocupa 16 bytes: hash@0, hidden@4, key@8, fake@0xC.
// Valor real = ((hidden - key) & 0xFFFFFFFF) ^ key.
public static class ObscuredValue
{
    /// <summary>Decodifica: <c>((hidden - key) &amp; 0xFFFFFFFF) ^ key</c> reinterpretado como int32. (função pura, testável)</summary>
    public static int Decode(uint hidden, uint key) => unchecked((int)((hidden - key) ^ key));

    /// <summary>Codifica o hiddenValue: <c>((v ^ key) + key) &amp; 0xFFFFFFFF</c>. (função pura, testável)</summary>
    public static uint EncodeHidden(int value, uint key) => unchecked(((uint)value ^ key) + key);

    public static int? ReadInt(MemoryAccess mem, nint addr)
    {
        byte[] raw = mem.ReadBytes(addr, 16);
        if (raw.Length < 16) return null;
        return Decode(BitConverter.ToUInt32(raw, 4), BitConverter.ToUInt32(raw, 8));   // hidden@+4, key@+8
    }

    // Escreve `value` mexendo SO no hiddenValue@+4 com a key@+8 atual (escrita minima:
    // nao toca hash/key/fake -> menor chance de acordar o honeypot do detector).
    // encode: hidden = ((v ^ key) + key) & 0xFFFFFFFF. Retorna True se o decode confirma.
    public static bool WriteInt(MemoryAccess mem, nint addr, int value)
    {
        byte[] raw = mem.ReadBytes(addr, 16);
        if (raw.Length < 16) return false;
        uint newhid = EncodeHidden(value, BitConverter.ToUInt32(raw, 8));
        mem.WriteBytes(addr + 4, BitConverter.GetBytes(newhid));
        return ReadInt(mem, addr) == value;
    }
}
