using System.IO;
using System.Text.Json;

namespace TbhBot.App.Services;

/// <summary>Um perfil salvo do usuário: switches (proteção/automação), filtros do fuse e stats/campos forçados.</summary>
public sealed class Profile
{
    public Dictionary<string, bool> Switches { get; set; } = new();
    public int FuseGrade { get; set; } = 2;
    public List<int> FuseTypes { get; set; } = [0, 1, 2];
    public Dictionary<string, double> Stats { get; set; } = new();
    public Dictionary<string, int> Stage { get; set; } = new();
}

/// <summary>
/// Persistência dos perfis em <c>profiles.json</c> NA PASTA DO .EXE — assim o painel é portátil:
/// levou a pasta, levou os perfis; e dá pra ver/editar/versionar o arquivo sem caçar no %APPDATA%.
///
/// A pasta é resolvida por <see cref="Environment.ProcessPath"/> — o .exe de verdade, por contrato.
/// (Medido neste publish single-file: <c>AppContext.BaseDirectory</c> também dá a pasta do exe; a
/// ressalva de que ele apontaria pra pasta temporária de extração valia no .NET Core 3.x. ProcessPath
/// é usado mesmo assim por não depender desse detalhe de versão/flags do publish.)
///
/// Se a pasta do exe não for gravável (exe em Program Files, pendrive travado, etc.), cai pro
/// <c>%APPDATA%/tbh_bot</c> em vez de perder o perfil calado — <see cref="ResolvedPath"/> sempre diz
/// onde de fato salvou. Perfis que já existiam no %APPDATA% são MIGRADOS na primeira gravação.
/// </summary>
public sealed class ProfileStore
{
    private const string FileName = "profiles.json";
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    /// <summary>Pasta do executável — o destino preferido.</summary>
    public static string ExeDir
    {
        get
        {
            var p = Environment.ProcessPath;
            var d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
        }
    }

    /// <summary>Fallback histórico: onde os perfis moravam até a v4.2 (e onde ainda vão parar se o exe
    /// estiver numa pasta só-leitura).</summary>
    public static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tbh_bot");

    private static string ExeFile => Path.Combine(ExeDir, FileName);
    private static string LegacyFile => Path.Combine(LegacyDir, FileName);

    /// <summary>Caminho realmente usado na última operação — pro log/UI não deixar dúvida.</summary>
    public string ResolvedPath { get; private set; } = ExeFile;

    /// <summary>Avisos ("migrei do %APPDATA%", "pasta do exe é só-leitura") pra barra de status.</summary>
    public Action<string>? Log;

    public Dictionary<string, Profile> Load()
    {
        var mine = TryRead(ExeFile);
        if (mine is not null) { ResolvedPath = ExeFile; return mine; }

        // Nada ao lado do exe: aproveita o que existia no %APPDATA% e MIGRA na hora (no primeiro start),
        // pra não depender do usuário clicar em salvar pra mudança de lugar acontecer.
        var legacy = TryRead(LegacyFile);
        if (legacy is not null) { Save(legacy); return legacy; }

        ResolvedPath = ExeFile;
        return new();
    }

    private static Dictionary<string, Profile>? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var all = JsonSerializer.Deserialize<Dictionary<string, Profile>>(File.ReadAllText(path));
            return all is { Count: > 0 } ? all : null;
        }
        catch { return null; }      // arquivo corrompido -> tenta o próximo
    }

    public void Save(Dictionary<string, Profile> all)
    {
        if (TryWrite(ExeDir, all))
        {
            ResolvedPath = ExeFile;
            // Migração: o arquivo antigo vira .bak pra não confundir (e pra dar pra voltar atrás).
            try
            {
                if (File.Exists(LegacyFile))
                {
                    File.Move(LegacyFile, LegacyFile + ".bak", overwrite: true);
                    Log?.Invoke($"perfis migrados pro lado do exe ({ExeFile}); o antigo virou profiles.json.bak");
                }
            }
            catch { /* migração é bônus: se falhar, o que importa (gravar) já deu certo */ }
            return;
        }

        // Pasta do exe não é gravável: não perde o perfil — grava no %APPDATA% e AVISA.
        if (TryWrite(LegacyDir, all))
        {
            ResolvedPath = LegacyFile;
            Log?.Invoke($"pasta do exe não é gravável — perfis salvos em {LegacyFile}");
        }
        else Log?.Invoke("não consegui salvar os perfis (sem permissão de escrita em nenhum dos dois lugares)");
    }

    private static bool TryWrite(string dir, Dictionary<string, Profile> all)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(all, Opt));
            return true;
        }
        catch { return false; }
    }
}
