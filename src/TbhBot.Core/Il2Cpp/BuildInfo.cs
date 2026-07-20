using System.Security.Cryptography;

namespace TbhBot.Core.Il2Cpp;

/// <summary>
/// Identidade da build do jogo. Portado de <c>dll_hash()</c> no tbh_core.py:
/// md5 dos primeiros 2.000.000 bytes do GameAssembly.dll, truncado em 12 hex.
/// Serve de chave em <see cref="GameConstants.KnownBuilds"/> — se o hash nao bater,
/// o engine re-dumpa os offsets sozinho (foi o bug que pegou o amigo do usuario:
/// hash None -> offsets vazios).
/// </summary>
public static class BuildInfo
{
    /// <summary>md5 dos 1os 2_000_000 bytes do modulo, 12 hex minusculos. null em qualquer falha.</summary>
    public static string? DllHash(string modulePath)
    {
        try
        {
            using var fs = new FileStream(modulePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // le no maximo 2M bytes (arquivo pode ser menor -> hasheia o que houver, igual ao read(2_000_000) do Python)
            var buf = new byte[2_000_000];
            int total = 0;
            int n;
            while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0)
                total += n;
            var hash = MD5.HashData(buf.AsSpan(0, total));
            return Convert.ToHexStringLower(hash)[..12];
        }
        catch
        {
            return null;
        }
    }
}
