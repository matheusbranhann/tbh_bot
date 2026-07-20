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
/// Persistência dos perfis em %APPDATA%/tbh_bot/profiles.json (local ESTÁVEL — o BaseDirectory de um exe
/// single-file é a pasta temporária de extração, que some entre execuções). {nome: Profile}.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tbh_bot");
    private static string FilePath => Path.Combine(Dir, "profiles.json");

    public Dictionary<string, Profile> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Profile>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* arquivo corrompido -> começa vazio */ }
        return new();
    }

    public void Save(Dictionary<string, Profile> all)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Opt));
        }
        catch { /* sem permissão de escrita: silencioso */ }
    }
}
