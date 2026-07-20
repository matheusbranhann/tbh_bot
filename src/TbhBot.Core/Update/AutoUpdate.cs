using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace TbhBot.Core.Update;

/// <summary>
/// Auto-update via GitHub Releases — porta de check_update/download_update/launch_updater
/// do tbh_core.py (~linhas 16-92).
///
/// NOTA: <see cref="Repo"/> e <see cref="CurrentVersion"/> sao o TRILHO DE RELEASE do build C#.
/// Enquanto o .exe C# nao tem release proprio publicado, sao PLACEHOLDER: o repo continua sendo
/// o do bot Python (matheusbranhann/taskbarhero-bot) e a versao vem do assembly. Quando houver
/// pipeline de release do TbhBot C#, bumpar <see cref="CurrentVersion"/> a cada release e (se for
/// outro repo) trocar <see cref="Repo"/>.
/// </summary>
public sealed class AutoUpdate
{
    // Repo de releases (PLACEHOLDER: mesmo do Python ate o exe C# ter release proprio).
    public const string Repo = "matheusbranhann/taskbarhero-bot";

    // Versao atual deste build (PLACEHOLDER: trilho de release C#; bumpar a cada release).
    public const string CurrentVersion = "0.1.0";

    // User-Agent OBRIGATORIO pela API do GitHub (rejeita requests sem UA).
    private const string UserAgent = "tbh_bot-updater";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// 'v3.1'/'3.10' -> (3,1)/(3,10) pra comparar versao ordinalmente (funcao pura, testavel).
    /// Igual ao _ver_tuple do Python: descarta o 'v', pega so digitos de cada segmento.
    /// </summary>
    public static int[] VerTuple(string? s)
    {
        var trimmed = (s ?? "").TrimStart('v', 'V').Trim();
        if (trimmed.Length == 0) return [0];
        var parts = trimmed.Split('.');
        var outv = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var digits = new string([.. parts[i].Where(char.IsDigit)]);
            outv[i] = digits.Length > 0 ? int.Parse(digits) : 0;
        }
        return outv;
    }

    /// <summary>Compara duas tuplas de versao ordinalmente (como tuple do Python: elemento a elemento).</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] ta = VerTuple(a), tb = VerTuple(b);
        int n = Math.Max(ta.Length, tb.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < ta.Length ? ta[i] : 0;
            int y = i < tb.Length ? tb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Consulta releases/latest do GitHub. Retorna (Available, Tag, Url) com Available=true so se houver
    /// versao MAIOR que <paramref name="currentVersion"/> e um asset .zip. Silencioso em falha de rede.
    /// Porta de check_update().
    /// </summary>
    public async Task<(bool Available, string Tag, string Url)> CheckAsync(
        string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (CompareVersions(tag, currentVersion) <= 0)
                return (false, tag, "");

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    var dl = a.TryGetProperty("browser_download_url", out var b) ? (b.GetString() ?? "") : "";
                    if (dl.Length > 0)
                        return (true, tag, dl);
                }
            }
        }
        catch
        {
            // Silencioso: sem rede / rate-limit / json torto -> simplesmente sem update.
        }
        return (false, "", "");
    }

    /// <summary>
    /// Baixa o zip do release e extrai o exe do painel (TBH_Panel.exe / TbhBot*.exe) para
    /// "&lt;exe atual&gt;.new.exe" ao lado do executavel corrente. Retorna (NewExe, Exe, ExeDir).
    /// Porta de download_update().
    /// </summary>
    public async Task<(string NewExe, string Exe, string ExeDir)> DownloadAndStageAsync(
        string url, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string exe = CurrentExePath();
        string exeDir = Path.GetDirectoryName(exe) ?? Directory.GetCurrentDirectory();

        // Baixa o zip inteiro pra memoria (com progresso, se Content-Length disponivel).
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;

        using var buf = new MemoryStream();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var chunk = new byte[65536];
            long read = 0;
            int got;
            while ((got = await src.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
            {
                buf.Write(chunk, 0, got);
                read += got;
                if (progress is not null && total > 0)
                    progress.Report(Math.Min((double)read / total, 1.0));
            }
        }

        buf.Position = 0;
        using var zip = new ZipArchive(buf, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => IsPanelExe(e.Name))
            ?? throw new InvalidOperationException("zip do release nao contem o exe do painel (TBH_Panel.exe/TbhBot*.exe)");

        byte[] data;
        await using (var es = entry.Open())
        using (var ms = new MemoryStream())
        {
            await es.CopyToAsync(ms, ct).ConfigureAwait(false);
            data = ms.ToArray();
        }

        // Sanidade: o exe real e dezenas de MB; muito menor = download torto/asset errado.
        if (data.Length < 1_000_000)
            throw new InvalidOperationException($"exe baixado pequeno demais ({data.Length} bytes)");

        string newExe = exe + ".new.exe";
        await File.WriteAllBytesAsync(newExe, data, ct).ConfigureAwait(false);
        return (newExe, exe, exeDir);
    }

    private static bool IsPanelExe(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("TBH_Panel.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith("TbhBot", StringComparison.OrdinalIgnoreCase);
    }

    // Conteudo do .bat updater: espera o PID sair, troca o exe (retenta ate destravar) e reabre.
    // Porta de _UPDATER_BAT (CRLF, %~1=exe %~2=new %~3=pid).
    private const string UpdaterBat =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "set \"EXE=%~1\"\r\n" +
        "set \"NEW=%~2\"\r\n" +
        "set PID=%~3\r\n" +
        ":wait\r\n" +
        "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul\r\n" +
        "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
        ":movetry\r\n" +
        "move /y \"%NEW%\" \"%EXE%\" >nul 2>&1\r\n" +
        "if errorlevel 1 ( timeout /t 1 /nobreak >nul & goto movetry )\r\n" +
        "start \"\" \"%EXE%\"\r\n" +
        "del \"%~f0\"\r\n";

    /// <summary>
    /// Escreve o .bat updater e o lanca DESACOPLADO. Ele espera ESTE processo (PID) sair, troca o exe
    /// (&lt;exe&gt;.new.exe -> &lt;exe&gt;) e reabre o painel. O chamador deve encerrar logo apos, senao o
    /// 'move' fica retentando ate o processo fechar. Porta de launch_updater().
    /// </summary>
    public void LaunchUpdater(string newExe, string exe, string exeDir)
    {
        string bat = Path.Combine(exeDir, "_tbh_update.bat");
        File.WriteAllText(bat, UpdaterBat, System.Text.Encoding.ASCII);

        int pid = Environment.ProcessId;
        var psi = new ProcessStartInfo
        {
            FileName = "cmd",
            WorkingDirectory = exeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(bat);
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add(newExe);
        psi.ArgumentList.Add(pid.ToString());
        Process.Start(psi);
    }

    // Caminho do executavel corrente (equivalente a sys.executable no exe congelado).
    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p)) return p;
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "TbhBot.exe");
    }
}
