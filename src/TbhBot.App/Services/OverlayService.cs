using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using TbhBot.Core.Market;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace TbhBot.App.Services;

/// <summary>
/// OVERLAY de preço (porta do tbh_overlay.py, agora NATIVO em C# — sem processo Python separado). Passe o
/// mouse sobre um item; o jogo abre o tooltip; o overlay captura a região sob o cursor, faz OCR nativo do
/// Windows (Windows.Media.Ocr), acha a linha "X Grade", resolve o NOME entre as linhas acima, casa com o
/// <see cref="PriceIndex"/> e desenha o preço numa janela topmost CLICK-THROUGH ao lado do tooltip.
/// Só dispara com o mouse PARADO sobre a janela do jogo. Liga/desliga por <see cref="Toggle"/>.
/// </summary>
public sealed class OverlayService
{
    private static readonly Regex GradeRe = new(
        @"\b(Common|Normal|Uncommon|Rare|Legendary|Immortal|Arcana|Beyond|Celestial|Divine|Cosmic)\s+Grade\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const double Scale = 1.7;               // upscale p/ OCR (igual ao Python)
    private const int Radius = 460;                 // meia-caixa de captura ao redor do cursor

    private readonly PriceIndex _prices = new();
    private readonly OcrEngine? _ocr = OcrEngine.TryCreateFromUserProfileLanguages();
    private OverlayWindow? _win;
    private DispatcherTimer? _timer;
    private bool _busy;
    private int _n;
    private int _lx = -9999, _ly = -9999;           // última pos do cursor (detecta parado)
    private (int mx, int my, Badge badge, int frame)? _last;   // anti-piscar

    public bool Enabled => _win is not null;
    public event Action<string>? Log;

    /// <summary>Liga/desliga. Retorna o novo estado. Loga se o OCR do Windows não estiver disponível.</summary>
    public bool Toggle()
    {
        if (Enabled) { Stop(); return false; }
        if (_ocr is null) { Log?.Invoke("overlay: OCR do Windows indisponível (instale um idioma) — não liguei"); return false; }
        if (_prices.Count == 0) { Log?.Invoke("overlay: índice de preços vazio"); return false; }
        Start();
        return Enabled;
    }

    private void Start()
    {
        _win = new OverlayWindow();
        _win.Show();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Log?.Invoke("◉ Price overlay: ON (passe o mouse sobre um item)");
    }

    public void Stop()
    {
        _timer?.Stop(); _timer = null;
        _win?.Close(); _win = null;
        _last = null; _lx = _ly = -9999;
        Log?.Invoke("◉ Price overlay: OFF");
    }

    private async Task TickAsync()
    {
        if (_busy || _win is null) return;
        _busy = true;
        try
        {
            _n++;
            _win.EnsureTopmost();
            var badge = await ScanAsync();
            _win.Render(badge);
        }
        catch { /* um frame ruim nunca derruba o overlay */ }
        finally { _busy = false; }
    }

    // Captura sob o cursor (se parado + sobre o jogo), faz OCR e devolve o badge (ou null).
    private async Task<Badge?> ScanAsync()
    {
        if (!Native.GetCursorPos(out var pt)) return null;
        int mx = pt.X, my = pt.Y;

        var (gameHwnd, rect) = GameWindow();
        if (gameHwnd == IntPtr.Zero) { _last = null; return null; }

        // cursor precisa estar SOBRE a janela do jogo (mesma raiz)
        nint under = Native.GetAncestor(Native.WindowFromPoint(pt), 2);
        if (under != gameHwnd) { _last = null; return null; }

        bool moved = Math.Abs(mx - _lx) > 22 || Math.Abs(my - _ly) > 22;
        _lx = mx; _ly = my;
        if (moved) return null;                     // movendo -> nada (mantém _last p/ quando parar)

        int rx0 = Math.Max(rect.Left, mx - Radius), ry0 = Math.Max(rect.Top, my - Radius);
        int rx1 = Math.Min(rect.Right, mx + Radius), ry1 = Math.Min(rect.Bottom, my + Radius);
        int w = rx1 - rx0, h = ry1 - ry0;
        if (w < 80 || h < 80) return null;

        int bw = (int)(w * Scale), bh = (int)(h * Scale);
        byte[]? bgra = Native.CaptureBgra(rx0, ry0, w, h, bw, bh);
        if (bgra is null) return null;

        var buf = CryptographicBuffer.CreateFromByteArray(bgra);
        var sb = SoftwareBitmap.CreateCopyFromBuffer(buf, BitmapPixelFormat.Bgra8, bw, bh);
        OcrResult res = await _ocr!.RecognizeAsync(sb);

        var badge = ReadBadge(res, rx0, ry0, mx, my);
        if (badge is not null) { _last = (mx, my, badge, _n); return badge; }

        // tooltip ainda não apareceu / OCR falhou este frame: segura o último (anti-piscar)
        if (_last is { } lst && Math.Abs(mx - lst.mx) <= 18 && Math.Abs(my - lst.my) <= 18 && _n - lst.frame <= 6)
            return lst.badge;
        return null;
    }

    private Badge? ReadBadge(OcrResult res, int rx0, int ry0, int mx, int my)
    {
        double cmx = (mx - rx0) * Scale, cmy = (my - ry0) * Scale;

        // linha de grade mais próxima do cursor
        string? gword = null; (double x0, double y0, double x1, double y1) gb = default; double bestd = 1e18;
        var lineBoxes = new List<(OcrLine ln, double x0, double y0, double x1, double y1)>();
        foreach (var ln in res.Lines)
        {
            var box = LineBox(ln);
            lineBoxes.Add((ln, box.x0, box.y0, box.x1, box.y1));
            var m = GradeRe.Match(ln.Text);
            if (!m.Success) continue;
            double dc = Math.Pow((box.x0 + box.x1) / 2 - cmx, 2) + Math.Pow((box.y0 + box.y1) / 2 - cmy, 2);
            if (dc < bestd) { bestd = dc; gword = m.Groups[1].Value; gb = box; }
        }
        if (gword is null) return null;

        int bx = (int)(rx0 + gb.x1 / Scale + 14);
        int by = (int)(ry0 + (gb.y0 + gb.y1) / 2 / Scale);

        // "Untradable" no jogo = não vende na Steam
        if (res.Lines.Any(l => l.Text.ToLowerInvariant().Contains("untradable")))
            return new Badge(bx, by, "Untradable", "#3a2323", "#d08a8a");

        // NOME = melhor item conhecido entre as linhas ACIMA da linha de grade
        var above = lineBoxes
            .Where(b => b.y1 <= gb.y0 + 8 && (gb.y0 - b.y1) is > -6 and < 200)
            .OrderBy(b => gb.y0 - b.y1)
            .Select(b => b.ln.Text);
        var baseName = _prices.ResolveBase(above);
        if (baseName is null) return null;

        var pr = _prices.PriceOf(baseName, gword);
        if (pr is not { } p) return new Badge(bx, by, "—", "#242424", "#7a7a7a");

        string txt = (p.approx ? "~$ " : "$ ") + (p.price < 100 ? p.price.ToString("0.00") : p.price.ToString("0"));
        string face = p.price >= 1 ? "#146c2e" : "#3d5a1e";
        return new Badge(bx, by, txt, face, "#eaffea");
    }

    private static (double x0, double y0, double x1, double y1) LineBox(OcrLine ln)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
        foreach (var wd in ln.Words)
        {
            var r = wd.BoundingRect;
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
        }
        return (x0, y0, x1, y1);
    }

    // Acha a janela do jogo por título (igual ao game_win do Python).
    private static (IntPtr hwnd, Native.RECT rect) GameWindow()
    {
        IntPtr found = IntPtr.Zero; Native.RECT rect = default;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            int len = Native.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Contains("TaskBarHero") || title == "TBH")
            {
                if (Native.GetWindowRect(h, out var rr)) { found = h; rect = rr; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return (found, rect);
    }

    /// <summary>Um badge a desenhar: posição de tela (px), texto, cor de fundo e de tinta.</summary>
    public sealed record Badge(int Sx, int Sy, string Text, string Face, string Ink);
}
