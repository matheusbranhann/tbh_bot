using System.Diagnostics;
using System.Windows;
using TbhBot.Core.Memory;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace TbhBot.App.Services;

/// <summary>
/// AUTO-RESTART — porte FIEL do _watchdog_loop (tbh_core.py:997). Quando o jogo FECHA por completo:
/// reabre na hora com START LIMPO (o AutomationLoop não aplica nada durante o boot, via WdHold), fecha o
/// botão Close do popup OFFLINE REWARDS (tolerante), lê o estágio, RELIGA tudo e REENTRA no estágio.
/// O watchdog NÃO attacha — quem re-attacha é o ConnectLoop (a "tick"); aqui só observamos svc.IsAttached.
/// </summary>
public sealed class WatchdogService(EngineService svc)
{
    private readonly OcrEngine? _ocr = OcrEngine.TryCreateFromUserProfileLanguages();
    public event Action<string>? Log;
    private void Emit(string m) => Log?.Invoke(m);

    private const int Startup = 120;   // ~4min esperando o jogo subir

    public async Task RunAsync(CancellationToken ct)
    {
        int lastStage = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var e = svc.Engine;
                if (!e.WantWatchdog) { e.WdHold = false; await Delay(1000, ct); continue; }

                // jogo vivo: opera normal + rastreia o estágio atual (fallback p/ o re-enter)
                if (svc.IsAttached)
                {
                    e.WdHold = false;
                    try { int cur = e.Save.StageProgress().Cur; if (cur > 0) lastStage = cur; } catch { }
                    await Delay(2000, ct); continue;
                }

                // ===================== JOGO FECHOU =====================
                e.WdHold = true;                                  // 1) start limpo (config segue no want; nada aplicado no boot)
                Emit("🛡 watchdog: jogo fechou — start limpo (nada aplicado no boot)");

                int gone = 0;                                     // 2) espera o exe SUMIR TOTALMENTE
                while (GameRunning())
                {
                    if (!e.WantWatchdog) { e.WdHold = false; break; }
                    await Delay(500, ct);
                    if (++gone > 240) break;
                }
                await Delay(1000, ct);
                if (!e.WantWatchdog) { e.WdHold = false; continue; }
                Emit("🛡 watchdog: exe sumiu — abrindo o jogo");
                svc.LaunchGame();                                 // 3) reabre

                bool up = false;                                  // 4) espera SUBIR — a TICK (ConnectLoop) re-attacha
                for (int i = 0; i < Startup; i++)
                {
                    if (!e.WantWatchdog) { e.WdHold = false; break; }
                    await Delay(2000, ct);
                    if (svc.IsAttached) { up = true; break; }
                }
                if (!up) { Emit("🛡 watchdog: jogo não subiu — tentando de novo"); e.WdHold = false; continue; }
                await Delay(3000, ct);                            // deixa carregar

                try { await CloseOfflinePopup(120, ct); }         // 5) fecha o popup — tolerante
                catch (Exception ex) { Emit($"🛡 watchdog: fechar popup deu erro ({ex.Message}) — prosseguindo"); }

                int reCur;                                        // 6) lê o estágio (save; fallback = último vivo)
                try { reCur = e.Save.StageProgress().Cur; if (reCur <= 0) reCur = lastStage; }
                catch { reCur = lastStage; }

                e.WdHold = false;                                 // 7) RELIGA tudo (o AutomationLoop re-aplica ACTk/God/stats)
                Emit("🛡 watchdog: religando todas as opções");
                await Delay(3000, ct);                            // deixa re-aplicar + instalar o dispatcher

                if (reCur > 0) ReEnter(reCur);                    // 8) REENTRA no estágio
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Emit($"🛡 watchdog: erro ({ex.Message})");
                try { svc.Engine.WdHold = false; } catch { }
                await Delay(3000, ct);
            }
        }
        try { svc.Engine.WdHold = false; } catch { }
    }

    private static bool GameRunning() => Process.GetProcessesByName(ProcessTarget.ProcessName).Length > 0;

    private void ReEnter(int cur)
    {
        try
        {
            var tbl = svc.Engine.StageNav.StageTable();
            bool isBoss = tbl.TryGetValue(cur, out var info) && info.Type == 1;
            bool ok = isBoss ? svc.Engine.StageNav.EnterBoss(cur) : svc.Engine.StageNav.GoToStage(cur);
            Emit($"🛡 watchdog: reentrou no estágio {cur} ({(isBoss ? "boss" : "normal")})={ok}");
        }
        catch (Exception ex) { Emit($"🛡 watchdog: reentrar no estágio falhou ({ex.Message})"); }
    }

    // ---------------- popup OFFLINE REWARDS (porte fiel de close_offline_popup) ----------------

    private async Task CloseOfflinePopup(int windowSec, CancellationToken ct)
    {
        if (_ocr is null) return;
        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalSeconds < windowSec)
        {
            if (!svc.IsAttached) return;
            var pos = await FindCloseButton();
            if (pos is Point p)
            {
                Emit($"🛡 watchdog: popup OFFLINE REWARDS detectado — fechando (clique {p.X:0},{p.Y:0})");
                Native.ClickReal((int)p.X, (int)p.Y);
                await Delay(1500, ct);
                if (await FindCloseButton() is null) { Emit("🛡 watchdog: popup fechado ✔"); return; }
                Native.ClickReal((int)p.X, (int)p.Y);   // 2ª tentativa
                await Delay(1500, ct);
                return;
            }
            await Delay(2000, ct);
        }
    }

    // (x,y) de tela do "Close" do OFFLINE REWARDS, ou null. Só devolve se CONFIRMAR o popup.
    private async Task<Point?> FindCloseButton()
    {
        const double sc = 2.5;
        var hwnd = Native.GameWindowByPid(svc.Engine.Target.ProcessId);
        if (hwnd == IntPtr.Zero) return null;
        byte[]? bgra = Native.CaptureWindowScaledBgra(hwnd, sc, out int bw, out int bh, out var origin);
        if (bgra is null) return null;

        var buf = CryptographicBuffer.CreateFromByteArray(bgra);
        var sb = SoftwareBitmap.CreateCopyFromBuffer(buf, BitmapPixelFormat.Bgra8, bw, bh);
        OcrResult res = await _ocr!.RecognizeAsync(sb);

        string all = string.Join(" | ", res.Lines.Select(l => l.Text.ToLowerInvariant()));
        if (!(all.Contains("offline") || all.Contains("last login") || all.Contains("reward"))) return null;

        foreach (var ln in res.Lines)
        {
            if (!ln.Text.ToLowerInvariant().Contains("close")) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
            foreach (var wd in ln.Words)
            {
                var r = wd.BoundingRect;
                x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
            }
            return new Point(origin.X + (x0 + x1) / 2 / sc, origin.Y + (y0 + y1) / 2 / sc);
        }
        return null;
    }

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
