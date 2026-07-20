using System.Windows;
using TbhBot.Core;
using TbhBot.Core.Automation;

namespace TbhBot.App.Services;

/// <summary>
/// Dono do <see cref="Engine"/> para a UI. Attacha/reataca ao jogo em background (equivalente ao watchdog
/// + reconnect do painel Python) e roda o único <see cref="AutomationLoop"/> que lê as flags Want* do engine.
/// Notifica a UI no thread da UI via <see cref="StateChanged"/>.
/// </summary>
public sealed class EngineService
{
    public Engine Engine { get; } = new();
    public bool IsAttached => Engine.IsAttached && Engine.Target.IsAlive();

    /// <summary>Disparado (no thread da UI) quando conecta/desconecta.</summary>
    public event Action? StateChanged;

    /// <summary>Log textual do engine/automação para a barra de status.</summary>
    public event Action<string>? Log;

    private CancellationTokenSource? _cts;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        Engine.Log += OnLog;

        var loop = new AutomationLoop(Engine);
        loop.Log += OnLog;

        var wd = new WatchdogService(this);
        wd.Log += OnLog;

        _ = ConnectLoopAsync(_cts.Token);
        _ = loop.RunAsync(_cts.Token);
        _ = wd.RunAsync(_cts.Token);        // Auto-restart: dono ÚNICO do relançamento/popup/reentrada
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Engine.Dispose();
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        bool last = false;
        while (!ct.IsCancellationRequested)
        {
            // Esta é a "TICK" do Python: re-attacha sozinho quando o jogo volta — INCLUSIVE durante o
            // restart (WdHold). O _wd_hold só impede o AutomationLoop de APLICAR no boot, não de attachar.
            bool now = IsAttached;
            if (!now)
            {
                try { now = Engine.Attach(); } catch { now = false; }
            }
            if (now != last)
            {
                last = now;
                Post(() => StateChanged?.Invoke());
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Abre o jogo via Steam (steam://run/&lt;appid&gt;). Usado pelo Auto-restart e pelo botão de launcher.</summary>
    public void LaunchGame()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"steam://run/{Engine.SteamAppId}") { UseShellExecute = true });
        }
        catch (Exception ex) { Post(() => Log?.Invoke($"launcher: falha ({ex.Message})")); }
    }

    private void OnLog(string msg) { LogToFile(msg); Post(() => Log?.Invoke(msg)); }

    /// <summary>Roteia um log externo (ex.: overlay) pra barra de status, no thread da UI.</summary>
    public void RaiseLog(string msg) { LogToFile(msg); Post(() => Log?.Invoke(msg)); }

    // Log em arquivo (%APPDATA%/tbh_bot/session.log) — histórico rolável do que aconteceu (watchdog etc.).
    private static readonly object _logLock = new();
    private static void LogToFile(string msg)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tbh_bot");
            System.IO.Directory.CreateDirectory(dir);
            lock (_logLock)
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "session.log"),
                    $"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // Sempre marshaliza para o thread da UI (as leituras rodam em background).
    private static void Post(Action a)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }
}
